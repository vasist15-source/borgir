using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    struct PipelineState
    {
        public PipelineUid Internal;

        public uint StagesCount
        {
            readonly get => (byte)((Internal.Id0 >> 0) & 0xFF);
            set => Internal.Id0 = (Internal.Id0 & 0xFFFFFFFFFFFFFF00) | ((ulong)value << 0);
        }

        public uint VertexAttributeDescriptionsCount
        {
            readonly get => (byte)((Internal.Id0 >> 8) & 0xFF);
            set => Internal.Id0 = (Internal.Id0 & 0xFFFFFFFFFFFF00FF) | ((ulong)value << 8);
        }

        public uint VertexBindingDescriptionsCount
        {
            readonly get => (byte)((Internal.Id0 >> 16) & 0xFF);
            set => Internal.Id0 = (Internal.Id0 & 0xFFFFFFFFFF00FFFF) | ((ulong)value << 16);
        }

        public uint ColorBlendAttachmentStateCount
        {
            readonly get => (byte)((Internal.Id0 >> 24) & 0xFF);
            set => Internal.Id0 = (Internal.Id0 & 0xFFFFFFFF00FFFFFF) | ((ulong)value << 24);
        }

        /*
         * Can be an input to a pipeline, but not sure what the situation for that is.
        public PrimitiveTopology Topology
        {
            readonly get => (PrimitiveTopology)((Internal.Id6 >> 16) & 0xF);
            set => Internal.Id6 = (Internal.Id6 & 0xFFFFFFFFFFF0FFFF) | ((ulong)value << 16);
        }
        */

        public MTLLogicOperation LogicOp
        {
            readonly get => (MTLLogicOperation)((Internal.Id0 >> 32) & 0xF);
            set => Internal.Id0 = (Internal.Id0 & 0xFFFFFFF0FFFFFFFF) | ((ulong)value << 32);
        }

        //?
        public bool PrimitiveRestartEnable
        {
            readonly get => ((Internal.Id0 >> 36) & 0x1) != 0UL;
            set => Internal.Id0 = (Internal.Id0 & 0xFFFFFFEFFFFFFFFF) | ((value ? 1UL : 0UL) << 36);
        }

        public bool RasterizerDiscardEnable
        {
            readonly get => ((Internal.Id0 >> 37) & 0x1) != 0UL;
            set => Internal.Id0 = (Internal.Id0 & 0xFFFFFFDFFFFFFFFF) | ((value ? 1UL : 0UL) << 37);
        }

        public bool LogicOpEnable
        {
            readonly get => ((Internal.Id0 >> 38) & 0x1) != 0UL;
            set => Internal.Id0 = (Internal.Id0 & 0xFFFFFFBFFFFFFFFF) | ((value ? 1UL : 0UL) << 38);
        }

        public bool AlphaToCoverageEnable
        {
            readonly get => ((Internal.Id0 >> 40) & 0x1) != 0UL;
            set => Internal.Id0 = (Internal.Id0 & 0xFFFFFEFFFFFFFFFF) | ((value ? 1UL : 0UL) << 40);
        }

        public bool AlphaToOneEnable
        {
            readonly get => ((Internal.Id0 >> 41) & 0x1) != 0UL;
            set => Internal.Id0 = (Internal.Id0 & 0xFFFFFDFFFFFFFFFF) | ((value ? 1UL : 0UL) << 41);
        }

        public MTLPixelFormat DepthStencilFormat
        {
            readonly get => (MTLPixelFormat)(Internal.Id0 >> 48);
            set => Internal.Id0 = (Internal.Id0 & 0x0000FFFFFFFFFFFF) | ((ulong)value << 48);
        }

        // Not sure how to appropriately use this, but it does need to be passed for tess.
        public uint PatchControlPoints
        {
            readonly get => (uint)((Internal.Id1 >> 0) & 0xFFFFFFFF);
            set => Internal.Id1 = (Internal.Id1 & 0xFFFFFFFF00000000) | ((ulong)value << 0);
        }

        public uint SamplesCount
        {
            readonly get => (uint)((Internal.Id1 >> 32) & 0xFFFFFFFF);
            set => Internal.Id1 = (Internal.Id1 & 0xFFFFFFFF) | ((ulong)value << 32);
        }

        // Advanced blend not supported

        private readonly void BuildColorAttachment(MTLRenderPipelineColorAttachmentDescriptor descriptor, ColorBlendStateUid blendState)
        {
            descriptor.PixelFormat = blendState.PixelFormat;
            descriptor.SetBlendingEnabled(blendState.Enable);
            descriptor.AlphaBlendOperation = blendState.AlphaBlendOperation;
            descriptor.RgbBlendOperation = blendState.RgbBlendOperation;
            descriptor.SourceAlphaBlendFactor = blendState.SourceAlphaBlendFactor;
            descriptor.DestinationAlphaBlendFactor = blendState.DestinationAlphaBlendFactor;
            descriptor.SourceRGBBlendFactor = blendState.SourceRGBBlendFactor;
            descriptor.DestinationRGBBlendFactor = blendState.DestinationRGBBlendFactor;
            descriptor.WriteMask = blendState.WriteMask;
        }

        /// <summary>
        /// Sets the vertex attributes and buffer layouts of this state from the GAL vertex
        /// state. Shared by the encoder and <see cref="PipelineConverter"/>, so a pipeline built
        /// ahead of its draw gets the same key the draw computes.
        /// </summary>
        public void UpdateVertexState(VertexBufferState[] bufferDescriptors, VertexAttribDescriptor[] attribDescriptors)
        {
            uint indexMask = 0;

            for (int i = 0; i < attribDescriptors.Length; i++)
            {
                ref VertexInputAttributeUid attrib = ref Internal.VertexAttributes[i];

                if (attribDescriptors[i].IsZero)
                {
                    attrib.Format = attribDescriptors[i].Format.Convert();
                    indexMask |= 1u << (int)Constants.ZeroBufferIndex;
                    attrib.BufferIndex = Constants.ZeroBufferIndex;
                    attrib.Offset = 0;
                }
                else
                {
                    attrib.Format = attribDescriptors[i].Format.Convert();
                    indexMask |= 1u << attribDescriptors[i].BufferIndex;
                    attrib.BufferIndex = (ulong)attribDescriptors[i].BufferIndex;
                    attrib.Offset = (ulong)attribDescriptors[i].Offset;
                }
            }

            for (int i = 0; i < bufferDescriptors.Length; i++)
            {
                ref VertexInputLayoutUid layout = ref Internal.VertexBindings[i];

                if ((indexMask & (1u << i)) != 0)
                {
                    layout.Stride = (uint)bufferDescriptors[i].Stride;

                    if (layout.Stride == 0)
                    {
                        layout.Stride = 1;
                        layout.StepFunction = MTLVertexStepFunction.Constant;
                        layout.StepRate = 0;
                    }
                    else
                    {
                        if (bufferDescriptors[i].Divisor > 0)
                        {
                            layout.StepFunction = MTLVertexStepFunction.PerInstance;
                            layout.StepRate = (uint)bufferDescriptors[i].Divisor;
                        }
                        else
                        {
                            layout.StepFunction = MTLVertexStepFunction.PerVertex;
                            layout.StepRate = 1;
                        }
                    }
                }
                else
                {
                    layout = new();
                }
            }

            ref VertexInputLayoutUid zeroBufLayout = ref Internal.VertexBindings[(int)Constants.ZeroBufferIndex];

            // Zero buffer
            if ((indexMask & (1u << (int)Constants.ZeroBufferIndex)) != 0)
            {
                zeroBufLayout.Stride = 1;
                zeroBufLayout.StepFunction = MTLVertexStepFunction.Constant;
                zeroBufLayout.StepRate = 0;
            }
            else
            {
                zeroBufLayout = new();
            }

            VertexAttributeDescriptionsCount = (uint)attribDescriptors.Length;
            VertexBindingDescriptionsCount = Constants.ZeroBufferIndex + 1; // TODO: move this out?
        }

        private readonly MTLVertexDescriptor BuildVertexDescriptor(bool prewarm)
        {
            MTLVertexDescriptor vertexDescriptor = new();

            for (int i = 0; i < VertexAttributeDescriptionsCount; i++)
            {
                VertexInputAttributeUid uid = Internal.VertexAttributes[i];

                MTLVertexAttributeDescriptor attrib = vertexDescriptor.Attributes.Object((ulong)i);
                attrib.Format = uid.Format;
                attrib.Offset = uid.Offset;
                attrib.BufferIndex = uid.BufferIndex;

                // The last unread link. A zero-size format, an offset past the stride, or
                // an attribute that is simply absent all leave the shader's zero-initialised
                // attribute untouched - constant across the primitive, which is a screen
                // filled with one texel.
                if (i == 0 && !prewarm)
                {
                    UploadCorrelator.NoteVertexAttrib((int)uid.Format, (int)uid.Offset, (int)uid.BufferIndex);
                }
            }

            for (int i = 0; i < VertexBindingDescriptionsCount; i++)
            {
                VertexInputLayoutUid uid = Internal.VertexBindings[i];

                MTLVertexBufferLayoutDescriptor layout = vertexDescriptor.Layouts.Object((ulong)i);

                layout.StepFunction = uid.StepFunction;
                layout.StepRate = uid.StepRate;
                layout.Stride = uid.Stride;
            }

            return vertexDescriptor;
        }

        private readonly MTLRenderPipelineDescriptor CreateRenderDescriptor(Program program)
        {
            return CreateRenderDescriptor(program, SnapshotSlot, prewarm: false);
        }

        /// <summary>
        /// The descriptor for a pipeline built off the render thread ahead of its draw: no
        /// tile snapshot slot (the static belongs to the pass the render thread is encoding)
        /// and no draw-time probes.
        /// </summary>
        public readonly MTLRenderPipelineDescriptor CreatePrewarmDescriptor(Program program)
        {
            return CreateRenderDescriptor(program, snapshotSlot: -1, prewarm: true);
        }

        private readonly MTLRenderPipelineDescriptor CreateRenderDescriptor(Program program, int snapshotSlot, bool prewarm)
        {
            MTLRenderPipelineDescriptor renderPipelineDescriptor = new();

            for (int i = 0; i < Constants.MaxColorAttachments; i++)
            {
                ColorBlendStateUid blendState = Internal.ColorBlendState[i];

                if (blendState.PixelFormat != MTLPixelFormat.Invalid)
                {
                    MTLRenderPipelineColorAttachmentDescriptor pipelineAttachment = renderPipelineDescriptor.ColorAttachments.Object((ulong)i);

                    BuildColorAttachment(pipelineAttachment, blendState);

                    if (i == snapshotSlot)
                    {
                        pipelineAttachment.WriteMask = MTLColorWriteMask.None;
                    }

                    // An attachment the fragment function does not write gets UNDEFINED
                    // contents from Metal (the OpenGL backend masks these with the program's
                    // FragmentOutputMap; MoltenVK disables the write mask for them). Without
                    // this, a render target the guest left bound - e.g. the scene texture
                    // during the game's upscaler draw - is overwritten with garbage.
                    // RYUJINX_METAL_MASK_UNWRITTEN=0 opts out.
                    if (MaskUnwrittenOutputs && program.FragmentOutputMap != -1)
                    {
                        uint bits = ((uint)program.FragmentOutputMap >> (i * 4)) & 0xFu;
                        MTLColorWriteMask allowed = MTLColorWriteMask.None;
                        allowed |= (bits & 1u) != 0 ? MTLColorWriteMask.Red : 0;
                        allowed |= (bits & 2u) != 0 ? MTLColorWriteMask.Green : 0;
                        allowed |= (bits & 4u) != 0 ? MTLColorWriteMask.Blue : 0;
                        allowed |= (bits & 8u) != 0 ? MTLColorWriteMask.Alpha : 0;
                        MTLColorWriteMask before = pipelineAttachment.WriteMask;
                        MTLColorWriteMask after = before & allowed;
                        if (after != before)
                        {
                            pipelineAttachment.WriteMask = after;
                            Interlocked.Increment(ref MaskedUnwrittenCount);
                        }
                    }
                }
            }

            MTLPixelFormat dsFormat = DepthStencilFormat;
            if (dsFormat != MTLPixelFormat.Invalid)
            {
                switch (dsFormat)
                {
                    // Depth Only Attachment
                    case MTLPixelFormat.Depth16Unorm:
                    case MTLPixelFormat.Depth32Float:
                        renderPipelineDescriptor.DepthAttachmentPixelFormat = dsFormat;
                        break;

                    // Stencil Only Attachment
                    case MTLPixelFormat.Stencil8:
                        renderPipelineDescriptor.StencilAttachmentPixelFormat = dsFormat;
                        break;

                    // Combined Attachment
                    case MTLPixelFormat.Depth24UnormStencil8:
                    case MTLPixelFormat.Depth32FloatStencil8:
                        renderPipelineDescriptor.DepthAttachmentPixelFormat = dsFormat;
                        renderPipelineDescriptor.StencilAttachmentPixelFormat = dsFormat;
                        break;
                    default:
                        Logger.Error?.PrintMsg(LogClass.Gpu, $"Unsupported Depth/Stencil Format: {dsFormat}!");
                        break;
                }
            }

            renderPipelineDescriptor.LogicOperationEnabled = LogicOpEnable;
            renderPipelineDescriptor.LogicOperation = LogicOp;
            renderPipelineDescriptor.AlphaToCoverageEnabled = AlphaToCoverageEnable;
            renderPipelineDescriptor.AlphaToOneEnabled = AlphaToOneEnable;
            renderPipelineDescriptor.RasterizationEnabled = !RasterizerDiscardEnable;
            renderPipelineDescriptor.SampleCount = Math.Max(1, SamplesCount);

            MTLVertexDescriptor vertexDescriptor = BuildVertexDescriptor(prewarm);
            renderPipelineDescriptor.VertexDescriptor = vertexDescriptor;

            renderPipelineDescriptor.VertexFunction = program.VertexFunction;

            if (program.FragmentFunction.NativePtr != 0)
            {
                renderPipelineDescriptor.FragmentFunction = program.FragmentFunction;
            }

            return renderPipelineDescriptor;
        }

        public static readonly bool MaskUnwrittenOutputs = Environment.GetEnvironmentVariable("RYUJINX_METAL_MASK_UNWRITTEN") != "0";

        /// <summary>The pass's tile-snapshot attachment slot, which no fragment function may write; -1 when the pass has none.</summary>
        public static int SnapshotSlot = -1;
        public static long MaskedUnwrittenCount;

        // Read-only probe for the synchronous pipeline-state compiles. A cache miss
        // below is a GPU back-end compile on the render thread, and nothing else in
        // the stats line can see one; a burst of them is the leading suspect for the
        // camera-turn drops that are neither sync waits nor pass count. Only the miss
        // path touches these, so the hit path is as it was.
        public static long PsoRenderCreated;
        public static long PsoRenderTicks;
        public static long PsoRenderMaxTicks;
        public static long PsoComputeCreated;
        public static long PsoComputeTicks;

        // Asynchronous pipeline-state builds (v489). The v403 probe measured a camera
        // turn at ~1000 render pipeline builds in 22 s, 0.3 ms each when the OS shader
        // cache hits, stacked 170-200 to a frame - a 24-75 ms frame from builds alone,
        // about a quarter of the excess of the slow frames. A cache miss now creates the
        // descriptor on the render thread (it reads the program and this state) and hands
        // the driver build to the thread pool; the draw is skipped until the pipeline
        // lands, exactly as a draw whose shaders are still compiling is skipped, and the
        // dirty flag left set by the invalid pipeline retries it on the next draw.
        // RYUJINX_METAL_ASYNC_PSO=0, or /tmp/ryujinx-metal-async-pso holding 0, restores
        // the synchronous build. Compute pipelines stay synchronous (28 per session).
        private static readonly bool _asyncPsoDefault = Environment.GetEnvironmentVariable("RYUJINX_METAL_ASYNC_PSO") != "0";
        public static bool AsyncPso = _asyncPsoDefault;
        public static long PsoRenderQueued;
        public static long PsoRenderPendingSkips;

        // Per-frame wait budget. A draw whose pipeline is still building waits for it,
        // up to this many milliseconds per frame in total, before it is skipped. A build
        // takes ~0.3 ms and the pool runs them in parallel, so the budget turns nearly
        // every pending skip (v490: 6225 skipped draws for 2814 builds in 3.5 minutes,
        // one missing object per skip) into a sub-millisecond wait while still bounding
        // what a burst can cost a frame. RYUJINX_METAL_ASYNC_PSO_WAIT_MS, default 4;
        // 0 never waits.
        private static readonly long _asyncPsoWaitBudgetTicks =
            (long)((double.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_ASYNC_PSO_WAIT_MS"), NumberStyles.Float, CultureInfo.InvariantCulture, out double waitMs) ? waitMs : 4.0) * Stopwatch.Frequency / 1000.0);
        private static long _psoWaitSpentTicksThisFrame;
        // Vertex (or index) count of the draw being prepared, set by the Pipeline draw
        // entry points; int.MaxValue for indirect draws. 3-6 means a fullscreen pass.
        public static int CurrentDrawVertices = int.MaxValue;
        // RYUJINX_METAL_ASYNC_PSO_FULLSCREEN_SYNC=0 lets fullscreen draws skip like the rest;
        // RYUJINX_METAL_ASYNC_PSO_BURST=N (default 64, 0 off) is the per-frame queue count
        // past which the frame builds the rest synchronously.
        private static readonly bool _fullscreenSync = Environment.GetEnvironmentVariable("RYUJINX_METAL_ASYNC_PSO_FULLSCREEN_SYNC") != "0";
        private static readonly int _burstSyncThreshold =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_ASYNC_PSO_BURST"), out int burst) ? burst : 64;
        private static int _psoFrameQueued;
        public static long PsoRenderSyncFallbacks;
        public static long PsoRenderWaited;
        public static long PsoRenderWaitTicks;
        public static long PsoFramePendingSkips;
        public static long PsoFrameWaitTicks;

        /// <summary>Once per presented frame: this frame's pending skips and wait time, and a fresh wait budget.</summary>
        public static void TakeFrameCounters(out long pendingSkips, out long waitTicks)
        {
            pendingSkips = PsoFramePendingSkips;
            waitTicks = PsoFrameWaitTicks;
            PsoFramePendingSkips = 0;
            PsoFrameWaitTicks = 0;
            _psoWaitSpentTicksThisFrame = 0;
            _psoFrameQueued = 0;
        }

        private MTLRenderPipelineState WaitForPendingPipeline(Program program, bool unbounded)
        {
            long waitStart = Stopwatch.GetTimestamp();
            SpinWait spin = new();

            while (true)
            {
                if (program.TryGetGraphicsPipeline(ref Internal, out MTLRenderPipelineState built))
                {
                    long waited = Stopwatch.GetTimestamp() - waitStart;
                    _psoWaitSpentTicksThisFrame += waited;
                    PsoFrameWaitTicks += waited;
                    PsoRenderWaitTicks += waited;
                    PsoRenderWaited++;

                    return built;
                }

                if (!program.IsGraphicsPipelinePending(ref Internal))
                {
                    // A pre-warm build stops being pending as it lands, so look once more
                    // before treating it as failed.
                    if (program.TryGetGraphicsPipeline(ref Internal, out built))
                    {
                        return built;
                    }

                    // The build failed; the next draw that needs it queues it again.
                    break;
                }

                long now = Stopwatch.GetTimestamp();

                if (!unbounded && _psoWaitSpentTicksThisFrame + (now - waitStart) >= _asyncPsoWaitBudgetTicks)
                {
                    _psoWaitSpentTicksThisFrame += now - waitStart;
                    PsoFrameWaitTicks += now - waitStart;
                    break;
                }

                spin.SpinOnce(-1);
            }

            PsoRenderPendingSkips++;
            PsoFramePendingSkips++;

            return default;
        }

        public static void RefreshAsyncPso()
        {
            try
            {
                AsyncPso = File.Exists("/tmp/ryujinx-metal-async-pso")
                    ? File.ReadAllText("/tmp/ryujinx-metal-async-pso").Trim() != "0"
                    : _asyncPsoDefault;
            }
            catch (IOException)
            {
                // Raced with the writer; the next frame picks it up.
            }
        }

        public MTLRenderPipelineState CreateRenderPipeline(MTLDevice device, Program program)
        {
            program.NotePrewarmDraw(ref Internal);

            if (program.TryGetGraphicsPipeline(ref Internal, out MTLRenderPipelineState pipelineState))
            {
                return pipelineState;
            }

            // Two draws must not be skipped: a fullscreen one (3-6 vertices: the
            // post-processing, exposure and tonemap passes - skipping one of those
            // paints the whole frame wrong, the white flashes reported on v490/v491),
            // and any draw once a frame has queued more than the burst threshold,
            // which is a scene load rather than a turn (v491 skipped 1974 draws in
            // the 120 frames after a load). Those build in place, or wait without a
            // budget for the build already in flight.
            bool mustHave = !AsyncPso ||
                            (_fullscreenSync && CurrentDrawVertices <= 6) ||
                            (_burstSyncThreshold > 0 && _psoFrameQueued >= _burstSyncThreshold);

            // In flight already: an asynchronous build, or the pre-warm of this very state.
            if (program.IsGraphicsPipelinePending(ref Internal))
            {
                if (mustHave)
                {
                    return WaitForPendingPipeline(program, unbounded: true);
                }

                if (_asyncPsoWaitBudgetTicks > 0 && _psoWaitSpentTicksThisFrame < _asyncPsoWaitBudgetTicks)
                {
                    return WaitForPendingPipeline(program, unbounded: false);
                }

                PsoRenderPendingSkips++;
                PsoFramePendingSkips++;

                return default;
            }

            // This draw builds it, so a pre-warm of the same state still waiting in the
            // queue would only build it twice.
            program.ClaimPrewarm(ref Internal);

            if (AsyncPso)
            {
                if (!mustHave)
                {
                    MTLRenderPipelineDescriptor asyncDescriptor = CreateRenderDescriptor(program);
                    PipelineUid key = Internal;

                    program.BeginGraphicsPipelineBuild(ref key);
                    PsoRenderQueued++;
                    _psoFrameQueued++;

                    ThreadPool.UnsafeQueueUserWorkItem(_ =>
                    {
                        NSError asyncError = new(IntPtr.Zero);
                        long asyncStart = Stopwatch.GetTimestamp();
                        MTLRenderPipelineState built = device.NewRenderPipelineState(asyncDescriptor, ref asyncError);
                        long asyncTicks = Stopwatch.GetTimestamp() - asyncStart;

                        asyncDescriptor.Dispose();

                        Interlocked.Increment(ref PsoRenderCreated);
                        Interlocked.Add(ref PsoRenderTicks, asyncTicks);

                        if (asyncTicks > PsoRenderMaxTicks)
                        {
                            PsoRenderMaxTicks = asyncTicks;
                        }

                        if (asyncError != IntPtr.Zero)
                        {
                            Logger.Error?.PrintMsg(LogClass.Gpu, $"Failed to create Render Pipeline State: {StringHelper.String(asyncError.LocalizedDescription)}");
                            built = default;
                        }

                        PipelineUid builtKey = key;
                        program.EndGraphicsPipelineBuild(ref builtKey, built);
                    }, null);

                    if (_asyncPsoWaitBudgetTicks > 0 && _psoWaitSpentTicksThisFrame < _asyncPsoWaitBudgetTicks)
                    {
                        return WaitForPendingPipeline(program, unbounded: false);
                    }

                    PsoRenderPendingSkips++;
                    PsoFramePendingSkips++;

                    return default;
                }

                // Fullscreen or burst: the synchronous build below, counted.
                PsoRenderSyncFallbacks++;
            }

            using MTLRenderPipelineDescriptor descriptor = CreateRenderDescriptor(program);

            NSError error = new(IntPtr.Zero);
            long psoStart = Stopwatch.GetTimestamp();
            pipelineState = device.NewRenderPipelineState(descriptor, ref error);
            long psoTicks = Stopwatch.GetTimestamp() - psoStart;
            PsoRenderCreated++;
            PsoRenderTicks += psoTicks;

            if (psoTicks > PsoRenderMaxTicks)
            {
                PsoRenderMaxTicks = psoTicks;
            }

            if (error != IntPtr.Zero)
            {
                Logger.Error?.PrintMsg(LogClass.Gpu, $"Failed to create Render Pipeline State: {StringHelper.String(error.LocalizedDescription)}");
            }

            // Only cache valid pipeline states. A null/zero PSO from a failed
            // compilation would otherwise be returned on every subsequent draw
            // with the same pipeline configuration, causing a SIGSEGV in the
            // Metal driver's drawPrimitives.
            if (pipelineState.NativePtr != IntPtr.Zero)
            {
                program.AddGraphicsPipeline(ref Internal, pipelineState);
            }

            return pipelineState;
        }

        public static MTLComputePipelineDescriptor CreateComputeDescriptor(Program program)
        {
            ComputeSize localSize = program.ComputeLocalSize;

            uint maxThreads = (uint)(localSize.X * localSize.Y * localSize.Z);

            if (maxThreads == 0)
            {
                throw new InvalidOperationException($"Local thread size for compute cannot be 0 in any dimension.");
            }

            MTLComputePipelineDescriptor descriptor = new()
            {
                ComputeFunction = program.ComputeFunction,
                MaxTotalThreadsPerThreadgroup = maxThreads,
                // Promising this unconditionally while games dispatch local sizes
                // like 1x1x1 breaks the promise (validation: "must be multiples of
                // 32") and makes the compute results undefined - fatal for
                // GPU-driven culling and page tables. Only promise it when the
                // shader's local size really is SIMD-group aligned (Apple GPUs have
                // a thread execution width of 32).
                ThreadGroupSizeIsMultipleOfThreadExecutionWidth = (maxThreads % 32) == 0,
            };

            return descriptor;
        }

        public static MTLComputePipelineState CreateComputePipeline(MTLDevice device, Program program)
        {
            if (program.TryGetComputePipeline(out MTLComputePipelineState pipelineState))
            {
                return pipelineState;
            }

            // A pre-warm already building it is waited for; one still queued is dropped.
            if (program.WaitForComputePrewarm(out pipelineState))
            {
                return pipelineState;
            }

            using MTLComputePipelineDescriptor descriptor = CreateComputeDescriptor(program);

            NSError error = new(IntPtr.Zero);
            long psoStart = Stopwatch.GetTimestamp();
            pipelineState = device.NewComputePipelineState(descriptor, MTLPipelineOption.None, 0, ref error);
            PsoComputeTicks += Stopwatch.GetTimestamp() - psoStart;
            PsoComputeCreated++;

            if (error != IntPtr.Zero)
            {
                Logger.Error?.PrintMsg(LogClass.Gpu, $"Failed to create Compute Pipeline State: {StringHelper.String(error.LocalizedDescription)}");
            }

            program.AddComputePipeline(pipelineState);

            return pipelineState;
        }

        public void Initialize()
        {
            SamplesCount = 1;

            Internal.ResetColorState();
        }

        /*
         * TODO, this is from vulkan.

        private void UpdateVertexAttributeDescriptions(VulkanRenderer gd)
        {
            // Vertex attributes exceeding the stride are invalid.
            // In metal, they cause glitches with the vertex shader fetching incorrect values.
            // To work around this, we reduce the format to something that doesn't exceed the stride if possible.
            // The assumption is that the exceeding components are not actually accessed on the shader.

            for (int index = 0; index < VertexAttributeDescriptionsCount; index++)
            {
                var attribute = Internal.VertexAttributeDescriptions[index];
                int vbIndex = GetVertexBufferIndex(attribute.Binding);

                if (vbIndex >= 0)
                {
                    ref var vb = ref Internal.VertexBindingDescriptions[vbIndex];

                    Format format = attribute.Format;

                    while (vb.Stride != 0 && attribute.Offset + FormatTable.GetAttributeFormatSize(format) > vb.Stride)
                    {
                        Format newFormat = FormatTable.DropLastComponent(format);

                        if (newFormat == format)
                        {
                            // That case means we failed to find a format that fits within the stride,
                            // so just restore the original format and give up.
                            format = attribute.Format;
                            break;
                        }

                        format = newFormat;
                    }

                    if (attribute.Format != format && gd.FormatCapabilities.BufferFormatSupports(FormatFeatureFlags.VertexBufferBit, format))
                    {
                        attribute.Format = format;
                    }
                }

                _vertexAttributeDescriptions2[index] = attribute;
            }
        }

        private int GetVertexBufferIndex(uint binding)
        {
            for (int index = 0; index < VertexBindingDescriptionsCount; index++)
            {
                if (Internal.VertexBindingDescriptions[index].Binding == binding)
                {
                    return index;
                }
            }

            return -1;
        }
        */
    }
}
