using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Nvn;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.HLE.HOS.Kernel.Process.NvnPv
{
    /// <summary>
    /// Replays the guest layer's submissions through GAL (<see cref="NvnReplay"/>, on the GPU
    /// thread) and presents through the GPU window. A window texture handed to presentation is not
    /// given back to the game until the window released it, as the display queue does on hardware,
    /// so the game never renders into a frame still waiting to be shown.
    /// </summary>
    sealed class NvnPvGalRenderer : INvnPvRenderer, IDisposable
    {
        private const int AcquireTimeoutMs = 250;

        private readonly Switch _device;
        private readonly NvnReplay _replay;
        private readonly Dictionary<ulong, WindowState> _windows = new();

        private sealed class WindowState
        {
            public int Next;
            public readonly int[] Queued = new int[16];   // presents of each texture not yet released by the window
        }

        public NvnPvGalRenderer(Switch device, KProcess process, ulong poolGenerationAddress)
        {
            _device = device;
            _replay = new NvnReplay(device.Gpu, process.Pid, device.Memory.Size) { PoolGenerationAddress = poolGenerationAddress };
        }

        public void PoolChanged(ulong addr, ulong size, uint flags, bool add) => _replay.PoolChanged(addr, size, flags, add);

        public void VirtualMapping(ulong gpuVa, ulong guestVa, ulong size) => _replay.VirtualMapping(gpuVa, guestVa, size);

        public void Submit(byte[] records, int length, ulong fence, Action<ulong> completed) => _replay.Submit(records, length, fence, completed);

        private WindowState Window(ulong window)
        {
            lock (_windows)
            {
                if (!_windows.TryGetValue(window, out WindowState w))
                {
                    w = new WindowState();
                    _windows[window] = w;
                }

                return w;
            }
        }

        public int Acquire(ulong window, int active)
        {
            WindowState w = Window(window);
            active = Math.Clamp(active, 1, w.Queued.Length);

            lock (w)
            {
                int index = w.Next % active;
                w.Next = (index + 1) % active;

                long deadline = Environment.TickCount64 + AcquireTimeoutMs;

                while (w.Queued[index] > 0)
                {
                    long left = deadline - Environment.TickCount64;

                    if (left <= 0)
                    {
                        break;   // the window stopped presenting (paused, closing): do not hang the game
                    }

                    Monitor.Wait(w, (int)left);
                }

                return index;
            }
        }

        public void Present(ulong window, int index, PvTex texture, ulong fence)
        {
            WindowState w = Window(window);
            int slot = Math.Clamp(index, 0, w.Queued.Length - 1);

            lock (w)
            {
                w.Queued[slot]++;
            }

            _replay.Present(texture, 0, 0, 0, 0, () =>
            {
                lock (w)
                {
                    w.Queued[slot] = Math.Max(0, w.Queued[slot] - 1);
                    Monitor.PulseAll(w);
                }
            });

            _device.Statistics.RecordGameFrameTime();

            long now = System.Diagnostics.Stopwatch.GetTimestamp();

            if (_lastPresent != 0)
            {
                _intervals[_intervalCount++ % _intervals.Length] = (now - _lastPresent) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }

            _lastPresent = now;

            if (++_presents % 300 == 0)
            {
                Logger.Info?.Print(LogClass.Gpu, $"nvnpv: {_replay.TakeStats(300)}");
                Logger.Info?.Print(LogClass.Gpu, $"nvnpv: {Pacing()}");
            }
        }

        private long _presents, _lastPresent;
        private readonly double[] _intervals = new double[300];
        private int _intervalCount;
        private int _gc0 = GC.CollectionCount(0), _gc1 = GC.CollectionCount(1), _gc2 = GC.CollectionCount(2);
        private long _translations;

        /// <summary>Present-to-present intervals of the last 300 frames, with what might explain the long ones.</summary>
        private string Pacing()
        {
            int n = Math.Min(_intervalCount, _intervals.Length);
            double[] sorted = _intervals[..n];
            Array.Sort(sorted);
            _intervalCount = 0;

            int over33 = 0, over50 = 0;

            foreach (double ms in sorted)
            {
                over33 += ms > 33.4 ? 1 : 0;
                over50 += ms > 50.0 ? 1 : 0;
            }

            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            long translations = _replay.Translations;

            string line = n == 0 ? "no frames" :
                $"pacing: frame ms p50 {sorted[n / 2]:F1}, p90 {sorted[n * 9 / 10]:F1}, p99 {sorted[Math.Min(n - 1, n * 99 / 100)]:F1}, max {sorted[n - 1]:F1}; " +
                $">33ms {over33}, >50ms {over50}; GCs {gc0 - _gc0}/{gc1 - _gc1}/{gc2 - _gc2}; new translations {translations - _translations}; " +
                $"submitted {_replay.TakeSubmittedBytes() / 300 / 1024} KiB/frame";

            (_gc0, _gc1, _gc2, _translations) = (gc0, gc1, gc2, translations);

            return line;
        }

        public void Texels(PvTexels request) => _replay.Texels(request);

        public void TextureFinalized(ulong address) => _replay.TextureFinalized(address);

        public void Dispose() => _replay.Dispose();
    }
}
