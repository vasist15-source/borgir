using Ryujinx.Common.Logging;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal;
using SharpMetal.ObjectiveCCore;
using System;
using System.IO;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Captures a GPU trace of a frame that actually came out white.
    ///
    /// Two things made the existing capture path unproductive, and neither was a limit
    /// of the tool:
    ///
    ///   - The frame was random. Flat frames are about 40% of them at the reproducing
    ///     save, so most traces were of healthy frames and nothing recorded which kind
    ///     had been caught. This classifies the captured frame and throws the trace away
    ///     unless it was flat, retrying until one lands - so every file that survives is
    ///     a picture of the fault.
    ///
    ///   - The window held no whole command buffer. Metal records a capture only for
    ///     command buffers that are both created and committed inside it - "GPU Capture
    ///     is empty: at least one command buffer must be created and committed within
    ///     the boundaries of a GPU Capture" - and the first version of this bracketed
    ///     just the pass, whose command buffer was created before and committed after.
    ///     It produced a well formed, entirely empty 2.6 MB trace. The window now opens
    ///     on a flush, so the next command buffer is born inside it, and closes on
    ///     another flush that commits that same command buffer. Exactly one command
    ///     buffer is recorded, holding the failing pass - which is also what keeps the
    ///     trace small, against the whole-frame captures that ran to hundreds of
    ///     megabytes and crashed the tools while finalising.
    ///
    /// Attempts are rare on purpose. A real capture costs about 1.3 seconds a frame -
    /// arming one every frame took the game to 0.77 fps and it could not be played into
    /// at all. So the hunter runs a cheap sample every frame with no sync and no
    /// capture, watches for a frame that came out flat, and only then arms an attempt on
    /// the next one: flat frames arrive in bursts, so the frame after a flat one is the
    /// cheapest place to spend a capture. Between attempts there is a cooldown, and the
    /// whole thing gives up after a bounded number of tries rather than degrading the
    /// session indefinitely.
    ///
    /// RYUJINX_METAL_CAPTURE_FLAT=1, and the process must be launched with
    /// METAL_CAPTURE_ENABLED=1 for GPU trace documents to be permitted.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class CaptureHunter
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_FLAT") == "1";

        private const int GridSide = 5;
        private const int Pixels = GridSide * GridSide;
        private const int BytesPerPixel = 4;

        // FlashGuard's calibrated criterion, so "flat" here means what it means
        // everywhere else in this investigation.
        private const float SaturatedLuma = 235f;
        private const int SaturatedNeeded = 6;

        // Give the picture time to be a picture. Attempts during load-in would capture a
        // legitimately white fade and report success.
        private static readonly int _startAfterFrames =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_AFTER"), out int after) && after > 0
                ? after
                : 900;

        private static MTLCommandQueue _queue;
        private static MTLBuffer _samples;

        private static bool _capturing;
        private static bool _closePending;
        private static bool _awaitingVerdict;
        private static bool _armedThisFrame;
        private static bool _done;
        private static int _attempts;
        private static int _frame;
        private static int _cooldownUntil;
        private static string _outputPath;

        // Cheap per-frame watch: sample, keep the fence, read it back whenever it has
        // signalled. Never waits, so it costs nothing while the hunter is idle.
        private const int WatchSlots = 8;
        private const int Cooldown = 60;

        private static readonly int _maxAttempts =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_TRIES"), out int tries) && tries > 0
                ? tries
                : 40;

        private static MTLBuffer _watch;
        private static readonly FenceHolder[] _watchFence = new FenceHolder[WatchSlots];

        public static void Init(MTLDevice device, MTLCommandQueue queue)
        {
            if (!Enabled)
            {
                return;
            }

            _queue = queue;
            _samples = device.NewBuffer(Pixels * BytesPerPixel, MTLResourceOptions.ResourceStorageModeShared);
            _watch = device.NewBuffer(WatchSlots * Pixels * BytesPerPixel, MTLResourceOptions.ResourceStorageModeShared);

            bool supported = MTLCaptureManager.SharedCaptureManager()
                .SupportsDestination(MTLCaptureDestination.GPUTraceDocument);

            Logger.Warning?.PrintMsg(LogClass.Gpu,
                supported
                    ? $"capture hunter armed: watching from frame {_startAfterFrames}, arming an attempt only after a flat frame, at most {_maxAttempts} tries"
                    : "capture hunter: GPU trace documents unavailable - relaunch with METAL_CAPTURE_ENABLED=1");

            if (!supported)
            {
                _done = true;
            }
        }

        /// <summary>
        /// Called before the pass that samples the scene texture opens, with no pass
        /// currently open so starting a capture is legal. Starts the trace and opens the
        /// scope around that one pass.
        /// </summary>
        public static void OnSceneSamplingPassBegin(ulong drawCount)
        {
            if (!Enabled || _done || !_armedThisFrame || _capturing || _drawsThisFrame < 1200)
            {
                return;
            }

            MTLCaptureManager manager = MTLCaptureManager.SharedCaptureManager();

            _outputPath = $"/tmp/ryujinx-flat-{_attempts:D3}.gputrace";

            try
            {
                if (Directory.Exists(_outputPath))
                {
                    Directory.Delete(_outputPath, true);
                }
            }
            catch (IOException)
            {
                // A leftover we cannot remove just means the next attempt picks a new name.
            }

            NSString outputString = StringHelper.NSString(_outputPath);
            NSURL outputUrl = new(ObjectiveCRuntime.IntPtr_objc_msgSend(
                new ObjectiveCClass("NSURL"), (Selector)"fileURLWithPath:", outputString.NativePtr));

            MTLCaptureDescriptor descriptor = new()
            {
                // Queue scope, not a capture scope. The window is bounded by the flushes
                // the caller performs either side, so exactly one command buffer lives
                // and dies inside it - which is the condition Metal actually requires.
                CaptureObject = _queue,
                Destination = MTLCaptureDestination.GPUTraceDocument,
                OutputURL = outputUrl,
            };

            NSError error = new(IntPtr.Zero);

            if (!manager.StartCapture(descriptor, ref error))
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"capture hunter: start failed - {(error.NativePtr != IntPtr.Zero ? error.LocalizedDescription.ToString() : "unknown")}");

                _done = true;

                return;
            }

            _capturing = true;
            _presentSinceOpen = false;
            _closePending = true;
            _passEnded = false;
            _passesWithDraws = 0;
            _drawCountAtStart = drawCount;
        }

        /// <summary>
        /// True once the watched pass has ended and the capture is waiting for the
        /// caller to commit its command buffer and close the window. The caller has to
        /// drive that from a point where flushing is legal, never from inside encoder
        /// acquisition.
        /// </summary>
        public static bool ClosePending => _capturing && _closePending && _passEnded;

        private static bool _passEnded;
        private static ulong _drawCountAtStart;
        private static ulong _capturedDraws;

        /// <summary>
        /// Called when a render pass ends, with the running draw count. Closing on the
        /// first pass end regardless produced a capture holding one command buffer, one
        /// blit encoder and zero draws: between opening the window and the render pass
        /// actually being encoded, something else ends a pass, and the window shut before
        /// any drawing reached it. Only a pass that carried draws counts.
        /// </summary>
        private static ulong _lastDrawCount;
        private static bool _presentSinceOpen;

        public static void OnPassEnd(ulong drawCount)
        {
            _lastDrawCount = drawCount;
            // Close only when the frame that opened the capture has presented, so the
            // trace holds the whole frame including the presenting blit at its very end -
            // twelve passes from the first scene sample was a slice from the middle of the
            // frame and never contained it (19 draws of 2,500 in the kept trace).
            if (_capturing && _closePending && _presentSinceOpen && drawCount > _drawCountAtStart)
            {
                _passEnded = true;
            }
        }

        private static int _passesWithDraws;

        // Closing after the first drawing pass caught a clear and one draw pass and
        // stopped short of whatever writes the white. The full resolution chain is
        // several passes long, so keep a few.
        private static readonly int _passesToKeep =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_PASSES"), out int keep) && keep > 0
                ? keep
                : 12;

        /// <summary>
        /// Called after the caller has flushed, so the command buffer holding the
        /// watched pass is committed inside the window.
        /// </summary>
        public static void CloseWindow()
        {
            if (!_capturing)
            {
                return;
            }

            _capturedDraws = _lastDrawCount - _drawCountAtStart;
            MTLCaptureManager.SharedCaptureManager().StopCapture();
            _closePending = false;
            _passEnded = false;
        }

        /// <summary>
        /// Encodes the classification samples. Called at present, before the command
        /// buffer is committed.
        /// </summary>
        public static void SamplePresentSource(CommandBufferScoped cbs, Texture src)
        {
            if (!Enabled || _done || src == null || _samples.NativePtr == IntPtr.Zero)
            {
                return;
            }

            MTLTexture tex = src.GetHandle(cbs);

            if (tex.NativePtr == IntPtr.Zero)
            {
                return;
            }

            MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();
            int watchSlot = _frame % WatchSlots;

            for (int i = 0; i < Pixels; i++)
            {
                MTLOrigin origin = new()
                {
                    x = (ulong)(src.Width * (i % GridSide + 1) / (GridSide + 1)),
                    y = (ulong)(src.Height * (i / GridSide + 1) / (GridSide + 1)),
                    z = 0,
                };

                MTLSize one = new() { width = 1, height = 1, depth = 1 };

                blit.CopyFromTexture(tex, 0, 0, origin, one, _watch,
                    (ulong)((watchSlot * Pixels + i) * BytesPerPixel), BytesPerPixel, BytesPerPixel);

                // Also on the frame after a capture: the verdict is taken there, not on
                // the captured frame.
                if (_armedThisFrame || _awaitingVerdict)
                {
                    blit.CopyFromTexture(tex, 0, 0, origin, one, _samples,
                        (ulong)(i * BytesPerPixel), BytesPerPixel, BytesPerPixel);
                }
            }

            _watchFence[watchSlot]?.Put();
            _watchFence[watchSlot] = cbs.GetFence();
            _watchFence[watchSlot].Get();
        }

        private static unsafe bool IsFlat(byte* p)
        {
            int saturated = 0;

            for (int i = 0; i < Pixels; i++)
            {
                byte* px = p + i * BytesPerPixel;

                if ((px[0] + px[1] + px[1] + px[2]) * 0.25f >= SaturatedLuma)
                {
                    saturated++;
                }
            }

            return saturated >= SaturatedNeeded;
        }

        /// <summary>
        /// Any completed watch slot that came out flat, read without waiting. Flat frames
        /// arrive in bursts, so this is the signal that an attempt is worth its cost.
        /// </summary>
        private static unsafe bool RecentFlatFrame()
        {
            bool any = false;

            for (int i = 0; i < WatchSlots; i++)
            {
                if (_watchFence[i] == null || !_watchFence[i].IsSignaled())
                {
                    continue;
                }

                if (IsFlat((byte*)_watch.Contents + i * Pixels * BytesPerPixel))
                {
                    any = true;
                }

                _watchFence[i].Put();
                _watchFence[i] = null;
            }

            return any;
        }

        /// <summary>
        /// Called after the frame's command buffer has completed. Decides whether the
        /// frame that was just captured was flat, keeps the trace if it was, and arms the
        /// next attempt if it was not.
        /// </summary>
        private static ulong _lastDrawTotal;
        private static ulong _drawsThisFrame;

        /// <summary>
        /// Draws issued in the frame that just ended. Two KEPT captures were menus and a
        /// loading screen - the flat criterion cannot tell a white loading fade from the
        /// fault, but the draw count can: gameplay issues ~2,500 draws a frame, loading
        /// and menus a few dozen. Fed from the pipeline at present.
        /// </summary>
        public static void NoteFrameDraws(ulong cumulativeDraws)
        {
            _drawsThisFrame = cumulativeDraws - _lastDrawTotal;
            _lastDrawTotal = cumulativeDraws;
        }

        public static unsafe void Decide()
        {
            if (!Enabled || _done)
            {
                return;
            }

            _frame++;

            if (_capturing)
            {
                // The frame that opened the capture has now presented: the blit is in the
                // trace. Close at this present rather than waiting for another pass end.
                _presentSinceOpen = true;
                _passEnded = true;
            }

            // The verdict on a capture is taken one frame late, deliberately. The
            // presented surface was last a colour attachment one frame before it is
            // shown - measured, not assumed: 1461 flat and 3911 normal frames at age 1
            // against 1 and 26 at age 0 - so the white is written into it during frame
            // N-1 and displayed at frame N. Every capture taken on the frame that
            // *displays* white therefore holds the frame after the crime. This is the
            // same one-frame error the ledger already recorded once for the in-frame
            // probes, repeated here with a GPU capture.
            if (_awaitingVerdict)
            {
                _awaitingVerdict = false;
                _attempts++;

                if (Verdict(out int saturated, out double meanLuma))
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu,
                        $"capture hunter: KEPT the frame before a flat one after {_attempts} attempts " +
                        $"(next frame saturated {saturated}/{Pixels}, mean luma {meanLuma:F0}): {_outputPath}");

                    _done = true;

                    return;
                }

                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"capture hunter: attempt {_attempts} - the next frame was normal " +
                    $"(saturated {saturated}/{Pixels}, mean luma {meanLuma:F0}), discarding");

                Discard();
            }

            if (_capturing)
            {
                if (_closePending)
                {
                    // The pass never ended before present; close here so the trace is
                    // still finalised rather than left running into the next frame.
                    CloseWindow();
                }

                _capturing = false;
                _awaitingVerdict = true;
                _armedThisFrame = false;

                return;
                _attempts++;

            }

            if (_attempts >= _maxAttempts)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"capture hunter: giving up after {_maxAttempts} attempts without catching a flat frame");

                _done = true;
                _armedThisFrame = false;

                return;
            }

            // Arm only when the fault is already firing and the cooldown has expired, so
            // an idle session runs at full speed. One frame at a time: capturing across a
            // present exhausts the layer's drawable pool and wedges the frame pipeline.
            bool burst = RecentFlatFrame();

            _armedThisFrame = _frame >= _startAfterFrames && _frame >= _cooldownUntil && burst;

            if (_armedThisFrame)
            {
                _cooldownUntil = _frame + Cooldown;
            }
        }

        public static bool WantsSyncThisFrame => Enabled && !_done && (_armedThisFrame || _awaitingVerdict);

        private static unsafe bool Verdict(out int saturated, out double meanLuma)
        {
            byte* p = (byte*)_samples.Contents;
            saturated = 0;
            double lumaSum = 0;

            for (int i = 0; i < Pixels; i++)
            {
                byte* px = p + i * BytesPerPixel;
                double luma = (px[0] + px[1] + px[1] + px[2]) * 0.25;

                lumaSum += luma;

                if (luma >= SaturatedLuma)
                {
                    saturated++;
                }
            }

            meanLuma = lumaSum / Pixels;

            // A KEPT capture on Aug 15 turned out to be a 28-draw black loading frame with a
            // bright HUD: six saturated samples of twenty-five is not a white frame. The
            // real fault is a uniform fill - the correlator measured min 246+ / sd ~2 on
            // every one - so demand the grid be white almost everywhere, and demand the
            // captured frame itself was full gameplay (draw count), which the black frame
            // was not.
            // The draw-count guard killed two genuine 25/25 luma-254 verdicts on its first
            // run - the count is not plumbed reliably through this path. 22/25 saturated at
            // mean 235+ already excludes the HUD-over-black false positive on its own.
            // The draw gate belongs at ARMING, not here: by verdict time _drawsThisFrame
            // describes a later frame, and it discarded twenty genuine 25/25 luma-254
            // verdicts in one run. Arming already refuses non-gameplay frames.
            return saturated >= 22 && meanLuma >= 235;
        }

        private static void Discard()
        {
            try
            {
                if (Directory.Exists(_outputPath))
                {
                    Directory.Delete(_outputPath, true);
                }
            }
            catch (IOException)
            {
                // Leaving one behind is harmless; the name carries the attempt number.
            }
        }

        /// <summary>
        /// True when a capture is about to be started, so the caller knows to flush the
        /// current command buffer first and let the next one be born inside the window.
        /// </summary>
        public static bool WantsStart => Enabled && !_done && _armedThisFrame && !_capturing;

        // The exact surfaces that have recently been presented, by storage identity.
        // Four aiming heuristics failed in a row - by sampled texture (dynamic
        // resolution had the scene at 800x448, under the width floor), by output width
        // (the drawable is 2560x1406 and matched first), and by widening the window -
        // because every property they keyed on moves. The handle of the texture that
        // actually reached the screen does not, so aim with that instead. Both surfaces
        // are kept because presentation alternates between two of them.
        private static readonly IntPtr[] _presentedRoots = new IntPtr[4];
        private static int _presentedCount;

        public static void NotePresentSource(Texture src)
        {
            if (!Enabled || src == null)
            {
                return;
            }

            IntPtr root = src.CanonicalPtr;

            for (int i = 0; i < _presentedRoots.Length; i++)
            {
                if (_presentedRoots[i] == root)
                {
                    return;
                }
            }

            _presentedRoots[_presentedCount++ % _presentedRoots.Length] = root;
        }

        /// <summary>
        /// True when this render target is a surface that has actually been presented -
        /// the one whose contents come out white.
        /// </summary>
        public static bool IsPresentedSurface(Texture target)
        {
            if (target == null)
            {
                return false;
            }

            foreach (IntPtr root in _presentedRoots)
            {
                if (root != IntPtr.Zero && root == target.CanonicalPtr)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
