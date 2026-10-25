using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader.Translation;
using SharpMetal.Metal;
using SharpMetal.QuartzCore;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    public sealed class MetalRenderer : IRenderer
    {
        public const int TotalSets = 4;
        private const ulong MaxReportedGpuMemory = 8UL * 1024 * 1024 * 1024;

        private readonly MTLDevice _device;

        /// <summary>Bytes the Metal device has allocated (textures, buffers, heaps): for telling GPU-side growth from managed growth.</summary>
        internal ulong DeviceAllocatedBytes => _device.CurrentAllocatedSize;
        private readonly MTLCommandQueue _queue;
        private readonly Func<CAMetalLayer> _getMetalLayer;

        private Pipeline _pipeline;
        private Window _window;

        public uint ProgramCount { get; set; }

#pragma warning disable CS0067 // The event is never used
        public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured;
#pragma warning restore CS0067

        public bool PreferThreading => true;
        public IPipeline Pipeline => _pipeline;
        public IWindow Window => _window;

        internal MTLCommandQueue BackgroundQueue { get; private set; }
        internal HelperShader HelperShader { get; private set; }
        internal BufferManager BufferManager { get; private set; }
        internal CommandBufferPool CommandBufferPool { get; private set; }
        internal BackgroundResources BackgroundResources { get; private set; }
        internal Action<Action> InterruptAction { get; private set; }
        internal SyncManager SyncManager { get; private set; }
        internal AutoFlushCounter AutoFlush { get; private set; }
        internal FrameCapture FrameCapture { get; private set; }
        internal CounterManager Counters { get; private set; }
        internal PipelinePrewarmer PipelinePrewarmer { get; private set; }

        internal HashSet<Program> Programs { get; }
        internal HashSet<SamplerHolder> Samplers { get; }

        public MetalRenderer(Func<CAMetalLayer> metalLayer)
        {
            _device = MTLDevice.CreateSystemDefaultDevice();
            Programs = [];
            Samplers = [];

            if (_device.ArgumentBuffersSupport != MTLArgumentBuffersTier.Tier2)
            {
                throw new NotSupportedException("Metal backend requires Tier 2 Argument Buffer support.");
            }

            // Background texture readbacks used to run on a second MTLCommandQueue. Metal
            // orders command buffers only within a queue, and this backend contains no
            // MTLEvent and no MTLFence anywhere, so a readback issued from a background
            // thread raced the main queue's render into the very texture it was copying -
            // an unsynchronised read of a live render target, whose torn result is then
            // written back into guest memory and can be uploaded again later.
            //
            // Vulkan never does this on macOS. MoltenVK fixes queue count per family at
            // one (kMVKQueueCountPerQueueFamily = 1, "Must be 1"), so VulkanRenderer's
            // `maxQueueCount >= 2` test fails, BackgroundQueue is never created, and
            // BackgroundResources falls back to the main queue and its lock. Sharing the
            // queue here restores that same guarantee: one timeline, commit order, and
            // Metal's automatic hazard tracking across the whole submission stream.
            bool splitQueue = Environment.GetEnvironmentVariable("RYUJINX_METAL_SPLIT_QUEUE") == "1";

            // A shared queue carries the main pool and every per-thread background pool at
            // once, so the ceiling on uncompleted command buffers has to cover both; asking
            // for more than the queue allows blocks the caller until one retires.
            _queue = _device.NewCommandQueue((ulong)(splitQueue
                ? CommandBufferPool.MaxCommandBuffers + 1
                : CommandBufferPool.MaxCommandBuffers * 2 + 8));

            BackgroundQueue = splitQueue
                ? _device.NewCommandQueue(CommandBufferPool.MaxCommandBuffers)
                : _queue;

            _getMetalLayer = metalLayer;
        }

        public void Initialize(GraphicsDebugLevel logLevel)
        {
            CAMetalLayer layer = _getMetalLayer();
            layer.Device = _device;
            layer.FramebufferOnly = false;

            CommandBufferPool = new CommandBufferPool(_queue);
            AutoFlush = new AutoFlushCounter(this);
            FrameCapture = new FrameCapture(_queue);
            _window = new Window(this, layer);
            _pipeline = new Pipeline(_device, this);
            BufferManager = new BufferManager(_device, this, _pipeline);

            _pipeline.InitEncoderStateManager(BufferManager);
            Counters = new CounterManager(_device, this);

            BackgroundResources = new BackgroundResources(this);
            HelperShader = new HelperShader(_device, this, _pipeline);
            SyncManager = new SyncManager(this);

            if (PipelinePrewarmer.Enabled || PipelinePrewarmer.DeferCompile)
            {
                PipelinePrewarmer = new PipelinePrewarmer(_device);
            }

            PresentProbe.Init(_device);
            FlashGuard.Init(_device);
            UploadCorrelator.Init(_device);
            UploadCorrelator.AttachPool(CommandBufferPool);
            CaptureHunter.Init(_device, _queue);
            CoverageProbe.Init(_device);

            if (HdrPassProbe.Enabled)
            {
                HdrPassProbe.Initialize(_device);
            }

            if (FrameProbe.Enabled)
            {
                FrameProbe.Initialize(_device);
                FrameProbe.SetPipeline(_pipeline);
            }
        }

        public void BackgroundContextAction(Action action, bool alwaysBackground = false)
        {
            // GetData methods should be thread safe, so we can call this directly.
            // Texture copy (scaled) may also happen in here, so that should also be thread safe.

            action();
        }

        public BufferHandle CreateBuffer(int size, BufferAccess access)
        {
            return BufferManager.CreateWithHandle(size);
        }

        public BufferHandle CreateBuffer(IntPtr pointer, int size)
        {
            return BufferManager.Create(pointer, size);
        }

        public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
        {
            throw new NotImplementedException();
        }

        public IImageArray CreateImageArray(int size, bool isBuffer)
        {
            return new ImageArray(size, isBuffer, _pipeline);
        }

        public IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info)
        {
            ProgramCount++;
            return new Program(this, _device, shaders, info.ResourceLayout, info.ComputeLocalSize, info.FragmentOutputMap, info.State, info.FromCache);
        }

        public ISampler CreateSampler(SamplerCreateInfo info)
        {
            return new SamplerHolder(this, _device, info);
        }

        public ITexture CreateTexture(TextureCreateInfo info)
        {
            if (info.Target == Target.TextureBuffer)
            {
                return new TextureBuffer(_device, this, _pipeline, info);
            }

            return new Texture(_device, this, _pipeline, info);
        }

        public ITextureArray CreateTextureArray(int size, bool isBuffer)
        {
            return new TextureArray(size, isBuffer, _pipeline);
        }

        public bool PrepareHostMapping(IntPtr address, ulong size)
        {
            // TODO: Metal Host Mapping
            return false;
        }

        public void CreateSync(ulong id, bool strict, HostSyncCreateSource source = HostSyncCreateSource.Unknown)
        {
            SyncManager.Create(id, strict, source);
        }

        public void DeleteBuffer(BufferHandle buffer)
        {
            BufferManager.Delete(buffer);
        }

        public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
        {
            return BufferManager.GetData(buffer, offset, size);
        }

        public Capabilities GetCapabilities()
        {
            // TODO: Finalize these values
            return new Capabilities(
                api: TargetApi.Metal,
                vendorName: HardwareInfoTools.GetVendor(),
                SystemMemoryType.UnifiedMemory,
                hasFrontFacingBug: false,
                hasVectorIndexingBug: false,
                needsFragmentOutputSpecialization: true,
                reduceShaderPrecision: true,
                supportsAstcCompression: true,
                supportsBc123Compression: true,
                supportsBc45Compression: true,
                supportsBc67Compression: true,
                // SharpMetal 1.0.0-preview21 exposes ETC2 RGB/RGB_A1 formats, but not
                // ETC2 RGBA8/RGBA8 sRGB. Report ETC2 as unsupported so the GPU texture
                // compatibility path decodes all ETC2 variants to RGBA8 instead of
                // allowing unsupported RGBA ETC2 textures to reach the Metal backend.
                supportsEtc2Compression: false,
                // Metal only allows block-compressed (BC/ASTC/ETC) pixel formats on
                // 2D, 2D array and cube textures - never on 3D textures. TOTK's Depths
                // gloom samples BC4-compressed 3D volumetric-noise textures; reporting
                // 3D compression as supported let those reach Metal, where the samples
                // came back garbage, so the gloom's noise-driven discard over-culled
                // the effect (sparse red streaks instead of a full field) and the
                // gloom coverage the game reads back for damage was wrong too. Report
                // it unsupported so the texture compatibility path decodes 3D BC/ASTC
                // textures to an uncompressed format before upload.
                supports3DTextureCompression: false,
                supportsBgraFormat: true,
                supportsR4G4Format: false,
                supportsR4G4B4A4Format: true,
                supportsScaledVertexFormats: false,
                supportsSnormBufferTextureFormat: true,
                supportsSparseBuffer: false,
                supports5BitComponentFormat: true,
                supportsBlendEquationAdvanced: false,
                supportsFragmentShaderInterlock: true,
                supportsFragmentShaderOrderingIntel: false,
                supportsGeometryShader: false,
                supportsGeometryShaderPassthrough: false,
                supportsTransformFeedback: false,
                supportsImageLoadFormatted: false,
                supportsLayerVertexTessellation: false,
                supportsMismatchingViewFormat: true,
                supportsCubemapView: true,
                supportsNonConstantTextureOffset: false,
                supportsQuads: false,
                supportsSeparateSampler: true,
                supportsShaderBallot: false,
                supportsShaderBarrierDivergence: false,
                supportsShaderFloat64: false,
                supportsShaderNonUniformIndexing: false,
                supportsTextureGatherOffsets: false,
                supportsTextureShadowLod: false,
                supportsVertexStoreAndAtomics: true,
                supportsViewportIndexVertexTessellation: false,
                supportsViewportMask: false,
                supportsViewportSwizzle: false,
                // The backend's DrawIndexedIndirectCount/DrawIndirectCount currently
                // ignore the GPU-written draw count buffer and blindly loop
                // maxDrawCount times, which breaks GPU-driven foliage and effect
                // rendering (missing luminous plants, frozen gloom in TOTK).
                // MoltenVK does not expose VK_KHR_draw_indirect_count on this
                // hardware either, so the Vulkan backend runs the GPU emulation
                // layer's indirect-count fallback path — report false so Metal
                // takes the same proven-correct path.
                supportsIndirectParameters: false,
                supportsDepthClipControl: false,
                uniformBufferSetIndex: (int)Constants.ConstantBuffersSetIndex,
                storageBufferSetIndex: (int)Constants.StorageBuffersSetIndex,
                textureSetIndex: (int)Constants.TexturesSetIndex,
                imageSetIndex: (int)Constants.ImagesSetIndex,
                extraSetBaseIndex: TotalSets,
                maximumExtraSets: (int)Constants.MaximumExtraSets,
                maximumUniformBuffersPerStage: Constants.MaxUniformBuffersPerStage,
                maximumStorageBuffersPerStage: Constants.MaxStorageBuffersPerStage,
                maximumTexturesPerStage: Constants.MaxTexturesPerStage,
                maximumImagesPerStage: Constants.MaxImagesPerStage,
                maximumComputeSharedMemorySize: (int)_device.MaxThreadgroupMemoryLength,
                maximumSupportedAnisotropy: 16,
                shaderSubgroupSize: 32,
                storageBufferOffsetAlignment: 16,
                textureBufferOffsetAlignment: 16,
                gatherBiasPrecision: 0,
                // Apple GPUs share system memory with the CPU. Capping the reported
                // budget keeps the texture cache at or below 4 GiB while still avoiding
                // the overly aggressive 512 MiB fallback used for an unknown budget.
                maximumGpuMemory: Math.Min(_device.RecommendedMaxWorkingSetSize, MaxReportedGpuMemory)
            );
        }

        public ulong GetCurrentSync()
        {
            return SyncManager.GetCurrent();
        }

        public HardwareInfo GetHardwareInfo()
        {
            return new HardwareInfo(HardwareInfoTools.GetVendor(), HardwareInfoTools.GetModel(), "Apple");
        }

        public IProgram LoadProgramBinary(byte[] programBinary, bool hasFragmentShader, ShaderInfo info)
        {
            ShaderSource[] shaders = MslProgramBinarySerializer.Unpack(programBinary);
            return new Program(this, _device, shaders, info.ResourceLayout, info.ComputeLocalSize, info.FragmentOutputMap, info.State, info.FromCache);
        }

        public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
        {
            BufferManager.SetData(buffer, offset, data, _pipeline.Cbs);
        }

        public void UpdateCounters()
        {
            Counters.Update();
        }

        public void PreFrame()
        {
            SyncManager.Cleanup();
        }

        public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
        {
            if (type == CounterType.SamplesPassed && Counters.SupportsSamplesPassed)
            {
                // Rotate first: the draws already encoded carry the offset they were issued
                // with, so they count into the reported counter either way. The new counter
                // then takes a slot of the same buffer and the encoder is pointed at it; only
                // when that is not possible does the pass still have to end.
                // The value the guest will read for this SamplesPassed report, stamped with
                // the presented frame, so it can be set beside the flare intensity it feeds.
                EventHandler<ulong> wrapped = resultHandler == null ? null : (s, v) => { UploadCorrelator.NoteCounterReport(v); resultHandler(s, v); };
                CounterEvent reported = Counters.Report(wrapped, divisor);

                if (!_pipeline.TrySwitchCounterInPass())
                {
                    _pipeline.EndCurrentPass(PassEndReason.Counter);
                }

                return reported;
            }

            CounterEvent counterEvent = new(null);
            if (type == CounterType.SamplesPassed) { UploadCorrelator.NoteCounterReport(1); }
            resultHandler?.Invoke(counterEvent, type == CounterType.SamplesPassed ? (ulong)1 : 0);
            return counterEvent;
        }

        public void ResetCounter(CounterType type)
        {
            if (type == CounterType.SamplesPassed && Counters.SupportsSamplesPassed)
            {
                Counters.Reset();

                if (!_pipeline.TrySwitchCounterInPass())
                {
                    _pipeline.EndCurrentPass(PassEndReason.Counter);
                }
            }
        }

        public void WaitSync(ulong id, HostSyncWaitSource source = HostSyncWaitSource.Unknown)
        {
            SyncManager.Wait(id, source);
        }

        public void FlushAllCommands()
        {
            _pipeline.FlushCommandsImpl();
        }

        internal EncoderType CurrentEncoderType => _pipeline.CurrentEncoderType;

        public void RegisterFlush()
        {
            SyncManager.RegisterFlush();

            // Periodically free unused regions of the staging buffer to avoid doing it all at once.
            BufferManager.StagingBuffer.FreeCompleted();
        }

        public void SetInterruptAction(Action<Action> interruptAction)
        {
            InterruptAction = interruptAction;
        }

        public void Screenshot()
        {
            _window.ScreenCaptureRequested = true;
        }

        public void OnScreenCaptured(ScreenCaptureImageInfo bitmap)
        {
            ScreenCaptured?.Invoke(this, bitmap);
        }

        public void Dispose()
        {
            // ThreadedRenderer has stopped its loop by the time it disposes us, and this
            // runs on the caller (GUI.WindowThread). Take the pool over so the shutdown
            // flushes run here directly instead of being marshalled onto a loop that no
            // longer services interrupts, and so the off-thread census stays quiet for a
            // path that is single-threaded by contract.
            CommandBufferPool.AdoptCurrentThread();
            InterruptAction = null;

            BackgroundResources.Dispose();

            // Stop the pre-warm workers first: a program waits for a build in flight on it.
            PipelinePrewarmer?.Dispose();

            foreach (Program program in Programs)
            {
                program.Dispose();
            }

            foreach (SamplerHolder sampler in Samplers)
            {
                sampler.Dispose();
            }

            Counters.Dispose();
            _pipeline.Dispose();
            _window.Dispose();
        }
    }
}
