using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Schedules proactive command buffer submissions so that host sync waits find
    /// work that has already been submitted (and ideally completed) by the GPU.
    /// Mirrors the Vulkan backend's AutoFlushCounter: without it, deferred syncs keep
    /// their fences on the command buffer that is still being recorded, so every
    /// guest-side wait pays the full submission and execution latency at once.
    /// </summary>
    [SupportedOSPlatform("macos")]
    class AutoFlushCounter
    {
        // How often to flush on framebuffer change.
        private static readonly long _framebufferFlushTimer = Stopwatch.Frequency / 1000; // (1ms)

        // How often to flush on draw when fast flush mode is enabled.
        private static readonly long _drawFlushTimer = Stopwatch.Frequency / 666; // (1.5ms)

        // Average wait time that triggers fast flush mode to be entered.
        private static readonly long _fastFlushEnterThreshold = Stopwatch.Frequency / 666; // (1.5ms)

        // Average wait time that triggers fast flush mode to be exited.
        private static readonly long _fastFlushExitThreshold = Stopwatch.Frequency / 10000; // (0.1ms)

        // Number of frames to average waiting times over.
        private const int SyncWaitAverageCount = 20;

        private const int MinDrawCountForFlush = 10;

        /// <summary>
        /// Auto-flush mode from RYUJINX_METAL_AUTO_FLUSH:
        /// 0 disables it (v27 behaviour), 1 is adaptive (default), 2 forces fast flush.
        /// Read once so SyncManager can pick its deferred-sync defaults consistently.
        /// </summary>
        public static int ConfiguredMode { get; } = GetConfiguredMode();

        public static bool ConfiguredEnabled => ConfiguredMode != 0;

        private readonly MetalRenderer _renderer;

        private long _lastFlush;
        private ulong _lastDrawCount;

        private readonly long[] _syncWaitHistory = new long[SyncWaitAverageCount];
        private int _syncWaitHistoryIndex;

        private bool _fastFlushMode;

        public bool Enabled { get; }
        public bool FastFlushMode => _fastFlushMode;

        /// <summary>The draw count at the last flush, whatever triggered it.</summary>
        public ulong LastFlushDrawCount => _lastDrawCount;

        public AutoFlushCounter(MetalRenderer renderer)
        {
            _renderer = renderer;
            Enabled = ConfiguredMode != 0;
            _fastFlushMode = ConfiguredMode == 2;

            Logger.Info?.PrintMsg(
                LogClass.Gpu,
                $"Metal auto-flush mode: {ModeName(ConfiguredMode)}. " +
                "Set RYUJINX_METAL_AUTO_FLUSH to 0 (off), 1 (adaptive) or 2 (forced fast flush) for A/B testing.");
        }

        public void RegisterFlush(ulong drawCount)
        {
            _lastFlush = Stopwatch.GetTimestamp();
            _lastDrawCount = drawCount;
        }

        public bool ShouldFlushDraw(ulong drawCount)
        {
            if (!Enabled || !_fastFlushMode)
            {
                return false;
            }

            long draws = (long)(drawCount - _lastDrawCount);

            if (draws < MinDrawCountForFlush)
            {
                if (draws == 0)
                {
                    _lastFlush = Stopwatch.GetTimestamp();
                }

                return false;
            }

            long now = Stopwatch.GetTimestamp();

            return now > _lastFlush + _drawFlushTimer;
        }

        /// <summary>
        /// Time-based submission bound for upload/compute heavy stretches (asset
        /// streaming), where draws and attachment changes are too rare to drive the
        /// cadence. Only fires outside an active render pass, so normal rendering
        /// never gets split by it; the caller checks the current encoder type.
        /// </summary>
        public bool ShouldFlushDeferredSync()
        {
            if (!Enabled)
            {
                return false;
            }

            long now = Stopwatch.GetTimestamp();

            return now > _lastFlush + _framebufferFlushTimer;
        }

        public bool ShouldFlushAttachmentChange(ulong drawCount)
        {
            if (!Enabled)
            {
                return false;
            }

            long draws = (long)(drawCount - _lastDrawCount);

            if (draws < MinDrawCountForFlush)
            {
                if (draws == 0)
                {
                    _lastFlush = Stopwatch.GetTimestamp();
                }

                return false;
            }

            long now = Stopwatch.GetTimestamp();

            return now > _lastFlush + _framebufferFlushTimer;
        }

        public void Present()
        {
            if (!Enabled)
            {
                return;
            }

            _syncWaitHistory[_syncWaitHistoryIndex] = _renderer.SyncManager.GetAndResetAutoFlushWaitTicks();

            _syncWaitHistoryIndex = (_syncWaitHistoryIndex + 1) % SyncWaitAverageCount;

            if (ConfiguredMode == 2)
            {
                return;
            }

            long averageWait = (long)_syncWaitHistory.Average();

            if (_fastFlushMode ? averageWait < _fastFlushExitThreshold : averageWait > _fastFlushEnterThreshold)
            {
                _fastFlushMode = !_fastFlushMode;
                Logger.Debug?.PrintMsg(LogClass.Gpu, $"Metal fast flush mode switched: ({_fastFlushMode})");
            }
        }

        private static string ModeName(int mode)
        {
            return mode switch
            {
                0 => "disabled",
                2 => "forced fast flush",
                _ => "adaptive",
            };
        }

        private static int GetConfiguredMode()
        {
            string value = Environment.GetEnvironmentVariable("RYUJINX_METAL_AUTO_FLUSH");

            if (int.TryParse(value, out int mode))
            {
                return Math.Clamp(mode, 0, 2);
            }

            return 1;
        }
    }
}
