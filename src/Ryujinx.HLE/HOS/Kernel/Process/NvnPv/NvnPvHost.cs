using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Nvn;
using Ryujinx.Cpu;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.Loaders.Elf;
using Ryujinx.Horizon.Common;
using Ryujinx.Memory;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Ryujinx.HLE.HOS.Kernel.Process.NvnPv
{
    /// <summary>
    /// Host side of the paravirtual NVN layer (RYUJINX_NVN_PV=1).
    ///
    /// At process start the guest object (Guest/nvnpv.c, compiled into this assembly as
    /// Ryujinx.HLE.NvnPv.nvnpv.o) is linked into two blocks mapped next to nnSdk: code
    /// (read/execute, preceded by one page of host-written stubs) and data (read/write). The
    /// first instruction of nvnBootstrapLoader becomes a branch to the guest's pv_bootstrap; a
    /// stub that runs the displaced instruction and branches back lets the guest call the real
    /// loader. From then on the game gets its NVN functions from the guest layer, which reaches
    /// the host only through SVC 0xF8 (operation in x8).
    ///
    /// Not compatible with the NVN call counter (RYUJINX_NVN_PROBE=2): both hook the bootstrap.
    /// </summary>
    static unsafe class NvnPvHost
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("RYUJINX_NVN_PV") is { Length: > 0 } v && v != "0";

        // RYUJINX_NVN_PV=1: the layer forwards everything to the real driver (probe); 2: it takes NVN over.
        public static readonly bool TakeOver = Environment.GetEnvironmentVariable("RYUJINX_NVN_PV") == "2";

        // RYUJINX_NVN_PV_REPLAY=0: with the layer in charge, replay nothing (the null replay: only what the CPU can see).
        public static readonly bool Replay = Environment.GetEnvironmentVariable("RYUJINX_NVN_PV_REPLAY") != "0";

        // Arena for command records and descriptor tables (RYUJINX_NVN_PV_ARENA_MB, default 256).
        private static readonly ulong ArenaBytes = (ulong)(int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_NVN_PV_ARENA_MB"), out int mb) && mb > 0 ? mb : 256) << 20;

        public const int SvcId = 0xF8;

        private const int OpLog = 1;
        private const int OpHello = 2;
        private const int OpUnimpl = 3;
        private const int OpPool = 4;
        private const int OpVpool = 5;
        private const int OpVpoolMap = 6;
        private const int OpSubmit = 7;
        private const int OpWait = 8;
        private const int OpPresent = 9;
        private const int OpAcquire = 10;
        private const int OpTexels = 11;
        private const int OpWindow = 12;
        private const int OpFinish = 13;
        private const int OpProgram = 14;
        private const int OpTexFinal = 15;

        private const ulong HostVersion = 1;
        private const ulong StubPageSize = 0x1000;

        // Offsets in the guest's struct pv_ctl (nvnpv.c).
        private const ulong CtlMagic = 0x00;
        private const ulong CtlRealBootstrap = 0x08;
        private const ulong CtlBootCalls = 0x18;
        private const ulong CtlGpaCalls = 0x20;
        private const ulong CtlWrapped = 0x28;
        private const ulong CtlPresents = 0x30;
        private const ulong PvMagic = 0x313030565056564EUL; // "NVNPV001"

        private static KProcess _process;
        private static ulong _ctl;
        private static bool _installed;
        private static NvnPvSession _session;

        public static NvnPvSession Session => _session;

        private static KernelContext _context;

        public static void Install(KProcess process, KernelContext context)
        {
            try
            {
                _context = context;
                InstallImpl(process);
            }
            catch (Exception e)
            {
                Logger.Error?.Print(LogClass.Loader, $"nvnpv: install failed: {e}");
            }
        }

        private static void InstallImpl(KProcess process)
        {
            HleProcessDebugger.Image sdk = process.Debugger.GetLoadedImages().FirstOrDefault(i => i.Name == "nnSdk");

            if (sdk == null)
            {
                Logger.Warning?.Print(LogClass.Loader, "nvnpv: no nnSdk module");
                return;
            }

            ElfSymbol boot = sdk.Symbols.FirstOrDefault(s => s.Name == "nvnBootstrapLoader" && s.ShIdx != 0 && s.Value != 0);

            if (boot.Name == null)
            {
                Logger.Warning?.Print(LogClass.Loader, "nvnpv: nnSdk does not define nvnBootstrapLoader");
                return;
            }

            GuestObject obj = new(LoadGuestObject());

            IVirtualMemoryManager memory = process.CpuMemory;
            ulong bootAddress = sdk.BaseAddress + boot.Value;
            uint displaced = memory.Read<uint>(bootAddress);

            if (Arm64.IsPcRelative(displaced))
            {
                Logger.Warning?.Print(LogClass.Loader, $"nvnpv: first instruction 0x{displaced:X8} of nvnBootstrapLoader is PC-relative, not hooking");
                return;
            }

            ulong codeBytes = StubPageSize + obj.CodeSize;
            ulong dataBytes = obj.DataSize;

            // Code, a guard page, then data, in one free block near nnSdk (branch range of the
            // hook). Chosen from the memory map, never by trying MapPages over live mappings:
            // KPageTableBase.CheckRange does not check the last block of a range.
            ulong total = codeBytes + 0x1000 + dataBytes;
            ulong codeVa = FindFreeBlock(process, sdk.BaseAddress, bootAddress, total);

            if (codeVa == 0)
            {
                Logger.Warning?.Print(LogClass.Loader, "nvnpv: no free block near nnSdk");
                return;
            }

            ulong dataVa = codeVa + codeBytes + 0x1000;

            Result result = process.MemoryManager.MapPages(codeVa, codeBytes / 0x1000, MemoryState.CodeMutable, KMemoryPermission.ReadAndExecute);

            if (result == Result.Success)
            {
                result = process.MemoryManager.MapPages(dataVa, dataBytes / 0x1000, MemoryState.CodeMutable, KMemoryPermission.ReadAndWrite);
            }

            if (result != Result.Success)
            {
                Logger.Warning?.Print(LogClass.Loader, $"nvnpv: mapping the guest layer failed ({result})");
                return;
            }

            ulong codeBase = codeVa + StubPageSize;
            (byte[] code, byte[] data) = obj.Link(codeBase, dataVa);

            ulong ctl = obj.GetSymbolAddress("pv_ctl", codeBase, dataVa);
            ulong entry = obj.GetSymbolAddress("pv_bootstrap", codeBase, dataVa);

            if (Math.Abs((long)(entry - bootAddress)) >= 0x8000000)
            {
                Logger.Warning?.Print(LogClass.Loader, "nvnpv: guest layer out of branch range of nvnBootstrapLoader");
                return;
            }

            // Stub page: the real bootstrap (displaced first instruction, branch back).
            byte[] stubs = new byte[StubPageSize];
            BitConverter.TryWriteBytes(stubs.AsSpan(0), displaced);
            BitConverter.TryWriteBytes(stubs.AsSpan(4), Arm64.B(codeVa + 4, bootAddress + 4));

            ulong ctlOffset = ctl - dataVa;
            BitConverter.TryWriteBytes(data.AsSpan((int)(ctlOffset + CtlRealBootstrap)), codeVa);

            if (BitConverter.ToUInt64(data, (int)(ctlOffset + CtlMagic)) != PvMagic)
            {
                Logger.Warning?.Print(LogClass.Loader, "nvnpv: control block magic mismatch, not hooking");
                return;
            }

            ulong arenaVa = 0;
            nint arenaHost = 0;

            if (TakeOver)
            {
                NvnPvRecords.Check();

                (arenaVa, arenaHost) = MapArena(process, dataVa + dataBytes);

                if (arenaVa == 0)
                {
                    Logger.Warning?.Print(LogClass.Loader, "nvnpv: no arena, not taking NVN over");
                    return;
                }

                BitConverter.TryWriteBytes(data.AsSpan((int)(ctlOffset + NvnPvSession.CtlMode)), 1UL);
                BitConverter.TryWriteBytes(data.AsSpan((int)(ctlOffset + NvnPvSession.CtlArenaBase)), arenaVa);
                BitConverter.TryWriteBytes(data.AsSpan((int)(ctlOffset + NvnPvSession.CtlArenaSize)), ArenaBytes);
            }

            memory.Write(codeVa, stubs);
            memory.Write(codeBase, code);
            memory.Write(dataVa, data);

            if (memory.Read<ulong>(ctl + CtlRealBootstrap) != codeVa || memory.Read<uint>(codeVa) != displaced)
            {
                Logger.Warning?.Print(LogClass.Loader, "nvnpv: guest layer did not read back, not hooking");
                return;
            }

            memory.Write(bootAddress, Arm64.B(bootAddress, entry));

            _process = process;
            _ctl = ctl;
            _installed = true;

            if (TakeOver)
            {
                _session = new NvnPvSession(process, ctl, arenaVa, (byte*)arenaHost, ArenaBytes);

                if (Replay)
                {
                    NvnPvRecords.Check();
                    _session.Renderer = new NvnPvGalRenderer(_context.Device, process, ctl + NvnPvSession.CtlPoolGeneration);
                }

                Logger.Warning?.Print(LogClass.Gpu, $"nvnpv: {(Replay ? "GAL replay" : "null replay")}");
            }

            new Thread(ReportLoop) { Name = "NvnPv.Report", IsBackground = true }.Start();

            Logger.Warning?.Print(LogClass.Loader,
                $"nvnpv: guest layer at code 0x{codeVa:X} (+0x{codeBytes:X}), data 0x{dataVa:X} (+0x{dataBytes:X}); " +
                $"nvnBootstrapLoader 0x{bootAddress:X} -> pv_bootstrap 0x{entry:X}");
        }

        /// <summary>
        /// Maps the arena: physically contiguous pages, so the host reads records through one pointer.
        /// Taken from the application pool if it has room, else from the system pools.
        /// </summary>
        private static (ulong Va, nint Host) MapArena(KProcess process, ulong after)
        {
            KernelContext context = _context;
            ulong pages = ArenaBytes / KPageTableBase.PageSize;

            ulong va = FindFreeBlock(process, after, after, ArenaBytes);

            if (va == 0)
            {
                return (0, 0);
            }

            foreach (MemoryRegion region in new[] { MemoryRegion.Application, MemoryRegion.NvServices, MemoryRegion.Service })
            {
                KMemoryRegionManager manager = context.MemoryManager.MemoryRegions[(int)region];
                ulong free = manager.GetFreePages();

                if (free < pages + 0x10000)   // leave the pool at least 256 MiB
                {
                    Logger.Info?.Print(LogClass.Loader, $"nvnpv: {region} pool has {free * 4 / 1024} MiB free, too little for the arena");
                    continue;
                }

                ulong pa = manager.AllocatePagesContiguous(context, pages, false);

                if (pa == 0)
                {
                    continue;
                }

                KPageList list = new();
                list.AddRange(pa, pages);

                Result result = process.MemoryManager.MapPages(va, list, MemoryState.CodeMutable, KMemoryPermission.ReadAndWrite);

                if (result != Result.Success)
                {
                    Logger.Warning?.Print(LogClass.Loader, $"nvnpv: mapping the arena failed ({result})");
                    return (0, 0);
                }

                byte* host = (byte*)context.Memory.GetPointer(pa - DramMemoryMap.DramBase, ArenaBytes);
                new Span<byte>(host, (int)Math.Min(ArenaBytes, int.MaxValue)).Clear();

                Logger.Warning?.Print(LogClass.Loader, $"nvnpv: arena of {ArenaBytes >> 20} MiB at 0x{va:X} (physical 0x{pa:X}, {region} pool)");

                return (va, (nint)host);
            }

            return (0, 0);
        }

        private static byte[] LoadGuestObject()
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Ryujinx.HLE.NvnPv.nvnpv.o")
                ?? throw new InvalidOperationException("the guest object was not built into this assembly (clang missing at build time?)");

            using MemoryStream ms = new();
            stream.CopyTo(ms);

            return ms.ToArray();
        }

        private static ulong FindFreeBlock(KProcess process, ulong start, ulong near, ulong size)
        {
            for (ulong a = start; a < near + 0x7000000;)
            {
                KMemoryInfo info = process.MemoryManager.QueryMemory(a);
                ulong blockEnd = info.Address + info.Size;

                if (info.Size == 0 || blockEnd <= a)
                {
                    break;
                }

                // 1 MiB aligned, so a guard gap separates the block from whatever ends before it.
                ulong candidate = (info.Address + 0xFFFFF) & ~0xFFFFFUL;

                if (info.State == MemoryState.Unmapped && candidate + size + 0x100000 <= blockEnd)
                {
                    return candidate;
                }

                a = blockEnd;
            }

            return 0;
        }

        /// <summary>Handles SVC 0xF8 from the guest layer; false for every other id.</summary>
        public static bool TryHandleSvc(IExecutionContext context, int id)
        {
            if (id != SvcId || !_installed)
            {
                return false;
            }

            int op = (int)context.GetX(8);

            switch (op)
            {
                case OpLog:
                    Logger.Info?.Print(LogClass.Gpu, ReadString(context.GetX(0)));
                    break;

                case OpHello:
                    Logger.Warning?.Print(LogClass.Gpu, $"nvnpv: guest layer says hello (control block 0x{context.GetX(0):X})");
                    context.SetX(0, HostVersion);
                    break;

                case OpUnimpl:
                    Logger.Warning?.Print(LogClass.Gpu, $"nvnpv: unimplemented NVN entry point called: {ReadString(context.GetX(0))}");
                    break;

                case OpPool:
                    context.SetX(0, _session?.PoolChanged(context.GetX(0), context.GetX(1), (uint)context.GetX(2), context.GetX(3) != 0) ?? 0);
                    break;

                case OpVpool:
                    context.SetX(0, _session?.VirtualPoolCreate(context.GetX(0)) ?? 0);
                    break;

                case OpVpoolMap:
                    context.SetX(0, _session != null && _session.VirtualPoolMap(context.GetX(0), (int)context.GetX(1), context.GetX(2), context.GetX(3)) ? 1UL : 0UL);
                    break;

                case OpSubmit:
                    _session?.Submit((int)context.GetX(0), (int)context.GetX(1), context.GetX(2), context.GetX(3));
                    break;

                case OpWait:
                    context.SetX(0, _session != null && _session.Wait(context.GetX(0), context.GetX(1)) ? 1UL : 0UL);
                    break;

                case OpFinish:
                    _session?.Wait(context.GetX(0), ulong.MaxValue);
                    break;

                case OpPresent:
                    _session?.Present(context.GetX(0), (int)context.GetX(1), context.GetX(2), context.GetX(3));
                    break;

                case OpAcquire:
                    context.SetX(0, (ulong)(_session?.Acquire(context.GetX(0), (int)context.GetX(1)) ?? 0));
                    break;

                case OpTexels:
                    _session?.Texels(context.GetX(0));
                    break;

                case OpWindow:
                    _session?.WindowInitialized(context.GetX(0), (int)context.GetX(1), context.GetX(2));
                    break;

                case OpProgram:
                    break;

                case OpTexFinal:
                    // x0 = serial, x1 = storage address: references the replay holds for it can go.
                    _session?.Renderer?.TextureFinalized(context.GetX(1));
                    break;

                default:
                    Logger.Error?.Print(LogClass.Gpu, $"nvnpv: unknown host call {op} from 0x{context.Pc:X}");
                    context.SetX(0, ulong.MaxValue);
                    break;
            }

            return true;
        }

        private static string ReadString(ulong address)
        {
            if (address == 0)
            {
                return "(null)";
            }

            StringBuilder sb = new();

            for (int i = 0; i < 4096; i++)
            {
                byte b = _process.CpuMemory.Read<byte>(address + (ulong)i);

                if (b == 0)
                {
                    break;
                }

                sb.Append((char)b);
            }

            return sb.ToString();
        }

        private static void ReportLoop()
        {
            ulong lastPresents = ulong.MaxValue;

            while (true)
            {
                Thread.Sleep(5000);

                try
                {
                    IVirtualMemoryManager memory = _process.CpuMemory;
                    ulong presents = memory.Read<ulong>(_ctl + CtlPresents);

                    if (presents == lastPresents)
                    {
                        continue;
                    }

                    lastPresents = presents;

                    Logger.Info?.Print(LogClass.Gpu,
                        $"nvnpv: bootstrap calls {memory.Read<ulong>(_ctl + CtlBootCalls)}, GetProcAddress calls {memory.Read<ulong>(_ctl + CtlGpaCalls)}, " +
                        $"wrapped {memory.Read<ulong>(_ctl + CtlWrapped)}, presents {presents}");
                }
                catch (Exception e)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"nvnpv: report failed: {e.Message}");
                    return;
                }
            }
        }
    }

    static class Arm64
    {
        public static uint B(ulong from, ulong to) => 0x14000000u | ((uint)((long)(to - from) >> 2) & 0x3FFFFFFu);

        /// <summary>Instructions whose meaning depends on where they execute (cannot be displaced).</summary>
        public static bool IsPcRelative(uint insn)
        {
            return (insn & 0x1F000000) == 0x10000000   // ADR / ADRP
                || (insn & 0x7C000000) == 0x14000000   // B / BL
                || (insn & 0xFE000000) == 0x54000000   // B.cond
                || (insn & 0x7E000000) == 0x34000000   // CBZ / CBNZ
                || (insn & 0x7E000000) == 0x36000000   // TBZ / TBNZ
                || (insn & 0x3B000000) == 0x18000000;  // LDR (literal)
        }
    }
}
