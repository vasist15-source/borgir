using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Ryujinx.Common.Logging
{
    public enum StallKind
    {
        // GPU.MainThread
        GpuIdle,
        GpuShaderTranslate,
        GpuQueueFull,
        GpuInvoke,
        GpuFenceWait,

        // PX.RenderThread
        RenderIdle,
        RenderProgramWait,
        RenderPipelineBuild,
        RenderPipelineWait,
        RenderDrawable,
        RenderFenceWait,

        // Any other thread (the game's own threads, mostly)
        OtherFenceWait,
    }

    /// <summary>
    /// RYUJINX_STALL_LEDGER=1: the GPU and render threads add up the time they spend in each place that can hold a
    /// frame back (waiting, building pipelines, translating shaders), and a frame that takes 100 ms or more logs where
    /// both threads' time went in the meantime. Off unless the variable is set; every method returns at once then.
    /// </summary>
    public static class StallLedger
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("RYUJINX_STALL_LEDGER") == "1";

        private const long SlowFrameMs = 100;

        private static readonly string[] _names =
        [
            "idle", "shader translation", "render queue full", "waiting on render thread", "GPU fence waits",
            "idle", "new program compile waits", "pipeline builds in place", "pipeline build waits", "next drawable", "GPU fence waits",
            "GPU fence waits",
        ];

        private static readonly int _kindCount = Enum.GetValues<StallKind>().Length;
        private static readonly long[] _ticks = new long[_kindCount];
        private static readonly long[] _counts = new long[_kindCount];
        private static long _lastPresent;

        [ThreadStatic]
        private static int _role;

        /// <summary>A start time for <see cref="End"/>, or 0 when the ledger is off.</summary>
        public static long Begin()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0;
        }

        public static void End(StallKind kind, long start)
        {
            if (start == 0)
            {
                return;
            }

            Interlocked.Add(ref _ticks[(int)kind], Stopwatch.GetTimestamp() - start);
            Interlocked.Increment(ref _counts[(int)kind]);
        }

        /// <summary>A fence wait, filed under the thread that waited.</summary>
        public static void EndFenceWait(long start)
        {
            if (start == 0)
            {
                return;
            }

            if (_role == 0)
            {
                _role = Thread.CurrentThread.Name switch
                {
                    "GPU.MainThread" => 1,
                    "PX.RenderThread" => 2,
                    _ => 3,
                };
            }

            End(_role switch { 1 => StallKind.GpuFenceWait, 2 => StallKind.RenderFenceWait, _ => StallKind.OtherFenceWait }, start);
        }

        /// <summary>
        /// Once per presented frame: starts the next frame's totals, and logs this frame's when it was slow.
        /// </summary>
        public static void NotePresent(long frame)
        {
            if (!Enabled)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            long last = _lastPresent;
            _lastPresent = now;

            long[] ticks = new long[_kindCount];
            long[] counts = new long[_kindCount];

            for (int i = 0; i < _kindCount; i++)
            {
                ticks[i] = Interlocked.Exchange(ref _ticks[i], 0);
                counts[i] = Interlocked.Exchange(ref _counts[i], 0);
            }

            long frameMs = (now - last) * 1000 / Stopwatch.Frequency;

            if (last == 0 || frameMs < SlowFrameMs)
            {
                return;
            }

            StringBuilder line = new($"Slow frame {frame}: {frameMs} ms. GPU thread:");

            Append(line, ticks, counts, StallKind.GpuIdle, StallKind.GpuFenceWait);
            line.Append(". Render thread:");
            Append(line, ticks, counts, StallKind.RenderIdle, StallKind.RenderFenceWait);
            line.Append(". Other threads:");
            Append(line, ticks, counts, StallKind.OtherFenceWait, StallKind.OtherFenceWait);

            Logger.Warning?.Print(LogClass.Gpu, line.ToString());
        }

        private static void Append(StringBuilder line, long[] ticks, long[] counts, StallKind first, StallKind last)
        {
            bool any = false;

            for (int i = (int)first; i <= (int)last; i++)
            {
                double ms = ticks[i] * 1000.0 / Stopwatch.Frequency;

                if (ms >= 1)
                {
                    line.Append(any ? ", " : " ").Append($"{_names[i]} {ms:F0} ms ({counts[i]})");
                    any = true;
                }
            }

            if (!any)
            {
                line.Append(" nothing over 1 ms");
            }
        }
    }
}
