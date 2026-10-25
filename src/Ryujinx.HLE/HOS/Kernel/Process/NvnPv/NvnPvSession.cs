using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Nvn;
using Ryujinx.Cpu;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Ryujinx.HLE.HOS.Kernel.Process.NvnPv
{
    /// <summary>
    /// The host side of a game whose NVN calls the guest layer took over (RYUJINX_NVN_PV=2):
    /// submissions, fences, windows and presents. Replay goes through an <see cref="INvnPvRenderer"/>;
    /// without one (the null replay) only what the CPU can observe is carried out - uniform updates,
    /// buffer copies and clears, counter reports and event signals, in submission order - so the
    /// game keeps running while nothing is drawn.
    /// </summary>
    sealed unsafe class NvnPvSession
    {
        private readonly KProcess _process;
        private readonly IVirtualMemoryManager _memory;
        private readonly ulong _ctl;
        private readonly ulong _arenaVa;
        private readonly byte* _arena;
        private readonly ulong _arenaSize;

        private readonly object _submitLock = new();
        private ulong _completedFence;
        private readonly object _fenceLock = new();

        private readonly Dictionary<ulong, (ulong Size, uint Flags)> _pools = new();
        private readonly Dictionary<ulong, Window> _windows = new();

        public INvnPvRenderer Renderer { get; set; }

        private sealed class Window
        {
            public int Count;
            public int Next;
            public PvTex[] Textures;
            public ulong Presents;
        }

        // Offsets in struct pv_ctl (Guest/pv_core.h).
        public const ulong CtlMode = 0x38;
        public const ulong CtlArenaBase = 0x40;
        public const ulong CtlArenaSize = 0x48;
        public const ulong CtlArenaBump = 0x50;
        public const ulong CtlCompletedFence = 0x58;
        public const ulong CtlSubmittedFence = 0x60;
        public const ulong CtlUnimplCalls = 0x70;
        public const ulong CtlSegmentsLive = 0x78;
        public const ulong CtlRecordingsLive = 0x80;
        public const ulong CtlRecords = 0x88;
        public const ulong CtlSubmits = 0x90;
        public const ulong CtlPoolGeneration = 0xC0;

        public ulong ControlBlock => _ctl;

        private readonly Stats _stats = new();

        public NvnPvSession(KProcess process, ulong ctl, ulong arenaVa, byte* arena, ulong arenaSize)
        {
            _process = process;
            _memory = process.CpuMemory;
            _ctl = ctl;
            _arenaVa = arenaVa;
            _arena = arena;
            _arenaSize = arenaSize;
        }

        public IVirtualMemoryManager Memory => _memory;

        // ---------- Pools ----------

        public ulong PoolChanged(ulong addr, ulong size, uint flags, bool add)
        {
            lock (_pools)
            {
                if (add)
                {
                    _pools[addr] = (size, flags);
                }
                else
                {
                    _pools.Remove(addr);
                }
            }

            Renderer?.PoolChanged(addr, size, flags, add);

            return addr;
        }

        // ---------- Virtual pools ----------

        // Virtual pools are GPU-only address ranges (CPU_NO_ACCESS) whose 64 KiB pages the game maps
        // to pages of physical pools. They get GPU addresses between the guest's 39-bit space and the
        // GPU's 40-bit one, so they cannot collide with pool storage (GPU address = guest address).
        public const ulong VirtualBase = 0x80_0000_0000UL;
        public const int VirtualPageShift = 16;

        private ulong _virtualNext = VirtualBase;
        private readonly Dictionary<ulong, ulong> _virtualPages = new();   // GPU page -> guest address of the physical page
        private readonly object _virtualLock = new();

        public ulong VirtualPoolCreate(ulong size)
        {
            lock (_virtualLock)
            {
                ulong at = _virtualNext;
                _virtualNext += (size + 0xFFFFFFUL) & ~0xFFFFFFUL;   // 16 MiB apart

                Logger.Info?.Print(LogClass.Gpu, $"nvnpv: virtual pool of 0x{size:X} bytes at GPU address 0x{at:X}");

                return at;
            }
        }

        public bool VirtualPoolMap(ulong pool, int count, ulong requests, ulong poolSize)
        {
            int stride = Marshal.SizeOf<PvVmapReq>();
            ulong page = 1UL << VirtualPageShift;

            lock (_virtualLock)
            {
                for (int i = 0; i < count; i++)
                {
                    PvVmapReq r = _memory.Read<PvVmapReq>(requests + (ulong)(i * stride));

                    if ((r.VirtualOffset | r.Size) % page != 0 || r.VirtualOffset + r.Size > poolSize || (r.PhysicalAddr != 0 && r.Size > r.PhysicalSize))
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"nvnpv: virtual mapping refused: offset 0x{r.VirtualOffset:X} size 0x{r.Size:X} of a 0x{poolSize:X} pool");
                        return false;
                    }

                    for (ulong o = 0; o < r.Size; o += page)
                    {
                        ulong gpuPage = (pool + r.VirtualOffset + o) >> VirtualPageShift;

                        if (r.PhysicalAddr == 0)
                        {
                            _virtualPages.Remove(gpuPage);
                        }
                        else
                        {
                            _virtualPages[gpuPage] = r.PhysicalAddr + o;
                        }
                    }

                    Renderer?.VirtualMapping(pool + r.VirtualOffset, r.PhysicalAddr, r.Size);
                }
            }

            return true;
        }

        /// <summary>Guest address behind a GPU address of a virtual pool (0 = unmapped); other addresses map to themselves.</summary>
        public ulong TranslateGpu(ulong gpu)
        {
            if (gpu < VirtualBase)
            {
                return gpu;
            }

            lock (_virtualLock)
            {
                return _virtualPages.TryGetValue(gpu >> VirtualPageShift, out ulong guest) ? guest + (gpu & ((1UL << VirtualPageShift) - 1)) : 0;
            }
        }

        // ---------- Submissions ----------

        public void Submit(int queue, int count, ulong handles, ulong fence)
        {
            ulong[] list = new ulong[count];

            for (int i = 0; i < count; i++)
            {
                list[i] = _memory.Read<ulong>(handles + (ulong)i * 8);
            }

            lock (_submitLock)
            {
                // The recordings are flattened into one buffer (calls inlined, segments joined) and deferred
                // handle binds take the values the game's arrays hold now, at submission, as on hardware;
                // the replay then reads nothing of the game's but the memory the commands name.
                _flat.Reset();

                foreach (ulong handle in list)
                {
                    ulong first = RecordingOf(handle);

                    if (first != 0)
                    {
                        Flatten(first, 0);
                    }
                }

                _flat.End();

                if (Renderer != null)
                {
                    Renderer.Submit(_flat.Buffer, _flat.Length, fence, Complete);
                }
                else
                {
                    NullReplay(_flat.Buffer, _flat.Length);
                    Complete(fence);
                }
            }
        }

        /// <summary>Flattened records of one submission (same record format as the arena).</summary>
        private sealed class FlatBuffer
        {
            public byte[] Buffer = new byte[1 << 20];
            public int Length;

            public void Reset() => Length = 0;

            public Span<byte> Append(int size)
            {
                if (Length + size > Buffer.Length)
                {
                    Array.Resize(ref Buffer, Math.Max(Buffer.Length * 2, Length + size));
                }

                Span<byte> span = Buffer.AsSpan(Length, size);
                Length += size;

                return span;
            }

            public void End()
            {
                Span<byte> e = Append(8);
                e.Clear();
            }
        }

        private readonly FlatBuffer _flat = new();

        private void Complete(ulong fence)
        {
            lock (_fenceLock)
            {
                if (fence > _completedFence)
                {
                    _completedFence = fence;
                    _memory.Write(_ctl + CtlCompletedFence, fence);
                }

                Monitor.PulseAll(_fenceLock);
            }
        }

        public bool Wait(ulong fence, ulong timeoutNs)
        {
            long deadline = timeoutNs >= long.MaxValue / 2 ? long.MaxValue : Environment.TickCount64 + (long)(timeoutNs / 1_000_000) + 1;

            lock (_fenceLock)
            {
                while (_completedFence < fence)
                {
                    long left = deadline - Environment.TickCount64;

                    if (left <= 0)
                    {
                        return false;
                    }

                    Monitor.Wait(_fenceLock, (int)Math.Min(left, 1000));
                }
            }

            return true;
        }

        private ulong RecordingOf(ulong handle)
        {
            // pv_mark {uint32 magic, uint32 generation, uint64 first segment}; seg_hdr {uint32 size, uint32 class, uint64 generation}.
            if (handle == 0 || _memory.Read<uint>(handle) != NvnPvRecords.MarkMagic)
            {
                _stats.BadHandles++;
                return 0;
            }

            uint gen = _memory.Read<uint>(handle + 4);
            ulong first = _memory.Read<ulong>(handle + 8);

            if (!InArena(first, NvnPvRecords.SegmentHeader) || (uint)*(ulong*)(_arena + (first - _arenaVa) + 8) != gen)
            {
                _stats.BadHandles++;
                return 0;
            }

            return first;
        }

        private bool InArena(ulong va, ulong size) => va >= _arenaVa && va + size <= _arenaVa + _arenaSize && va + size >= va;

        private void Flatten(ulong segment, int depth)
        {
            if (depth > 8)
            {
                _stats.TooDeep++;
                return;
            }

            ulong p = segment + NvnPvRecords.SegmentHeader;

            while (true)
            {
                if (!InArena(p, 8))
                {
                    _stats.Corrupt++;
                    return;
                }

                byte* r = _arena + (p - _arenaVa);
                PvOp op = (PvOp)((uint*)r)[0];
                uint size = ((uint*)r)[1];

                if (size < 8 || (size & 7) != 0 || !InArena(p, size) || op >= PvOp.Count)
                {
                    _stats.Corrupt++;
                    return;
                }

                switch (op)
                {
                    case PvOp.End:
                        return;

                    case PvOp.Jump:
                        p = *(ulong*)(r + 8) + NvnPvRecords.SegmentHeader;
                        continue;

                    case PvOp.Call:
                        Flatten(*(ulong*)(r + 8), depth + 1);
                        break;

                    default:
                        {
                            _stats.Count(op);
                            Span<byte> dst = _flat.Append((int)size);
                            new ReadOnlySpan<byte>(r, (int)size).CopyTo(dst);

                            if (op is PvOp.BindTextures or PvOp.BindImages or PvOp.BindSamplers)
                            {
                                ResolveDeferred(dst);
                            }

                            break;
                        }
                }

                p += size;
            }
        }

        // p_bind_handles {int stage, first, n, pad; uint64 src; uint64 h[n]}: src != 0 names the game's array.
        private void ResolveDeferred(Span<byte> record)
        {
            ulong src = MemoryMarshal.Read<ulong>(record.Slice(8 + 16));
            int n = MemoryMarshal.Read<int>(record.Slice(8 + 8));

            if (src == 0 || n <= 0 || n > 32)
            {
                return;
            }

            _memory.Read(src, record.Slice(8 + 24, n * 8));
            _stats.Deferred++;
        }

        // ---------- The null replay: CPU-visible side effects only ----------

        private ulong _gpuTicks;

        private void NullReplay(byte[] buffer, int length)
        {
            fixed (byte* b = buffer)
            {
                for (int at = 0; at + 8 <= length;)
                {
                    PvOp op = (PvOp)((uint*)(b + at))[0];
                    uint size = ((uint*)(b + at))[1];

                    if (op == PvOp.End || size < 8)
                    {
                        break;
                    }

                    NullReplay(op, b + at + 8);
                    at += (int)size;
                }
            }
        }

        private void NullReplay(PvOp op, byte* payload)
        {
            switch (op)
            {
                case PvOp.UpdateUniformBuffer:
                    {
                        ulong dst = *(ulong*)payload;
                        ulong size = *(ulong*)(payload + 8);

                        if (size <= 0x10000)
                        {
                            _memory.Write(dst, new ReadOnlySpan<byte>(payload + 16, (int)size));
                        }

                        break;
                    }

                case PvOp.CopyBufferToBuffer:
                    {
                        ulong src = *(ulong*)payload;
                        ulong dst = *(ulong*)(payload + 8);
                        ulong size = *(ulong*)(payload + 16);

                        if (size <= 64 * 1024 * 1024)
                        {
                            byte[] tmp = new byte[size];
                            _memory.Read(src, tmp);
                            _memory.Write(dst, tmp);
                        }

                        break;
                    }

                case PvOp.ClearBuffer:
                    {
                        ulong addr = *(ulong*)payload;
                        ulong size = *(ulong*)(payload + 8);
                        uint value = *(uint*)(payload + 16);

                        if (size <= 64 * 1024 * 1024)
                        {
                            uint[] tmp = new uint[size / 4];
                            Array.Fill(tmp, value);
                            _memory.Write(addr, MemoryMarshal.AsBytes(tmp.AsSpan()));
                        }

                        break;
                    }

                case PvOp.ReportCounter:
                    {
                        int type = *(int*)payload;
                        ulong addr = *(ulong*)(payload + 8);

                        // NVNcounterData {counter, timestamp}. Timestamps (types 0, TOP) carry counter 0: the
                        // game's conditional clears rely on it. Occlusion counts report 1 (visible).
                        ulong counter = type == 0 ? 0UL : 1UL;
                        _gpuTicks += 1000;
                        _memory.Write(addr, counter);
                        _memory.Write(addr + 8, _gpuTicks);
                        break;
                    }

                case PvOp.SignalEvent:
                    {
                        ulong addr = *(ulong*)payload;
                        int mode = *(int*)(payload + 8);
                        uint value = *(uint*)(payload + 20);

                        if (mode == 1)
                        {
                            _memory.Write(addr, _memory.Read<uint>(addr) + value);
                        }
                        else
                        {
                            _memory.Write(addr, value);
                        }

                        break;
                    }
            }
        }

        // ---------- Windows ----------

        public void WindowInitialized(ulong window, int count, ulong textures)
        {
            Window w = new() { Count = count, Textures = new PvTex[count] };

            for (int i = 0; i < count; i++)
            {
                w.Textures[i] = _memory.Read<PvTex>(textures + (ulong)(i * Marshal.SizeOf<PvTex>()));
            }

            lock (_windows)
            {
                _windows[window] = w;
            }

            PvTex t = w.Textures[0];
            Logger.Info?.Print(LogClass.Gpu, $"nvnpv: window 0x{window:X}: {count} textures, {t.D.Width}x{t.D.Height} format 0x{t.D.Format:X}");
        }

        public int Acquire(ulong window, int active)
        {
            Window w;

            lock (_windows)
            {
                _windows.TryGetValue(window, out w);
            }

            if (w == null || active <= 0)
            {
                return 0;
            }

            int index = Renderer?.Acquire(window, active) ?? w.Next++ % active;

            return index;
        }

        public void Present(ulong window, int index, ulong tex, ulong fence)
        {
            PvTex t = _memory.Read<PvTex>(tex);

            lock (_submitLock)
            {
                Renderer?.Present(window, index, t, fence);
            }

            _stats.Present(this);
        }

        public void Texels(ulong request)
        {
            PvTexels q = _memory.Read<PvTexels>(request);

            if (Renderer != null)
            {
                Renderer.Texels(q);
            }
            else if (q.Write == 0)
            {
                _stats.ReadTexels++;
            }
        }

        // ---------- Statistics ----------

        private sealed class Stats
        {
            public readonly long[] Ops = new long[(int)PvOp.Count];
            public long BadHandles, Corrupt, TooDeep, ReadTexels, Deferred;
            private long _presents;
            private long _lastTicks = Environment.TickCount64;
            private readonly long[] _lastOps = new long[(int)PvOp.Count];

            public void Count(PvOp op) => Ops[(int)op]++;

            public void Present(NvnPvSession s)
            {
                long n = Interlocked.Increment(ref _presents);

                if (n % 300 != 0)
                {
                    return;
                }

                long now = Environment.TickCount64;
                double seconds = (now - _lastTicks) / 1000.0;
                _lastTicks = now;

                StringBuilder sb = new();
                sb.Append($"nvnpv: {300 / seconds:F1} presents/s; per frame:");

                for (int i = 0; i < Ops.Length; i++)
                {
                    long d = Ops[i] - _lastOps[i];
                    _lastOps[i] = Ops[i];

                    if (d != 0)
                    {
                        sb.Append($" {(PvOp)i}={d / 300.0:F1}");
                    }
                }

                IVirtualMemoryManager m = s._memory;
                sb.Append($"; live recordings {m.Read<ulong>(s._ctl + CtlRecordingsLive)}, segments {m.Read<ulong>(s._ctl + CtlSegmentsLive)}, " +
                          $"arena {m.Read<ulong>(s._ctl + CtlArenaBump) >> 20} MiB, unimplemented calls {m.Read<ulong>(s._ctl + CtlUnimplCalls)}, " +
                          $"bad handles {BadHandles}, corrupt {Corrupt}");

                Logger.Info?.Print(LogClass.Gpu, sb.ToString());
            }
        }
    }

    /// <summary>What replays submissions (the GAL renderer); absent = the null replay.</summary>
    interface INvnPvRenderer
    {
        void PoolChanged(ulong addr, ulong size, uint flags, bool add);
        void VirtualMapping(ulong gpuVa, ulong guestVa, ulong size);   // guestVa = 0: unmapped
        void Submit(byte[] records, int length, ulong fence, Action<ulong> completed);
        int Acquire(ulong window, int active);
        void Present(ulong window, int index, PvTex texture, ulong fence);
        void Texels(PvTexels request);
        void TextureFinalized(ulong address);
    }
}
