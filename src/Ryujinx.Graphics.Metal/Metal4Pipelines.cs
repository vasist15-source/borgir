using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Render pipelines made through Metal 4's compiler, a prototype that RYUJINX_METAL_MTL4_PIPELINES=1 turns on.
    ///
    /// A program's pipeline is built once per vertex layout and the few other states the compiled code depends on, as
    /// a base whose colour attachments (format, blending, write mask) are left unspecialized, and every state of that
    /// base is then made from it by specialization. A Metal 4 pipeline names no depth or stencil format either, so one
    /// base serves every depth attachment. A standalone probe on an M4 (macOS 27.0.1) measured a specialization at
    /// 11-67 µs where building the same pipeline took 2.6-3.6 ms, and drew correctly with the specialized pipelines in
    /// the classic render encoder this backend uses, with D16, D32F and D32FS8 attachments or none, depth-only
    /// pipelines included, under Metal API validation. Breath of the Wild's 9,385 saved states (PipelineStateStore)
    /// share 3,143 bases and Mario Kart 8 Deluxe's 2,570 share 1,461: two thirds and two fifths of their full builds
    /// become specializations.
    ///
    /// The pre-warm builds its states this way. A draw whose base is built takes its pipeline by specialization on the
    /// render thread; one whose base is not is built classically, as before, and its base in the background: a game's
    /// first Metal 4 session compiles every base from scratch (Breath of the Wild: 3,885 bases, 44 s of compiles on the
    /// pre-warm workers, 11 ms each) while the OS cache still holds the classic pipelines (0.14 ms a build). The OS does
    /// not keep Metal 4 compiles itself, so without the archive every session would pay that again.
    ///
    /// Breath of the Wild 1.9.0 on an M4 (warm caches, the same 150 s scripted route, 30 fps cap): with the archive the
    /// pre-warm takes the 3,885 bases from it (1.0 s of lookups) and specializes 10,872 pipelines (98 ms), 1.75 s of
    /// builds against the classic 1.58 s; the 83 pipelines made at draw time cost 6.9 ms against 11.2 ms, with 5 built
    /// in place against 56; no draw skipped either way, Metal allocations and the presented image the same.
    ///
    /// The bases are also kept per game in Metal 4 archives beside the shader cache (metal4/, one file per write). The
    /// next session takes a base from one in about 0.14 ms (a miss costs 0.08 ms) instead of compiling it; an archive
    /// matches by compiled code, so the programs' MSL being compiled again each session finds them. Only bases no file
    /// holds are compiled, so each file holds new bases only. A compile must not end while its serializer writes
    /// (nxnative found that crashes Metal), so the compiler and serializer that capture new bases belong to a period:
    /// a write starts a new period for later compiles and writes the old one once its compiles have returned.
    /// RYUJINX_METAL_MTL4_ARCHIVE=0 keeps the bases in memory only. Old files are not compacted yet.
    ///
    /// States with a logic op are built classically: the Metal 4 pipeline descriptor has none. So is a state whose
    /// base or specialization fails.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class Metal4Pipelines
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("RYUJINX_METAL_MTL4_PIPELINES") == "1";
        private static readonly bool _archiveEnabled = Environment.GetEnvironmentVariable("RYUJINX_METAL_MTL4_ARCHIVE") != "0";

        private const string ArchiveFolder = "metal4";
        private const string ArchivePrefix = "bases-";
        private const string ArchiveExtension = ".mtl4a";
        private const string PartialSuffix = ".partial";

        // New bases are written once none has come for this long, so a scene load lands in one file.
        private static readonly long _writeQuietTicks = Stopwatch.Frequency * 10;
        private const int FailuresLogged = 8;

        // MTLPixelFormatUnspecialized, MTLColorWriteMaskUnspecialized, MTL4BlendState and
        // MTL4PipelineDataSetSerializerConfigurationCaptureBinaries.
        private const ulong PixelFormatUnspecialized = 263;
        private const ulong WriteMaskUnspecialized = 0x10;
        private const long BlendDisabled = 0;
        private const long BlendEnabled = 1;
        private const long BlendUnspecialized = 2;
        private const ulong CaptureBinaries = 1 << 1;

        // Session counters, for the stats line and the summary when the game stops.
        public static long BasesCompiled;
        public static long BaseCompileTicks;
        public static long BasesFromArchive;
        public static long ArchiveLookupTicks;
        public static long BaseFailures;
        public static long Specialized;
        public static long SpecializeTicks;
        public static long SpecializedAtDraw;
        public static long SpecializeFailures;
        public static long BasesWritten;

        private static readonly Lock _lock = new();
        private static volatile bool _available;
        private static int _active;
        private static int _writing;
        private static int _failuresLogged;
        private static IntPtr _device;
        // Specializations go through a compiler with no serializer: they cost microseconds, so archiving them would
        // only grow the files.
        private static IntPtr _specializer;
        private static Period _period;
        private static string _directory;
        private static IntPtr[] _archives = [];
        private static readonly List<IntPtr> _retiredArchives = [];
        private static IntPtr _vertexMainName;
        private static IntPtr _fragmentMainName;

        /// <summary>
        /// The compiler that builds new bases and the serializer capturing them, until they are written out and later
        /// compiles get a new period.
        /// </summary>
        private sealed class Period
        {
            public string Directory;
            public IntPtr Compiler;
            public IntPtr Serializer;
            public int InFlight;
            public int NewBases;
            public long LastNewBase;
        }

        /// <summary>A base pipeline and the descriptor its specializations are made from.</summary>
        public sealed class Base
        {
            public const int Building = 0;
            public const int Ready = 1;
            public const int Failed = 2;

            public volatile int State;
            public IntPtr Descriptor;
            public IntPtr Pipeline;

            /// <summary>Waits for the thread building it; the state it ended in.</summary>
            public int Wait()
            {
                lock (this)
                {
                    while (State == Building)
                    {
                        Monitor.Wait(this);
                    }

                    return State;
                }
            }

            public void Release()
            {
                Native.Release(Pipeline);
                Native.Release(Descriptor);
                Pipeline = IntPtr.Zero;
                Descriptor = IntPtr.Zero;
            }
        }

        /// <summary>A program's bases, by <see cref="BaseKey"/>.</summary>
        public sealed class Bases : IDisposable
        {
            private readonly HashTableSlim<PipelineUid, Base> _bases = new();
            private bool _disposed;

            /// <summary>The base for this key; <paramref name="build"/> when it is new and the caller builds it.</summary>
            public Base GetOrAdd(ref PipelineUid baseKey, out bool build)
            {
                lock (_bases)
                {
                    if (_bases.TryGetValue(ref baseKey, out Base existing))
                    {
                        build = false;

                        return existing;
                    }

                    Base created = new();
                    _bases.Add(ref baseKey, created);
                    build = true;

                    return created;
                }
            }

            public Base TryGet(ref PipelineUid baseKey)
            {
                lock (_bases)
                {
                    return _bases.TryGetValue(ref baseKey, out Base existing) ? existing : null;
                }
            }

            /// <summary>The builder's result, for the threads waiting on it; released at once if the program is gone.</summary>
            public void Finish(Base entry, IntPtr descriptor, IntPtr pipeline)
            {
                lock (_bases)
                {
                    lock (entry)
                    {
                        entry.Descriptor = descriptor;
                        entry.Pipeline = pipeline;

                        if (_disposed || pipeline == IntPtr.Zero)
                        {
                            entry.Release();
                            entry.State = Base.Failed;
                        }
                        else
                        {
                            entry.State = Base.Ready;
                        }

                        Monitor.PulseAll(entry);
                    }
                }
            }

            public void Dispose()
            {
                lock (_bases)
                {
                    _disposed = true;

                    foreach (Base entry in _bases.Values)
                    {
                        lock (entry)
                        {
                            if (entry.State == Base.Ready)
                            {
                                entry.State = Base.Failed;
                                entry.Release();
                            }
                        }
                    }
                }
            }
        }

        /// <summary>The renderer is starting: makes the specializing compiler, if the system has Metal 4.</summary>
        public static void Initialize(MTLDevice device)
        {
            if (!Enabled)
            {
                return;
            }

            lock (_lock)
            {
                if (Native.ClassRenderPipelineDescriptor == IntPtr.Zero || Native.ClassCompilerDescriptor == IntPtr.Zero)
                {
                    Logger.Warning?.Print(LogClass.Gpu, "Metal 4 pipelines: this system has no Metal 4 compiler, building pipelines classically");

                    return;
                }

                _device = device.NativePtr;
                _specializer = NewCompiler(IntPtr.Zero, out string error);

                if (_specializer == IntPtr.Zero)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"Metal 4 pipelines: no compiler ({error}), building pipelines classically");

                    return;
                }

                _vertexMainName = Native.Retain(StringHelper.NSString("vertexMain").NativePtr);
                _fragmentMainName = Native.Retain(StringHelper.NSString("fragmentMain").NativePtr);

                BasesCompiled = BaseCompileTicks = BasesFromArchive = ArchiveLookupTicks = BaseFailures = 0;
                Specialized = SpecializeTicks = SpecializedAtDraw = SpecializeFailures = BasesWritten = 0;
                _failuresLogged = 0;
                _available = true;
            }

            Logger.Info?.Print(LogClass.Gpu, $"Metal 4 pipelines: on, archive {(_archiveEnabled ? "on" : "off")}");
        }

        /// <summary>Whether a state's pipeline can be made from a base: the program compiled, and no logic op.</summary>
        public static bool CanBuild(Program program, bool logicOpEnable)
        {
            return _available &&
                   !logicOpEnable &&
                   program.Metal4Bases != null &&
                   program.VertexLibrary.NativePtr != IntPtr.Zero &&
                   (program.FragmentFunction.NativePtr == IntPtr.Zero || program.FragmentLibrary.NativePtr != IntPtr.Zero);
        }

        /// <summary>
        /// Render thread: specializes the state's base if it is built already, in tens of microseconds. False when it is
        /// not (still building, or never asked for), for the draw to queue or build the state as before.
        /// </summary>
        public static bool TrySpecializeReady(Program program, ref PipelineUid key, Array8<ColorBlendStateUid> colors, out MTLRenderPipelineState pipeline)
        {
            pipeline = default;

            if (!Enter())
            {
                return false;
            }

            try
            {
                PipelineUid baseKey = BaseKey(ref key);
                Base entry = program.Metal4Bases.TryGet(ref baseKey);

                if (entry == null || entry.State != Base.Ready || !TrySpecialize(entry, colors, out pipeline))
                {
                    return false;
                }

                Interlocked.Increment(ref SpecializedAtDraw);

                return true;
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>
        /// Render thread, for a draw building its state classically because the base is not built: builds the base in
        /// the background (or takes it from an archive), so the program's later states with it are specializations.
        /// </summary>
        public static void QueueBase(Program program, ref PipelineUid key)
        {
            PipelineUid baseKey = BaseKey(ref key);
            Base entry = program.Metal4Bases.GetOrAdd(ref baseKey, out bool build);

            if (!build)
            {
                return;
            }

            if (!program.TryBeginBackgroundBuild())
            {
                program.Metal4Bases.Finish(entry, IntPtr.Zero, IntPtr.Zero);

                return;
            }

            PipelineUid stateKey = key;

            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                IntPtr pool = ObjcOwnership.objc_autoreleasePoolPush();

                try
                {
                    if (Enter())
                    {
                        try
                        {
                            BuildBase(program, ref stateKey, entry);
                        }
                        finally
                        {
                            Exit();
                        }
                    }
                    else
                    {
                        program.Metal4Bases.Finish(entry, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                finally
                {
                    ObjcOwnership.objc_autoreleasePoolPop(pool);
                    program.EndBackgroundBuild();
                }
            }, null);
        }

        /// <summary>
        /// Any thread: the state's pipeline, specialized from its base. Builds the base first (or takes it from an
        /// archive) when the program has none for this state yet, and waits for one another thread is building. False
        /// when Metal 4 could not make it, for the caller to build the state classically.
        /// </summary>
        public static bool TryBuild(Program program, ref PipelineUid key, Array8<ColorBlendStateUid> colors, out MTLRenderPipelineState pipeline)
        {
            pipeline = default;

            if (!Enter())
            {
                return false;
            }

            try
            {
                PipelineUid baseKey = BaseKey(ref key);
                Base entry = program.Metal4Bases.GetOrAdd(ref baseKey, out bool build);

                if (build)
                {
                    BuildBase(program, ref key, entry);
                }

                return entry.Wait() == Base.Ready && TrySpecialize(entry, colors, out pipeline);
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>
        /// The state with what Metal 4 leaves to specialization taken out: the colour attachments and their count, the
        /// depth/stencil format, and the logic op (states with one never get here).
        /// </summary>
        private static PipelineUid BaseKey(ref PipelineUid key)
        {
            PipelineUid baseKey = key;

            baseKey.Id0 &= ~((0xFFUL << 24) | (0xFUL << 32) | (1UL << 38) | (0xFFFFUL << 48));
            baseKey.ColorBlendState = new();
            baseKey.AttachmentIntegerFormatMask = 0;
            baseKey.LogicOpsAllowed = false;

            return baseKey;
        }

        private static void BuildBase(Program program, ref PipelineUid key, Base entry)
        {
            IntPtr descriptor = CreateBaseDescriptor(program, ref key);
            IntPtr pipeline = IntPtr.Zero;
            IntPtr[] archives;

            lock (_lock)
            {
                OpenGameLocked();
                archives = _archives;
            }

            long start = Stopwatch.GetTimestamp();

            foreach (IntPtr archive in archives)
            {
                IntPtr missed = IntPtr.Zero;
                pipeline = Native.Send(archive, Native.SelArchiveNewRenderPipeline, descriptor, ref missed);

                if (pipeline != IntPtr.Zero)
                {
                    break;
                }
            }

            long compileStart = Stopwatch.GetTimestamp();
            Interlocked.Add(ref ArchiveLookupTicks, compileStart - start);

            if (pipeline != IntPtr.Zero)
            {
                Interlocked.Increment(ref BasesFromArchive);
            }
            else
            {
                Period period = BeginCompile();
                IntPtr error = IntPtr.Zero;

                if (period.Compiler != IntPtr.Zero)
                {
                    pipeline = Native.Send(period.Compiler, Native.SelCompilerNewRenderPipeline, descriptor, IntPtr.Zero, ref error);
                }

                EndCompile(period, pipeline != IntPtr.Zero);

                if (pipeline != IntPtr.Zero)
                {
                    Interlocked.Increment(ref BasesCompiled);
                    Interlocked.Add(ref BaseCompileTicks, Stopwatch.GetTimestamp() - compileStart);
                }
                else
                {
                    Interlocked.Increment(ref BaseFailures);
                    LogFailure($"base of {program.DebugLabel}", error);
                }
            }

            program.Metal4Bases.Finish(entry, descriptor, pipeline);
        }

        private static IntPtr CreateBaseDescriptor(Program program, ref PipelineUid key)
        {
            PipelineState state = new() { Internal = key };
            IntPtr descriptor = Native.Send(Native.ClassRenderPipelineDescriptor, Native.SelNew);

            SetFunction(descriptor, Native.SelSetVertexFunctionDescriptor, program.VertexLibrary, _vertexMainName);

            if (program.FragmentFunction.NativePtr != IntPtr.Zero)
            {
                SetFunction(descriptor, Native.SelSetFragmentFunctionDescriptor, program.FragmentLibrary, _fragmentMainName);
            }

            MTLVertexDescriptor vertexDescriptor = state.BuildVertexDescriptor(prewarm: true);
            Native.SendVoid(descriptor, Native.SelSetVertexDescriptor, vertexDescriptor.NativePtr);
            Native.Release(vertexDescriptor.NativePtr);

            Native.SendVoid(descriptor, Native.SelSetRasterSampleCount, (ulong)Math.Max(1, state.SamplesCount));
            Native.SendVoid(descriptor, Native.SelSetAlphaToCoverageState, state.AlphaToCoverageEnable ? 1L : 0L);
            Native.SendVoid(descriptor, Native.SelSetAlphaToOneState, state.AlphaToOneEnable ? 1L : 0L);
            Native.SendVoid(descriptor, Native.SelSetRasterizationEnabled, !state.RasterizerDiscardEnable);

            IntPtr attachments = Native.Send(descriptor, Native.SelColorAttachments);

            for (int i = 0; i < Constants.MaxColorAttachments; i++)
            {
                IntPtr attachment = Native.Send(attachments, Native.SelObjectAtIndexedSubscript, (ulong)i);

                Native.SendVoid(attachment, Native.SelSetPixelFormat, PixelFormatUnspecialized);
                Native.SendVoid(attachment, Native.SelSetBlendingState, BlendUnspecialized);
                Native.SendVoid(attachment, Native.SelSetWriteMask, WriteMaskUnspecialized);
            }

            return descriptor;
        }

        private static void SetFunction(IntPtr descriptor, IntPtr setter, MTLLibrary library, IntPtr name)
        {
            IntPtr function = Native.Send(Native.ClassLibraryFunctionDescriptor, Native.SelNew);

            Native.SendVoid(function, Native.SelSetLibrary, library.NativePtr);
            Native.SendVoid(function, Native.SelSetName, name);
            Native.SendVoid(descriptor, setter, function);
            Native.Release(function);
        }

        private static bool TrySpecialize(Base entry, Array8<ColorBlendStateUid> colors, out MTLRenderPipelineState pipeline)
        {
            long start = Stopwatch.GetTimestamp();
            IntPtr descriptor = Native.Send(entry.Descriptor, Native.SelCopy);
            IntPtr attachments = Native.Send(descriptor, Native.SelColorAttachments);

            // The same colour state the classic descriptor gets (PipelineState.BuildColorAttachment).
            for (int i = 0; i < Constants.MaxColorAttachments; i++)
            {
                IntPtr attachment = Native.Send(attachments, Native.SelObjectAtIndexedSubscript, (ulong)i);
                ColorBlendStateUid state = colors[i];

                if (state.PixelFormat == MTLPixelFormat.Invalid)
                {
                    Native.SendVoid(attachment, Native.SelSetPixelFormat, (ulong)MTLPixelFormat.Invalid);
                    Native.SendVoid(attachment, Native.SelSetBlendingState, BlendDisabled);
                    Native.SendVoid(attachment, Native.SelSetWriteMask, (ulong)MTLColorWriteMask.All);

                    continue;
                }

                Native.SendVoid(attachment, Native.SelSetPixelFormat, (ulong)state.PixelFormat);
                Native.SendVoid(attachment, Native.SelSetBlendingState, state.Enable ? BlendEnabled : BlendDisabled);
                Native.SendVoid(attachment, Native.SelSetAlphaBlendOperation, (ulong)state.AlphaBlendOperation);
                Native.SendVoid(attachment, Native.SelSetRgbBlendOperation, (ulong)state.RgbBlendOperation);
                Native.SendVoid(attachment, Native.SelSetSourceAlphaBlendFactor, (ulong)state.SourceAlphaBlendFactor);
                Native.SendVoid(attachment, Native.SelSetDestinationAlphaBlendFactor, (ulong)state.DestinationAlphaBlendFactor);
                Native.SendVoid(attachment, Native.SelSetSourceRgbBlendFactor, (ulong)state.SourceRGBBlendFactor);
                Native.SendVoid(attachment, Native.SelSetDestinationRgbBlendFactor, (ulong)state.DestinationRGBBlendFactor);
                Native.SendVoid(attachment, Native.SelSetWriteMask, (ulong)state.WriteMask);
            }

            IntPtr error = IntPtr.Zero;
            IntPtr built = Native.Send(_specializer, Native.SelSpecialize, descriptor, entry.Pipeline, ref error);

            Native.Release(descriptor);
            Interlocked.Add(ref SpecializeTicks, Stopwatch.GetTimestamp() - start);

            if (built == IntPtr.Zero)
            {
                Interlocked.Increment(ref SpecializeFailures);
                LogFailure("specialization", error);
                pipeline = default;

                return false;
            }

            Interlocked.Increment(ref Specialized);
            pipeline = new MTLRenderPipelineState(built);

            return true;
        }

        private static Period BeginCompile()
        {
            lock (_lock)
            {
                OpenGameLocked();

                _period ??= NewPeriodLocked();
                _period.InFlight++;

                return _period;
            }
        }

        private static void EndCompile(Period period, bool built)
        {
            lock (_lock)
            {
                period.InFlight--;

                if (built)
                {
                    period.NewBases++;
                    period.LastNewBase = Stopwatch.GetTimestamp();
                }
            }
        }

        private static Period NewPeriodLocked()
        {
            Period period = new() { Directory = _directory };

            if (_archiveEnabled && _directory != null)
            {
                IntPtr serializerDescriptor = Native.Send(Native.ClassSerializerDescriptor, Native.SelNew);
                Native.SendVoid(serializerDescriptor, Native.SelSetConfiguration, CaptureBinaries);
                period.Serializer = Native.Send(_device, Native.SelNewSerializer, serializerDescriptor);
                Native.Release(serializerDescriptor);
            }

            period.Compiler = NewCompiler(period.Serializer, out string error);

            if (period.Compiler == IntPtr.Zero)
            {
                LogFailure($"compiler ({error})", IntPtr.Zero);
            }

            return period;
        }

        private static IntPtr NewCompiler(IntPtr serializer, out string error)
        {
            IntPtr descriptor = Native.Send(Native.ClassCompilerDescriptor, Native.SelNew);

            if (serializer != IntPtr.Zero)
            {
                Native.SendVoid(descriptor, Native.SelSetPipelineDataSetSerializer, serializer);
            }

            IntPtr nsError = IntPtr.Zero;
            IntPtr compiler = Native.Send(_device, Native.SelNewCompiler, descriptor, ref nsError);
            Native.Release(descriptor);
            error = Describe(nsError);

            return compiler;
        }

        /// <summary>
        /// Opens the running game's archives on its first base, and starts over when another game's shader cache is in
        /// use. Old archives stay open until <see cref="Close"/>, since lookups read the list without the lock.
        /// </summary>
        private static void OpenGameLocked()
        {
            string directory = ShaderCacheLocation.Directory;

            if (directory == _directory)
            {
                return;
            }

            if (_period != null)
            {
                WriteLater(_period);
                _period = null;
            }

            _retiredArchives.AddRange(_archives);
            _archives = [];
            _directory = directory;

            if (directory == null || !_archiveEnabled)
            {
                return;
            }

            string folder = Path.Combine(directory, ArchiveFolder);

            if (!Directory.Exists(folder))
            {
                return;
            }

            // Newest first: a base compiled again after a system update is in the newest file.
            string[] files = Directory.GetFiles(folder, ArchivePrefix + "*" + ArchiveExtension)
                .Where(file => !file.EndsWith(PartialSuffix + ArchiveExtension, StringComparison.Ordinal))
                .OrderByDescending(file => file, StringComparer.Ordinal)
                .ToArray();

            List<IntPtr> archives = [];
            long start = Stopwatch.GetTimestamp();
            IntPtr pool = ObjcOwnership.objc_autoreleasePoolPush();

            try
            {
                foreach (string file in files)
                {
                    IntPtr error = IntPtr.Zero;
                    IntPtr archive = Native.Send(_device, Native.SelNewArchive, FileUrl(file), ref error);

                    if (archive != IntPtr.Zero)
                    {
                        archives.Add(archive);
                    }
                    else
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"Metal 4 pipelines: could not open {Path.GetFileName(file)}: {Describe(error)}");
                    }
                }
            }
            finally
            {
                ObjcOwnership.objc_autoreleasePoolPop(pool);
            }

            _archives = [.. archives];

            Logger.Info?.Print(LogClass.Gpu,
                $"Metal 4 pipelines: {archives.Count} of {files.Length} archive files opened in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1} ms");
        }

        /// <summary>Render thread, once per presented frame: writes the new bases out once none has come for a while.</summary>
        public static void NotePresent()
        {
            if (!_available || !_archiveEnabled)
            {
                return;
            }

            Period period = Volatile.Read(ref _period);

            if (period == null ||
                period.Serializer == IntPtr.Zero ||
                Volatile.Read(ref period.NewBases) == 0 ||
                Stopwatch.GetTimestamp() - Volatile.Read(ref period.LastNewBase) < _writeQuietTicks ||
                Volatile.Read(ref _writing) != 0)
            {
                return;
            }

            lock (_lock)
            {
                if (_period != period)
                {
                    return;
                }

                // The next compile starts a new period.
                _period = null;
            }

            WriteLater(period);
        }

        private static void WriteLater(Period period)
        {
            Interlocked.Increment(ref _writing);

            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try
                {
                    Write(period);
                }
                finally
                {
                    Interlocked.Decrement(ref _writing);
                }
            }, null);
        }

        /// <summary>Writes a period's new bases to a new archive file, once its compiles have returned, and frees it.</summary>
        private static void Write(Period period)
        {
            long waitStart = Stopwatch.GetTimestamp();

            while (Volatile.Read(ref period.InFlight) > 0)
            {
                if (Stopwatch.GetElapsedTime(waitStart).TotalSeconds > 30)
                {
                    // Leaked rather than risk writing under a compile.
                    Logger.Warning?.Print(LogClass.Gpu, $"Metal 4 pipelines: a base compile has not returned in 30 s; {period.NewBases} new bases not written");

                    return;
                }

                Thread.Sleep(5);
            }

            IntPtr pool = ObjcOwnership.objc_autoreleasePoolPush();

            try
            {
                if (period.Serializer != IntPtr.Zero && period.NewBases > 0 && period.Directory != null)
                {
                    string folder = Path.Combine(period.Directory, ArchiveFolder);
                    string name = ArchivePrefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
                    string partial = Path.Combine(folder, name + PartialSuffix + ArchiveExtension);
                    string final = Path.Combine(folder, name + ArchiveExtension);
                    long start = Stopwatch.GetTimestamp();

                    Directory.CreateDirectory(folder);

                    IntPtr error = IntPtr.Zero;

                    if (Native.SendBool(period.Serializer, Native.SelSerialize, FileUrl(partial), ref error))
                    {
                        File.Move(partial, final, overwrite: true);
                        Interlocked.Add(ref BasesWritten, period.NewBases);

                        Logger.Info?.Print(LogClass.Gpu,
                            $"Metal 4 pipelines: {period.NewBases} new bases written to {name}{ArchiveExtension} " +
                            $"({new FileInfo(final).Length / 1024} KiB, {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms)");
                    }
                    else
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"Metal 4 pipelines: could not write {period.NewBases} new bases: {Describe(error)}");
                        File.Delete(partial);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Metal 4 pipelines: could not write {period.NewBases} new bases: {exception.Message}");
            }
            finally
            {
                Native.Release(period.Compiler);
                Native.Release(period.Serializer);
                ObjcOwnership.objc_autoreleasePoolPop(pool);
            }
        }

        /// <summary>
        /// The game is stopping, after the pre-warm workers: lets builds still running finish, writes the new bases and
        /// frees the compilers and archives.
        /// </summary>
        public static void Close()
        {
            if (!_available)
            {
                return;
            }

            _available = false;

            long waitStart = Stopwatch.GetTimestamp();

            while ((Volatile.Read(ref _active) > 0 || Volatile.Read(ref _writing) > 0) && Stopwatch.GetElapsedTime(waitStart).TotalSeconds < 30)
            {
                Thread.Sleep(5);
            }

            Period period;

            lock (_lock)
            {
                period = _period;
                _period = null;
            }

            if (period != null)
            {
                Write(period);
            }

            double tickMs = 1000.0 / Stopwatch.Frequency;

            Logger.Info?.Print(LogClass.Gpu,
                $"Metal 4 pipelines session: {BasesCompiled} bases compiled ({BaseCompileTicks * tickMs:F0} ms), {BasesFromArchive} taken from " +
                $"{_archives.Length} archive files ({ArchiveLookupTicks * tickMs:F0} ms of lookups), {BaseFailures} failed; " +
                $"{Specialized} pipelines specialized ({SpecializeTicks * tickMs:F0} ms, {SpecializedAtDraw} at their draw), {SpecializeFailures} failed; " +
                $"{BasesWritten} new bases written.");

            if (Volatile.Read(ref _active) > 0)
            {
                // A build is still running on them; leaked rather than freed under it.
                return;
            }

            lock (_lock)
            {
                foreach (IntPtr archive in _archives.Concat(_retiredArchives))
                {
                    Native.Release(archive);
                }

                _archives = [];
                _retiredArchives.Clear();
                _directory = null;

                Native.Release(_specializer);
                Native.Release(_vertexMainName);
                Native.Release(_fragmentMainName);
                _specializer = _vertexMainName = _fragmentMainName = IntPtr.Zero;
            }
        }

        public static string FormatStats()
        {
            if (!_available)
            {
                return string.Empty;
            }

            double tickMs = 1000.0 / Stopwatch.Frequency;

            return $" metal 4: {BasesCompiled} bases compiled ({BaseCompileTicks * tickMs:F0} ms), {BasesFromArchive} from archives " +
                   $"({ArchiveLookupTicks * tickMs:F0} ms), {BaseFailures} failed; {Specialized} specialized ({SpecializeTicks * tickMs:F1} ms, " +
                   $"{SpecializedAtDraw} at their draw), {SpecializeFailures} failed.";
        }

        // Counted before the check, so Close either sees the call or the call sees Close.
        private static bool Enter()
        {
            Interlocked.Increment(ref _active);

            if (_available)
            {
                return true;
            }

            Interlocked.Decrement(ref _active);

            return false;
        }

        private static void Exit()
        {
            Interlocked.Decrement(ref _active);
        }

        private static IntPtr FileUrl(string path)
        {
            return Native.Send(Native.ClassUrl, Native.SelFileUrlWithPath, StringHelper.NSString(path).NativePtr);
        }

        private static string Describe(IntPtr error)
        {
            return error != IntPtr.Zero ? StringHelper.String(new NSError(error).LocalizedDescription) : "no error given";
        }

        private static void LogFailure(string what, IntPtr error)
        {
            if (Interlocked.Increment(ref _failuresLogged) <= FailuresLogged)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Metal 4 pipelines: {what} failed, built classically: {Describe(error)}");
            }
        }

        /// <summary>The Metal 4 classes and selectors, which SharpMetal does not bind.</summary>
        private static class Native
        {
            private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";

            [DllImport(ObjCLibrary)]
            private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPStr)] string name);

            [DllImport(ObjCLibrary)]
            private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);

            [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
            public static extern IntPtr Send(IntPtr receiver, IntPtr selector);

            [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
            public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

            [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
            public static extern IntPtr Send(IntPtr receiver, IntPtr selector, ulong argument);

            [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
            public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument, ref IntPtr error);

            [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
            public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second, ref IntPtr error);

            [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
            [return: MarshalAs(UnmanagedType.I1)]
            public static extern bool SendBool(IntPtr receiver, IntPtr selector, IntPtr argument, ref IntPtr error);

            [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
            public static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr argument);

            [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
            public static extern void SendVoid(IntPtr receiver, IntPtr selector, ulong argument);

            [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
            public static extern void SendVoid(IntPtr receiver, IntPtr selector, long argument);

            [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
            public static extern void SendVoid(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool argument);

            public static readonly IntPtr ClassCompilerDescriptor = objc_getClass("MTL4CompilerDescriptor");
            public static readonly IntPtr ClassSerializerDescriptor = objc_getClass("MTL4PipelineDataSetSerializerDescriptor");
            public static readonly IntPtr ClassRenderPipelineDescriptor = objc_getClass("MTL4RenderPipelineDescriptor");
            public static readonly IntPtr ClassLibraryFunctionDescriptor = objc_getClass("MTL4LibraryFunctionDescriptor");
            public static readonly IntPtr ClassUrl = objc_getClass("NSURL");

            public static readonly IntPtr SelNew = sel_registerName("new");
            public static readonly IntPtr SelCopy = sel_registerName("copy");
            private static readonly IntPtr _selRetain = sel_registerName("retain");
            private static readonly IntPtr _selRelease = sel_registerName("release");
            public static readonly IntPtr SelFileUrlWithPath = sel_registerName("fileURLWithPath:");

            public static readonly IntPtr SelNewCompiler = sel_registerName("newCompilerWithDescriptor:error:");
            public static readonly IntPtr SelNewSerializer = sel_registerName("newPipelineDataSetSerializerWithDescriptor:");
            public static readonly IntPtr SelNewArchive = sel_registerName("newArchiveWithURL:error:");
            public static readonly IntPtr SelSetPipelineDataSetSerializer = sel_registerName("setPipelineDataSetSerializer:");
            public static readonly IntPtr SelSetConfiguration = sel_registerName("setConfiguration:");
            public static readonly IntPtr SelSerialize = sel_registerName("serializeAsArchiveAndFlushToURL:error:");

            public static readonly IntPtr SelCompilerNewRenderPipeline = sel_registerName("newRenderPipelineStateWithDescriptor:compilerTaskOptions:error:");
            public static readonly IntPtr SelSpecialize = sel_registerName("newRenderPipelineStateBySpecializationWithDescriptor:pipeline:error:");
            public static readonly IntPtr SelArchiveNewRenderPipeline = sel_registerName("newRenderPipelineStateWithDescriptor:error:");

            public static readonly IntPtr SelSetLibrary = sel_registerName("setLibrary:");
            public static readonly IntPtr SelSetName = sel_registerName("setName:");
            public static readonly IntPtr SelSetVertexFunctionDescriptor = sel_registerName("setVertexFunctionDescriptor:");
            public static readonly IntPtr SelSetFragmentFunctionDescriptor = sel_registerName("setFragmentFunctionDescriptor:");
            public static readonly IntPtr SelSetVertexDescriptor = sel_registerName("setVertexDescriptor:");
            public static readonly IntPtr SelSetRasterSampleCount = sel_registerName("setRasterSampleCount:");
            public static readonly IntPtr SelSetAlphaToCoverageState = sel_registerName("setAlphaToCoverageState:");
            public static readonly IntPtr SelSetAlphaToOneState = sel_registerName("setAlphaToOneState:");
            public static readonly IntPtr SelSetRasterizationEnabled = sel_registerName("setRasterizationEnabled:");
            public static readonly IntPtr SelColorAttachments = sel_registerName("colorAttachments");
            public static readonly IntPtr SelObjectAtIndexedSubscript = sel_registerName("objectAtIndexedSubscript:");

            public static readonly IntPtr SelSetPixelFormat = sel_registerName("setPixelFormat:");
            public static readonly IntPtr SelSetBlendingState = sel_registerName("setBlendingState:");
            public static readonly IntPtr SelSetAlphaBlendOperation = sel_registerName("setAlphaBlendOperation:");
            public static readonly IntPtr SelSetRgbBlendOperation = sel_registerName("setRgbBlendOperation:");
            public static readonly IntPtr SelSetSourceAlphaBlendFactor = sel_registerName("setSourceAlphaBlendFactor:");
            public static readonly IntPtr SelSetDestinationAlphaBlendFactor = sel_registerName("setDestinationAlphaBlendFactor:");
            public static readonly IntPtr SelSetSourceRgbBlendFactor = sel_registerName("setSourceRGBBlendFactor:");
            public static readonly IntPtr SelSetDestinationRgbBlendFactor = sel_registerName("setDestinationRGBBlendFactor:");
            public static readonly IntPtr SelSetWriteMask = sel_registerName("setWriteMask:");

            public static IntPtr Retain(IntPtr target)
            {
                return target != IntPtr.Zero ? Send(target, _selRetain) : IntPtr.Zero;
            }

            public static void Release(IntPtr target)
            {
                if (target != IntPtr.Zero)
                {
                    Send(target, _selRelease);
                }
            }
        }
    }
}
