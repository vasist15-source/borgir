using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace Ryujinx.Common.Logging
{
    /// <summary>
    /// RYUJINX_READBACK_LOG=1: every time a thread touches guest memory the GPU has written, and the GPU's result is
    /// copied back into it first, this adds up what was copied (which buffer or texture, how much of it next to how
    /// much the access needed), who touched it, and how long they waited for the GPU and for the copy. Logged every
    /// 5 seconds, the kinds of copy that cost the most time first. The sync wait totals in the Metal stats line say
    /// how long the game's threads wait; this says what for. Off unless the variable is set; every method returns at
    /// once then.
    /// </summary>
    public static class ReadbackLog
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("RYUJINX_READBACK_LOG") == "1";

        private const int ReportedKinds = 20;

        private static readonly long _reportIntervalTicks = Stopwatch.Frequency * 5;

        private sealed class Totals
        {
            public long Count;
            public long WaitTicks;
            public long CopyTicks;
            public long TotalTicks;
            public long MaxTicks;
            public long BytesCopied;
            public long BytesAccessed;
            public long Empty;
            public long Outside;
        }

        private static readonly Lock _lock = new();
        private static readonly Dictionary<string, Totals> _totals = [];
        private static long _lastReport = Stopwatch.GetTimestamp();

        // The access being handled on this thread: what it touched, how much, and what it has cost so far.
        [ThreadStatic]
        private static int _depth;

        [ThreadStatic]
        private static string _what;

        [ThreadStatic]
        private static string _by;

        [ThreadStatic]
        private static ulong _accessed;

        [ThreadStatic]
        private static long _waitTicks;

        [ThreadStatic]
        private static long _copyTicks;

        [ThreadStatic]
        private static long _bytes;

        /// <summary>A start time for the other methods, or 0 when the log is off.</summary>
        public static long Start()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0;
        }

        /// <summary>
        /// A memory access has hit GPU-written data in <paramref name="what"/> and is about to copy it back; the
        /// access covered <paramref name="accessed"/> bytes of it. Memory is protected a page at a time, so an access
        /// to the same page that misses the resource comes here too, with a size that has wrapped below zero. <paramref name="by"/> names the thread that made the
        /// access, when the copy runs on another one. Returns a start time for <see cref="EndAccess"/>.
        /// </summary>
        public static long BeginAccess(string what, ulong accessed, string by = null)
        {
            if (!Enabled || _depth++ > 0)
            {
                return 0;
            }

            _what = what;
            _by = by;
            _accessed = accessed;
            _waitTicks = 0;
            _copyTicks = 0;
            _bytes = 0;

            return Stopwatch.GetTimestamp();
        }

        public static void EndAccess(long start)
        {
            if (!Enabled)
            {
                return;
            }

            _depth--;

            if (start == 0)
            {
                return;
            }

            long total = Stopwatch.GetTimestamp() - start;

            Add($"{_what} by {_by ?? Thread.CurrentThread.Name ?? "unnamed thread"}", _waitTicks, _copyTicks, total, _bytes, (long)_accessed);
        }

        /// <summary>A wait for the GPU to finish the work that wrote the data, which began at <paramref name="start"/>.</summary>
        public static void AddWait(long start)
        {
            if (start != 0 && _depth > 0)
            {
                _waitTicks += Stopwatch.GetTimestamp() - start;
            }
        }

        /// <summary>
        /// <paramref name="bytes"/> copied from the GPU into guest memory, which began at <paramref name="start"/>.
        /// Outside of an access (a texture written back before the texture cache drops it, say) it is filed under
        /// <paramref name="what"/>.
        /// </summary>
        public static void AddCopy(long start, long bytes, string what)
        {
            if (start == 0)
            {
                return;
            }

            long ticks = Stopwatch.GetTimestamp() - start;

            if (_depth > 0)
            {
                _copyTicks += ticks;
                _bytes += bytes;
            }
            else
            {
                Add($"{what}, not from an access, by {Thread.CurrentThread.Name ?? "unnamed thread"}", 0, ticks, ticks, bytes, 0);
            }
        }

        private static void Add(string key, long waitTicks, long copyTicks, long totalTicks, long bytesCopied, long bytesAccessed)
        {
            string report = null;

            lock (_lock)
            {
                if (!_totals.TryGetValue(key, out Totals totals))
                {
                    _totals[key] = totals = new Totals();
                }

                totals.Count++;
                totals.WaitTicks += waitTicks;
                totals.CopyTicks += copyTicks;
                totals.TotalTicks += totalTicks;
                totals.MaxTicks = Math.Max(totals.MaxTicks, totalTicks);
                totals.BytesCopied += bytesCopied;

                if (bytesAccessed < 0)
                {
                    totals.Outside++;
                }
                else
                {
                    totals.BytesAccessed += bytesAccessed;
                }

                if (bytesCopied == 0)
                {
                    totals.Empty++;
                }

                long now = Stopwatch.GetTimestamp();

                if (now - _lastReport >= _reportIntervalTicks)
                {
                    report = TakeReportLocked((now - _lastReport) / (double)Stopwatch.Frequency);
                    _lastReport = now;
                }
            }

            if (report != null)
            {
                Logger.Info?.Print(LogClass.Gpu, report);
            }
        }

        private static string TakeReportLocked(double seconds)
        {
            static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            static string Size(double bytes) => bytes >= 1 << 20 ? $"{bytes / (1 << 20):F1} MB" : $"{bytes / 1024:F1} KB";

            long count = _totals.Values.Sum(t => t.Count);
            long total = _totals.Values.Sum(t => t.TotalTicks);
            long wait = _totals.Values.Sum(t => t.WaitTicks);
            long copy = _totals.Values.Sum(t => t.CopyTicks);
            long copied = _totals.Values.Sum(t => t.BytesCopied);
            long accessed = _totals.Values.Sum(t => t.BytesAccessed);

            StringBuilder report = new(
                $"Readbacks over {seconds:F1} s: {count} copies back from the GPU, {Ms(total):F0} ms in all " +
                $"({Ms(wait):F0} ms waiting for the GPU, {Ms(copy):F0} ms copying), {Size(copied)} copied for accesses of {Size(accessed)}. " +
                "By kind, ms in all / count / avg wait ms / avg copy ms / max ms / copied per time / accessed per time / times nothing was copied / " +
                "times the access missed it (same page only):");

            foreach ((string key, Totals totals) in _totals.OrderByDescending(kv => kv.Value.TotalTicks).Take(ReportedKinds))
            {
                report.Append(
                    $"\n    {Ms(totals.TotalTicks),7:F1}  {totals.Count,5}  {Ms(totals.WaitTicks) / totals.Count,6:F2}  {Ms(totals.CopyTicks) / totals.Count,6:F2}" +
                    $"  {Ms(totals.MaxTicks),6:F1}  {Size(totals.BytesCopied / (double)totals.Count),9}  {Size(totals.BytesAccessed / (double)Math.Max(1, totals.Count - totals.Outside)),9}  {totals.Empty,4}  {totals.Outside,4}  {key}");
            }

            if (_totals.Count > ReportedKinds)
            {
                report.Append($"\n    ...and {_totals.Count - ReportedKinds} more kinds");
            }

            _totals.Clear();

            return report.ToString();
        }
    }
}
