using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Commits the command buffer being recorded when the GPU is about to run out of work.
    ///
    /// GapAnatomy measured the dense 2K scene idling the GPU 5.8 ms a frame with no guest
    /// thread waiting on it, and 4.0 ms of that while the buffer that would end the gap
    /// had already started encoding: the work existed, the GPU sat idle, and the auto-flush
    /// timers (1 ms at attachment changes, 1.5 ms at draws in fast flush mode) had simply
    /// not fired yet. This adds one more trigger to both places that already flush: when
    /// the last committed buffer has been handed to the GPU (level 2) or has finished
    /// (level 1), commit what has been recorded so far instead of waiting for the timer.
    ///
    /// A flush at a draw ends the render pass, so it is a split with the usual store/load
    /// cost; one at an attachment change is free, the pass ends there anyway. Committing
    /// earlier never reorders anything - the same flush points already run on timers.
    ///
    /// Levels 3 and 4 split the difference, because a flush at a draw costs a pass split and
    /// one at an attachment change costs nothing: 3 flushes at attachment changes as soon as
    /// the GPU holds its last buffer but at draws only once it is idle; 4 uses the level 2
    /// condition at both but lets a pass grow to 64 draws before splitting it.
    ///
    /// Measured in one session, levels alternating every 60 s at a standing spot (3,800 draws
    /// a frame): level 2 +2.5% and +2.1% frame rate in two runs, level 1 +0.8%, level 3 no
    /// significant gain, level 4 the same as 2. Level 2 is the default.
    ///
    /// RYUJINX_METAL_STARVE_FLUSH=0..4 (default 2), or /tmp/ryujinx-metal-starve-flush
    /// holding one of those while the game runs.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class StarveFlush
    {
        private const string HotFile = "/tmp/ryujinx-metal-starve-flush";

        /// <summary>Draws recorded since the last flush before a starving GPU triggers one.</summary>
        private const ulong MinDraws = 16;

        /// <summary>Level 4's longer minimum, to split fewer passes.</summary>
        private const ulong MinDrawsLong = 64;

        /// <summary>Only look at the queue every this many draws; reading the status is a message send.</summary>
        private const ulong PollEvery = 8;

        private static readonly int _default = ParseLevel(Environment.GetEnvironmentVariable("RYUJINX_METAL_STARVE_FLUSH")) ?? 2;

        public static int Level { get; private set; } = _default;

        private static long _drawFlushes;
        private static long _attachmentFlushes;

        public static void Refresh()
        {
            try
            {
                Level = File.Exists(HotFile) ? ParseLevel(File.ReadAllText(HotFile)) ?? _default : _default;
            }
            catch (IOException)
            {
                // Raced with the writer; the next frame picks it up.
            }
        }

        public static bool ShouldFlushAtDraw(ulong drawCount, ulong lastFlushDrawCount, CommandBufferPool pool)
        {
            ulong minDraws = Level == 4 ? MinDrawsLong : MinDraws;

            if (Level == 0 || drawCount % PollEvery != 0 || drawCount - lastFlushDrawCount < minDraws)
            {
                return false;
            }

            // Idle (1) or last buffer (2) as the condition for splitting a pass.
            if (!pool.GpuRunningDry(Level is 1 or 3 ? 1 : 2))
            {
                return false;
            }

            Interlocked.Increment(ref _drawFlushes);

            return true;
        }

        public static bool ShouldFlushAtAttachmentChange(ulong drawCount, ulong lastFlushDrawCount, CommandBufferPool pool)
        {
            if (Level == 0 || drawCount - lastFlushDrawCount < MinDraws / 2 || !pool.GpuRunningDry(Level == 1 ? 1 : 2))
            {
                return false;
            }

            Interlocked.Increment(ref _attachmentFlushes);

            return true;
        }

        public static string TakeStats(int frames)
        {
            long draws = Interlocked.Exchange(ref _drawFlushes, 0);
            long attachments = Interlocked.Exchange(ref _attachmentFlushes, 0);

            return $" starve flush: level {Level}, {draws / (double)frames:F1} at draws + {attachments / (double)frames:F1} at attachment changes per frame.";
        }

        private static int? ParseLevel(string text)
        {
            return int.TryParse(text?.Trim(), out int level) && level is >= 0 and <= 4 ? level : null;
        }
    }
}
