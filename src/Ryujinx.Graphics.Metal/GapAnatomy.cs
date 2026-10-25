using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Read-only breakdown of the host GPU's idle gaps.
    ///
    /// In dense scenes the GPU sits idle about a quarter of each frame, in roughly three
    /// gaps, and each gap follows one of the game's own reads of GPU-written memory. The
    /// read itself cannot go (it is game semantics, and making sync points finer only made
    /// the waits longer), so what is left to shorten is the time from "the GPU ran dry" to
    /// "the GPU has work again". That time has three parts with different owners:
    ///
    ///   wake     GPU finished the awaited work -> the waiting guest thread returned
    ///   pre      guest resumed -> the backend opened an encoder for the next buffer
    ///            (game CPU work, GPFIFO processing, the threaded renderer's queue)
    ///   encode   first encoder of the next buffer -> that buffer was committed
    ///   submit   commit -> the GPU started it
    ///
    /// A gap with no guest wait returning inside it is reported separately ("stall": the
    /// GPU starved while nobody was waiting on it). Every figure is milliseconds per frame
    /// over the stats window. Times are host seconds on the mach_absolute_time base, the
    /// same base as GPUStartTime; the "clock" count is buffers whose GPU start precedes
    /// their commit, which must be zero for the breakdown to mean anything.
    ///
    /// Default off: RYUJINX_METAL_GAP_ANATOMY=1.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class GapAnatomy
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_GAP_ANATOMY") == "1";

        private const int BufferCapacity = 16384;
        private const int WaitCapacity = 8192;
        private const double GapThreshold = 0.0005;
        private const double WakeSlack = 0.0005;

        private static readonly object _lock = new();

        private static readonly double[] _encode = new double[BufferCapacity];
        private static readonly double[] _commit = new double[BufferCapacity];
        private static readonly double[] _start = new double[BufferCapacity];
        private static readonly double[] _end = new double[BufferCapacity];
        private static int _buffers;

        private static readonly double[] _waitStart = new double[WaitCapacity];
        private static readonly double[] _waitEnd = new double[WaitCapacity];
        private static int _waits;

        private static readonly double _tickSeconds = 1.0 / Stopwatch.Frequency;

        public static double Seconds(long ticks) => ticks * _tickSeconds;

        public static double Now() => Stopwatch.GetTimestamp() * _tickSeconds;

        public static void NoteBuffer(long encodeTicks, long commitTicks, double gpuStart, double gpuEnd)
        {
            if (commitTicks == 0 || gpuStart <= 0.0 || gpuEnd < gpuStart)
            {
                return;
            }

            double commit = Seconds(commitTicks);
            double encode = encodeTicks != 0 ? Seconds(encodeTicks) : commit;

            lock (_lock)
            {
                if (_buffers == BufferCapacity)
                {
                    return;
                }

                _encode[_buffers] = encode;
                _commit[_buffers] = commit;
                _start[_buffers] = gpuStart;
                _end[_buffers] = gpuEnd;
                _buffers++;
            }
        }

        public static void NoteWaitReturned(long startTicks)
        {
            double now = Now();

            lock (_lock)
            {
                if (_waits < WaitCapacity)
                {
                    _waitStart[_waits] = Seconds(startTicks);
                    _waitEnd[_waits++] = now;
                }
            }
        }

        /// <summary>Consumes the window and returns the stats-line fragment, per frame.</summary>
        public static string TakeAndFormat(int frames)
        {
            if (!Enabled || frames <= 0)
            {
                return string.Empty;
            }

            double[] encode, commit, start, end, waits, waitStarts;
            int n, w;

            lock (_lock)
            {
                n = _buffers;
                w = _waits;
                encode = _encode[..n];
                commit = _commit[..n];
                start = _start[..n];
                end = _end[..n];
                waits = _waitEnd[..w];
                waitStarts = _waitStart[..w];
                _buffers = 0;
                _waits = 0;
            }

            if (n < 2)
            {
                return " gap anatomy: no buffers.";
            }

            int[] order = new int[n];

            for (int i = 0; i < n; i++)
            {
                order[i] = i;
            }

            Array.Sort(order, (a, b) => start[a].CompareTo(start[b]));
            Array.Sort(waits, waitStarts);

            int clockViolations = 0;

            for (int i = 0; i < n; i++)
            {
                if (start[i] + 0.0001 < commit[i])
                {
                    clockViolations++;
                }
            }

            int gaps = 0, withWait = 0;
            double gapSum = 0, wake = 0, guestLate = 0, pre = 0, enc = 0, submit = 0, stall = 0, late = 0;
            double held = 0, behind = 0;
            int blocked = 0, noWait = 0;
            double runEnd = end[order[0]];

            for (int k = 1; k < n; k++)
            {
                int j = order[k];

                if (start[j] > runEnd + GapThreshold)
                {
                    double gs = runEnd;
                    double ge = start[j];
                    double c = commit[j];
                    double f = Math.Min(encode[j], c);

                    gaps++;
                    gapSum += ge - gs;

                    // The first guest thread to come back around the time the GPU ran dry
                    // and before the buffer that ends the gap was committed.
                    int idx = Array.BinarySearch(waits, gs - WakeSlack);
                    idx = idx < 0 ? ~idx : idx;
                    double wr = idx < waits.Length && waits[idx] <= c ? waits[idx] : double.NaN;

                    if (!double.IsNaN(wr))
                    {
                        withWait++;

                        // Blocked when the GPU ran dry: the time to its return is wake-up
                        // latency. Arrived after: the GPU sat idle while the guest was still
                        // busy elsewhere, and only the wait itself counts as wake.
                        double ws = waitStarts[idx];

                        if (ws <= gs)
                        {
                            blocked++;
                            wake += Math.Max(0.0, wr - gs);
                        }
                        else
                        {
                            guestLate += ws - gs;
                            wake += Math.Max(0.0, wr - ws);
                        }

                        pre += Math.Max(0.0, f - Math.Max(wr, gs));
                        enc += Math.Max(0.0, c - Math.Max(f, Math.Max(wr, gs)));
                        submit += Math.Max(0.0, ge - Math.Max(c, gs));
                    }
                    else if (c > gs)
                    {
                        // Nobody waiting and the GPU ran dry. Split by whether the buffer
                        // that ends the gap had already started encoding: if it had, the
                        // work existed and was only held back from commit; if not, the
                        // CPU side had not produced it yet.
                        noWait++;
                        stall += c - gs;
                        behind += Math.Max(0.0, f - gs);
                        held += c - Math.Max(f, gs);
                        submit += ge - c;
                    }
                    else
                    {
                        // Committed before the GPU ran dry and still started late.
                        late += ge - gs;
                    }
                }

                if (end[j] > runEnd)
                {
                    runEnd = end[j];
                }
            }

            double k1 = 1000.0 / frames;

            return $" gap anatomy/frame: {gaps / (double)frames:F1} gaps {gapSum * k1:F2}ms " +
                   $"({withWait / (double)frames:F1} after a guest wait, {blocked / (double)frames:F1} of them blocked when it ran dry: " +
                   $"guest late {guestLate * k1:F2}, wake {wake * k1:F2}, pre {pre * k1:F2}, encode {enc * k1:F2}, submit {submit * k1:F2}; " +
                   $"no-wait stall {stall * k1:F2} in {noWait / (double)frames:F1} gaps = cpu behind {behind * k1:F2} + held uncommitted {held * k1:F2}, " +
                   $"late start {late * k1:F2}), waits {w / (double)frames:F1}, clock {clockViolations}/{n}.";
        }
    }
}
