using Ryujinx.Common;
using Ryujinx.Common.Logging;
using SharpMetal.Metal;
using System;
using System.Linq;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Per-present flash detector. Blits a 3x3 grid of pixels from the present source
    /// into a small shared buffer each frame, then classifies a frame as a flash when
    /// its mean brightness spikes well above BOTH neighbouring frames - which is what
    /// a single-frame flash is, and what a bright-but-steady scene is not.
    ///
    /// The earlier version sampled two pixels and flagged the frame whenever they were
    /// equal and non-zero. That fired on any two pixels that happened to match: an
    /// overexposed sky lit it continuously (flashes were logged on consecutive frames,
    /// which a single-frame flash cannot be), and opaque black scored as a flash too
    /// because only exact zero was excluded. It never compared frames to each other,
    /// so it could not tell a spike from a steady value at all.
    ///
    /// Enable: RYUJINX_PRESENT_PROBE=1
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class PresentProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_PRESENT_PROBE") == "1";

        private const int BytesPerPixel = 4; // RGBA8/BGRA8 - channel order is irrelevant to luma here
        private const int GridSide = 5;
        private const int PixelsPerFrame = GridSide * GridSide;
        private const int SlotBytes = PixelsPerFrame * BytesPerPixel;

        // Enough slots that every frame the readback touches is at least one frame old,
        // so the blit that filled it has long since completed.
        private const int Slots = 4;

        // A white frame is one where most samples are saturated - not one where they are
        // all equal. The HUD is composited into the present source and survives the
        // fault, so a uniformity test is decided by whether a sample lands on the
        // minimap; that made the same scene report anywhere from 0% to 42%. Thresholds
        // from 300 captured frames: flat frames have >= 8 of 25 saturated, ordinary
        // frames at most 4.
        // Counting that directly, rather than as a spike above both neighbours, is what
        // makes runs visible: a run of white frames has white neighbours, so a spike
        // test scores every frame inside it as normal and only ever sees the isolated
        // ones. Nine points spread over the frame agreeing this closely is not a scene -
        // even a blown-out sky keeps a gradient across that distance.
        private const float UniformSpread = 2f;
        private const float WhiteLuma = 240f;

        // Kept as a secondary signal: an isolated frame far brighter than both
        // neighbours, which is what a single-frame flash looks like.
        private const float FlashDelta = 40f;

        // Detailed lines only for the first runs; after that the periodic line carries it.
        private const int DetailedRuns = 30;

        // The flash only lasts a couple of minutes before the in-game sun moves off the
        // angle that triggers it. Reporting every 600 frames left six data points in
        // that window - far too few to split into A/B arms - so this reports every 60.
        private const int ReportInterval = 60;

        private static MTLBuffer _buf;
        private static int _frame;

        // Stats
        private static int _flashCount;   // isolated spikes
        private static int _whiteCount;   // frames that are uniformly white
        private static int _runCount;     // consecutive stretches of them
        private static int _currentRun;
        private static int _maxRun;
        private static int _frameCount;

        // Paired arms: the skip under test is applied on even present indices only, so
        // both arms see the same seconds of gameplay. The trigger view lasts about two
        // minutes and the rate swings 18-44% on its own, so unpaired windows cannot
        // resolve a single draw's contribution.
        internal static int Frame => _frame;

        // The most recent source the detector classified as a normal frame. The flash
        // guard needs this rather than "the previous surface": white frames come in runs
        // averaging 1.6, so the immediately preceding surface is often white as well.
        private static Texture _lastGoodSource;
        private static readonly Texture[] _slotSourceTex = new Texture[Slots];

        internal static Texture LastGoodSource => _lastGoodSource;

        // Stain sweep bookkeeping: which draw index this frame was stained at, and
        // whether the frame came back green (nothing wrote the source after that draw).
        private static int _stainIndex = -1;
        private static readonly int[] _slotStainIndex = new int[Slots];
        private static readonly System.Collections.Generic.Dictionary<int, (int green, int total)> _sweep = new();

        public static void NoteStainIndex(int index)
        {
            _stainIndex = index;
        }

        private static void TallySweep(int slot, bool green)
        {
            int k = _slotStainIndex[slot];

            if (k < 0)
            {
                return;
            }

            _sweep.TryGetValue(k, out (int green, int total) v);
            _sweep[k] = (v.green + (green ? 1 : 0), v.total + 1);
        }

        public static string DescribeSweep()
        {
            if (_sweep.Count == 0)
            {
                return "none";
            }

            System.Text.StringBuilder sb = new();

            foreach (int k in _sweep.Keys.OrderBy(x => x))
            {
                (int green, int total) v = _sweep[k];
                sb.Append($" {k}:{v.green}/{v.total}");
            }

            return sb.ToString();
        }

        private static int _whiteEven, _totalEven, _whiteOdd, _totalOdd;
        private static float _lastFlashLuma;
        private static float _lastFlashSpread;

        // Draw/dispatch counts, kept per slot so a flagged frame reports its own workload
        private static readonly ulong[] _slotDraws = new ulong[Slots];
        private static readonly ulong[] _slotDispatches = new ulong[Slots];
        private static readonly int[] _slotSmUploads = new int[Slots];
        private static readonly int[] _slotSmFlushActions = new int[Slots];

        // Which resource each frame was presented from. Only a couple of surfaces
        // rotate, so the sequence around a flash separates "the wrong buffer was
        // presented" from "the buffer that was presented had turned white".
        private static readonly IntPtr[] _slotSrc = new IntPtr[Slots];
        private static readonly int[] _slotTexId = new int[Slots];

        private static ulong _drawCountAtPresent;
        private static ulong _dispatchCountAtPresent;

        // Rolling averages over non-flash frames, for comparison in the flash log
        private static ulong _goodDraws;
        private static ulong _goodDispatches;

        // ── Pass ring ──────────────────────────────────────────────────────
        private const int RingSize = 16; // must be power of 2
        private static readonly PassEntry[] _ring = new PassEntry[RingSize];
        private static int _ringHead;

        private struct PassEntry
        {
            public int Frame;
            public PassEndReason Reason;
            public ulong Draws;
        }

        // ── Public API ─────────────────────────────────────────────────────

        public static void Init(MTLDevice device)
        {
            if (!Enabled)
                return;

            _buf = device.NewBuffer((ulong)(SlotBytes * Slots), MTLResourceOptions.ResourceStorageModeShared);

            Logger.Warning?.PrintMsg(LogClass.Gpu,
                $"PresentProbe: armed (buf=0x{_buf.NativePtr:X}, {PixelsPerFrame}px/frame, spike>{FlashDelta})");
        }

        /// <summary>
        /// Record each pass end into the ring. Called from Pipeline.EndCurrentPass.
        /// </summary>
        public static void RecordPass(int frame, PassEndReason reason, ulong drawsInPass)
        {
            ref PassEntry e = ref _ring[_ringHead & (RingSize - 1)];
            e.Frame = frame;
            e.Reason = reason;
            e.Draws = drawsInPass;
            _ringHead++;
        }

        /// <summary>
        /// Called from Pipeline.Present, BEFORE the present blit.
        /// Classifies the frame two presents ago using its neighbours, then samples
        /// this frame's grid.
        /// </summary>
        public static unsafe void OnPresent(CommandBufferScoped cbs, Texture src, ulong currentDrawCount, ulong currentDispatchCount)
        {
            if (_buf.NativePtr == IntPtr.Zero)
                return;

            MTLTexture srcTex = src.GetHandle();
            if (srcTex.NativePtr == IntPtr.Zero)
                return;

            int frame = _frame++;
            int srcW = src.Width;
            int srcH = src.Height;

            var (smUploads, smProtected, smClean, smFlushAct) = SyncMemDiag.SnapshotAndReset();

            ulong drawsThisFrame = currentDrawCount - _drawCountAtPresent;
            _drawCountAtPresent = currentDrawCount;

            ulong dispatchesThisFrame = currentDispatchCount - _dispatchCountAtPresent;
            _dispatchCountAtPresent = currentDispatchCount;

            int slot = frame % Slots;
            _slotDraws[slot] = drawsThisFrame;
            _slotDispatches[slot] = dispatchesThisFrame;
            _slotSmUploads[slot] = smUploads;
            _slotSmFlushActions[slot] = smFlushAct;
            _slotSourceTex[slot] = src;
            _slotStainIndex[slot] = _stainIndex;
            _slotSrc[slot] = srcTex.NativePtr;
            _slotTexId[slot] = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(src);

            // Everything this frame encoded is done; park it so the classification two
            // presents from now can report what ran. The present blit itself is encoded
            // after this point, so the drawable-sized target lands in the next frame's
            // record - it is told apart by its size and BGRA format.
            HdrPassProbe.NotePresentSource(src, slot);
            HdrPassProbe.SampleInputs(cbs, slot);
            HdrPassProbe.NoteIdentity("present", src);

            // The final state of the watched stage, which is what the stage after it reads.
            // Sampling only before each pass leaves the last pass's output unmeasured, and
            // that is the one that matters.
            HdrPassProbe.SampleAtPresent(cbs, slot);

            HdrPassProbe.Commit(slot);

            // ── 1. Classify the frame two presents back ─────────────────────
            // Its own blit and both neighbours' blits are complete by now.
            if (frame >= 3)
            {
                int mid = frame - 2;

                float lumaPrev = ReadLuma((mid - 1) % Slots, out _, out _);
                float lumaMid = ReadLuma(mid % Slots, out float midMin, out float midMax);
                float lumaNext = ReadLuma((mid + 1) % Slots, out _, out _);

                float neighbour = MathF.Max(lumaPrev, lumaNext);
                float spike = lumaMid - neighbour;
                float spread = midMax - midMin;
                bool uniformWhite = ReadSaturatedCount(mid % Slots) >= 6;
                bool uniformGreen = ReadIsGreen(mid % Slots);

                TallySweep(mid % Slots, uniformGreen);

                _frameCount++;

                int midSlot = mid % Slots;

                if ((mid & 1) == 0)
                {
                    _totalEven++;
                }
                else
                {
                    _totalOdd++;
                }

                if (uniformWhite)
                {
                    if ((mid & 1) == 0)
                    {
                        _whiteEven++;
                    }
                    else
                    {
                        _whiteOdd++;
                    }

                    _whiteCount++;
                    _currentRun++;
                    _maxRun = Math.Max(_maxRun, _currentRun);

                    if (spike > FlashDelta)
                    {
                        _flashCount++;
                    }

                    // Only the first frame of a run is interesting: it is the transition
                    // that has to be explained, and logging every frame of a run both
                    // floods the log and perturbs the timing being measured.
                    if (_currentRun == 1)
                    {
                        _runCount++;
                        _lastFlashLuma = lumaMid;
                        _lastFlashSpread = spread;

                        if (_runCount <= DetailedRuns)
                        {
                            Logger.Warning?.PrintMsg(LogClass.Gpu,
                                $"presentprobe WHITE-RUN #{_runCount} starts f={mid} t={Environment.TickCount64} " +
                                $"luma={lumaPrev:F0}->{lumaMid:F0}<-{lumaNext:F0} spread={spread:F0} " +
                                $"D={_slotDraws[midSlot]}(good~{_goodDraws}) " +
                                $"C={_slotDispatches[midSlot]}(good~{_goodDispatches}) " +
                                $"SM:u={_slotSmUploads[midSlot]},f={_slotSmFlushActions[midSlot]} " +
                                $"srcseq=0x{_slotSrc[(mid - 1) % Slots]:X}->0x{_slotSrc[midSlot]:X}<-0x{_slotSrc[(mid + 1) % Slots]:X} " +
                                $"texid={_slotTexId[(mid - 1) % Slots]}->{_slotTexId[midSlot]}<-{_slotTexId[(mid + 1) % Slots]} " +
                                $"{srcW}x{srcH}\n" +
                                $"    WHITE targets: {HdrPassProbe.Describe(midSlot)}\n" +
                                $"     prev targets: {HdrPassProbe.Describe((mid - 1) % Slots)}\n" +
                                $"    WHITE passContent: {HdrPassProbe.DescribePassContent(midSlot)}\n" +
                                $"     prev passContent: {HdrPassProbe.DescribePassContent((mid - 1) % Slots)}\n" +
                                $"    WHITE inputs: {HdrPassProbe.DescribeInputs(midSlot)}\n" +
                                $"     prev inputs: {HdrPassProbe.DescribeInputs((mid - 1) % Slots)}\n" +
                                $"    WHITE nonRenderWrites: {HdrPassProbe.DescribeWrites(midSlot)}\n" +
                                $"    WHITE coverage:{CoverageProbe.Describe(midSlot)}\n" +
                                $"    WHITE flipOps: {HdrPassProbe.DescribeFlipOps(midSlot)}\n" +
                                $"     prev flipOps: {HdrPassProbe.DescribeFlipOps((mid - 1) % Slots)}\n" +
                                $"     prev coverage:{CoverageProbe.Describe((mid - 1) % Slots)}\n" +
                                $"    WHITE computeImage: {HdrPassProbe.DescribeComputeImage(midSlot)}\n" +
                                $"     prev computeImage: {HdrPassProbe.DescribeComputeImage((mid - 1) % Slots)}\n" +
                                $"    WHITE order: {HdrPassProbe.DescribeOrdinals(midSlot)}\n" +
                                $"     prev order: {HdrPassProbe.DescribeOrdinals((mid - 1) % Slots)}\n" +
                                $"    WHITE sceneCopies:{HdrPassProbe.DescribeSceneCopies(midSlot)}\n" +
                                $"     prev sceneCopies:{HdrPassProbe.DescribeSceneCopies((mid - 1) % Slots)}\n" +
                                $"     prev presentSrc: {HdrPassProbe.DescribePresentWritten((mid - 1) % Slots)}");
                        }
                    }
                }
                else
                {
                    if (_currentRun > 0 && _runCount <= DetailedRuns)
                    {
                        Logger.Warning?.PrintMsg(LogClass.Gpu,
                            $"presentprobe WHITE-RUN #{_runCount} ends after {_currentRun} frame(s)");
                    }

                    HdrPassProbe.CheckAttribution(midSlot, false);

                    if (_slotSourceTex[midSlot] != null)
                    {
                        _lastGoodSource = _slotSourceTex[midSlot];
                    }

                    _currentRun = 0;
                    _goodDraws = _slotDraws[midSlot];
                    _goodDispatches = _slotDispatches[midSlot];

                    if (_frameCount % ReportInterval == 0)
                    {
                        float whiteRate = _whiteCount * 100f / _frameCount;
                        float meanRun = _runCount > 0 ? (float)_whiteCount / _runCount : 0f;

                        Logger.Warning?.PrintMsg(LogClass.Gpu,
                            $"presentprobe f={frame} white={_whiteCount}/{_frameCount} " +
                            $"skipArm={_whiteEven}/{_totalEven} keepArm={_whiteOdd}/{_totalOdd} " +
                            $"rate={whiteRate:F2}% runs={_runCount} meanRun={meanRun:F1} maxRun={_maxRun} " +
                            $"isolatedSpikes={_flashCount} " +
                            $"lastWhiteLuma={_lastFlashLuma:F0} lastWhiteSpread={_lastFlashSpread:F0} " +
                            $"luma={lumaMid:F0} spread={spread:F0} " +
                            $"D={drawsThisFrame} C={dispatchesThisFrame} " +
                            $"SM:u={smUploads},p={smProtected},f={smFlushAct} " +
                            $"src=0x{srcTex.NativePtr:X}:{srcW}x{srcH} opSeq={OpRing.Seq}\n" +
                            $"     sweep: {DescribeSweep()}\n" +
                            $"     good coverage:{CoverageProbe.Describe(midSlot)}");
                    }
                }
            }

            // ── 2. Sample this frame's grid ─────────────────────────────────
            MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();
            ulong slotOffset = (ulong)(slot * SlotBytes);

            for (int gy = 0; gy < GridSide; gy++)
            {
                for (int gx = 0; gx < GridSide; gx++)
                {
                    int index = gy * GridSide + gx;

                    // Quarter, half and three-quarter points: spread across the frame so
                    // sky, ground and UI are all represented and no pair dominates.
                    ulong x = (ulong)(srcW * (gx + 1) / (GridSide + 1));
                    ulong y = (ulong)(srcH * (gy + 1) / (GridSide + 1));

                    blit.CopyFromTexture(
                        srcTex,
                        0, 0,
                        new MTLOrigin { x = x, y = y, z = 0 },
                        new MTLSize { width = 1, height = 1, depth = 1 },
                        _buf,
                        slotOffset + (ulong)(index * BytesPerPixel),
                        (ulong)BytesPerPixel,
                        (ulong)BytesPerPixel);
                }
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// Mean luma of a slot's grid, plus the darkest and brightest sample in it.
        /// A uniform frame - what a full-screen flash looks like - has a small spread.
        /// </summary>
        /// <summary>
        /// All nine samples green: the stain survived to Present, so nothing wrote the
        /// source after the draw this frame was stained at.
        /// </summary>
        private static unsafe int ReadSaturatedCount(int slot)
        {
            byte* ptr = (byte*)_buf.Contents + slot * SlotBytes;
            int saturated = 0;

            for (int i = 0; i < PixelsPerFrame; i++)
            {
                byte* px = ptr + i * BytesPerPixel;

                if ((px[0] + px[1] + px[1] + px[2]) * 0.25f >= 235f)
                {
                    saturated++;
                }
            }

            return saturated;
        }

        private static unsafe bool ReadIsGreen(int slot)
        {
            byte* ptr = (byte*)_buf.Contents + slot * SlotBytes;

            for (int i = 0; i < PixelsPerFrame; i++)
            {
                byte* px = ptr + i * BytesPerPixel;

                if (px[1] < 170 || px[0] > 110 || px[2] > 110)
                {
                    return false;
                }
            }

            return true;
        }

        private static unsafe float ReadLuma(int slot, out float min, out float max)
        {
            byte* ptr = (byte*)_buf.Contents + slot * SlotBytes;

            float total = 0f;
            min = 255f;
            max = 0f;

            for (int i = 0; i < PixelsPerFrame; i++)
            {
                byte* px = ptr + i * BytesPerPixel;

                // Channel order does not matter for a brightness estimate; the alpha
                // byte is skipped so an opaque frame is not scored as bright.
                float luma = (px[0] + px[1] + px[1] + px[2]) * 0.25f;

                total += luma;
                min = MathF.Min(min, luma);
                max = MathF.Max(max, luma);
            }

            return total / PixelsPerFrame;
        }
    }
}
