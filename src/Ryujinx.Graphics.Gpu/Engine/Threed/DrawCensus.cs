using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Memory.Tracking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    /// <summary>
    /// Host draws per second by the path that issued them, to find where one guest draw
    /// becomes several host draws. A plain draw issued while the guest's instance counter is
    /// above zero is one instance of an instanced draw that was not merged into a single
    /// host draw. RYUJINX_DRAW_CENSUS=1; one log line every 5 s.
    /// </summary>
    static class DrawCensus
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("RYUJINX_DRAW_CENSUS") == "1";

        public static long Plain;
        public static long PlainSplitInstance;
        public static long Macro;
        public static long MacroInstances;
        public static long Merged;
        public static long MergedInstances;

        private static int _started;

        /// <summary>The macro whose HLE issued the draw being noted (set by MacroHLE).</summary>
        public static string Source = "?";

        /// <summary>The command buffer entry being processed (set by GPFifoProcessor).</summary>
        public static ulong EntryGpuVa;
        public static int EntryBytes;

        // RYUJINX_DRAW_WATCH_COUNT=n: the first DrawElements macro draw of n indices arms a write
        // watch on the guest pages holding its command entry, and every guest write to them is
        // logged with the writing instruction, so the code that writes those commands is named.
        private static readonly int _watchCount =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_DRAW_WATCH_COUNT"), out int n) ? n : -1;
        private static RegionHandle _watch;
        private static int _watchHits;
        private static long _lastArm;

        public static void MaybeWatch(MemoryManager memory, int count)
        {
            if (_watchCount < 0 || count != _watchCount || Source != "DrawElements")
            {
                return;
            }

            if (_watch != null)
            {
                // Re-arm about once a second, up to 32 logged writes.
                if (_watch.Dirty && _watchHits < 32 && Environment.TickCount64 - _lastArm > 1000)
                {
                    _lastArm = Environment.TickCount64;
                    _watch.Reprotect();
                }

                return;
            }

            ulong cpuVa = memory.Translate(EntryGpuVa);

            if (cpuVa == MemoryManager.PteUnmapped)
            {
                return;
            }

            ulong start = cpuVa & ~0xFFFUL;
            ulong end = (cpuVa + (ulong)EntryBytes + 0xFFF) & ~0xFFFUL;

            _watch = memory.Physical.BeginTracking(start, end - start, ResourceKind.None);
            // OnDirty, not RegisterAction: the action is the pre-access flush hook and also runs
            // when the GPU front end merely reads the commands (the first version of this watch
            // reported that read as a write). OnDirty is raised only by a write.
            ulong watchStart = start;
            ulong entryStart = cpuVa, entryEnd = cpuVa + (ulong)EntryBytes;
            _watch.RegisterDirtyEvent(() =>
            {
                int hit = Interlocked.Increment(ref _watchHits);

                if (hit <= 32)
                {
                    ulong at = GuestFaultContext.Address;
                    string where = at >= entryStart && at < entryEnd ? $"inside the entry at +0x{at - entryStart:X}" : $"outside the entry (page offset 0x{at & 0xFFF:X})";
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"draw-watch: write {hit} to 0x{at:X}, {where}, by guest pc 0x{GuestFaultContext.Pc:X} on {Thread.CurrentThread.Name}");

                    // A host-side write: name the emulated engine behind it.
                    if (GuestFaultContext.Pc == 0 && hit <= 3)
                    {
                        string stack = string.Join(" <- ", Environment.StackTrace.Split('\n')
                            .Select(l => l.Trim())
                            .Where(l => l.StartsWith("at Ryujinx.", StringComparison.Ordinal))
                            .Select(l => l[3..].Split('(')[0])
                            .Skip(1)
                            .Take(10));
                        Logger.Warning?.Print(LogClass.Gpu, $"draw-watch: host write path: {stack}");
                    }
                }
            });
            _lastArm = Environment.TickCount64;
            _watch.Reprotect();

            Logger.Warning?.Print(LogClass.Gpu,
                $"draw-watch: armed on entry gpu 0x{EntryGpuVa:X} cpu 0x{cpuVa:X} ({EntryBytes} bytes), pages 0x{start:X}-0x{end:X}");
        }

        // Macro draws keyed by what they draw: how often the same draw repeats in a window.
        private static readonly object _keysLock = new();
        private static Dictionary<(ulong, int, int, int, int, int, string), int> _keys = new();

        public static void NoteMacroDraw(ulong indexBuffer, int firstIndex, int count, int firstVertex, int instanceCount, int firstInstance)
        {
            var key = (indexBuffer, firstIndex, count, firstVertex, instanceCount, firstInstance, Source);

            lock (_keysLock)
            {
                _keys[key] = _keys.GetValueOrDefault(key) + 1;
            }
        }

        public static void Start()
        {
            if (Enabled && Interlocked.Exchange(ref _started, 1) == 0)
            {
                new Thread(Loop) { Name = "DrawCensus", IsBackground = true }.Start();
            }
        }

        private static void Loop()
        {
            const int PeriodMs = 5000;

            while (true)
            {
                Thread.Sleep(PeriodMs);

                double s = PeriodMs / 1000.0;
                long plain = Interlocked.Exchange(ref Plain, 0);
                long split = Interlocked.Exchange(ref PlainSplitInstance, 0);
                long macro = Interlocked.Exchange(ref Macro, 0);
                long macroInst = Interlocked.Exchange(ref MacroInstances, 0);
                long merged = Interlocked.Exchange(ref Merged, 0);
                long mergedInst = Interlocked.Exchange(ref MergedInstances, 0);

                Dictionary<(ulong, int, int, int, int, int, string), int> keys;

                lock (_keysLock)
                {
                    keys = _keys;
                    _keys = new();
                }

                // Repeats per key over the window, as a histogram of how many keys repeat how often.
                var byRepeat = keys.Values.GroupBy(v => v <= 2 ? v : v <= 8 ? 8 : v <= 32 ? 32 : v <= 128 ? 128 : v <= 512 ? 512 : 9999)
                    .OrderBy(g => g.Key).Select(g => $"<={g.Key}: {g.Count()} keys/{g.Sum()} draws");
                var top = keys.OrderByDescending(kv => kv.Value).Take(6)
                    .Select(kv => $"ib 0x{kv.Key.Item1:X} first {kv.Key.Item2} count {kv.Key.Item3} inst {kv.Key.Item5} via {kv.Key.Item7} x{kv.Value}");
                var bySource = keys.GroupBy(kv => kv.Key.Item7).Select(g => $"{g.Key} {g.Sum(kv => kv.Value) / s:F0}/s");

                Logger.Warning?.Print(LogClass.Gpu,
                    $"draw-census per s: plain {plain / s:F0} (split instances {split / s:F0}), " +
                    $"macro {macro / s:F0} ({macroInst / s:F0} instances), merged instanced {merged / s:F0} ({mergedInst / s:F0} instances); " +
                    $"macro draws by macro: {string.Join(", ", bySource)}; in {PeriodMs / 1000} s: {keys.Values.Sum()} over {keys.Count} distinct keys [{string.Join(", ", byRepeat)}]; top {string.Join("; ", top)}");
            }
        }
    }
}
