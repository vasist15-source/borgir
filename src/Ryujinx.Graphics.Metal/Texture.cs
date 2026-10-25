using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using System;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    class Texture : TextureBase, ITexture
    {
        private Auto<DisposableTexture> _identitySwizzleHandle;
        private readonly bool _identityIsDifferent;

        // FrameProbe identifies writers by native pointer. A view gets its own
        // MTLTexture pointer, so without this map a write through a view looks like a
        // write to a texture nobody ever presents - which is how "nobody writes the
        // presented image" got reported once and had to be retracted.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, IntPtr> _viewRoots = new();

        public IntPtr ViewRootPtr { get; private set; }

        /// <summary>
        /// One identity per underlying resource, set unconditionally at construction: the
        /// storage this texture was created from, or its own storage when it is not a
        /// view. Ownership comparisons must use this. Comparing MTLTexture handles does
        /// not work - a view, the identity view and the swizzled view of one resource all
        /// carry different pointers, so a write through any of them looks like a write to
        /// a texture nobody touches. That failure produced three separate "nothing writes
        /// this" conclusions in the white-flash investigation, each of them wrong.
        /// </summary>
        public IntPtr CanonicalPtr { get; private set; }

        /// <summary>The root texture's usage census record; null unless UsageProbe is enabled.</summary>
        internal UsageProbe.Root UsageRoot;

        public static IntPtr ResolveViewRoot(IntPtr native)
        {
            IntPtr current = native;

            for (int depth = 0; depth < 8; depth++)
            {
                if (!_viewRoots.TryGetValue(current, out IntPtr parent) || parent == current)
                {
                    break;
                }

                current = parent;
            }

            return current;
        }

        private static readonly int _noGpuOpt =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_NO_GPU_OPT"), out int v) ? v : 0;

        public Texture(MTLDevice device, MetalRenderer renderer, Pipeline pipeline, TextureCreateInfo info) : base(device, renderer, pipeline, info)
        {
            MTLPixelFormat pixelFormat = HostFormat(Info.Format);

            MTLTextureDescriptor descriptor = new()
            {
                PixelFormat = pixelFormat,
                Usage = GetTextureUsage(Info, pixelFormat),
                SampleCount = (ulong)Info.Samples,
                TextureType = Info.Target.Convert(),
                Width = (ulong)Info.Width,
                Height = (ulong)Info.Height,
                MipmapLevelCount = (ulong)Info.Levels,

                // Private, matching what MoltenVK allocates for VkImages. Left unset this
                // defaulted to Shared on Apple silicon, routing every texture through the
                // driver's CPU-coherent tile paths - the region where the load fault
                // lives. MoltenVK on the same driver shows the artefact at 1/175th the
                // rate, and device-local storage is its sharpest usage difference. All
                // texture data movement here already goes through blit encoders.
                // RYUJINX_METAL_SHARED_TEXTURES=1 restores the old default.
                StorageMode = _sharedTextures ? MTLStorageMode.Shared : MTLStorageMode.Private,
            };

            // Lossless framebuffer compression is the one thing every previous arm left on.
            // "GPU-optimized contents" is Apple's name for the compressed layout with its
            // side-band metadata; the presentation half goes picture -> white between the
            // composite's store and present's read with no API-layer command in between, and
            // a texture whose *metadata* is misread would show exactly that under both the
            // sampler and the blit engine (both decode through it). MoltenVK leaves this on
            // too, so a null here does not indict it - but a hit isolates the mechanism.
            // RYUJINX_METAL_NO_GPU_OPT=1: every non-depth texture; =2: only 1080p RGBA8 sRGB.
            if (_noGpuOpt == 1 && !info.Format.IsDepthOrStencil)
            {
                descriptor.AllowGPUOptimizedContents = false;
            }
            else if (_noGpuOpt == 2 && pixelFormat == MTLPixelFormat.RGBA8UnormsRGB && info.Width == 1920 && info.Height == 1080)
            {
                descriptor.AllowGPUOptimizedContents = false;
                Ryujinx.Common.Logging.Logger.Warning?.PrintMsg(Ryujinx.Common.Logging.LogClass.Gpu, "no-gpu-opt: 1080p sRGB target created uncompressed");
            }

            if (info.Target == Target.Texture3D)
            {
                descriptor.Depth = (ulong)Info.Depth;
            }
            else if (info.Target != Target.Cubemap)
            {
                if (info.Target == Target.CubemapArray)
                {
                    descriptor.ArrayLength = (ulong)(Info.Depth / 6);
                }
                else
                {
                    descriptor.ArrayLength = (ulong)Info.Depth;
                }
            }

            MTLTextureSwizzleChannels swizzle = GetSwizzle(info, descriptor.PixelFormat);

            // The scene class can be moved into a tracked placement heap, so that the whole
            // class is one hazard-tracked resource instead of one per texture. That is the
            // only difference the experiment introduces: same descriptor, same storage
            // mode, same usage. TryAllocate declines rather than throwing whenever the
            // descriptor cannot legally live in the heap, so this always ends up with a
            // texture. RYUJINX_METAL_TEXTURE_HEAP=1, off by default.
            DisposableTexture storage;

            // IsSceneClass demands Width >= 1000 and the repro runs at 800x448 under
            // dynamic resolution, so gating the heap on it placed none of the textures that
            // matter - the first arm came back inside the baseline having never engaged.
            // Level 2 places every private colour texture instead.
            bool heapCandidate = TextureHeapAllocator.Level >= 2
                ? !info.Format.IsDepthOrStencil && info.Width >= 64 && info.Height >= 64
                : IsSceneClass(info);

            if (TextureHeapAllocator.Enabled && heapCandidate &&
                TextureHeapAllocator.TryAllocate(Device, descriptor, out MTLTexture heapTexture, out TextureHeapAllocation heapAllocation))
            {
                storage = new DisposableTexture(heapTexture, heapAllocation);
            }
            else
            {
                storage = new DisposableTexture(Device.NewTexture(descriptor));
            }

            _identitySwizzleHandle = new Auto<DisposableTexture>(storage);

            if (LiveResources.Enabled)
            {
                LiveResources.NoteTexture(LiveKind.Texture, storage.Value);
            }

            // A freshly created texture holds undefined content, and the elision
            // experiment proved the flash IS undefined content crystallised by the first
            // Load/Store pass. This logs every creation of the scene-shaped class with the
            // probe's frame number, so creations can be correlated against flash frames
            // directly: damage at creation frame N should display at N+1.
            if (PresentProbe.Enabled && Info.Width >= 1500 && Info.Width <= 1700 &&
                pixelFormat == MTLPixelFormat.RG11B10Float)
            {
                Ryujinx.Common.Logging.Logger.Warning?.PrintMsg(Ryujinx.Common.Logging.LogClass.Gpu,
                    $"texcreate f={PresentProbe.Frame} {Info.Width}x{Info.Height} RG11B10Float " +
                    $"handle=0x{GetHandle().NativePtr:X}");
            }



            if (SwizzleIsIdentity(swizzle))
            {
                MtlTextureAuto = _identitySwizzleHandle;
            }
            else
            {
                MTLTexture identityTexture = _identitySwizzleHandle.GetUnsafe().Value;
                SetHandle(CreateDefaultView(identityTexture, swizzle, descriptor), _identitySwizzleHandle);
                _identityIsDifferent = true;
            }

            MtlFormat = pixelFormat;

            // Not a view: its own storage is the canonical identity.
            CanonicalPtr = _identitySwizzleHandle.GetUnsafe().Value.NativePtr;

            if (UsageProbe.Enabled)
            {
                UsageRoot = UsageProbe.NoteRoot(info, pixelFormat);
            }

            if (_leanTargets && IsLeanTargetClass(info))
            {
                LeanUsage = true;
                Interlocked.Increment(ref _leanCreated);
            }

            StainOnCreate();

            descriptor.Dispose();
        }

        /// <summary>
        /// Fills a newly created scene-sized RG11B10Float texture with a constant, so that
        /// whether anything ever writes it can be answered positively instead of by
        /// enumerating hooks.
        ///
        /// The composite samples one of these and reads the scene out of it on ordinary
        /// frames, yet CopyTo, the render blit and SetData all report never touching it.
        /// A negative assembled from hooks is not proof - this file records the same
        /// conclusion being overturned once already, when four hook types with positive
        /// controls agreed nothing wrote a texture and staining it green showed otherwise.
        ///
        /// 0x55 repeated packs to a constant that is neither the scene nor white, and a
        /// byte fill is enough to place it. Read the screen after:
        ///
        ///   ordinary frames still show the scene -> a writer exists and is unhooked
        ///   flat frames show the stain           -> a flat frame reads this texture before
        ///                                           anything has written it
        ///
        /// RYUJINX_METAL_STAIN_SCENE=1, off by default - it destroys the image of any
        /// texture it touches until something overwrites it.
        /// </summary>
        private static readonly bool _stainScene =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_STAIN_SCENE") == "1";

        private void StainOnCreate()
        {
            if (!_stainScene || Info.Width != 1600 || MtlFormat != MTLPixelFormat.RG11B10Float)
            {
                return;
            }

            int bytes = Info.Width * Info.Height * 4;
            MemoryOwner<byte> stain = MemoryOwner<byte>.Rent(bytes);
            stain.Span.Fill(0x55);

            Logger.Warning?.PrintMsg(LogClass.Gpu,
                $"stain: filling new {Info.Width}x{Info.Height} {MtlFormat} 0x{CanonicalPtr:X} with 0x55");

            SetData(stain);
        }

        /// <summary>
        /// Textures were previously created with MTLTextureUsage.Unknown, which the
        /// validation layer rejects outright ("usage must be set", thousands of hits
        /// per frame) and which leaves rendering into them, writing them as images
        /// and reinterpreting them through views formally undefined - a systemic
        /// source of intermittent empty/stale content. Declare every usage the
        /// emulator can exercise; guest textures are freely reused as render
        /// targets, storage images and reinterpreted views.
        /// </summary>
        /// <summary>
        /// The class of texture the composite reads and the flash appears in: the guest's
        /// full resolution HDR scene target. Keyed on the guest format, not MtlFormat, so
        /// it survives the RGBA16Float substitution below.
        /// </summary>
        /// <summary>
        /// The single place a guest format becomes a host one. Both the base texture and
        /// every view have to agree: deriving them separately meant a substituted base
        /// was handed views still asking for the packed format, and Metal rejects that
        /// pair at creation ("source texture pixelFormat not compatible with texture view
        /// pixelFormat").
        /// </summary>
        internal static MTLPixelFormat HostFormat(Format format)
        {
            if (_sceneRgba16f && format == Format.R11G11B10Float)
            {
                return MTLPixelFormat.RGBA16Float;
            }

            return FormatTable.GetFormat(format);
        }

        public static bool IsSceneClass(TextureCreateInfo info) =>
            info.Format == Format.R11G11B10Float && info.Width >= 1000;

        /// <summary>
        /// Card 2: run the scene class as RGBA16Float instead of the packed RG11B10Float.
        /// The white is manufactured on the read - every host write channel is excluded at
        /// power - and a packed 32-bit three-channel format is decoded on the read path by
        /// hardware an unpacked half4 never touches. Substitution is consistent across the
        /// whole format, so blits between two of these still see matching layouts.
        /// RYUJINX_METAL_SCENE_RGBA16F=1.
        /// </summary>
        private static readonly bool _sceneRgba16f =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_SCENE_RGBA16F") == "1";

        /// <summary>
        /// Card 4: stop asking for usages the scene class never exercises. Every colour
        /// texture here declares PixelFormatView and ShaderWrite unconditionally, and on
        /// Apple GPUs either flag alone disables lossless compression - which is why the
        /// flash lives on a long-lived *uncompressed* colour target. MoltenVK sets
        /// ShaderWrite only when the VkImage actually carries STORAGE_BIT, so Vulkan runs
        /// this same surface compressed and flashes at 1/175th the rate. Scoped to the
        /// scene class alone to keep the blast radius off genuine storage images.
        /// 1 drops ShaderWrite, 2 drops ShaderWrite and PixelFormatView.
        /// </summary>
        private static readonly int _leanUsage =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_LEAN_USAGE"), out int leanUsage)
                ? leanUsage
                : 0;

        /// <summary>
        /// Lossless-compression experiment. Apple GPUs compress a texture's tile stores and
        /// loads only when it declares neither ShaderWrite nor PixelFormatView, and every
        /// colour texture here declares both, so each split render pass moves uncompressed
        /// data. A 25,200-frame census (UsageProbe) found no storage-image binds at all and
        /// no view in a format other than its own, a swizzle or its sRGB twin on the three
        /// full-resolution colour classes - about 65% of attachment traffic by bytes. Those
        /// alone are created with ShaderRead | RenderTarget. A later use that would need a
        /// dropped flag is logged as a violation (see NoteLeanViolation), not repaired:
        /// rebuilding the storage would leave existing views on the old texture.
        /// Measured 2026-09-30, four alternating launches at one standing spot: GPU time per
        /// frame 23.40 -> 22.24 ms (-5.0%), frame rate +2.7%, no violations, no visible change.
        /// On by default; RYUJINX_METAL_LEAN_TARGETS=0 restores the full usage flags.
        /// </summary>
        private static readonly bool _leanTargets =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_LEAN_TARGETS") != "0";

        public static bool LeanTargetsEnabled => _leanTargets;

        private static long _leanCreated;
        private static long _leanViolations;

        public static long LeanCreated => Interlocked.Read(ref _leanCreated);

        public static long LeanViolations => Interlocked.Read(ref _leanViolations);

        /// <summary>True for a texture created without ShaderWrite and PixelFormatView; views inherit it.</summary>
        internal bool LeanUsage;

        private static bool IsLeanTargetClass(TextureCreateInfo info) =>
            info.Target == Target.Texture2D &&
            info.Samples == 1 &&
            info.Width >= 1600 && info.Height >= 900 &&
            info.Format is Format.R11G11B10Float or Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb;

        /// <summary>
        /// A lean texture was used in a way that needs a flag it was created without. The
        /// use goes ahead (the experiment measures, it does not repair), so any hit means
        /// that class must leave the lean set before this can be a default.
        /// </summary>
        internal static void NoteLeanViolation(string what)
        {
            long n = Interlocked.Increment(ref _leanViolations);

            if (n <= 20)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"lean-targets violation #{n}: {what}");
            }
        }

        private static MTLTextureUsage GetTextureUsage(TextureCreateInfo info, MTLPixelFormat pixelFormat)
        {
            if (_leanTargets && IsLeanTargetClass(info))
            {
                return MTLTextureUsage.ShaderRead | MTLTextureUsage.RenderTarget;
            }

            MTLTextureUsage usage = MTLTextureUsage.ShaderRead | MTLTextureUsage.PixelFormatView;

            if (_leanUsage > 0 && IsSceneClass(info))
            {
                usage |= MTLTextureUsage.RenderTarget;

                if (_leanUsage >= 2)
                {
                    usage &= ~MTLTextureUsage.PixelFormatView;
                }

                return usage;
            }

            if (info.Format.IsDepthOrStencil)
            {
                // Depth/stencil formats are renderable but not shader-writable. They are
                // also not reinterpretable: Metal only permits view formats within a
                // depth format's own family (the X32/X24 stencil views are created from
                // the descriptor's own format, not through PixelFormatView), so the
                // blanket PixelFormatView above is removed here. It is at minimum
                // undefined for depth on this driver, it disables optimisations, and the
                // depth attachment is loaded beside the colour in the very pass whose
                // load returns white - the one input to that load nothing has varied.
                // RYUJINX_METAL_DEPTH_PFV=1 restores the old flags.
                usage &= ~MTLTextureUsage.PixelFormatView;

                if (_depthPixelFormatView)
                {
                    usage |= MTLTextureUsage.PixelFormatView;
                }

                usage |= MTLTextureUsage.RenderTarget;
            }
            else if (!info.IsCompressed)
            {
                // Compressed formats can be neither rendered to nor written from
                // shaders on Metal; other color formats may be used both ways.
                usage |= MTLTextureUsage.RenderTarget | MTLTextureUsage.ShaderWrite;
            }

            return usage;
        }

        /// <summary>
        /// Monotonic creation serial, never recycled. Every identity measurement so far
        /// compared address values, and address recycling makes different objects across
        /// time look identical - this is the identity that cannot alias.
        /// </summary>
        public long Serial { get; } = System.Threading.Interlocked.Increment(ref _nextSerial);

        private static long _nextSerial;

        private static readonly bool _sharedTextures =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_SHARED_TEXTURES") == "1";

        private static readonly bool _depthPixelFormatView =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_DEPTH_PFV") == "1";

        public Texture(MTLDevice device, MetalRenderer renderer, Pipeline pipeline, TextureCreateInfo info, MTLTexture sourceTexture, int firstLayer, int firstLevel) :
            this(device, renderer, pipeline, info, sourceTexture, null, firstLayer, firstLevel)
        {
        }

        private Texture(
            MTLDevice device,
            MetalRenderer renderer,
            Pipeline pipeline,
            TextureCreateInfo info,
            Auto<DisposableTexture> sourceTexture,
            int firstLayer,
            int firstLevel) :
            this(device, renderer, pipeline, info, sourceTexture.GetUnsafe().Value, sourceTexture, firstLayer, firstLevel)
        {
        }

        private Texture(
            MTLDevice device,
            MetalRenderer renderer,
            Pipeline pipeline,
            TextureCreateInfo info,
            MTLTexture sourceTexture,
            Auto<DisposableTexture> sourceTextureAuto,
            int firstLayer,
            int firstLevel) : base(device, renderer, pipeline, info)
        {
            MTLPixelFormat pixelFormat = HostFormat(Info.Format);

            if (info.DepthStencilMode == DepthStencilMode.Stencil)
            {
                pixelFormat = pixelFormat switch
                {
                    MTLPixelFormat.Depth32FloatStencil8 => MTLPixelFormat.X32Stencil8,
                    MTLPixelFormat.Depth24UnormStencil8 => MTLPixelFormat.X24Stencil8,
                    _ => pixelFormat
                };
            }

            MTLTextureType textureType = Info.Target.Convert();

            // A 2D view of one slice of a 3D texture (NVN renders volumes slice by slice through
            // such views): Metal cannot view a 3D texture as 2D, so the view stays 3D over the one
            // level and the slice becomes the render pass attachment's depth plane.
            if (sourceTexture.TextureType == MTLTextureType.Type3D && textureType != MTLTextureType.Type3D)
            {
                textureType = MTLTextureType.Type3D;
                DepthPlane = firstLayer;
                firstLayer = 0;
            }

            NSRange levels;
            levels.location = (ulong)firstLevel;
            levels.length = (ulong)Info.Levels;
            NSRange slices;
            slices.location = (ulong)firstLayer;
            slices.length = textureType == MTLTextureType.Type3D ? 1 : (ulong)info.GetDepthOrLayers();

            MTLTextureSwizzleChannels swizzle = GetSwizzle(info, pixelFormat);

            MTLTexture identityTexture = sourceTexture.NewTextureView(pixelFormat, textureType, levels, slices);

            if (LiveResources.Enabled)
            {
                LiveResources.NoteTexture(LiveKind.TextureView, identityTexture);
            }

            _identitySwizzleHandle = sourceTextureAuto != null
                ? new Auto<DisposableTexture>(new DisposableTexture(identityTexture), null, sourceTextureAuto)
                : new Auto<DisposableTexture>(new DisposableTexture(identityTexture));

            if (SwizzleIsIdentity(swizzle))
            {
                MtlTextureAuto = _identitySwizzleHandle;
            }
            else
            {
                MTLTexture swizzledTexture = sourceTexture.NewTextureView(pixelFormat, textureType, levels, slices, swizzle);

                if (sourceTextureAuto != null)
                {
                    SetHandle(swizzledTexture, sourceTextureAuto);
                }
                else
                {
                    SetHandle(swizzledTexture);
                }

                _identityIsDifferent = true;
            }

            MtlFormat = pixelFormat;
            FirstLayer = firstLayer;
            FirstLevel = firstLevel;
            CanonicalPtr = sourceTexture.NativePtr;

            // Instrument-only bookkeeping: keep it off the normal path entirely so the
            // probe cannot change the behaviour it is measuring. HdrPassProbe needs the
            // same map: comparing raw handles instead of view roots is what made writes
            // through a view look like writes to a texture nobody touches.
            if (FrameProbe.Enabled || HdrPassProbe.Enabled)
            {
                ViewRootPtr = ResolveViewRoot(sourceTexture.NativePtr);

                if (ViewRootPtr != IntPtr.Zero)
                {
                    if (identityTexture.NativePtr != IntPtr.Zero)
                    {
                        _viewRoots[identityTexture.NativePtr] = ViewRootPtr;
                    }

                    MTLTexture handle = GetIdentityHandle();

                    if (handle.NativePtr != IntPtr.Zero)
                    {
                        _viewRoots[handle.NativePtr] = ViewRootPtr;
                    }
                }
            }
        }

        /// <summary>The slice a 2D view of a 3D texture renders to (see the view constructor); 0 otherwise.</summary>
        public int DepthPlane { get; }

        public void PopulateRenderPassAttachment(MTLRenderPassColorAttachmentDescriptor descriptor, CommandBufferScoped cbs)
        {
            descriptor.Texture = GetIdentityHandle(cbs);
            descriptor.DepthPlane = (ulong)DepthPlane;
        }

        public override MTLTexture GetIdentityHandle()
        {
            return Valid ? _identitySwizzleHandle.GetUnsafe().Value : new MTLTexture(IntPtr.Zero);
        }

        public override MTLTexture GetIdentityHandle(CommandBufferScoped cbs)
        {
            return Valid ? _identitySwizzleHandle.Get(cbs).Value : new MTLTexture(IntPtr.Zero);
        }

        private MTLTexture CreateDefaultView(MTLTexture texture, MTLTextureSwizzleChannels swizzle, MTLTextureDescriptor descriptor)
        {
            NSRange levels;
            levels.location = 0;
            levels.length = (ulong)Info.Levels;
            NSRange slices;
            slices.location = 0;
            slices.length = Info.Target == Target.Texture3D ? 1 : (ulong)Info.GetDepthOrLayers();

            return texture.NewTextureView(descriptor.PixelFormat, descriptor.TextureType, levels, slices, swizzle);
        }

        private bool SwizzleIsIdentity(MTLTextureSwizzleChannels swizzle)
        {
            return swizzle.red == MTLTextureSwizzle.Red &&
                   swizzle.green == MTLTextureSwizzle.Green &&
                   swizzle.blue == MTLTextureSwizzle.Blue &&
                   swizzle.alpha == MTLTextureSwizzle.Alpha;
        }

        private MTLTextureSwizzleChannels GetSwizzle(TextureCreateInfo info, MTLPixelFormat pixelFormat)
        {
            MTLTextureSwizzle swizzleR = Info.SwizzleR.Convert();
            MTLTextureSwizzle swizzleG = Info.SwizzleG.Convert();
            MTLTextureSwizzle swizzleB = Info.SwizzleB.Convert();
            MTLTextureSwizzle swizzleA = Info.SwizzleA.Convert();

            if (info.Format == Format.R5G5B5A1Unorm ||
                info.Format == Format.R5G5B5X1Unorm ||
                info.Format == Format.R5G6B5Unorm)
            {
                (swizzleB, swizzleR) = (swizzleR, swizzleB);
            }
            else if (pixelFormat == MTLPixelFormat.ABGR4Unorm || info.Format == Format.A1B5G5R5Unorm)
            {
                MTLTextureSwizzle tempB = swizzleB;
                MTLTextureSwizzle tempA = swizzleA;

                swizzleB = swizzleG;
                swizzleA = swizzleR;
                swizzleR = tempA;
                swizzleG = tempB;
            }

            return new MTLTextureSwizzleChannels
            {
                red = swizzleR,
                green = swizzleG,
                blue = swizzleB,
                alpha = swizzleA
            };
        }

        private static readonly System.Collections.Generic.HashSet<(string, Format, Format)> _loggedCopyGaps = [];

        // These texture-to-texture copy variants are not implemented yet. They were
        // silently dropped before, leaving destination textures with stale or empty
        // content (missing baked foliage pages, frozen gloom in TOTK). Log each
        // distinct case once so real-game requirements are visible in session logs.
        private static void LogUnimplementedCopy(string kind, TextureBase src, TextureBase dst)
        {
            lock (_loggedCopyGaps)
            {
                if (_loggedCopyGaps.Add((kind, src.Info.Format, dst.Info.Format)))
                {
                    Logger.Warning?.PrintMsg(
                        LogClass.Gpu,
                        $"Unimplemented texture copy ({kind}): {src.Info.Format} {src.Info.Target} -> {dst.Info.Format} {dst.Info.Target}");
                }
            }
        }

        public void CopyTo(ITexture destination, int firstLayer, int firstLevel)
        {
            StoreLiveness.NoteRead(CanonicalPtr);

            if (firstLayer == 0 && firstLevel == 0 && destination is Texture whole &&
                whole.Info.Width == Info.Width && whole.Info.Height == Info.Height)
            {
                StoreLiveness.NoteOverwrite(whole.CanonicalPtr);
            }

            OpRing.NoteTexCopy(GetHandle().NativePtr, (destination as Texture)?.GetHandle().NativePtr ?? IntPtr.Zero);
            HdrPassProbe.NoteSceneCopy(this, destination as Texture);
            UploadCorrelator.NoteCopyIn(destination as Texture);
            // The flat white 1920x1080 surface is not written by any render pass - it
            // never appears in the per-target pass census - so it has to arrive by copy.
            // This names the copies that land on a full resolution destination.
            if (HdrPassProbe.Enabled && destination is Texture dstTex && dstTex.Width >= 1900)
            {
                HdrPassProbe.NoteCopy(Width, Height, MtlFormat.ToString(),
                    dstTex.Width, dstTex.Height, dstTex.MtlFormat.ToString());
            }

            HdrPassProbe.NoteNonRenderWrite(destination as Texture, "copy");
            HdrPassProbe.NoteCopyInto(destination as Texture);

            CommandBufferScoped cbs = Pipeline.Cbs;

            TextureBase src = this;
            TextureBase dst = (TextureBase)destination;

            if (!Valid || !dst.Valid)
            {
                return;
            }

            MTLTexture srcImage = GetIdentityHandle(cbs);
            MTLTexture dstImage = dst.GetIdentityHandle(cbs);

            if (!dst.Info.Target.IsMultisample && Info.Target.IsMultisample)
            {
                LogUnimplementedCopy("ms-to-nonms", src, dst);
            }
            else if (dst.Info.Target.IsMultisample && !Info.Target.IsMultisample)
            {
                LogUnimplementedCopy("nonms-to-ms", src, dst);
            }
            else if (dst.Info.BytesPerPixel != Info.BytesPerPixel)
            {
                LogUnimplementedCopy("incompatible-bpp", src, dst);
            }
            else if (src.Info.Format.IsDepthOrStencil != dst.Info.Format.IsDepthOrStencil)
            {
                LogUnimplementedCopy("depth-color", src, dst);
            }
            else
            {
                TextureCopy.Copy(
                    cbs,
                    srcImage,
                    dstImage,
                    src.Info,
                    dst.Info,
                    0,
                    firstLayer,
                    0,
                    firstLevel);
            }
        }

        public void CopyTo(ITexture destination, int srcLayer, int dstLayer, int srcLevel, int dstLevel)
        {
            StoreLiveness.NoteRead(CanonicalPtr);

            OpRing.NoteTexCopy(GetHandle().NativePtr, (destination as Texture)?.GetHandle().NativePtr ?? IntPtr.Zero);
            HdrPassProbe.NoteSceneCopy(this, destination as Texture);
            UploadCorrelator.NoteCopyIn(destination as Texture);
            // The flat white 1920x1080 surface is not written by any render pass - it
            // never appears in the per-target pass census - so it has to arrive by copy.
            // This names the copies that land on a full resolution destination.
            if (HdrPassProbe.Enabled && destination is Texture dstTex && dstTex.Width >= 1900)
            {
                HdrPassProbe.NoteCopy(Width, Height, MtlFormat.ToString(),
                    dstTex.Width, dstTex.Height, dstTex.MtlFormat.ToString());
            }

            HdrPassProbe.NoteNonRenderWrite(destination as Texture, "copyLayer");
            HdrPassProbe.NoteCopyInto(destination as Texture);

            CommandBufferScoped cbs = Pipeline.Cbs;

            TextureBase src = this;
            TextureBase dst = (TextureBase)destination;

            if (!Valid || !dst.Valid)
            {
                return;
            }

            MTLTexture srcImage = GetIdentityHandle(cbs);
            MTLTexture dstImage = dst.GetIdentityHandle(cbs);

            if (!dst.Info.Target.IsMultisample && Info.Target.IsMultisample)
            {
                LogUnimplementedCopy("ms-to-nonms", src, dst);
            }
            else if (dst.Info.Target.IsMultisample && !Info.Target.IsMultisample)
            {
                LogUnimplementedCopy("nonms-to-ms", src, dst);
            }
            else if (dst.Info.BytesPerPixel != Info.BytesPerPixel)
            {
                LogUnimplementedCopy("incompatible-bpp", src, dst);
            }
            else if (src.Info.Format.IsDepthOrStencil != dst.Info.Format.IsDepthOrStencil)
            {
                LogUnimplementedCopy("depth-color", src, dst);
            }
            else
            {
                TextureCopy.Copy(
                    cbs,
                    srcImage,
                    dstImage,
                    src.Info,
                    dst.Info,
                    srcLayer,
                    dstLayer,
                    srcLevel,
                    dstLevel,
                    1,
                    1);
            }
        }

        public void CopyTo(ITexture destination, Extents2D srcRegion, Extents2D dstRegion, bool linearFilter)
        {
            StoreLiveness.NoteRead(CanonicalPtr);

            if (destination is Texture blitDst && dstRegion.X1 == 0 && dstRegion.Y1 == 0 &&
                dstRegion.X2 == blitDst.Info.Width && dstRegion.Y2 == blitDst.Info.Height)
            {
                StoreLiveness.NoteOverwrite(blitDst.CanonicalPtr);
            }

            OpRing.NoteTexCopy(GetHandle().NativePtr, (destination as Texture)?.GetHandle().NativePtr ?? IntPtr.Zero);
            HdrPassProbe.NoteSceneCopy(this, destination as Texture);
            UploadCorrelator.NoteCopyIn(destination as Texture);
            // The flat white 1920x1080 surface is not written by any render pass - it
            // never appears in the per-target pass census - so it has to arrive by copy.
            // This names the copies that land on a full resolution destination.
            if (HdrPassProbe.Enabled && destination is Texture dstTex && dstTex.Width >= 1900)
            {
                HdrPassProbe.NoteCopy(Width, Height, MtlFormat.ToString(),
                    dstTex.Width, dstTex.Height, dstTex.MtlFormat.ToString());
            }

            HdrPassProbe.NoteNonRenderWrite(destination as Texture, "blitScaled");
            HdrPassProbe.NoteCopyInto(destination as Texture);

            if (!Renderer.CommandBufferPool.OwnedByCurrentThread)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, "Metal doesn't currently support scaled blit on background thread.");

                return;
            }

            Texture dst = (Texture)destination;

            bool isDepthOrStencil = dst.Info.Format.IsDepthOrStencil;

            Pipeline.Blit(this, dst, srcRegion, dstRegion, isDepthOrStencil, linearFilter);
        }

        public void CopyTo(BufferRange range, int layer, int level, int stride)
        {
            StoreLiveness.NoteRead(CanonicalPtr);

            CommandBufferScoped cbs = Pipeline.Cbs;

            int outSize = Info.GetMipSize(level);
            int hostSize = GetBufferDataLength(outSize);

            int offset = range.Offset;

            Auto<DisposableBuffer> autoBuffer = Renderer.BufferManager.GetBuffer(range.Handle, true);
            MTLBuffer mtlBuffer = autoBuffer.Get(cbs, range.Offset, outSize).Value;
            UploadCorrelator.NoteWriteTo(mtlBuffer.NativePtr, range.Offset, outSize, $"T2B:{Info.Width}x{Info.Height}/{MtlFormat}L{level}");

            if (PrepareOutputBuffer(cbs, hostSize, mtlBuffer, out MTLBuffer copyToBuffer, out BufferHolder tempCopyHolder))
            {
                offset = 0;
            }

            CopyFromOrToBuffer(cbs, copyToBuffer, GetIdentityHandle(cbs), hostSize, true, layer, level, 1, 1, singleSlice: true, offset, stride);

            if (tempCopyHolder != null)
            {
                CopyDataToOutputBuffer(cbs, tempCopyHolder, autoBuffer, hostSize, range.Offset);
                tempCopyHolder.Dispose();
            }
        }

        public ITexture CreateView(TextureCreateInfo info, int firstLayer, int firstLevel)
        {
            Texture view = new(Device, Renderer, Pipeline, info, _identitySwizzleHandle, firstLayer, firstLevel);

            if (LeanUsage)
            {
                view.LeanUsage = true;

                if (view.MtlFormat != MtlFormat && !UsageProbe.IsSrgbTwin(MtlFormat, view.MtlFormat))
                {
                    NoteLeanViolation($"{view.MtlFormat} view of {Info.Width}x{Info.Height} {MtlFormat}");
                }
            }

            if (UsageProbe.Enabled)
            {
                view.UsageRoot = UsageRoot;
                UsageProbe.NoteView(UsageRoot, view.MtlFormat, view._identityIsDifferent);
            }

            return view;
        }

        private void CopyDataToBuffer(Span<byte> storage, ReadOnlySpan<byte> input)
        {
            if (NeedsD24S8Conversion())
            {
                FormatConverter.ConvertD24S8ToD32FS8(storage, input);
                return;
            }

            input.CopyTo(storage);
        }

        private ReadOnlySpan<byte> GetDataFromBuffer(ReadOnlySpan<byte> storage, int size, Span<byte> output)
        {
            if (NeedsD24S8Conversion())
            {
                if (output.IsEmpty)
                {
                    output = new byte[GetBufferDataLength(size)];
                }

                FormatConverter.ConvertD32FS8ToD24S8(output, storage);
                return output;
            }

            return storage;
        }

        private bool PrepareOutputBuffer(CommandBufferScoped cbs, int hostSize, MTLBuffer target, out MTLBuffer copyTarget, out BufferHolder copyTargetHolder)
        {
            if (NeedsD24S8Conversion())
            {
                copyTargetHolder = Renderer.BufferManager.Create(hostSize);
                copyTarget = copyTargetHolder.GetBuffer().Get(cbs, 0, hostSize).Value;

                return true;
            }

            copyTarget = target;
            copyTargetHolder = null;

            return false;
        }

        private void CopyDataToOutputBuffer(CommandBufferScoped cbs, BufferHolder hostData, Auto<DisposableBuffer> copyTarget, int hostSize, int dstOffset)
        {
            if (NeedsD24S8Conversion())
            {
                Renderer.HelperShader.ConvertD32S8ToD24S8(cbs, hostData, copyTarget, hostSize / (2 * sizeof(int)), dstOffset);
            }
        }

        private bool NeedsD24S8Conversion()
        {
            return FormatTable.IsD24S8(Info.Format) && MtlFormat == MTLPixelFormat.Depth32FloatStencil8;
        }

        public void CopyFromOrToBuffer(
            CommandBufferScoped cbs,
            MTLBuffer buffer,
            MTLTexture image,
            int size,
            bool to,
            int dstLayer,
            int dstLevel,
            int dstLayers,
            int dstLevels,
            bool singleSlice,
            int offset = 0,
            int stride = 0)
        {
            MTLBlitCommandEncoder blitCommandEncoder = cbs.Encoders.EnsureBlitEncoder();

            bool is3D = Info.Target == Target.Texture3D;
            int width = Math.Max(1, Info.Width >> dstLevel);
            int height = Math.Max(1, Info.Height >> dstLevel);
            int depth = is3D && !singleSlice ? Math.Max(1, Info.Depth >> dstLevel) : 1;
            int layers = dstLayers;
            int levels = dstLevels;

            for (int oLevel = 0; oLevel < levels; oLevel++)
            {
                int level = oLevel + dstLevel;
                int mipSize = Info.GetMipSize2D(level);

                int mipSizeLevel = GetBufferDataLength(is3D && !singleSlice
                    ? Info.GetMipSize(level)
                    : mipSize * dstLayers);

                int endOffset = offset + mipSizeLevel;

                if ((uint)endOffset > (uint)size)
                {
                    break;
                }

                for (int oLayer = 0; oLayer < layers; oLayer++)
                {
                    int layer = !is3D ? dstLayer + oLayer : 0;
                    int z = is3D ? dstLayer + oLayer : 0;

                    if (to)
                    {
                        blitCommandEncoder.CopyFromTexture(
                            image,
                            (ulong)layer,
                            (ulong)level,
                            new MTLOrigin { z = (ulong)z },
                            new MTLSize { width = (ulong)width, height = (ulong)height, depth = 1 },
                            buffer,
                            (ulong)offset,
                            (ulong)Info.GetMipStride(level),
                            (ulong)mipSize
                        );
                    }
                    else
                    {
                        blitCommandEncoder.CopyFromBuffer(
                            buffer,
                            (ulong)offset,
                            (ulong)Info.GetMipStride(level),
                            (ulong)mipSize,
                            new MTLSize { width = (ulong)width, height = (ulong)height, depth = 1 },
                            image,
                            (ulong)(layer + oLayer),
                            (ulong)level,
                            new MTLOrigin { z = (ulong)z }
                        );
                    }

                    offset += mipSize;
                }

                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);

                if (Info.Target == Target.Texture3D)
                {
                    depth = Math.Max(1, depth >> 1);
                }
            }
        }

        private ReadOnlySpan<byte> GetData(CommandBufferPool cbp, PersistentFlushBuffer flushBuffer)
        {
            int size = 0;

            for (int level = 0; level < Info.Levels; level++)
            {
                size += Info.GetMipSize(level);
            }

            size = GetBufferDataLength(size);

            Span<byte> result = flushBuffer.GetTextureData(cbp, this, size);

            return GetDataFromBuffer(result, size, result);
        }

        private ReadOnlySpan<byte> GetData(CommandBufferPool cbp, PersistentFlushBuffer flushBuffer, int layer, int level)
        {
            int size = GetBufferDataLength(Info.GetMipSize(level));

            Span<byte> result = flushBuffer.GetTextureData(cbp, this, size, layer, level);

            return GetDataFromBuffer(result, size, result);
        }

        private static readonly bool _logTexReadback =
            System.Environment.GetEnvironmentVariable("RYUJINX_METAL_LOG_READBACK") == "1";

        /// <summary>
        /// The render-thread path flushes the main queue first, so its copy is ordered
        /// against everything already recorded. The background path does neither: it
        /// blits on a different queue with no MTLEvent or MTLFence between them, so it
        /// reads whatever the texture happens to hold while the main queue is still
        /// rendering into it. This says which path a readback actually took, and of
        /// what, because the whole cross-queue race hypothesis is dead unless the
        /// background path fires on frame-sized colour textures during play.
        /// </summary>
        private void LogReadback(bool background)
        {
            Ryujinx.Common.Logging.Logger.Warning?.PrintMsg(
                Ryujinx.Common.Logging.LogClass.Gpu,
                $"readback {(background ? "BACKGROUND" : "render")} tex target={Info.Target} fmt={Info.Format} " +
                $"{Info.Width}x{Info.Height}x{Info.Depth} levels={Info.Levels}");
        }

        public PinnedSpan<byte> GetData()
        {
            StoreLiveness.NoteRead(CanonicalPtr);

            EncoderStateManager.NoteReadback(CanonicalPtr, $"{Info.Width}x{Info.Height}/{MtlFormat} full");

            BackgroundResource resources = Renderer.BackgroundResources.Get();

            if (Renderer.CommandBufferPool.OwnedByCurrentThread)
            {
                if (_logTexReadback)
                {
                    LogReadback(false);
                }

                Renderer.FlushAllCommands();

                ReadOnlySpan<byte> fg = GetData(Renderer.CommandBufferPool, resources.GetFlushBuffer());
                UploadCorrelator.NoteProbeReadback(Info.Width, Info.Height, Info.BytesPerPixel, fg, false);
                return PinnedSpan<byte>.UnsafeFromSpan(fg);
            }

            if (_logTexReadback)
            {
                LogReadback(true);
            }

            ReadOnlySpan<byte> bg = GetData(resources.GetPool(), resources.GetFlushBuffer());
            UploadCorrelator.NoteProbeReadback(Info.Width, Info.Height, Info.BytesPerPixel, bg, true);
            return PinnedSpan<byte>.UnsafeFromSpan(bg);
        }

        public PinnedSpan<byte> GetData(int layer, int level)
        {
            StoreLiveness.NoteRead(CanonicalPtr);

            EncoderStateManager.NoteReadback(CanonicalPtr, $"{Info.Width}x{Info.Height}/{MtlFormat} layer={layer} level={level}");

            BackgroundResource resources = Renderer.BackgroundResources.Get();

            if (Renderer.CommandBufferPool.OwnedByCurrentThread)
            {
                if (_logTexReadback)
                {
                    LogReadback(false);
                }

                Renderer.FlushAllCommands();

                ReadOnlySpan<byte> fgl = GetData(Renderer.CommandBufferPool, resources.GetFlushBuffer(), layer, level);
                UploadCorrelator.NoteProbeReadback(Info.Width, Info.Height, Info.BytesPerPixel, fgl, false);
                return PinnedSpan<byte>.UnsafeFromSpan(fgl);
            }

            if (_logTexReadback)
            {
                LogReadback(true);
            }

            ReadOnlySpan<byte> bgl = GetData(resources.GetPool(), resources.GetFlushBuffer(), layer, level);
            UploadCorrelator.NoteProbeReadback(Info.Width, Info.Height, Info.BytesPerPixel, bgl, true);
            return PinnedSpan<byte>.UnsafeFromSpan(bgl);
        }

        /// <summary>
        /// GetData asks OwnedByCurrentThread and sends background callers to their own
        /// pool; SetData below takes Pipeline.Cbs and opens a blit encoder on it with no
        /// such check, so a thread-pool worker can be mid-encoder on the render thread's
        /// command buffer when the render thread commits it. That is the shape of the
        /// crash that appears within two minutes of moving and never while standing
        /// still. This counts the off-thread callers so the asymmetry can be confirmed
        /// rather than argued. RYUJINX_METAL_LOG_SETDATA_THREAD=1.
        /// </summary>
        private static readonly bool _logSetDataThread =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_LOG_SETDATA_THREAD") == "1";

        private static int _setDataOffThread;
        private static int _setDataOnThread;

        private void NoteSetDataThread()
        {
            if (!_logSetDataThread)
            {
                return;
            }

            if (Renderer.CommandBufferPool.OwnedByCurrentThread)
            {
                _setDataOnThread++;
            }
            else if (++_setDataOffThread % 200 == 1)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"SetData OFF the render thread: {_setDataOffThread} (on-thread {_setDataOnThread}) " +
                    $"{Info.Width}x{Info.Height} {MtlFormat} thread={System.Threading.Thread.CurrentThread.Name ?? "?"}");
            }
        }

        public void SetData(MemoryOwner<byte> data)
        {
            StoreLiveness.NoteOverwrite(CanonicalPtr);

            NoteSetDataThread();
            OpRing.NoteSetData(GetHandle().NativePtr);
            HdrPassProbe.NoteSceneCopy(null, this);
            HdrPassProbe.NoteNonRenderWrite(this, "upload");
            UploadCorrelator.NoteUpload(this);

            CommandBufferScoped cbs = Pipeline.Cbs;
            MTLBlitCommandEncoder blitCommandEncoder = Pipeline.GetOrCreateBlitEncoder();

            Span<byte> dataSpan = data.Memory.Span;

            BufferHolder buffer = Renderer.BufferManager.Create(dataSpan.Length);
            buffer.SetDataUnchecked(0, dataSpan);
            MTLBuffer mtlBuffer = buffer.GetBuffer(false).Get(cbs).Value;
            MTLTexture image = GetIdentityHandle(cbs);

            int width = Info.Width;
            int height = Info.Height;
            int depth = Info.Depth;
            int levels = Info.Levels;
            int layers = Info.GetLayers();
            bool is3D = Info.Target == Target.Texture3D;

            int offset = 0;

            for (int level = 0; level < levels; level++)
            {
                int mipSize = Info.GetMipSize2D(level);
                int endOffset = offset + mipSize;

                if ((uint)endOffset > (uint)dataSpan.Length)
                {
                    // Truncated upload: without this the staging BufferHolder (and
                    // its device buffer sized for the full upload) leaked.
                    buffer.Dispose();

                    return;
                }

                for (int layer = 0; layer < layers; layer++)
                {
                    blitCommandEncoder.CopyFromBuffer(
                        mtlBuffer,
                        (ulong)offset,
                        (ulong)Info.GetMipStride(level),
                        (ulong)mipSize,
                        new MTLSize { width = (ulong)width, height = (ulong)height, depth = is3D ? (ulong)depth : 1 },
                        image,
                        (ulong)layer,
                        (ulong)level,
                        new MTLOrigin()
                    );

                    offset += mipSize;
                }

                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);

                if (is3D)
                {
                    depth = Math.Max(1, depth >> 1);
                }
            }

            // Cleanup
            buffer.Dispose();
        }

        private void SetData(ReadOnlySpan<byte> data, int layer, int level, int layers, int levels, bool singleSlice)
        {
            int bufferDataLength = GetBufferDataLength(data.Length);

            using BufferHolder bufferHolder = Renderer.BufferManager.Create(bufferDataLength);

            // TODO: loadInline logic

            CommandBufferScoped cbs = Pipeline.Cbs;

            CopyDataToBuffer(bufferHolder.GetDataStorage(0, bufferDataLength), data);

            MTLBuffer buffer = bufferHolder.GetBuffer().Get(cbs).Value;
            MTLTexture image = GetIdentityHandle(cbs);

            CopyFromOrToBuffer(cbs, buffer, image, bufferDataLength, false, layer, level, layers, levels, singleSlice);
        }

        public void SetData(MemoryOwner<byte> data, int layer, int level)
        {
            NoteSetDataThread();
            OpRing.NoteSetData(GetHandle().NativePtr);
            HdrPassProbe.NoteSceneCopy(null, this);
            UploadCorrelator.NoteUpload(this);
            SetData(data.Memory.Span, layer, level, 1, 1, singleSlice: true);

            data.Dispose();
        }

        public void SetData(MemoryOwner<byte> data, int layer, int level, Rectangle<int> region)
        {
            NoteSetDataThread();
            OpRing.NoteSetData(GetHandle().NativePtr);
            HdrPassProbe.NoteSceneCopy(null, this);
            UploadCorrelator.NoteUpload(this);
            CommandBufferScoped cbs = Pipeline.Cbs;
            MTLBlitCommandEncoder blitCommandEncoder = Pipeline.GetOrCreateBlitEncoder();

            ulong bytesPerRow = (ulong)Info.GetMipStride(level);
            ulong bytesPerImage = 0;
            MTLTexture image = GetIdentityHandle(cbs);

            if (image.TextureType == MTLTextureType.Type3D)
            {
                bytesPerImage = bytesPerRow * (ulong)Info.Height;
            }

            Span<byte> dataSpan = data.Memory.Span;

            BufferHolder buffer = Renderer.BufferManager.Create(dataSpan.Length);
            buffer.SetDataUnchecked(0, dataSpan);
            MTLBuffer mtlBuffer = buffer.GetBuffer(false).Get(cbs).Value;

            blitCommandEncoder.CopyFromBuffer(
                mtlBuffer,
                0,
                bytesPerRow,
                bytesPerImage,
                new MTLSize { width = (ulong)region.Width, height = (ulong)region.Height, depth = 1 },
                image,
                (ulong)layer,
                (ulong)level,
                new MTLOrigin { x = (ulong)region.X, y = (ulong)region.Y }
            );

            // Cleanup
            buffer.Dispose();
        }

        private int GetBufferDataLength(int length)
        {
            if (NeedsD24S8Conversion())
            {
                return length * 2;
            }

            return length;
        }

        public void SetStorage(BufferRange buffer)
        {
            throw new NotImplementedException();
        }

        public override void Release()
        {
            if (TryInvalidate())
            {
                if (_identityIsDifferent)
                {
                    _identitySwizzleHandle.Dispose();
                }

                DisposeHandle();
            }
        }
    }
}
