using Ryujinx.Common.Logging;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Builds the pipelines of the programs loaded from the shader cache before the game
    /// draws with them.
    ///
    /// A pipeline state object is otherwise created at its first draw, every session. The
    /// OS keeps the compiled GPU code between launches (com.apple.metal in the app's cache
    /// directory), so on a second launch each creation is a cache hit, but still 0.1-1.5 ms
    /// on the render thread, and a scene load asks for hundreds at once: Mario Kart 8 on a
    /// repeat launch made 276 in one 296 ms frame, and draws were skipped while the rest
    /// built. The shader cache records, with each program, the GAL pipeline state it was
    /// first used with (ShaderInfo.State, which the Vulkan backend builds from in the same
    /// way), so the pipeline a draw will ask for is known as soon as the program loads.
    ///
    /// MTLBinaryArchive was measured as the alternative and rejected: its hits cost the same
    /// as an OS cache hit, saving one costs ~1.7 ms per pipeline (12 s to re-save a loaded
    /// 1200-pipeline archive), and serializing while other threads compile pipelines
    /// corrupted the archive on macOS 27 (failed saves, and entries silently dropped from
    /// the archive itself).
    ///
    /// Programs queue themselves once their MSL compiles (<see cref="Program"/>); a few
    /// low-priority workers build them once the cache load stops queueing more. A draw that
    /// needs a pipeline still building waits for it like any build in flight, and one that
    /// gets to a queued program first builds it itself and the queued build is dropped.
    /// Builds here are not counted as draw-time "pso compiles", which is what this exists to
    /// bring down.
    ///
    /// The same workers compile the programs' MSL first (<see cref="DeferCompile"/>). The
    /// shader cache loader used to wait for every program to compile, 8 at a time: with the
    /// OS compiler cache warm that is 0.3-1.6 s for a few hundred programs, but cold (after
    /// an OS update or a cache purge) Mario Kart 8's 452 took 4.1 s, ~9 ms each, so a
    /// 5,000-program cache would hold the boot for most of a minute. Now a cached program
    /// counts as loaded at once and is compiled here, or on the render thread if the game
    /// binds it before a worker gets to it.
    ///
    /// RYUJINX_METAL_PREWARM=0 turns the pipeline builds off, RYUJINX_METAL_DEFER_COMPILE=0
    /// compiles during the load again.
    /// </summary>
    [SupportedOSPlatform("macos")]
    sealed class PipelinePrewarmer : IDisposable
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("RYUJINX_METAL_PREWARM") != "0";
        public static readonly bool DeferCompile = Environment.GetEnvironmentVariable("RYUJINX_METAL_DEFER_COMPILE") != "0";

        private const int MismatchesLogged = 8;

        // Process-wide, like PipelineState's counters, for the stats line.
        public static long BackgroundCompiled;
        public static long BackgroundCompileTicks;
        public static long FirstUseCompiled;
        public static long FirstUseWaits;
        public static long FirstUseTicks;
        public static long CompileFailed;
        public static long RenderBuilt;
        public static long ComputeBuilt;
        public static long BuildTicks;
        public static long Failed;
        public static long Overtaken;
        public static long Matched;
        public static long Mismatched;
        public static long ComputeUsed;

        private static int _mismatchesLogged;

        // Builds wait until no program has been queued for this long. Programs queue as the
        // shader cache loader compiles them, and building pipelines alongside those compiles
        // stretched Mario Wonder's 371-program load from 0.25 s to 4.3 s: both go through the
        // compiler service. Once the load is over they take about 0.1 s.
        private static readonly long _quietTicks = Stopwatch.Frequency / 4;

        // The batch is logged once the queue has stayed empty for this long.
        private const int IdleLogMs = 1000;

        private readonly MTLDevice _device;
        private readonly BlockingCollection<Program> _queue = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Thread[] _workers;

        private long _lastEnqueue;

        // The current batch, logged as one line when the queue runs dry.
        private int _busy;
        private long _batchFirstBuild;
        private long _batchLastBuild;
        private long _batchRender;
        private long _batchCompute;
        private long _batchTicks;
        private long _batchCompiled;

        // Session totals are reported relative to these.
        private readonly long _startBackgroundCompiled = BackgroundCompiled;
        private readonly long _startBackgroundCompileTicks = BackgroundCompileTicks;
        private readonly long _startFirstUseCompiled = FirstUseCompiled;
        private readonly long _startFirstUseWaits = FirstUseWaits;
        private readonly long _startFirstUseTicks = FirstUseTicks;
        private readonly long _startCompileFailed = CompileFailed;
        private readonly long _startRenderCompiles = PipelineState.PsoRenderCreated;
        private readonly long _startRenderCompileTicks = PipelineState.PsoRenderTicks;
        private readonly long _startComputeCompiles = PipelineState.PsoComputeCreated;
        private readonly long _startComputeCompileTicks = PipelineState.PsoComputeTicks;
        private readonly long _startRenderBuilt = RenderBuilt;
        private readonly long _startComputeBuilt = ComputeBuilt;
        private readonly long _startBuildTicks = BuildTicks;
        private readonly long _startFailed = Failed;
        private readonly long _startOvertaken = Overtaken;
        private readonly long _startMatched = Matched;
        private readonly long _startMismatched = Mismatched;
        private readonly long _startComputeUsed = ComputeUsed;

        public PipelinePrewarmer(MTLDevice device)
        {
            _device = device;

            // A third of the cores: cache hits are cheap and a cold cache hands the work to
            // the compiler service anyway, while the game is booting on the rest.
            _workers = new Thread[Math.Clamp(Environment.ProcessorCount / 3, 1, 4)];

            for (int i = 0; i < _workers.Length; i++)
            {
                _workers[i] = new Thread(WorkerMain)
                {
                    Name = $"GPU.PipelinePrewarm.{i}",
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                };

                _workers[i].Start();
            }
        }

        /// <summary>Any thread: builds this program's pipeline when a worker is free.</summary>
        public void Enqueue(Program program)
        {
            Volatile.Write(ref _lastEnqueue, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _busy);

            try
            {
                _queue.Add(program);
            }
            catch (InvalidOperationException)
            {
                // Shutting down.
                Interlocked.Decrement(ref _busy);
            }
        }

        private void WorkerMain()
        {
            CancellationToken shutdown = _shutdown.Token;

            try
            {
                while (true)
                {
                    if (!_queue.TryTake(out Program program, IdleLogMs, shutdown))
                    {
                        if (Volatile.Read(ref _busy) == 0)
                        {
                            LogBatch();
                        }

                        continue;
                    }

                    // Let the cache loader finish compiling first; a draw that needs one of
                    // these pipelines meanwhile builds it itself.
                    while (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastEnqueue) < _quietTicks)
                    {
                        if (shutdown.WaitHandle.WaitOne(25))
                        {
                            return;
                        }
                    }

                    IntPtr pool = ObjcOwnership.objc_autoreleasePoolPush();

                    Interlocked.CompareExchange(ref _batchFirstBuild, Stopwatch.GetTimestamp(), 0);

                    try
                    {
                        if (program.EnsureCompiled(background: true))
                        {
                            Interlocked.Increment(ref _batchCompiled);
                        }

                        Build(program);
                    }
                    catch (Exception exception)
                    {
                        Logger.Error?.PrintMsg(LogClass.Gpu, $"Pipeline pre-warm of {program.DebugLabel} failed: {exception.Message}");
                    }
                    finally
                    {
                        ObjcOwnership.objc_autoreleasePoolPop(pool);
                        Volatile.Write(ref _batchLastBuild, Stopwatch.GetTimestamp());
                        Interlocked.Decrement(ref _busy);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Disposed; whatever is still queued is built by its first draw instead.
            }
        }

        private void Build(Program program)
        {
            if (!program.TryBeginPrewarm())
            {
                return;
            }

            NSError error = new(IntPtr.Zero);
            long start = Stopwatch.GetTimestamp();

            if (program.IsCompute)
            {
                MTLComputePipelineState pipeline = default;

                try
                {
                    using MTLComputePipelineDescriptor descriptor = PipelineState.CreateComputeDescriptor(program);

                    pipeline = _device.NewComputePipelineState(descriptor, MTLPipelineOption.None, 0, ref error);
                }
                finally
                {
                    if (error != IntPtr.Zero || pipeline.NativePtr == IntPtr.Zero)
                    {
                        Interlocked.Increment(ref Failed);
                    }
                    else
                    {
                        Interlocked.Increment(ref ComputeBuilt);
                        Interlocked.Increment(ref _batchCompute);
                    }

                    program.EndComputePrewarm(error != IntPtr.Zero ? default : pipeline);
                }
            }
            else
            {
                MTLRenderPipelineState pipeline = default;

                try
                {
                    PipelineState state = new() { Internal = program.PrewarmKey };

                    using MTLRenderPipelineDescriptor descriptor = state.CreatePrewarmDescriptor(program);

                    pipeline = _device.NewRenderPipelineState(descriptor, ref error);
                }
                finally
                {
                    if (error != IntPtr.Zero || pipeline.NativePtr == IntPtr.Zero)
                    {
                        // The draw builds it again and reports the error there.
                        Interlocked.Increment(ref Failed);
                        pipeline = default;
                    }
                    else
                    {
                        Interlocked.Increment(ref RenderBuilt);
                        Interlocked.Increment(ref _batchRender);
                    }

                    program.EndPrewarm(pipeline);
                }
            }

            long ticks = Stopwatch.GetTimestamp() - start;

            Interlocked.Add(ref BuildTicks, ticks);
            Interlocked.Add(ref _batchTicks, ticks);
        }

        /// <summary>
        /// Any thread, from <see cref="Program.EnsureCompiled"/>: a deferred program compiled
        /// (or, for the render thread, waited for) in <paramref name="ticks"/>.
        /// </summary>
        public static void NoteCompile(bool background, bool compiledHere, bool failed, long ticks)
        {
            if (failed)
            {
                Interlocked.Increment(ref CompileFailed);
            }

            if (background)
            {
                if (compiledHere)
                {
                    Interlocked.Increment(ref BackgroundCompiled);
                    Interlocked.Add(ref BackgroundCompileTicks, ticks);
                }

                return;
            }

            // The render thread stalled either way: compiling it, or waiting for the worker
            // that was.
            if (compiledHere)
            {
                Interlocked.Increment(ref FirstUseCompiled);
            }
            else
            {
                Interlocked.Increment(ref FirstUseWaits);
            }

            Interlocked.Add(ref FirstUseTicks, ticks);
        }

        private void LogBatch()
        {
            long render = Interlocked.Exchange(ref _batchRender, 0);
            long compute = Interlocked.Exchange(ref _batchCompute, 0);
            long ticks = Interlocked.Exchange(ref _batchTicks, 0);
            long compiled = Interlocked.Exchange(ref _batchCompiled, 0);
            long first = Interlocked.Exchange(ref _batchFirstBuild, 0);

            if (render == 0 && compute == 0 && compiled == 0)
            {
                return;
            }

            double wallMs = (Volatile.Read(ref _batchLastBuild) - first) * 1000.0 / Stopwatch.Frequency;
            double tickMs = 1000.0 / Stopwatch.Frequency;

            Logger.Info?.PrintMsg(LogClass.Gpu,
                $"Pipeline pre-warm: compiled {compiled} cached programs and built {render} render + {compute} compute pipelines in {wallMs:F0} ms " +
                $"on {_workers.Length} workers ({ticks * tickMs:F0} ms of pipeline builds); " +
                $"so far {FirstUseCompiled} programs compiled at first use ({FirstUseWaits} waited for a worker, {FirstUseTicks * tickMs:F0} ms on the render thread), " +
                $"{Overtaken} pipelines reached by their draw first.");
        }

        /// <summary>Render thread: the first draw of a pre-warmed program, and the state it asked for.</summary>
        public static void NoteFirstDraw(Program program, ref PipelineUid prewarmed, ref PipelineUid drawn)
        {
            if (drawn.Equals(ref prewarmed))
            {
                Matched++;

                return;
            }

            Mismatched++;

            if (_mismatchesLogged < MismatchesLogged)
            {
                _mismatchesLogged++;

                Logger.Info?.PrintMsg(LogClass.Gpu,
                    $"Pipeline pre-warm: {program.DebugLabel} first drew with another state than the one built for it: {Describe(prewarmed, drawn)}");
            }
        }

        /// <summary>The fields that differ between two pipeline keys, as built -> drawn.</summary>
        private static string Describe(PipelineUid built, PipelineUid drawn)
        {
            PipelineState a = new() { Internal = built };
            PipelineState b = new() { Internal = drawn };
            List<string> diffs = [];

            void Field<T>(string name, T x, T y)
            {
                if (!EqualityComparer<T>.Default.Equals(x, y))
                {
                    diffs.Add($"{name} {x}->{y}");
                }
            }

            Field("attribs", a.VertexAttributeDescriptionsCount, b.VertexAttributeDescriptionsCount);
            Field("bindings", a.VertexBindingDescriptionsCount, b.VertexBindingDescriptionsCount);
            Field("targets", a.ColorBlendAttachmentStateCount, b.ColorBlendAttachmentStateCount);
            Field("logicOp", a.LogicOpEnable ? a.LogicOp.ToString() : "off", b.LogicOpEnable ? b.LogicOp.ToString() : "off");
            Field("discard", a.RasterizerDiscardEnable, b.RasterizerDiscardEnable);
            Field("alphaToCoverage", a.AlphaToCoverageEnable, b.AlphaToCoverageEnable);
            Field("alphaToOne", a.AlphaToOneEnable, b.AlphaToOneEnable);
            Field("depth", a.DepthStencilFormat, b.DepthStencilFormat);
            Field("samples", a.SamplesCount, b.SamplesCount);
            Field("patch", a.PatchControlPoints, b.PatchControlPoints);
            Field("restart", a.PrimitiveRestartEnable, b.PrimitiveRestartEnable);

            for (int i = 0; i < Constants.MaxColorAttachments; i++)
            {
                ColorBlendStateUid x = built.ColorBlendState[i];
                ColorBlendStateUid y = drawn.ColorBlendState[i];

                Field($"rt{i}", x.PixelFormat, y.PixelFormat);
                Field($"rt{i}.mask", x.WriteMask, y.WriteMask);

                // Everything but the format and the write mask, listed above.
                if ((x.Id0 & ~0x0F0000000000FFFFUL) != (y.Id0 & ~0x0F0000000000FFFFUL))
                {
                    diffs.Add($"rt{i}.blend {x.Id0:X16}->{y.Id0:X16}");
                }
            }

            uint attribs = Math.Min(a.VertexAttributeDescriptionsCount, b.VertexAttributeDescriptionsCount);

            for (int i = 0; i < attribs; i++)
            {
                VertexInputAttributeUid x = built.VertexAttributes[i];
                VertexInputAttributeUid y = drawn.VertexAttributes[i];

                if (x.Id0 != y.Id0)
                {
                    diffs.Add($"va{i} {x.Format}+{x.Offset}@{x.BufferIndex}->{y.Format}+{y.Offset}@{y.BufferIndex}");
                }
            }

            uint bindings = Math.Min(a.VertexBindingDescriptionsCount, b.VertexBindingDescriptionsCount);

            for (int i = 0; i < bindings; i++)
            {
                VertexInputLayoutUid x = built.VertexBindings[i];
                VertexInputLayoutUid y = drawn.VertexBindings[i];

                if (x.Id0 != y.Id0)
                {
                    diffs.Add($"vb{i} {x.Stride}/{x.StepFunction}/{x.StepRate}->{y.Stride}/{y.StepFunction}/{y.StepRate}");
                }
            }

            return diffs.Count == 0 ? "no field differs (hash collision?)" : string.Join(", ", diffs);
        }

        /// <summary>For the stats line: pre-warm totals so far.</summary>
        public static string FormatStats()
        {
            double tickMs = 1000.0 / Stopwatch.Frequency;

            return $" deferred compiles: {BackgroundCompiled} in background ({BackgroundCompileTicks * tickMs:F0} ms), " +
                   $"{FirstUseCompiled + FirstUseWaits} at first use ({FirstUseTicks * tickMs:F0} ms on the render thread), {CompileFailed} failed." +
                   $" pre-warm: {RenderBuilt} render + {ComputeBuilt} compute built ahead ({BuildTicks * tickMs:F0} ms), " +
                   $"{Failed} failed, {Overtaken} reached by their draw first; first draws {Matched} matched, {Mismatched} other state, " +
                   $"{ComputeUsed} compute used.";
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            _queue.CompleteAdding();

            foreach (Thread worker in _workers)
            {
                worker.Join(TimeSpan.FromSeconds(5));
            }

            double tickMs = 1000.0 / Stopwatch.Frequency;

            Logger.Info?.PrintMsg(LogClass.Gpu,
                $"Pipeline pre-warm session: {BackgroundCompiled - _startBackgroundCompiled} cached programs compiled in the background " +
                $"({(BackgroundCompileTicks - _startBackgroundCompileTicks) * tickMs:F0} ms), " +
                $"{FirstUseCompiled - _startFirstUseCompiled} at first use and {FirstUseWaits - _startFirstUseWaits} waited for " +
                $"({(FirstUseTicks - _startFirstUseTicks) * tickMs:F0} ms on the render thread), {CompileFailed - _startCompileFailed} failed; " +
                $"{RenderBuilt - _startRenderBuilt} render + {ComputeBuilt - _startComputeBuilt} compute pipelines built ahead of use " +
                $"({(BuildTicks - _startBuildTicks) * tickMs:F0} ms, {Failed - _startFailed} failed, {Overtaken - _startOvertaken} reached by their draw first); " +
                $"first draws of pre-warmed programs: {Matched - _startMatched} asked for the pre-built state, {Mismatched - _startMismatched} for another; " +
                $"{ComputeUsed - _startComputeUsed} pre-built compute pipelines used. " +
                $"Pipelines still compiled at draw time this session: {PipelineState.PsoRenderCreated - _startRenderCompiles} render " +
                $"({(PipelineState.PsoRenderTicks - _startRenderCompileTicks) * tickMs:F0} ms), " +
                $"{PipelineState.PsoComputeCreated - _startComputeCompiles} compute ({(PipelineState.PsoComputeTicks - _startComputeCompileTicks) * tickMs:F0} ms).");
        }
    }
}
