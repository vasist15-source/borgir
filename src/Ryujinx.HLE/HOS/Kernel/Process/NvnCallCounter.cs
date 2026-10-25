using Ryujinx.Common.Logging;
using Ryujinx.Cpu;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.Loaders.Elf;
using Ryujinx.Horizon.Common;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Ryujinx.HLE.HOS.Kernel.Process
{
    /// <summary>
    /// Counts the game's NVN calls per function, to size an NVN-level graphics path.
    ///
    /// The game imports a single NVN symbol, nvnBootstrapLoader, and asks it for
    /// nvnDeviceGetProcAddress; every other NVN function comes out of that. Both are hooked:
    /// the bootstrap's first instruction becomes a branch into a page this maps next to
    /// nnSdk, and each pointer the game is handed back is replaced by a counting thunk -
    /// an atomic add on its own counter, then a branch to the real function. Handing out
    /// pointers costs a supervisor call each (a few hundred, at start-up); the calls
    /// themselves never leave the guest, so counting does not change the game's pacing.
    ///
    /// The page starts with 0x100 zero bytes, so nothing that mistakes it for a module reads
    /// an instruction as a header offset. Then, at fixed offsets: the bootstrap trampoline (SVC 0xF0, the displaced
    /// first instruction, branch back), the bootstrap return stub (SVC 0xF1, BR x17), the
    /// GetProcAddress wrapper (SVC 0xF2, load the real address, BR x16) and its return stub
    /// (SVC 0xF3, BR x17), then 4096 thunks with their counters and targets. Thunk code is
    /// written once before the game starts; only data changes afterwards, so no instruction
    /// cache maintenance is ever needed. x16/x17 are the intra-procedure-call scratch
    /// registers, free at a call boundary by the AArch64 procedure call standard.
    ///
    /// RYUJINX_NVN_PROBE=2 (1 is the symbol inventory alone). Every 5 s it writes
    /// /tmp/ryujinx-nvn-calls.txt and one log line.
    ///
    /// Tracing, for the render-pass structure: each thunk also tests its own trace flag (data,
    /// next to the counters) and, when set, makes SVC 0xF4 with its index in x17 before going
    /// on to the real function; EndRecording additionally returns through SVC 0xF5 so the
    /// command handle it hands back is seen. Writing a number of seconds to
    /// /tmp/ryujinx-nvn-trace-request sets the flags of the structural calls (render targets,
    /// draws, dispatches, clears, copies, barriers, counters, tiled-cache actions, command
    /// recording and calls, queue, window and sync) for that long, 3 s by default, and writes
    /// every traced call with its arguments to /tmp/ryujinx-nvn-trace.txt. Binds and plain
    /// state setters are never trapped, so the game slows down only by the traced calls.
    ///
    /// The game replays command lists it recorded long before (while the world loads), so a
    /// window alone misses most draws. RYUJINX_NVN_TRACE_RECORDINGS=1 keeps the structural
    /// flags set from boot and remembers the latest recording behind every command handle;
    /// a window then also writes out each recording it references, directly or through
    /// nested calls, that was made before it opened.
    ///
    /// Function pointers can also be obtained past the hooked bootstrap: through
    /// nvnBootstrapLoaderInternal, nvnDevtoolsBootstrap, or the real GetProcAddress called
    /// directly. Those entries are patched too (SVC 0x100 + hook number, the displaced
    /// instruction, branch back), every name they hand out is wrapped, and the report lists
    /// which module calls each of them. A pointer that is already a thunk is never wrapped
    /// again, and only names starting with "nvn" are wrapped at all.
    /// </summary>
    static class NvnCallCounter
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("RYUJINX_NVN_PROBE") == "2";

        private const int SvcBootEntry = 0xF0;
        private const int SvcBootReturn = 0xF1;
        private const int SvcGpaEntry = 0xF2;
        private const int SvcGpaReturn = 0xF3;
        private const int SvcTrace = 0xF4;
        private const int SvcTraceReturn = 0xF5;
        private const int SvcHookBase = 0x100;
        private const int MaxHooks = 48;
        private const ulong HooksOffset = 0x200;
        private const ulong HookBytes = 32;

        private const int ThunkCount = 4096;
        private const int ThunkBytes = 64;
        private const ulong ThunksOffset = 0x1000;
        private const ulong CountersOffset = ThunksOffset + ThunkCount * ThunkBytes;
        private const ulong TargetsOffset = CountersOffset + ThunkCount * 8;
        private const ulong TraceFlagsOffset = TargetsOffset + ThunkCount * 8;
        private const ulong PageBytes = TraceFlagsOffset + ThunkCount * 8;

        private const ulong OffBootTramp = 0x100;
        private const ulong OffBootRet = 0x110;
        private const ulong OffGpaWrap = 0x120;
        private const ulong OffGpaRet = 0x130;
        private const ulong OffGpaSlot = 0x140;
        private const ulong OffTraceRet = 0x150;

        private const string TraceRequestPath = "/tmp/ryujinx-nvn-trace-request";
        private const string TracePath = "/tmp/ryujinx-nvn-trace.txt";
        private const int MaxTraceEvents = 4_000_000;

        // NVNtextureView is 40 bytes; render-target views are compared by content, because
        // the game builds them in reused stack slots every frame.
        private const int ViewWords = 5;

        private static KProcess _process;
        private static Switch _device;
        private static ulong _page;
        private static bool _installed;

        private static readonly object _lock = new();
        private static readonly Dictionary<(string, ulong), int> _byNameAndTarget = new();
        private static readonly List<string> _names = new();
        private static readonly string[] _nameByIndex = new string[ThunkCount];
        private static readonly bool[] _capturesReturn = new bool[ThunkCount];

        private struct TraceEvent
        {
            public long Seq;
            public int Thread;
            public int Index;
            public bool Return;
            public ulong A0, A1, A2, A3, A4, A5, A6, A7;
            public ulong[] Extra;
        }

        private enum HookKind
        {
            Bootstrap,      // (const char* name) -> function pointer
            GetProcAddress, // (const NVNdevice* device, const char* name) -> function pointer
            CountOnly,      // signature unknown: callers are counted, results left alone
        }

        private sealed class Hook
        {
            public string Name;
            public ulong Address;
            public HookKind Kind;
            public long Calls;
            public long ViaThunk;
            public readonly Dictionary<string, long> Callers = new();
        }

        // RYUJINX_NVN_ENTRY_CENSUS=1: also patch the real entries of these, to count every call
        // however the caller got the pointer, and to name the code that calls them directly.
        private static readonly bool _entryCensus = Environment.GetEnvironmentVariable("RYUJINX_NVN_ENTRY_CENSUS") == "1";
        private static readonly HashSet<string> _censusNames = new()
        {
            "nvnCommandBufferBeginRecording", "nvnCommandBufferEndRecording", "nvnCommandBufferSetRenderTargets",
            "nvnCommandBufferDrawElementsInstanced", "nvnCommandBufferDrawElementsBaseVertex", "nvnCommandBufferDrawElements",
            "nvnCommandBufferDrawArrays", "nvnCommandBufferDrawArraysInstanced", "nvnCommandBufferBarrier",
        };

        private static readonly Hook[] _hooks = new Hook[MaxHooks];
        private static int _hookCount;
        private static readonly HashSet<ulong> _hookedAddresses = new();
        private static (string Name, ulong Start, ulong End)[] _modules = Array.Empty<(string, ulong, ulong)>();

        private static readonly List<TraceEvent> _trace = new();
        private static volatile bool _tracing;
        private static long _seq;

        private static readonly bool _trackRecordings = Environment.GetEnvironmentVariable("RYUJINX_NVN_TRACE_RECORDINGS") == "1";
        private const int MaxRecordings = 200_000;

        private sealed class Recording
        {
            public TraceEvent[] Calls;
            public TraceEvent End;
            public TraceEvent Return;
        }

        private sealed class PendingEnd
        {
            public List<TraceEvent> Calls;
            public TraceEvent End;
        }

        private static readonly object _recordingLock = new();
        private static readonly Dictionary<ulong, List<TraceEvent>> _openByCommandBuffer = new();
        private static readonly Dictionary<ulong, Recording> _recordingByHandle = new();
        private static bool _recordingCapWarned;

        private readonly struct Frame
        {
            public readonly ulong ReturnAddress;
            public readonly string Name;
            public readonly int Index;
            public readonly PendingEnd Pending;

            public Frame(ulong returnAddress, string name, int index = -1, PendingEnd pending = null)
            {
                ReturnAddress = returnAddress;
                Name = name;
                Index = index;
                Pending = pending;
            }
        }

        [ThreadStatic]
        private static Stack<Frame> _frames;

        public static void Install(KProcess process, KernelContext context)
        {
            try
            {
                _device = context.Device;
                InstallImpl(process);
            }
            catch (Exception e)
            {
                Logger.Warning?.Print(LogClass.Loader, $"nvn-calls: install failed: {e.Message}");
            }
        }

        private static void InstallImpl(KProcess process)
        {
            HleProcessDebugger.Image sdk = process.Debugger.GetLoadedImages().FirstOrDefault(i => i.Name == "nnSdk");

            if (sdk == null)
            {
                Logger.Warning?.Print(LogClass.Loader, "nvn-calls: no nnSdk module");
                return;
            }

            ElfSymbol boot = sdk.Symbols.FirstOrDefault(s => s.Name == "nvnBootstrapLoader" && s.ShIdx != 0 && s.Value != 0);

            if (boot.Name == null)
            {
                Logger.Warning?.Print(LogClass.Loader, "nvn-calls: nnSdk does not define nvnBootstrapLoader");
                return;
            }

            IVirtualMemoryManager memory = process.CpuMemory;
            ulong bootAddress = sdk.BaseAddress + boot.Value;
            uint displaced = memory.Read<uint>(bootAddress);

            if (IsPcRelative(displaced))
            {
                Logger.Warning?.Print(LogClass.Loader, $"nvn-calls: first instruction 0x{displaced:X8} of nvnBootstrapLoader is PC-relative, not hooking");
                return;
            }

            // Diagnostic: the process memory map from the first module to past the SDK.
            {
                StringBuilder map = new();
                ulong a = 0x8000000;

                while (a < 0x10000000)
                {
                    KMemoryInfo info = process.MemoryManager.QueryMemory(a);
                    map.AppendLine($"0x{info.Address:X9}-0x{info.Address + info.Size:X9} {info.State,-20} {info.Permission}");

                    if (info.Size == 0 || info.Address + info.Size <= a)
                    {
                        break;
                    }

                    a = info.Address + info.Size;
                }

                File.WriteAllText("/tmp/ryujinx-nvn-memmap.txt", map.ToString());
            }

            // A free spot within branch range of the bootstrap, chosen from the memory map rather
            // than by trying MapPages: KPageTableBase.CheckRange never checks the last block of a
            // range, so MapPages also "succeeds" over a live mapping when the whole range lies in
            // one block. The first three runs of this probe replaced 0x31000 bytes of nnSdk's
            // read-only segment that way. Never CodeStatic either: the guest's loader walks every
            // CodeStatic RX region as a module and reads a MOD0 offset from its fifth byte.
            ulong pages = (PageBytes + 0xFFF) / 0x1000;
            ulong size = pages * 0x1000;
            ulong page = 0;
            Result result = KernelResult.InvalidMemState;

            for (ulong a = sdk.BaseAddress; page == 0 && a < bootAddress + 0x7000000;)
            {
                KMemoryInfo info = process.MemoryManager.QueryMemory(a);
                ulong blockEnd = info.Address + info.Size;

                if (info.Size == 0 || blockEnd <= a)
                {
                    break;
                }

                // 1 MiB aligned, so a guard gap separates the page from whatever ends before it.
                ulong candidate = (info.Address + 0xFFFFF) & ~0xFFFFFUL;

                if (info.State == MemoryState.Unmapped && candidate + size <= blockEnd)
                {
                    result = process.MemoryManager.MapPages(candidate, pages, MemoryState.CodeMutable, KMemoryPermission.ReadAndExecute);

                    if (result == Result.Success)
                    {
                        page = candidate;
                        Logger.Info?.Print(LogClass.Loader, $"nvn-calls: page at 0x{page:X} in the free block 0x{info.Address:X}-0x{blockEnd:X}");
                    }
                }

                a = blockEnd;
            }

            if (page == 0)
            {
                Logger.Warning?.Print(LogClass.Loader, $"nvn-calls: could not map a page near nnSdk ({result})");
                return;
            }

            // The code must land in the page just mapped; read it back before trusting it.
            if (!memory.IsMapped(page) || !memory.IsMapped(page + PageBytes - 1))
            {
                Logger.Warning?.Print(LogClass.Loader, $"nvn-calls: page 0x{page:X} is not mapped after mapping it");
                return;
            }

            if (Math.Abs((long)(page - bootAddress)) >= 0x8000000)
            {
                Logger.Warning?.Print(LogClass.Loader, "nvn-calls: page out of branch range");
                return;
            }

            byte[] code = new byte[PageBytes];

            void Put(ulong offset, uint insn) => BitConverter.TryWriteBytes(code.AsSpan((int)offset, 4), insn);

            Put(OffBootTramp + 0, Svc(SvcBootEntry));
            Put(OffBootTramp + 4, displaced);
            Put(OffBootTramp + 8, B(page + OffBootTramp + 8, bootAddress + 4));

            Put(OffBootRet + 0, Svc(SvcBootReturn));
            Put(OffBootRet + 4, Br(17));

            Put(OffGpaWrap + 0, Svc(SvcGpaEntry));
            Put(OffGpaWrap + 4, LdrLiteral(16, page + OffGpaWrap + 4, page + OffGpaSlot));
            Put(OffGpaWrap + 8, Br(16));

            Put(OffGpaRet + 0, Svc(SvcGpaReturn));
            Put(OffGpaRet + 4, Br(17));

            Put(OffTraceRet + 0, Svc(SvcTraceReturn));
            Put(OffTraceRet + 4, Br(17));

            for (int i = 0; i < ThunkCount; i++)
            {
                ulong t = ThunksOffset + (ulong)i * ThunkBytes;
                ulong at = page + t;

                Put(t + 0, Adr(16, at, page + CountersOffset + (ulong)i * 8));
                Put(t + 4, MovzX(17, 1));
                Put(t + 8, 0xF831021Fu);  // stadd x17, [x16]
                Put(t + 12, LdrLiteral(17, at + 12, page + TraceFlagsOffset + (ulong)i * 8));
                Put(t + 16, Cbz(17, at + 16, at + 28));
                Put(t + 20, MovzX(17, (uint)i));
                Put(t + 24, Svc(SvcTrace));
                Put(t + 28, LdrLiteral(16, at + 28, page + TargetsOffset + (ulong)i * 8));
                Put(t + 32, Br(16));
            }

            memory.Write(page, code);

            if (memory.Read<uint>(page + OffBootTramp) != Svc(SvcBootEntry) ||
                memory.Read<uint>(page + ThunksOffset + 32) != Br(16))
            {
                Logger.Warning?.Print(LogClass.Loader, "nvn-calls: thunk page did not read back, not hooking");
                return;
            }

            memory.Write(bootAddress, B(bootAddress, page + OffBootTramp));

            _process = process;
            _page = page;
            _installed = true;

            _modules = process.Debugger.GetLoadedImages().Select(i => (i.Name, i.BaseAddress, i.EndAddress)).ToArray();

            foreach ((string symbol, HookKind kind) in new[] { ("nvnBootstrapLoaderInternal", HookKind.Bootstrap), ("nvnDevtoolsBootstrap", HookKind.CountOnly) })
            {
                ElfSymbol sym = sdk.Symbols.FirstOrDefault(s => s.Name == symbol && s.ShIdx != 0 && s.Value != 0);

                if (sym.Name != null)
                {
                    HookEntry(sdk.BaseAddress + sym.Value, symbol, kind);
                }
            }

            new Thread(ReportLoop) { Name = "NvnCallCounter", IsBackground = true }.Start();
            new Thread(TraceControlLoop) { Name = "NvnTraceControl", IsBackground = true }.Start();

            Logger.Warning?.Print(LogClass.Loader,
                $"nvn-calls: hooked nvnBootstrapLoader at 0x{bootAddress:X} (displaced 0x{displaced:X8}), thunk page 0x{page:X}");
        }

        /// <summary>Handles the probe's supervisor calls; false for every other id.</summary>
        public static bool TryHandleSvc(IExecutionContext context, int id)
        {
            if (!_installed)
            {
                return false;
            }

            if (id >= SvcHookBase && id < SvcHookBase + _hookCount)
            {
                _frames ??= new Stack<Frame>();
                HookCalled(context, _hooks[id - SvcHookBase]);

                return true;
            }

            if (id < SvcBootEntry || id > SvcTraceReturn)
            {
                return false;
            }

            _frames ??= new Stack<Frame>();

            switch (id)
            {
                case SvcBootEntry:
                    _frames.Push(new Frame(context.GetX(30), ReadString(context.GetX(0))));
                    context.SetX(30, _page + OffBootRet);
                    break;

                case SvcGpaEntry:
                    _frames.Push(new Frame(context.GetX(30), ReadString(context.GetX(1))));
                    context.SetX(30, _page + OffGpaRet);
                    break;

                case SvcBootReturn:
                case SvcGpaReturn:
                    {
                        Frame frame = _frames.Count != 0 ? _frames.Pop() : new Frame(0, "?");
                        context.SetX(0, Wrap(frame.Name, context.GetX(0)));
                        context.SetX(17, frame.ReturnAddress);
                        break;
                    }

                case SvcTrace:
                    TraceCall(context);
                    break;

                case SvcTraceReturn:
                    {
                        Frame frame = _frames.Count != 0 ? _frames.Pop() : new Frame(0, "?");

                        TraceEvent ret = new()
                        {
                            Seq = Interlocked.Increment(ref _seq),
                            Thread = Environment.CurrentManagedThreadId,
                            Index = frame.Index,
                            Return = true,
                            A0 = context.GetX(0),
                        };

                        if (frame.Pending != null)
                        {
                            Remember(ret.A0, frame.Pending, ret);
                        }

                        if (_tracing)
                        {
                            Record(ret);
                        }

                        context.SetX(17, frame.ReturnAddress);
                        break;
                    }
            }

            return true;
        }

        private static void TraceCall(IExecutionContext context)
        {
            int index = (int)context.GetX(17);

            // A thread can pass its flag check just as the window closes; those calls go uncounted.
            bool window = _tracing;

            if ((!window && !_trackRecordings) || (uint)index >= ThunkCount || _nameByIndex[index] == null)
            {
                return;
            }

            TraceEvent e = new()
            {
                Seq = Interlocked.Increment(ref _seq),
                Thread = Environment.CurrentManagedThreadId,
                Index = index,
                A0 = context.GetX(0),
                A1 = context.GetX(1),
                A2 = context.GetX(2),
                A3 = context.GetX(3),
                A4 = context.GetX(4),
                A5 = context.GetX(5),
                A6 = context.GetX(6),
                A7 = context.GetX(7),
            };

            string name = _nameByIndex[index];
            e.Extra = ReadExtra(name, e.A1, e.A2, e.A3, e.A5);

            PendingEnd pending = _trackRecordings ? Track(name, e) : null;

            if (_capturesReturn[index])
            {
                _frames.Push(new Frame(context.GetX(30), name, index, pending));
                context.SetX(30, _page + OffTraceRet);
            }

            if (window)
            {
                Record(e);
            }
        }

        /// <summary>Adds a command-buffer call to that buffer's open recording; EndRecording takes it.</summary>
        private static PendingEnd Track(string name, in TraceEvent e)
        {
            if (!name.StartsWith("nvnCommandBuffer", StringComparison.Ordinal))
            {
                return null;
            }

            string op = name["nvnCommandBuffer".Length..];

            lock (_recordingLock)
            {
                switch (op)
                {
                    case "Initialize":
                    case "BeginRecording":
                        _openByCommandBuffer[e.A0] = new List<TraceEvent>();
                        return null;

                    case "Finalize":
                        _openByCommandBuffer.Remove(e.A0);
                        return null;

                    case "EndRecording":
                        _openByCommandBuffer.Remove(e.A0, out List<TraceEvent> calls);
                        return new PendingEnd { Calls = calls ?? new List<TraceEvent>(), End = e };
                }

                if (!_openByCommandBuffer.TryGetValue(e.A0, out List<TraceEvent> open))
                {
                    _openByCommandBuffer[e.A0] = open = new List<TraceEvent>();
                }

                open.Add(e);
            }

            return null;
        }

        private static void Remember(ulong handle, PendingEnd pending, in TraceEvent ret)
        {
            lock (_recordingLock)
            {
                if (_recordingByHandle.Count >= MaxRecordings && !_recordingByHandle.ContainsKey(handle))
                {
                    if (!_recordingCapWarned)
                    {
                        _recordingCapWarned = true;
                        Logger.Warning?.Print(LogClass.Loader, $"nvn-trace: {MaxRecordings} distinct command handles remembered; ignoring new ones");
                    }

                    return;
                }

                _recordingByHandle[handle] = new Recording { Calls = pending.Calls.ToArray(), End = pending.End, Return = ret };
            }
        }

        private static void Record(in TraceEvent e)
        {
            lock (_trace)
            {
                if (_trace.Count < MaxTraceEvents)
                {
                    _trace.Add(e);
                }
            }
        }

        /// <summary>The memory a traced call's arguments point at, where the pass structure needs it.</summary>
        private static ulong[] ReadExtra(string name, ulong a1, ulong a2, ulong a3, ulong a5)
        {
            try
            {
                switch (name)
                {
                    case "nvnCommandBufferSetRenderTargets":
                        {
                            // count, color textures, color view contents, depth view contents
                            // (the depth texture itself is a4, already in the event).
                            int n = (int)Math.Min((uint)a1, 8u);
                            List<ulong> x = new() { (ulong)n };

                            for (int i = 0; i < n; i++)
                            {
                                x.Add(a2 != 0 ? Read64(a2 + (ulong)i * 8) : 0);
                            }

                            for (int i = 0; i < n; i++)
                            {
                                AddView(x, a3 != 0 ? Read64(a3 + (ulong)i * 8) : 0);
                            }

                            AddView(x, a5);

                            return x.ToArray();
                        }

                    case "nvnCommandBufferCallCommands":
                    case "nvnCommandBufferCopyCommands":
                    case "nvnQueueSubmitCommands":
                        {
                            int n = (int)Math.Min((uint)a1, 256u);
                            ulong[] handles = new ulong[n];

                            for (int i = 0; i < n; i++)
                            {
                                handles[i] = Read64(a2 + (ulong)i * 8);
                            }

                            return handles;
                        }
                }
            }
            catch (Exception)
            {
                // An argument that does not point at mapped memory; the event keeps its registers.
            }

            return null;
        }

        private static void AddView(List<ulong> x, ulong view)
        {
            for (int w = 0; w < ViewWords; w++)
            {
                x.Add(view != 0 ? Read64(view + (ulong)w * 8) : 0);
            }
        }

        private static ulong Read64(ulong address) => _process.CpuMemory.Read<ulong>(address);

        /// <summary>Calls whose order decides where render passes have to begin and end.</summary>
        private static bool IsStructural(string name)
        {
            if (name.StartsWith("nvnQueue", StringComparison.Ordinal) ||
                name.StartsWith("nvnWindow", StringComparison.Ordinal) ||
                name.StartsWith("nvnSync", StringComparison.Ordinal))
            {
                return true;
            }

            if (!name.StartsWith("nvnCommandBuffer", StringComparison.Ordinal))
            {
                return false;
            }

            string op = name["nvnCommandBuffer".Length..];

            if (op.StartsWith("Bind", StringComparison.Ordinal) || op.StartsWith("Get", StringComparison.Ordinal))
            {
                return false;
            }

            if (op.StartsWith("Set", StringComparison.Ordinal))
            {
                return op == "SetRenderTargets" || op == "SetRasterizerDiscard" ||
                       op.StartsWith("SetTiledCache", StringComparison.Ordinal) ||
                       op.StartsWith("SetRenderEnable", StringComparison.Ordinal);
            }

            return true;
        }

        private static void TraceControlLoop()
        {
            while (true)
            {
                Thread.Sleep(500);

                if (!File.Exists(TraceRequestPath))
                {
                    continue;
                }

                double seconds = 3;

                try
                {
                    string text = File.ReadAllText(TraceRequestPath).Trim();
                    File.Delete(TraceRequestPath);

                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
                    {
                        seconds = Math.Clamp(s, 0.2, 60);
                    }
                }
                catch (IOException)
                {
                    continue;
                }

                try
                {
                    RunTrace(seconds);
                }
                catch (Exception e)
                {
                    _tracing = false;
                    Logger.Warning?.Print(LogClass.Loader, $"nvn-trace: failed: {e.Message}");
                }
            }
        }

        private static void RunTrace(double seconds)
        {
            string[] names;

            lock (_lock)
            {
                names = _names.ToArray();
            }

            List<int> traced = new();

            for (int i = 0; i < names.Length; i++)
            {
                if (IsStructural(names[i]))
                {
                    traced.Add(i);
                }
            }

            lock (_trace)
            {
                _trace.Clear();
            }

            Dictionary<ulong, Recording> before = null;

            if (_trackRecordings)
            {
                lock (_recordingLock)
                {
                    before = new Dictionary<ulong, Recording>(_recordingByHandle);
                }
            }

            _tracing = true;

            if (!_trackRecordings)
            {
                foreach (int i in traced)
                {
                    _process.CpuMemory.Write(_page + TraceFlagsOffset + (ulong)i * 8, 1UL);
                }
            }

            long start = Stopwatch.GetTimestamp();
            Thread.Sleep(TimeSpan.FromSeconds(seconds));

            if (!_trackRecordings)
            {
                foreach (int i in traced)
                {
                    _process.CpuMemory.Write(_page + TraceFlagsOffset + (ulong)i * 8, 0UL);
                }
            }

            double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;

            // Calls already past their flag check still land; EndRecording returns come later.
            Thread.Sleep(250);
            _tracing = false;

            TraceEvent[] windowEvents;

            lock (_trace)
            {
                windowEvents = _trace.ToArray();
                _trace.Clear();
            }

            // Recordings made before the window that it references, followed through nested calls.
            List<TraceEvent> earlier = new();
            int earlierRecordings = 0;

            if (before != null)
            {
                HashSet<ulong> done = new();
                Queue<ulong> pending = new();

                void Referenced(in TraceEvent e)
                {
                    if (e.Extra == null || e.Return || (uint)e.Index >= ThunkCount)
                    {
                        return;
                    }

                    string n = _nameByIndex[e.Index];

                    if (n == "nvnCommandBufferCallCommands" || n == "nvnCommandBufferCopyCommands" || n == "nvnQueueSubmitCommands")
                    {
                        foreach (ulong h in e.Extra)
                        {
                            if (done.Add(h))
                            {
                                pending.Enqueue(h);
                            }
                        }
                    }
                }

                foreach (TraceEvent e in windowEvents)
                {
                    Referenced(e);
                }

                while (pending.Count != 0)
                {
                    if (!before.TryGetValue(pending.Dequeue(), out Recording r))
                    {
                        continue;
                    }

                    earlierRecordings++;
                    earlier.AddRange(r.Calls);
                    earlier.Add(r.End);
                    earlier.Add(r.Return);

                    foreach (TraceEvent e in r.Calls)
                    {
                        Referenced(e);
                    }
                }
            }

            TraceEvent[] events = new TraceEvent[earlier.Count + windowEvents.Length];
            earlier.CopyTo(events);
            windowEvents.CopyTo(events, earlier.Count);
            Array.Sort(events, (a, b) => a.Seq.CompareTo(b.Seq));

            StringBuilder sb = new();
            sb.AppendLine($"# nvn trace: {windowEvents.Length} events in {elapsed:F2} s, {traced.Count} of {names.Length} functions traced, " +
                          $"plus {earlierRecordings} earlier recordings ({earlier.Count} events) the window references");
            sb.AppendLine("# seq thread name a0 a1 a2 a3 a4 a5 a6 a7 [| extra]   or   seq thread ret name x0");

            foreach (TraceEvent e in events)
            {
                string name = e.Index >= 0 && e.Index < ThunkCount ? _nameByIndex[e.Index] ?? "?" : "?";

                if (e.Return)
                {
                    sb.Append(e.Seq).Append(' ').Append(e.Thread).Append(" ret ").Append(name).Append(' ').AppendLine(e.A0.ToString("X"));
                    continue;
                }

                sb.Append(e.Seq).Append(' ').Append(e.Thread).Append(' ').Append(name)
                  .Append(' ').Append(e.A0.ToString("X")).Append(' ').Append(e.A1.ToString("X"))
                  .Append(' ').Append(e.A2.ToString("X")).Append(' ').Append(e.A3.ToString("X"))
                  .Append(' ').Append(e.A4.ToString("X")).Append(' ').Append(e.A5.ToString("X"))
                  .Append(' ').Append(e.A6.ToString("X")).Append(' ').Append(e.A7.ToString("X"));

                if (e.Extra != null)
                {
                    sb.Append(" |");

                    foreach (ulong v in e.Extra)
                    {
                        sb.Append(' ').Append(v.ToString("X"));
                    }
                }

                sb.AppendLine();
            }

            File.WriteAllText(TracePath, sb.ToString());

            Logger.Warning?.Print(LogClass.Loader, $"nvn-trace: {windowEvents.Length} events in {elapsed:F2} s ({traced.Count} functions), " +
                                                   $"{earlierRecordings} earlier recordings -> {TracePath}");
        }

        /// <summary>
        /// Patches a guest function's first instruction with a branch to a stub that makes
        /// SVC 0x100 + n, runs the displaced instruction and branches back. Refuses entries
        /// whose first instruction depends on its address (other than a plain branch).
        /// </summary>
        private static bool HookEntry(ulong address, string name, HookKind kind)
        {
            lock (_lock)
            {
                if (_hookedAddresses.Contains(address))
                {
                    return true;
                }

                if (_hookCount == MaxHooks)
                {
                    return false;
                }

                IVirtualMemoryManager memory = _process.CpuMemory;
                uint first = memory.Read<uint>(address);
                ulong stub = _page + HooksOffset + (ulong)_hookCount * HookBytes;
                uint moved;

                ulong literal = 0;
                bool useLiteral = false;

                if ((first & 0x7C000000) == 0x14000000)
                {
                    // b / bl: re-aim from the stub at the same absolute target.
                    long offset = (long)((int)(first << 6) >> 6) * 4;
                    ulong target = (ulong)((long)address + offset);
                    moved = (first & 0x80000000) | (B(stub + 4, target) & 0x7FFFFFFF);
                }
                else if ((first & 0x9F000000) == 0x90000000)
                {
                    // adrp xd: load the page address it would have computed from a literal.
                    long imm = ((long)(first >> 5 & 0x7FFFF) << 2) | (first >> 29 & 3);
                    imm = (imm << 43) >> 43; // sign-extend 21 bits
                    literal = (ulong)((long)(address & ~0xFFFUL) + (imm << 12));
                    moved = LdrLiteral((int)(first & 0x1F), stub + 4, stub + 16);
                    useLiteral = true;
                }
                else if (IsPcRelative(first))
                {
                    Logger.Warning?.Print(LogClass.Loader, $"nvn-calls: {name} at 0x{address:X} starts with PC-relative 0x{first:X8}, not hooked");
                    return false;
                }
                else
                {
                    moved = first;
                }

                if (Math.Abs((long)(stub - address)) >= 0x8000000)
                {
                    return false;
                }

                int n = _hookCount;
                _hooks[n] = new Hook { Name = name, Address = address, Kind = kind };

                memory.Write(stub + 0, Svc(SvcHookBase + n));
                memory.Write(stub + 4, moved);
                memory.Write(stub + 8, B(stub + 8, address + 4));
                memory.Write(stub + 12, 0xD503201Fu); // nop

                if (useLiteral)
                {
                    memory.Write(stub + 16, literal);
                }

                _hookCount = n + 1;
                _hookedAddresses.Add(address);

                // Last, and as one aligned word: a thread entering now runs either instruction.
                memory.Write(address, B(address, stub));

                Logger.Warning?.Print(LogClass.Loader, $"nvn-calls: hooked {name} at 0x{address:X} (first 0x{first:X8}) as {kind}, svc 0x{SvcHookBase + n:X}");

                return true;
            }
        }

        private static void HookCalled(IExecutionContext context, Hook hook)
        {
            ulong lr = context.GetX(30);

            // A thunk reaches the real function with BR x16, x16 holding its address.
            bool viaThunk = context.GetX(16) == hook.Address;

            lock (hook)
            {
                hook.Calls++;

                if (viaThunk)
                {
                    hook.ViaThunk++;
                }
                else
                {
                    string caller = ModuleOf(lr);
                    hook.Callers[caller] = hook.Callers.GetValueOrDefault(caller) + 1;
                }
            }

            switch (hook.Kind)
            {
                case HookKind.Bootstrap:
                    _frames.Push(new Frame(lr, ReadString(context.GetX(0))));
                    context.SetX(30, _page + OffBootRet);
                    break;

                case HookKind.GetProcAddress:
                    _frames.Push(new Frame(lr, ReadString(context.GetX(1))));
                    context.SetX(30, _page + OffGpaRet);
                    break;
            }
        }

        private static string ModuleOf(ulong address)
        {
            if (address >= _page && address < _page + PageBytes)
            {
                return "probe";
            }

            foreach ((string name, ulong start, ulong end) in _modules)
            {
                if (address >= start && address < end)
                {
                    return $"{name}+0x{address - start:X}";
                }
            }

            return $"0x{address:X}";
        }

        private static ulong Wrap(string name, ulong target)
        {
            if (target == 0 || (target >= _page && target < _page + PageBytes) ||
                !name.StartsWith("nvn", StringComparison.Ordinal))
            {
                return target;
            }

            if (name == "nvnDeviceGetProcAddress")
            {
                // Patched at its entry, every caller is seen, however it got the pointer.
                if (HookEntry(target, name, HookKind.GetProcAddress))
                {
                    return target;
                }

                _process.CpuMemory.Write(_page + OffGpaSlot, target);

                return _page + OffGpaWrap;
            }

            lock (_lock)
            {
                if (!_byNameAndTarget.TryGetValue((name, target), out int index))
                {
                    if (_names.Count == ThunkCount)
                    {
                        return target;
                    }

                    index = _names.Count;
                    _names.Add(name);
                    _nameByIndex[index] = name;
                    _capturesReturn[index] = name == "nvnCommandBufferEndRecording";
                    _byNameAndTarget[(name, target)] = index;
                    _process.CpuMemory.Write(_page + TargetsOffset + (ulong)index * 8, target);

                    if (_trackRecordings && IsStructural(name))
                    {
                        _process.CpuMemory.Write(_page + TraceFlagsOffset + (ulong)index * 8, 1UL);
                    }

                    if (_entryCensus && _censusNames.Contains(name))
                    {
                        HookEntry(target, name, HookKind.CountOnly);
                    }
                }

                return _page + ThunksOffset + (ulong)index * ThunkBytes;
            }
        }

        private static void ReportLoop()
        {
            ulong[] last = new ulong[ThunkCount];
            const int PeriodMs = 5000;

            while (true)
            {
                Thread.Sleep(PeriodMs);

                string[] names;

                lock (_lock)
                {
                    names = _names.ToArray();
                }

                if (names.Length == 0)
                {
                    continue;
                }

                ulong[] now = new ulong[names.Length];

                try
                {
                    for (int i = 0; i < names.Length; i++)
                    {
                        now[i] = _process.CpuMemory.Read<ulong>(_page + CountersOffset + (ulong)i * 8);
                    }
                }
                catch (Exception)
                {
                    return; // Process gone.
                }

                double fps = _device.Statistics.GetGameFrameRate();
                double frames = Math.Max(1.0, fps * PeriodMs / 1000.0);

                // Same function names handed out twice (with and without a device) share a row.
                Dictionary<string, ulong> perName = new();
                ulong total = 0;

                for (int i = 0; i < names.Length; i++)
                {
                    ulong delta = now[i] - last[i];
                    last[i] = now[i];
                    perName[names[i]] = perName.GetValueOrDefault(names[i]) + delta;
                    total += delta;
                }

                var rows = perName.Where(kv => kv.Value != 0).OrderByDescending(kv => kv.Value).ToArray();

                var groups = perName
                    .GroupBy(kv => Group(kv.Key))
                    .Select(g => (Group: g.Key, Calls: g.Sum(kv => (double)kv.Value)))
                    .Where(g => g.Calls > 0)
                    .OrderByDescending(g => g.Calls)
                    .ToArray();

                double queries = perName.Where(kv => IsQueryLike(kv.Key)).Sum(kv => (double)kv.Value);

                StringBuilder hooks = new();

                for (int h = 0; h < _hookCount; h++)
                {
                    Hook hook = _hooks[h];

                    lock (hook)
                    {
                        string callers = string.Join(", ", hook.Callers.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} x{kv.Value}"));
                        hooks.AppendLine($"  {hook.Name} at 0x{hook.Address:X}: {hook.Calls} calls since boot, {hook.ViaThunk} through a thunk; " +
                                         $"direct callers {callers}");
                    }
                }

                StringBuilder sb = new();
                sb.AppendLine($"NVN calls over {PeriodMs / 1000} s at {fps:F1} fps: {total / frames:F0} per frame, " +
                              $"{rows.Length} of {perName.Count} handed-out functions called, query-like (Get*/Map/Wait/Finish) {queries / frames:F0} per frame");
                sb.AppendLine();
                sb.AppendLine("Entry hooks (where function pointers come from):");
                sb.Append(hooks);
                sb.AppendLine();
                sb.AppendLine("By group, per frame:");

                foreach (var g in groups)
                {
                    sb.AppendLine($"  {g.Calls / frames,10:F1}  {g.Group}");
                }

                sb.AppendLine();
                sb.AppendLine("By function, per frame:");

                foreach (var kv in rows)
                {
                    sb.AppendLine($"  {kv.Value / frames,10:F1}  {kv.Key}{(IsQueryLike(kv.Key) ? "   [query-like]" : "")}");
                }

                try
                {
                    File.WriteAllText("/tmp/ryujinx-nvn-calls.txt", sb.ToString());
                }
                catch (IOException)
                {
                }

                string top = string.Join(", ", rows.Take(6).Select(kv => $"{kv.Key.Replace("nvn", "")} {kv.Value / frames:F0}"));
                Logger.Warning?.Print(LogClass.Loader,
                    $"nvn-calls: {total / frames:F0} per frame at {fps:F1} fps, {rows.Length} functions active, query-like {queries / frames:F0}; top: {top}");
            }
        }

        /// <summary>nvnCommandBufferDrawElements -> CommandBuffer: the object an NVN call acts on.</summary>
        private static string Group(string name)
        {
            if (!name.StartsWith("nvn", StringComparison.Ordinal) || name.Length < 4)
            {
                return name;
            }

            string rest = name[3..];
            string[] objects =
            {
                "CommandBuffer", "QueueBuilder", "Queue", "TextureBuilder", "TextureView", "Texture", "BufferBuilder", "Buffer",
                "MemoryPoolBuilder", "MemoryPool", "ProgramBuilder", "Program", "SamplerBuilder", "Sampler", "SyncBuilder", "Sync",
                "WindowBuilder", "Window", "DeviceBuilder", "Device", "BlendState", "ColorState", "ChannelMaskState", "DepthStencilState",
                "MultisampleState", "PolygonState", "VertexAttribState", "VertexStreamState", "TexturePool", "SamplerPool", "EventBuilder", "Event",
            };

            return objects.FirstOrDefault(o => rest.StartsWith(o, StringComparison.Ordinal)) ?? rest;
        }

        /// <summary>Calls that, behind a ring, would need an answer before the guest can go on.</summary>
        private static bool IsQueryLike(string name)
        {
            return name.Contains("Get", StringComparison.Ordinal) && !name.Contains("Builder", StringComparison.Ordinal) ||
                   name.EndsWith("Map", StringComparison.Ordinal) ||
                   name.Contains("Wait", StringComparison.Ordinal) ||
                   name.Contains("Finish", StringComparison.Ordinal);
        }

        private static string ReadString(ulong address)
        {
            if (address == 0)
            {
                return "?";
            }

            StringBuilder sb = new();

            for (int i = 0; i < 128; i++)
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

        private static bool IsPcRelative(uint insn)
        {
            return (insn & 0x7C000000) == 0x14000000 ||   // b, bl
                   (insn & 0xFF000010) == 0x54000000 ||   // b.cond
                   (insn & 0x7E000000) == 0x34000000 ||   // cbz, cbnz
                   (insn & 0x7E000000) == 0x36000000 ||   // tbz, tbnz
                   (insn & 0x1F000000) == 0x10000000 ||   // adr, adrp
                   (insn & 0x3B000000) == 0x18000000;     // ldr literal
        }

        private static uint Svc(int imm) => 0xD4000001u | ((uint)imm << 5);

        private static uint MovzX(int d, uint imm16) => 0xD2800000u | (imm16 << 5) | (uint)d;

        private static uint Cbz(int t, ulong from, ulong to) =>
            0xB4000000u | ((uint)(((long)(to - from) >> 2) & 0x7FFFF) << 5) | (uint)t;

        private static uint Br(int n) => 0xD61F0000u | ((uint)n << 5);

        private static uint B(ulong from, ulong to) => 0x14000000u | (uint)(((long)(to - from) >> 2) & 0x3FFFFFF);

        private static uint LdrLiteral(int t, ulong from, ulong to) =>
            0x58000000u | ((uint)(((long)(to - from) >> 2) & 0x7FFFF) << 5) | (uint)t;

        private static uint Adr(int d, ulong from, ulong to)
        {
            long imm = (long)(to - from);

            return 0x10000000u | ((uint)(imm & 3) << 29) | ((uint)((imm >> 2) & 0x7FFFF) << 5) | (uint)d;
        }
    }
}
