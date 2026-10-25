using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    class SyncManager
    {
        // Without auto-flush the batch is the only proactive submission mechanism, so
        // it must stay small. With auto-flush enabled it is only a backstop for long
        // draw-less stretches (compute or upload heavy), so it can be much larger.
        private const int DefaultDeferredSyncBatchSize = 4;
        private const int DefaultDeferredSyncBatchSizeWithAutoFlush = 16;
        private const int MaximumDeferredSyncBatchSize = 64;

        private static readonly long _waitBucketTicks0 = Stopwatch.Frequency / 2000; // 0.5ms
        private static readonly long _waitBucketTicks1 = Stopwatch.Frequency / 500; // 2ms
        private static readonly long _waitBucketTicks2 = Stopwatch.Frequency / 125; // 8ms

        private class SyncHandle
        {
            public ulong ID;
            public MultiFenceHolder Waitable;
            public ulong FlushId;
            public HostSyncCreateSource Source;
            public bool Signalled;

            public bool NeedsFlush(ulong currentFlushId)
            {
                return (long)(FlushId - currentFlushId) >= 0;
            }
        }

        // RYUJINX_METAL_SYNC_STRICT: bit 1 = never mark a handle signalled off another
        // handle's wait (no coalescing - what the Vulkan backend does); bit 2 = a new handle
        // also waits on command buffers already committed but not yet completed, not only
        // the ones still being recorded. Diagnostic arms for the flare count readback.
        private static readonly int _strict =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_SYNC_STRICT"), out int st) ? st : 0;
        private ulong _firstHandle;

        private readonly MetalRenderer _renderer;
        private readonly List<SyncHandle> _handles;
        private readonly int _deferredSyncBatchSize;
        private ulong _flushId;
        private int _deferredSyncsSinceFlush;
        private long _waitTicks;
        private long _autoFlushWaitTicks;
        private int _waitCount;
        private int _forcedFlushCount;
        private int _proactiveFlushCount;
        private int _coalescedSignalCount;
        private long _maxWaitTicks;
        private readonly int[] _waitBucketCounts = new int[4];
        private readonly long[] _waitTicksByWaitSource = new long[Enum.GetValues<HostSyncWaitSource>().Length];
        private readonly int[] _waitCountByWaitSource = new int[Enum.GetValues<HostSyncWaitSource>().Length];
        private readonly long[] _waitTicksByCreateSource = new long[Enum.GetValues<HostSyncCreateSource>().Length];
        private readonly int[] _waitCountByCreateSource = new int[Enum.GetValues<HostSyncCreateSource>().Length];

        // Wait time is summed across every thread that waits, so it can exceed wall
        // clock and says nothing on its own about what the frame rate is losing.
        // Attribute it per thread instead: only the thread that feeds the command
        // stream stalls the frame.
        private readonly Dictionary<string, (long Ticks, int Count)> _waitByThread = [];

        public SyncManager(MetalRenderer renderer)
        {
            _renderer = renderer;
            _handles = [];
            _deferredSyncBatchSize = GetDeferredSyncBatchSize();

            Logger.Info?.PrintMsg(
                LogClass.Gpu,
                $"Metal deferred host-sync batch size: {_deferredSyncBatchSize} (0 disables proactive submission). " +
                "Set RYUJINX_METAL_DEFERRED_SYNC_BATCH to override it for A/B testing.");

            Logger.Info?.PrintMsg(
                LogClass.Gpu,
                AutoFlushCounter.ConfiguredEnabled
                    ? "Metal deferred sync batch acts as a backstop; submission cadence is driven by auto-flush."
                    : "Metal auto-flush is disabled; deferred sync batch is the only proactive submission mechanism.");
        }

        // Strict syncs that committed while a render pass was live, by what created them.
        private static readonly long[] _strictMidPass = new long[Enum.GetValues<HostSyncCreateSource>().Length];

        public static string TakeStrictMidPass(int frames)
        {
            string text = string.Join(", ", Enum.GetValues<HostSyncCreateSource>()
                .Where(s => Interlocked.Read(ref _strictMidPass[(int)s]) != 0)
                .Select(s => $"{s}={Interlocked.Exchange(ref _strictMidPass[(int)s], 0) / (double)frames:F1}"));

            return text.Length == 0 ? null : text;
        }

        public void RegisterFlush()
        {
            _flushId++;
            _deferredSyncsSinceFlush = 0;
        }

        public void Create(ulong id, bool strict, HostSyncCreateSource source)
        {
            ulong flushId = _flushId;
            MultiFenceHolder waitable = new();
            if ((_strict & 2) != 0)
            {
                _renderer.CommandBufferPool.AddWaitable(waitable);
            }
            if (strict || _renderer.InterruptAction == null)
            {
                if (_renderer.CurrentEncoderType == EncoderType.Render)
                {
                    Interlocked.Increment(ref _strictMidPass[(int)source]);
                }

                // Attach the sync to the command buffers that form this exact boundary
                // before submitting them. Metal queues execute in order, so waiting for
                // these fences also covers earlier submissions without retaining every
                // older in-flight command buffer on each sync handle.
                _renderer.CommandBufferPool.AddInUseWaitable(waitable);
                _renderer.FlushAllCommands();
            }
            else
            {
                // Don't flush commands, instead wait for the current command buffer to finish.
                // If this sync is waited on before the command buffer is submitted, interrupt the gpu thread and flush it manually.

                _renderer.CommandBufferPool.AddInUseWaitable(waitable);

                // Purely lazy submission creates hundreds of on-demand cross-thread
                // interrupts in real games. Submit a small batch proactively so Metal
                // can overlap execution with guest CPU work while preserving every
                // original sync and wait boundary.
                //
                // During asset streaming there are almost no draws or attachment
                // changes, so the draw-driven auto-flush cannot bound the deferral
                // window and waits grow to the size of upload-heavy command buffers.
                // Outside of an active render pass a time-based bound is cheap and
                // safe, so apply it here in addition to the count-based backstop.
                if (_deferredSyncBatchSize != 0 && ++_deferredSyncsSinceFlush >= _deferredSyncBatchSize)
                {
                    Interlocked.Increment(ref _proactiveFlushCount);
                    _renderer.FlushAllCommands();
                }
                else if (_renderer.CurrentEncoderType != EncoderType.Render &&
                    _renderer.AutoFlush.ShouldFlushDeferredSync())
                {
                    Interlocked.Increment(ref _proactiveFlushCount);
                    _renderer.FlushAllCommands();
                }
            }

            SyncHandle handle = new()
            {
                ID = id,
                Waitable = waitable,
                FlushId = flushId,
                Source = source,
            };

            lock (_handles)
            {
                _handles.Add(handle);
            }
        }

        public ulong GetCurrent()
        {
            lock (_handles)
            {
                ulong lastHandle = _firstHandle;

                foreach (SyncHandle handle in _handles)
                {
                    lock (handle)
                    {
                        if (handle.Waitable == null)
                        {
                            continue;
                        }

                        if (handle.ID > lastHandle)
                        {
                            bool signaled = handle.Signalled || handle.Waitable.WaitForFences(false);
                            if (signaled)
                            {
                                lastHandle = handle.ID;
                                handle.Signalled = true;
                            }
                        }
                    }
                }

                return lastHandle;
            }
        }

        public void Wait(ulong id, HostSyncWaitSource source)
        {
            SyncHandle result = null;

            lock (_handles)
            {
                if ((long)(_firstHandle - id) > 0)
                {
                    return; // The handle has already been signalled or deleted.
                }

                foreach (SyncHandle handle in _handles)
                {
                    if (handle.ID == id)
                    {
                        result = handle;
                        break;
                    }
                }
            }

            if (result != null)
            {
                lock (result)
                {
                    if (result.Waitable == null || result.Signalled)
                    {
                        return;
                    }
                }

                if (result.Waitable == null)
                {
                    return;
                }

                long beforeTicks = Stopwatch.GetTimestamp();

                if (result.NeedsFlush(_flushId))
                {
                    bool flushed = false;

                    _renderer.InterruptAction(() =>
                    {
                        if (result.NeedsFlush(_flushId))
                        {
                            _renderer.FlushAllCommands();
                            flushed = true;
                        }
                    });

                    if (flushed)
                    {
                        Interlocked.Increment(ref _forcedFlushCount);
                    }
                }

                bool signaled = false;

                lock (result)
                {
                    if (result.Waitable == null || result.Signalled)
                    {
                        return;
                    }

                    signaled = result.Waitable.WaitForFences(true);

                    if (!signaled)
                    {
                        Logger.Error?.PrintMsg(LogClass.Gpu, $"Metal Sync Object {result.ID} failed to signal within 1000ms. Continuing...");
                    }
                    else
                    {
                        if (GapAnatomy.Enabled)
                        {
                            GapAnatomy.NoteWaitReturned(beforeTicks);
                        }

                        long elapsedTicks = Stopwatch.GetTimestamp() - beforeTicks;

                        Interlocked.Add(ref _waitTicks, elapsedTicks);
                        Interlocked.Add(ref _autoFlushWaitTicks, elapsedTicks);
                        Interlocked.Increment(ref _waitCount);
                        AddBucket(_waitTicksByWaitSource, _waitCountByWaitSource, source, elapsedTicks);
                        AddBucket(_waitTicksByCreateSource, _waitCountByCreateSource, result.Source, elapsedTicks);
                        AddThreadWait(elapsedTicks);
                        AddWaitDurationSample(elapsedTicks);
                        result.Signalled = true;
                    }
                }

                if (signaled && (_strict & 1) == 0)
                {
                    int coalescedSignals = MarkCoveredHandlesSignalled(result.FlushId);

                    if (coalescedSignals != 0)
                    {
                        Interlocked.Add(ref _coalescedSignalCount, coalescedSignals);
                    }
                }
            }
        }

        private int MarkCoveredHandlesSignalled(ulong completedFlushId)
        {
            int count = 0;

            // Every command buffer is committed to the same ordered Metal queue. Once
            // a fence from a submission boundary has completed, all sync handles from
            // that boundary and earlier boundaries are complete as well. Marking them
            // here avoids issuing hundreds of duplicate waitUntilCompleted calls for
            // fence holders that ultimately refer to the same submitted GPU work.
            lock (_handles)
            {
                foreach (SyncHandle handle in _handles)
                {
                    if ((long)(handle.FlushId - completedFlushId) > 0)
                    {
                        break;
                    }

                    lock (handle)
                    {
                        if (handle.Waitable != null && !handle.Signalled)
                        {
                            handle.Signalled = true;
                            count++;
                        }
                    }
                }
            }

            return count;
        }

        public void Cleanup()
        {
            // Iterate through handles and remove any that have already been signalled.

            while (true)
            {
                SyncHandle first = null;
                lock (_handles)
                {
                    first = _handles.FirstOrDefault();
                }

                if (first == null || first.NeedsFlush(_flushId))
                {
                    break;
                }

                bool signaled = first.Waitable.WaitForFences(false);
                if (signaled)
                {
                    // Delete the sync object.
                    lock (_handles)
                    {
                        lock (first)
                        {
                            _firstHandle = first.ID + 1;
                            _handles.RemoveAt(0);
                            first.Waitable = null;
                        }
                    }
                }
                else
                {
                    // This sync handle and any following have not been reached yet.
                    break;
                }
            }
        }

        public long GetAndResetWaitTicks()
        {
            return GetAndResetWaitStats(out _, out _, out _);
        }

        public long GetAndResetWaitStats(out int waitCount, out int forcedFlushCount, out int proactiveFlushCount)
        {
            return GetAndResetWaitStats(out waitCount, out forcedFlushCount, out proactiveFlushCount, out _, out _, out _, out _, out _);
        }

        public long GetAndResetAutoFlushWaitTicks()
        {
            return Interlocked.Exchange(ref _autoFlushWaitTicks, 0);
        }

        public long GetAndResetWaitStats(
            out int waitCount,
            out int forcedFlushCount,
            out int proactiveFlushCount,
            out int coalescedSignalCount,
            out string waitBreakdown,
            out string createBreakdown,
            out string waitDurations,
            out string threadBreakdown)
        {
            long result = Interlocked.Exchange(ref _waitTicks, 0);
            waitCount = Interlocked.Exchange(ref _waitCount, 0);
            forcedFlushCount = Interlocked.Exchange(ref _forcedFlushCount, 0);
            proactiveFlushCount = Interlocked.Exchange(ref _proactiveFlushCount, 0);
            coalescedSignalCount = Interlocked.Exchange(ref _coalescedSignalCount, 0);
            waitBreakdown = FormatAndResetWaitBuckets(_waitTicksByWaitSource, _waitCountByWaitSource);
            createBreakdown = FormatAndResetCreateBuckets(_waitTicksByCreateSource, _waitCountByCreateSource);
            waitDurations = FormatAndResetWaitDurations();
            threadBreakdown = FormatAndResetThreadWaits();

            return result;
        }

        private void AddThreadWait(long elapsedTicks)
        {
            string name = Thread.CurrentThread.Name ?? $"tid{Environment.CurrentManagedThreadId}";

            lock (_waitByThread)
            {
                _waitByThread.TryGetValue(name, out (long Ticks, int Count) entry);
                _waitByThread[name] = (entry.Ticks + elapsedTicks, entry.Count + 1);
            }
        }

        private string FormatAndResetThreadWaits()
        {
            lock (_waitByThread)
            {
                if (_waitByThread.Count == 0)
                {
                    return "none";
                }

                string text = string.Join(", ", _waitByThread
                    .OrderByDescending(entry => entry.Value.Ticks)
                    .Take(6)
                    .Select(entry => $"{entry.Key}={entry.Value.Ticks * 1000.0 / Stopwatch.Frequency:F0}ms/{entry.Value.Count}"));

                _waitByThread.Clear();

                return text;
            }
        }

        private void AddWaitDurationSample(long elapsedTicks)
        {
            int bucket = elapsedTicks < _waitBucketTicks0 ? 0 :
                elapsedTicks < _waitBucketTicks1 ? 1 :
                elapsedTicks < _waitBucketTicks2 ? 2 : 3;

            Interlocked.Increment(ref _waitBucketCounts[bucket]);

            long currentMax;
            while (elapsedTicks > (currentMax = Interlocked.Read(ref _maxWaitTicks)))
            {
                if (Interlocked.CompareExchange(ref _maxWaitTicks, elapsedTicks, currentMax) == currentMax)
                {
                    break;
                }
            }
        }

        private string FormatAndResetWaitDurations()
        {
            int bucket0 = Interlocked.Exchange(ref _waitBucketCounts[0], 0);
            int bucket1 = Interlocked.Exchange(ref _waitBucketCounts[1], 0);
            int bucket2 = Interlocked.Exchange(ref _waitBucketCounts[2], 0);
            int bucket3 = Interlocked.Exchange(ref _waitBucketCounts[3], 0);
            long maxTicks = Interlocked.Exchange(ref _maxWaitTicks, 0);

            if (bucket0 == 0 && bucket1 == 0 && bucket2 == 0 && bucket3 == 0)
            {
                return null;
            }

            double maxMs = maxTicks * 1000.0 / Stopwatch.Frequency;

            return $"<0.5ms:{bucket0}, 0.5-2ms:{bucket1}, 2-8ms:{bucket2}, >=8ms:{bucket3}, max:{maxMs:F2}ms";
        }

        private static void AddBucket<T>(long[] ticks, int[] counts, T source, long elapsedTicks) where T : struct, Enum
        {
            int index = Convert.ToInt32(source);

            if ((uint)index < (uint)ticks.Length)
            {
                Interlocked.Add(ref ticks[index], elapsedTicks);
                Interlocked.Increment(ref counts[index]);
            }
        }

        private static string FormatAndResetWaitBuckets(long[] ticks, int[] counts)
        {
            return FormatAndResetBuckets(ticks, counts, static index => ((HostSyncWaitSource)index).ToString());
        }

        private static string FormatAndResetCreateBuckets(long[] ticks, int[] counts)
        {
            return FormatAndResetBuckets(ticks, counts, static index => ((HostSyncCreateSource)index).ToString());
        }

        private static string FormatAndResetBuckets(long[] ticks, int[] counts, Func<int, string> nameForIndex)
        {
            StringBuilder builder = null;

            for (int i = 0; i < ticks.Length; i++)
            {
                long bucketTicks = Interlocked.Exchange(ref ticks[i], 0);
                int bucketCount = Interlocked.Exchange(ref counts[i], 0);

                if (bucketCount == 0)
                {
                    continue;
                }

                builder ??= new StringBuilder();

                if (builder.Length != 0)
                {
                    builder.Append(", ");
                }

                double waitMs = bucketTicks * 1000.0 / Stopwatch.Frequency;
                builder.Append(nameForIndex(i)).Append('=').Append(waitMs.ToString("F0")).Append("ms/").Append(bucketCount);
            }

            return builder?.ToString();
        }

        private static int GetDeferredSyncBatchSize()
        {
            string value = Environment.GetEnvironmentVariable("RYUJINX_METAL_DEFERRED_SYNC_BATCH");

            if (int.TryParse(value, out int batchSize))
            {
                return Math.Clamp(batchSize, 0, MaximumDeferredSyncBatchSize);
            }

            return AutoFlushCounter.ConfiguredEnabled
                ? DefaultDeferredSyncBatchSizeWithAutoFlush
                : DefaultDeferredSyncBatchSize;
        }
    }
}
