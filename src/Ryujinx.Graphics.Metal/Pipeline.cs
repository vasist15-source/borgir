using Ryujinx.Common.Memory;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.QuartzCore;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Why a render pass was ended. Passes are the expensive unit on a tile based
    /// GPU, so the stats line attributes them to whatever forced the split.
    /// </summary>
    public enum PassEndReason
    {
        Unspecified,
        SwapState,
        FragmentDependencySkipped,
        Present,
        Flush,
        DepthDump,
        FragmentDependency,
        ColorMask,
        RenderTargets,
        Counter,
        Barrier,
        BlitEncoder,
        ComputeEncoder,
        Dispose,
        DrawBudget,
        FragmentDependencyDeferred,
    }

    public enum EncoderType
    {
        Blit,
        Compute,
        Render,
        None
    }

    [SupportedOSPlatform("macos")]
    class Pipeline : IPipeline, IEncoderFactory, IDisposable
    {
        private const ulong MinByteWeightForFlush = 256 * 1024 * 1024; // MiB
        private const int MaxDisposedResourceCountForFlush = 4096;
        private const int SyncStatsLogFrameInterval = 120;

        // RYUJINX_METAL_FRAME_LINE=1: one log line per presented frame.
        private static readonly bool _frameLine =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_FRAME_LINE") == "1";
        private ulong _lastFrameLineDraws;
        private ulong _lastFrameLineRenderPasses;

        // Pipeline-state compiles are attributed per presented frame, then folded into
        // the 120-frame block as "frames that compiled" and "worst frame": a burst of
        // three stalled frames averages away to nothing over 120, and the burst is the
        // thing being looked for.
        private long _lastPresentPsoCreated;
        private long _lastPresentPsoTicks;
        private long _statsPsoFramesWithCreation;
        private long _statsPsoWorstFrameTicks;
        private long _statsPsoWorstFrameCount;
        private long _lastStatsPsoRenderCreated;
        private long _lastStatsPsoRenderQueued;
        private long _lastStatsPsoPendingSkips;
        private long _lastStatsPsoWaited;
        private long _lastStatsPsoWaitTicks;
        private long _lastStatsPsoSyncFallbacks;
        private long _lastStatsPsoRenderTicks;
        private long _lastStatsPsoComputeCreated;
        private long _lastStatsPsoComputeTicks;

        private readonly MTLDevice _device;
        private readonly MetalRenderer _renderer;
        private EncoderStateManager _encoderStateManager;
        private ulong _byteWeight;
        private int _disposedResourceCount;
        private int _presentCount;

        // The per-frame slot the probes index by. Passes encoded now belong to the frame
        // this present will close, which is the same numbering PresentProbe classifies by.
        internal int FrameSlot => _presentCount;
        private int _autoFlushDrawCount;
        private int _autoFlushAttachmentCount;

        // Render passes are the expensive unit on a tile based GPU: each one loads and
        // stores every attachment. Counted here against draws so the stats line says
        // how many draws a pass is actually amortised over.
        private ulong _renderPassCount;
        private ulong _lastStatsDrawCount;
        private ulong _lastStatsRenderPassCount;
        private long _lastStatsTicks = Stopwatch.GetTimestamp();
        private readonly int[] _passEndReasons = new int[Enum.GetValues<PassEndReason>().Length];
        private ulong _drawCountAtPassStart;

        // Subtraction harness. Nineteen configurations of the standalone
        // reproducer failed to synthesise the fault by adding properties, so the
        // remaining move is to remove them from the real frame instead: drop the
        // draws in a window of passes and watch whether the flat rate moves.
        // Passes are still opened, so attachments, load actions and store
        // actions are untouched - only what is drawn changes.
        //
        // /tmp/ryujinx-metal-skip-draws holds "A-B" (pass indexes within the
        // frame, half open), re-read once a frame. Empty or absent disables it.
        // The composite is pass 2 and the scene finishes around pass 167, so
        // "3-167" removes the scene render while leaving the consumer intact.
        private int _passIndexInFrame;
        private static int _skipDrawsFrom = -1;
        private static int _skipDrawsTo = -1;
        private static long _skippedDraws;

        // Skip every draw whose program label starts with one of these prefixes.
        // /tmp/ryujinx-metal-skip-program holds a comma separated list, re-read once a
        // frame. Unlike the pass-window skip this removes a handful of draws rather
        // than the whole scene render, so the picture survives - which the correlator's
        // per-outcome luma confirms rather than assumes.
        private static string[] _skipPrograms = [];
        private static long _skippedByProgram;

        // On by default. Ryujinx's Vulkan backend issues pipeline barriers, and Metal
        // cannot express a read-after-write barrier inside a render encoder, so MoltenVK
        // resolves one by ending the encoder - the Vulkan path splits at every
        // read-after-write for free, and does not flash. This backend issued no barrier
        // of any kind and leaned entirely on Metal's automatic hazard tracking, which for
        // argument-buffer reads is fed by useResource rather than by anything the driver
        // observes. Doing what Vulkan does removes roughly half the white frames:
        // 45.2% -> 24.3% -> 41.5% and 36.7% -> 27.7% across two sessions, reversible,
        // 16 SE, at 30.01 fps against 29.99 with it off.
        //
        // Colour attachments only, scoped to the command buffer. Adding depth
        // attachments, storage-image stores and blit writes changed the rate by nothing
        // (25.0% against 24.3%) and widening the scope to the whole frame made every draw
        // its own pass for no benefit at all - both reverted.
        //
        // It is not a cure: a floor of about 25% is completely indifferent to ordering,
        // and Vulkan sits at zero under the same conditions, so something else still
        // differs. RYUJINX_METAL_RAW_SPLIT=0 opts out; the frame cost was measured only
        // at the reproducing save, where there is headroom inside the 30fps cap.
        private static readonly bool _dumpAllShaders = Environment.GetEnvironmentVariable("RYUJINX_METAL_DUMP_SHADERS") == "1";
        private static readonly bool _drawTraceOn = Environment.GetEnvironmentVariable("RYUJINX_METAL_DRAW_TRACE") == "1";
        private static readonly bool _passTraceOn = Environment.GetEnvironmentVariable("RYUJINX_METAL_PASS_TRACE") == "1";
        // /tmp/ryujinx-metal-barrier-scope can explicitly select "hazard", "all" or
        // "deferred" for same-session diagnosis; absent/unknown values use the
        // environment default. The bindings visible at the barrier can belong to the
        // preceding draw, so a hazard check there does not prove that subsequent draws
        // will not consume earlier writes. The draw-time path also permits some
        // self-reads, and cannot replace the skipped barrier. The old policy stays as
        // an explicit diagnostic opt-in; RYUJINX_METAL_BARRIER_SCOPE=all ends the pass
        // at every guest barrier.
        private static bool _barrierHazardOnly =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_BARRIER_SCOPE") == "hazard";
        private static readonly bool _barrierHazardDefault = _barrierHazardOnly;
        // Default (v486, 2026-09-11; RYUJINX_METAL_BARRIER_SCOPE=deferred): a guest
        // texture barrier ends nothing by itself; it marks what the pass has written,
        // and the draw that later samples one
        // of those attachments ends the pass right before itself (the barrier's actual
        // consumer, which the hazard heuristic could not see because it judged from the
        // preceding draw's bindings). A barrier nothing samples costs no pass. Passes with
        // a fragment storage store still end at the barrier, as in hazard mode. Needs the
        // draw-time RAW split; without it the mode falls back to "all".
        private static bool _barrierDeferred =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_BARRIER_SCOPE") is not ("all" or "hazard");
        private static readonly bool _barrierDeferredDefault = _barrierDeferred;

        private static void RefreshBarrierScope()
        {
            try
            {
                string scope = System.IO.File.Exists("/tmp/ryujinx-metal-barrier-scope")
                    ? System.IO.File.ReadAllText("/tmp/ryujinx-metal-barrier-scope").Trim()
                    : null;

                _barrierHazardOnly = scope switch
                {
                    "hazard" => true,
                    "all" or "deferred" => false,
                    _ => _barrierHazardDefault,
                };
                _barrierDeferred = scope switch
                {
                    "deferred" => true,
                    "all" or "hazard" => false,
                    _ => _barrierDeferredDefault,
                };
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; the next frame picks it up.
            }
        }
        private static readonly string _stageLabel = Environment.GetEnvironmentVariable("RYUJINX_METAL_STAGE_LABEL") ?? "";
        // The program whose input is read by the compute engine right before its pass, at the
        // read-after-write split point (UploadCorrelator.PreCompositeProbe). Default: the
        // game's 12-tap upscaler, whose output is the first white surface of the frame.
        private static readonly string _preProbeLabel = Environment.GetEnvironmentVariable("RYUJINX_METAL_PREPROBE_LABEL") ?? "7d92cd";
        private static long _preDraws, _preDrawsFlushedBefore, _preSplitHits, _preSplitNull, _preDrawsSplitPath;
        private static bool _rawSplit =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_RAW_SPLIT") != "0";

        // NVN semantics for a draw whose only read-after-write hazard is its own pass's
        // attachments: on the guest hardware that exact read carries no barrier and is
        // served stale data, and the game shipped against that behaviour. The classifier
        // measured the remaining split load as almost entirely this shape (hotSelf 150-760
        // a frame against ~30 barriers the game actually issues); skipping reproduces the
        // guest contract - unbarriered self-reads see stale content, the game's own
        // TextureBarrier calls still split, and data formats (R32Float first among them,
        // the depth/picking chain Ultrahand reads) are never skipped - serving those stale
        // is precisely how grabbing broke, twice.
        //
        // Default on since 2026-08-30: measured +61% inside one session at equal spots
        // (27.8 -> 44.7 fps median, passes 530 -> 198, sync waits 2686 -> 193 ms/block),
        // then held at 45 by the mod's own FPS cap; Ultrahand and sunlight verified by
        // hand after the format restriction. RYUJINX_METAL_SKIP_SELF_SPLIT=0 or
        // /tmp/ryujinx-metal-skip-self-split containing 0 to revert, re-read once a frame.
        private static bool _skipSelfSplit =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_SKIP_SELF_SPLIT") != "0";

        private static readonly bool _skipSelfSplitDefault = _skipSelfSplit;

        // Same-pixel self-reads of a data-format attachment go to a framebuffer-fetch
        // variant of the shader instead of ending the pass. RYUJINX_METAL_FB_FETCH=0 or
        // /tmp/ryujinx-metal-fb-fetch containing 0 to revert, re-read once a frame.
        private static bool _fbFetch =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_FB_FETCH") != "0";

        private static readonly bool _fbFetchDefault = _fbFetch;

        private static void RefreshFbFetch()
        {
            try
            {
                _fbFetch = System.IO.File.Exists("/tmp/ryujinx-metal-fb-fetch")
                    ? System.IO.File.ReadAllText("/tmp/ryujinx-metal-fb-fetch").Trim() == "1"
                    : _fbFetchDefault;
                TileSnapshot.Refresh();
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; next frame picks it up.
            }
        }

        private static void RefreshSkipSelfSplit()
        {
            try
            {
                _skipSelfSplit = System.IO.File.Exists("/tmp/ryujinx-metal-skip-self-split")
                    ? System.IO.File.ReadAllText("/tmp/ryujinx-metal-skip-self-split").Trim() == "1"
                    : _skipSelfSplitDefault;
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; next frame picks it up.
            }
        }

        // Forces the split for the blit that writes the presented surface, whether or not
        // SamplesEarlierWrite() notices the dependency. RYUJINX_METAL_SPLIT_BLIT=1.
        private static readonly int _canaryClear2 =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_CANARY_CLEAR2"), out int cc2) ? cc2 : 0;

        private static readonly HashSet<string> _clearCensus = new();

        private static readonly bool _presentBarrier =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_PRESENT_BARRIER") == "1";

        private static readonly bool _splitBlit =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_SPLIT_BLIT") == "1";

        /// <summary>
        /// Ends the pass before every draw, not only those SamplesEarlierWrite() flags.
        /// "Full serialisation changed nothing" is recorded in the handoff from an earlier
        /// session, and by today's standards that result is not trustworthy: it predates the
        /// admissibility gate and the drive-in's draw-count check, and five arms measured
        /// today turned out to be menus or dark scenes reporting a clean zero. If the split's
        /// benefit is monotonic in how much it splits, this is where it ends up.
        /// RYUJINX_METAL_SPLIT_ALL=1.
        /// </summary>
        private static readonly bool _splitAll =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_SPLIT_ALL") == "1";

        private static readonly bool _rawSplitDefault = _rawSplit;
        private static long _rawSplits;

        // Ending the encoder only stops two pieces of work being encoded together; it
        // does not make the GPU wait. MoltenVK, translating a VkImageMemoryBarrier, has
        // MTLFence available for that and this backend has never used one - there is no
        // MTLFence and no MTLEvent anywhere in it. If the read-after-write split works by
        // narrowing a race window rather than removing a cause, which is what the
        // content-independent floor suggests, then a real wait is the difference between
        // narrowing it and closing it. Same detection, stronger primitive.
        // RYUJINX_METAL_RAW_FENCE=1, hot via /tmp/ryujinx-metal-raw-fence.
        private static bool _rawFence =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_RAW_FENCE") == "1";

        private static readonly bool _rawFenceDefault = _rawFence;
        private MTLFence _fence;
        // RYUJINX_METAL_ENCODER_FENCE=1: every render encoder updates a fence after its
        // fragment stage and the next render encoder waits on it BEFORE ITS VERTEX STAGE.
        // The earlier RAW_FENCE experiment waited before the fragment stage only, which
        // leaves a later encoder's vertex shader free to run ahead of the previous
        // encoder's fragment writes - the flare's count slot is written by fragment
        // atomics and then reset by a vertex-stage store a few encoders later.
        private static readonly bool _encoderFence = Environment.GetEnvironmentVariable("RYUJINX_METAL_ENCODER_FENCE") == "1";
        private MTLFence _encFence;
        private bool _encFencePending;
        private bool _fenceWaitPending;
        private static long _fenceWaits;

        private static void RefreshRawFence()
        {
            try
            {
                _rawFence = System.IO.File.Exists("/tmp/ryujinx-metal-raw-fence")
                    ? System.IO.File.ReadAllText("/tmp/ryujinx-metal-raw-fence").Trim() == "1"
                    : _rawFenceDefault;
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; next frame picks it up.
            }
        }

        private static void RefreshRawSplit()
        {
            try
            {
                _rawSplit = System.IO.File.Exists("/tmp/ryujinx-metal-raw-split")
                    ? System.IO.File.ReadAllText("/tmp/ryujinx-metal-raw-split").Trim() == "1"
                    : _rawSplitDefault;
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; next frame picks it up.
            }
        }

        private static void RefreshSkipProgram()
        {
            try
            {
                string text = System.IO.File.Exists("/tmp/ryujinx-metal-skip-program")
                    ? System.IO.File.ReadAllText("/tmp/ryujinx-metal-skip-program").Trim()
                    : string.Empty;

                _skipPrograms = text.Length == 0
                    ? []
                    : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; next frame picks it up.
            }
        }

        private bool SkipThisProgram()
        {
            if (_skipPrograms.Length == 0)
            {
                return false;
            }

            string label = _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel;

            if (label == null)
            {
                return false;
            }

            foreach (string prefix in _skipPrograms)
            {
                if (label.StartsWith(prefix, StringComparison.Ordinal))
                {
                    _skippedByProgram++;

                    return true;
                }
            }

            return false;
        }

        private static void RefreshSkipDraws()
        {
            try
            {
                string text = System.IO.File.Exists("/tmp/ryujinx-metal-skip-draws")
                    ? System.IO.File.ReadAllText("/tmp/ryujinx-metal-skip-draws").Trim()
                    : string.Empty;

                string[] parts = text.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out int from) &&
                    int.TryParse(parts[1], out int to))
                {
                    _skipDrawsFrom = from;
                    _skipDrawsTo = to;
                }
                else
                {
                    _skipDrawsFrom = _skipDrawsTo = -1;
                }
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; next frame picks it up.
            }
        }

        /// <summary>
        /// Attributes this draw to the storage it is drawing into, so the presented
        /// surface can be described by what actually wrote it - in the frame that wrote
        /// it, which the age table showed is the frame before it is shown.
        /// </summary>
        private void NoteAttachmentWriter()
        {
            if (!UploadCorrelator.Enabled)
            {
                return;
            }

            if (_passTraceOn)
            {
                UploadCorrelator.NotePassDrawLabel(_encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel is string dl && dl.Length > 6 ? dl[..6] : "?");
            }
            if (_dumpAllShaders)
            {
                _encoderStateManager.RenderProgram?.DumpSources(FrameCapture.ShaderDumpDir);
            }

            // All colour attachments, not only slot 0: the game's final render target can
            // sit in a higher MRT slot, and a census keyed on slot 0 alone never sees it.
            for (int rtIndex = 0; rtIndex < _encoderStateManager.RenderTargets.Length; rtIndex++)
            {
                if (_encoderStateManager.RenderTargets[rtIndex] is not Texture target)
                {
                    continue;
                }

                UploadCorrelator.NoteFullResAttachment(target);
                UploadCorrelator.NoteAttachmentDraw(
                    target.CanonicalPtr,
                    _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel);

                if (rtIndex == 0)
                {
                    UploadCorrelator.NoteWriterCb(
                        target.CanonicalPtr, Cbs.CommandBufferIndex,
                        _renderer.CommandBufferPool.RentSeqOf(Cbs.CommandBufferIndex));
                }
            }
        }

        internal long PoolRentSeq(int cbIndex) => _renderer.CommandBufferPool.RentSeqOf(cbIndex);

        // RYUJINX_METAL_WATCH_ENCODE=<label prefix>: dump how one program's draw is encoded,
        // once per occurrence.
        private static readonly bool _watchEncode =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_WATCH_ENCODE") == "1";

        private bool SkipThisDraw()
        {
            if (SkipThisProgram())
            {
                return true;
            }

            if (_skipDrawsFrom < 0 || _passIndexInFrame < _skipDrawsFrom || _passIndexInFrame >= _skipDrawsTo)
            {
                return false;
            }

            _skippedDraws++;

            return true;
        }

        // Draws allowed in one render pass before it is split (0 = never). Seeded from
        // RYUJINX_METAL_PASS_SPLIT_DRAWS; /tmp/ryujinx-metal-pass-split-draws overrides
        // it hot, re-read once per frame, so both arms run inside one session.
        private static int _passSplitDraws =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_PASS_SPLIT_DRAWS"), out int passSplitDraws)
                ? passSplitDraws
                : 0;

        private static readonly int _passSplitDrawsDefault = _passSplitDraws;

        // Card 3 toggle and its scratch surface. Hot via /tmp/ryujinx-metal-bounce-scene.
        //   1 - full bounce, scene -> scratch -> scene
        //   2 - positive control: scratch -> scene only, without filling scratch first.
        //       The scratch holds anything but this frame's scene, so if the hook is live
        //       the picture must visibly break. A null from mode 1 means nothing until
        //       mode 2 has been seen to break the screen: the first attempt at this card
        //       measured "no effect" from a hook that never fired, because the bound
        //       texture arrived through TextureArrayRefs and the walk only read
        //       TextureRefs.
        private static int _bounceSceneInput =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_BOUNCE_SCENE"), out int bounceScene)
                ? bounceScene
                : 0;

        private static readonly int _bounceSceneInputDefault = _bounceSceneInput;

        private Texture _bounceScratch;
        private int _bounceScratchWidth;
        private int _bounceScratchHeight;
        private int _bouncedAtPresent = -1;
        private int _bounceCount;
        private int _bounceMissCount;

        private static void RefreshBounceScene()
        {
            try
            {
                if (System.IO.File.Exists("/tmp/ryujinx-metal-bounce-scene"))
                {
                    _bounceSceneInput =
                        int.TryParse(System.IO.File.ReadAllText("/tmp/ryujinx-metal-bounce-scene").Trim(), out int value)
                            ? value
                            : _bounceSceneInputDefault;
                }
                else
                {
                    _bounceSceneInput = _bounceSceneInputDefault;
                }
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; next frame picks it up.
            }
        }

        /// <summary>
        /// Copies the bound scene texture out to a scratch surface and straight back, so
        /// the surface the next pass samples has just been written by the blit engine.
        /// Once per frame: the composite is the second pass of the frame and the only one
        /// that reads this class, and repeating it per pass would cost bandwidth for
        /// nothing.
        /// </summary>
        private void BounceSceneInput()
        {
            if (_bouncedAtPresent == _presentCount)
            {
                return;
            }

            Texture scene = _encoderStateManager.SceneClassSampledTexture();

            if (scene == null)
            {
                _bounceMissCount++;
                return;
            }

            _bouncedAtPresent = _presentCount;
            _bounceCount++;

            // Dynamic resolution moves this size during play. Rebuilding the scratch on
            // every change is what freed a live attachment under FlashGuard and faulted
            // the driver, so only grow it, and never release the old one mid-flight.
            if (_bounceScratch == null || scene.Width != _bounceScratchWidth || scene.Height != _bounceScratchHeight)
            {
                _bounceScratch = new Texture(_device, _renderer, this, scene.Info);
                _bounceScratchWidth = scene.Width;
                _bounceScratchHeight = scene.Height;

                if (_bounceSceneInput == 2)
                {
                    // Make the control unmistakable. A fresh texture's undefined content
                    // turned out to be indistinguishable from the scene on screen, which
                    // is no control at all; 0x55 packs to a constant that is neither the
                    // scene nor white, so if the copy lands the picture cannot survive it.
                    int bytes = scene.Info.Width * scene.Info.Height * 4;
                    MemoryOwner<byte> fill = MemoryOwner<byte>.Rent(bytes);
                    fill.Span.Fill(0x55);
                    _bounceScratch.SetData(fill);
                }
            }

            MTLBlitCommandEncoder blit = Cbs.Encoders.EnsureBlitEncoder();

            MTLTexture src = scene.GetHandle(Cbs);
            MTLTexture scratch = _bounceScratch.GetHandle(Cbs);

            MTLOrigin origin = new();
            MTLSize size = new() { width = (ulong)scene.Width, height = (ulong)scene.Height, depth = 1 };

            if (_bounceSceneInput != 2)
            {
                blit.CopyFromTexture(src, 0, 0, origin, size, scratch, 0, 0, origin);
            }

            blit.CopyFromTexture(scratch, 0, 0, origin, size, src, 0, 0, origin);

            if (_bounceCount % 600 == 1)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"bounce mode={_bounceSceneInput} fired={_bounceCount} missed={_bounceMissCount} " +
                    $"{scene.Width}x{scene.Height} {scene.MtlFormat}");
            }
        }

        private static void RefreshPassSplit()
        {
            try
            {
                if (System.IO.File.Exists("/tmp/ryujinx-metal-pass-split-draws"))
                {
                    _passSplitDraws =
                        int.TryParse(System.IO.File.ReadAllText("/tmp/ryujinx-metal-pass-split-draws").Trim(), out int value)
                            ? value
                            : _passSplitDrawsDefault;
                }
                else
                {
                    _passSplitDraws = _passSplitDrawsDefault;
                }
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; next frame picks it up.
            }
        }

        public ulong DrawCount { get; private set; }
        public ulong DispatchCount { get; private set; }

        public MTLCommandBuffer CommandBuffer;

        public IndexBufferPattern QuadsToTrisPattern;
        public IndexBufferPattern TriFanToTrisPattern;

        internal CommandBufferScoped? PreloadCbs { get; private set; }
        internal CommandBufferScoped Cbs { get; private set; }
        internal CommandBufferEncoder Encoders => Cbs.Encoders;
        internal EncoderType CurrentEncoderType => Encoders.CurrentEncoderType;
        internal bool SupportsSamplesPassed => _renderer.Counters.SupportsSamplesPassed;

        public Pipeline(MTLDevice device, MetalRenderer renderer)
        {
            _device = device;
            _renderer = renderer;

            renderer.CommandBufferPool.Initialize(this);

            CommandBuffer = (Cbs = _renderer.CommandBufferPool.Rent()).CommandBuffer;
        }

        internal void InitEncoderStateManager(BufferManager bufferManager)
        {
            _encoderStateManager = new EncoderStateManager(_device, bufferManager, this);

            QuadsToTrisPattern = new IndexBufferPattern(_renderer, 4, 6, 0, [0, 1, 2, 0, 2, 3], 4, false);
            TriFanToTrisPattern = new IndexBufferPattern(_renderer, 3, 3, 2, [int.MinValue, -1, 0], 1, true);
        }

        public EncoderState SwapState(EncoderState state, DirtyFlags flags = DirtyFlags.All, bool endRenderPass = true)
        {
            if (endRenderPass && CurrentEncoderType == EncoderType.Render)
            {
                EndCurrentPass(PassEndReason.SwapState);
            }

            return _encoderStateManager.SwapState(state, flags);
        }

        public PredrawState SavePredrawState()
        {
            return _encoderStateManager.SavePredrawState();
        }

        public void RestorePredrawState(PredrawState state)
        {
            _encoderStateManager.RestorePredrawState(state);
        }

        public void SetClearLoadAction(bool clear)
        {
            _encoderStateManager.SetClearLoadAction(clear);
        }

        private static readonly int _prepassRebindMode =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_PREPASS_REBIND"), out int prepassMode) ? prepassMode : 1;
        private long _prepassEncoderChanges;

        public MTLRenderCommandEncoder GetOrCreateRenderEncoder(bool forDraw = false)
        {
            // Mark all state as dirty to ensure it is set on the new encoder
            if (Cbs.Encoders.CurrentEncoderType != EncoderType.Render)
            {
                _encoderStateManager.SignalRenderDirty();
            }

            // A draw that samples one of its own attachments reads undefined data on
            // Metal. End the pass first so it reads finished writes instead. Decided from
            // bound state before the prepass runs and before any encoder is acquired: the
            // previous version decided during the prepass and had to end the pass from
            // inside encoder acquisition, which faulted the driver at drawIndexedPrimitives.
            // Only when the pass already carries draws - with none there is nothing to
            // order, and that also stops this looping, since the feedback does not go away.
            if (forDraw && (FeedbackProbe.Fix || FeedbackProbe.FixLive) &&
                Cbs.Encoders.CurrentEncoderType == EncoderType.Render &&
                DrawCount != _drawCountAtPassStart &&
                _encoderStateManager.SamplesOwnAttachment())
            {
                EndCurrentPass(PassEndReason.FragmentDependency);
                _encoderStateManager.SignalRenderDirty();
            }

            // Split where Vulkan is forced to split. A draw that samples storage written
            // as a colour attachment earlier in this command buffer is a read-after-write
            // that MoltenVK cannot express inside an encoder and therefore resolves by
            // ending it; this backend emits no barrier at all and trusts Metal's automatic
            // tracking. /tmp/ryujinx-metal-raw-split holds 1 to enable, re-read once a
            // frame. Decided from bound state before the prepass, never from inside
            // encoder acquisition - the constraint the feedback split had to learn.
            // Unconditionally for the blit that writes the presented surface. The
            // read-after-write split relies on SamplesEarlierWrite() noticing that this
            // draw samples something the same encoder wrote; if it misses this one, the
            // blit reads a texture still resident in tile memory with no barrier, which on
            // this hardware returns undefined - uniform - content. Everything else about
            // this draw has been measured correct, so whether the split fires for it is one
            // of the few things left that has not been.
            string blitLabel = _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel;

            // Per-draw trace: split the pass after every draw whose colour target is the
            // 1600x896 RG11B10 combine buffer, so the pass-luma trace records each draw's
            // cumulative output and the exact draw that lifts it from ~0.6 to ~147 is named.
            // RYUJINX_METAL_DRAW_TRACE=1 (with RYUJINX_METAL_PASS_TRACE=1).
            bool forceDrawTrace = false;
            if (forDraw && _drawTraceOn &&
                Cbs.Encoders.CurrentEncoderType == EncoderType.Render &&
                DrawCount != _drawCountAtPassStart)
            {
                // Any MRT slot: this buffer is attached at slot 1 for part of the chain.
                foreach (Texture dtTex in _encoderStateManager.RenderTargets)
                {
                    if (dtTex != null && dtTex.Width == 1600 && dtTex.Height == 896 &&
                        dtTex.MtlFormat == SharpMetal.Metal.MTLPixelFormat.RG11B10Float)
                    {
                        forceDrawTrace = true;
                        break;
                    }
                }
            }

            bool forceAllSplit = forDraw && _splitAll &&
                Cbs.Encoders.CurrentEncoderType == EncoderType.Render &&
                DrawCount != _drawCountAtPassStart;

            bool forceBlitSplit = forDraw && _rawSplit && _splitBlit &&
                Cbs.Encoders.CurrentEncoderType == EncoderType.Render &&
                DrawCount != _drawCountAtPassStart &&
                blitLabel != null &&
                blitLabel.StartsWith("480117", StringComparison.Ordinal);

            bool rawHazardSplit = false;
            Program fetchVariant = null;

            if (forDraw && _rawSplit &&
                Cbs.Encoders.CurrentEncoderType == EncoderType.Render &&
                DrawCount != _drawCountAtPassStart)
            {
                EncoderStateManager.RawHazard hazard = _encoderStateManager.SamplesEarlierWrite();
                _encoderStateManager.NoteStorageRawAtDraw();

                if (hazard == EncoderStateManager.RawHazard.Fetchable)
                {
                    // Serve the data-format self-reads from tile memory. Any skippable colour
                    // self-read on the same draw is handled as before: skipped when that is
                    // on, otherwise the draw still has to split for it. A variant that is
                    // still compiling falls back to the split for this draw, never a stall.
                    bool colourOk = !EncoderStateManager.FetchPlanHasSkippableSelf || _skipSelfSplit;
                    Program variant = _fbFetch && colourOk
                        ? _encoderStateManager.RenderProgram?.GetOrCreateFetchVariant(_device, EncoderStateManager.FetchPlan)
                        : null;

                    if (variant != null && EncoderStateManager.FetchPlanNeedsSnapshot && TileSnapshot.Mode == 2)
                    {
                        // Diagnostic: take the snapshot on the encoder as the real path would,
                        // then split anyway. Prices the dispatch by itself.
                        _encoderStateManager.DispatchSnapshotIfPlanned(Cbs.Encoders.RenderEncoder);
                        variant = null;
                    }

                    if (variant != null && variant.CheckProgramLink(false) == ProgramLinkStatus.Success)
                    {
                        fetchVariant = variant;
                        EncoderStateManager.NoteFetchServed();

                        if (EncoderStateManager.FetchPlanHasSkippableSelf)
                        {
                            _encoderStateManager.NoteSelfSkipAndTaint();
                        }
                    }
                    else
                    {
                        if (variant != null)
                        {
                            EncoderStateManager.NoteFetchPending();
                        }

                        hazard = EncoderStateManager.RawHazard.Hazard;
                    }
                }

                if (hazard == EncoderStateManager.RawHazard.SelfOnly && _skipSelfSplit)
                {
                    _encoderStateManager.NoteSelfSkipAndTaint();
                }
                else if (hazard != EncoderStateManager.RawHazard.Fetchable)
                {
                    rawHazardSplit = hazard != EncoderStateManager.RawHazard.None;
                }
            }

            if (forDraw)
            {
                _encoderStateManager.UseFetchVariant(fetchVariant);
            }

            if (forceDrawTrace || forceAllSplit || forceBlitSplit || rawHazardSplit)
            {
                // Signal on the encoder that did the writing, before it ends, and wait on
                // the one that will do the reading - the pairing MoltenVK produces for an
                // image barrier.
                if (_rawFence)
                {
                    if (_fence.NativePtr == IntPtr.Zero)
                    {
                        _fence = _device.NewFence;
                    }

                    Cbs.Encoders.RenderEncoder.UpdateFence(_fence, MTLRenderStages.RenderStageFragment);
                    _fenceWaitPending = true;
                }

                // Before ending the pass, remember which earlier-written texture this draw
                // samples; the split then leaves the command buffer between the writer's
                // pass and the composite's, and the pre-composite probe reads it right there.
                bool isPreLabel = UploadCorrelator.Enabled && blitLabel != null && _preProbeLabel.Length != 0 &&
                    blitLabel.StartsWith(_preProbeLabel, StringComparison.Ordinal);
                Texture preInput = isPreLabel
                    ? (_encoderStateManager.EarlierWrittenSampledTexture() ?? _encoderStateManager.FirstBoundLargeTexture())
                    : null;
                if (isPreLabel) { _preSplitHits++; if (preInput == null) { _preSplitNull++; } }

                EndCurrentPass(PassEndReason.FragmentDependency);
                _encoderStateManager.SignalRenderDirty();
                _rawSplits++;
                UploadCorrelator.NoteRawSplit();

                if (preInput != null)
                {
                    UploadCorrelator.PreCompositeProbe(Cbs, preInput);
                    // The probe opened a compute encoder; the render encoder for the composite
                    // is created afresh by the acquisition that follows.
                    _encoderStateManager.SignalRenderDirty();
                }
            }

            // Partial-render discriminator. AGX splits a render pass by itself when the
            // tiled vertex buffer fills - store all tiles, reload, continue - through
            // auxiliary load/store programs distinct from the ordinary end-of-pass path
            // (Rosenzweig, "The Impossible Bug": get those programs wrong and attachments
            // come back garbage). Only the scene pass here carries enough geometry to
            // overflow (~1524 draws; every other pass averages 13). Capping draws per
            // pass keeps any single pass below the overflow point, so the driver's
            // implicit partial-render store/reload is replaced by the explicit path this
            // backend already exercises 197 times a frame. If the flash rate collapses
            // under the cap, the fault lives in the partial-render path; the cap is then
            // also a shippable workaround. Same safe split pattern as the feedback fix:
            // decided from counters alone, before the prepass, never inside acquisition.
            if (forDraw && _passSplitDraws > 0 &&
                Cbs.Encoders.CurrentEncoderType == EncoderType.Render &&
                DrawCount - _drawCountAtPassStart >= (ulong)_passSplitDraws)
            {
                EndCurrentPass(PassEndReason.DrawBudget);
                _encoderStateManager.SignalRenderDirty();
            }

            if (forDraw)
            {
                int preparedCb = Cbs.CommandBufferIndex;
                EncoderType preparedType = Cbs.Encoders.CurrentEncoderType;
                var preparedGeneration = CommandBufferEncoder.RenderEncoderGeneration;
                _encoderStateManager.RenderResourcesPrepass();

                // Resolving a mirror may upload pending bytes and end the render
                // encoder. The initial dirty check above then describes the retired
                // encoder, while the binding lists only contain the sets that were
                // dirty before that upload. Prepare all sets for the new encoder.
                if (_prepassRebindMode != 0 &&
                    (Cbs.CommandBufferIndex != preparedCb ||
                     CommandBufferEncoder.RenderEncoderGeneration != preparedGeneration ||
                     (preparedType == EncoderType.Render && Cbs.Encoders.CurrentEncoderType != EncoderType.Render)))
                {
                    if (++_prepassEncoderChanges <= 24)
                    {
                        Logger.Warning?.PrintMsg(LogClass.Gpu,
                            $"prepass-encoder-change frame={_presentCount} draw={DrawCount} mode={_prepassRebindMode} " +
                            $"cb={preparedCb}->{Cbs.CommandBufferIndex} encoder={preparedType}->{Cbs.Encoders.CurrentEncoderType}");
                    }

                    if (_prepassRebindMode == 1)
                    {
                        _encoderStateManager.SignalRenderDirty();
                        _encoderStateManager.RenderResourcesPrepass();
                    }
                }
            }

            // Before the pass opens, while switching encoders is still legal, record what
            // the watched target holds. The blit encoder this may open is closed again by
            // EnsureRenderEncoder below.
            if (HdrPassProbe.Enabled && Cbs.Encoders.CurrentEncoderType != EncoderType.Render)
            {
                HdrPassProbe.SampleBeforePass(Cbs, _encoderStateManager.RenderTargets[0], _presentCount % 4);
            }

            // Card 3: bounce the scene texture through the blit engine before the pass that
            // samples it. Every host write channel is excluded on flat frames, so the white
            // is manufactured on the read; and at present time a CPU-side blit of this very
            // texture reads the scene correctly on frames the shader read white. If a blit
            // round trip clears the fault, whatever the sampler sees is a cached or
            // deferred view of the surface rather than its memory - and the bounce is then
            // also a real fix, unlike repeating the previous frame. Same safety contract as
            // the probe above: only with no pass open, so opening a blit encoder is legal.
            // forDraw only. Bindings are stale on the non-draw calls, and with the
            // once-a-frame latch below a single early non-draw call was enough to spend
            // the frame's bounce on whatever happened to still be bound - which is why
            // the first positive control wrote to a scene texture nothing went on to read
            // and left the picture untouched.
            if (forDraw && _bounceSceneInput > 0 && Cbs.Encoders.CurrentEncoderType != EncoderType.Render)
            {
                BounceSceneInput();
            }

            // Close a window whose watched pass has already ended. Done here, at the
            // top of encoder acquisition with no pass open, because it needs a flush and
            // flushing from inside EndCurrentPass would recurse.
            if (CaptureHunter.ClosePending && Cbs.Encoders.CurrentEncoderType != EncoderType.Render)
            {
                FlushCommandsImpl();
                CaptureHunter.CloseWindow();
            }

            // Same predicate as the bounce, which the 0x55 control proved lands on the
            // pass whose fetch comes back white. Flush first so the command buffer that
            // will carry that pass is created inside the capture window - Metal records
            // nothing for a command buffer that was not both created and committed
            // inside it, which is why the first attempt produced an empty trace.
            // Target the composite by what it writes, not by what it samples. The
            // sampled-texture predicate picked the G-buffer pass instead: it keys on a
            // width of 1000 or more, and dynamic resolution had the scene at 800x448, so
            // the real scene textures never matched. The composite is the pass that
            // writes the full resolution surface, and that stays 1920x1080 whatever
            // dynamic resolution does to the scene.
            // No aiming predicate any more. Every one of them opened the window too
            // late: the capture that finally showed the fault proved the 1920x1080
            // surface was already white when it arrived and that no encoder inside the
            // window ever wrote it, so the writer runs earlier in the frame. The
            // composite is the frame's second pass, so starting at the frame's first
            // draw and keeping a dozen drawing passes contains it by construction.
            if (forDraw && CaptureHunter.WantsStart &&
                Cbs.Encoders.CurrentEncoderType != EncoderType.Render)
            {
                // Start first, flush second. StartCapture only records command buffers
                // created after it, and flushing first meant the command buffer that
                // would carry the composite was born before the window opened - the
                // capture then held one command buffer with a blit encoder, no render
                // encoder and no draws. Opening the window first makes the flush's fresh
                // command buffer the one Metal records.
                if (CaptureHunter.WantsStart)
                {
                    CaptureHunter.OnSceneSamplingPassBegin(DrawCount);
                    FlushCommandsImpl();
                }
            }

            MTLRenderCommandEncoder renderCommandEncoder = Cbs.Encoders.EnsureRenderEncoder();

            if (_encoderFence && _encFencePending)
            {
                renderCommandEncoder.WaitForFence(_encFence, MTLRenderStages.RenderStageVertex);
                _encFencePending = false;
            }

            if (_fenceWaitPending)
            {
                renderCommandEncoder.WaitForFence(_fence, MTLRenderStages.RenderStageFragment);
                _fenceWaitPending = false;
                _fenceWaits++;
            }

            if (forDraw)
            {
                if (fetchVariant != null && EncoderStateManager.FetchPlanNeedsSnapshot && TileSnapshot.Mode != 3 &&
                    !_encoderStateManager.DispatchSnapshotIfPlanned(renderCommandEncoder))
                {
                    // No snapshot could be taken (the tile pipeline failed to build and the
                    // feature has switched itself off): the variant would read a twin nobody
                    // wrote. Draw with the base program instead - one stale read, not garbage.
                    fetchVariant = null;
                    _encoderStateManager.UseFetchVariant(null);
                }

                _encoderStateManager.RebindRenderState(renderCommandEncoder);

                // Only now is the pass genuinely writing its attachments. The split
                // decision for this draw is already made above, so this marks them for
                // the draws that follow - which is what makes a split worth anything:
                // the pass it opens starts with an empty write set instead of the same
                // attachments that caused the split.
                _encoderStateManager.MarkPassAttachmentsWritten();
            }

            return renderCommandEncoder;
        }

        public MTLBlitCommandEncoder GetOrCreateBlitEncoder()
        {
            return Cbs.Encoders.EnsureBlitEncoder();
        }

        public MTLComputeCommandEncoder GetOrCreateComputeEncoder(bool forDispatch = false)
        {

            // Mark all state as dirty to ensure it is set on the new encoder
            if (Cbs.Encoders.CurrentEncoderType != EncoderType.Compute)
            {
                _encoderStateManager.SignalComputeDirty();
            }

            if (forDispatch)
            {
                _encoderStateManager.ComputeResourcesPrepass();
            }

            MTLComputeCommandEncoder computeCommandEncoder = Cbs.Encoders.EnsureComputeEncoder();

            if (forDispatch)
            {
                _encoderStateManager.RebindComputeState(computeCommandEncoder);
            }

            return computeCommandEncoder;
        }

        private PassEndReason _pendingPassEndReason = PassEndReason.Unspecified;

        private readonly List<BufferHolder> _activeBufferMirrors = [];

        /// <summary>
        /// Remembers a buffer that has live mirrors, so they can all be dropped when the
        /// command buffer changes and their staging reservations go with it.
        /// </summary>
        public void RegisterActiveMirror(BufferHolder buffer)
        {
            _activeBufferMirrors.Add(buffer);
        }

        public void ClearActiveMirrors()
        {
            foreach (BufferHolder buffer in _activeBufferMirrors)
            {
                buffer.ClearMirrors();
            }

            _activeBufferMirrors.Clear();
        }

        /// <summary>
        /// Marks bound buffer sets dirty so the next draw resolves the range again and
        /// picks up, or stops using, a mirror. Metal rebuilds the argument buffers for a
        /// dirty set wholesale, so this does not need to name the individual binding.
        /// </summary>
        public void RebindBufferRange(Auto<DisposableBuffer> buffer, int offset, int size)
        {
            _encoderStateManager.SignalBufferRebind();
        }

        // FrameProbe needs the live command buffer to blit a patch out mid-frame. Both
        // are thin wrappers so the probe never reaches into pipeline internals itself.
        internal CommandBufferScoped CurrentCbs => Cbs;

        internal void EndCurrentPassForProbe()
        {
            EndCurrentPass(PassEndReason.Unspecified);
        }

        public void EndCurrentPass(PassEndReason reason = PassEndReason.Unspecified)
        {
            _renderer.Counters?.NotePassEnded();

            if (_encoderFence && CurrentEncoderType == EncoderType.Render)
            {
                if (_encFence.NativePtr == IntPtr.Zero)
                {
                    _encFence = _device.NewFence;
                }
                Cbs.Encoders.RenderEncoder.UpdateFence(_encFence, MTLRenderStages.RenderStageFragment);
                _encFencePending = true;
            }
            _endingPassFragmentStore = _passHasFragmentStore;
            _passHasFragmentStore = false;
            if (CurrentEncoderType == EncoderType.Render) { UploadCorrelator.Seq(reason == PassEndReason.RenderTargets ? "P-RT" : reason == PassEndReason.FragmentDependency ? "P-FD" : reason == PassEndReason.Flush ? "P-F" : "P-" + reason); }
            _pendingPassEndReason = reason;

            Cbs.Encoders.EndCurrentPass();

            _pendingPassEndReason = PassEndReason.Unspecified;

            CaptureHunter.OnPassEnd(DrawCount);

            // The in-stream witness: photograph the blit's input the moment its pass ends.
            UploadCorrelator.SampleInputAfterBlit(Cbs);

            // Under the narrowed scope the write set is per pass: what the pass just ending
            // wrote is now ordered by that boundary, so it no longer forces later splits.
            _encoderStateManager.ClearWrittenThisPass();
            UploadCorrelator.SampleStageAfterPass(Cbs);
            if (_passTraceOn)
            {
                UploadCorrelator.TracePassEnd(Cbs, _encoderStateManager.RenderTargets, $"{reason} last={UploadCorrelator.LastPassLastLabel} draws={UploadCorrelator.LastPassDraws}");
            }

            // Sample the watched target right after a pass on it ends. Sampling only on
            // encoder transitions left every chart entry between transitions holding the
            // previous frame's pixels - the fault Phase 1's first run exposed.
            if (HdrPassProbe.Enabled)
            {
                HdrPassProbe.SampleBoundary(Cbs, _presentCount % 4);
            }
        }

        public void OnRenderPassEnded(EncoderType startingType)
        {
            PassEndReason reason = startingType switch
            {
                EncoderType.Blit => PassEndReason.BlitEncoder,
                EncoderType.Compute => PassEndReason.ComputeEncoder,
                _ => _pendingPassEndReason,
            };

            _passEndReasons[(int)reason]++;

            if (_passLabelId == null)
            {
                _endingPassFragmentStore = false;
            }
            else
            {
                string programs = string.Join(",", _passPrograms.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}"));
                bool fragmentStore = _endingPassFragmentStore || _passHasFragmentStore;
                EncoderLabels.Record($"{_passLabelId}\t{reason}\t{DrawCount - _drawCountAtPassStart}\t{programs}\tfs={(fragmentStore ? 1 : 0)}\trp={(_passReadsPrev ? 1 : 0)}");
                _passLabelId = null;
                _endingPassFragmentStore = false;
            }

            if (PresentProbe.Enabled)
            {
                ulong drawsInPass = DrawCount - _drawCountAtPassStart;
                PresentProbe.RecordPass(_presentCount, reason, drawsInPass);
                HdrPassProbe.EndPass(drawsInPass, reason);
            }
        }

        // Diagnostic: how often a new pass uses an attachment set seen among the
        // last few passes. same = identical to the previous pass (mergeable with no
        // reordering at all); aba = identical to two passes back (mergeable only by
        // reordering across one intervening pass). Bounds what pass merging is worth
        // before anything is built.
        private readonly ulong[] _passSignatureRing = new ulong[4];
        private int _passSignatureCount;
        private int _passRevisitSame;
        private int _passRevisitAba;

        private ulong ComputePassSignature()
        {
            ulong hash = 17;

            Texture[] targets = _encoderStateManager.RenderTargets;

            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] != null)
                {
                    hash = hash * 31 + (ulong)targets[i].GetHandle().NativePtr + (ulong)i;
                }
            }

            if (_encoderStateManager.DepthStencil != null)
            {
                hash = hash * 31 + (ulong)_encoderStateManager.DepthStencil.GetHandle().NativePtr;
            }

            return hash;
        }

        public MTLRenderCommandEncoder CreateRenderCommandEncoder()
        {
            _renderPassCount++;
            _passIndexInFrame++;
            _drawCountAtPassStart = DrawCount;

            ulong signature = ComputePassSignature();

            if (_passSignatureCount > 0 && signature == _passSignatureRing[(_passSignatureCount - 1) & 3])
            {
                _passRevisitSame++;
            }
            else if (_passSignatureCount > 1 && signature == _passSignatureRing[(_passSignatureCount - 2) & 3])
            {
                _passRevisitAba++;
            }

            _passSignatureRing[_passSignatureCount & 3] = signature;
            _passSignatureCount++;

            MTLRenderCommandEncoder encoder = _encoderStateManager.CreateRenderCommandEncoder();

            if (EncoderLabels.Active)
            {
                _passLabelId = EncoderLabels.NextId('r');
                _passPrograms.Clear();
                (_labelPrevTargets, _labelCurTargets) = (_labelCurTargets, _labelPrevTargets);
                _labelCurTargets.Clear();

                foreach (Texture target in _encoderStateManager.RenderTargets)
                {
                    if (target != null)
                    {
                        _labelCurTargets.Add(target.CanonicalPtr);
                    }
                }

                if (_encoderStateManager.DepthStencil is Texture depthTarget)
                {
                    _labelCurTargets.Add(depthTarget.CanonicalPtr);
                }

                _passReadsPrev = false;
                EncoderLabels.Label(encoder, _passLabelId + EncoderLabels.DescribeTargets(_encoderStateManager.RenderTargets, _encoderStateManager.DepthStencil));
            }

            return encoder;
        }

        // EncoderLabels: the id of the render pass being encoded and its programs' draw counts.
        private string _passLabelId;
        private readonly Dictionary<string, int> _passPrograms = new();
        // EncoderLabels: the previous and the current render pass's attachments, and whether a
        // draw of the current pass sampled one of the previous pass's.
        private HashSet<IntPtr> _labelPrevTargets = new(), _labelCurTargets = new();
        private bool _passReadsPrev;

        public ulong PrepareCounterRenderPass(MTLRenderPassDescriptor descriptor)
        {
            return _renderer.Counters.PrepareRenderPass(descriptor, Cbs);
        }

        /// <summary>
        /// A guest counter report or reset used to end the render pass, because the new
        /// counter had a result buffer of its own and a pass binds exactly one. With every
        /// counter in a slot of the same buffer the encoder can just be pointed at the new
        /// slot: draws already encoded keep the offset they were issued with, so the split
        /// they used to need buys nothing. False when there is no open render pass, when the
        /// pass belongs to the coverage probe, or when the slot pool is exhausted - the
        /// caller ends the pass as before.
        /// </summary>
        public bool TrySwitchCounterInPass()
        {
            if (Cbs.Encoders.CurrentEncoderType != EncoderType.Render ||
                !_renderer.Counters.PassBoundSlotBuffer ||
                !_renderer.Counters.TryGetCurrentSlotOffset(Cbs, out ulong offset))
            {
                CounterManager.NotePassEndFallback();

                return false;
            }

            Cbs.Encoders.RenderEncoder.SetVisibilityResultMode(MTLVisibilityResultMode.Counting, offset);
            CounterManager.NoteInPassSwitch();

            return true;
        }

        public MTLComputeCommandEncoder CreateComputeCommandEncoder()
        {
            return _encoderStateManager.CreateComputeCommandEncoder();
        }

        public void FixupStoreActions(MTLRenderCommandEncoder encoder)
        {
            _encoderStateManager.FixupStoreActions(encoder, DrawCount - _drawCountAtPassStart);
            _encoderStateManager.NotePassStored();
        }

        // Diagnostic: RYUJINX_METAL_LOG_PRESENT=1 logs the source texture of every
        // presented frame with a wall clock stamp, so single-frame artefacts caught on
        // a screen recording can be correlated with dynamic resolution switches.
        private static readonly bool _logPresent =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_LOG_PRESENT") == "1";

        private (IntPtr Handle, int W, int H) _lastPresented;

        [ThreadStatic]
        private static IntPtr _frameAutoreleasePool;

        public void Present(CAMetalDrawable drawable, Texture src, Extents2D srcRegion, Extents2D dstRegion, bool isLinear, bool useFsrSharpener, float scalingFilterLevel)
        {
            UsageProbe.OnPresent();

            UploadCorrelator._inPresent = true;
            try
            {
            UploadCorrelator.WaitForLastComposite();
            // The GPU-layer modification trace shows the game's final sRGB target is BOUND as
            // a render target across the present boundary (RT-bind:Srgb at frame end,
            // RT-unbind:Srgb at next frame start). If the encoder writing it is still open
            // when present samples it, the sample sees pre-store contents. End every open
            // pass and commit before present so the write has landed. RYUJINX_METAL_PRESENT_BARRIER=1
            if (_presentBarrier)
            {
                EndCurrentPass(PassEndReason.Flush);
                FlushCommandsImpl();
            }

            // Drain everything autoreleased on the render thread since the last
            // present (encoders, command buffers, drawables and their driver-side
            // shadows), then open the next frame's pool. Without this the thread
            // has no pool at all and every autoreleased +1 is permanent.
            if (_frameAutoreleasePool != IntPtr.Zero)
            {
                ObjcOwnership.objc_autoreleasePoolPop(_frameAutoreleasePool);
            }

            _frameAutoreleasePool = ObjcOwnership.objc_autoreleasePoolPush();

            if (LiveResources.Enabled)
            {
                LiveResources.OnPresent(_renderer.DeviceAllocatedBytes);
            }

            if (_logPresent)
            {
                // Log only when the presented texture changes identity or size; a line
                // per frame perturbs timing enough to mask the very race under study.
                (IntPtr Handle, int W, int H) now = (src.GetHandle().NativePtr, src.Width, src.Height);

                if (now != _lastPresented)
                {
                    _lastPresented = now;

                    Logger.Warning?.PrintMsg(
                        LogClass.Gpu,
                        $"present-change {now.W}x{now.H} handle=0x{now.Handle:X} wall={DateTime.Now:HH:mm:ss.fff}");
                }
            }

            _lastPresentSource = src;
            _drawCountAtFrameStart = DrawCount;

            if (_stainSweep)
            {
                // 24 steps over a ~3000 draw frame, cycling so the whole range is covered
                // several times inside one trigger window.
                _stainAtDraw = (_presentCount % 24) * 128;
                PresentProbe.NoteStainIndex(_stainAtDraw);
            }

            _renderer.FrameCapture.CurrentCommandBuffer = CommandBuffer;
            _renderer.FrameCapture.OnPresentBegin();

            AppliedRenderState.RefreshToggle();
            EncoderStateManager.RefreshDedupToggle();
            FeedbackProbe.RefreshToggle();
            RefreshSkipDispatch();
            OpRing.OnPresent();
            FlashGuard.RefreshToggle();
            RefreshPassSplit();
            RefreshBounceScene();
            RefreshSkipDraws();
            RefreshSkipProgram();
            RefreshRawSplit();
            RefreshSkipSelfSplit();
            RefreshFbFetch();
            EncoderStateManager.RefreshRawDeclared();
            RefreshBarrierScope();
            PipelineState.RefreshAsyncPso();
            CounterManager.RefreshToggle();
            StarveFlush.Refresh();
            EncoderLabels.RefreshPerFrame((int)_presentCount);
            EncoderStateManager.RefreshSplitScope();
            RefreshRawFence();
            _passIndexInFrame = 0;
            RefreshBarrierToggle();
            EncoderStateManager.RefreshSamplingToggle();

            if (DrawRing.Enabled)
            {
                DrawRing.OnPresent();
            }

            if (ToneMapProbe.Enabled)
            {
                ToneMapProbe.OnPresent();
            }

            if (PresentProbe.Enabled)
            {
                PresentProbe.OnPresent(Cbs, src, DrawCount, DispatchCount);
            }

            // After the classification above has read this frame's slot, and before the
            // next frame encodes anything into its own.
            CoverageProbe.OnPresent(Cbs, _presentCount + 1);

            // TODO: Clean this up
            TextureCreateInfo textureInfo = new((int)drawable.Texture.Width, (int)drawable.Texture.Height, (int)drawable.Texture.Depth, (int)drawable.Texture.MipmapLevelCount, (int)drawable.Texture.SampleCount, 0, 0, 0, Format.B8G8R8A8Unorm, 0, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);
            Texture dst = new(_device, _renderer, this, textureInfo, drawable.Texture, 0, 0);

            // Positive control for the stain probe: staining here must turn the presented
            // frame green. If it does not, the stain never took effect and a "no green
            // ever came back" result from the post-present stain means nothing.
            if (_stainBeforePresent)
            {
                // A pass may still be open here; creating an encoder on top of one is an
                // immediate driver assertion. The post-present stain happens after
                // EndCurrentPass and so never hit this.
                EndCurrentPass(PassEndReason.Unspecified);

                MTLRenderPassDescriptor pre = new();
                MTLRenderPassColorAttachmentDescriptor pa = pre.ColorAttachments.Object(0);
                pa.Texture = src.GetIdentityHandle(Cbs);
                pa.LoadAction = MTLLoadAction.Clear;
                pa.StoreAction = MTLStoreAction.Store;
                pa.ClearColor = new MTLClearColor { red = 0.0, green = 1.0, blue = 0.0, alpha = 1.0 };

                MTLRenderCommandEncoder preEncoder = CommandBuffer.RenderCommandEncoder(pre);
                preEncoder.EndEncoding();
                pre.Dispose();
            }

            if (FlashGuard.Enabled)
            {
                // Fold this frame into the kept image unless it is flat, then present the
                // kept image. Both steps decide in the shader, for the frame they apply
                // to - the earlier variants either needed a CPU stall to know in time, or
                // fell back on a surface from the rotation that was often flat itself.
                Texture keep = FlashGuard.GetKeepTexture(_device, _renderer, this, src);

                if (keep != null)
                {
                    // Register both against this command buffer before the pass names them.
                    // The parameterless GetHandle does not register, and this texture is
                    // reachable only from a static field, so nothing else stops it being
                    // recycled while the frame that attaches it is still in flight.
                    keep.GetHandle(Cbs);
                    src.GetHandle(Cbs);

                    _renderer.HelperShader.UpdateKeepGood(Cbs, src, keep);
                    _renderer.HelperShader.BlitColor(Cbs, keep, dst, srcRegion, dstRegion, isLinear, true);
                }
                else
                {
                    _renderer.HelperShader.BlitColor(Cbs, src, dst, srcRegion, dstRegion, isLinear, true);
                }
            }
            else if (useFsrSharpener)
            {
                // Three identities at the moment of present, for the correlator: what
                // the present shader READS (src), what it WRITES (dst, the drawable), and
                // what the frame's last full-resolution pass rendered into.
                UploadCorrelator.NotePresentTriple(src.CanonicalPtr, dst.CanonicalPtr, src.Width, src.Height, src.Serial);
                _renderer.HelperShader.PresentColor(Cbs, src, dst, srcRegion, dstRegion, scalingFilterLevel / 100f, true);
            }
            else
            {
                _renderer.HelperShader.BlitColor(Cbs, src, dst, srcRegion, dstRegion, isLinear, true);
            }

            _guardPreviousSource = src;

            EndCurrentPass(PassEndReason.Present);

            // Attribution-free probe: after presenting, stain the source a colour nothing
            // in the scene produces. If it comes back on a later frame still stained, the
            // frame was presented without anything having drawn into it - which separates
            // "something wrote white" from "nothing wrote at all". Bookkeeping-based
            // ownership tracking cannot answer this; its self-check says it is unreliable.
            if (_stainPresentSource)
            {
                MTLRenderPassDescriptor stain = new();
                MTLRenderPassColorAttachmentDescriptor a = stain.ColorAttachments.Object(0);
                a.Texture = src.GetIdentityHandle(Cbs);
                a.LoadAction = MTLLoadAction.Clear;
                a.StoreAction = MTLStoreAction.Store;
                a.ClearColor = new MTLClearColor { red = 0.0, green = 1.0, blue = 0.0, alpha = 1.0 };

                MTLRenderCommandEncoder stainEncoder = CommandBuffer.RenderCommandEncoder(stain);
                stainEncoder.EndEncoding();
                stain.Dispose();
            }

            // Sample the present source and every target this frame passed through.
            // Has to sit here: no pass is open, the present blit is already encoded, and
            // the command buffer has not been committed yet. Read back several frames
            // later, so nothing is ever waited on.
            if (FrameProbe.Enabled)
            {
                FrameProbe.Capture(Cbs, src);
            }

            // Same contract as FrameProbe.Capture: encode-only sampling here, classified
            // several presents later, never waited on, never logged per frame.
            UploadCorrelator.NotePresented(src);
            UploadCorrelator.OnPresent(Cbs, src);

            CaptureHunter.NotePresentSource(src);
            CaptureHunter.SamplePresentSource(Cbs, src);

            // The per-present view of the drawable's texture was never released -
            // one native texture view leaked per frame. The Auto defers the native
            // release until the command buffer using it completes, so this is safe
            // before the flush below.
            dst.Release();

            Cbs.CommandBuffer.PresentDrawable(drawable);

            // Balance the ownership taken at nextDrawable time (Window.Present).
            ObjcOwnership.Release(drawable.NativePtr);

            // The capture attempt has to be judged against the frame it captured, so
            // this is the one place a wait is taken - and it is safe on evidence: the
            // CPU-sampling FlashGuard held a sync here every frame and still measured
            // 35% flat, so the sync does not close the race. Only while hunting.
            FenceHolder decideFence = null;

            if (CaptureHunter.WantsSyncThisFrame)
            {
                decideFence = Cbs.GetFence();
                decideFence.Get();
            }

            FlushCommandsImpl();

            if (decideFence != null)
            {
                decideFence.Wait();
                decideFence.Put();
            }

            CaptureHunter.NoteFrameDraws(DrawCount);
            CaptureHunter.Decide();

            // A scoped capture has to be told where a frame begins and ends, and nothing
            // was telling it: without this the scope never opened and the trace came out
            // empty, while the queue-scope alternative recorded every command buffer and
            // produced multi-GB bundles the tools aborted while finalising. Bracketing one
            // present-to-present interval gives Xcode exactly one frame.
            _renderer.FrameCapture.EndScope();

            _renderer.AutoFlush.Present();
            _renderer.FrameCapture.ProcessPresent();
            _renderer.FrameCapture.BeginScope();

            _presentCount++;
            CrashRing.Present((int)_presentCount);

            // Synchronous pipeline-state compiles this frame, render and compute together:
            // for a stall what matters is the wall clock the render thread spent inside
            // the driver, whichever kind of pipeline it was building.
            long psoCreatedTotal = PipelineState.PsoRenderCreated + PipelineState.PsoComputeCreated;
            long psoTicksTotal = PipelineState.PsoRenderTicks + PipelineState.PsoComputeTicks;
            long framePsoCreated = psoCreatedTotal - _lastPresentPsoCreated;
            long framePsoTicks = psoTicksTotal - _lastPresentPsoTicks;
            _lastPresentPsoCreated = psoCreatedTotal;
            _lastPresentPsoTicks = psoTicksTotal;

            if (framePsoCreated != 0)
            {
                _statsPsoFramesWithCreation++;

                if (framePsoTicks > _statsPsoWorstFrameTicks)
                {
                    _statsPsoWorstFrameTicks = framePsoTicks;
                    _statsPsoWorstFrameCount = framePsoCreated;
                }
            }

            PipelineState.TakeFrameCounters(out long framePsoSkips, out long framePsoWaitTicks);

            StallLedger.NotePresent(_presentCount);

            if (_frameLine)
            {
                // One line per presented frame: the map flicker lasts about five frames
                // and the 120-frame stats block cannot see it. Tells "the draws were
                // skipped" from "the draws ran and read nothing".
                ulong fDraws = DrawCount - _lastFrameLineDraws;
                ulong fPasses = _renderPassCount - _lastFrameLineRenderPasses;
                _lastFrameLineDraws = DrawCount;
                _lastFrameLineRenderPasses = _renderPassCount;
                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"frameline f={_presentCount} t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} " +
                    $"passes={fPasses} draws={fDraws} skipped={_skippedDraws} rawsplits={_rawSplits} " +
                    $"pso={framePsoCreated}/{framePsoTicks * 1000.0 / Stopwatch.Frequency:F1}ms " +
                    $"psoSkip={framePsoSkips} psoWait={framePsoWaitTicks * 1000.0 / Stopwatch.Frequency:F1}ms");
            }

            if (_presentCount % SyncStatsLogFrameInterval == 0)
            {
                long syncWaitTicks = _renderer.SyncManager.GetAndResetWaitStats(
                    out int syncWaitCount,
                    out int forcedSyncFlushCount,
                    out int proactiveSyncFlushCount,
                    out int coalescedSyncSignalCount,
                    out string waitBreakdown,
                    out string createBreakdown,
                    out string waitDurations,
                    out string threadBreakdown);

                int autoFlushDrawCount = _autoFlushDrawCount;
                int autoFlushAttachmentCount = _autoFlushAttachmentCount;
                _autoFlushDrawCount = 0;
                _autoFlushAttachmentCount = 0;

                long nowTicks = Stopwatch.GetTimestamp();
                double windowMs = (nowTicks - _lastStatsTicks) * 1000.0 / Stopwatch.Frequency;
                _lastStatsTicks = nowTicks;

                GpuTimeline.Snapshot gpu = GpuTimeline.Enabled ? GpuTimeline.TakeAndReset() : default;

                // The window's pipeline-state compiles, consumed here whether or not the
                // block prints, so each block's numbers are its own window's.
                long psoRenderCreated = PipelineState.PsoRenderCreated - _lastStatsPsoRenderCreated;
                long psoRenderTicks = PipelineState.PsoRenderTicks - _lastStatsPsoRenderTicks;
                long psoComputeCreated = PipelineState.PsoComputeCreated - _lastStatsPsoComputeCreated;
                long psoComputeTicks = PipelineState.PsoComputeTicks - _lastStatsPsoComputeTicks;
                long psoRenderMaxTicks = PipelineState.PsoRenderMaxTicks;
                long psoFramesWithCreation = _statsPsoFramesWithCreation;
                long psoWorstFrameTicks = _statsPsoWorstFrameTicks;
                long psoWorstFrameCount = _statsPsoWorstFrameCount;
                long psoRenderQueued = PipelineState.PsoRenderQueued - _lastStatsPsoRenderQueued;
                long psoPendingSkips = PipelineState.PsoRenderPendingSkips - _lastStatsPsoPendingSkips;
                long psoWaited = PipelineState.PsoRenderWaited - _lastStatsPsoWaited;
                long psoWaitTicks = PipelineState.PsoRenderWaitTicks - _lastStatsPsoWaitTicks;
                _lastStatsPsoWaited = PipelineState.PsoRenderWaited;
                _lastStatsPsoWaitTicks = PipelineState.PsoRenderWaitTicks;
                long psoSyncFallbacks = PipelineState.PsoRenderSyncFallbacks - _lastStatsPsoSyncFallbacks;
                _lastStatsPsoSyncFallbacks = PipelineState.PsoRenderSyncFallbacks;
                _lastStatsPsoRenderQueued = PipelineState.PsoRenderQueued;
                _lastStatsPsoPendingSkips = PipelineState.PsoRenderPendingSkips;
                _lastStatsPsoRenderCreated = PipelineState.PsoRenderCreated;
                _lastStatsPsoRenderTicks = PipelineState.PsoRenderTicks;
                _lastStatsPsoComputeCreated = PipelineState.PsoComputeCreated;
                _lastStatsPsoComputeTicks = PipelineState.PsoComputeTicks;
                PipelineState.PsoRenderMaxTicks = 0;
                _statsPsoFramesWithCreation = 0;
                _statsPsoWorstFrameTicks = 0;
                _statsPsoWorstFrameCount = 0;

                if (syncWaitCount != 0 || forcedSyncFlushCount != 0 || proactiveSyncFlushCount != 0 || coalescedSyncSignalCount != 0 ||
                    autoFlushDrawCount != 0 || autoFlushAttachmentCount != 0 || gpu.Count != 0 ||
                    psoRenderCreated != 0 || psoComputeCreated != 0)
                {
                    double waitMs = syncWaitTicks * 1000.0 / Stopwatch.Frequency;
                    string sourceText = string.IsNullOrEmpty(waitBreakdown) ? string.Empty : $" wait: {waitBreakdown}.";
                    string createText = string.IsNullOrEmpty(createBreakdown) ? string.Empty : $" created: {createBreakdown}.";
                    string durationText = string.IsNullOrEmpty(waitDurations) ? string.Empty : $" durations: {waitDurations}.";
                    string threadText = string.IsNullOrEmpty(threadBreakdown) ? string.Empty : $" threads: {threadBreakdown}.";

                    ulong draws = DrawCount - _lastStatsDrawCount;
                    ulong passes = _renderPassCount - _lastStatsRenderPassCount;
                    _lastStatsDrawCount = DrawCount;
                    _lastStatsRenderPassCount = _renderPassCount;

                    string passText =
                        $" skipped draws: {_skippedDraws}, by program: {_skippedByProgram}, raw splits: {_rawSplits}, fence waits: {_fenceWaits}." +
                        $" pre-probe[{_preProbeLabel}]: draws {_preDraws}, auto-flushed-before {_preDrawsFlushedBefore}, split-block hits {_preSplitHits}, null input {_preSplitNull}, pass-start probes {_prePassProbes}, no-input {_prePassProbesNoInput}. mask-unwritten: {(PipelineState.MaskUnwrittenOutputs ? "on" : "off")}, pipelines masked {PipelineState.MaskedUnwrittenCount}." +
                        $" per frame: {passes / (ulong)SyncStatsLogFrameInterval} passes, " +
                        $"{draws / (ulong)SyncStatsLogFrameInterval} draws " +
                        $"({(passes != 0 ? (double)draws / passes : 0):F1} draws/pass).";

                    // Every stats block reports the configuration it was produced under.
                    // Two builds that differ only in an uncommitted change log the same
                    // version string, so a hot-switched A/B could not otherwise be told
                    // from a build that never had the change in it.
                    string configText =
                        $" config: splitScope={(EncoderStateManager.SplitScopePass ? "pass" : "cb")}, " +
                        $"rawSplit={_rawSplit}, barrier={(_barrierHazardOnly ? "hazard" : _barrierDeferred ? "deferred" : "all")}, asyncPso={PipelineState.AsyncPso}, " +
                        $"markOnDraw={EncoderStateManager.MarkOnDrawActive}, " +
                        $"declaredOnly={EncoderStateManager.RawDeclaredOnly}, skipSelf={_skipSelfSplit}, fbFetch={_fbFetch}, tileSnapshot={TileSnapshot.Enabled}/{TileSnapshot.Mode}, " +
                        $"leanTargets={Texture.LeanTargetsEnabled}/{Texture.LeanCreated} made/{Texture.LeanViolations} violations.";

                    // Occupancy, and the evidence that the occupancy is readable. The span
                    // is the GPU clock's own measure of the same window the wall clock just
                    // measured; if the two disagree the busy percentage below is meaningless,
                    // and saying so in the line is cheaper than discovering it later.
                    string gpuText = string.Empty;

                    if (gpu.Count == 0 && gpu.Rejected != 0)
                    {
                        // The readings exist and are not usable. Say so: the previous
                        // version of this line printed nothing at all in that case, which
                        // reads exactly like a build that never had the change in it.
                        gpuText = $" host gpu: NO USABLE READINGS, {gpu.Rejected} rejected.";
                    }
                    else if (gpu.Count != 0 && windowMs > 0.0)
                    {
                        double busyMs = gpu.BusySeconds * 1000.0;
                        double spanMs = gpu.SpanSeconds * 1000.0;

                        gpuText =
                            $" host gpu: busy {busyMs:F0}ms of {windowMs:F0}ms wall ({busyMs * 100.0 / windowMs:F1}%)," +
                            $" idle {(spanMs - busyMs):F0}ms, {gpu.Count} buffers," +
                            $" overlap {(gpu.BusySeconds > 0.0 ? gpu.SumSeconds / gpu.BusySeconds : 0.0):F2}x," +
                            $" gaps>1ms {gpu.GapsOverThreshold} (longest {gpu.LongestGapSeconds * 1000.0:F1}ms)," +
                            $" clockcheck span/wall {(windowMs > 0.0 ? spanMs / windowMs : 0.0):F3}" +
                            $"{(gpu.Dropped != 0 ? $", DROPPED {gpu.Dropped}" : string.Empty)}" +
                            $"{(gpu.Rejected != 0 ? $", REJECTED {gpu.Rejected}" : string.Empty)}.";
                    }

                    gpuText += GapAnatomy.TakeAndFormat(SyncStatsLogFrameInterval);
                    gpuText += StarveFlush.TakeStats(SyncStatsLogFrameInterval);

                    string splitClassText = EncoderStateManager.TakeSplitClasses(SyncStatsLogFrameInterval) ?? string.Empty;
                    string storeText = StoreLiveness.Take(SyncStatsLogFrameInterval) ?? string.Empty;

                    string reasonText = " pass ends: " + string.Join(", ", Enum.GetValues<PassEndReason>()
                        .Where(r => _passEndReasons[(int)r] != 0)
                        .OrderByDescending(r => _passEndReasons[(int)r])
                        .Select(r => $"{r}={_passEndReasons[(int)r] / SyncStatsLogFrameInterval}"));

                    Array.Clear(_passEndReasons);

                    string revisitText =
                        $" pass revisits: same={_passRevisitSame / SyncStatsLogFrameInterval}, aba={_passRevisitAba / SyncStatsLogFrameInterval} per frame.";
                    _passRevisitSame = 0;
                    _passRevisitAba = 0;

                    string uploadGates = BufferHolder.TakeUploadGates();
                    string gateText = uploadGates == null ? string.Empty : $" upload gates: {uploadGates}.";
                    string blitCallers = CommandBufferEncoder.TakeBlitCallers();
                    string blitText = blitCallers == null ? string.Empty : $" blit callers: {blitCallers}.";
                    string flushCallers = TakeFlushCallers(SyncStatsLogFrameInterval);
                    blitText += flushCallers == null ? string.Empty : $" flush callers mid-pass per frame: {flushCallers}.";
                    string strictMidPass = SyncManager.TakeStrictMidPass(SyncStatsLogFrameInterval);
                    blitText += strictMidPass == null ? string.Empty : $" strict syncs mid-pass per frame: {strictMidPass}.";

                    double tickMs = 1000.0 / Stopwatch.Frequency;
                    string psoText =
                        $" pso compiles: {psoRenderCreated} render ({psoRenderTicks * tickMs:F1}ms, longest {psoRenderMaxTicks * tickMs:F1}ms), " +
                        $"{psoComputeCreated} compute ({psoComputeTicks * tickMs:F1}ms); " +
                        $"async {(PipelineState.AsyncPso ? "on" : "off")}: {psoRenderQueued} queued, {psoWaited} waited ({psoWaitTicks * tickMs:F1}ms), {psoPendingSkips} draws skipped pending, {psoSyncFallbacks} built in place (fullscreen/burst); " +
                        $"{psoFramesWithCreation} of {SyncStatsLogFrameInterval} frames compiled, " +
                        $"worst frame {psoWorstFrameCount} in {psoWorstFrameTicks * tickMs:F1}ms." +
                        (PipelinePrewarmer.Enabled || PipelinePrewarmer.DeferCompile ? PipelinePrewarmer.FormatStats() : string.Empty) + CounterManager.TakeStats() +
                        (Ryujinx.Common.SyncMemDiag.SnapshotAndResetExtraSync() is long extraSyncs && extraSyncs != 0 ? $" extra syncs: {extraSyncs}." : string.Empty);

                    Logger.Info?.PrintMsg(
                        LogClass.Gpu,
                        $"Metal sync stats over last {SyncStatsLogFrameInterval} frames (device allocated {_renderer.DeviceAllocatedBytes >> 20} MiB, managed heap {GC.GetTotalMemory(false) >> 20} MiB): {waitMs:F2}ms in {syncWaitCount} waits, " +
                        $"{forcedSyncFlushCount} forced flushes, {proactiveSyncFlushCount} proactive flushes, " +
                        $"{coalescedSyncSignalCount} coalesced signals, " +
                        $"{autoFlushDrawCount} draw auto-flushes, {autoFlushAttachmentCount} attachment auto-flushes " +
                        $"(fast flush: {_renderer.AutoFlush.FastFlushMode}).{sourceText}{createText}{durationText}{threadText}{passText}{gpuText}{splitClassText}{reasonText}{revisitText}{gateText}{blitText}{storeText}{psoText}{configText}");
                }
            }

            // Cleanup
            dst.Dispose();
                    }
            finally
            {
                UploadCorrelator._inPresent = false;
            }
        }

        public CommandBufferScoped GetPreloadCommandBuffer()
        {
            PreloadCbs ??= _renderer.CommandBufferPool.Rent();

            return PreloadCbs.Value;
        }

        public void FlushCommandsIfWeightExceeding(IAuto disposedResource, ulong byteWeight)
        {
            bool usedByCurrentCb = disposedResource.HasCommandBufferDependency(Cbs);

            if (PreloadCbs != null && !usedByCurrentCb)
            {
                usedByCurrentCb = disposedResource.HasCommandBufferDependency(PreloadCbs.Value);
            }

            if (usedByCurrentCb)
            {
                // Since we can only free memory after the command buffer that uses a given resource was executed,
                // keeping the command buffer might cause a high amount of memory to be in use.
                // To prevent that, we force submit command buffers if the memory usage by resources
                // in use by the current command buffer is above a given limit, and those resources were disposed.
                _byteWeight += byteWeight;
                _disposedResourceCount++;

                // A large number of tiny Metal resources has significant driver-side
                // overhead even when their combined payload is small. Bound both the
                // byte weight and object count so they cannot accumulate indefinitely.
                if (_byteWeight >= MinByteWeightForFlush || _disposedResourceCount >= MaxDisposedResourceCountForFlush)
                {
                    FlushCommandsImpl();
                }
            }
        }

        private static int _crossThreadFlushLogs;

        // RYUJINX_METAL_FLUSH_TRACE=1: which caller committed while a render pass was live, i.e.
        // which flushes cost a pass split. Two frames of context: the caller and its caller.
        private static readonly bool _flushTrace = Environment.GetEnvironmentVariable("RYUJINX_METAL_FLUSH_TRACE") == "1";
        private static readonly Dictionary<string, int> _flushCallers = new();

        private static string TakeFlushCallers(int frames)
        {
            lock (_flushCallers)
            {
                if (_flushCallers.Count == 0)
                {
                    return null;
                }

                string text = string.Join(", ", _flushCallers
                    .OrderByDescending(entry => entry.Value)
                    .Take(8)
                    .Select(entry => $"{entry.Key}={entry.Value / (double)frames:F1}"));

                _flushCallers.Clear();

                return text;
            }
        }

        private void NoteFlushCaller()
        {
            System.Diagnostics.StackTrace trace = new(2, false);
            string Name(int i)
            {
                System.Reflection.MethodBase method = i < trace.FrameCount ? trace.GetFrame(i)?.GetMethod() : null;

                return method == null ? "?" : $"{method.DeclaringType?.Name}.{method.Name}";
            }

            string key = $"{Name(0)}<{Name(1)}";

            lock (_flushCallers)
            {
                _flushCallers[key] = _flushCallers.GetValueOrDefault(key) + 1;
            }
        }

        public void FlushCommandsImpl()
        {
            if (_flushTrace && Cbs.Encoders.CurrentEncoderType == EncoderType.Render)
            {
                NoteFlushCaller();
            }

            // The pipeline's encoder state machine is single-threaded by construction:
            // ThreadedRenderer makes the thread that called RunLoop the backend thread
            // (GUI.RenderThread here), and draws, presents and interrupts all execute on
            // it. Every one of 2026-08-30's five AGX/IOGPU lifecycle aborts - double
            // commit, double endEncoding, commit with a live encoder, encoder creation
            // over freed state - faulted on a DIFFERENT thread committing this pipeline's
            // command buffers directly. Those are bypasses: sync creation, counter and
            // readback paths that call back into the backend from the GPU emulation or a
            // guest thread. Marshal them onto the backend thread through the interrupt
            // mechanism the sync wait path already uses, and name the caller so the
            // bypass list shrinks by evidence rather than by hope.
            if (!_renderer.CommandBufferPool.OwnedByCurrentThread)
            {
                if (_crossThreadFlushLogs++ < 20)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu,
                        $"cross-thread flush marshalled from '{System.Threading.Thread.CurrentThread.Name}'\n{Environment.StackTrace}");
                }

                if (_renderer.InterruptAction != null)
                {
                    _renderer.InterruptAction(FlushCommandsImpl);

                    return;
                }

                // No interrupt mechanism (startup, or threading off): nothing to marshal
                // onto; the direct call preserves the old behaviour and the log above
                // still names the path.
            }

            _renderer.AutoFlush.RegisterFlush(DrawCount);
            EndCurrentPass(PassEndReason.Flush);

            _byteWeight = 0;
            _disposedResourceCount = 0;

            if (PreloadCbs != null)
            {
                PreloadCbs.Value.Dispose();
                PreloadCbs = null;
            }

            CommandBuffer = (Cbs = _renderer.CommandBufferPool.ReturnAndRent(Cbs)).CommandBuffer;

            // Scoped to the command buffer. Widening it to the whole frame was tried,
            // on the reasoning that auto-flush rotates the command buffer ~245 times a
            // frame and clearing here hides most read-after-write pairs from the check.
            // It made every draw its own pass - 197 passes a frame became 2509, sync
            // waits went to 10.8 seconds per 120 frames - and the flat rate did not move
            // at all: 25.3% against 24.3% for this cheap version. The extra pairs it
            // caught are not the ones that matter.
            _encoderStateManager.ClearWrittenThisCb();

            // Mirrors live in staging reservations owned by the command buffer that is
            // being retired, so none of them survive the swap.
            ClearActiveMirrors();
            _renderer.RegisterFlush();
        }

        public void DirtyTextures()
        {
            _encoderStateManager.DirtyTextures();
        }

        public void DirtyImages()
        {
            _encoderStateManager.DirtyImages();
        }

        public void Blit(
            Texture src,
            Texture dst,
            Extents2D srcRegion,
            Extents2D dstRegion,
            bool isDepthOrStencil,
            bool linearFilter)
        {
            if (isDepthOrStencil)
            {
                _renderer.HelperShader.BlitDepthStencil(Cbs, src, dst, srcRegion, dstRegion);
            }
            else
            {
                _renderer.HelperShader.BlitColor(Cbs, src, dst, srcRegion, dstRegion, linearFilter);
            }
        }

        // RYUJINX_METAL_BARRIER_ENDS_PASS: 1 = a guest barrier inside a render pass ends
        // the pass; 2 = only when the pass has drawn with a fragment shader that stores to
        // a storage buffer (the lens-flare occlusion counter is fragment atomics read by a
        // later vertex shader - unordered within one pass on a tile-based GPU, and the
        // render-encoder memory barrier cannot name the fragment stage).
        private static readonly int _barrierEndsPass =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_BARRIER_ENDS_PASS"), out int bep) ? bep : 0;
        private bool _passHasFragmentStore;
        // EncoderLabels: whether the pass being ended stored from its fragment stage (cleared above before the end lands).
        private bool _endingPassFragmentStore;
        private long _barrierPassEnds;

        public void Barrier()
        {
            UploadCorrelator.Seq(CurrentEncoderType == EncoderType.Render ? "B" : "b");
            switch (CurrentEncoderType)
            {
                case EncoderType.Render:
                    {
                        if (_barrierEndsPass == 1 || (_barrierEndsPass == 2 && _passHasFragmentStore))
                        {
                            _barrierPassEnds++;
                            EndCurrentPass(PassEndReason.Barrier);
                            break;
                        }
                        // afterStages may only name stages that can be waited on, which
                        // on Apple GPUs excludes fragment and tile: passing them makes
                        // the barrier illegal and its behaviour undefined, so writes it
                        // was meant to order can be read before they land.
                        // MTLBarrierScopeRenderTargets is not accepted by a render
                        // encoder barrier on this device either, and including it makes
                        // the whole barrier illegal.
                        MTLBarrierScope scope = MTLBarrierScope.Buffers | MTLBarrierScope.Textures;
                        Encoders.RenderEncoder.MemoryBarrier(
                            scope,
                            MTLRenderStages.RenderStageVertex,
                            MTLRenderStages.RenderStageVertex | MTLRenderStages.RenderStageFragment);
                        break;
                    }
                case EncoderType.Compute:
                    {
                        // RenderTargets scope is only valid on render encoders; passing
                        // it to a compute encoder is rejected by the validation layer
                        // ("scope has an invalid value for compute", 5500+ hits per
                        // minute in TOTK's Depths) and leaves the barrier behaviour
                        // undefined - compute results could be read before the writes
                        // completed, freezing compute-driven effects.
                        MTLBarrierScope scope = MTLBarrierScope.Buffers | MTLBarrierScope.Textures;
                        Encoders.ComputeEncoder.MemoryBarrier(scope);
                        break;
                    }
            }
        }

        public void ClearBuffer(BufferHandle destination, int offset, int size, uint value)
        {
            MTLBlitCommandEncoder blitCommandEncoder = GetOrCreateBlitEncoder();

            MTLBuffer mtlBuffer = _renderer.BufferManager.GetBuffer(destination, offset, size, true).Get(Cbs, offset, size, true).Value;
            UploadCorrelator.NoteWriteTo(mtlBuffer.NativePtr, offset, size, "FILL");

            // Might need a closer look, range's count, lower, and upper bound
            // must be a multiple of 4
            blitCommandEncoder.FillBuffer(mtlBuffer,
                new NSRange
                {
                    location = (ulong)offset,
                    length = (ulong)size
                },
                (byte)value);
        }

        public void ClearRenderTargetColor(int index, int layer, int layerCount, uint componentMask, ColorF color)
        {
            float[] colors = [color.Red, color.Green, color.Blue, color.Alpha];

            // Canary v2. The first canary sat on pass-descriptor clear colours and caught
            // nothing - guest clears do not go through it; they are DRAW-based, through
            // HelperShader, and this is their colour. If white frames turn magenta, the
            // white is a guest clear the blit read before its overwriter ran.
            Texture dst = _encoderStateManager.RenderTargets[index];

            // TODO: Remove workaround for Wonder which has an invalid texture due to unsupported format
            if (dst == null)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, "Attempted to clear invalid render target!");
                return;
            }

            // A draw-based clear: it opens a pass on the target and writes the whole
            // surface without going through the ordinary draw path, which is exactly the
            // p=1 d=0 entry the census reports for the texture the composite samples. If
            // that clear is white, a flat frame is the clear showing through.
            HdrPassProbe.NoteSceneClear(dst, color);

            // Mode 1 overrides every guest clear with magenta - it proved the path
            // exists (810 magenta frames reached presentation) and broke everything else.
            // Mode 2 only takes a census: every distinct (colour, size) pair once.
            if (_canaryClear2 == 2)
            {
                string key = $"{dst.Width}x{dst.Height} ({color.Red:F3} {color.Green:F3} {color.Blue:F3} {color.Alpha:F3})";

                lock (_clearCensus)
                {
                    if (_clearCensus.Add(key) && _clearCensus.Count <= 48)
                    {
                        Logger.Info?.PrintMsg(LogClass.Gpu, $"CLEAR {key}");
                    }
                }
            }
            else if (_canaryClear2 == 1)
            {
                colors = [1f, 0f, 1f, 1f];
            }
            else if (_canaryClear2 == 3 && color.Red >= 0.9f && color.Green >= 0.9f && color.Blue >= 0.9f)
            {
                // Only the white clears become magenta. If the white frames turn magenta,
                // the screen's white IS one of these clears reaching presentation.
                colors = [1f, 0f, 1f, 1f];
            }

            _renderer.HelperShader.ClearColor(index, colors, componentMask, dst.Width, dst.Height, dst.Info.Format);
        }

        public void ClearRenderTargetDepthStencil(int layer, int layerCount, float depthValue, bool depthMask, int stencilValue, int stencilMask)
        {
            Texture depthStencil = _encoderStateManager.DepthStencil;

            if (depthStencil == null)
            {
                return;
            }

            _renderer.HelperShader.ClearDepthStencil(depthValue, depthMask, stencilValue, stencilMask, depthStencil.Width, depthStencil.Height);
        }

        public void CommandBufferBarrier()
        {
            Barrier();
        }

        public void CopyBuffer(BufferHandle src, BufferHandle dst, int srcOffset, int dstOffset, int size)
        {
            Auto<DisposableBuffer> srcBuffer = _renderer.BufferManager.GetBuffer(src, srcOffset, size, false);
            Auto<DisposableBuffer> dstBuffer = _renderer.BufferManager.GetBuffer(dst, dstOffset, size, true);
            UploadCorrelator.NoteWriteTo(dstBuffer.GetUnsafe().Value.NativePtr, dstOffset, size, "COPY");

            BufferHolder.Copy(Cbs, srcBuffer, dstBuffer, srcOffset, dstOffset, size);
        }

        public void PushDebugGroup(string name)
        {
            MTLCommandEncoder? encoder = Encoders.CurrentEncoder;
            NSString debugGroupName = StringHelper.NSString(name);

            if (encoder == null)
            {
                return;
            }

            switch (Encoders.CurrentEncoderType)
            {
                case EncoderType.Render:
                    encoder.Value.PushDebugGroup(debugGroupName);
                    break;
                case EncoderType.Blit:
                    encoder.Value.PushDebugGroup(debugGroupName);
                    break;
                case EncoderType.Compute:
                    encoder.Value.PushDebugGroup(debugGroupName);
                    break;
            }
        }

        public void PopDebugGroup()
        {
            MTLCommandEncoder? encoder = Encoders.CurrentEncoder;

            if (encoder == null)
            {
                return;
            }

            switch (Encoders.CurrentEncoderType)
            {
                case EncoderType.Render:
                    encoder.Value.PopDebugGroup();
                    break;
                case EncoderType.Blit:
                    encoder.Value.PopDebugGroup();
                    break;
                case EncoderType.Compute:
                    encoder.Value.PopDebugGroup();
                    break;
            }
        }

        // The flip interval holds exactly two dispatches and a zero-draw pass. Skipping
        // dispatches by label splits them: the rate collapsing names the writer.
        // /tmp/ryujinx-metal-skip-dispatch holds comma-separated label prefixes.
        private static string[] _skipDispatchLabels = System.Array.Empty<string>();

        internal static void RefreshSkipDispatch()
        {
            try
            {
                _skipDispatchLabels = System.IO.File.Exists("/tmp/ryujinx-metal-skip-dispatch")
                    ? System.IO.File.ReadAllText("/tmp/ryujinx-metal-skip-dispatch")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : System.Array.Empty<string>();
            }
            catch (System.IO.IOException)
            {
            }
        }

        public void DispatchCompute(int groupsX, int groupsY, int groupsZ)
        {
            _encoderStateManager.NoteComputeImageWriters();
            string computeLabel = _encoderStateManager.ComputeProgram?.DebugLabel;
            if (_dumpAllShaders)
            {
                _encoderStateManager.ComputeProgram?.DumpSources(FrameCapture.ShaderDumpDir);
            }
            if (UploadCorrelator.Enabled && computeLabel != null)
            {
                UploadCorrelator.Seq("CD:" + (computeLabel.Length > 6 ? computeLabel[..6] : computeLabel) + $"({groupsX}x{groupsY}x{groupsZ})");
            }

            if (_skipDispatchLabels.Length != 0 && computeLabel != null)
            {
                foreach (string skip in _skipDispatchLabels)
                {
                    if (computeLabel.StartsWith(skip, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
            }

            OpRing.NoteDispatch(computeLabel);
            DispatchCompute(groupsX, groupsY, groupsZ, String.Empty);
        }

        public void DispatchCompute(int groupsX, int groupsY, int groupsZ, string debugGroupName)
        {
            // Metal rejects a dispatch with any zero dimension outright - its validation
            // layer asserts on threadgroupsPerGrid.width(0) - and without the layer it is
            // undefined. The guest issues these; Vulkan accepts them as a no-op, so
            // nothing upstream filters them out. Dropping them here is what a no-op means.
            if (groupsX <= 0 || groupsY <= 0 || groupsZ <= 0)
            {
                return;
            }

            MTLComputeCommandEncoder computeCommandEncoder = GetOrCreateComputeEncoder(true);

            ComputeSize localSize = _encoderStateManager.ComputeLocalSize;

            TraceDispatch(groupsX, groupsY, groupsZ, localSize);

            if (debugGroupName != String.Empty)
            {
                PushDebugGroup(debugGroupName);
            }

            computeCommandEncoder.DispatchThreadgroups(
                new MTLSize { width = (ulong)groupsX, height = (ulong)groupsY, depth = (ulong)groupsZ },
                new MTLSize { width = (ulong)localSize.X, height = (ulong)localSize.Y, depth = (ulong)localSize.Z });

            DispatchCount++;

            if (debugGroupName != String.Empty)
            {
                PopDebugGroup();
            }

            _encoderStateManager.DisposeComputeTemporaryBuffers();
        }

        public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance)
        {
            Draw(vertexCount, instanceCount, firstVertex, firstInstance, String.Empty);
        }

        // Must run before any state or buffer is captured against the current
        // command buffer, since a flush swaps Cbs.
        // The split-point probe never fired for the upscaler: its read-after-write on the
        // scene texture is resolved by the pass boundary that the render-target change
        // causes, not by the RAW split. So for the pre-probe program the pass is ended here
        // explicitly (it would end a moment later anyway when the target changes; when it
        // would not, this is one extra Load/Store split per frame) and the program's input
        // is read by the compute engine in the gap, before its render encoder is created.
        // Note the probe itself is a tracked read of the scene texture: if that read alone
        // makes the flash vanish, the ordering fault is between the scene pass and this draw.
        private static long _prePassProbes, _prePassProbesNoInput;
        private void PreProbeAtPassStart()
        {
            if (_preProbeLabel.Length == 0 || !UploadCorrelator.Enabled ||
                _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel is not string pl ||
                !pl.StartsWith(_preProbeLabel, StringComparison.Ordinal))
            {
                return;
            }

            // The program's own first texture binding (recorded at its previous bind), not
            // the first stale texture in the guest's binding table: the earlier probe read a
            // different, brighter texture (mean 0.5 vs the scene's 0.135).
            Texture input = UploadCorrelator.LastStageInput ?? _encoderStateManager.EarlierWrittenSampledTexture() ?? _encoderStateManager.FirstBoundLargeTexture();
            UploadCorrelator.NoteStageDrawState(_encoderStateManager.DescribeRenderTargets(input), _encoderStateManager.RenderProgram?.FragmentOutputMap ?? -2);
            if (input == null)
            {
                _prePassProbesNoInput++;
                return;
            }

            if (UploadCorrelator.PreProbedThisPeriod)
            {
                return;   // once per period: a label with many draws must not be split at each
            }

            if (Cbs.Encoders.CurrentEncoderType == EncoderType.Render)
            {
                EndCurrentPass(PassEndReason.Unspecified);
            }

            UploadCorrelator.PreCompositeProbe(Cbs, input);
            UploadCorrelator.NoteStageOutputBefore(Cbs, _encoderStateManager.RenderTargets[0]);
            _encoderStateManager.SignalRenderDirty();
            _prePassProbes++;
        }

        private void AutoFlushPreDraw()
        {
            if (_dumpAllShaders)
            {
                // RYUJINX_METAL_DUMP_SHADERS=1: every program's MSL the first time it draws
                // (DumpSources writes each program once), without the upload correlator.
                _encoderStateManager.CurrentEncoderState.RenderProgram?.DumpSources(FrameCapture.ShaderDumpDir);
            }

            if (_passLabelId != null)
            {
                string label = _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel ?? "?";
                _passPrograms[label] = _passPrograms.GetValueOrDefault(label) + 1;
                _passReadsPrev |= !_passReadsPrev && _encoderStateManager.SamplesAnyOf(_labelPrevTargets);
            }

            bool isPre = _preProbeLabel.Length != 0 && UploadCorrelator.Enabled &&
                _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel is string pl &&
                pl.StartsWith(_preProbeLabel, StringComparison.Ordinal);
            if (isPre) { _preDraws++; }

            if (_renderer.AutoFlush.ShouldFlushDraw(DrawCount))
            {
                _autoFlushDrawCount++;
                if (isPre) { _preDrawsFlushedBefore++; }
                FlushCommandsImpl();
            }
            else if (StarveFlush.ShouldFlushAtDraw(DrawCount, _renderer.AutoFlush.LastFlushDrawCount, _renderer.CommandBufferPool))
            {
                FlushCommandsImpl();
            }

            DrawCount++;
        }

        // Gloom (miasma) material programs identified from the v41/v43 shader dumps:
        // their fragment shaders scroll volumetric noise by sin(fp_c4[9].z * PI), so
        // dumping their constant buffers across the traced frames reveals whether that
        // phase input advances per frame or is frozen. An extra label can be supplied
        // via RYUJINX_METAL_DUMP_CBUF for programs whose hash shifts with spec state.
        private static readonly string _extraCbufDumpLabel =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_DUMP_CBUF");

        private static bool IsGloomTraceProgram(string label)
        {
            switch (label)
            {
                case "ea7aeb577fe51e2f":
                case "b82634558e8e193d":
                case "6a81d5ed27684a34":
                case "375f2adfe7dbc34b":
                    return true;
                default:
                    return _extraCbufDumpLabel != null && label == _extraCbufDumpLabel;
            }
        }

        private static readonly string _dumpDepthAfter =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_DUMP_DEPTH_AFTER");

        private static readonly int _dumpDepthNth =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_DUMP_DEPTH_NTH"), out int n) ? n : 1;

        private bool _depthDumped;
        private int _depthDumpSeen;
        private bool _depthDumpPending;

        /// <summary>
        /// Reads the bound depth attachment back and reports how many pixels left the
        /// cleared value. <paramref name="when"/> tells the sample taken before the
        /// draw is encoded apart from the one taken after it.
        /// </summary>
        private void SampleDepth(string label, string when)
        {
            Texture depth = _encoderStateManager.DepthStencil;

            if (depth == null)
            {
                return;
            }

            EndCurrentPass(PassEndReason.DepthDump);
            _renderer.FlushAllCommands();

            using PinnedSpan<byte> data = depth.GetData();
            ReadOnlySpan<byte> bytes = data.Get();

            int total = bytes.Length / sizeof(float);
            int written = 0;
            float min = float.MaxValue;
            float max = float.MinValue;

            for (int i = 0; i < total; i++)
            {
                float value = BitConverter.ToSingle(bytes.Slice(i * sizeof(float), sizeof(float)));

                if (value < 0.99999f)
                {
                    written++;
                    min = Math.Min(min, value);
                    max = Math.Max(max, value);
                }
            }

            string range = written != 0 ? $" min={min:F6} max={max:F6}" : string.Empty;

            Logger.Warning?.PrintMsg(
                LogClass.Gpu,
                $"depthdump {when} {label} draw#{_depthDumpSeen}: written={written}/{total}{range}");
        }

        /// <summary>
        /// Takes the "before" depth sample and arms the "after" one. The trace hook
        /// runs before the primitive is encoded, so a readback done from there alone
        /// reports the state the draw has not contributed to yet.
        /// </summary>
        private void DumpDepthAroundDraw(string label)
        {
            if (_dumpDepthAfter == null || _depthDumped || label != _dumpDepthAfter)
            {
                return;
            }

            if (++_depthDumpSeen < _dumpDepthNth)
            {
                return;
            }

            if (_encoderStateManager.DepthStencil == null)
            {
                return;
            }

            _depthDumped = true;
            _depthDumpPending = true;

            SampleDepth(label, "before");
        }

        /// <summary>
        /// Called once the primitive has been encoded, to take the "after" sample.
        /// </summary>
        private void TraceDrawPost()
        {
            if (!_depthDumpPending)
            {
                return;
            }

            _depthDumpPending = false;

            SampleDepth(_dumpDepthAfter, "after ");
        }

        // Diagnostic: RYUJINX_METAL_CAPTURE_FROM / _TO name the programs whose draws
        // open and close the GPU capture scope, so a trace covers only them.
        private static readonly string _captureFromLabel =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_FROM");

        private static readonly string _captureToLabel =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_TO");

        private bool _scopeOpen;
        private bool _scopeClosed;

        private void TraceDraw(string kind, int count, int instanceCount, int firstIndexOrVertex, int firstInstance)
        {
            // Outside the trace gate on purpose. The chain is measured as far as "a correct
            // vertex buffer, a stride of 16, and a constant attribute in the shader", and
            // the draw's own parameters are one of the three places left where those can
            // stop agreeing - a count of one, or a first-vertex that pins every invocation
            // to the same element, collapses the UVs exactly as observed.
            if (UploadCorrelator.Enabled &&
                _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel is string dl &&
                dl.StartsWith("480117", StringComparison.Ordinal))
            {
                UploadCorrelator.NoteDrawParams(count, instanceCount, firstIndexOrVertex, kind == "DrawIndexed" ? 1 : 0);
            }

            if (_renderer.FrameCapture.DrawTraceActive)
            {
                Texture target = _encoderStateManager.RenderTargets[0];
                string targetText = target != null ? $"{target.Width}x{target.Height}/{target.Info.Format}" : "none";

                Program program = _encoderStateManager.RenderProgram;
                string programText = "none";

                if (program != null)
                {
                    programText = program.DebugLabel;
                    program.DumpSources(FrameCapture.ShaderDumpDir);
                }

                Logger.Warning?.PrintMsg(
                    LogClass.Gpu,
                    $"trace draw#{DrawCount} {kind} count={count} inst={instanceCount} first={firstIndexOrVertex} firstInst={firstInstance} topo={_encoderStateManager.Topology} rt={targetText} prog={programText}");

                if (instanceCount > 1 && kind == "Draw")
                {
                    _encoderStateManager.TraceDumpInstanceBuffers(firstInstance);
                }

                DumpDepthAroundDraw(programText);

                if (_captureFromLabel != null && programText == _captureFromLabel && !_scopeOpen &&
                    _renderer.FrameCapture.ScopeReady)
                {
                    _scopeOpen = true;
                    _renderer.FrameCapture.BeginScope();
                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"capture scope opened at {programText}");
                }
                else if (_captureToLabel != null && programText == _captureToLabel && _scopeOpen && !_scopeClosed)
                {
                    _scopeClosed = true;
                    _renderer.FrameCapture.EndScope();
                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"capture scope closed at {programText}");
                }

                if (IsGloomTraceProgram(programText))
                {
                    _encoderStateManager.TraceDumpUniformBuffers(programText);
                    _encoderStateManager.TraceDumpTextures(programText);
                }
            }
        }

        private void TraceDispatch(int groupsX, int groupsY, int groupsZ, ComputeSize localSize)
        {
            if (_renderer.FrameCapture.DrawTraceActive)
            {
                Program program = _encoderStateManager.ComputeProgram;
                string programText = "none";

                if (program != null)
                {
                    programText = program.DebugLabel;
                    program.DumpSources(FrameCapture.ShaderDumpDir);
                }

                Logger.Warning?.PrintMsg(
                    LogClass.Gpu,
                    $"trace dispatch groups={groupsX}x{groupsY}x{groupsZ} local={localSize.X}x{localSize.Y}x{localSize.Z} prog={programText}");
            }
        }

        public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance, string debugGroupName)
        {
            if (vertexCount == 0 || SkipThisDraw())
            {
                return;
            }

            PipelineState.CurrentDrawVertices = vertexCount;

            NoteAttachmentWriter();

            if (_encoderStateManager.RenderProgram?.FragmentWritesStorage == true) { _passHasFragmentStore = true; _encoderStateManager.NoteFragmentStorageWrites(); }
            UploadCorrelator.PollCount();
            if (UploadCorrelator.Enabled)
            {
                Program sp = _encoderStateManager.RenderProgram;
                string sl = sp?.DebugLabel;
                if (sp != null && sl != null && (sp.FragmentWritesStorage || sl.StartsWith("2b36a7") || sl.StartsWith("4e8cad") || sl.StartsWith("1997e8")))
                {
                    UploadCorrelator.Seq((sp.FragmentWritesStorage ? "Dw:" : "D:") + (sl.Length > 6 ? sl[..6] : sl));
                }
            }

            if (_stageLabel.Length != 0 && UploadCorrelator.Enabled &&
                _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel is string stageLbl &&
                stageLbl.StartsWith(_stageLabel, StringComparison.Ordinal))
            {
                UploadCorrelator.NoteStageDrawIssued();
                UploadCorrelator.NoteStageDrawBinding();
                UploadCorrelator.NoteStageDrawArgs($"Draw v{vertexCount} inst{instanceCount} fv{firstVertex} fi{firstInstance} topo={_encoderStateManager.Topology}");
                UploadCorrelator.NoteStageRaster(_encoderStateManager.DescribeRaster());
                UploadCorrelator.ArmStageDump(_encoderStateManager.RenderTargets);
                // The render target as it stands immediately BEFORE this draw, so the draw's
                // own contribution is out - before. A blit here ends the pass, which the
                // per-draw split already does routinely.
                UploadCorrelator.NoteStageOutputBefore(Cbs, _encoderStateManager.RenderTargets[0]);
                UploadCorrelator.NoteStageBlend(_encoderStateManager.DescribeBlend(0));
            }

            PreProbeAtPassStart();

            AutoFlushPreDraw();

            if (_hardSync && _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel == "3ebc3a8f6b77cc8f")
            {
                CommandBufferScoped previous = Cbs;
                FlushCommandsImpl();
                previous.CommandBuffer.WaitUntilCompleted();
            }

            if (HdrPassProbe.Enabled)
            {
                HdrPassProbe.NoteDraw(_encoderStateManager.RenderTargets[0],
                    _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel, DrawCount);

                bool isToneDraw = HdrPassProbe.TakeToneDrawFlag();

                if (isToneDraw && HdrPassProbe.ShouldSkipToneDraw())
                {
                    return;
                }

                if (HdrPassProbe.ShouldSkipDraw(_encoderStateManager.RenderTargets[0]) ||
                    HdrPassProbe.ShouldSkipPresentWrite(_encoderStateManager.RenderTargets[0]))
                {
                    return;
                }
            }

            if (DrawRing.Enabled)
            {
                DrawRing.Record(_encoderStateManager.CurrentEncoderState, _encoderStateManager.Topology, vertexCount, instanceCount);
            }

            TraceDraw("Draw", vertexCount, instanceCount, firstVertex, firstInstance);

            MTLPrimitiveType primitiveType = TopologyRemap(_encoderStateManager.Topology).Convert();

            if (TopologyUnsupported(_encoderStateManager.Topology))
            {
                IndexBufferPattern pattern = GetIndexBufferPattern();

                BufferHandle handle = pattern.GetRepeatingBuffer(vertexCount, out int indexCount);
                Auto<DisposableBuffer> buffer = _renderer.BufferManager.GetBuffer(handle, false);
                MTLBuffer mtlBuffer = buffer.Get(Cbs, 0, indexCount * sizeof(int)).Value;

                // The last unread link in the chain from the presented pixel back to the
                // draw. This buffer turns the blit's quad into two triangles; if its six
                // entries are all zero, every invocation fetches vertex zero and the
                // interpolated attribute is constant across the primitive - which is a
                // screen filled with one texel, exactly what is measured.
                if (UploadCorrelator.Enabled && indexCount >= 6 &&
                    _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel is string idl &&
                    idl.StartsWith("480117", StringComparison.Ordinal))
                {
                    UploadCorrelator.NoteIndices(mtlBuffer.Contents);
                }

                MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);
                ToneMapProbe.Record(_encoderStateManager.CurrentEncoderState);

                // Checked only after the encoder has applied state, since that is what
                // builds the pipeline. A failed build leaves the encoder without a usable
                // pipeline, and issuing the draw anyway faults inside the Metal driver.
                // Only the draw is skipped - the cleanup below still has to run.
                if (_encoderStateManager.HasValidRenderPipeline &&
                    !(_skipHdrDraws && HdrPassProbe.IsWatchedTarget(_encoderStateManager.RenderTargets[0])) &&
                    !(_skipShader.Length != 0 &&
                      _skipShader == _encoderStateManager.RenderProgram?.DebugLabel))
                {
                    // The converted-topology path must keep the draw's instancing and
                    // base vertex/instance: dropping them draws a single instance of
                    // the wrong vertices for instanced quad/fan draws.
                    CrashRing.Draw(CrashRing.Kind.DrawIndexed, _encoderStateManager.RenderProgram?.DebugLabel, EncoderStateManager.LastPso, mtlBuffer.NativePtr, renderCommandEncoder.NativePtr, CommandBufferEncoder.RenderEncoderGeneration, indexCount);
                    renderCommandEncoder.DrawIndexedPrimitives(
                        primitiveType,
                        (ulong)indexCount,
                        MTLIndexType.UInt32,
                        mtlBuffer,
                        0,
                        (ulong)instanceCount,
                        firstVertex,
                        (ulong)firstInstance);
                }
            }
            else
            {
                MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);
                ToneMapProbe.Record(_encoderStateManager.CurrentEncoderState);

                if (debugGroupName != String.Empty)
                {
                    PushDebugGroup(debugGroupName);
                }

                if (_encoderStateManager.HasValidRenderPipeline &&
                    !(_skipHdrDraws && HdrPassProbe.IsWatchedTarget(_encoderStateManager.RenderTargets[0])) &&
                    !(_skipShader.Length != 0 &&
                      _skipShader == _encoderStateManager.RenderProgram?.DebugLabel))
                {
                    CrashRing.Draw(CrashRing.Kind.DrawArrays, _encoderStateManager.RenderProgram?.DebugLabel, EncoderStateManager.LastPso, IntPtr.Zero, renderCommandEncoder.NativePtr, CommandBufferEncoder.RenderEncoderGeneration, vertexCount);
                    renderCommandEncoder.DrawPrimitives(
                        primitiveType,
                        (ulong)firstVertex,
                        (ulong)vertexCount,
                        (ulong)instanceCount,
                        (ulong)firstInstance);
                }

                if (debugGroupName != String.Empty)
                {
                    PopDebugGroup();
                }
            }

            _encoderStateManager.DisposeRenderTemporaryBuffers();

            if (_stainSweep && _stainAtDraw >= 0 && _lastPresentSource != null &&
                (int)(DrawCount - _drawCountAtFrameStart) == _stainAtDraw)
            {
                EndCurrentPass(PassEndReason.Unspecified);

                MTLRenderPassDescriptor sweep = new();
                MTLRenderPassColorAttachmentDescriptor sa = sweep.ColorAttachments.Object(0);
                sa.Texture = _lastPresentSource.GetIdentityHandle(Cbs);
                sa.LoadAction = MTLLoadAction.Clear;
                sa.StoreAction = MTLStoreAction.Store;
                sa.ClearColor = new MTLClearColor { red = 0.0, green = 1.0, blue = 0.0, alpha = 1.0 };

                MTLRenderCommandEncoder sweepEncoder = CommandBuffer.RenderCommandEncoder(sweep);
                sweepEncoder.EndEncoding();
                sweep.Dispose();
            }

            if (_serializeDraws)
            {
                EndCurrentPass(PassEndReason.FragmentDependency);
            }

            TraceDrawPost();
        }

        private IndexBufferPattern GetIndexBufferPattern()
        {
            return _encoderStateManager.Topology switch
            {
                PrimitiveTopology.Quads => QuadsToTrisPattern,
                PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon => TriFanToTrisPattern,
                _ => throw new NotSupportedException($"Unsupported topology: {_encoderStateManager.Topology}"),
            };
        }

        private PrimitiveTopology TopologyRemap(PrimitiveTopology topology)
        {
            return topology switch
            {
                PrimitiveTopology.Quads => PrimitiveTopology.Triangles,
                PrimitiveTopology.QuadStrip => PrimitiveTopology.TriangleStrip,
                PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon => PrimitiveTopology.Triangles,
                _ => topology,
            };
        }

        private bool TopologyUnsupported(PrimitiveTopology topology)
        {
            return topology switch
            {
                PrimitiveTopology.Quads or PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon => true,
                _ => false,
            };
        }

        public void DrawIndexed(int indexCount, int instanceCount, int firstIndex, int firstVertex, int firstInstance)
        {
            PipelineState.CurrentDrawVertices = indexCount;

            if (_watchEncode && _encoderStateManager.RenderProgram?.IsWatchedMapShader == true)
            {
                // The one draw that composites the Depths map is issued on every frame and
                // its output is missing on a flicker frame even when the shader writes a
                // constant - so what differs has to be in how this draw is encoded. Record
                // the pieces of that encoding per frame; the flicker frames are identified
                // offline from a recording and lined up by timestamp.
                MTLRenderCommandEncoder enc = GetOrCreateRenderEncoder(true);
                Texture rt0 = _encoderStateManager.RenderTargets.Length > 0
                    ? _encoderStateManager.RenderTargets[0]
                    : null;

                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"watchenc t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} " +
                    $"idx={indexCount} inst={instanceCount} enc={enc.NativePtr:X} " +
                    $"prog={_encoderStateManager.RenderProgram?.GetHashCode():X8} " +
                    $"rt0={rt0?.GetHashCode() ?? 0:X8} rtw={rt0?.Width ?? 0} rth={rt0?.Height ?? 0} " +
                    $"raster={_encoderStateManager.DescribeRaster()}");
            }

            if (SkipThisDraw())
            {
                return;
            }

            NoteAttachmentWriter();

            if (indexCount == 0)
            {
                return;
            }

            if (_encoderStateManager.RenderProgram?.FragmentWritesStorage == true) { _passHasFragmentStore = true; _encoderStateManager.NoteFragmentStorageWrites(); }
            UploadCorrelator.PollCount();
            if (UploadCorrelator.Enabled)
            {
                Program sp = _encoderStateManager.RenderProgram;
                string sl = sp?.DebugLabel;
                if (sp != null && sl != null && (sp.FragmentWritesStorage || sl.StartsWith("2b36a7") || sl.StartsWith("4e8cad") || sl.StartsWith("1997e8")))
                {
                    UploadCorrelator.Seq((sp.FragmentWritesStorage ? "Dw:" : "D:") + (sl.Length > 6 ? sl[..6] : sl));
                }
            }

            if (_stageLabel.Length != 0 && UploadCorrelator.Enabled &&
                _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel is string stageLbl2 &&
                stageLbl2.StartsWith(_stageLabel, StringComparison.Ordinal))
            {
                UploadCorrelator.NoteStageDrawIssued();
                UploadCorrelator.NoteStageDrawBinding();
                UploadCorrelator.NoteStageDrawArgs($"DrawIndexed idx{indexCount} inst{instanceCount} fi{firstIndex} fv{firstVertex} finst{firstInstance} topo={_encoderStateManager.Topology}");
                UploadCorrelator.NoteStageRaster(_encoderStateManager.DescribeRaster());
                UploadCorrelator.ArmStageDump(_encoderStateManager.RenderTargets);
                // The render target as it stands immediately BEFORE this draw, so the draw's
                // own contribution is out - before. A blit here ends the pass, which the
                // per-draw split already does routinely.
                UploadCorrelator.NoteStageOutputBefore(Cbs, _encoderStateManager.RenderTargets[0]);
                UploadCorrelator.NoteStageBlend(_encoderStateManager.DescribeBlend(0));
            }

            PreProbeAtPassStart();

            AutoFlushPreDraw();

            if (HdrPassProbe.Enabled)
            {
                HdrPassProbe.NoteDraw(_encoderStateManager.RenderTargets[0],
                    _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel, DrawCount);

                bool isToneDraw = HdrPassProbe.TakeToneDrawFlag();

                if (isToneDraw && HdrPassProbe.ShouldSkipToneDraw())
                {
                    return;
                }

                if (HdrPassProbe.ShouldSkipDraw(_encoderStateManager.RenderTargets[0]) ||
                    HdrPassProbe.ShouldSkipPresentWrite(_encoderStateManager.RenderTargets[0]))
                {
                    return;
                }
            }

            if (DrawRing.Enabled)
            {
                DrawRing.Record(_encoderStateManager.CurrentEncoderState, _encoderStateManager.Topology, indexCount, instanceCount);
            }

            TraceDraw("DrawIndexed", indexCount, instanceCount, firstIndex, firstInstance);

            MTLBuffer mtlBuffer;
            int offset;
            MTLIndexType type;
            int finalIndexCount = indexCount;

            MTLPrimitiveType primitiveType = TopologyRemap(_encoderStateManager.Topology).Convert();

            if (TopologyUnsupported(_encoderStateManager.Topology))
            {
                IndexBufferPattern pattern = GetIndexBufferPattern();
                int convertedCount = pattern.GetConvertedCount(indexCount);

                finalIndexCount = convertedCount;

                (mtlBuffer, offset, type) = _encoderStateManager.IndexBuffer.GetConvertedIndexBuffer(_renderer, Cbs, firstIndex, indexCount, convertedCount, pattern);
            }
            else
            {
                (mtlBuffer, offset, type) = _encoderStateManager.IndexBuffer.GetIndexBuffer(_renderer, Cbs);

                // The guest's own index buffer, which is the one this draw actually uses -
                // the earlier probe sat in the quad-conversion branch and never fired.
                // Six zero entries here would make every invocation fetch vertex zero,
                // collapsing the interpolated attribute across the primitive, which is a
                // screen filled with a single texel.
                if (UploadCorrelator.Enabled && indexCount >= 6 &&
                    _encoderStateManager.CurrentEncoderState.RenderProgram?.DebugLabel is string idl2 &&
                    idl2.StartsWith("480117", StringComparison.Ordinal))
                {
                    UploadCorrelator.NoteIndices(mtlBuffer.Contents + offset, (int)type);
                }
            }

            if (mtlBuffer.NativePtr != IntPtr.Zero)
            {
                MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);
                ToneMapProbe.Record(_encoderStateManager.CurrentEncoderState);

                if (_encoderStateManager.HasValidRenderPipeline &&
                    !(_skipHdrDraws && HdrPassProbe.IsWatchedTarget(_encoderStateManager.RenderTargets[0])) &&
                    !(_skipShader.Length != 0 &&
                      _skipShader == _encoderStateManager.RenderProgram?.DebugLabel))
                {
                    CrashRing.Draw(CrashRing.Kind.DrawIndexed, _encoderStateManager.RenderProgram?.DebugLabel, EncoderStateManager.LastPso, mtlBuffer.NativePtr, renderCommandEncoder.NativePtr, CommandBufferEncoder.RenderEncoderGeneration, finalIndexCount);
                    renderCommandEncoder.DrawIndexedPrimitives(
                        primitiveType,
                        (ulong)finalIndexCount,
                        type,
                        mtlBuffer,
                        (ulong)offset,
                        (ulong)instanceCount,
                        firstVertex,
                        (ulong)firstInstance);
                }
            }

            _encoderStateManager.DisposeRenderTemporaryBuffers();

            if (_stainSweep && _stainAtDraw >= 0 && _lastPresentSource != null &&
                (int)(DrawCount - _drawCountAtFrameStart) == _stainAtDraw)
            {
                EndCurrentPass(PassEndReason.Unspecified);

                MTLRenderPassDescriptor sweep = new();
                MTLRenderPassColorAttachmentDescriptor sa = sweep.ColorAttachments.Object(0);
                sa.Texture = _lastPresentSource.GetIdentityHandle(Cbs);
                sa.LoadAction = MTLLoadAction.Clear;
                sa.StoreAction = MTLStoreAction.Store;
                sa.ClearColor = new MTLClearColor { red = 0.0, green = 1.0, blue = 0.0, alpha = 1.0 };

                MTLRenderCommandEncoder sweepEncoder = CommandBuffer.RenderCommandEncoder(sweep);
                sweepEncoder.EndEncoding();
                sweep.Dispose();
            }

            if (_serializeDraws)
            {
                EndCurrentPass(PassEndReason.FragmentDependency);
            }

            TraceDrawPost();
        }

        public void DrawIndexedIndirect(BufferRange indirectBuffer)
        {
            DrawIndexedIndirectOffset(indirectBuffer);
        }

        public void DrawIndexedIndirectOffset(BufferRange indirectBuffer, int offset = 0)
        {
            PipelineState.CurrentDrawVertices = int.MaxValue;

            // Indirect draws write attachments too; the writer census had only the direct
            // entry points and never saw a target painted through here.
            NoteAttachmentWriter();
            // TODO: Reindex unsupported topologies
            if (TopologyUnsupported(_encoderStateManager.Topology))
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Drawing indexed with unsupported topology: {_encoderStateManager.Topology}");
            }

            AutoFlushPreDraw();

            TraceDraw("DrawIndexedIndirect", 0, 0, offset, 0);

            MTLBuffer buffer = _renderer.BufferManager
                .GetBuffer(indirectBuffer.Handle, indirectBuffer.Offset, indirectBuffer.Size, false)
                .Get(Cbs, indirectBuffer.Offset, indirectBuffer.Size).Value;

            MTLPrimitiveType primitiveType = TopologyRemap(_encoderStateManager.Topology).Convert();

            (MTLBuffer indexBuffer, int indexOffset, MTLIndexType type) = _encoderStateManager.IndexBuffer.GetIndexBuffer(_renderer, Cbs);

            if (indexBuffer.NativePtr != IntPtr.Zero && buffer.NativePtr != IntPtr.Zero)
            {
                MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);

                if (_encoderStateManager.HasValidRenderPipeline &&
                    !(_skipHdrDraws && HdrPassProbe.IsWatchedTarget(_encoderStateManager.RenderTargets[0])) &&
                    !(_skipShader.Length != 0 &&
                      _skipShader == _encoderStateManager.RenderProgram?.DebugLabel))
                {
                    CrashRing.Draw(CrashRing.Kind.DrawIndirect, _encoderStateManager.RenderProgram?.DebugLabel, EncoderStateManager.LastPso, indexBuffer.NativePtr, renderCommandEncoder.NativePtr, CommandBufferEncoder.RenderEncoderGeneration, 0);
                    renderCommandEncoder.DrawIndexedPrimitives(
                        primitiveType,
                        type,
                        indexBuffer,
                        (ulong)indexOffset,
                        buffer,
                        (ulong)(indirectBuffer.Offset + offset));
                }
            }

            _encoderStateManager.DisposeRenderTemporaryBuffers();
        }

        public void DrawIndexedIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
        {
            // Indirect draws write attachments too; the writer census had only the direct
            // entry points and never saw a target painted through here.
            NoteAttachmentWriter();
            for (int i = 0; i < maxDrawCount; i++)
            {
                DrawIndexedIndirectOffset(indirectBuffer, stride * i);
            }
        }

        public void DrawIndirect(BufferRange indirectBuffer)
        {
            DrawIndirectOffset(indirectBuffer);
        }

        public void DrawIndirectOffset(BufferRange indirectBuffer, int offset = 0)
        {
            PipelineState.CurrentDrawVertices = int.MaxValue;

            // Indirect draws write attachments too; the writer census had only the direct
            // entry points and never saw a target painted through here.
            NoteAttachmentWriter();
            if (TopologyUnsupported(_encoderStateManager.Topology))
            {
                // TODO: Reindex unsupported topologies
                Logger.Warning?.Print(LogClass.Gpu, $"Drawing indirect with unsupported topology: {_encoderStateManager.Topology}");
            }

            AutoFlushPreDraw();

            TraceDraw("DrawIndirect", 0, 0, offset, 0);

            MTLBuffer buffer = _renderer.BufferManager
                .GetBuffer(indirectBuffer.Handle, indirectBuffer.Offset, indirectBuffer.Size, false)
                .Get(Cbs, indirectBuffer.Offset, indirectBuffer.Size).Value;

            MTLPrimitiveType primitiveType = TopologyRemap(_encoderStateManager.Topology).Convert();
            MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);

            if (_encoderStateManager.HasValidRenderPipeline)
            {
                renderCommandEncoder.DrawPrimitives(
                    primitiveType,
                    buffer,
                    (ulong)(indirectBuffer.Offset + offset));
            }

            _encoderStateManager.DisposeRenderTemporaryBuffers();
        }

        public void DrawIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
        {
            // Indirect draws write attachments too; the writer census had only the direct
            // entry points and never saw a target painted through here.
            NoteAttachmentWriter();
            for (int i = 0; i < maxDrawCount; i++)
            {
                DrawIndirectOffset(indirectBuffer, stride * i);
            }
        }

        public void DrawTexture(ITexture texture, ISampler sampler, Extents2DF srcRegion, Extents2DF dstRegion)
        {
            PipelineState.CurrentDrawVertices = 4;

            NoteAttachmentWriter();
            _renderer.HelperShader.DrawTexture(texture, sampler, srcRegion, dstRegion);
        }

        public void SetAlphaTest(bool enable, float reference, CompareOp op)
        {
            // This is currently handled using shader specialization, as Metal does not support alpha test.
            // In the future, we may want to use this to write the reference value into the support buffer,
            // to avoid creating one version of the shader per reference value used.
        }

        public void SetBlendState(AdvancedBlendDescriptor blend)
        {
            // Metal does not support advanced blend.
        }

        public void SetBlendState(int index, BlendDescriptor blend)
        {
            _encoderStateManager.UpdateBlendDescriptors(index, blend);
        }

        public void SetDepthBias(PolygonModeMask enables, float factor, float units, float clamp)
        {
            if (enables == 0)
            {
                _encoderStateManager.UpdateDepthBias(0, 0, 0);
            }
            else
            {
                _encoderStateManager.UpdateDepthBias(units, factor, clamp);
            }
        }

        public void SetDepthClamp(bool clamp)
        {
            _encoderStateManager.UpdateDepthClamp(clamp);
        }

        public void SetDepthMode(DepthMode mode)
        {
            // Metal does not support depth clip control.
        }

        public void SetDepthTest(DepthTestDescriptor depthTest)
        {
            _encoderStateManager.UpdateDepthState(depthTest);
        }

        public void SetFaceCulling(bool enable, Face face)
        {
            _encoderStateManager.UpdateCullMode(enable, face);
        }

        public void SetFrontFace(FrontFace frontFace)
        {
            _encoderStateManager.UpdateFrontFace(frontFace);
        }

        public void SetIndexBuffer(BufferRange buffer, IndexType type)
        {
            _encoderStateManager.UpdateIndexBuffer(buffer, type);
        }

        public void SetImage(ShaderStage stage, int binding, ITexture image)
        {
            // A storage-image binding is a potential writer of that texture (compute or
            // fragment image store); register it for the writer census.
            if (UploadCorrelator.Enabled && image is Texture imgTex)
            {
                UploadCorrelator.NoteAttachmentDraw(imgTex.CanonicalPtr, "image");
            }

            if (UsageProbe.Enabled && image is Texture probeTex)
            {
                UsageProbe.NoteStorage(probeTex.UsageRoot);
            }

            if (image is Texture { LeanUsage: true } leanTex)
            {
                Texture.NoteLeanViolation($"storage bind of {leanTex.Info.Width}x{leanTex.Info.Height} {leanTex.MtlFormat}");
            }

            if (image is TextureBase img)
            {
                _encoderStateManager.UpdateImage(stage, binding, img);
            }
        }

        public void SetImageArray(ShaderStage stage, int binding, IImageArray array)
        {
            if (array is ImageArray imageArray)
            {
                _encoderStateManager.UpdateImageArray(stage, binding, imageArray);
            }
        }

        public void SetImageArraySeparate(ShaderStage stage, int setIndex, IImageArray array)
        {
            if (array is ImageArray imageArray)
            {
                _encoderStateManager.UpdateImageArraySeparate(stage, setIndex, imageArray);
            }
        }

        public void SetLineParameters(float width, bool smooth)
        {
            // Metal does not support wide-lines.
        }

        public void SetLogicOpState(bool enable, LogicalOp op)
        {
            _encoderStateManager.UpdateLogicOpState(enable, op);
        }

        public void SetMultisampleState(MultisampleDescriptor multisample)
        {
            _encoderStateManager.UpdateMultisampleState(multisample);
        }

        public void SetPatchParameters(int vertices, ReadOnlySpan<float> defaultOuterLevel, ReadOnlySpan<float> defaultInnerLevel)
        {
            // TODO: Default tessellation levels need shader emulation.
        }

        public void SetPointParameters(float size, bool isProgramPointSize, bool enablePointSprite, Origin origin)
        {
            // TODO: Point size and point sprites need shader emulation.
        }

        public void SetPolygonMode(PolygonMode frontMode, PolygonMode backMode)
        {
            // Metal does not support polygon mode.
        }

        public void SetPrimitiveRestart(bool enable, int index)
        {
            // Always active for LineStrip and TriangleStrip
            // https://github.com/gpuweb/gpuweb/issues/1220#issuecomment-732483263
            // https://developer.apple.com/documentation/metal/mtlrendercommandencoder/1515520-drawindexedprimitives
            // https://stackoverflow.com/questions/70813665/how-to-render-multiple-trianglestrips-using-metal

            // Emulating disabling this is very difficult. It's unlikely for an index buffer to use the largest possible index,
            // so it's fine nearly all of the time.
        }

        public void SetPrimitiveTopology(PrimitiveTopology topology)
        {
            _encoderStateManager.UpdatePrimitiveTopology(topology);
        }

        public void SetProgram(IProgram program)
        {
            _encoderStateManager.UpdateProgram(program);
        }

        public void SetRasterizerDiscard(bool discard)
        {
            _encoderStateManager.UpdateRasterizerDiscard(discard);
        }

        public void SetRenderTargetColorMasks(ReadOnlySpan<uint> componentMask)
        {
            _encoderStateManager.UpdateRenderTargetColorMasks(componentMask);
        }

        public void SetRenderTargets(Span<ITexture> colors, ITexture depthStencil)
        {
            // Attachment changes end the current render pass anyway, so they are the
            // cheapest place to submit pending work early for host sync waits.
            if (_renderer.AutoFlush.ShouldFlushAttachmentChange(DrawCount))
            {
                _autoFlushAttachmentCount++;
                FlushCommandsImpl();
            }
            else if (StarveFlush.ShouldFlushAtAttachmentChange(DrawCount, _renderer.AutoFlush.LastFlushDrawCount, _renderer.CommandBufferPool))
            {
                FlushCommandsImpl();
            }

            if (UsageProbe.Enabled)
            {
                foreach (ITexture color in colors)
                {
                    if (color is Texture colorTex)
                    {
                        UsageProbe.NoteRenderTarget(colorTex.UsageRoot);
                    }
                }
            }

            _encoderStateManager.UpdateRenderTargets(colors, depthStencil);
        }

        public void SetScissors(ReadOnlySpan<Rectangle<int>> regions)
        {
            _encoderStateManager.UpdateScissors(regions);
        }

        public void SetStencilTest(StencilTestDescriptor stencilTest)
        {
            _encoderStateManager.UpdateStencilState(stencilTest);
        }

        public void SetUniformBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            _encoderStateManager.UpdateUniformBuffers(buffers);
        }

        public void SetStorageBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            _encoderStateManager.UpdateStorageBuffers(buffers);
        }

        internal void SetStorageBuffers(int first, ReadOnlySpan<Auto<DisposableBuffer>> buffers)
        {
            _encoderStateManager.UpdateStorageBuffers(first, buffers);
        }

        public void SetTextureAndSampler(ShaderStage stage, int binding, ITexture texture, ISampler sampler)
        {
            if (texture is TextureBase tex)
            {
                if (sampler == null || sampler is SamplerHolder)
                {
                    _encoderStateManager.UpdateTextureAndSampler(stage, binding, tex, (SamplerHolder)sampler);
                }
            }
        }

        public void SetTextureArray(ShaderStage stage, int binding, ITextureArray array)
        {
            if (array is TextureArray textureArray)
            {
                _encoderStateManager.UpdateTextureArray(stage, binding, textureArray);
            }
        }

        public void SetTextureArraySeparate(ShaderStage stage, int setIndex, ITextureArray array)
        {
            if (array is TextureArray textureArray)
            {
                _encoderStateManager.UpdateTextureArraySeparate(stage, setIndex, textureArray);
            }
        }

        public void SetUserClipDistance(int index, bool enableClip)
        {
            // TODO. Same as Vulkan
        }

        public void SetVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs)
        {
            _encoderStateManager.UpdateVertexAttribs(vertexAttribs);
        }

        public void SetVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers)
        {
            _encoderStateManager.UpdateVertexBuffers(vertexBuffers);
        }

        public void SetViewports(ReadOnlySpan<Viewport> viewports)
        {
            _encoderStateManager.UpdateViewports(viewports);
        }

        // A/B switch for the skip below, re-read once a frame so both behaviours can be
        // measured inside one session - this machine's white-frame rate swings with the
        // camera, so comparing separate runs measures the view, not the change.
        private static bool _strictBarrier;

        // Diagnostic hammer: end the render pass after every draw, so no draw can ever
        // read a texture another draw in the same pass wrote. If the white frames
        // survive full serialisation the fault cannot be a read-after-write ordering
        // problem, and has to be inside the shader itself.
        private static bool _serializeDraws;

        // Bisect: drop every draw whose colour target 0 is the full resolution composite.
        // If the frame still comes out flat white with all of them gone, the white was
        // already in that texture when the passes loaded it, and nothing they draw is
        // responsible - which is the half of the search space no instrument here has been
        // able to separate. Hot-swappable so both arms run in one session.
        private static bool _skipHdrDraws;

        // Per shader bisect. Every externally observable quantity matches between flat and
        // ordinary frames, so the difference is inside shader execution; dropping one
        // shader's draws at a time is what names which one. Hot-swappable, so all arms run
        // in the session that reproduces the fault.
        private static string _skipShader = string.Empty;

        // Diagnostic hammer, stronger than _serializeDraws: commit the command buffer and
        // block until the GPU has finished it before the watched draw. That orders the
        // watched read after every write already recorded, across encoders AND across
        // command buffers. If the white frames survive this, no ordering fix can help -
        // the shader is being handed the wrong contents, not stale ones.
        private static bool _hardSync;
        private static bool _stainPresentSource;
        private static bool _stainBeforePresent;

        // Sweep: stain the present source after the Kth draw of the frame, with K stepping
        // frame by frame. The frame comes back green only if nothing wrote the source
        // after draw K, so the K where green stops is the last writer's position. One run
        // covers the whole range, which matters because the trigger view lasts ~2 minutes.
        private static bool _stainSweep;
        private static int _stainAtDraw = -1;
        private static ulong _drawCountAtFrameStart;
        private Texture _lastPresentSource;

        // The surface presented last time. The present source alternates between a couple
        // of textures, so the other one already holds the previous frame - no copy needed.
        private Texture _guardPreviousSource;

        internal static void RefreshBarrierToggle()
        {
            try
            {
                _strictBarrier = System.IO.File.Exists("/tmp/ryujinx-metal-strict-barrier") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-strict-barrier").Trim() == "1";

                _serializeDraws = System.IO.File.Exists("/tmp/ryujinx-metal-serialize") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-serialize").Trim() == "1";

                _skipHdrDraws = System.IO.File.Exists("/tmp/ryujinx-metal-skip-hdr") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-skip-hdr").Trim() == "1";

                _skipShader = System.IO.File.Exists("/tmp/ryujinx-metal-skip-shader")
                    ? System.IO.File.ReadAllText("/tmp/ryujinx-metal-skip-shader").Trim()
                    : string.Empty;

                _hardSync = System.IO.File.Exists("/tmp/ryujinx-metal-hardsync") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-hardsync").Trim() == "1";

                HdrPassProbe.SetSkipToneDraw(System.IO.File.Exists("/tmp/ryujinx-metal-skiptone") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-skiptone").Trim() == "1");

                HdrPassProbe.SetSkipPresentWrites(System.IO.File.Exists("/tmp/ryujinx-metal-skippresent") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-skippresent").Trim() == "1");

                _stainPresentSource = System.IO.File.Exists("/tmp/ryujinx-metal-stain") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-stain").Trim() == "1";

                _stainBeforePresent = System.IO.File.Exists("/tmp/ryujinx-metal-stainpre") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-stainpre").Trim() == "1";

                _stainSweep = System.IO.File.Exists("/tmp/ryujinx-metal-stainsweep") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-stainsweep").Trim() == "1";

                if (System.IO.File.Exists("/tmp/ryujinx-metal-skiprange"))
                {
                    string[] parts = System.IO.File.ReadAllText("/tmp/ryujinx-metal-skiprange").Trim().Split(' ');

                    if (parts.Length == 2 &&
                        int.TryParse(parts[0], out int lo) &&
                        int.TryParse(parts[1], out int hi))
                    {
                        HdrPassProbe.SetSkipRange(lo, hi);
                    }
                }
                else
                {
                    HdrPassProbe.SetSkipRange(-1, -1);
                }
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; the next frame picks it up.
            }
        }

        public void TextureBarrier()
        {
            UploadCorrelator.NoteBarrierRequested();
            UploadCorrelator.Seq("TB");

            if (CurrentEncoderType != EncoderType.Render)
            {
                return;
            }

            // The barrier orders fragment writes before later fragment reads. Writes
            // made by earlier passes are already ordered by the boundary that ended
            // them, so with no draw encoded since this pass began there is nothing
            // for it to order - and splitting anyway costs a full attachment store
            // and reload. The guest issues these in the hundreds per frame.
            if (!_strictBarrier && DrawCount == _drawCountAtPassStart)
            {
                _passEndReasons[(int)PassEndReason.FragmentDependencySkipped]++;
                UploadCorrelator.NoteBarrierSkipped();

                return;
            }

            // A barrier that orders nothing can be skipped. The guest issues these in the
            // hundreds per frame; ending the pass for one whose bound textures were not
            // written by the pass now running costs a full attachment store and reload and
            // orders a dependency that does not exist. This was the default in v419-v482;
            // RYUJINX_METAL_BARRIER_SCOPE=hazard now explicitly opts into this heuristic. It
            // is only meaningful together with RAW_SPLIT_SCOPE=pass (also the default),
            // which makes the write set per pass rather than per command buffer.
            // The draw-time check runs for the next draw regardless, so ending the pass
            // here only pays off for a hazard that check would also split on. None: the
            // pass wrote nothing the bound textures read. Fetchable: the read is served
            // from tile memory by a fetch variant, or falls back to the draw-time split.
            // A SelfOnly hazard is different: the draw-time path lets that colour
            // self-read through stale by policy, and the stale value is only tolerable
            // while memory is fresh - which is exactly what the guest's barrier used to
            // guarantee by ending the pass. Skipping it too handed the decals a G-buffer
            // that had not been stored since a pass or more back, tile-partial on this
            // GPU: black polygons flickering with primitive order at the user's save
            // (2026-09-03). So a game-issued barrier over a SelfOnly read stands. (This
            // call also feeds the classification counters, so in hazard mode they count
            // barrier sites as well as draws.)
            // The texture check sees attachments only. A fragment stage that stored to a
            // storage buffer in this pass is a dependency it cannot see, and one this GPU
            // cannot order inside a pass at all: a later draw's vertex work runs before
            // any of the pass's fragment work, and fragment-to-fragment has no memory
            // barrier. 1130 of the game's fragment shaders store (the visibility feedback
            // flags), the decals among them, and skipping their barrier is what painted
            // the flickering black footprints at the user's save. So once the pass holds
            // a fragment store the guest's barrier ends it, whatever the textures say.
            if (_barrierDeferred && _rawSplit && !_passHasFragmentStore)
            {
                // Deferred: mark the pass's writes and let their consumer split. See the
                // field comment. Blit and compute consumers already end the render pass by
                // changing encoder, so only same-pass draws need the mark.
                _encoderStateManager.NoteGuestBarrier();
                _passEndReasons[(int)PassEndReason.FragmentDependencyDeferred]++;
                UploadCorrelator.NoteBarrierSkipped();

                return;
            }

            if (_barrierHazardOnly && !_passHasFragmentStore)
            {
                EncoderStateManager.RawHazard barrierHazard = _encoderStateManager.SamplesEarlierWrite();

                if (barrierHazard == EncoderStateManager.RawHazard.None ||
                    barrierHazard == EncoderStateManager.RawHazard.Fetchable)
                {
                    _passEndReasons[(int)PassEndReason.FragmentDependencySkipped]++;
                    UploadCorrelator.NoteBarrierSkipped();

                    return;
                }
            }
            else if (_barrierHazardOnly)
            {
                // Kept for the fragment store: count how often the bindings at this point
                // really overlap what was stored, which is what a buffer-identity skip
                // would look at instead.
                _encoderStateManager.NoteStorageRawAtBarrier();
            }

            // A fragment-writes-then-fragment-reads dependency cannot be expressed
            // as a render encoder barrier on Apple GPUs (afterStages must not name
            // fragment). Ending the pass is the only construct that actually orders
            // the two, and an illegal barrier here orders nothing at all.
            EndCurrentPass(PassEndReason.FragmentDependency);
        }

        public void TextureBarrierTiled()
        {
            TextureBarrier();
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool isEqual)
        {
            // TODO: Implementable via indirect draw commands
            return false;
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual)
        {
            // TODO: Implementable via indirect draw commands
            return false;
        }

        public void EndHostConditionalRendering()
        {
            // TODO: Implementable via indirect draw commands
        }

        public void BeginTransformFeedback(PrimitiveTopology topology)
        {
            // Metal does not support transform feedback.
        }

        public void EndTransformFeedback()
        {
            // Metal does not support transform feedback.
        }

        public void SetTransformFeedbackBuffers(ReadOnlySpan<BufferRange> buffers)
        {
            // Metal does not support transform feedback.
        }

        public void Dispose()
        {
            EndCurrentPass(PassEndReason.Dispose);
            _encoderStateManager.Dispose();
        }
    }
}
