using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.IO;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Per frame classification of the render targets that feed the presented
    /// image, without perturbing the frame. The sunlight flicker replaces the whole
    /// scene with a single near white value while the HUD keeps drawing correctly;
    /// an external window monitor sees that but cannot say which host resource lost
    /// its content, and a log line per frame is known to hide the race.
    ///
    /// Every render pass only records its attachments in memory - no Metal calls,
    /// no pass splits. All the sampling happens in one blit encoder at present
    /// time, is read back several frames later when that command buffer is long
    /// finished, and is reported as a single aggregate line per interval. Two
    /// distant patches are taken per target so that "uniform" means genuinely flat
    /// and not just a patch of flat sky.
    ///
    /// The aggregate answers the question: walking the chain from scene colour to
    /// present source, which is the first stage that is uniform exactly on the
    /// frames that come out white?
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class FrameProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_FRAME_PROBE") == "1";

        private const int Slots = 16;
        private const int MaxTargets = 96;
        private const int Patches = 2;
        private const int Side = 16;
        private const int Texels = Side * Side;
        private const int BytesPerPixel = 4;
        private const int PatchBytes = Texels * BytesPerPixel;
        private const int EntryBytes = PatchBytes * Patches;

        // Entry 0 of every slot is the present source; 1..MaxTargets are the render
        // targets this frame passed through.
        private const int EntriesPerSlot = MaxTargets + 1;
        private const int SlotBytes = EntryBytes * EntriesPerSlot;

        // The command buffer that recorded slot N is certainly complete after this
        // many further presents - each present flushes and the pool holds 16 command
        // buffers. Nothing is ever waited on.
        private const int ReadbackDelay = 6;

        // Anything smaller than this is a shadow map, a histogram or a UI atlas, not
        // a stage of the main scene chain.
        private const int MinTargetWidth = 256;
        private const int MinTargetHeight = 144;

        private static readonly int _reportInterval =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_FRAME_PROBE_INTERVAL"), out int interval) && interval > 0
                ? interval
                : 300;

        private struct Sample
        {
            public IntPtr Source;
            public int Width;
            public int Height;
            public MTLPixelFormat Format;
            public IntPtr Root;
            public int Side;
            public bool Valid;
        }

        private struct Stats
        {
            public int Total;
            public int Uniform;
            public int UniformWhenFrameFlat;
            public int UniformWhenFrameNormal;
            public int Width;
            public int Height;
            public MTLPixelFormat Format;
            public uint LastUniformValue;
            public int[] OnFlat;
        }

        private static MTLBuffer _buffer;
        private static IntPtr _contents;
        private static bool _initialized;

        private static readonly int[] _slotFrame = new int[Slots];
        private static readonly Sample[] _slotSamples = new Sample[Slots * EntriesPerSlot];

        // Targets seen so far in the frame being encoded, in first-use order.
        private static readonly Texture[] _frameTargets = new Texture[MaxTargets];
        private static readonly int[] _frameTargetPasses = new int[MaxTargets];
        private static readonly int[] _frameTargetDraws = new int[MaxTargets];
        private static readonly IntPtr[] _frameTargetRoots = new IntPtr[MaxTargets];
        private static int _frameTargetCount;
        private static long _truncated;

        // 1600x896 is the gameplay scene resolution and never appears on the title
        // screen, so it is a reliable "gameplay has started" marker.
        private static bool _inGame;

        // Ground truth: 16x16 patches have repeatedly misled this investigation, so keep
        // a small ring of whole-frame copies and write the offending one to disk once a
        // flat frame is classified. Safer than a GPU capture, which froze before.
        // MUST exceed ReadbackDelay. The slot is frame % FullSlots, and classification
        // runs ReadbackDelay frames late, so with 2 slots (and an even delay) the frame
        // being classified always shares a slot with a newer frame that already
        // overwrote it - _fullFrame[slot] never matched and the dump could never fire.
        private const int FullSlots = 8;
        private const int FullWidth = 1920;
        private const int FullHeight = 1080;
        private const int FullBytes = FullWidth * FullHeight * 4;

        private static MTLDevice _fullDevice;
        private static MTLBuffer _fullBuffer;
        private static IntPtr _fullContents;
        private static readonly int[] _fullFrame = new int[FullSlots];
        // No brightness test: a night-time fault would produce a dark constant, and any
        // "is it white" filter would drop exactly that. Dump the first few flat frames
        // with distinct values instead, so one legitimate fade cannot consume the shot.
        // The interleaved A/B harness (MirrorAb) was removed along with its hooks in
        // other files; keep the column so old and new reports stay comparable.
        private const int AbLevel = 0;

        private const int MaxFullDumps = 4;
        private static int _fullDumpCount;
        private static readonly HashSet<uint> _fullDumpedValues = new();

        private static readonly bool _fullDumpEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_FULLDUMP") == "1";

        // Copying 8.3 MB every frame starves the very frame being measured. Flat frames
        // recur at ~15%, so arm on the first one and record only the short window until
        // the next - a handful of frames instead of thousands.
        private static int _fullArmedUntil = -1;

        // Sample the full resolution target immediately before each draw into it. The
        // chain scan only sees which targets ended up uniform; this says which draw made
        // them so, and hands over that draw's program label.
        private const int PreDrawSlots = 40;
        private static readonly Dictionary<string, (int Flat, int Ok)> _preDrawStats = new();
        private static readonly string[] _slotPreDraw = new string[Slots];
        private static readonly int[] _slotPreDrawCount = new int[Slots];

        // Per frame history of who wrote each target, so a buffer that was fine when it
        // was drawn and flat when it was shown can be traced to the frame that ruined it.
        private struct TargetFrameInfo
        {
            public IntPtr Handle;
            public IntPtr Root;
            public int Passes;
            public int Draws;
            public int Writes;
        }

        private static readonly TargetFrameInfo[] _slotTargetInfo = new TargetFrameInfo[Slots * MaxTargets];
        private static readonly int[] _slotTargetCount = new int[Slots];
        private static readonly Dictionary<string, (int Flat, int Ok)> _originStats = new();
        private static readonly Dictionary<string, (int Flat, int Ok)> _chooserStats = new();

        // The one draw that builds the presented buffer has the same pass/draw structure
        // on good and bad frames, so the difference must be its inputs. Record every
        // bound texture for that draw and split the signature by outcome.
        private static readonly Dictionary<string, (int Flat, int Ok)> _finalDrawInputs = new();
        private static readonly string[] _slotFinalDrawInputs = new string[Slots];

        // The composite draw's small inputs - an exposure or adaptation texture is
        // typically 16x16 or smaller - were below the tracked size floor and had never
        // been sampled. Force them in: a saturating tone curve maps every large input to
        // the same asymptote, so a blown exposure looks exactly like a fixed constant.
        private static readonly Texture[] _forcedInputs = new Texture[16];
        private static int _forcedInputCount;

        // For a tiny LUT, uniformity says nothing - it is always uniform. What matters is
        // its value, split by whether that frame came out flat.
        private static readonly Dictionary<string, (int Flat, int Ok)> _forcedInputValues = new();

        // Last unchecked input of the composite draw: its constant buffers. Hash their
        // contents at encode time (CPU visible, no GPU work) and split the hash by
        // outcome - a hash that only ever appears on flat frames names the culprit.
        private static readonly Dictionary<ulong, (int Flat, int Ok, string Sample)> _cbufHashes = new();
        private static readonly ulong[] _slotCbufHash = new ulong[Slots];
        private static readonly string[] _slotCbufSample = new string[Slots];

        // Hashing the whole buffer is useless here: it contains a per-frame counter, so
        // every frame hashes differently. Accumulate per-field means instead - a counter
        // averages out, a field that is wrong only on flat frames does not.
        private const int CbufFields = 128;
        private static readonly float[] _slotCbufFields = new float[Slots * CbufFields];
        private static readonly double[] _cbufFlatSum = new double[CbufFields];
        private static readonly double[] _cbufOkSum = new double[CbufFields];
        private static int _cbufFlatN;
        private static int _cbufOkN;

        // Means dilute a rare bit-pattern anomaly, which is why the denormal fields only
        // surfaced in one report out of three. Count per field how often a frame's value
        // is denormal or non-finite, split by outcome - that survives averaging.
        private static readonly int[] _cbufOddFlat = new int[CbufFields];
        private static readonly int[] _cbufOddOk = new int[CbufFields];
        private static readonly string[] _slotChooser = new string[Slots];
        private static readonly string[] _slotWriter = new string[Slots];
        private static readonly int[] _slotLevel = new int[Slots];
        private static readonly Dictionary<string, (int Flat, int Ok)> _lateStats = new();
        private static readonly int[] _slotLateEntry = new int[Slots];

        // Is the white a fixed literal, or a pixel sampled from the scene? A constant
        // value across many frames and camera angles means a literal source; a value
        // that tracks the scene means the full screen draw is sampling one texel.
        private static readonly Dictionary<uint, int> _flatValueHistogram = new();
        private static readonly long[] _levelFlat = new long[16];
        private static readonly long[] _levelTotal = new long[16];
        private static readonly Dictionary<string, (int Flat, int Ok)> _writerStats = new();

        // Hypothesis free: if the presented patch is byte identical to some target's
        // patch, the two are the same memory whatever the pointers say, and that
        // target's writer is the presented texture's writer.
        private static readonly Dictionary<string, (int Flat, int Ok)> _aliasStats = new();
        private static readonly Texture[] _presentSources = new Texture[4];
        private static int _presentSourceCount;

        private static readonly Dictionary<IntPtr, Stats> _stats = new();

        // Present goes through the nvnflinger buffer queue, so the framebuffer being
        // shown is not necessarily the one the command stream has just finished. The
        // history lets every target's uniformity be correlated against the flat frame
        // at offsets -2..+2 instead of assuming they line up.
        private const int History = 64;
        private const int MaxTracked = 64;
        private const int Offsets = 5;

        private static readonly IntPtr[] _trackedIds = new IntPtr[MaxTracked];
        private static int _trackedCount;

        private static readonly int[] _histFrame = new int[History];
        private static readonly bool[] _histFlat = new bool[History];
        private static readonly ulong[] _histMask = new ulong[History];
        private static int _flatCentres;
        private static readonly HashSet<MTLPixelFormat> _skippedFormats = new();

        private static int _frame;
        private static int _classified;
        private static int _frameFlatCount;

        // The stage that goes flat is the pass writing the full resolution float
        // target. Its inputs are read straight out of the CPU visible constant buffer
        // at encode time - no GPU work, no readback, no pass split - so a bad frame can
        // be compared against a good one field by field.
        private const int MaxDraws = 4;
        private const int CBufs = 18;
        private const int Vec4s = 4;
        private const int Floats = CBufs * Vec4s * 4;
        private const int MaxDrawTextures = 8;

        private struct DrawInfo
        {
            public string Program;
            public int Width;
            public int Height;
            public float[] Cbuf;
            public int[] CbufOffset;
            public int[] CbufSize;
            public int[] TexIds;
            public int[] TexWidth;
            public int[] TexHeight;
            public IntPtr[] TexHandle;
            public Texture Src0;
            public Format Src0Format;
            public double ScissorX;
            public double ScissorY;
            public double ScissorW;
            public double ScissorH;
            public int TexCount;
            public bool Valid;
        }

        private static readonly DrawInfo[] _slotDraws = new DrawInfo[Slots * MaxDraws];
        private static readonly TextureBase[] _trackedTextures = new TextureBase[128];
        private static int _trackedTextureCount;
        private static int _frameDrawCount;
        private static bool _reportedBad;
        private static bool _reportedGood;

        // Which source texture the full resolution copy sampled, split by whether that
        // frame came out flat. One anecdote is not evidence; the split is.
        private static readonly Dictionary<int, (int Bad, int Good, IntPtr Handle, int Width, int Height)> _drawTexStats = new();
        private static readonly Dictionary<string, (int Bad, int Good)> _drawShapeStats = new();

        // Which shaders wrote the final full resolution framebuffer, and how often each
        // was present on a frame that came out flat versus a healthy one. A stage that
        // is missing - or an extra one that is not - shows up as a lopsided pair.
        private static readonly Dictionary<string, (int Bad, int Good)> _finalProgramStats = new();

        // A finished framebuffer that turns into a constant between the frame that drew
        // it and the frame that shows it must have been overwritten by something that is
        // not a draw. These count the other ways a texture can be written.
        private static readonly Dictionary<IntPtr, int> _writesThisFrame = new();
        private static readonly int[] _slotPresentWrites = new int[Slots];
        private static readonly string[] _slotPresentWriteKind = new string[Slots];
        private static readonly Dictionary<string, (int Bad, int Good)> _writeKindStats = new();
        private static readonly List<string>[] _slotFinalPrograms = new List<string>[Slots];
        private static readonly int[] _slotFinalDraws = new int[Slots];
        private static int _frameFinalDraws;
        private static int _badFrames;
        private static int _goodFrames;
        private static long _badDrawSum;
        private static long _goodDrawSum;

        public static void Initialize(MTLDevice device)
        {
            if (_initialized)
            {
                return;
            }

            _buffer = device.NewBuffer(Slots * SlotBytes, MTLResourceOptions.ResourceStorageModeShared);
            _contents = _buffer.Contents;

            _fullDevice = device;

            for (int i = 0; i < FullSlots; i++)
            {
                _fullFrame[i] = -1;
            }

            for (int i = 0; i < Slots; i++)
            {
                _slotFrame[i] = -1;
            }

            _initialized = true;
        }

        /// <summary>
        /// Records the attachments of a render pass that is about to be encoded. No
        /// Metal object is touched: the pass owns them and reaching into their
        /// handles here would perturb the very lifetime race under study.
        /// </summary>
        /// <summary>
        /// Records a non-draw write to a texture: guest memory uploads and texture to
        /// texture copies. Called from the Metal texture write paths.
        /// </summary>
        public static void NoteWrite(Texture texture, string kind)
        {
            if (!Enabled || texture == null)
            {
                return;
            }

            _totalWrites++;
            _writeKindsSeen.Add(kind);

            AddWrite(texture.GetIdentityHandle().NativePtr);

            IntPtr root = RootHandle(texture);

            if (root != texture.GetIdentityHandle().NativePtr)
            {
                AddWrite(root);
            }

            if (_writeKinds.Length < 512)
            {
                _writeKinds.Append(kind).Append(' ');
            }
        }

        private static readonly StringBuilder _writeKinds = new();

        private static void AddWrite(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            _writesThisFrame.TryGetValue(handle, out int count);
            _writesThisFrame[handle] = count + 1;
        }
        private static long _totalWrites;
        private static long _overlaySeen;
        private static long _overlaySkipped;

        public static void NoteOverlaySeen(bool skipped)
        {
            _overlaySeen++;

            if (skipped)
            {
                _overlaySkipped++;
            }
        }
        private static readonly HashSet<string> _writeKindsSeen = new();
        private static readonly Dictionary<IntPtr, string> _nativeWriters = new();

        /// <summary>
        /// Lowest level record of a texture write: the destination native pointer of a
        /// blit. Everything above this can be bypassed by a view or a call path I did
        /// not enumerate, which is exactly how "nobody writes the presented texture"
        /// survived several rounds.
        /// </summary>
        public static void NoteNativeWrite(IntPtr destination, string kind)
        {
            if (!Enabled || destination == IntPtr.Zero)
            {
                return;
            }

            _totalWrites++;
            _writeKindsSeen.Add(kind);
            _nativeWriters[destination] = kind;
            AddWrite(destination);

            IntPtr root = Texture.ResolveViewRoot(destination);

            if (root != destination)
            {
                _nativeWriters[root] = kind;
                AddWrite(root);
            }
        }

        public static void NotePass(Texture[] colors, Texture depthStencil)
        {
            for (int i = 0; i < colors.Length; i++)
            {
                Note(colors[i]);
            }

            Note(depthStencil);
        }

        private static void Note(Texture texture)
        {
            if (texture == null)
            {
                return;
            }

            if (_frameTargetCount == MaxTargets)
            {
                _truncated++;

                return;
            }

            if (texture.Width < MinTargetWidth || texture.Height < MinTargetHeight)
            {
                return;
            }

            IntPtr root = RootHandle(texture);

            for (int i = 0; i < _frameTargetCount; i++)
            {
                if (_frameTargetRoots[i] == root)
                {
                    _frameTargetPasses[i]++;

                    return;
                }
            }

            _frameTargetPasses[_frameTargetCount] = 1;
            _frameTargetDraws[_frameTargetCount] = 0;
            _inGame |= texture.Width == 1600 && texture.Height == 896;

            _frameTargetRoots[_frameTargetCount] = root;
            _frameTargets[_frameTargetCount++] = texture;
        }

        /// <summary>
        /// Records the inputs of a draw into the full resolution float target. Only
        /// CPU visible memory and managed references are read, so this costs nothing on
        /// the GPU and cannot move the race.
        /// </summary>
        public static unsafe void NoteDraw(EncoderState state)
        {
            Texture rt = state.RenderTargets[0];

            if (rt == null)
            {
                return;
            }

            IntPtr drawRoot = RootHandle(rt);

            for (int i = 0; i < _frameTargetCount; i++)
            {
                if (_frameTargetRoots[i] == drawRoot)
                {
                    _frameTargetDraws[i]++;

                    break;
                }
            }

            for (int i = 0; i < _presentSourceCount; i++)
            {
                if (RootHandle(_presentSources[i]) != drawRoot)
                {
                    continue;
                }

                StringBuilder inputs = new();
                inputs.Append($"p={state.RenderProgram?.DebugLabel} rt={rt.Width}x{rt.Height} in=");

                for (int t = 0; t < state.TextureRefs.Length; t++)
                {
                    if (state.TextureRefs[t].Storage is not Texture bound)
                    {
                        continue;
                    }

                    inputs.Append($"{bound.Width}x{bound.Height}/{bound.Info.Format},");
                }

                // The composite program is the last unexamined thing in the chain; dump its
                // MSL once so the generated code can be read directly.
                if (_inGame)
            {
                state.RenderProgram?.DumpSources(FrameCapture.ShaderDumpDir);
            }



                _slotFinalDrawInputs[_frame % Slots] = inputs.ToString();

                unsafe
                {
                    ulong hash = 1469598103934665603UL;
                    StringBuilder sample = new();

                    for (int b = 0; b < state.UniformBufferRefs.Length; b++)
                    {
                        ref BufferRef bufferRef = ref state.UniformBufferRefs[b];

                        if (bufferRef.Buffer == null)
                        {
                            continue;
                        }

                        MTLBuffer mtl = bufferRef.Buffer.GetUnsafe().Value;

                        if (mtl.NativePtr == IntPtr.Zero || mtl.Contents == IntPtr.Zero)
                        {
                            continue;
                        }

                        int off = bufferRef.Range?.Offset ?? 0;
                        int len = Math.Min(bufferRef.Range?.Size ?? 256, 4096);
                        byte* data = (byte*)mtl.Contents + off;

                        for (int c = 0; c < len; c++)
                        {
                            hash = (hash ^ data[c]) * 1099511628211UL;
                        }

                        if (sample.Length < 120 && len >= 16)
                        {
                            float* f = (float*)data;
                            sample.Append($"b{b}({f[0]:G4},{f[1]:G4},{f[2]:G4},{f[3]:G4})");
                        }
                    }

                    _slotCbufHash[_frame % Slots] = hash;
                    _slotCbufSample[_frame % Slots] = sample.ToString();

                    int field = 0;
                    int fieldBase = (_frame % Slots) * CbufFields;

                    for (int b = 0; b < state.UniformBufferRefs.Length && field < CbufFields; b++)
                    {
                        ref BufferRef fieldRef = ref state.UniformBufferRefs[b];

                        if (fieldRef.Buffer == null)
                        {
                            continue;
                        }

                        MTLBuffer fieldBuffer = fieldRef.Buffer.GetUnsafe().Value;

                        if (fieldBuffer.NativePtr == IntPtr.Zero || fieldBuffer.Contents == IntPtr.Zero)
                        {
                            continue;
                        }

                        int fieldOff = fieldRef.Range?.Offset ?? 0;
                        int floats = Math.Min((fieldRef.Range?.Size ?? 256) / sizeof(float), 16);
                        float* src = (float*)((byte*)fieldBuffer.Contents + fieldOff);

                        for (int c = 0; c < floats && field < CbufFields; c++)
                        {
                            _slotCbufFields[fieldBase + field++] = src[c];
                        }
                    }

                    while (field < CbufFields)
                    {
                        _slotCbufFields[fieldBase + field++] = 0f;
                    }
                }

                for (int t = 0; t < state.TextureRefs.Length && _forcedInputCount < _forcedInputs.Length; t++)
                {
                    if (state.TextureRefs[t].Storage is not Texture bound || bound.Width > 64 || bound.Height > 64)
                    {
                        continue;
                    }

                    bool seen = false;

                    for (int f = 0; f < _forcedInputCount; f++)
                    {
                        seen |= ReferenceEquals(_forcedInputs[f], bound);
                    }

                    if (!seen)
                    {
                        _forcedInputs[_forcedInputCount++] = bound;
                    }
                }

                break;
            }

            if (rt.Width < 1900)
            {
                return;
            }

            state.RenderProgram?.DumpSources(FrameCapture.ShaderDumpDir);

            if (rt.Info.Format is Format.R8G8B8A8Unorm or Format.B8G8R8A8Unorm or Format.R8G8B8A8Srgb or Format.B8G8R8A8Srgb)
            {
                int slotIndex = _frame % Slots;

                _slotFinalPrograms[slotIndex] ??= new List<string>();

                string label = state.RenderProgram?.DebugLabel ?? "none";

                if (!_slotFinalPrograms[slotIndex].Contains(label))
                {
                    _slotFinalPrograms[slotIndex].Add(label);
                }

                _frameFinalDraws++;

                return;
            }

            if (_frameDrawCount == MaxDraws)
            {
                return;
            }

            ref DrawInfo draw = ref _slotDraws[(_frame % Slots) * MaxDraws + _frameDrawCount];
            _frameDrawCount++;

            draw.Cbuf ??= new float[Floats];
            draw.CbufOffset ??= new int[CBufs];
            draw.CbufSize ??= new int[CBufs];
            draw.TexIds ??= new int[MaxDrawTextures];
            draw.TexWidth ??= new int[MaxDrawTextures];
            draw.TexHeight ??= new int[MaxDrawTextures];
            draw.TexHandle ??= new IntPtr[MaxDrawTextures];
            draw.Program = state.RenderProgram?.DebugLabel;
            draw.Width = rt.Width;
            draw.Height = rt.Height;
            draw.Valid = true;

            Array.Clear(draw.Cbuf);
            Array.Clear(draw.CbufOffset);
            Array.Clear(draw.CbufSize);

            for (int i = 0; i < CBufs && i < state.UniformBufferRefs.Length; i++)
            {
                ref BufferRef bufferRef = ref state.UniformBufferRefs[i];

                if (bufferRef.Buffer == null)
                {
                    continue;
                }

                draw.CbufSize[i] = -1;

                MTLBuffer buffer = bufferRef.Buffer.GetUnsafe().Value;

                if (buffer.NativePtr == IntPtr.Zero || buffer.Contents == IntPtr.Zero)
                {
                    continue;
                }

                int offset = bufferRef.Range?.Offset ?? 0;
                int size = bufferRef.Range?.Size ?? Vec4s * 16;

                draw.CbufOffset[i] = offset;
                draw.CbufSize[i] = size;

                int count = Math.Min(Vec4s * 4, size / sizeof(float));
                float* data = (float*)((byte*)buffer.Contents + offset);

                for (int f = 0; f < count; f++)
                {
                    draw.Cbuf[i * Vec4s * 4 + f] = data[f];
                }
            }

            draw.TexCount = 0;
            draw.Src0 = null;
            draw.ScissorX = state.Scissors[0].x;
            draw.ScissorY = state.Scissors[0].y;
            draw.ScissorW = state.Scissors[0].width;
            draw.ScissorH = state.Scissors[0].height;

            for (int i = 0; i < state.TextureRefs.Length && draw.TexCount < MaxDrawTextures; i++)
            {
                TextureBase storage = state.TextureRefs[i].Storage;

                if (storage == null)
                {
                    continue;
                }

                draw.TexWidth[draw.TexCount] = storage.Width;
                draw.TexHeight[draw.TexCount] = storage.Height;
                // GetIdentityHandle() with no command buffer only reads the cached
                // pointer, so it stays a plain field read like the rest of this probe.
                draw.TexHandle[draw.TexCount] = storage.GetIdentityHandle().NativePtr;
                if (draw.Src0 == null && storage is Texture src)
                {
                    draw.Src0 = src;
                    draw.Src0Format = src.Info.Format;
                }

                draw.TexIds[draw.TexCount++] = TrackedTextureId(storage);
            }
        }

        /// <summary>
        /// Blits a patch of a full resolution target just before a draw lands on it, and
        /// records whether it was already uniform at that point together with the label
        /// of the draw about to run.
        /// </summary>
        private static unsafe void PreDrawSample(Texture rt, string label)
        {
            int slot = _frame % Slots;

            if (!_initialized || _slotPreDrawCount[slot] >= PreDrawSlots || _pipelineRef == null)
            {
                return;
            }

            int index = _slotPreDrawCount[slot]++;
            int entry = EntriesPerSlot - 2 - index;

            if (entry <= 0)
            {
                return;
            }

            _pipelineRef.EndCurrentPassForProbe();
            SampleTarget(_pipelineRef.CurrentCbs, slot, entry, rt);

            _slotPreDraw[slot] = (_slotPreDraw[slot] ?? string.Empty) + $"{index}:{label}:e{entry} ";
        }

        private static Texture _postDrawTarget;

        public static void ArmPostDraw(Texture rt)
        {
            _postDrawTarget = rt;
        }

        public static void PostDrawSample()
        {
            if (_postDrawTarget == null)
            {
                return;
            }

            Texture rt = _postDrawTarget;
            _postDrawTarget = null;

            PreDrawSample(rt, "AFTER");
        }

        private static Pipeline _pipelineRef;

        public static void SetPipeline(Pipeline pipeline)
        {
            _pipelineRef = pipeline;
        }

        private static int TrackedTextureId(TextureBase texture)
        {
            for (int i = 0; i < _trackedTextureCount; i++)
            {
                if (ReferenceEquals(_trackedTextures[i], texture))
                {
                    return i;
                }
            }

            if (_trackedTextureCount == _trackedTextures.Length)
            {
                return -1;
            }

            _trackedTextures[_trackedTextureCount] = texture;

            return _trackedTextureCount++;
        }

        /// <summary>
        /// Samples the present source and every render target seen this frame. Must
        /// be called with no render pass open, after the present blit is encoded and
        /// before the command buffer is committed.
        /// </summary>
        public static void Capture(CommandBufferScoped cbs, Texture presentSource)
        {
            int frame = _frame++;

            Classify(frame - ReadbackDelay);

            if (frame % _reportInterval == _reportInterval - 1)
            {
                Report();
            }

            int slot = frame % Slots;
            _slotFrame[slot] = frame;
            _slotLevel[slot] = AbLevel;

            for (int i = 0; i < EntriesPerSlot; i++)
            {
                _slotSamples[slot * EntriesPerSlot + i].Valid = false;
            }

            for (int i = 0; i < MaxDraws; i++)
            {
                ref DrawInfo draw = ref _slotDraws[slot * MaxDraws + i];

                if (draw.Valid)
                {
                    Note(draw.Src0);
                }
            }

            _writesThisFrame.TryGetValue(presentSource.GetIdentityHandle().NativePtr, out int presentWrites);
            _slotPresentWrites[slot] = presentWrites;
            _slotPresentWriteKind[slot] = presentWrites == 0 ? "none" : _writeKinds.ToString();

            bool known = false;

            for (int i = 0; i < _presentSourceCount; i++)
            {
                known |= ReferenceEquals(_presentSources[i], presentSource);
            }

            if (!known && _presentSourceCount < _presentSources.Length)
            {
                _presentSources[_presentSourceCount++] = presentSource;
            }

            Note(presentSource);

            {
                // A presented view's writes land on the resource behind it, so the view's
                // own pointer alone reports NEVER for a texture that is written every frame.
                IntPtr ptr = presentSource.GetIdentityHandle().NativePtr;
                IntPtr root = RootHandle(presentSource);

                string writer = _nativeWriters.TryGetValue(ptr, out string kind)
                    ? $"self:{kind}"
                    : _nativeWriters.TryGetValue(root, out string rootKind)
                        ? $"root:{rootKind}"
                        : root != ptr ? "NEVER(view)" : "NEVER(plain)";

                _slotWriter[slot] = writer;
            }

            _slotChooser[slot] =
                presentSource == null
                    ? "present=<null>"
                    : $"present={presentSource.Width}x{presentSource.Height}:{presentSource.Info.Format} " +
                      $"root=0x{RootHandle(presentSource):X}";

            CaptureFull(cbs, frame, presentSource);

            SampleTarget(cbs, slot, 0, presentSource);

            _slotTargetCount[slot] = _frameTargetCount;

            for (int i = 0; i < _frameTargetCount; i++)
            {
                ref TargetFrameInfo info = ref _slotTargetInfo[slot * MaxTargets + i];
                info.Handle = _frameTargets[i].GetIdentityHandle().NativePtr;
                info.Root = RootHandle(_frameTargets[i]);

                _writesThisFrame.TryGetValue(info.Handle, out int writes);

                if (info.Root != info.Handle)
                {
                    _writesThisFrame.TryGetValue(info.Root, out int rootWrites);
                    writes += rootWrites;
                }
                info.Passes = _frameTargetPasses[i];
                info.Draws = _frameTargetDraws[i];
                info.Writes = writes;

                SampleTarget(cbs, slot, i + 1, _frameTargets[i]);
                _frameTargets[i] = null;
            }

            _frameTargetCount = 0;
            _frameDrawCount = 0;
            _writesThisFrame.Clear();
            _writeKinds.Clear();

            // Reserve the swap chain's slots for the frame about to be encoded, before
            // the render passes can fill the array and push them out.
            for (int i = 0; i < _presentSourceCount; i++)
            {
                _frameTargetPasses[_frameTargetCount] = 0;
                _frameTargetDraws[_frameTargetCount] = 0;
                _frameTargetRoots[_frameTargetCount] = RootHandle(_presentSources[i]);
                _frameTargets[_frameTargetCount++] = _presentSources[i];
            }

            for (int i = 0; i < _forcedInputCount; i++)
            {
                _frameTargetPasses[_frameTargetCount] = 0;
                _frameTargetDraws[_frameTargetCount] = 0;
                _frameTargetRoots[_frameTargetCount] = RootHandle(_forcedInputs[i]);
                _frameTargets[_frameTargetCount++] = _forcedInputs[i];
            }
            _slotFinalDraws[slot] = _frameFinalDraws;
            _frameFinalDraws = 0;
        }

        /// <summary>
        /// Samples the present source a second time, after the present blit is encoded.
        /// A disagreement with the first sample means the resource is still being changed
        /// after the point the probe has been observing it.
        /// </summary>
        public static void CaptureLate(CommandBufferScoped cbs, Texture presentSource)
        {
            int frame = _frame - 1;

            if (frame < 0)
            {
                return;
            }

            int slot = frame % Slots;
            int entry = EntriesPerSlot - 1;

            _slotLateEntry[slot] = entry;
            SampleTarget(cbs, slot, entry, presentSource);
        }

        private static void CaptureFull(CommandBufferScoped cbs, int frame, Texture texture)
        {
            // Arming has to expire: the frame that armed it is not in the ring yet, so
            // if no flat frame follows soon the recording would run forever at 8.3 MB a
            // frame - which measured 40x slower than not recording at all.
            if (!_fullDumpEnabled || _fullDumpCount >= MaxFullDumps || frame > _fullArmedUntil ||
                texture.Width != FullWidth || texture.Height != FullHeight)
            {
                return;
            }

            if (_fullContents == IntPtr.Zero)
            {
                _fullBuffer = _fullDevice.NewBuffer(FullSlots * (ulong)FullBytes, MTLResourceOptions.ResourceStorageModeShared);
                _fullContents = _fullBuffer.Contents;
            }

            MTLTexture image = texture.GetIdentityHandle(cbs);

            if (image.NativePtr == IntPtr.Zero || !IsFourBytePlainFormat(image.PixelFormat))
            {
                return;
            }

            int slot = frame % FullSlots;

            cbs.Encoders.EnsureBlitEncoder().CopyFromTexture(
                image,
                0,
                0,
                new MTLOrigin { x = 0, y = 0, z = 0 },
                new MTLSize { width = FullWidth, height = FullHeight, depth = 1 },
                _fullBuffer,
                (ulong)(slot * FullBytes),
                FullWidth * 4,
                (ulong)FullBytes);

            _fullFrame[slot] = frame;
        }

        private static unsafe void DumpFull(int frame, uint flatValue)
        {
            int slot = frame % FullSlots;

            if (_fullDumpCount >= MaxFullDumps || _fullFrame[slot] != frame ||
                !_fullDumpedValues.Add(flatValue))
            {
                return;
            }

            _fullDumpCount++;

            try
            {
                byte[] data = new byte[FullBytes];
                System.Runtime.InteropServices.Marshal.Copy(_fullContents + slot * FullBytes, data, 0, FullBytes);

                string path = $"/tmp/ryujinx-flatframe-{frame}-{flatValue:X8}.raw";
                File.WriteAllBytes(path, data);

                Logger.Warning?.PrintMsg(LogClass.Gpu, $"flat frame dumped: {path} ({FullWidth}x{FullHeight} BGRA8) value=0x{flatValue:X8} n={_fullDumpCount}/{MaxFullDumps}");
            }
            catch (Exception exception)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"flat frame dump failed: {exception.Message}");
            }
        }

        private static void SampleTarget(CommandBufferScoped cbs, int slot, int entry, Texture texture)
        {
            if (texture == null)
            {
                return;
            }

            int side = Math.Min(Side, Math.Min(texture.Width, texture.Height));

            MTLTexture image = texture.GetIdentityHandle(cbs);

            if (image.NativePtr == IntPtr.Zero)
            {
                return;
            }

            MTLPixelFormat format = image.PixelFormat;

            // Depth/stencil and compressed formats need a blit option this binding
            // does not expose; a mismatched byte count would silently copy garbage.
            if (!IsFourBytePlainFormat(format))
            {
                _skippedFormats.Add(format);

                return;
            }

            MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();

            ulong baseOffset = (ulong)(slot * SlotBytes + entry * EntryBytes);

            for (int patch = 0; patch < Patches; patch++)
            {
                // One patch a quarter in, one three quarters in: a stage is only
                // called uniform when two distant windows agree.
                int numerator = patch == 0 ? 1 : 3;

                ulong x = (ulong)Math.Clamp(texture.Width * numerator / 4 - side / 2, 0, texture.Width - side);
                ulong y = (ulong)Math.Clamp(texture.Height * numerator / 4 - side / 2, 0, texture.Height - side);

                blit.CopyFromTexture(
                    image,
                    0,
                    0,
                    new MTLOrigin { x = x, y = y, z = 0 },
                    new MTLSize { width = (ulong)side, height = (ulong)side, depth = 1 },
                    _buffer,
                    baseOffset + (ulong)(patch * PatchBytes),
                    (ulong)(side * BytesPerPixel),
                    (ulong)(side * side * BytesPerPixel));
            }

            ref Sample sample = ref _slotSamples[slot * EntriesPerSlot + entry];

            sample.Source = image.NativePtr;
            sample.Root = RootHandle(texture);
            sample.Width = texture.Width;
            sample.Height = texture.Height;
            sample.Format = format;
            sample.Side = side;
            sample.Valid = true;
        }

        private static bool IsFourBytePlainFormat(MTLPixelFormat format)
        {
            return format switch
            {
                MTLPixelFormat.RGBA8Unorm or
                MTLPixelFormat.RGBA8UnormsRGB or
                MTLPixelFormat.BGRA8Unorm or
                MTLPixelFormat.BGRA8UnormsRGB or
                MTLPixelFormat.RGB10A2Unorm or
                MTLPixelFormat.RG11B10Float or
                MTLPixelFormat.RGB9E5Float or
                MTLPixelFormat.RG16Float or
                MTLPixelFormat.RG16Unorm or
                MTLPixelFormat.R32Float or
                MTLPixelFormat.Depth32Float => true,
                _ => false,
            };
        }

        private static unsafe void Classify(int frame)
        {
            if (frame < 0)
            {
                return;
            }

            int slot = frame % Slots;

            if (_slotFrame[slot] != frame)
            {
                return;
            }

            _slotFrame[slot] = -1;
            _classified++;

            Span<bool> uniform = stackalloc bool[EntriesPerSlot];
            Span<uint> value = stackalloc uint[EntriesPerSlot];

            for (int entry = 0; entry < EntriesPerSlot; entry++)
            {
                uniform[entry] = false;

                if (!_slotSamples[slot * EntriesPerSlot + entry].Valid)
                {
                    continue;
                }

                uint* words = (uint*)(_contents + slot * SlotBytes + entry * EntryBytes);

                int side = _slotSamples[slot * EntriesPerSlot + entry].Side;
                int texels = side * side;

                uniform[entry] = IsUniform(words, texels, out uint first) &&
                                 IsUniform(words + Texels, texels, out uint second) &&
                                 first == second;
                value[entry] = first;
            }

            bool frameFlat = uniform[0];

            if (frameFlat)
            {
                _frameFlatCount++;

                uint flatValue = value[0];

                if (_inGame)
                {
                    // Either this flat frame is already in the ring, or arm and catch the
                    // next one - they come every few frames.
                    DumpFull(frame, flatValue);
                    _fullArmedUntil = frame + 90;
                }
            }

            if (frameFlat)
            {
                _flatValueHistogram.TryGetValue(value[0], out int seen);
                _flatValueHistogram[value[0]] = seen + 1;
            }

            for (int entry = 1; entry < EntriesPerSlot; entry++)
            {
                ref Sample small = ref _slotSamples[slot * EntriesPerSlot + entry];

                if (!small.Valid || small.Width > 64 || small.Height > 64)
                {
                    continue;
                }

                string key = $"0x{small.Source:X}:{small.Width}x{small.Height}={value[entry]:X8}";

                _forcedInputValues.TryGetValue(key, out (int Flat, int Ok) v);
                _forcedInputValues[key] = (v.Flat + (frameFlat ? 1 : 0), v.Ok + (frameFlat ? 0 : 1));
            }

            {
                int lateEntry = _slotLateEntry[slot];
                ref Sample early = ref _slotSamples[slot * EntriesPerSlot];
                ref Sample late = ref _slotSamples[slot * EntriesPerSlot + lateEntry];

                string verdict = "no-late-sample";

                if (lateEntry > 0 && early.Valid && late.Valid && early.Side == late.Side)
                {
                    bool same = true;

                    unsafe
                    {
                        uint* a = (uint*)(_contents + slot * SlotBytes);
                        uint* b = (uint*)(_contents + slot * SlotBytes + lateEntry * EntryBytes);

                        for (int i = 0; i < early.Side * early.Side; i++)
                        {
                            if (a[i] != b[i])
                            {
                                same = false;

                                break;
                            }
                        }
                    }

                    verdict = same ? "unchanged-after-present" : "CHANGED-after-present";
                }

                _lateStats.TryGetValue(verdict, out (int Flat, int Ok) late2);
                _lateStats[verdict] = (late2.Flat + (frameFlat ? 1 : 0), late2.Ok + (frameFlat ? 0 : 1));
                _slotLateEntry[slot] = 0;
            }

            if (_slotPreDraw[slot] != null)
            {
                StringBuilder pre = new();
                pre.Append(frameFlat ? "FLAT " : "ok   ");

                foreach (string part in _slotPreDraw[slot].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] bits = part.Split(':');
                    int e = int.Parse(bits[2].Substring(1));

                    if (uniform[e])
                    {
                        pre.Append($"{bits[1]}=UNIFORM ");
                    }
                }

                _preDrawStats.TryGetValue(pre.ToString(), out (int Flat, int Ok) pd);
                _preDrawStats[pre.ToString()] = (pd.Flat + (frameFlat ? 1 : 0), pd.Ok + (frameFlat ? 0 : 1));
                _slotPreDraw[slot] = null;
                _slotPreDrawCount[slot] = 0;
            }

            int level = _slotLevel[slot] & 15;
            _levelTotal[level]++;

            if (frameFlat)
            {
                _levelFlat[level]++;
            }

            ulong mask = 0;

            for (int entry = 1; entry < EntriesPerSlot; entry++)
            {
                if (!uniform[entry])
                {
                    continue;
                }

                int id = TrackedId(_slotSamples[slot * EntriesPerSlot + entry].Source);

                if (id >= 0)
                {
                    mask |= 1UL << id;
                }
            }

            int h = ((frame % History) + History) % History;
            _histFrame[h] = frame;
            _histFlat[h] = frameFlat;
            _histMask[h] = mask;

            Correlate(frame - 2);

            bool bigFloatUniform = false;

            for (int entry = 1; entry < EntriesPerSlot; entry++)
            {
                ref Sample sample = ref _slotSamples[slot * EntriesPerSlot + entry];

                if (sample.Valid && uniform[entry] && sample.Width >= 1280 && sample.Format == MTLPixelFormat.RG11B10Float)
                {
                    bigFloatUniform = true;
                }
            }

            for (int i = 0; i < MaxDraws; i++)
            {
                ref DrawInfo tally = ref _slotDraws[slot * MaxDraws + i];

                if (!tally.Valid || tally.TexCount == 0)
                {
                    continue;
                }

                int texId = tally.TexIds[0];

                _drawTexStats.TryGetValue(texId, out (int Bad, int Good, IntPtr Handle, int Width, int Height) tex);

                _drawTexStats[texId] = (
                    tex.Bad + (bigFloatUniform ? 1 : 0),
                    tex.Good + (bigFloatUniform ? 0 : 1),
                    tally.TexHandle[0],
                    tally.TexWidth[0],
                    tally.TexHeight[0]);
            }

            int fullresDraws = 0;

            for (int i = 0; i < MaxDraws; i++)
            {
                ref DrawInfo shape = ref _slotDraws[slot * MaxDraws + i];

                if (!shape.Valid)
                {
                    continue;
                }

                fullresDraws++;

                {
                    string key = $"p={shape.Program} rt={shape.Width}x{shape.Height} " +
                        $"src={(shape.TexCount > 0 ? $"{shape.TexWidth[0]}x{shape.TexHeight[0]}" : "none")}:{shape.Src0Format} tex={shape.TexCount}";

                    _drawShapeStats.TryGetValue(key, out (int Bad, int Good) shapeStats);
                    _drawShapeStats[key] = (shapeStats.Bad + (bigFloatUniform ? 1 : 0), shapeStats.Good + (bigFloatUniform ? 0 : 1));
                }
            }

            {
                string key = $"fullresDraws={fullresDraws}";

                _drawShapeStats.TryGetValue(key, out (int Bad, int Good) countStats);
                _drawShapeStats[key] = (countStats.Bad + (bigFloatUniform ? 1 : 0), countStats.Good + (bigFloatUniform ? 0 : 1));
            }

            bool finalUniform = false;

            for (int entry = 1; entry < EntriesPerSlot; entry++)
            {
                ref Sample sample = ref _slotSamples[slot * EntriesPerSlot + entry];

                if (sample.Valid && uniform[entry] && sample.Width >= 1900 &&
                    sample.Format is MTLPixelFormat.RGBA8Unorm or MTLPixelFormat.BGRA8Unorm
                        or MTLPixelFormat.RGBA8UnormsRGB or MTLPixelFormat.BGRA8UnormsRGB)
                {
                    finalUniform = true;
                }
            }

            if (finalUniform)
            {
                _badFrames++;
                _badDrawSum += _slotFinalDraws[slot];
            }
            else
            {
                _goodFrames++;
                _goodDrawSum += _slotFinalDraws[slot];
            }

            List<string> programs = _slotFinalPrograms[slot];

            if (programs != null)
            {
                foreach (string label in programs)
                {
                    _finalProgramStats.TryGetValue(label, out (int Bad, int Good) programStats);
                    _finalProgramStats[label] = (programStats.Bad + (finalUniform ? 1 : 0), programStats.Good + (finalUniform ? 0 : 1));
                }

                programs.Clear();
            }

            {
                string key = $"presentWrites={_slotPresentWrites[slot]}:{_slotPresentWriteKind[slot]}";

                _writeKindStats.TryGetValue(key, out (int Bad, int Good) writeStats);
                _writeKindStats[key] = (writeStats.Bad + (frameFlat ? 1 : 0), writeStats.Good + (frameFlat ? 0 : 1));
            }

            IntPtr presentHandle = _slotSamples[slot * EntriesPerSlot].Root;

            for (int back = 0; back <= 2; back++)
            {
                int past = frame - back;
                int pastSlot = ((past % Slots) + Slots) % Slots;
                string found = "untracked";
                int passes = 0;
                int draws = 0;
                int writes = 0;
                int matches = 0;

                // The presented texture and the render target can be two objects over one
                // resource. Stopping at the first match returns whichever was reserved
                // first and hides the one that actually wrote the frame.
                for (int i = 0; i < _slotTargetCount[pastSlot]; i++)
                {
                    ref TargetFrameInfo info = ref _slotTargetInfo[pastSlot * MaxTargets + i];

                    if (info.Handle == presentHandle || info.Root == presentHandle)
                    {
                        matches++;
                        passes += info.Passes;
                        draws += info.Draws;
                        writes += info.Writes;
                    }
                }

                if (matches != 0)
                {
                    found = $"m={matches},passes={passes},draws={draws},writes={writes}";
                }

                string key = $"presentSrc@N-{back}:{found}";

                _originStats.TryGetValue(key, out (int Flat, int Ok) origin);
                _originStats[key] = (origin.Flat + (frameFlat ? 1 : 0), origin.Ok + (frameFlat ? 0 : 1));
            }

            if (_slotCbufHash[slot] != 0)
            {
                double[] sums = frameFlat ? _cbufFlatSum : _cbufOkSum;

                int[] odd = frameFlat ? _cbufOddFlat : _cbufOddOk;

                for (int f = 0; f < CbufFields; f++)
                {
                    float v = _slotCbufFields[slot * CbufFields + f];

                    sums[f] += v;

                    // Denormal or non-finite: the fingerprint of integer data, or of an
                    // uninitialised region, being read where a float belongs.
                    if (!float.IsFinite(v) || (v != 0f && Math.Abs(v) < 1e-30f))
                    {
                        odd[f]++;
                    }
                }

                if (frameFlat)
                {
                    _cbufFlatN++;
                }
                else
                {
                    _cbufOkN++;
                }

                _cbufHashes.TryGetValue(_slotCbufHash[slot], out (int Flat, int Ok, string Sample) cbStats);
                _cbufHashes[_slotCbufHash[slot]] = (cbStats.Flat + (frameFlat ? 1 : 0), cbStats.Ok + (frameFlat ? 0 : 1), _slotCbufSample[slot]);
                _slotCbufHash[slot] = 0;
            }

            if (_slotFinalDrawInputs[slot] != null)
            {
                _finalDrawInputs.TryGetValue(_slotFinalDrawInputs[slot], out (int Flat, int Ok) inputStats);
                _finalDrawInputs[_slotFinalDrawInputs[slot]] = (inputStats.Flat + (frameFlat ? 1 : 0), inputStats.Ok + (frameFlat ? 0 : 1));
                _slotFinalDrawInputs[slot] = null;
            }

            if (_slotWriter[slot] != null)
            {
                _writerStats.TryGetValue(_slotWriter[slot], out (int Flat, int Ok) w);
                _writerStats[_slotWriter[slot]] = (w.Flat + (frameFlat ? 1 : 0), w.Ok + (frameFlat ? 0 : 1));
            }

            if (_slotChooser[slot] != null)
            {
                _chooserStats.TryGetValue(_slotChooser[slot], out (int Flat, int Ok) chooser);
                _chooserStats[_slotChooser[slot]] = (chooser.Flat + (frameFlat ? 1 : 0), chooser.Ok + (frameFlat ? 0 : 1));
            }

            AliasScan(frame, slot, frameFlat);

            ReportDraws(frame, slot, bigFloatUniform);

            for (int entry = 0; entry < EntriesPerSlot; entry++)
            {
                ref Sample sample = ref _slotSamples[slot * EntriesPerSlot + entry];

                if (!sample.Valid)
                {
                    continue;
                }

                _stats.TryGetValue(sample.Source, out Stats stats);

                stats.Total++;
                stats.Width = sample.Width;
                stats.Height = sample.Height;
                stats.Format = sample.Format;

                if (uniform[entry])
                {
                    stats.Uniform++;
                    stats.LastUniformValue = value[entry];

                    if (frameFlat)
                    {
                        stats.UniformWhenFrameFlat++;
                    }
                    else
                    {
                        stats.UniformWhenFrameNormal++;
                    }
                }

                _stats[sample.Source] = stats;
            }
        }

        /// <summary>
        /// Prints the inputs of the full resolution float pass once for a frame where
        /// it came out flat and once for a healthy frame, so the two can be diffed.
        /// </summary>
        private static void ReportDraws(int frame, int slot, bool bad)
        {
            if (bad ? _reportedBad : _reportedGood)
            {
                return;
            }

            if (bad)
            {
                _reportedBad = true;
            }
            else
            {
                _reportedGood = true;
            }

            for (int i = 0; i < MaxDraws; i++)
            {
                ref DrawInfo draw = ref _slotDraws[slot * MaxDraws + i];

                if (!draw.Valid)
                {
                    continue;
                }

                StringBuilder text = new();
                text.Append($"frameprobe-draw f={frame} {(bad ? "BAD " : "good")} d={i} p={draw.Program} rt={draw.Width}x{draw.Height} tex=");

                for (int t = 0; t < draw.TexCount; t++)
                {
                    text.Append($"{draw.TexIds[t]}:{draw.TexWidth[t]}x{draw.TexHeight[t]}@0x{draw.TexHandle[t]:X},");
                }

                for (int c = 0; c < CBufs; c++)
                {
                    if (draw.CbufSize[c] == 0)
                    {
                        continue;
                    }

                    text.Append($" cb{c}@{draw.CbufOffset[c]}/{draw.CbufSize[c]}=");

                    for (int v = 0; v < Vec4s; v++)
                    {
                        int b = c * Vec4s * 4 + v * 4;
                        text.Append($"({draw.Cbuf[b]:G6},{draw.Cbuf[b + 1]:G6},{draw.Cbuf[b + 2]:G6},{draw.Cbuf[b + 3]:G6})");
                    }
                }

                Logger.Warning?.PrintMsg(LogClass.Gpu, text.ToString());

                draw.Valid = false;
            }
        }

        /// <summary>
        /// The underlying resource behind a texture view. Two views of one render target
        /// have different native pointers, so comparing those alone reports every
        /// presented framebuffer as a texture nobody ever rendered into.
        /// </summary>
        private static IntPtr RootHandle(Texture texture)
        {
            return texture.ViewRootPtr != IntPtr.Zero
                ? texture.ViewRootPtr
                : texture.GetIdentityHandle().NativePtr;
        }

        private static int TrackedId(IntPtr source)
        {
            for (int i = 0; i < _trackedCount; i++)
            {
                if (_trackedIds[i] == source)
                {
                    return i;
                }
            }

            if (_trackedCount == MaxTracked)
            {
                return -1;
            }

            _trackedIds[_trackedCount] = source;

            return _trackedCount++;
        }

        /// <summary>
        /// Counts, for the flat frame at <paramref name="centre"/>, which targets were
        /// uniform two frames earlier through two frames later. The offset that lights
        /// up is the real pipeline alignment between the command stream and present.
        /// </summary>
        private static void Correlate(int centre)
        {
            if (centre < 2)
            {
                return;
            }

            int c = centre % History;

            if (_histFrame[c] != centre || !_histFlat[c])
            {
                return;
            }

            _flatCentres++;

            for (int offset = 0; offset < Offsets; offset++)
            {
                int frame = centre + offset - 2;
                int h = frame % History;

                if (_histFrame[h] != frame)
                {
                    continue;
                }

                ulong mask = _histMask[h];

                for (int id = 0; id < _trackedCount && mask != 0; id++)
                {
                    if ((mask & (1UL << id)) == 0)
                    {
                        continue;
                    }

                    _stats.TryGetValue(_trackedIds[id], out Stats stats);
                    stats.OnFlat ??= new int[Offsets];
                    stats.OnFlat[offset]++;
                    _stats[_trackedIds[id]] = stats;
                }
            }
        }

        private static unsafe void AliasScan(int frame, int slot, bool frameFlat)
        {
            ref Sample present = ref _slotSamples[slot * EntriesPerSlot];

            if (!present.Valid)
            {
                return;
            }

            uint* presentWords = (uint*)(_contents + slot * SlotBytes);
            int presentTexels = present.Side * present.Side;
            string match = "none";

            // Entries 1.._presentSourceCount are the reserved swap chain buffers, one of
            // which IS the texture being compared - matching against itself proves nothing.
            for (int entry = _presentSourceCount + 1; entry < EntriesPerSlot; entry++)
            {
                ref Sample other = ref _slotSamples[slot * EntriesPerSlot + entry];

                if (!other.Valid || other.Side != present.Side)
                {
                    continue;
                }

                uint* otherWords = (uint*)(_contents + slot * SlotBytes + entry * EntryBytes);
                bool same = true;

                for (int i = 0; i < presentTexels * Patches; i++)
                {
                    // Patch 1 starts at Texels regardless of side, so walk both patches
                    // through the same fixed stride the sampler wrote them with.
                    int index = (i / presentTexels) * Texels + (i % presentTexels);

                    if (presentWords[index] != otherWords[index])
                    {
                        same = false;

                        break;
                    }
                }

                if (same)
                {
                    // Entries 1..N map to _frameTargets[0..N-1], so the per frame write
                    // record for the real owner of this memory is one index back.
                    ref TargetFrameInfo info = ref _slotTargetInfo[slot * MaxTargets + (entry - 1)];

                    match = $"{other.Width}x{other.Height}:{other.Format} passes={info.Passes} draws={info.Draws} writes={info.Writes}";

                    break;
                }
            }

            _aliasStats.TryGetValue(match, out (int Flat, int Ok) alias);
            _aliasStats[match] = (alias.Flat + (frameFlat ? 1 : 0), alias.Ok + (frameFlat ? 0 : 1));
        }

        private static unsafe bool IsUniform(uint* words, int texels, out uint first)
        {
            first = words[0];

            // Two distinct values are still uniform: the presented framebuffer is
            // dithered, so a genuinely flat white patch holds a pair of neighbours.
            uint second = first;

            for (int i = 1; i < texels; i++)
            {
                uint word = words[i];

                if (word == first || word == second)
                {
                    continue;
                }

                if (second == first)
                {
                    second = word;
                    continue;
                }

                return false;
            }

            return true;
        }

        private static void Report()
        {
            StringBuilder text = new();

            text.Append($"frameprobe f={_frame} classified={_classified} flatFrames={_frameFlatCount} flatCentres={_flatCentres} targets={_stats.Count}");

            foreach ((int texId, (int Bad, int Good, IntPtr Handle, int Width, int Height) tex) in _drawTexStats)
            {
                string sampled = _stats.TryGetValue(tex.Handle, out Stats srcStats)
                    ? $"n={srcStats.Total},uni={srcStats.Uniform}"
                    : "NOT-SAMPLED";

                text.Append($" fullresSrc[{texId}]=0x{tex.Handle:X}:{tex.Width}x{tex.Height}:bad={tex.Bad},good={tex.Good},{sampled}");
            }

            foreach ((string key, (int Bad, int Good) shape) in _drawShapeStats)
            {
                text.Append($" {{{key}:bad={shape.Bad},good={shape.Good}}}");
            }

            text.Append($" final: bad={_badFrames}(draws {(_badFrames == 0 ? 0 : _badDrawSum / _badFrames)}) good={_goodFrames}(draws {(_goodFrames == 0 ? 0 : _goodDrawSum / _goodFrames)})");

            foreach ((string label, (int Bad, int Good) programStats) in _finalProgramStats)
            {
                // Only the lopsided ones matter: a stage present on every frame explains
                // nothing, a stage present on one side of the split explains everything.
                bool everywhere = programStats.Bad == _badFrames && programStats.Good == _goodFrames;

                if (everywhere)
                {
                    continue;
                }

                text.Append($" prog[{label}]=bad{programStats.Bad}/{_badFrames},good{programStats.Good}/{_goodFrames}");
            }

            foreach ((string match, (int Flat, int Ok) alias) in _aliasStats)
            {
                text.Append($" alias[{match}]=flat{alias.Flat},ok{alias.Ok}");
            }

            foreach ((string writer, (int Flat, int Ok) w) in _writerStats)
            {
                text.Append($" presentWriter[{writer}]=flat{w.Flat},ok{w.Ok}");
            }

            text.Append($" nativeDsts={_nativeWriters.Count} truncated={_truncated} overlaySeen={_overlaySeen} overlaySkipped={_overlaySkipped}");
            text.Append($" writeHookTotal={_totalWrites} kinds=");

            foreach (string kind in _writeKindsSeen)
            {
                text.Append($"{kind},");
            }

            if (_cbufFlatN > 0 && _cbufOkN > 0)
            {
                (int Field, double Rel, double Flat, double Ok)[] diffs = new (int, double, double, double)[CbufFields];

                for (int f = 0; f < CbufFields; f++)
                {
                    double flatMean = _cbufFlatSum[f] / _cbufFlatN;
                    double okMean = _cbufOkSum[f] / _cbufOkN;
                    double scale = Math.Max(Math.Abs(flatMean), Math.Abs(okMean));
                    double rel = scale > 1e-9 ? Math.Abs(flatMean - okMean) / scale : 0;

                    diffs[f] = (f, rel, flatMean, okMean);
                }

                text.Append($" cbufFields(flatN={_cbufFlatN},okN={_cbufOkN}):");

                int oddFlatTotal = 0;
                int oddOkTotal = 0;

                for (int f = 0; f < CbufFields; f++)
                {
                    oddFlatTotal += _cbufOddFlat[f];
                    oddOkTotal += _cbufOddOk[f];
                }

                text.Append($" odd(flat={oddFlatTotal},ok={oddOkTotal})");

                for (int f = 0; f < CbufFields; f++)
                {
                    if (_cbufOddFlat[f] == 0 && _cbufOddOk[f] == 0)
                    {
                        continue;
                    }

                    double flatRate = _cbufOddFlat[f] / (double)_cbufFlatN;
                    double okRate = _cbufOddOk[f] / (double)_cbufOkN;

                    if (flatRate > okRate * 2 + 0.05)
                    {
                        text.Append($" ODD f{f}[flat={flatRate:P0},ok={okRate:P0}]");
                    }
                }

                foreach ((int Field, double Rel, double Flat, double Ok) d in diffs.OrderByDescending(x => x.Rel).Take(6))
                {
                    text.Append($" f{d.Field}[rel={d.Rel:F3} flat={d.Flat:G6} ok={d.Ok:G6}]");
                }
            }

            int cbufFlatOnly = 0;
            int cbufTotal = _cbufHashes.Count;

            foreach ((ulong cbHash, (int Flat, int Ok, string Sample) v) in _cbufHashes.OrderByDescending(kv => kv.Value.Flat).Take(4))
            {
                text.Append($" CB[{cbHash:X16}]=flat{v.Flat},ok{v.Ok},{v.Sample}");
            }

            foreach ((ulong cbHash2, (int Flat, int Ok, string Sample) v) in _cbufHashes)
            {
                if (v.Flat > 0 && v.Ok == 0)
                {
                    cbufFlatOnly++;
                }
            }

            text.Append($" cbufDistinct={cbufTotal} cbufFlatOnly={cbufFlatOnly}");

            foreach ((string key, (int Flat, int Ok) v) in _preDrawStats)
            {
                text.Append($" PRE[{key.Trim()}]=f{v.Flat},o{v.Ok}");
            }

            foreach ((string key, (int Flat, int Ok) v) in _lateStats)
            {
                text.Append($" LATE[{key}]=flat{v.Flat},ok{v.Ok}");
            }

            foreach ((string key, (int Flat, int Ok) v) in _forcedInputValues)
            {
                text.Append($" LUT[{key}]=flat{v.Flat},ok{v.Ok}");
            }

            foreach ((string key, (int Flat, int Ok) inputs) in _finalDrawInputs)
            {
                text.Append($" IN[{key}]=flat{inputs.Flat},ok{inputs.Ok}");
            }

            foreach ((string key, (int Flat, int Ok) chooser) in _chooserStats)
            {
                text.Append($" {{{key}:flat={chooser.Flat},ok={chooser.Ok}}}");
            }

            foreach ((string key, (int Flat, int Ok) origin) in _originStats)
            {
                if (origin.Flat == 0)
                {
                    continue;
                }

                text.Append($" <{key}:flat={origin.Flat},ok={origin.Ok}>");
            }

            foreach ((string key, (int Bad, int Good) writeStats) in _writeKindStats)
            {
                text.Append($" <{key}:flat={writeStats.Bad},ok={writeStats.Good}>");
            }

            foreach (MTLPixelFormat format in _skippedFormats)
            {
                text.Append($" skipped={format}");
            }

            foreach ((IntPtr source, Stats stats) in _stats)
            {
                if (stats.Uniform == 0)
                {
                    continue;
                }

                int[] onFlat = stats.OnFlat ?? new int[Offsets];

                text.Append(
                    $" [0x{source:X}:{stats.Width}x{stats.Height}:{stats.Format} n={stats.Total} " +
                    $"uni={stats.Uniform} onFlat(-2..+2)={onFlat[0]}/{onFlat[1]}/{onFlat[2]}/{onFlat[3]}/{onFlat[4]} " +
                    $"val=0x{stats.LastUniformValue:X8}]");
            }

            // The listing above skips targets that were never uniform, which is exactly
            // the set that differs between day and night (94 targets vs 53). Without an
            // unfiltered census the daylight-only passes cannot be named at all.
            Dictionary<string, int> census = new();

            foreach ((_, Stats stats) in _stats)
            {
                string shape = $"{stats.Width}x{stats.Height}:{stats.Format}";

                census.TryGetValue(shape, out int seen);
                census[shape] = seen + 1;
            }

            text.Append(" census:");

            foreach ((string shape, int count) in census.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key))
            {
                text.Append($" {shape}*{count}");
            }

            StringBuilder levels = new();
            levels.Append("frameprobe-ab");

            for (int level = 0; level < _levelTotal.Length; level++)
            {
                if (_levelTotal[level] == 0)
                {
                    continue;
                }

                double rate = _levelFlat[level] / (double)_levelTotal[level];
                double se = Math.Sqrt(rate * (1 - rate) / _levelTotal[level]);

                levels.Append($" L{level}={_levelFlat[level]}/{_levelTotal[level]}({rate * 100:F2}%±{se * 200:F2})");
            }

            levels.Append(" flatVals:");

            foreach ((uint v, int c) in _flatValueHistogram.OrderByDescending(kv => kv.Value).Take(6))
            {
                levels.Append($" 0x{v:X8}x{c}");
            }

            levels.Append($" distinct={_flatValueHistogram.Count}");

            Logger.Warning?.PrintMsg(LogClass.Gpu, levels.ToString());
            Logger.Warning?.PrintMsg(LogClass.Gpu, text.ToString());

            _stats.Clear();
            _classified = 0;
            _frameFlatCount = 0;
            _flatCentres = 0;
            _reportedBad = false;
            _reportedGood = false;
            _drawTexStats.Clear();
            _drawShapeStats.Clear();
            _finalProgramStats.Clear();
            _writeKindStats.Clear();
            _originStats.Clear();
            _chooserStats.Clear();
            _finalDrawInputs.Clear();
            _forcedInputValues.Clear();
            _lateStats.Clear();
            _preDrawStats.Clear();
            _cbufHashes.Clear();
            Array.Clear(_cbufFlatSum);
            Array.Clear(_cbufOkSum);
            _cbufFlatN = 0;
            _cbufOkN = 0;
            Array.Clear(_cbufOddFlat);
            Array.Clear(_cbufOddOk);
            _writerStats.Clear();
            _aliasStats.Clear();
            _badFrames = 0;
            _goodFrames = 0;
            _badDrawSum = 0;
            _goodDrawSum = 0;
        }

        public static void Shutdown()
        {
            if (_initialized)
            {
                _buffer.Dispose();
                _initialized = false;
            }
        }
    }
}
