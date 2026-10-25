using Ryujinx.Common.Logging;
using SharpMetal;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Trigger-file driven programmatic GPU capture. Touch the trigger file while a
    /// game is running and the next full frame is written as a .gputrace document
    /// (openable in Xcode), together with a per-draw log of that frame. The Metal
    /// runtime only allows GPU trace documents when the process was launched with
    /// METAL_CAPTURE_ENABLED=1.
    /// </summary>
    [SupportedOSPlatform("macos")]
    class FrameCapture
    {
        private const string TriggerPath = "/tmp/ryujinx-metal-capture";
        private const string TraceTriggerPath = "/tmp/ryujinx-metal-trace";

        /// <summary>
        /// Where the draw/dispatch trace dumps the MSL source of every program it
        /// sees, named {DebugLabel}-{stage}.metal.
        /// </summary>
        public const string ShaderDumpDir = "/tmp/ryujinx-metal-shaders";

        // Every prior capture attempt wedged the frame pipeline before reaching the
        // next present, so the capture never ended and the app had to be killed.
        // The watchdog force-stops the capture from a timer thread instead.
        //
        // Three seconds is not enough for a queue-scope capture of a frame this size: it
        // fired mid-write every time, leaving multi-GB bundles Xcode refuses to open,
        // while the scope-only captures that did finish covered just the tail of a frame
        // - UI and minimap, with the scene already drawn. Overridable so a full frame can
        // be captured without rebuilding.
        private static readonly int WatchdogMilliseconds =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_WATCHDOG_MS"), out int ms) && ms > 0
                ? ms
                : 3000;

        private readonly object _lock = new();
        private readonly MTLCommandQueue _queue;
        private bool _capturing;
        private int _captureGeneration;
        private Timer _watchdog;
        private int _traceFramesRemaining;
        private bool _supportChecked;
        private bool _supported;
        private string _outputPath;

        public bool DrawTraceActive => _capturing || _traceFramesRemaining > 0;

        /// <summary>
        /// Command buffer the capture should be scoped to, set by the pipeline before
        /// the capture starts. Zero falls back to queue scope.
        /// </summary>
        public MTLCommandBuffer CurrentCommandBuffer;

        private MTLCaptureScope _scope;

        /// <summary>
        /// True while a scoped capture is running and its scope has not been used yet.
        /// </summary>
        public bool ScopeReady => _capturing && _scope.NativePtr != IntPtr.Zero;

        /// <summary>
        /// Opens the capture scope, if a scoped capture is running. Draws encoded
        /// between this and <see cref="EndScope"/> are what the trace contains.
        /// </summary>
        public void BeginScope()
        {
            if (_capturing && _scope.NativePtr != IntPtr.Zero)
            {
                _scope.BeginScope();
            }
        }

        public void EndScope()
        {
            if (_capturing && _scope.NativePtr != IntPtr.Zero)
            {
                _scope.EndScope();
            }
        }

        public FrameCapture(MTLCommandQueue queue)
        {
            _queue = queue;
        }

        /// <summary>
        /// Called at the start of presentation, before the drawable is touched.
        /// Ends an active capture here so the capture window never includes drawable
        /// acquisition or presentation: capturing across a present exhausted the
        /// layer's drawable pool and wedged the frame pipeline (observed with both
        /// device- and queue-scoped captures).
        /// </summary>
        public void OnPresentBegin()
        {
            lock (_lock)
            {
                if (_capturing)
                {
                    MTLCaptureManager.SharedCaptureManager().StopCapture();
                    _capturing = false;
                    _captureGeneration++;

                    _watchdog?.Dispose();
                    _watchdog = null;

                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"Metal frame capture finished: {_outputPath}");
                }
            }
        }

        private void WatchdogStop(int generation)
        {
            lock (_lock)
            {
                if (_capturing && generation == _captureGeneration)
                {
                    MTLCaptureManager.SharedCaptureManager().StopCapture();
                    _capturing = false;
                    _captureGeneration++;

                    Logger.Warning?.PrintMsg(
                        LogClass.Gpu,
                        $"Metal frame capture watchdog fired after {WatchdogMilliseconds}ms without reaching the next present; capture stopped: {_outputPath}");
                }
            }
        }

        /// <summary>
        /// Called once per presented frame, after the present was queued. Starts a
        /// capture when the trigger file exists; it ends at the next OnPresentBegin.
        /// The draw-trace trigger logs the draws of the next frames without touching
        /// MTLCaptureManager at all, so it cannot disturb the frame pipeline.
        /// </summary>
        public void ProcessPresent()
        {
            if (_traceFramesRemaining > 0 && --_traceFramesRemaining == 0)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, "Metal draw trace finished.");
            }

            if (_traceFramesRemaining == 0 && TryConsumeTrigger(TraceTriggerPath))
            {
                _traceFramesRemaining = 3;

                Logger.Warning?.PrintMsg(LogClass.Gpu, "Metal draw trace started for 3 frames.");

                return;
            }

            if (!TryConsumeTrigger(TriggerPath))
            {
                return;
            }

            MTLCaptureManager manager = MTLCaptureManager.SharedCaptureManager();

            if (!_supportChecked)
            {
                _supportChecked = true;
                _supported = manager.SupportsDestination(MTLCaptureDestination.GPUTraceDocument);

                if (!_supported)
                {
                    Logger.Warning?.PrintMsg(
                        LogClass.Gpu,
                        "Metal GPU trace capture is not available. Launch with METAL_CAPTURE_ENABLED=1 to enable it.");
                }
            }

            if (!_supported)
            {
                return;
            }

            StartGpuTrace(manager);
        }

        private static bool TryConsumeTrigger(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // If the trigger cannot be deleted, do not fire endlessly.
                return false;
            }

            return true;
        }

        private void StartGpuTrace(MTLCaptureManager manager)
        {

            _outputPath = $"/tmp/ryujinx-metal-{DateTime.Now:yyyyMMdd-HHmmss}.gputrace";

            NSString outputString = StringHelper.NSString(_outputPath);
            NSURL outputUrl = new(ObjectiveCRuntime.IntPtr_objc_msgSend(new ObjectiveCClass("NSURL"), (Selector)"fileURLWithPath:", outputString.NativePtr));

            // Queue-scope captures record every command buffer submitted until the
            // capture stops. This backend encodes hundreds of thousands of render
            // passes per frame, so a whole-frame capture produces hundreds of MB and
            // the GPU tools have been observed to crash while finalising it (leaving
            // an "unsorted-capture" bundle Xcode refuses to open). Capturing a single
            // command buffer keeps the trace small enough to finalise.
            // RYUJINX_METAL_CAPTURE_QUEUE=1 restores queue scope.
            bool queueScope = Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_QUEUE") == "1";

            // A capture scope lets the trace cover just the draws of interest instead of
            // a whole frame. This backend encodes hundreds of thousands of render
            // passes per frame, and a whole-frame trace is large enough that the GPU
            // tools crash while finalising it. RYUJINX_METAL_CAPTURE_QUEUE=1 restores
            // the old queue-wide behaviour.
            if (!queueScope)
            {
                _scope = manager.NewCaptureScope(_queue);
            }

            MTLCaptureDescriptor descriptor = new()
            {
                CaptureObject = queueScope ? _queue : _scope,
                Destination = MTLCaptureDestination.GPUTraceDocument,
                OutputURL = outputUrl,
            };

            NSError error = new(IntPtr.Zero);

            if (manager.StartCapture(descriptor, ref error))
            {
                lock (_lock)
                {
                    _capturing = true;

                    int generation = ++_captureGeneration;

                    _watchdog?.Dispose();
                    _watchdog = new Timer(_ => WatchdogStop(generation), null, WatchdogMilliseconds, Timeout.Infinite);
                }

                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Metal frame capture started: {_outputPath}");
            }
            else
            {
                string message = error.NativePtr != IntPtr.Zero ? error.LocalizedDescription.ToString() : "unknown error";

                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Metal frame capture failed to start: {message}");
            }
        }
    }
}
