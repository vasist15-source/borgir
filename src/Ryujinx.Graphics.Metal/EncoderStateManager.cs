using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.State;
using Ryujinx.Graphics.Metal.SharpMetalExtensions;
using Ryujinx.Graphics.Shader;
using SharpMetal.Metal;
using System;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BufferAssignment = Ryujinx.Graphics.GAL.BufferAssignment;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// What a render command encoder has actually been told, as opposed to what the
    /// next draw wants. The dirty flags describe changes to the desired state, and
    /// several of them - the pipeline flag above all - are raised by updates that
    /// leave the encoder level state identical, so applying them unconditionally
    /// repeats the same call over and over within a pass.
    ///
    /// Held by reference so the readonly setters can update it, and keyed on the
    /// encoder itself: Metal starts every encoder from default state rather than
    /// from the previous encoder's, so a different encoder invalidates everything
    /// here without any dirty flag bookkeeping having to be right.
    /// </summary>
    [SupportedOSPlatform("macos")]
    sealed class AppliedRenderState
    {
        [Flags]
        public enum Field
        {
            Pipeline = 1 << 0,
            BlendColor = 1 << 1,
            DepthStencil = 1 << 2,
            DepthClip = 1 << 3,
            DepthBias = 1 << 4,
            Cull = 1 << 5,
            Winding = 1 << 6,
            StencilRef = 1 << 7,
        }

        // A/B switch: RYUJINX_METAL_STATE_CACHE=0, or a 0 in the toggle file, reports
        // every field as unknown, so each setter applies unconditionally the way it
        // did before this cache existed.
        //
        // The toggle is re-read once a frame because this machine's frame rate drifts
        // by ~10% over the twenty minutes three separate runs take, which is several
        // times the effect being measured. Alternating both behaviours inside one
        // session cancels that drift. Flipping is safe at any moment: the cache is
        // updated whether or not it is being consulted, so it never goes stale.
        // Levels, so a measurement can attribute the effect to one part at a time:
        // 0 nothing, 1 state fields, 2 also buffer binds, 3 also residency.
        public const int LevelOff = 0;
        public const int LevelFields = 1;
        public const int LevelBuffers = 2;
        public const int LevelResidency = 3;

        private const string ToggleFile = "/tmp/ryujinx-metal-state-cache";

        private static int _level = ParseLevel(Environment.GetEnvironmentVariable("RYUJINX_METAL_STATE_CACHE"), LevelResidency);

        private static int ParseLevel(string text, int fallback)
        {
            return int.TryParse(text, out int level) ? Math.Clamp(level, LevelOff, LevelResidency) : fallback;
        }

        public static void RefreshToggle()
        {
            try
            {
                if (File.Exists(ToggleFile))
                {
                    _level = ParseLevel(File.ReadAllText(ToggleFile).Trim(), _level);
                }
            }
            catch (IOException)
            {
                // Raced with the writer; the next frame will pick it up.
            }
        }

        private const int BufferSlots = (int)Constants.MaximumBufferArgumentTableEntries;
        private const int MaxResidentTracked = 8192;

        private IntPtr _encoder;
        private long _generation = -1;
        private Field _known;

        private readonly (IntPtr Buffer, ulong Offset)[] _vertexBuffers = new (IntPtr, ulong)[BufferSlots];
        private readonly (IntPtr Buffer, ulong Offset)[] _fragmentBuffers = new (IntPtr, ulong)[BufferSlots];
        private uint _vertexBound;
        private uint _fragmentBound;

        private readonly HashSet<(IntPtr Resource, MTLResourceUsage Usage, MTLRenderStages Stages)> _resident = [];

        public IntPtr PipelineState;

        /// <summary>
        /// Whether the last <see cref="EncoderStateManager.SetRenderPipelineState"/> left a
        /// usable pipeline on the encoder. Kept separate from <see cref="PipelineState"/>,
        /// which persists across encoders: zeroing that field to signal failure would make
        /// the draw guard skip the very call that repairs it, blacking out every later frame.
        /// </summary>
        public bool PipelineValid;

        public IntPtr DepthStencilState;
        public ColorF BlendColor;
        public MTLDepthClipMode DepthClipMode;
        public float DepthBias;
        public float SlopeScale;
        public float Clamp;
        public MTLCullMode CullMode;
        public MTLWinding Winding;
        public int FrontRefValue;
        public int BackRefValue;

        /// <summary>
        /// Whether this cache already holds the value <paramref name="field"/> was
        /// last set to on <paramref name="encoder"/>. False means the caller must
        /// apply it; the field is marked known either way, because every caller
        /// applies when this returns false.
        /// </summary>
        public bool Knows(MTLRenderCommandEncoder encoder, Field field)
        {
            Retarget(encoder);

            bool known = _level >= LevelFields && (_known & field) != 0;

            _known |= field;

            return known;
        }

        /// <summary>
        /// Whether <paramref name="buffer"/> is already bound at its own index for
        /// the vertex or fragment stage of this encoder, in which case binding it
        /// again would do nothing. Contents may of course have changed since - the
        /// binding names the buffer, and the GPU reads it when the draw executes.
        /// </summary>
        public bool IsBound(MTLRenderCommandEncoder encoder, in BufferResource buffer, bool fragment)
        {
            Retarget(encoder);

            if (buffer.Binding >= BufferSlots)
            {
                return false;
            }

            int slot = (int)buffer.Binding;

            ref (IntPtr Buffer, ulong Offset) bound = ref (fragment ? ref _fragmentBuffers[slot] : ref _vertexBuffers[slot]);
            ref uint mask = ref (fragment ? ref _fragmentBound : ref _vertexBound);

            uint bit = 1u << slot;

            bool same = _level >= LevelBuffers &&
                (mask & bit) != 0 &&
                bound.Buffer == buffer.Buffer.NativePtr &&
                bound.Offset == buffer.Offset;

            mask |= bit;
            bound = (buffer.Buffer.NativePtr, buffer.Offset);

            return same;
        }

        /// <summary>
        /// Whether <paramref name="resource"/> has already been declared resident on
        /// this encoder for the same usage and stages, which lasts until the encoder
        /// ends and so does not need repeating. The encoder retains what it has been
        /// given, so a live declaration's pointer cannot be recycled underneath this.
        /// </summary>
        /// <summary>
        /// Drops the record of what has been declared to this encoder, so the next draw
        /// declares everything again without the pass ending. Ending a pass is the only way
        /// residency is currently re-issued, and more splitting is the one thing that
        /// reduces the flash - this separates the two.
        /// </summary>
        public void ForgetResidency()
        {
            _resident.Clear();
        }

        public bool IsResident(MTLRenderCommandEncoder encoder, IntPtr resource, MTLResourceUsage usage, MTLRenderStages stages)
        {
            Retarget(encoder);

            // A single pass can hold an unbounded number of draws, so stop growing the
            // set rather than trade a call rate for a memory leak. Declaring a
            // resource again is wasteful, never wrong.
            if (_resident.Count >= MaxResidentTracked)
            {
                return false;
            }

            bool already = !_resident.Add((resource, usage, stages));

            return _level >= LevelResidency && already;
        }

        private void Retarget(MTLRenderCommandEncoder encoder)
        {
            // The pointer alone is not an identity: a released encoder's address is
            // routinely reused by the next one, and treating that as the same encoder
            // keeps every field marked applied, so nothing is ever set on the new
            // encoder - the first draw then faults in the driver on a null pipeline.
            long generation = CommandBufferEncoder.RenderEncoderGeneration;

            if (_encoder == encoder.NativePtr && _generation == generation)
            {
                return;
            }

            _encoder = encoder.NativePtr;
            _generation = generation;
            _known = default;
            _vertexBound = 0;
            _fragmentBound = 0;
            _resident.Clear();
        }
    }

    [SupportedOSPlatform("macos")]
    struct EncoderStateManager : IDisposable
    {
        private const int ArrayGrowthSize = 16;

        private readonly MTLDevice _device;
        private readonly Pipeline _pipeline;
        private readonly BufferManager _bufferManager;

        private readonly DepthStencilCache _depthStencilCache;
        private readonly MTLDepthStencilState _defaultState;
        private readonly AppliedRenderState _applied = new();

        private readonly EncoderState _mainState = new();
        private EncoderState _currentState;

        internal readonly EncoderState CurrentEncoderState => _currentState;

        /// <summary>
        /// True when the render encoder has a valid, non-null pipeline state set.
        /// Draw calls must check this and skip when false to avoid a SIGSEGV in
        /// the Metal driver (async shader compilation can leave the PSO null).
        /// </summary>
        public bool HasValidRenderPipeline => _applied.PipelineValid;

        public readonly IndexBufferState IndexBuffer => _currentState.IndexBuffer;
        public readonly PrimitiveTopology Topology => _currentState.Topology;
        public readonly Texture[] RenderTargets => _currentState.RenderTargets;
        public readonly Program RenderProgram => _currentState.RenderProgram;
        public readonly Program ComputeProgram => _currentState.ComputeProgram;

        /// <summary>
        /// Diagnostic companion to the draw trace: for every per-instance vertex
        /// buffer binding, log its layout and the CPU-visible bytes of the record
        /// the given first instance will fetch, plus which attributes read it.
        /// </summary>
        public readonly void TraceDumpInstanceBuffers(int firstInstance)
        {
            for (int i = 0; i < _currentState.VertexBuffers.Length; i++)
            {
                VertexBufferState vertexBuffer = _currentState.VertexBuffers[i];

                if (vertexBuffer.Divisor <= 0 || vertexBuffer.Stride <= 0)
                {
                    continue;
                }

                int record = firstInstance / vertexBuffer.Divisor;
                string recordText = vertexBuffer.DescribeRecordForTrace(_bufferManager, record);

                if (recordText == null)
                {
                    continue;
                }

                Logger.Warning?.PrintMsg(
                    LogClass.Gpu,
                    $"trace vb[{i}] stride={vertexBuffer.Stride} div={vertexBuffer.Divisor} rec[{record}]={recordText}");
            }

            for (int i = 0; i < _currentState.VertexAttribs.Length; i++)
            {
                VertexAttribDescriptor attrib = _currentState.VertexAttribs[i];

                if (attrib.IsZero || attrib.BufferIndex >= _currentState.VertexBuffers.Length)
                {
                    continue;
                }

                if (_currentState.VertexBuffers[attrib.BufferIndex].Divisor > 0)
                {
                    Logger.Warning?.PrintMsg(
                        LogClass.Gpu,
                        $"trace attr{i} vb={attrib.BufferIndex} off={attrib.Offset} fmt={attrib.Format}");
                }
            }
        }

        /// <summary>
        /// Diagnostic companion to the draw trace: dump the CPU-visible contents of
        /// every bound uniform (constant) buffer as vec4 floats. Comparing the same
        /// draw across the three traced frames shows whether an animation input (e.g.
        /// the gloom flow phase in fp_c4[9].z) actually advances per frame on this
        /// backend, or is frozen. All buffers are storageModeShared so Contents is
        /// the live value the shader will read.
        /// </summary>
        /// <summary>
        /// Diagnostic companion to the draw trace: for every bound texture, log its
        /// target (2D/3D/Cube), format, dimensions and mip levels. The gloom material
        /// samples four 3D volumetric-noise textures that drive its discard threshold;
        /// a wrong target or format here would make the noise wrong and over-discard
        /// the gloom (sparse coverage on Metal vs full field on Vulkan).
        /// </summary>
        public readonly void TraceDumpTextures(string tag)
        {
            for (int i = 0; i < _currentState.TextureRefs.Length; i++)
            {
                ref TextureRef texRef = ref _currentState.TextureRefs[i];

                if (texRef.Storage == null)
                {
                    continue;
                }

                TextureCreateInfo info = texRef.Storage.Info;

                Logger.Warning?.PrintMsg(
                    LogClass.Gpu,
                    $"trace tex[{i}] {tag} stage={texRef.Stage} target={info.Target} fmt={info.Format} {info.Width}x{info.Height}x{info.Depth} levels={info.Levels} imgFmt={texRef.ImageFormat}");
            }
        }

        public readonly unsafe void TraceDumpUniformBuffers(string tag)
        {
            for (int i = 0; i < _currentState.UniformBufferRefs.Length; i++)
            {
                ref BufferRef bufferRef = ref _currentState.UniformBufferRefs[i];

                if (bufferRef.Buffer == null)
                {
                    continue;
                }

                MTLBuffer mtlBuffer = bufferRef.Buffer.GetUnsafe().Value;

                if (mtlBuffer.NativePtr == IntPtr.Zero || mtlBuffer.Contents == IntPtr.Zero)
                {
                    continue;
                }

                int offset = bufferRef.Range?.Offset ?? 0;
                int size = bufferRef.Range?.Size ?? 256;

                int vec4Count = Math.Min(16, size / 16);

                if (vec4Count <= 0)
                {
                    continue;
                }

                float* data = (float*)((byte*)mtlBuffer.Contents + offset);

                System.Text.StringBuilder builder = new();
                builder.Append($"trace cbuf[{i}] {tag} off={offset} size={size}:");

                for (int v = 0; v < vec4Count; v++)
                {
                    builder.Append($" [{v}]({data[v * 4].ToString("G6")},{data[v * 4 + 1].ToString("G6")},{data[v * 4 + 2].ToString("G6")},{data[v * 4 + 3].ToString("G6")})");
                }

                Logger.Warning?.PrintMsg(LogClass.Gpu, builder.ToString());
            }
        }
        public readonly Texture DepthStencil => _currentState.DepthStencil;
        public readonly ComputeSize ComputeLocalSize => _currentState.ComputeProgram.ComputeLocalSize;

        // RGBA32F is the biggest format
        private const int ZeroBufferSize = 4 * 4;
        private readonly BufferHandle _zeroBuffer;

        public unsafe EncoderStateManager(MTLDevice device, BufferManager bufferManager, Pipeline pipeline)
        {
            _device = device;
            _pipeline = pipeline;
            _bufferManager = bufferManager;

            _depthStencilCache = new(device);
            _currentState = _mainState;

            _defaultState = _depthStencilCache.GetOrCreate(_currentState.DepthStencilUid);

            // Zero buffer
            byte[] zeros = new byte[ZeroBufferSize];
            fixed (byte* ptr = zeros)
            {
                _zeroBuffer = _bufferManager.Create((IntPtr)ptr, ZeroBufferSize);
            }
        }

        public readonly void Dispose()
        {
            _mainState.RenderEncoderBindings.DisposeTemporaryBuffers();
            _mainState.ComputeEncoderBindings.DisposeTemporaryBuffers();

            if (_currentState != _mainState)
            {
                _currentState.RenderEncoderBindings.DisposeTemporaryBuffers();
                _currentState.ComputeEncoderBindings.DisposeTemporaryBuffers();
            }

            _depthStencilCache.Dispose();
        }

        public readonly void DisposeRenderTemporaryBuffers()
        {
            _currentState.RenderEncoderBindings.DisposeTemporaryBuffers();
        }

        public readonly void DisposeComputeTemporaryBuffers()
        {
            _currentState.ComputeEncoderBindings.DisposeTemporaryBuffers();
        }

        private readonly void SignalDirty(DirtyFlags flags)
        {
            _currentState.Dirty |= flags;
        }

        /// <summary>
        /// A bound buffer range changed identity - it gained or lost a mirror - so the
        /// argument buffers that name it have to be rebuilt.
        /// </summary>
        public readonly void SignalBufferRebind()
        {
            SignalDirty(DirtyFlags.Uniforms | DirtyFlags.Storages);
        }

        public readonly void SignalRenderDirty()
        {
            SignalDirty(DirtyFlags.RenderAll);
        }

        /// <summary>
        /// Draws the current program through its framebuffer-fetch variant (null = the
        /// program itself). Only the pipeline object changes; the variant keeps the base
        /// program's binding layout, so argument buffers are built from the base as before.
        /// </summary>
        public readonly void UseFetchVariant(Program variant)
        {
            if (!ReferenceEquals(_currentState.FetchVariant, variant))
            {
                _currentState.FetchVariant = variant;
                SignalDirty(DirtyFlags.RenderPipeline);
            }
        }

        public readonly void SignalComputeDirty()
        {
            SignalDirty(DirtyFlags.ComputeAll);
        }

        public EncoderState SwapState(EncoderState state, DirtyFlags flags = DirtyFlags.All)
        {
            _currentState = state ?? _mainState;

            SignalDirty(flags);

            return _mainState;
        }

        public PredrawState SavePredrawState()
        {
            return new PredrawState
            {
                CullMode = _currentState.CullMode,
                DepthStencilUid = _currentState.DepthStencilUid,
                Topology = _currentState.Topology,
                Viewports = _currentState.Viewports.ToArray(),
            };
        }

        public readonly void RestorePredrawState(PredrawState state)
        {
            _currentState.CullMode = state.CullMode;
            _currentState.DepthStencilUid = state.DepthStencilUid;
            _currentState.Topology = state.Topology;
            _currentState.Viewports = state.Viewports;

            SignalDirty(DirtyFlags.CullMode | DirtyFlags.DepthStencil | DirtyFlags.Viewports);
        }

        public readonly void SetClearLoadAction(bool clear)
        {
            _currentState.ClearLoadAction = clear;
        }

        public readonly void DirtyTextures()
        {
            SignalDirty(DirtyFlags.Textures);
        }

        public readonly void DirtyImages()
        {
            SignalDirty(DirtyFlags.Images);
        }

        public readonly MTLRenderCommandEncoder CreateRenderCommandEncoder()
        {
            _passAttachments.Clear();
            _passStoreCandidates.Clear();
            _passStoreElided = false;

            // Reads through bindings made before the producing pass ended: the guest binds
            // its inputs, then switches targets, and the draws of this new pass sample
            // them without another bind call. Sweep what is bound now so those stores
            // count as consumed; a bind change mid-pass still reports through the setter.
            if (StoreLiveness.Enabled)
            {
                for (int i = 0; i < _currentState.TextureRefs.Length; i++)
                {
                    if (_currentState.TextureRefs[i].Storage is Texture bound)
                    {
                        StoreLiveness.NoteRead(bound.CanonicalPtr);
                    }
                }

                for (int i = 0; i < _currentState.ImageRefs.Length; i++)
                {
                    if (_currentState.ImageRefs[i].Storage is Texture boundImage)
                    {
                        StoreLiveness.NoteRead(boundImage.CanonicalPtr);
                    }
                }
            }
            // Initialise Pass & State
            using MTLRenderPassDescriptor renderPassDescriptor = new();

            FeedbackProbe.BeginPass();

            _passStoreUnknown = _elideEmptyStore;
            _passColorMask = 0;
            _passHasDepth = false;
            _passHasStencil = false;
            _passCleared = _currentState.ClearLoadAction;

            Texture sceneCheck = _currentState.RenderTargets[0] as Texture;
            _passSceneClass = sceneCheck != null &&
                sceneCheck.Width >= 1500 && sceneCheck.Width <= 1700 &&
                sceneCheck.MtlFormat == MTLPixelFormat.RG11B10Float;

            NotePassSize(0, 0);

            for (int i = 0; i < Constants.MaxColorAttachments; i++)
            {
                if (_currentState.RenderTargets[i] is Texture tex)
                {
                    MTLRenderPassColorAttachmentDescriptor passAttachment = renderPassDescriptor.ColorAttachments.Object((ulong)i);
                    tex.PopulateRenderPassAttachment(passAttachment, _pipeline.Cbs);

                    // The 11:03 crash was AGX segfaulting while walking this descriptor's
                    // attachments at encoder creation - a dead texture handle. If one ever
                    // lands here again, name it in the log instead of letting the driver
                    // dereference it.
                    if (passAttachment.Texture.NativePtr == IntPtr.Zero && _attachFaults++ < 20)
                    {
                        Logger.Error?.PrintMsg(LogClass.Gpu,
                            $"null colour attachment handle at pass build: rt={i} {tex.Width}x{tex.Height}/{tex.MtlFormat}\n{Environment.StackTrace}");
                    }
                    passAttachment.LoadAction = _currentState.ClearLoadAction ? MTLLoadAction.Clear : MTLLoadAction.Load;

                    // Experiment: the game's final 1080p sRGB target is Loaded every frame and
                    // then fully repainted by its composite; the Metal API layer shows no other
                    // command touching that storage, yet present later reads white. If Load is
                    // handing the pass a stale tile instead of the texture's memory, forcing a
                    // Clear here removes the only read of old contents. RYUJINX_METAL_CLEAR_SRGB1080=1
                    if (_clearSrgb1080 && tex.Width >= 1900 && tex.Height >= 1000 &&
                        (tex.Info.Format == Format.R8G8B8A8Srgb || tex.Info.Format == Format.B8G8R8A8Srgb) &&
                        passAttachment.LoadAction == MTLLoadAction.Load)
                    {
                        passAttachment.LoadAction = MTLLoadAction.Clear;
                        passAttachment.ClearColor = new MTLClearColor { red = 0.0, green = 0.0, blue = 0.0, alpha = 1.0 };
                    }

                    // Canary: a clear colour the game never uses, set even when the load
                    // action is Load - the spec says it is ignored then. If white frames
                    // turn magenta, the driver executed this Load as a Clear, a mechanism
                    // nothing has tested. RYUJINX_METAL_CANARY_CLEAR=1.
                    if (_canaryClear)
                    {
                        passAttachment.ClearColor = new MTLClearColor { red = 1.0, green = 0.0, blue = 1.0, alpha = 1.0 };
                    }
                    passAttachment.StoreAction = _passStoreUnknown ? MTLStoreAction.Unknown : MTLStoreAction.Store;
                    _passColorMask |= 1ul << i;
                    StoreLiveness.NoteLoad(tex.CanonicalPtr, passAttachment.LoadAction == MTLLoadAction.Load);
                    _passStoreCandidates.Add((tex.CanonicalPtr, tex.Info.Width, tex.Info.Height, tex.MtlFormat, tex.Info.BytesPerPixel, false));

                    // The attachments this pass really carries. Comparing against
                    // _currentState.RenderTargets instead counted targets bound for an
                    // earlier pass, which is what buried the feedback signal twice.
                    FeedbackProbe.NoteAttachment(i, tex);
                    UploadCorrelator.NoteAttachment(tex, _currentState.ClearLoadAction);
                    if (SplitScopePass)
                    {
                        _passAttachments.Add(new PassAttachment(tex.CanonicalPtr, SubRange.Of(tex), i));
                    }
                    else
                    {
                        NoteAttachmentWritten(tex.CanonicalPtr, SubRange.Of(tex));
                    }
                    NotePassSize((ulong)tex.Width, (ulong)tex.Height);
                }
            }

            if (_passWidth == 0 && _currentState.DepthStencil != null)
            {
                NotePassSize((ulong)_currentState.DepthStencil.Width, (ulong)_currentState.DepthStencil.Height);
            }

            // Tile snapshot: a pass carrying an R32Float colour target gets a memoryless
            // twin in its highest free slot. The twin is in the pipeline descriptor of every
            // draw of the pass (write mask forced off), never stored or loaded, and only a
            // tile kernel writes it - right before a draw that reads the R32Float slot it
            // also writes, which then fetches the twin instead of splitting the pass.
            _snapshotSrc = -1;
            _snapshotDst = -1;
            PipelineState.SnapshotSlot = -1;

            if (TileSnapshot.Enabled)
            {
                int snapshotSrc = -1;
                int snapshotDst = -1;
                Texture snapshotSource = null;

                for (int i = 0; i < Constants.MaxColorAttachments; i++)
                {
                    if (_currentState.RenderTargets[i] is Texture candidate)
                    {
                        if (snapshotSrc < 0 && candidate.MtlFormat == MTLPixelFormat.R32Float)
                        {
                            snapshotSrc = i;
                            snapshotSource = candidate;
                        }
                    }
                    else
                    {
                        snapshotDst = i;
                    }
                }

                if (snapshotSrc >= 0 && snapshotDst >= 0)
                {
                    MTLRenderPassColorAttachmentDescriptor twin = renderPassDescriptor.ColorAttachments.Object((ulong)snapshotDst);
                    twin.Texture = TileSnapshot.GetMemoryless(_device, snapshotSource.Width, snapshotSource.Height);
                    twin.LoadAction = MTLLoadAction.DontCare;
                    twin.StoreAction = MTLStoreAction.DontCare;

                    ref ColorBlendStateUid twinBlend = ref _currentState.Pipeline.Internal.ColorBlendState[snapshotDst];
                    twinBlend.PixelFormat = MTLPixelFormat.R32Float;
                    twinBlend.WriteMask = MTLColorWriteMask.None;

                    _snapshotSrc = snapshotSrc;
                    _snapshotDst = snapshotDst;
                    PipelineState.SnapshotSlot = snapshotDst;
                    SignalDirty(DirtyFlags.RenderPipeline);
                }
            }

            MTLRenderPassDepthAttachmentDescriptor depthAttachment = renderPassDescriptor.DepthAttachment;
            MTLRenderPassStencilAttachmentDescriptor stencilAttachment = renderPassDescriptor.StencilAttachment;

            if (_currentState.DepthStencil != null)
            {
                // The depth/stencil attachment is written by this pass too. Until now only the
                // colour attachments entered the read-after-write write set, so a draw that
                // samples the scene depth while the pass writing it is still open - TOTK's
                // sun-visibility pass takes eight depth taps around the sun - never split the
                // pass and read whatever the texture last stored: on a tile-based GPU the
                // live depth sits in tile memory until the pass ends. RYUJINX_METAL_DEPTH_RAW=0
                // restores the old behaviour for A/B.
                if (_depthInWriteSet)
                {
                    if (SplitScopePass)
                    {
                        _passAttachments.Add(new PassAttachment(_currentState.DepthStencil.CanonicalPtr, SubRange.Of(_currentState.DepthStencil), -1));
                    }
                    else
                    {
                        NoteAttachmentWritten(_currentState.DepthStencil.CanonicalPtr, SubRange.Of(_currentState.DepthStencil));
                    }
                }
                StoreLiveness.NoteLoad(_currentState.DepthStencil.CanonicalPtr, true);
                _passStoreCandidates.Add((_currentState.DepthStencil.CanonicalPtr, _currentState.DepthStencil.Info.Width, _currentState.DepthStencil.Info.Height,
                    _currentState.DepthStencil.MtlFormat, _currentState.DepthStencil.Info.BytesPerPixel, true));

                switch (_currentState.DepthStencil.GetHandle().PixelFormat)
                {
                    // Depth Only Attachment
                    case MTLPixelFormat.Depth16Unorm:
                    case MTLPixelFormat.Depth32Float:
                        // Must be the identity handle, not the swizzled view: a Metal
                        // texture view created with a swizzle does not carry
                        // MTLTextureUsageRenderTarget, and binding one as an attachment
                        // is undefined behaviour - depth writes are silently dropped.
                        // Colour attachments already use the identity handle.
                        depthAttachment.Texture = _currentState.DepthStencil.GetIdentityHandle(_pipeline.Cbs);
                        depthAttachment.LoadAction = MTLLoadAction.Load;
                        depthAttachment.StoreAction = _passStoreUnknown ? MTLStoreAction.Unknown : MTLStoreAction.Store;
                        _passHasDepth = true;
                        break;

                    // Stencil Only Attachment
                    case MTLPixelFormat.Stencil8:
                        stencilAttachment.Texture = _currentState.DepthStencil.GetIdentityHandle(_pipeline.Cbs);
                        stencilAttachment.LoadAction = MTLLoadAction.Load;
                        stencilAttachment.StoreAction = _passStoreUnknown ? MTLStoreAction.Unknown : MTLStoreAction.Store;
                        _passHasStencil = true;
                        break;

                    // Combined Attachment
                    case MTLPixelFormat.Depth24UnormStencil8:
                    case MTLPixelFormat.Depth32FloatStencil8:
                        depthAttachment.Texture = _currentState.DepthStencil.GetIdentityHandle(_pipeline.Cbs);
                        depthAttachment.LoadAction = MTLLoadAction.Load;
                        depthAttachment.StoreAction = _passStoreUnknown ? MTLStoreAction.Unknown : MTLStoreAction.Store;
                        _passHasDepth = true;

                        stencilAttachment.Texture = _currentState.DepthStencil.GetIdentityHandle(_pipeline.Cbs);
                        stencilAttachment.LoadAction = MTLLoadAction.Load;
                        stencilAttachment.StoreAction = _passStoreUnknown ? MTLStoreAction.Unknown : MTLStoreAction.Store;
                        _passHasStencil = true;
                        break;
                    default:
                        Logger.Error?.PrintMsg(LogClass.Gpu, $"Unsupported Depth/Stencil Format: {_currentState.DepthStencil.GetHandle().PixelFormat}!");
                        break;
                }
            }

            // A render pass with no attachments at all is invalid: Metal's validation
            // layer reports "No output textures defined for the render pass" and the
            // encoder's output is undefined. Ryujinx creates these whenever the guest
            // has neither a colour target nor a depth/stencil target bound. Configure
            // targetless rasterization instead, which is the supported way to encode
            // draws that only have side effects.
            bool hasAttachment = _currentState.DepthStencil != null;

            if (!hasAttachment)
            {
                for (int i = 0; i < Constants.MaxColorAttachments; i++)
                {
                    if (_currentState.RenderTargets[i] != null)
                    {
                        hasAttachment = true;
                        break;
                    }
                }
            }

            if (!hasAttachment)
            {
                // The wrapper is a struct over the same native object, so a local copy
                // still configures the descriptor being built.
                MTLRenderPassDescriptor targetless = renderPassDescriptor;

                targetless.RenderTargetWidth = (ulong)Math.Max(1, _currentState.Viewports.Length > 0 ? (int)_currentState.Viewports[0].width : 1);
                targetless.RenderTargetHeight = (ulong)Math.Max(1, _currentState.Viewports.Length > 0 ? (int)_currentState.Viewports[0].height : 1);
                targetless.DefaultRasterSampleCount = 1;
            }

            // Record what this pass writes, so the present-time sweep can walk the chain
            // from scene colour to present source. Bookkeeping only - no Metal call, no
            // pass split, so it cannot move the race it is measuring.
            if (FrameProbe.Enabled)
            {
                FrameProbe.NotePass(_currentState.RenderTargets, _currentState.DepthStencil);
            }

            OpRing.NotePass(
                _currentState.RenderTargets[0]?.GetHandle().NativePtr ?? IntPtr.Zero,
                _currentState.DepthStencil?.GetHandle().NativePtr ?? IntPtr.Zero);

            if (HdrPassProbe.Enabled)
            {
                HdrPassProbe.BeginPassAll(_currentState.RenderTargets, _currentState.ClearLoadAction);

                foreach (Texture rt in _currentState.RenderTargets)
                {
                    HdrPassProbe.NoteIdentity("target", rt);
                }
            }

            if (HdrPassProbe.Enabled)
            {
                HdrPassProbe.NoteWriterProgram(_currentState.RenderTargets[0], _currentState.RenderProgram?.DebugLabel);
            }

            bool countSamples = _pipeline.SupportsSamplesPassed;
            ulong visibilityOffset = 0;

            // A pass descriptor carries one visibility buffer, so the coverage probe and
            // the guest's occlusion queries cannot both have it. The probe takes only the
            // passes into the watched composite, and only when it is switched on.
            bool measureCoverage = CoverageProbe.Enabled && CoverageProbe.TryTakePass(
                _pipeline.FrameSlot,
                _currentState.RenderTargets.Length > 0 ? _currentState.RenderTargets[0] : null,
                _currentState.Scissors.Length > 0 ? _currentState.Scissors[0] : default,
                _currentState.Viewports.Length > 0 ? _currentState.Viewports[0] : default,
                _currentState.DepthStencilUid.DepthCompareFunction,
                renderPassDescriptor,
                out visibilityOffset);

            if (!measureCoverage && countSamples)
            {
                visibilityOffset = _pipeline.PrepareCounterRenderPass(renderPassDescriptor);
            }

            // Initialise Encoder
            MTLRenderCommandEncoder renderCommandEncoder = _pipeline.CommandBuffer.RenderCommandEncoder(renderPassDescriptor);
            if (UploadCorrelator.Enabled)
            {
                Texture rt0 = _currentState.RenderTargets[0] as Texture;
                UploadCorrelator.Seq(rt0 != null ? $"P+{rt0.Width}x{rt0.Height}" : "P+none");
            }
            if (UploadCorrelator.VsDumpEnabled && UploadCorrelator.VsDumpBuffer.NativePtr != IntPtr.Zero)
            {
                // Vertex index 30 is outside every index the backend uses (vertex buffers
                // 0-15, zero buffer 16, argument tables 17-20), so the state cache never
                // touches it and the patched flare vertex shader finds its ring here.
                renderCommandEncoder.SetVertexBuffer(UploadCorrelator.VsDumpBuffer, 0, 30);
                renderCommandEncoder.SetFragmentBuffer(UploadCorrelator.VsDumpBuffer, 0, 30);
            }

            if (countSamples || measureCoverage)
            {
                renderCommandEncoder.SetVisibilityResultMode(MTLVisibilityResultMode.Counting, visibilityOffset);
            }

            return renderCommandEncoder;
        }

        /// <summary>
        /// Resolves the deferred store actions when the pass ends. Zero draws and nothing
        /// cleared means nothing in tile memory can differ from what the load brought in,
        /// so DontCare skips the write-back entirely - the pass stops touching memory.
        /// </summary>
        public readonly void FixupStoreActions(MTLRenderCommandEncoder encoder, ulong drawsInPass)
        {
            if (!_passStoreUnknown)
            {
                return;
            }

            MTLStoreAction action = drawsInPass == 0 && !_passCleared && _passSceneClass
                ? MTLStoreAction.DontCare
                : MTLStoreAction.Store;

            for (int i = 0; i < Constants.MaxColorAttachments; i++)
            {
                if ((_passColorMask & (1ul << i)) != 0)
                {
                    encoder.SetColorStoreAction(action, (ulong)i);
                }
            }

            if (_passHasDepth)
            {
                encoder.SetDepthStoreAction(action);
            }

            if (_passHasStencil)
            {
                encoder.SetStencilStoreAction(action);
            }

            _passStoreElided = action == MTLStoreAction.DontCare;
            _passStoreUnknown = false;
        }

        /// <summary>
        /// Runs the snapshot kernel for the planned fetch on the open encoder: slot src is
        /// copied into the twin for every pixel. The tile pipeline replaces the encoder's
        /// pipeline state, so the draw's own is marked dirty and bound again after.
        /// </summary>
        public readonly bool DispatchSnapshotIfPlanned(MTLRenderCommandEncoder encoder)
        {
            if (!_fetchPlanNeedsSnapshot || _snapshotSrc < 0 || _snapshotDst < 0)
            {
                return false;
            }

            Span<MTLPixelFormat> formats = stackalloc MTLPixelFormat[Constants.MaxColorAttachments];

            for (int i = 0; i < formats.Length; i++)
            {
                formats[i] = _currentState.Pipeline.Internal.ColorBlendState[i].PixelFormat;
            }

            if (!TileSnapshot.Dispatch(_device, encoder, _snapshotSrc, _snapshotDst, formats))
            {
                return false;
            }

            // The encoder now holds the tile pipeline. The applied-state cache still
            // remembers the draw's pipeline as bound and would skip setting it again -
            // which drew with the tile pipeline and faulted inside drawIndexedPrimitives
            // the first time this ran. Forget it so the rebind really sets it.
            _applied.PipelineState = IntPtr.Zero;
            SignalDirty(DirtyFlags.RenderPipeline);

            return true;
        }

        /// <summary>
        /// The pass has ended and its attachments were stored (unless the fixup elided
        /// the store): hand every one to the liveness probe.
        /// </summary>
        public readonly void NotePassStored()
        {
            if (_passStoreElided)
            {
                return;
            }

            for (int i = 0; i < _passStoreCandidates.Count; i++)
            {
                (IntPtr ptr, int w, int h, MTLPixelFormat fmt, int bpp, bool depth) = _passStoreCandidates[i];
                StoreLiveness.NoteStore(ptr, w, h, fmt, bpp, depth);
            }

            // A helper pass that ends without going through CreateRenderCommandEncoder
            // must not report the previous pass's attachments a second time.
            _passStoreCandidates.Clear();
        }

        public readonly MTLComputeCommandEncoder CreateComputeCommandEncoder()
        {
            using MTLComputePassDescriptor descriptor = new();
            MTLComputeCommandEncoder computeCommandEncoder = _pipeline.CommandBuffer.ComputeCommandEncoder(descriptor);

            return computeCommandEncoder;
        }

        // Diagnostic probe: with RYUJINX_METAL_FULL_REBIND=1 every draw re-writes the
        // argument buffers and residency lists from the CURRENT texture/buffer
        // handles, ignoring dirty tracking. Costs performance; if it eliminates the
        // stale-content symptoms (vanishing baked foliage, frozen gloom), the dirty
        // tracking is missing handle-invalidation signals.
        private static readonly bool _forceFullRebind =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_FULL_REBIND") == "1";

        /// <summary>
        /// Every image currently bound for compute is a potential writer of that texture
        /// through kernel write(); register them so a compute-filled target shows up in the
        /// writer census, which only ever covered render attachments and copies.
        /// </summary>
        public readonly void NoteComputeImageWriters()
        {
            for (int i = 0; i < _currentState.ImageRefs.Length; i++)
            {
                if (_currentState.ImageRefs[i].Storage is Texture img)
                {
                    UploadCorrelator.NoteAttachmentDraw(img.CanonicalPtr, "compute-img");
                }
            }
        }

        public readonly void RenderResourcesPrepass()
        {
            _currentState.RenderEncoderBindings.Clear();

            if (_forceFullRebind)
            {
                _currentState.Dirty |= DirtyFlags.RenderPipeline | DirtyFlags.Uniforms |
                    DirtyFlags.Storages | DirtyFlags.Textures | DirtyFlags.Images;
            }

            if ((_currentState.Dirty & DirtyFlags.RenderPipeline) != 0)
            {
                SetVertexBuffers(_currentState.VertexBuffers, ref _currentState.RenderEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Uniforms) != 0)
            {
                UpdateAndBind(_currentState.RenderProgram, Constants.ConstantBuffersSetIndex, ref _currentState.RenderEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Storages) != 0)
            {
                UpdateAndBind(_currentState.RenderProgram, Constants.StorageBuffersSetIndex, ref _currentState.RenderEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Textures) != 0)
            {
                UpdateAndBind(_currentState.RenderProgram, Constants.TexturesSetIndex, ref _currentState.RenderEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Images) != 0)
            {
                UpdateAndBind(_currentState.RenderProgram, Constants.ImagesSetIndex, ref _currentState.RenderEncoderBindings);
            }
        }

        public readonly void ComputeResourcesPrepass()
        {
            _currentState.ComputeEncoderBindings.Clear();

            if (_forceFullRebind)
            {
                _currentState.Dirty |= DirtyFlags.Uniforms | DirtyFlags.Storages |
                    DirtyFlags.Textures | DirtyFlags.Images;
            }

            if ((_currentState.Dirty & DirtyFlags.Uniforms) != 0)
            {
                UpdateAndBind(_currentState.ComputeProgram, Constants.ConstantBuffersSetIndex, ref _currentState.ComputeEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Storages) != 0)
            {
                UpdateAndBind(_currentState.ComputeProgram, Constants.StorageBuffersSetIndex, ref _currentState.ComputeEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Textures) != 0)
            {
                UpdateAndBind(_currentState.ComputeProgram, Constants.TexturesSetIndex, ref _currentState.ComputeEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Images) != 0)
            {
                UpdateAndBind(_currentState.ComputeProgram, Constants.ImagesSetIndex, ref _currentState.ComputeEncoderBindings);
            }
        }

        public void RebindRenderState(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if ((_currentState.Dirty & DirtyFlags.RenderPipeline) != 0)
            {
                SetRenderPipelineState(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.DepthStencil) != 0)
            {
                SetDepthStencilState(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.DepthClamp) != 0)
            {
                SetDepthClamp(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.DepthBias) != 0)
            {
                SetDepthBias(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.CullMode) != 0)
            {
                SetCullMode(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.FrontFace) != 0)
            {
                SetFrontFace(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.StencilRef) != 0)
            {
                SetStencilRefValue(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.Viewports) != 0)
            {
                SetViewports(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.Scissors) != 0)
            {
                SetScissors(renderCommandEncoder);
            }

            UseRenderResources(renderCommandEncoder, ref _currentState.RenderEncoderBindings);

            foreach (BufferResource buffer in _currentState.RenderEncoderBindings.VertexBuffers)
            {
                if (!_applied.IsBound(renderCommandEncoder, buffer, fragment: false))
                {
                    renderCommandEncoder.SetVertexBuffer(buffer.Buffer, buffer.Offset, buffer.Binding);
                }
            }

            foreach (BufferResource buffer in _currentState.RenderEncoderBindings.FragmentBuffers)
            {
                if (!_applied.IsBound(renderCommandEncoder, buffer, fragment: true))
                {
                    renderCommandEncoder.SetFragmentBuffer(buffer.Buffer, buffer.Offset, buffer.Binding);
                }
            }

            _currentState.Dirty &= ~DirtyFlags.RenderAll;

            // A pipeline that failed to build has to stay dirty. Clearing the flag would
            // stop the state cache from ever rebuilding it, and since the draw guard skips
            // on an invalid pipeline, nothing would ever run SetRenderPipelineState again -
            // one failed build would blank every later frame. Retrying each draw instead
            // lets the draw recover as soon as async shader compilation finishes.
            if (!_applied.PipelineValid)
            {
                _currentState.Dirty |= DirtyFlags.RenderPipeline;
            }
        }

        public void RebindComputeState(MTLComputeCommandEncoder computeCommandEncoder)
        {
            if ((_currentState.Dirty & DirtyFlags.ComputePipeline) != 0)
            {
                SetComputePipelineState(computeCommandEncoder);
            }

            UseComputeResources(computeCommandEncoder, ref _currentState.ComputeEncoderBindings);

            foreach (BufferResource buffer in _currentState.ComputeEncoderBindings.Buffers)
            {
                computeCommandEncoder.SetBuffer(buffer.Buffer, buffer.Offset, buffer.Binding);
            }

            _currentState.Dirty &= ~DirtyFlags.ComputeAll;
        }

        private readonly void UseRenderResources(MTLRenderCommandEncoder renderCommandEncoder, ref RenderEncoderBindings bindings)
        {
            if (bindings.Resources.Count == 0)
            {
                return;
            }

            MTLResource[] resources = bindings.GetResourceScratch(bindings.Resources.Count);

            // RYUJINX_METAL_REDECLARE=N re-declares residency every N draws without ending
            // the pass, which is the experiment that separates "splitting helps because it
            // re-issues useResource" from "splitting helps for some other reason".
            if (_redeclareEvery > 0 && _pipeline.DrawCount % (ulong)_redeclareEvery == 0)
            {
                _applied.ForgetResidency();
            }

            if (_shadowBind)
            {
                for (int i = 0; i < bindings.ShadowTextures.Count; i++)
                {
                    renderCommandEncoder.SetFragmentTexture(new MTLTexture(bindings.ShadowTextures[i]), (ulong)i);
                }

                if (bindings.ShadowTextures.Count > 0 && ++_shadowBinds % 500000 == 1)
                {
                    Logger.Info?.PrintMsg(LogClass.Gpu, $"shadow-bind engaged: applies={_shadowBinds} collected={_shadowCollected}");
                }
            }

            UseRenderResources(renderCommandEncoder, bindings.Resources, resources, MTLResourceUsage.Read, MTLRenderStages.RenderStageVertex);
            UseRenderResources(renderCommandEncoder, bindings.Resources, resources, MTLResourceUsage.Read, MTLRenderStages.RenderStageFragment);
            UseRenderResources(renderCommandEncoder, bindings.Resources, resources, MTLResourceUsage.Read, MTLRenderStages.RenderStageVertex | MTLRenderStages.RenderStageFragment);
            UseRenderResources(renderCommandEncoder, bindings.Resources, resources, MTLResourceUsage.Read | MTLResourceUsage.Write, MTLRenderStages.RenderStageVertex);
            UseRenderResources(renderCommandEncoder, bindings.Resources, resources, MTLResourceUsage.Read | MTLResourceUsage.Write, MTLRenderStages.RenderStageFragment);
            UseRenderResources(renderCommandEncoder, bindings.Resources, resources, MTLResourceUsage.Read | MTLResourceUsage.Write, MTLRenderStages.RenderStageVertex | MTLRenderStages.RenderStageFragment);
        }

        private readonly void UseRenderResources(
            MTLRenderCommandEncoder renderCommandEncoder,
            List<Resource> bindings,
            MTLResource[] resources,
            MTLResourceUsage usage,
            MTLRenderStages stages)
        {
            int count = 0;

            foreach (Resource binding in bindings)
            {
                // Residency lasts for the life of the encoder, so a resource already
                // declared under this usage and stages does not need declaring again.
                if (binding.ResourceUsage == usage && binding.Stages == stages &&
                    !_applied.IsResident(renderCommandEncoder, binding.MtlResource.NativePtr, usage, stages))
                {
                    resources[count++] = binding.MtlResource;
                }
            }

            if (count != 0)
            {
                renderCommandEncoder.UseResourcesCompat(resources, (ulong)count, usage, stages);
            }
        }

        private static void UseComputeResources(MTLComputeCommandEncoder computeCommandEncoder, ref ComputeEncoderBindings bindings)
        {
            if (bindings.Resources.Count == 0)
            {
                return;
            }

            MTLResource[] resources = bindings.GetResourceScratch(bindings.Resources.Count);

            UseComputeResources(computeCommandEncoder, bindings.Resources, resources, MTLResourceUsage.Read);
            UseComputeResources(computeCommandEncoder, bindings.Resources, resources, MTLResourceUsage.Read | MTLResourceUsage.Write);
        }

        private static void UseComputeResources(
            MTLComputeCommandEncoder computeCommandEncoder,
            List<Resource> bindings,
            MTLResource[] resources,
            MTLResourceUsage usage)
        {
            int count = 0;

            foreach (Resource binding in bindings)
            {
                if (binding.ResourceUsage == usage)
                {
                    resources[count++] = binding.MtlResource;
                }
            }

            if (count != 0)
            {
                computeCommandEncoder.UseResourcesCompat(resources, (ulong)count, usage);
            }
        }

        private readonly void SetRenderPipelineState(MTLRenderCommandEncoder renderCommandEncoder)
        {
            MTLRenderPipelineState pipelineState = _currentState.Pipeline.CreateRenderPipeline(_device, _currentState.FetchVariant ?? _currentState.RenderProgram);

            // Compilation failed (async shader compile not finished, or a genuinely
            // bad pipeline). Leave whatever is on the encoder alone and report the
            // state as unusable, so the caller skips the draw instead of letting the
            // Metal driver dereference a null pipeline inside drawPrimitives.
            _applied.PipelineValid = pipelineState.NativePtr != IntPtr.Zero;
            _lastPsoPtr = pipelineState.NativePtr;

            if (!_applied.PipelineValid)
            {
                return;
            }

            if (!_applied.Knows(renderCommandEncoder, AppliedRenderState.Field.Pipeline) ||
                _applied.PipelineState != pipelineState.NativePtr)
            {
                _applied.PipelineState = pipelineState.NativePtr;

                renderCommandEncoder.SetRenderPipelineState(pipelineState);
                CrashRing.PsoSet(pipelineState.NativePtr, renderCommandEncoder.NativePtr, CommandBufferEncoder.RenderEncoderGeneration, _currentState.RenderProgram?.DebugLabel);
            }

            // The blend colour is not part of the pipeline object, but every dirty
            // flag that rebuilds the pipeline used to resend it too - by far the most
            // repeated redundant encoder call in a frame.
            if (!_applied.Knows(renderCommandEncoder, AppliedRenderState.Field.BlendColor) ||
                _applied.BlendColor != _currentState.BlendColor)
            {
                _applied.BlendColor = _currentState.BlendColor;

                renderCommandEncoder.SetBlendColor(
                    _currentState.BlendColor.Red,
                    _currentState.BlendColor.Green,
                    _currentState.BlendColor.Blue,
                    _currentState.BlendColor.Alpha);
            }
        }

        private readonly void SetComputePipelineState(MTLComputeCommandEncoder computeCommandEncoder)
        {
            if (_currentState.ComputeProgram == null)
            {
                return;
            }

            MTLComputePipelineState pipelineState = PipelineState.CreateComputePipeline(_device, _currentState.ComputeProgram);

            computeCommandEncoder.SetComputePipelineState(pipelineState);
        }

        public readonly void UpdateIndexBuffer(BufferRange buffer, IndexType type)
        {
            if (buffer.Handle != BufferHandle.Null)
            {
                _currentState.IndexBuffer = new IndexBufferState(buffer.Handle, buffer.Offset, buffer.Size, type);
            }
            else
            {
                _currentState.IndexBuffer = IndexBufferState.Null;
            }
        }

        public readonly void UpdatePrimitiveTopology(PrimitiveTopology topology)
        {
            _currentState.Topology = topology;
        }

        public readonly void UpdateProgram(IProgram program)
        {
            Program prg = (Program)program;

            // A program from the shader cache that the background workers have not reached
            // yet is compiled now.
            prg.EnsureCompiled(background: false);

            if (prg.IsCompiling)
            {
                // Bound before its shaders have compiled, which only "Compile shaders in the background" allows. A
                // graphics program is bound anyway and its draws are skipped until it is ready (see
                // PipelineState.CreateRenderPipeline); a compute program's results are read later, so wait for it.
                if (AsyncShaders.Enabled && !prg.IsCompute)
                {
                    BindRenderProgram(prg);

                    return;
                }

                long ledger = StallLedger.Begin();
                prg.CheckProgramLink(true);
                StallLedger.End(StallKind.RenderProgramWait, ledger);
            }

            if (prg.VertexFunction == IntPtr.Zero && prg.ComputeFunction == IntPtr.Zero)
            {
                if (prg.FragmentFunction == IntPtr.Zero)
                {
                    Logger.Error?.PrintMsg(LogClass.Gpu, "No compute function");
                }
                else
                {
                    Logger.Error?.PrintMsg(LogClass.Gpu, "No vertex function");
                }
                return;
            }

            if (prg.VertexFunction != IntPtr.Zero)
            {
                BindRenderProgram(prg);
            }
            else if (prg.ComputeFunction != IntPtr.Zero)
            {
                _currentState.ComputeProgram = prg;

                SignalDirty(DirtyFlags.ComputePipeline | DirtyFlags.ArgBuffers);
            }
        }

        private readonly void BindRenderProgram(Program prg)
        {
            _currentState.RenderProgram = prg;
            _currentState.FetchVariant = null;
            _currentState.DrawRingCb1Address = 0;
            _currentState.DrawRingCb3Address = 0;
            _currentState.DrawRingTex8ResourceId = 0;
            _currentState.DrawRingTexAResourceId = 0;

            SignalDirty(DirtyFlags.RenderPipeline | DirtyFlags.ArgBuffers);
        }

        public readonly void UpdateRasterizerDiscard(bool discard)
        {
            _currentState.Pipeline.RasterizerDiscardEnable = discard;

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public readonly void UpdateRenderTargets(Span<ITexture> colors, ITexture depthStencil)
        {
            _currentState.FramebufferUsingColorWriteMask = false;
            UpdateRenderTargetsInternal(colors, depthStencil);
        }

        public readonly void UpdateRenderTargetColorMasks(ReadOnlySpan<uint> componentMask)
        {
            ref Array8<ColorBlendStateUid> blendState = ref _currentState.Pipeline.Internal.ColorBlendState;

            for (int i = 0; i < componentMask.Length; i++)
            {
                bool red = (componentMask[i] & (0x1 << 0)) != 0;
                bool green = (componentMask[i] & (0x1 << 1)) != 0;
                bool blue = (componentMask[i] & (0x1 << 2)) != 0;
                bool alpha = (componentMask[i] & (0x1 << 3)) != 0;

                MTLColorWriteMask mask = MTLColorWriteMask.None;

                mask |= red ? MTLColorWriteMask.Red : 0;
                mask |= green ? MTLColorWriteMask.Green : 0;
                mask |= blue ? MTLColorWriteMask.Blue : 0;
                mask |= alpha ? MTLColorWriteMask.Alpha : 0;

                ref ColorBlendStateUid mtlBlend = ref blendState[i];

                // When color write mask is 0, remove all blend state to help the pipeline cache.
                // Restore it when the mask becomes non-zero.
                if (mtlBlend.WriteMask != mask)
                {
                    if (mask == 0)
                    {
                        _currentState.StoredBlend[i] = mtlBlend;

                        mtlBlend.Swap(new ColorBlendStateUid());
                    }
                    else if (mtlBlend.WriteMask == 0)
                    {
                        mtlBlend.Swap(_currentState.StoredBlend[i]);
                    }
                }

                blendState[i].WriteMask = mask;
            }

            if (_currentState.FramebufferUsingColorWriteMask)
            {
                UpdateRenderTargetsInternal(_currentState.PreMaskRenderTargets, _currentState.PreMaskDepthStencil);
            }
            else
            {
                // The write mask lives in the render pipeline descriptor, not the render
                // pass descriptor, and it is part of PipelineUid, so a new pipeline object
                // is all a change needs. Ending the pass was only a roundabout way of
                // forcing that rebuild, and it cost a full attachment store and reload
                // every time the guest changed a mask - 43 render passes a frame.
                if (_endPassOnColorMask && _pipeline.CurrentEncoderType == EncoderType.Render)
                {
                    _pipeline.EndCurrentPass(PassEndReason.ColorMask);
                }
                else
                {
                    SignalDirty(DirtyFlags.RenderPipeline);
                }
            }
        }

        // A/B switch for the above: RYUJINX_METAL_END_PASS_ON_COLOR_MASK=1 restores the
        // pass split.
        private static readonly bool _endPassOnColorMask =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_END_PASS_ON_COLOR_MASK") == "1";

        private readonly void UpdateRenderTargetsInternal(Span<ITexture> colors, ITexture depthStencil)
        {
            // TBDR GPUs don't work properly if the same attachment is bound to multiple targets,
            // due to each attachment being a copy of the real attachment, rather than a direct write.
            //
            // Just try to remove duplicate attachments.
            // Save a copy of the array to rebind when mask changes.

            // Look for textures that are masked out.

            ref PipelineState pipeline = ref _currentState.Pipeline;
            ref Array8<ColorBlendStateUid> blendState = ref pipeline.Internal.ColorBlendState;

            pipeline.ColorBlendAttachmentStateCount = (uint)colors.Length;

            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i] == null)
                {
                    continue;
                }

                MTLColorWriteMask mtlMask = blendState[i].WriteMask;

                for (int j = 0; j < i; j++)
                {
                    // Check each binding for a duplicate binding before it.
                    //
                    // By storage, not by reference: two views of one storage are different
                    // ITexture objects, so reference equality misses exactly the case this
                    // dedup exists for - and with both slots left attached, the fragment
                    // shader stores a defined value through one and an undefined value
                    // through the other, into the same memory. That is the profile of the
                    // TOTK white flash measured on 2026-08-09: intermittent, invisible to
                    // every identity-keyed probe, no copy, no clear, no compute, and
                    // landing exactly at the zero-draw mask passes at the end of the frame.
                    bool sameStorage = colors[i] == colors[j] ||
                        (!_dedupByReferenceOnly &&
                            colors[i] is Texture ti && colors[j] is Texture tj &&
                            ti.CanonicalPtr != IntPtr.Zero && ti.CanonicalPtr == tj.CanonicalPtr);

                    if (sameStorage)
                    {
                        // Prefer the binding with no write mask.

                        MTLColorWriteMask mtlMask2 = blendState[j].WriteMask;

                        if (mtlMask == 0)
                        {
                            colors[i] = null;
                            MaskOut(colors, depthStencil);
                        }
                        else if (mtlMask2 == 0)
                        {
                            colors[j] = null;
                            MaskOut(colors, depthStencil);
                        }
                    }
                }
            }

            _currentState.RenderTargets = new Texture[Constants.MaxColorAttachments];

            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i] is not Texture tex)
                {
                    blendState[i].PixelFormat = MTLPixelFormat.Invalid;

                    continue;
                }

                blendState[i].PixelFormat = tex.GetHandle().PixelFormat; // TODO: cache this
                _currentState.RenderTargets[i] = tex;
            }

            if (depthStencil is Texture depthTexture)
            {
                pipeline.DepthStencilFormat = depthTexture.GetHandle().PixelFormat; // TODO: cache this
                _currentState.DepthStencil = depthTexture;
            }
            else if (depthStencil == null)
            {
                pipeline.DepthStencilFormat = MTLPixelFormat.Invalid;
                _currentState.DepthStencil = null;
            }

            // Measured: the attachment set really does change on all 116 of these a
            // frame, so there is no redundant case here to skip.
            if (_pipeline.CurrentEncoderType == EncoderType.Render)
            {
                _pipeline.EndCurrentPass(PassEndReason.RenderTargets);
            }
        }

        private readonly void MaskOut(Span<ITexture> colors, ITexture depthStencil)
        {
            if (!_currentState.FramebufferUsingColorWriteMask)
            {
                _currentState.PreMaskRenderTargets = colors.ToArray();
                _currentState.PreMaskDepthStencil = depthStencil;
            }

            // If true, then the framebuffer must be recreated when the mask changes.
            _currentState.FramebufferUsingColorWriteMask = true;
        }

        public readonly void UpdateVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs)
        {
            vertexAttribs.CopyTo(_currentState.VertexAttribs);

            // Update the buffers on the pipeline
            UpdatePipelineVertexState(_currentState.VertexBuffers, _currentState.VertexAttribs);

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public readonly void UpdateBlendDescriptors(int index, BlendDescriptor blend)
        {
            ref ColorBlendStateUid blendState = ref _currentState.Pipeline.Internal.ColorBlendState[index];

            blendState.Enable = blend.Enable;
            blendState.AlphaBlendOperation = blend.AlphaOp.Convert();
            blendState.RgbBlendOperation = blend.ColorOp.Convert();
            blendState.SourceAlphaBlendFactor = blend.AlphaSrcFactor.Convert();
            blendState.DestinationAlphaBlendFactor = blend.AlphaDstFactor.Convert();
            blendState.SourceRGBBlendFactor = blend.ColorSrcFactor.Convert();
            blendState.DestinationRGBBlendFactor = blend.ColorDstFactor.Convert();

            if (blendState.WriteMask == 0)
            {
                _currentState.StoredBlend[index] = blendState;

                blendState.Swap(new ColorBlendStateUid());
            }

            _currentState.BlendColor = blend.BlendConstant;

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public void UpdateStencilState(StencilTestDescriptor stencilTest)
        {
            ref DepthStencilUid uid = ref _currentState.DepthStencilUid;

            uid.FrontFace = new StencilUid
            {
                StencilFailureOperation = stencilTest.FrontSFail.Convert(),
                DepthFailureOperation = stencilTest.FrontDpFail.Convert(),
                DepthStencilPassOperation = stencilTest.FrontDpPass.Convert(),
                StencilCompareFunction = stencilTest.FrontFunc.Convert(),
                ReadMask = (uint)stencilTest.FrontFuncMask,
                WriteMask = (uint)stencilTest.FrontMask
            };

            uid.BackFace = new StencilUid
            {
                StencilFailureOperation = stencilTest.BackSFail.Convert(),
                DepthFailureOperation = stencilTest.BackDpFail.Convert(),
                DepthStencilPassOperation = stencilTest.BackDpPass.Convert(),
                StencilCompareFunction = stencilTest.BackFunc.Convert(),
                ReadMask = (uint)stencilTest.BackFuncMask,
                WriteMask = (uint)stencilTest.BackMask
            };

            uid.StencilTestEnabled = stencilTest.TestEnable;

            UpdateStencilRefValue(stencilTest.FrontFuncRef, stencilTest.BackFuncRef);

            SignalDirty(DirtyFlags.DepthStencil);
        }

        /// <summary>
        /// Diagnostic: RYUJINX_METAL_RELAX_DEPTH_EQUAL replaces the Equal depth
        /// comparison with the named one (Always / LessEqual). Geometry drawn with
        /// Equal is shaded in a second pass that must match a depth prepass exactly;
        /// relaxing the comparison tells "the prepass never wrote this depth" apart
        /// from "the geometry never reached the rasteriser".
        /// </summary>
        private static readonly MTLCompareFunction? _relaxDepthEqual =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_RELAX_DEPTH_EQUAL") switch
            {
                "always" or "1" => MTLCompareFunction.Always,
                "lessequal" or "lequal" => MTLCompareFunction.LessEqual,
                _ => null,
            };

        public readonly void UpdateDepthState(DepthTestDescriptor depthTest)
        {
            ref DepthStencilUid uid = ref _currentState.DepthStencilUid;

            MTLCompareFunction compare = depthTest.TestEnable ? depthTest.Func.Convert() : MTLCompareFunction.Always;

            if (_relaxDepthEqual.HasValue && compare == MTLCompareFunction.Equal)
            {
                compare = _relaxDepthEqual.Value;
            }

            uid.DepthCompareFunction = compare;
            uid.DepthWriteEnabled = depthTest.TestEnable && depthTest.WriteEnable;

            SignalDirty(DirtyFlags.DepthStencil);
        }

        public readonly void UpdateDepthClamp(bool clamp)
        {
            _currentState.DepthClipMode = clamp ? MTLDepthClipMode.Clamp : MTLDepthClipMode.Clip;

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetDepthClamp(renderCommandEncoder);
                return;
            }

            SignalDirty(DirtyFlags.DepthClamp);
        }

        public readonly void UpdateDepthBias(float depthBias, float slopeScale, float clamp)
        {
            _currentState.DepthBias = depthBias;
            _currentState.SlopeScale = slopeScale;
            _currentState.Clamp = clamp;

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetDepthBias(renderCommandEncoder);
                return;
            }

            SignalDirty(DirtyFlags.DepthBias);
        }

        public readonly void UpdateLogicOpState(bool enable, LogicalOp op)
        {
            _currentState.Pipeline.LogicOpEnable = enable;
            _currentState.Pipeline.LogicOp = op.Convert();

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public readonly void UpdateMultisampleState(MultisampleDescriptor multisample)
        {
            _currentState.Pipeline.AlphaToCoverageEnable = multisample.AlphaToCoverageEnable;
            _currentState.Pipeline.AlphaToOneEnable = multisample.AlphaToOneEnable;

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public void UpdateScissors(ReadOnlySpan<Rectangle<int>> regions)
        {
            for (int i = 0; i < regions.Length; i++)
            {
                Rectangle<int> region = regions[i];

                _currentState.Scissors[i] = new MTLScissorRect
                {
                    height = (ulong)region.Height,
                    width = (ulong)region.Width,
                    x = (ulong)region.X,
                    y = (ulong)region.Y
                };
            }

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetScissors(renderCommandEncoder);
                return;
            }

            SignalDirty(DirtyFlags.Scissors);
        }

        public void UpdateViewports(ReadOnlySpan<Viewport> viewports)
        {
            static float Clamp(float value)
            {
                return Math.Clamp(value, 0f, 1f);
            }

            for (int i = 0; i < viewports.Length; i++)
            {
                Viewport viewport = viewports[i];
                // Y coordinate is inverted
                _currentState.Viewports[i] = new MTLViewport
                {
                    originX = viewport.Region.X,
                    originY = viewport.Region.Y + viewport.Region.Height,
                    width = viewport.Region.Width,
                    height = -viewport.Region.Height,
                    znear = Clamp(viewport.DepthNear),
                    zfar = Clamp(viewport.DepthFar)
                };
            }

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetViewports(renderCommandEncoder);
                return;
            }

            SignalDirty(DirtyFlags.Viewports);
        }

        public readonly void UpdateVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers)
        {
            for (int i = 0; i < Constants.MaxVertexBuffers; i++)
            {
                if (i < vertexBuffers.Length)
                {
                    VertexBufferDescriptor vertexBuffer = vertexBuffers[i];

                    _currentState.VertexBuffers[i] = new VertexBufferState(
                        vertexBuffer.Buffer.Handle,
                        vertexBuffer.Buffer.Offset,
                        vertexBuffer.Buffer.Size,
                        vertexBuffer.Divisor,
                        vertexBuffer.Stride);
                }
                else
                {
                    _currentState.VertexBuffers[i] = VertexBufferState.Null;
                }
            }

            // Update the buffers on the pipeline
            UpdatePipelineVertexState(_currentState.VertexBuffers, _currentState.VertexAttribs);

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public readonly void UpdateUniformBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            foreach (BufferAssignment assignment in buffers)
            {
                BufferRange buffer = assignment.Range;
                int index = assignment.Binding;

                Auto<DisposableBuffer> mtlBuffer = buffer.Handle == BufferHandle.Null
                    ? null
                    : _bufferManager.GetBuffer(buffer.Handle, buffer.Write);

                _currentState.UniformBufferRefs[index] = new BufferRef(mtlBuffer, ref buffer);
            }

            SignalDirty(DirtyFlags.Uniforms);
        }

        public readonly void UpdateStorageBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            foreach (BufferAssignment assignment in buffers)
            {
                BufferRange buffer = assignment.Range;
                int index = assignment.Binding;

                Auto<DisposableBuffer> mtlBuffer = buffer.Handle == BufferHandle.Null
                    ? null
                    : _bufferManager.GetBuffer(buffer.Handle, buffer.Write);

                _currentState.StorageBufferRefs[index] = new BufferRef(mtlBuffer, ref buffer);
            }

            SignalDirty(DirtyFlags.Storages);
        }

        public readonly void UpdateStorageBuffers(int first, ReadOnlySpan<Auto<DisposableBuffer>> buffers)
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                Auto<DisposableBuffer> mtlBuffer = buffers[i];
                int index = first + i;

                _currentState.StorageBufferRefs[index] = new BufferRef(mtlBuffer);
            }

            SignalDirty(DirtyFlags.Storages);
        }

        public void UpdateCullMode(bool enable, Face face)
        {
            // Metal has no "cull everything" mode, so culling both faces is emulated
            // with an empty scissor rect. The guest's cull face register keeps its
            // value while culling is disabled, so that emulation must only kick in
            // when culling is actually enabled - otherwise geometry drawn double
            // sided (foliage, in particular) is silently scissored away entirely.
            bool cullBoth = enable && face == Face.FrontAndBack;

            bool dirtyScissor = cullBoth != _currentState.CullBoth;

            _currentState.CullMode = enable ? face.Convert() : MTLCullMode.None;
            _currentState.CullBoth = cullBoth;

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetCullMode(renderCommandEncoder);
                SetScissors(renderCommandEncoder);
                return;
            }

            // Mark dirty
            SignalDirty(DirtyFlags.CullMode);

            if (dirtyScissor)
            {
                SignalDirty(DirtyFlags.Scissors);
            }
        }

        public readonly void UpdateFrontFace(FrontFace frontFace)
        {
            _currentState.Winding = frontFace.Convert();

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetFrontFace(renderCommandEncoder);
                return;
            }

            SignalDirty(DirtyFlags.FrontFace);
        }

        private readonly void UpdateStencilRefValue(int frontRef, int backRef)
        {
            _currentState.FrontRefValue = frontRef;
            _currentState.BackRefValue = backRef;

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetStencilRefValue(renderCommandEncoder);
            }

            SignalDirty(DirtyFlags.StencilRef);
        }

        public readonly void UpdateTextureAndSampler(ShaderStage stage, int binding, TextureBase texture, SamplerHolder samplerHolder)
        {
            if (texture != null)
            {
                _currentState.TextureRefs[binding] = new(stage, texture, samplerHolder?.GetSampler());

                if (texture is Texture sampled)
                {
                    StoreLiveness.NoteRead(sampled.CanonicalPtr);
                }
            }
            else
            {
                _currentState.TextureRefs[binding] = default;
            }

            SignalDirty(DirtyFlags.Textures);
        }

        public readonly void UpdateImage(ShaderStage stage, int binding, TextureBase image)
        {
            if (image is Texture view)
            {
                _currentState.ImageRefs[binding] = new(stage, view);
                StoreLiveness.NoteRead(view.CanonicalPtr);
            }
            else
            {
                _currentState.ImageRefs[binding] = default;
            }

            SignalDirty(DirtyFlags.Images);
        }

        public readonly void UpdateTextureArray(ShaderStage stage, int binding, TextureArray array)
        {
            ref EncoderState.ArrayRef<TextureArray> arrayRef = ref GetArrayRef(ref _currentState.TextureArrayRefs, binding, ArrayGrowthSize);

            if (arrayRef.Stage != stage || arrayRef.Array != array)
            {
                arrayRef = new EncoderState.ArrayRef<TextureArray>(stage, array);

                SignalDirty(DirtyFlags.Textures);
            }
        }

        public readonly void UpdateTextureArraySeparate(ShaderStage stage, int setIndex, TextureArray array)
        {
            ref EncoderState.ArrayRef<TextureArray> arrayRef = ref GetArrayRef(ref _currentState.TextureArrayExtraRefs, setIndex - MetalRenderer.TotalSets);

            if (arrayRef.Stage != stage || arrayRef.Array != array)
            {
                arrayRef = new EncoderState.ArrayRef<TextureArray>(stage, array);

                SignalDirty(DirtyFlags.Textures);
            }
        }

        public readonly void UpdateImageArray(ShaderStage stage, int binding, ImageArray array)
        {
            ref EncoderState.ArrayRef<ImageArray> arrayRef = ref GetArrayRef(ref _currentState.ImageArrayRefs, binding, ArrayGrowthSize);

            if (arrayRef.Stage != stage || arrayRef.Array != array)
            {
                arrayRef = new EncoderState.ArrayRef<ImageArray>(stage, array);

                SignalDirty(DirtyFlags.Images);
            }
        }

        public readonly void UpdateImageArraySeparate(ShaderStage stage, int setIndex, ImageArray array)
        {
            ref EncoderState.ArrayRef<ImageArray> arrayRef = ref GetArrayRef(ref _currentState.ImageArrayExtraRefs, setIndex - MetalRenderer.TotalSets);

            if (arrayRef.Stage != stage || arrayRef.Array != array)
            {
                arrayRef = new EncoderState.ArrayRef<ImageArray>(stage, array);

                SignalDirty(DirtyFlags.Images);
            }
        }

        private static ref EncoderState.ArrayRef<T> GetArrayRef<T>(ref EncoderState.ArrayRef<T>[] array, int index, int growthSize = 1)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);

            if (array.Length <= index)
            {
                Array.Resize(ref array, index + growthSize);
            }

            return ref array[index];
        }

        private readonly void SetDepthStencilState(MTLRenderCommandEncoder renderCommandEncoder)
        {
            MTLDepthStencilState state = DepthStencil != null
                ? _depthStencilCache.GetOrCreate(_currentState.DepthStencilUid)
                : _defaultState;

            if (!_applied.Knows(renderCommandEncoder, AppliedRenderState.Field.DepthStencil) ||
                _applied.DepthStencilState != state.NativePtr)
            {
                _applied.DepthStencilState = state.NativePtr;

                renderCommandEncoder.SetDepthStencilState(state);
            }
        }

        private readonly void SetDepthClamp(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if (!_applied.Knows(renderCommandEncoder, AppliedRenderState.Field.DepthClip) ||
                _applied.DepthClipMode != _currentState.DepthClipMode)
            {
                _applied.DepthClipMode = _currentState.DepthClipMode;

                renderCommandEncoder.SetDepthClipMode(_currentState.DepthClipMode);
            }
        }

        private readonly void SetDepthBias(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if (!_applied.Knows(renderCommandEncoder, AppliedRenderState.Field.DepthBias) ||
                _applied.DepthBias != _currentState.DepthBias ||
                _applied.SlopeScale != _currentState.SlopeScale ||
                _applied.Clamp != _currentState.Clamp)
            {
                _applied.DepthBias = _currentState.DepthBias;
                _applied.SlopeScale = _currentState.SlopeScale;
                _applied.Clamp = _currentState.Clamp;

                renderCommandEncoder.SetDepthBias(_currentState.DepthBias, _currentState.SlopeScale, _currentState.Clamp);
            }
        }

        // The size of the pass actually being encoded, recorded where the descriptor is
        // built. Deriving it from _currentState instead was wrong twice over: the state
        // can hold targets bound for an earlier pass, and for passes the helper shaders
        // build it holds none at all - in which case the fallback returned ulong.MaxValue
        // and the scissor clamp below became a no-op. Metal's validation layer caught it
        // immediately: 65535x65535 scissor rects against a 1x1 render pass.
        private static ulong _passWidth;
        private static ulong _passHeight;

        internal static void NotePassSize(ulong width, ulong height)
        {
            _passWidth = width;
            _passHeight = height;
        }

        private readonly (ulong Width, ulong Height) GetRenderPassSize()
        {
            if (_passWidth != 0 && _passHeight != 0)
            {
                return (_passWidth, _passHeight);
            }

            for (int i = 0; i < _currentState.RenderTargets.Length; i++)
            {
                if (_currentState.RenderTargets[i] is Texture tex)
                {
                    return ((ulong)tex.Width, (ulong)tex.Height);
                }
            }

            if (_currentState.DepthStencil != null)
            {
                return ((ulong)_currentState.DepthStencil.Width, (ulong)_currentState.DepthStencil.Height);
            }

            // Reaching here means the pass size is unknown from both the descriptor and
            // the bound state, and the caller will then clamp against ulong.MaxValue,
            // which is no clamp at all. Metal's validation layer still reports 65535
            // scissor rects against a 1x1 pass, so one path gets here; name it rather
            // than guess. Once per distinct shape.
            if (_unknownPassSizeReported.Add((_currentState.Scissors.Length, _currentState.RenderTargets.Length)))
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"pass size unknown at scissor time: targets={_currentState.RenderTargets.Length} " +
                    $"depth={(_currentState.DepthStencil != null ? "yes" : "no")} " +
                    $"scissors={_currentState.Scissors.Length} " +
                    $"program={_currentState.RenderProgram?.DebugLabel ?? "<none>"}");
            }

            return (ulong.MaxValue, ulong.MaxValue);
        }

        private static readonly HashSet<(int, int)> _unknownPassSizeReported = [];

        private unsafe void SetScissors(MTLRenderCommandEncoder renderCommandEncoder)
        {
            bool isTriangles = (_currentState.Topology == PrimitiveTopology.Triangles) ||
                               (_currentState.Topology == PrimitiveTopology.TriangleStrip);

            if (_currentState.CullBoth && isTriangles)
            {
                renderCommandEncoder.SetScissorRect(new MTLScissorRect { x = 0, y = 0, width = 0, height = 0 });
            }
            else
            {
                int count = _currentState.Scissors.Length;

                if (count > 0)
                {
                    // Metal requires every scissor rect to lie within the render pass
                    // attachments. The guest freely uses full-surface sentinel values
                    // (65535x65535); passing them through is an API violation and
                    // undefined behaviour without the validation layer - observed as
                    // randomly dropped rasterization in small passes (impostor atlas
                    // bakes). Clamp at apply time against the current pass size; the
                    // unclamped guest values stay in _currentState for the next pass.
                    (ulong passWidth, ulong passHeight) = GetRenderPassSize();

                    // Unknown size means the clamp would run against ulong.MaxValue and
                    // pass the guest's 65535 sentinel straight through - which is what
                    // Metal's validation layer kept reporting against a 1x1 pass even
                    // after the size was taken from the descriptor, because the bound
                    // state is emptied again before this runs. Leaving the scissor unset
                    // is always legal and means the whole attachment, which is what a
                    // full-surface sentinel was asking for anyway.
                    if (passWidth == ulong.MaxValue)
                    {
                        return;
                    }

                    MTLScissorRect* clamped = stackalloc MTLScissorRect[count];

                    for (int i = 0; i < count; i++)
                    {
                        MTLScissorRect rect = _currentState.Scissors[i];

                        rect.x = Math.Min(rect.x, passWidth);
                        rect.y = Math.Min(rect.y, passHeight);
                        rect.width = Math.Min(rect.width, passWidth - rect.x);
                        rect.height = Math.Min(rect.height, passHeight - rect.y);

                        clamped[i] = rect;
                    }

                    renderCommandEncoder.SetScissorRects((IntPtr)clamped, (ulong)count);
                }
            }
        }

        private readonly unsafe void SetViewports(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if (_currentState.Viewports.Length > 0)
            {
                fixed (MTLViewport* pMtlViewports = _currentState.Viewports)
                {
                    renderCommandEncoder.SetViewports((IntPtr)pMtlViewports, (ulong)_currentState.Viewports.Length);
                }
            }
        }

        private readonly void UpdatePipelineVertexState(VertexBufferState[] bufferDescriptors, VertexAttribDescriptor[] attribDescriptors)
        {
            _currentState.Pipeline.UpdateVertexState(bufferDescriptors, attribDescriptors);
        }

        private readonly void SetVertexBuffers(VertexBufferState[] bufferStates, ref readonly RenderEncoderBindings bindings)
        {
            for (int i = 0; i < bufferStates.Length; i++)
            {
                (MTLBuffer mtlBuffer, int offset) = bufferStates[i].GetVertexBuffer(_bufferManager, _pipeline.Cbs);

                if (mtlBuffer.NativePtr != IntPtr.Zero)
                {
                    bindings.VertexBuffers.Add(new BufferResource(mtlBuffer, (ulong)offset, (ulong)i));

                    UploadCorrelator.NoteStageVertexBuffer(i, mtlBuffer, offset, (int)bufferStates[i].Stride);

                    // The UVs of the pass-through blit that writes the presented surface
                    // come from a vertex attribute, and on a white frame they are constant
                    // across the primitive. This photographs the buffer that attribute is
                    // fetched from, so a constant one shows up as a constant here.
                    if (i == 0 && _watchLabel.Length != 0 &&
                        _currentState.RenderProgram?.DebugLabel is string vlabel &&
                        vlabel.StartsWith(_watchLabel, StringComparison.Ordinal))
                    {
                        UploadCorrelator.NoteCompositeConstants(31, mtlBuffer.Contents, offset);
                        UploadCorrelator.NoteVertexSpread(mtlBuffer.Contents, offset, bufferStates[i].Stride);
                        UploadCorrelator.NoteVertexStride(bufferStates[i].Stride);
                    }
                }
            }

            Auto<DisposableBuffer> autoZeroBuffer = _zeroBuffer == BufferHandle.Null
                ? null
                : _bufferManager.GetBuffer(_zeroBuffer, false);

            if (autoZeroBuffer == null)
            {
                return;
            }

            MTLBuffer zeroMtlBuffer = autoZeroBuffer.Get(_pipeline.Cbs).Value;
            bindings.VertexBuffers.Add(new BufferResource(zeroMtlBuffer, 0, Constants.ZeroBufferIndex));
        }

        private readonly (ulong gpuAddress, IntPtr nativePtr) AddressForBuffer(ref BufferRef buffer)
        {
            ulong gpuAddress = 0;
            IntPtr nativePtr = IntPtr.Zero;

            BufferRange? range = buffer.Range;
            Auto<DisposableBuffer> autoBuffer = buffer.Buffer;

            if (autoBuffer != null)
            {
                int offset = 0;
                DisposableBuffer bound;

                if (range.HasValue)
                {
                    offset = range.Value.Offset;

                    if (range.Value.Write)
                    {
                        bound = autoBuffer.Get(_pipeline.Cbs, offset, range.Value.Size, true);
                    }
                    else
                    {
                        // A read only binding may be served from a mirror holding writes
                        // that have not been put into the buffer itself, which is what
                        // keeps those writes from needing a blit inside the render pass.
                        bound = autoBuffer.GetMirrorable(_pipeline.Cbs, ref offset, range.Value.Size, out _);
                    }
                }
                else
                {
                    bound = autoBuffer.Get(_pipeline.Cbs);
                }

                gpuAddress = bound.GpuAddress + (ulong)offset;
                nativePtr = bound.Value.NativePtr;
            }

            return (gpuAddress, nativePtr);
        }

        /// <summary>
        /// Which program's texture bindings the input report follows.
        ///
        /// It was hardcoded to 3ebc3a8f6b77cc8f - the tonemap - in three places, while the
        /// shader being measured in-shader all session is ee89b4e471373459, the composite.
        /// So every "WHITE inputs" line described a different program's bindings than the
        /// fetch measurement it was being read alongside, and the two were never about the
        /// same texture.
        /// </summary>
        private static readonly string _inputWatchLabel =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_INPUT_WATCH") ?? "3ebc3a8f6b77cc8f";

        // Hot-swappable so both arms run inside one session with everything else equal.
        // The first validation of this change compared arms that differed in their probe
        // environment as well, and the 0% it reported was the probes' doing, not the fix's
        // - the exact single-variable rule these notes keep re-learning.
        private static bool _dedupByReferenceOnly = true;

        internal static void RefreshDedupToggle()
        {
            // Default ON (reference-only, the original behaviour). The storage-identity
            // comparison measured no effect on the flash in a clean in-session A/B, and
            // with it active the game aborts in Metal validation on camera movement -
            // IOGPUMetalCommandBuffer validate -> MTLReportFailure -> abort - which is the
            // signature of an attachment nulled while the pipeline still declares it.
            // Opt back in with /tmp/ryujinx-metal-dedup-by-storage=1 for experiments only.
            try
            {
                _dedupByReferenceOnly = !(System.IO.File.Exists("/tmp/ryujinx-metal-dedup-by-storage") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-dedup-by-storage").Trim() == "1");
            }
            catch (System.IO.IOException)
            {
            }
        }

        private static bool _identitySampling;

        /// <summary>
        /// Declare residency on a sampled texture's parent rather than on the view.
        ///
        /// Sampled textures reach the shader through an argument buffer, so the encoder has
        /// to be told about them with useResource, and this backend passes the handle it
        /// bound - which for a view is a distinct MTLTexture from the one the render pass
        /// wrote as an attachment. Metal tracks residency and hazards against the
        /// underlying allocation, and Apple's guidance for texture views is to declare the
        /// parent; naming only the view can leave the dependency on those earlier writes
        /// unestablished.
        ///
        /// The composite's input is exactly that shape - the log shows handle 0xB910A4000
        /// against root 0xB9109BC00, so it is sampled through a view of the texture the
        /// scene passes wrote. And the failure this predicts is the one measured: reading
        /// content from before the writes landed, which is uniform, on a Metal backend
        /// only, intermittently, and beyond the reach of the guest's own barriers - the
        /// last of which was ruled out tonight at about 1300 samples an arm.
        ///
        /// The argument buffer still gets the view's GPU resource ID, so format and swizzle
        /// are unaffected. Only what is named for residency changes.
        ///
        /// Hot-swapped through /tmp/ryujinx-metal-parent-residency so both arms can be
        /// measured inside one reproduction window.
        /// </summary>
        private static bool _residencyOnParent;

        /// <summary>
        /// The verdict experiment for the zero-draw Load/Store pass, and its fix if it
        /// holds. Passes are created with StoreAction Unknown and the real store action is
        /// chosen when the pass ends: Store normally, DontCare when the pass encoded no
        /// draws and cleared nothing - a pass like that is a pure tile round trip, and the
        /// white flip interval contains exactly one of them and nothing else that writes.
        /// /tmp/ryujinx-metal-elide-empty-store, re-read once a frame; the per-pass flag
        /// remembers which convention the descriptor was built with, because a pass
        /// created with a fixed action must not be fixed up.
        /// </summary>
        private static bool _elideEmptyStore;

        /// <summary>
        /// The program whose sampled texture the correlator photographs. Defaults to the
        /// pass-through blit that writes the presented surface.
        /// </summary>
        private static readonly string _stageLabel = Environment.GetEnvironmentVariable("RYUJINX_METAL_STAGE_LABEL") ?? "";
        private static readonly string _watchLabel =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_WATCH_LABEL") ?? "480117";

        private static readonly int _redeclareEvery =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_REDECLARE"), out int rd) ? rd : 0;

        // The bound PSO's identity - the one observable at the blit draw never split by
        // outcome. A bad cached variant selected on a fifth of frames would leave every
        // other measurement correct.
        /// <summary>
        /// RYUJINX_METAL_SHADOW_BIND=1: bind every fragment texture to a spare direct slot
        /// as well. The bypass hypothesis at one twentieth of the cost - if the white comes
        /// from automatic tracking missing argument-buffer reads, forcing the tracker to
        /// see them ends it; if the white survives, the full codegen bypass is pointless.
        /// </summary>
        private static readonly bool _shadowBind =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_SHADOW_BIND") == "1";

        private static readonly bool _clearSrgb1080 =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_CLEAR_SRGB1080") == "1";

        private static readonly bool _canaryClear =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_CANARY_CLEAR") == "1";

        private static long _shadowBinds;
        private static long _shadowCollected;

        private static IntPtr _lastPsoPtr;
        public static IntPtr LastPso => _lastPsoPtr;

        private static bool _passStoreUnknown;
        private static ulong _passColorMask;
        private static bool _passHasDepth;
        private static bool _passHasStencil;
        private static bool _passCleared;

        // Whether this pass's colour target 0 is the scene-class texture the flash lives
        // on. Blanket elision wedged the guest - some empty pass elsewhere is load-bearing
        // - so the DontCare is confined to the class where the crystallisation was proven.
        private static bool _passSceneClass;

        internal static void RefreshSamplingToggle()
        {
            try
            {
                _identitySampling = System.IO.File.Exists("/tmp/ryujinx-metal-identity-sample") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-identity-sample").Trim() == "1";

                _residencyOnParent = System.IO.File.Exists("/tmp/ryujinx-metal-parent-residency") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-parent-residency").Trim() == "1";

                _elideEmptyStore = System.IO.File.Exists("/tmp/ryujinx-metal-elide-empty-store") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-elide-empty-store").Trim() == "1";
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; the next frame picks it up.
            }
        }

        /// <summary>
        /// Sampling a texture that is simultaneously a colour attachment is undefined in
        /// Metal, and undefined reads on this hardware come back near-white - the flash's
        /// exact signature. It also fits every other constraint at once: no write happens
        /// so every write hook is blind, it is intermittent because it depends on whether
        /// the guest's barrier was honoured, and MoltenVK does not expose it because
        /// Vulkan's own feedback rules force a different structure. This backend skips 89
        /// guest barriers per frame as no-ops, which is exactly the population that could
        /// leave a sampled texture still attached.
        /// </summary>
        /// <summary>
        /// Whether any texture currently bound for sampling is also an attachment of the
        /// pass that is open. Reads bound state only, so it can be answered before
        /// RenderResourcesPrepass runs - which is the whole point: the previous version
        /// answered it during the prepass and had to end the pass from inside encoder
        /// acquisition, which faulted the driver.
        /// </summary>
        public readonly bool SamplesOwnAttachment()
        {
            if (!FeedbackProbe.Enabled)
            {
                return false;
            }

            bool any = false;

            foreach (TextureRef reference in _currentState.TextureRefs)
            {
                if (reference.Storage is Texture sampled && FeedbackProbe.CheckAgainstLiveAttachments(sampled))
                {
                    any = true;
                }
            }

            return any;
        }

        /// <summary>
        /// Storage written as a colour attachment since the current command buffer began,
        /// and whether anything now bound for sampling is in that set.
        ///
        /// This is where the two backends part company. Ryujinx's Vulkan path issues real
        /// pipeline barriers, and MoltenVK has nowhere to put a read-after-write barrier
        /// inside a Metal render encoder, so it ends the encoder - Vulkan therefore splits
        /// the pass at every read-after-write, automatically. This backend issues no
        /// barriers at all and leans entirely on Metal's automatic hazard tracking, which
        /// for argument-buffer reads is fed by useResource rather than by anything the
        /// driver can see directly. Same machine, same driver, same save, argument buffers
        /// on in both: Vulkan 0 flat frames in 120 samples, Metal about 40%.
        ///
        /// The ledger's "strict barrier" test keyed on guest TextureBarrier calls, which
        /// this game issues rarely; this keys on the hazard actually occurring.
        /// </summary>
        private readonly Dictionary<IntPtr, SubRange> _writtenThisCb = new();
        private readonly HashSet<IntPtr> _reallyWrittenThisPass = new();
        // Attachments written by this pass before a guest texture barrier that the
        // deferred barrier mode did not act on. A later draw that samples one of them
        // is the barrier's real consumer and splits there (SamplesEarlierWrite).
        private readonly HashSet<IntPtr> _barrieredWrites = new();
        private static long _barriersDeferred;
        private static long _splitBarriered;

        /// <summary>
        /// Subresource span of a view within its storage. Instrument-only: the split
        /// predicate still fires at whole-texture granularity; these ranges only record
        /// what a finer predicate would have decided, so the classification below can
        /// say how many of the remaining splits a subresource-level write set would
        /// eliminate before anyone risks building one.
        /// </summary>
        public readonly struct SubRange
        {
            public static readonly SubRange Full = new(0, int.MaxValue, 0, int.MaxValue);

            public readonly int MinLevel;
            public readonly int MaxLevel;
            public readonly int MinLayer;
            public readonly int MaxLayer;

            public SubRange(int minLevel, int maxLevel, int minLayer, int maxLayer)
            {
                MinLevel = minLevel;
                MaxLevel = maxLevel;
                MinLayer = minLayer;
                MaxLayer = maxLayer;
            }

            public static SubRange Of(Texture view)
            {
                int levels = Math.Max(1, view.Info.Levels);
                int layers = Math.Max(1, view.Info.GetLayers());

                return new SubRange(view.FirstLevel, view.FirstLevel + levels - 1,
                    view.FirstLayer, view.FirstLayer + layers - 1);
            }

            public SubRange Union(SubRange other) => new(
                Math.Min(MinLevel, other.MinLevel),
                Math.Max(MaxLevel, other.MaxLevel),
                Math.Min(MinLayer, other.MinLayer),
                Math.Max(MaxLayer, other.MaxLayer));

            public bool Overlaps(SubRange other) =>
                MinLevel <= other.MaxLevel && other.MinLevel <= MaxLevel &&
                MinLayer <= other.MaxLayer && other.MinLayer <= MaxLayer;
        }

        private readonly record struct PassAttachment(IntPtr Ptr, SubRange Range, int Slot);

        // Split classification. Every raw split lands in exactly one bucket:
        //   hotSelf  - the draw samples an attachment of the very pass it joins; the one
        //              shape that cannot be ordered on Apple GPUs without ending the pass.
        //   hotOther - reads a texture written earlier in scope, subresources overlapping.
        //   subres   - every match reads mips/layers the writes never touched; a
        //              subresource-granular write set would not have split here.
        //   legacy   - fired by the whole-table fallback, unclassified.
        /// <summary>
        /// What the read-after-write scan found for the draw about to be encoded.
        /// SelfOnly means every overlapping match is an attachment of the pass the draw
        /// is joining - on NVN this exact read carries no barrier and is served stale
        /// data, which is the behaviour the game shipped against. Hazard is everything
        /// else that overlaps, and what the legacy whole-table walk reports, since it
        /// cannot classify.
        /// </summary>
        public enum RawHazard : byte
        {
            None,
            SelfOnly,
            Hazard,
            // Every hazard is a same-pixel read of a current attachment that a
            // framebuffer-fetch variant can serve from tile memory without a split.
            Fetchable,
        }

        private static long _splitHotSelf;
        private static long _splitHotOther;
        private static long _splitSubres;
        private static long _splitLegacy;
        private static long _splitSelfSkipped;
        private static long _splitOtherUnwritten;
        private static long _splitSelfUnwritten;
        private static long _splitFetchable;
        private static long _splitFetchRefusedWriter;
        private static long _splitFetched;
        private static long _fetchPending;
        // The fetch plan of the draw being classified: which fragment texture bindings
        // alias which colour attachment slot. Static like the rest of the classification
        // state; the backend is single-threaded on the render thread.
        private static int _snapshotSrc = -1;
        private static int _snapshotDst = -1;
        private static bool _fetchPlanNeedsSnapshot;
        private static long _splitSnapshot;
        public static bool FetchPlanNeedsSnapshot => _fetchPlanNeedsSnapshot;
        private static readonly FetchBinding[] _fetchPlan = new FetchBinding[3];
        private static int _fetchPlanCount;
        private static bool _fetchPlanHasSkippableSelf;
        public static ReadOnlySpan<FetchBinding> FetchPlan => _fetchPlan.AsSpan(0, _fetchPlanCount);
        public static bool FetchPlanHasSkippableSelf => _fetchPlanHasSkippableSelf;
        public static void NoteFetchServed() => _splitFetched++;
        public static void NoteFetchPending() => _fetchPending++;
        private static readonly Dictionary<string, int> _otherLabels = new();
        private static readonly Dictionary<(MTLPixelFormat Format, bool Skippable, bool Written), int> _hazardTextures = new();

        private static void NoteHazardTexture(MTLPixelFormat format, bool skippable, bool written)
        {
            (MTLPixelFormat, bool, bool) key = (format, skippable, written);
            _hazardTextures.TryGetValue(key, out int n);
            _hazardTextures[key] = n + 1;
        }
        private static int _attachFaults;

        /// <summary>
        /// Counts a SelfOnly hazard that was allowed through without a split, and taints
        /// the pass's attachments: their contents were computed from a stale read, which
        /// is fine for pictures but wrong for anything the guest later reads back. The
        /// 19:56 guest crash dereferenced a pointer whose bits were two floats - the
        /// shape of game logic consuming bad data - and whether skipped output can even
        /// reach guest memory is exactly what the taint log answers.
        /// </summary>
        public void NoteSelfSkipAndTaint()
        {
            _splitSelfSkipped++;

            for (int i = 0; i < _passAttachments.Count; i++)
            {
                TaintedStaleFrames[_passAttachments[i].Ptr] = _splitSelfSkipped;
            }
        }

        public static void NoteSelfSkip() => _splitSelfSkipped++;

        /// <summary>Attachments of passes that encoded a stale self-read, by CanonicalPtr.</summary>
        internal static readonly Dictionary<IntPtr, long> TaintedStaleFrames = new();

        private static int _taintReadbackLogs;

        /// <summary>Called from the readback path: the guest is about to receive this texture's bytes.</summary>
        public static void NoteReadback(IntPtr canonicalPtr, string identity)
        {
            if (TaintedStaleFrames.ContainsKey(canonicalPtr) && _taintReadbackLogs++ < 20)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"tainted readback: guest reads {identity} whose content was rendered from a stale self-read\n{Environment.StackTrace}");
            }
        }

        // A few concrete hotSelf splits per stats window: which program, what raster
        // state, what it sampled. The classification says the remaining split load is
        // almost entirely draws sampling their own pass's attachment; whether the fix is
        // a framebuffer-fetch rewrite (same-pixel reads), Switch-semantics tolerance
        // (stale reads the guest hardware also served), or nothing (fresh data really
        // needed) depends entirely on which shaders these are.
        private const int MaxSelfSamples = 4;
        private static readonly string[] _selfSamples = new string[MaxSelfSamples];
        private static int _selfSampleCount;
        private static string _selfSampleTex = string.Empty;
        private static readonly Dictionary<(int Width, int Height, MTLPixelFormat Format), int> _splitShapes = new();

        private static void NoteSplitShape(Texture sampled)
        {
            (int, int, MTLPixelFormat) key = (sampled.Width, sampled.Height, sampled.MtlFormat);
            _splitShapes.TryGetValue(key, out int n);
            _splitShapes[key] = n + 1;
        }

        /// <summary>
        /// Snapshot and reset the split classification, formatted for the stats line.
        /// Null when the window recorded nothing.
        /// </summary>
        public static string TakeSplitClasses(int frames)
        {
            long self = _splitHotSelf, other = _splitHotOther, subres = _splitSubres, legacy = _splitLegacy, skipped = _splitSelfSkipped;
            long otherUnwritten = _splitOtherUnwritten, selfUnwritten = _splitSelfUnwritten;
            long fetchable = _splitFetchable, fetched = _splitFetched, fetchPending = _fetchPending, fetchWriter = _splitFetchRefusedWriter;
            _splitFetchRefusedWriter = 0;
            long storageWrites = _storageWrites, storageRawDraws = _storageRawDraws, storageBarriersKept = _storageBarriersKept, storageRawBarriers = _storageRawBarriers;
            long snapshotServed = _splitSnapshot, snapshotDispatches = TileSnapshot.Dispatches;
            long barriersDeferred = _barriersDeferred, splitBarriered = _splitBarriered;
            _barriersDeferred = _splitBarriered = 0;
            _splitSnapshot = 0;
            TileSnapshot.Dispatches = 0;
            _storageWrites = _storageRawDraws = _storageBarriersKept = _storageRawBarriers = 0;
            _splitHotSelf = _splitHotOther = _splitSubres = _splitLegacy = _splitSelfSkipped = 0;
            _splitOtherUnwritten = _splitSelfUnwritten = _splitFetchable = _splitFetched = _fetchPending = 0;

            if (self + other + subres + legacy + skipped + fetchable == 0)
            {
                _splitShapes.Clear();
                _otherLabels.Clear();
                _hazardTextures.Clear();
                return null;
            }

            string shapes = string.Join(", ", _splitShapes.OrderByDescending(kv => kv.Value).Take(3)
                .Select(kv => $"{kv.Key.Width}x{kv.Key.Height}/{kv.Key.Format}={kv.Value / frames}"));

            _splitShapes.Clear();

            string samples = string.Empty;

            if (_selfSampleCount > 0)
            {
                samples = " hotSelf samples: " + string.Join(" | ", _selfSamples.Take(_selfSampleCount)) + ".";
                _selfSampleCount = 0;
            }

            // hazard textures: format(w=really written this pass|u=unwritten, s=skippable format|d=data format)=matches per frame
            string hazardTex = string.Join(", ", _hazardTextures.OrderByDescending(kv => kv.Value).Take(5)
                .Select(kv => $"{kv.Key.Format}({(kv.Key.Written ? "w" : "u")}{(kv.Key.Skippable ? "s" : "d")})={kv.Value / frames}"));
            _hazardTextures.Clear();
            string otherLabels = string.Join(", ", _otherLabels.OrderByDescending(kv => kv.Value).Take(5)
                .Select(kv => $"{kv.Key}={kv.Value / frames}"));
            _otherLabels.Clear();

            return $" rawsplit classes/frame: hotSelf={self / frames}, hotOther={other / frames}, subres={subres / frames}, selfSkipped={skipped / frames}" +
                   (barriersDeferred != 0 || splitBarriered != 0 ? $", barrierDeferred={barriersDeferred / frames} splitBarriered={splitBarriered / frames}" : string.Empty) +
                   (legacy != 0 ? $", legacy={legacy / frames}" : string.Empty) +
                   $", onUnwritten: other={otherUnwritten / frames} self={selfUnwritten / frames}" +
                   $", fetchable={fetchable / frames} fetched={fetched / frames} fetchPending={fetchPending / frames} fetchWriter={fetchWriter / frames}" +
                   (shapes.Length != 0 ? $". hot shapes: {shapes}." : ".") +
                   (hazardTex.Length != 0 ? $" hazard textures/frame: {hazardTex}." : string.Empty) +
                   (otherLabels.Length != 0 ? $" hotOther programs/frame: {otherLabels}." : string.Empty) +
                   $" snapshot: served={snapshotServed / frames}/frame, dispatches={snapshotDispatches / frames}/frame, failures={TileSnapshot.Failures}." +
                   $" storage: fragWrites={storageWrites / frames}/frame, rawDraws={storageRawDraws / frames}/frame, keptBarriers={storageBarriersKept / frames}/frame, rawAtBarrier={storageRawBarriers / frames}/frame." + samples;
        }

        /// <summary>
        /// The current pass's attachments, held back until a draw actually writes them.
        /// Marking them when the pass descriptor was built made every split pointless:
        /// the pass opened by a split re-marked the same attachments before encoding a
        /// single draw, so the next draw that sampled one split again - 674 times a
        /// frame, every one of them resuming the same render target. Pass scope only.
        /// </summary>
        private readonly List<PassAttachment> _passAttachments = new();
        private readonly List<(IntPtr Ptr, int Width, int Height, MTLPixelFormat Format, int BytesPerPixel, bool Depth)> _passStoreCandidates = new();
        private static bool _passStoreElided;
        private static readonly bool _depthInWriteSet = Environment.GetEnvironmentVariable("RYUJINX_METAL_DEPTH_RAW") == "1";

        /// <summary>
        /// Restrict the read-after-write scan to the bindings the bound program declares.
        /// Default on; RYUJINX_METAL_RAW_DECLARED=0 or /tmp/ryujinx-metal-raw-declared
        /// containing 0 restores the whole-table walk, re-read once a frame so both arms
        /// can be measured inside one session.
        /// </summary>
        private static readonly bool _rawDeclaredDefault =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_RAW_DECLARED") != "0";

        private static bool _rawDeclaredOnly = _rawDeclaredDefault;

        public static bool RawDeclaredOnly => _rawDeclaredOnly;

        public static void RefreshRawDeclared()
        {
            try
            {
                _rawDeclaredOnly = System.IO.File.Exists("/tmp/ryujinx-metal-raw-declared")
                    ? System.IO.File.ReadAllText("/tmp/ryujinx-metal-raw-declared").Trim() != "0"
                    : _rawDeclaredDefault;
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; next frame picks it up.
            }
        }

        public readonly void NoteAttachmentWritten(IntPtr root) => NoteAttachmentWritten(root, SubRange.Full);

        public readonly void NoteAttachmentWritten(IntPtr root, SubRange range)
        {
            if (root == IntPtr.Zero)
            {
                return;
            }

            _writtenThisCb[root] = _writtenThisCb.TryGetValue(root, out SubRange existing)
                ? existing.Union(range)
                : range;
        }

        // Scope of the read-after-write split's write set.
        //   pass (default): only textures written by the CURRENT pass, and only once a draw
        //        has actually written them. That is the dependency Metal genuinely cannot
        //        express - a fragment write followed by a fragment read inside one encoder -
        //        and it is what Ultrahand's readback needs ordered.
        //   cb:  every texture that has been an attachment anywhere in this command buffer.
        //        A draw sampling one splits the pass even though that write finished passes
        //        ago and is already ordered by those pass boundaries and by Metal's own
        //        cross-encoder hazard tracking.
        // Measured by flipping the toggle every 30s inside one session while the game was
        // played normally, 48 blocks against 54: -220 passes a frame (2.9 SE) and -7.3 ms a
        // frame (2.5 SE) at equal draw counts, worth about +15% on an average scene and more
        // on dense ones. Ultrahand grab, rotate, glue and release all verified unaffected.
        // RYUJINX_METAL_RAW_SPLIT_SCOPE=cb to go back.
        // Hot-switchable like the split itself: /tmp/ryujinx-metal-split-scope holds "pass"
        // or "cb", re-read once a frame. Static env vars cannot be A/B'd inside one session,
        // and cross-session comparison on this machine is worthless - the scene varies more
        // than the setting does (8,964 draws a frame in one view against 2,528 in another).
        private static bool _splitScopePass =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_RAW_SPLIT_SCOPE") != "cb";
        private static readonly bool _splitScopeDefault = _splitScopePass;
        public static bool SplitScopePass => _splitScopePass;

        /// <summary>
        /// True when the write set is filled by draws rather than by binding attachments.
        /// Reported in the stats line so a run can prove which code it is running.
        /// </summary>
        public static bool MarkOnDrawActive => _splitScopePass;

        public static void RefreshSplitScope()
        {
            try
            {
                _splitScopePass = File.Exists("/tmp/ryujinx-metal-split-scope")
                    ? File.ReadAllText("/tmp/ryujinx-metal-split-scope").Trim() == "pass"
                    : _splitScopeDefault;
            }
            catch (IOException)
            {
                // Raced with the writer; the next frame picks it up.
            }
        }

        /// <summary>
        /// A draw is about to be encoded into the current pass, so the pass really does
        /// write its attachments from here on. Called after the split decision for that
        /// draw has been made, so the draw itself never splits on its own output.
        /// </summary>
        public readonly void MarkPassAttachmentsWritten()
        {
            // The conservative set marks every attachment. The honest set beside it marks
            // only what this draw can actually write: a colour slot whose guest write mask
            // is non-zero AND whose output the fragment function declares (the pipeline
            // descriptor already ANDs those two), depth only with depth writes enabled.
            // Probe only: the split decision still uses the conservative set, and the
            // stats line reports how many hazards land on attachments nobody in the pass
            // has really written.
            int outputMap = _currentState.RenderProgram?.FragmentOutputMap ?? -1;

            for (int i = 0; i < _passAttachments.Count; i++)
            {
                NoteAttachmentWritten(_passAttachments[i].Ptr, _passAttachments[i].Range);

                int slot = _passAttachments[i].Slot;
                bool writes = slot < 0
                    ? _currentState.DepthStencilUid.DepthWriteEnabled
                    : DrawWritesColorSlot(slot, outputMap);

                if (writes)
                {
                    _reallyWrittenThisPass.Add(_passAttachments[i].Ptr);
                }
            }
        }

        public readonly void ClearWrittenThisPass()
        {
            if (SplitScopePass)
            {
                _writtenThisCb.Clear();
                _reallyWrittenThisPass.Clear();
                _storageWrittenThisPass.Clear();
                _barrieredWrites.Clear();
            }
        }

        // Storage buffers a fragment stage stored to in this pass, by buffer identity and
        // range. The texture write set cannot see these, and a later draw in the same
        // pass reading one is the dependency the guest's barrier has been kept for since
        // the decal footprints. Probe only: it counts how often such a read really
        // occurs at a draw and at a barrier, which decides whether the barrier can be
        // skipped on buffer identity instead of on "the pass has a fragment store".
        private readonly List<(Auto<DisposableBuffer> Buffer, int Offset, int Size)> _storageWrittenThisPass = new();
        private static long _storageWrites, _storageRawDraws, _storageBarriersKept, _storageRawBarriers;

        public readonly void NoteFragmentStorageWrites()
        {
            Program program = _currentState.RenderProgram;

            if (program == null)
            {
                return;
            }

            foreach (ResourceBindingSegment segment in program.BindingSegments[(int)Constants.StorageBuffersSetIndex])
            {
                if ((segment.Stages & ResourceStages.Fragment) == 0)
                {
                    continue;
                }

                for (int i = 0; i < segment.Count; i++)
                {
                    int index = segment.Binding + i;

                    if ((uint)index >= (uint)_currentState.StorageBufferRefs.Length || _currentState.StorageBufferRefs[index].Buffer == null)
                    {
                        continue;
                    }

                    ref BufferRef buffer = ref _currentState.StorageBufferRefs[index];
                    _storageWrittenThisPass.Add((buffer.Buffer, buffer.Range?.Offset ?? 0, buffer.Range?.Size ?? int.MaxValue));
                    _storageWrites++;
                }
            }
        }

        /// <summary>Whether any storage buffer the current program binds, at any stage, overlaps one a fragment stored to in this pass.</summary>
        public readonly bool BoundStorageOverlapsWritten()
        {
            if (_storageWrittenThisPass.Count == 0 || _currentState.RenderProgram == null)
            {
                return false;
            }

            foreach (ResourceBindingSegment segment in _currentState.RenderProgram.BindingSegments[(int)Constants.StorageBuffersSetIndex])
            {
                for (int i = 0; i < segment.Count; i++)
                {
                    int index = segment.Binding + i;

                    if ((uint)index >= (uint)_currentState.StorageBufferRefs.Length || _currentState.StorageBufferRefs[index].Buffer == null)
                    {
                        continue;
                    }

                    ref BufferRef bound = ref _currentState.StorageBufferRefs[index];
                    int offset = bound.Range?.Offset ?? 0;
                    int size = bound.Range?.Size ?? int.MaxValue;

                    for (int w = 0; w < _storageWrittenThisPass.Count; w++)
                    {
                        (Auto<DisposableBuffer> wb, int wo, int ws) = _storageWrittenThisPass[w];

                        if (ReferenceEquals(wb, bound.Buffer) && offset < wo + ws && wo < offset + size)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public readonly void NoteStorageRawAtDraw()
        {
            if (BoundStorageOverlapsWritten())
            {
                _storageRawDraws++;
            }
        }

        public readonly void NoteStorageRawAtBarrier()
        {
            _storageBarriersKept++;

            if (BoundStorageOverlapsWritten())
            {
                _storageRawBarriers++;
            }
        }

        public readonly void ClearWrittenThisCb()
        {
            _writtenThisCb.Clear();
            _reallyWrittenThisPass.Clear();
            _storageWrittenThisPass.Clear();
            _barrieredWrites.Clear();
        }

        /// <summary>
        /// Deferred guest texture barrier: remember that everything this pass has written so
        /// far must be visible to whatever samples it later in the same pass. The pass is not
        /// ended here; the draw that actually samples one of these attachments ends it
        /// (SamplesEarlierWrite reports Hazard instead of SelfOnly/Fetchable-with-skip), and a
        /// barrier nothing later samples costs no pass at all. Storage-buffer consumers are
        /// not visible to this table; the caller keeps ending the pass for those.
        /// </summary>
        public readonly void NoteGuestBarrier()
        {
            _barriersDeferred++;

            foreach (IntPtr root in _writtenThisCb.Keys)
            {
                _barrieredWrites.Add(root);
            }
        }

        /// <summary>
        /// Whether a bound storage image (any stage, single or array) aliases an attachment
        /// written before a deferred barrier. The texture walk covers sampled bindings only.
        /// </summary>
        private readonly bool ImagesReadBarrieredWrite()
        {
            if (_barrieredWrites.Count == 0)
            {
                return false;
            }

            foreach (ImageRef reference in _currentState.ImageRefs)
            {
                if (reference.Storage is Texture image && _barrieredWrites.Contains(image.CanonicalPtr))
                {
                    return true;
                }
            }

            foreach (EncoderState.ArrayRef<ImageArray> arrayRef in _currentState.ImageArrayRefs)
            {
                if (arrayRef.Array == null)
                {
                    continue;
                }

                foreach (TextureRef reference in arrayRef.Array.GetTextureRefs())
                {
                    if (reference.Storage is Texture image && _barrieredWrites.Contains(image.CanonicalPtr))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// The bound texture this draw samples that was written earlier in this command
        /// buffer - the composite's input at the read-after-write split point. Null if none.
        /// </summary>
        public readonly Texture EarlierWrittenSampledTexture()
        {
            if (_writtenThisCb.Count == 0)
            {
                return null;
            }

            foreach (TextureRef reference in _currentState.TextureRefs)
            {
                if (reference.Storage is Texture sampled && _writtenThisCb.ContainsKey(sampled.CanonicalPtr))
                {
                    return sampled;
                }
            }

            return null;
        }

        /// <summary>Viewport 0 and scissor 0, for the draw census.</summary>
        public readonly string DescribeRaster()
        {
            MTLViewport vp = _currentState.Viewports.Length > 0 ? _currentState.Viewports[0] : default;
            MTLScissorRect sc = _currentState.Scissors.Length > 0 ? _currentState.Scissors[0] : default;
            return $"vp({vp.originX:F0},{vp.originY:F0},{vp.width:F0}x{vp.height:F0},z{vp.znear:G3}-{vp.zfar:G3}) sc({sc.x},{sc.y},{sc.width}x{sc.height}) cull={_currentState.CullMode} depth={_currentState.DepthStencilUid.DepthCompareFunction}/{(_currentState.DepthStencilUid.DepthWriteEnabled ? "w" : "-")}";
        }

        /// <summary>The blend state of one colour attachment, for the draw census.</summary>
        public readonly string DescribeBlend(int index)
        {
            ColorBlendStateUid b = _currentState.Pipeline.Internal.ColorBlendState[index];
            return $"blendId0=0x{b.Id0:X} rgb({(int)b.SourceRGBBlendFactor},{(int)b.DestinationRGBBlendFactor},op{(int)b.RgbBlendOperation}) a({(int)b.SourceAlphaBlendFactor},{(int)b.DestinationAlphaBlendFactor},op{(int)b.AlphaBlendOperation}) wm{(int)b.WriteMask:X}";
        }

        /// <summary>All colour attachments of the next pass with their write masks, marking
        /// the one that is the given input texture's storage, plus the clear-load flag.</summary>
        public readonly string DescribeRenderTargets(Texture input)
        {
            StringBuilder sb = new();
            for (int i = 0; i < _currentState.RenderTargets.Length; i++)
            {
                if (_currentState.RenderTargets[i] is not Texture t) { continue; }
                MTLColorWriteMask wm = _currentState.Pipeline.Internal.ColorBlendState[i].WriteMask;
                sb.Append($"rt{i}:{t.Width}x{t.Height}:{t.MtlFormat}:wm{(int)wm:X}{(input != null && t.CanonicalPtr == input.CanonicalPtr ? "=IN" : "")} ");
            }
            if (_currentState.DepthStencil is Texture ds) { sb.Append($"ds:{ds.Width}x{ds.Height} "); }
            sb.Append(_currentState.ClearLoadAction ? "clearLoad" : "load");
            return sb.ToString();
        }

        public readonly Texture FirstBoundLargeTexture()
        {
            foreach (TextureRef reference in _currentState.TextureRefs)
            {
                if (reference.Storage is Texture t && t.Width >= 256 && t.Height >= 128)
                {
                    return t;
                }
            }

            return null;
        }

        /// <summary>
        /// Whether this draw reads something the pass already wrote - the read-after-write
        /// hazard that can only be ordered by ending the pass.
        ///
        /// TextureRefs is a persistent table indexed by binding slot, not the set of
        /// textures this draw uses: a slot keeps whatever was last bound to it, across
        /// programs, until something overwrites it. Walking the whole table therefore
        /// reports a hazard for a render target that is merely still sitting in a slot the
        /// current shader never declared - and a shader that does not declare a binding
        /// cannot read through it, so that is not a hazard at any level.
        ///
        /// Measured at 1000+ splits a frame against a floor of ~110 real render target
        /// switches, each costing a full attachment store and reload (45.8us a pass; the
        /// dense scenes spend 57ms of a 74ms frame on it). Disabling the split entirely -
        /// incorrect, but it bounds the prize - was worth +76% in those scenes.
        ///
        /// Restricting the walk to the bindings the bound program actually declares is a
        /// strict narrowing: it can only remove hazards the shader was incapable of
        /// performing. RYUJINX_METAL_RAW_DECLARED=0 (or /tmp/ryujinx-metal-raw-declared)
        /// restores the whole-table walk for comparison.
        /// </summary>
        /// <summary>
        /// Whether the draw about to be encoded samples any texture whose storage is in
        /// <paramref name="roots"/> (canonical pointers). Walks the bindings the program declares,
        /// like <see cref="SamplesEarlierWrite"/>. Used by EncoderLabels to mark passes that read
        /// what the previous pass rendered.
        /// </summary>
        public readonly bool SamplesAnyOf(HashSet<IntPtr> roots)
        {
            Program program = _currentState.RenderProgram;

            if (roots.Count == 0 || program == null)
            {
                return false;
            }

            foreach (ResourceBindingSegment segment in program.BindingSegments[Constants.TexturesSetIndex])
            {
                if (segment.Type is ResourceType.Sampler or ResourceType.BufferTexture)
                {
                    continue;
                }

                if (!segment.IsArray)
                {
                    for (int i = 0; i < segment.Count; i++)
                    {
                        int index = segment.Binding + i;

                        if ((uint)index < (uint)_currentState.TextureRefs.Length &&
                            _currentState.TextureRefs[index].Storage is Texture sampled &&
                            roots.Contains(sampled.CanonicalPtr))
                        {
                            return true;
                        }
                    }
                }
                else if ((uint)segment.Binding < (uint)_currentState.TextureArrayRefs.Length &&
                         _currentState.TextureArrayRefs[segment.Binding].Array is TextureArray array)
                {
                    foreach (TextureRef reference in array.GetTextureRefs())
                    {
                        if (reference.Storage is Texture sampled && roots.Contains(sampled.CanonicalPtr))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public readonly RawHazard SamplesEarlierWrite()
        {
            if (_writtenThisCb.Count == 0)
            {
                return RawHazard.None;
            }

            Program program = _currentState.RenderProgram;

            if (!RawDeclaredOnly || program == null)
            {
                if (SamplesEarlierWriteAnyBinding())
                {
                    _splitLegacy++;
                    return RawHazard.Hazard;
                }

                return RawHazard.None;
            }

            // Detection and classification in one walk. The split fires exactly as before
            // (any declared binding whose storage is in the write set), but every firing is
            // also binned by what a subresource-granular predicate would have said, so the
            // stats line reports how much of the remaining split load is actually
            // avoidable before anyone builds the finer write set.
            bool any = false;
            bool anyOverlap = false;
            bool anySelf = false;
            bool anyForeign = false;
            bool anyReallyWritten = false;
            bool anyFetchable = false;
            bool anySkippableSelf = false;
            bool anyBarriered = false;
            _fetchPlanCount = 0;
            _fetchPlanNeedsSnapshot = false;

            if (ImagesReadBarrieredWrite())
            {
                _splitBarriered++;

                return RawHazard.Hazard;
            }

            ResourceBindingSegment[] segments = program.BindingSegments[Constants.TexturesSetIndex];

            foreach (ResourceBindingSegment segment in segments)
            {
                // A sampler-only segment names no texture, and a buffer texture is never a
                // render target, so neither can carry the hazard.
                if (segment.Type is ResourceType.Sampler or ResourceType.BufferTexture)
                {
                    continue;
                }

                if (!segment.IsArray)
                {
                    for (int i = 0; i < segment.Count; i++)
                    {
                        int index = segment.Binding + i;

                        if ((uint)index >= (uint)_currentState.TextureRefs.Length)
                        {
                            continue;
                        }

                        if (_currentState.TextureRefs[index].Storage is Texture sampled)
                        {
                            ClassifyMatch(sampled, index, ref any, ref anyOverlap, ref anySelf, ref anyForeign, ref anyReallyWritten, ref anyFetchable, ref anySkippableSelf, ref anyBarriered);
                        }
                    }
                }
                else
                {
                    if ((uint)segment.Binding >= (uint)_currentState.TextureArrayRefs.Length)
                    {
                        continue;
                    }

                    TextureArray array = _currentState.TextureArrayRefs[segment.Binding].Array;

                    if (array == null)
                    {
                        continue;
                    }

                    foreach (TextureRef reference in array.GetTextureRefs())
                    {
                        if (reference.Storage is Texture sampled)
                        {
                            ClassifyMatch(sampled, -1, ref any, ref anyOverlap, ref anySelf, ref anyForeign, ref anyReallyWritten, ref anyFetchable, ref anySkippableSelf, ref anyBarriered);
                        }
                    }
                }
            }

            if (!any)
            {
                return RawHazard.None;
            }

            if (!anyOverlap)
            {
                _splitSubres++;

                return RawHazard.Hazard;
            }

            if (anyForeign)
            {
                // A mixed draw - it reads its own pass's attachment AND something a
                // different pass wrote. The foreign read is a real cross-pass hazard, so
                // the draw is never a skip candidate.
                _splitHotOther++;

                if (!anyReallyWritten)
                {
                    _splitOtherUnwritten++;
                }

                _otherLabels.TryGetValue(program.DebugLabel, out int seen);
                _otherLabels[program.DebugLabel] = seen + 1;

                return RawHazard.Hazard;
            }

            if (anyBarriered && (anySkippableSelf || !anyFetchable))
            {
                // The guest put a barrier between the write and this read. The stale
                // self-skip is only tolerable without one (NVN would have served memory
                // that the barrier had just made fresh); a fetch of the data formats is
                // still fine, tile memory is fresher than memory would have been.
                _splitBarriered++;

                return RawHazard.Hazard;
            }

            if (anyFetchable)
            {
                // Only same-pixel attachment reads: the data-format ones go to a fetch
                // variant, any skippable colour ones are the caller's to skip or split.
                _splitFetchable++;
                _fetchPlanHasSkippableSelf = anySkippableSelf;

                return RawHazard.Fetchable;
            }

            _splitHotSelf++;

            if (!anyReallyWritten)
            {
                _splitSelfUnwritten++;
            }

            if (_selfSampleCount < MaxSelfSamples)
            {
                _selfSamples[_selfSampleCount++] =
                    $"[{program.DebugLabel}] reads {_selfSampleTex} {DescribeRaster()}";
            }

            return RawHazard.SelfOnly;
        }

        private readonly void ClassifyMatch(Texture sampled, int binding, ref bool any, ref bool anyOverlap, ref bool anySelf, ref bool anyForeign, ref bool anyReallyWritten, ref bool anyFetchable, ref bool anySkippableSelf, ref bool anyBarriered)
        {
            if (!_writtenThisCb.TryGetValue(sampled.CanonicalPtr, out SubRange written))
            {
                return;
            }

            any = true;
            anyBarriered |= _barrieredWrites.Contains(sampled.CanonicalPtr);

            if (!written.Overlaps(SubRange.Of(sampled)))
            {
                return;
            }

            if (!anyOverlap)
            {
                anyOverlap = true;
                NoteSplitShape(sampled);
            }

            // Every overlapping match, not just the first: the hot-shapes histogram only
            // sees the first texture a draw matches, which is how the second one - the
            // one that makes a draw hotOther - stayed invisible.
            bool reallyWritten = _reallyWrittenThisPass.Contains(sampled.CanonicalPtr);
            anyReallyWritten |= reallyWritten;
            NoteHazardTexture(sampled.MtlFormat, SkippableSelfFormat(sampled.MtlFormat), reallyWritten);

            bool self = false;
            int slot = -1;

            for (int i = 0; i < _passAttachments.Count; i++)
            {
                if (_passAttachments[i].Ptr == sampled.CanonicalPtr)
                {
                    self = true;
                    slot = _passAttachments[i].Slot;

                    break;
                }
            }

            if (self && SkippableSelfFormat(sampled.MtlFormat))
            {
                anySelf = true;
                anySkippableSelf = true;

                if (_selfSampleCount < MaxSelfSamples)
                {
                    _selfSampleTex =
                        $"{sampled.Width}x{sampled.Height}/{sampled.MtlFormat} lv{sampled.FirstLevel}+{Math.Max(1, sampled.Info.Levels)} ly{sampled.FirstLayer}+{Math.Max(1, sampled.Info.GetLayers())}";
                }
            }
            else if (self && binding >= 0 && slot >= 0 && FetchableSelfFormat(sampled.MtlFormat) &&
                     sampled.FirstLevel == 0 && sampled.FirstLayer == 0 &&
                     sampled.Info.Levels <= 1 && sampled.Info.GetLayers() <= 1 &&
                     DrawWritesColorSlot(slot, _currentState.RenderProgram?.FragmentOutputMap ?? -1))
            {
                // The draw reads the slot it also writes. Between draws a fetch and a split
                // agree - the split stores the tile the fetch would have read - but inside
                // one draw they do not: a fetch sees the fragment before it at the same
                // pixel, a sample after a split sees memory from before the draw began.
                // The guest's decals and layered translucents rely on the latter (a
                // two-sided decal box read the front face's fresh depth through the fetch,
                // rebuilt a floating world position and shaded its whole footprint
                // black, flickering with primitive order). So this stays a split.
                if (TileSnapshot.Enabled && slot == _snapshotSrc && _snapshotDst >= 0 && _fetchPlanCount < _fetchPlan.Length)
                {
                    // Served from the snapshot instead: the tile kernel copies this slot's
                    // tile value into the twin right before the draw, and the fetch variant
                    // reads the twin - the value the split would have stored, constant for
                    // the whole draw, with no round trip to memory.
                    _fetchPlan[_fetchPlanCount++] = new FetchBinding(binding, _snapshotDst);
                    _fetchPlanNeedsSnapshot = true;
                    anyFetchable = true;
                    _splitSnapshot++;
                }
                else
                {
                    _splitFetchRefusedWriter++;
                    anyForeign = true;
                }
            }
            else if (self && binding >= 0 && slot >= 0 && FetchableSelfFormat(sampled.MtlFormat) &&
                     sampled.FirstLevel == 0 && sampled.FirstLayer == 0 &&
                     sampled.Info.Levels <= 1 && sampled.Info.GetLayers() <= 1 &&
                     _fetchPlanCount < _fetchPlan.Length)
            {
                // A data-format attachment the draw samples at its own pixel (every such
                // shader in the archive projects its own clip position: uv = attr.xy / attr.w,
                // no offset). Tile memory holds exactly the value the split would have
                // stored, so a framebuffer-fetch variant reads it in place - fresh, unlike
                // the stale self-skip that broke Ultrahand on this very format.
                _fetchPlan[_fetchPlanCount++] = new FetchBinding(binding, slot);
                anyFetchable = true;
            }
            else
            {
                anyForeign = true;
            }
        }

        /// <summary>
        /// Data formats whose same-pixel self-reads a framebuffer-fetch variant serves.
        /// R32Float is the full-resolution linear depth the translucent materials read for
        /// soft blending; it is what every remaining hotOther split at the probe point was.
        /// </summary>
        /// <summary>
        /// Whether the current draw can write colour slot <paramref name="slot"/>: the
        /// fragment function declares that output (FragmentOutputMap keeps a component
        /// nibble per attachment; -1 means unknown, treated as declared) and the guest's
        /// write mask for the slot is not empty.
        /// </summary>
        private readonly bool DrawWritesColorSlot(int slot, int outputMap)
        {
            bool declared = outputMap == -1 || ((outputMap >> (slot * 4)) & 0xF) != 0;

            return declared && _currentState.Pipeline.Internal.ColorBlendState[slot].WriteMask != MTLColorWriteMask.None;
        }

        private static bool FetchableSelfFormat(MTLPixelFormat format)
        {
            // R32Float only: the linear depth translucent materials read at their own
            // pixel (soft particles, depth fade), where the fetched tile value is exactly
            // the split's stored value. RG11B10Float was tried and reverted: the HDR scene
            // target is read by water refraction at a PERTURBED coordinate, not the
            // fragment's own pixel, and the coordinate is a computed temp the patcher
            // cannot tell from a same-pixel one - so fetch returned own-pixel colour and
            // the refraction flattened (reported live, 2026-09-03). A colour target only
            // becomes fetchable again with a runtime same-pixel proof, not a format test.
            return format == MTLPixelFormat.R32Float;
        }

        /// <summary>
        /// Whether a self-read of this format may be served stale under the NVN
        /// unbarriered-read tolerance. Colour formats carry picture - water refraction,
        /// distortion, compositing - where a frame-late read is what the guest hardware
        /// delivered too. Data formats are excluded: the R32Float self-reads feed the
        /// depth/picking chain, and letting those go stale is exactly how Ultrahand
        /// stopped grabbing (reported live, 2026-08-30, and once before in the archive
        /// when splits were disabled wholesale). Anything not explicitly listed splits.
        /// </summary>
        private static bool SkippableSelfFormat(MTLPixelFormat format)
        {
            switch (format)
            {
                case MTLPixelFormat.RGBA8Unorm:
                case MTLPixelFormat.RGBA8UnormsRGB:
                case MTLPixelFormat.BGRA8Unorm:
                case MTLPixelFormat.BGRA8UnormsRGB:
                    return true;

                // RG11B10Float is the scene HDR target and water refraction samples it
                // mid-pass; served stale on a TBDR the read returns tile rows the pass
                // has stored and nothing for the rows it has not, which renders as
                // horizontal banding across flowing water (user screenshot, 2026-08-30
                // 13:2x). On the guest's IMR the same unbarriered read was merely a
                // frame late, never partial. RGB10A2 goes with it on the same argument
                // (normal/material data); the census counted its self-reads at zero, so
                // excluding it costs nothing. The 8-bit compose targets carry the bulk
                // of the win (631 of ~700 skips a frame) and stay.
                default:
                    return false;
            }
        }

        /// <summary>
        /// The original predicate: any texture sitting in any binding slot. Kept as the
        /// comparison arm, and as the fallback when no program is bound.
        /// </summary>
        private readonly bool SamplesEarlierWriteAnyBinding()
        {
            foreach (TextureRef reference in _currentState.TextureRefs)
            {
                if (reference.Storage is Texture sampled && _writtenThisCb.ContainsKey(sampled.CanonicalPtr))
                {
                    return true;
                }
            }

            foreach (EncoderState.ArrayRef<TextureArray> arrayRef in _currentState.TextureArrayRefs)
            {
                if (arrayRef.Array == null)
                {
                    continue;
                }

                foreach (TextureRef reference in arrayRef.Array.GetTextureRefs())
                {
                    if (reference.Storage is Texture sampled && _writtenThisCb.ContainsKey(sampled.CanonicalPtr))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// The scene-class texture currently bound for sampling, if any. Bound state only,
        /// so it can be answered before RenderResourcesPrepass and before any encoder is
        /// acquired - the same constraint the feedback split had to learn the hard way.
        /// </summary>
        public readonly Texture SceneClassSampledTexture()
        {
            foreach (TextureRef reference in _currentState.TextureRefs)
            {
                if (reference.Storage is Texture sampled && Texture.IsSceneClass(sampled.Info))
                {
                    return sampled;
                }
            }

            // Textures also arrive as arrays, and a walk of TextureRefs alone silently
            // misses those - which is exactly the shape of every "nothing is bound here"
            // artefact in this investigation's history.
            foreach (EncoderState.ArrayRef<TextureArray> arrayRef in _currentState.TextureArrayRefs)
            {
                if (arrayRef.Array == null)
                {
                    continue;
                }

                foreach (TextureRef reference in arrayRef.Array.GetTextureRefs())
                {
                    if (reference.Storage is Texture sampled && Texture.IsSceneClass(sampled.Info))
                    {
                        return sampled;
                    }
                }
            }

            return null;
        }

        private readonly (ulong gpuAddress, IntPtr nativePtr) AddressForTexture(ref TextureRef texture)
        {
            TextureBase storage = texture.Storage;

            ulong gpuAddress = 0;
            IntPtr nativePtr = IntPtr.Zero;

            if (storage != null)
            {
                if (storage is TextureBuffer textureBuffer)
                {
                    textureBuffer.RebuildStorage(false);
                }

                // A/B switch: sampling normally goes through the swizzled view, while
                // attachments use the identity handle. RG11B10Float carries no alpha, so
                // a swizzle that routes a component to alpha or to one samples as exactly
                // 1.0 - which is the value the tonemap reads on a white frame.
                MTLTexture mtlTexture = _identitySampling
                    ? storage.GetIdentityHandle(_pipeline.Cbs)
                    : storage.GetHandle(_pipeline.Cbs);

                gpuAddress = mtlTexture.GpuResourceID._impl;
                nativePtr = mtlTexture.NativePtr;
            }

            return (gpuAddress, nativePtr);
        }

        private readonly (ulong gpuAddress, IntPtr nativePtr) AddressForImage(ref ImageRef image)
        {
            Texture storage = image.Storage;

            ulong gpuAddress = 0;
            IntPtr nativePtr = IntPtr.Zero;

            if (storage != null)
            {
                // A/B switch: sampling normally goes through the swizzled view, while
                // attachments use the identity handle. RG11B10Float carries no alpha, so
                // a swizzle that routes a component to alpha or to one samples as exactly
                // 1.0 - which is the value the tonemap reads on a white frame.
                MTLTexture mtlTexture = _identitySampling
                    ? storage.GetIdentityHandle(_pipeline.Cbs)
                    : storage.GetHandle(_pipeline.Cbs);

                gpuAddress = mtlTexture.GpuResourceID._impl;
                nativePtr = mtlTexture.NativePtr;

                // The view carries the format and swizzle the shader needs, so it stays in
                // the argument buffer; only the resource named for residency changes.
                if (_residencyOnParent && storage is Texture t && t.CanonicalPtr != IntPtr.Zero)
                {
                    nativePtr = t.CanonicalPtr;
                }
            }

            return (gpuAddress, nativePtr);
        }

        private readonly (ulong gpuAddress, IntPtr nativePtr) AddressForTextureBuffer(ref TextureBuffer bufferTexture)
        {
            ulong gpuAddress = 0;
            IntPtr nativePtr = IntPtr.Zero;

            if (bufferTexture != null)
            {
                bufferTexture.RebuildStorage(false);

                MTLTexture mtlTexture = bufferTexture.GetHandle(_pipeline.Cbs);

                gpuAddress = mtlTexture.GpuResourceID._impl;
                nativePtr = mtlTexture.NativePtr;
            }

            return (gpuAddress, nativePtr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddResource(IntPtr resourcePointer, MTLResourceUsage usage, MTLRenderStages stages, ref readonly RenderEncoderBindings bindings)
        {
            if (resourcePointer != IntPtr.Zero)
            {
                bindings.Resources.Add(new Resource(new MTLResource(resourcePointer), usage, stages));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddResource(IntPtr resourcePointer, MTLResourceUsage usage, ref readonly ComputeEncoderBindings bindings)
        {
            if (resourcePointer != IntPtr.Zero)
            {
                bindings.Resources.Add(new Resource(new MTLResource(resourcePointer), usage, 0));
            }
        }

        private readonly void UpdateAndBind(Program program, uint setIndex, ref readonly RenderEncoderBindings bindings)
        {
            ResourceBindingSegment[] bindingSegments = program.BindingSegments[setIndex];
            bool captureToneMapBindings = ToneMapProbe.IsWatched(program.DebugLabel);

            if (bindingSegments.Length == 0)
            {
                return;
            }

            ScopedTemporaryBuffer vertArgBuffer = default;
            ScopedTemporaryBuffer fragArgBuffer = default;

            if (program.ArgumentBufferSizes[setIndex] > 0)
            {
                vertArgBuffer = _bufferManager.ReserveOrCreate(_pipeline.Cbs, program.ArgumentBufferSizes[setIndex] * sizeof(ulong), true);
                bindings.TemporaryBuffers.Add(vertArgBuffer);
            }

            if (program.FragArgumentBufferSizes[setIndex] > 0)
            {
                fragArgBuffer = _bufferManager.ReserveOrCreate(_pipeline.Cbs, program.FragArgumentBufferSizes[setIndex] * sizeof(ulong), true);
                bindings.TemporaryBuffers.Add(fragArgBuffer);
            }

            Span<ulong> vertResourceIds = stackalloc ulong[program.ArgumentBufferSizes[setIndex]];
            Span<ulong> fragResourceIds = stackalloc ulong[program.FragArgumentBufferSizes[setIndex]];

            int vertResourceIdIndex = 0;
            int fragResourceIdIndex = 0;

            foreach (ResourceBindingSegment segment in bindingSegments)
            {
                int binding = segment.Binding;
                int count = segment.Count;

                switch (setIndex)
                {
                    case Constants.ConstantBuffersSetIndex:
                        for (int i = 0; i < count; i++)
                        {
                            int index = binding + i;

                            ref BufferRef buffer = ref _currentState.UniformBufferRefs[index];
                            (ulong gpuAddress, IntPtr nativePtr) = AddressForBuffer(ref buffer);

                            // The stage program's constant buffers, resolved exactly as the
                            // draw binds them (mirror included), for the CPU-vs-GPU snapshot.
                            if (UploadCorrelator.Enabled && _stageLabel.Length != 0 && buffer.Buffer != null &&
                                program.DebugLabel != null && program.DebugLabel.StartsWith(_stageLabel, StringComparison.Ordinal))
                            {
                                int sOff = buffer.Range?.Offset ?? 0;
                                int sSize = buffer.Range?.Size ?? 0;
                                MTLBuffer sb = buffer.Range.HasValue && !buffer.Range.Value.Write
                                    ? buffer.Buffer.GetMirrorable(_pipeline.Cbs, ref sOff, sSize, out _).Value
                                    : buffer.Buffer.Get(_pipeline.Cbs, sOff, sSize, buffer.Range?.Write ?? false).Value;
                                // Tagged by stage: fp_c3 and vp_c3 land on different indices
                                // and only the vertex one carries the flare's two multipliers.
                                UploadCorrelator.NoteStageUniform(
                                    (segment.Stages & ResourceStages.Vertex) != 0 ? 2000 + index : index,
                                    sb, sOff, sSize);
                            }

                            // Dedicated fp_c3[0..1] probe for RYUJINX_METAL_CONST_LABEL.
                            if (UploadCorrelator.Enabled && buffer.Buffer != null && index <= 4 &&
                                UploadCorrelator.ConstLabel.Length != 0 && program.DebugLabel != null &&
                                program.DebugLabel.StartsWith(UploadCorrelator.ConstLabel, StringComparison.Ordinal))
                            {
                                MTLBuffer c3 = buffer.Buffer.GetUnsafe().Value;
                                UploadCorrelator.NoteProgramConst(index, c3.Contents, (int)(buffer.Range?.Offset ?? 0));
                            }

                            // Contents, not identity. The composite's weight sum can be
                            // driven to zero by its constants alone, and identity is what
                            // every previous binding check compared.
                            if (UploadCorrelator.Enabled && buffer.Buffer != null &&
                                program.IsTexelFetchComposite)
                            {
                                MTLBuffer cb = buffer.Buffer.GetUnsafe().Value;
                                UploadCorrelator.NoteCompositeConstants(
                                    index, cb.Contents, (int)(buffer.Range?.Offset ?? 0));
                            }

                            // Row 5 of every uniform slot at the blit's draw: position.w is
                            // dot(attr.xyz, c3[5].xyz) + c3[5].w and the guest data behind
                            // it swings over thousands.
                            if (UploadCorrelator.Enabled && buffer.Buffer != null &&
                                program.DebugLabel is string bl &&
                                bl.StartsWith("480117", StringComparison.Ordinal))
                            {
                                MTLBuffer wb = buffer.Buffer.GetUnsafe().Value;
                                UploadCorrelator.NoteBlitRow(
                                    index, wb.Contents, (int)(buffer.Range?.Offset ?? 0));
                            }

                            if (HdrPassProbe.Enabled && buffer.Buffer != null && index == 20 &&
                                program.DebugLabel == _inputWatchLabel)
                            {
                                MTLBuffer tb = buffer.Buffer.GetUnsafe().Value;
                                HdrPassProbe.NoteToneMapWeights(tb.Contents, buffer.Range?.Offset ?? 0);
                            }

                            if (HdrPassProbe.Enabled && buffer.Buffer != null &&
                                HdrPassProbe.IsWatchedTarget(_currentState.RenderTargets[0]))
                            {
                                MTLBuffer ub = buffer.Buffer.GetUnsafe().Value;
                                HdrPassProbe.NoteUniform(ub.Contents, buffer.Range?.Offset ?? 0, buffer.Range?.Size ?? 256);
                            }

                            if (captureToneMapBindings)
                            {
                                if (index == 20)
                                {
                                    _currentState.DrawRingCb1Address = gpuAddress;
                                }
                                else if (index == 22)
                                {
                                    _currentState.DrawRingCb3Address = gpuAddress;
                                }
                            }

                            MTLRenderStages renderStages = 0;

                            if ((segment.Stages & ResourceStages.Vertex) != 0)
                            {
                                vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                vertResourceIdIndex++;

                                renderStages |= MTLRenderStages.RenderStageVertex;
                            }

                            if ((segment.Stages & ResourceStages.Fragment) != 0)
                            {
                                fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                fragResourceIdIndex++;

                                renderStages |= MTLRenderStages.RenderStageFragment;
                            }

                            AddResource(nativePtr, MTLResourceUsage.Read, renderStages, in bindings);
                        }
                        break;
                    case Constants.StorageBuffersSetIndex:
                        for (int i = 0; i < count; i++)
                        {
                            int index = binding + i;

                            ref BufferRef buffer = ref _currentState.StorageBufferRefs[index];
                            (ulong gpuAddress, IntPtr nativePtr) = AddressForBuffer(ref buffer);

                            // The stage program's storage buffers, resolved as the draw binds
                            // them. The flare vertex shader reads vp_s0->data[0] as an INTEGER
                            // COUNT (float(as_type<int>(...))) and multiplies the sprite's
                            // intensity by it - an occlusion-query style result, and the one
                            // input of that draw never captured.
                            if (UploadCorrelator.Enabled && buffer.Buffer != null && buffer.Range.HasValue)
                            {
                                // Who touches the flare's count slot: every writable storage
                                // binding is reported against it, and the flare draw itself
                                // reports where it reads from and what the CPU side holds there.
                                int wOff = buffer.Range.Value.Offset;
                                int wSize = buffer.Range.Value.Size;
                                IntPtr wPtr = buffer.Buffer.GetUnsafe().Value.NativePtr;
                                // Every storage binding overlapping the flare's count slot, read or
                                // write, with the stage and the Write flag: the writer is found by
                                // elimination, not by trusting the flag.
                                UploadCorrelator.NoteWriteTo(wPtr, wOff, wSize, (buffer.Range.Value.Write ? "W:" : "S:") + (program.DebugLabel is string wl && wl.Length > 6 ? wl[..6] : program.DebugLabel) + ((segment.Stages & ResourceStages.Vertex) != 0 ? "v" : "") + ((segment.Stages & ResourceStages.Fragment) != 0 ? "f" : ""));
                                if (index == 0 && program.IsFlareVertex)
                                {
                                    int rOff = wOff;
                                    MTLBuffer rb = buffer.Buffer.GetMirrorable(_pipeline.Cbs, ref rOff, wSize, out bool rMir).Value;
                                    UploadCorrelator.NoteFlareCountBinding(wPtr, wOff, rb, rOff, rMir);
                                }
                            }
                            if (UploadCorrelator.Enabled && _stageLabel.Length != 0 && buffer.Buffer != null &&
                                program.DebugLabel != null && program.DebugLabel.StartsWith(_stageLabel, StringComparison.Ordinal))
                            {
                                int sOff = buffer.Range?.Offset ?? 0;
                                int sSize = buffer.Range?.Size ?? 0;
                                MTLBuffer sb = buffer.Range.HasValue && !buffer.Range.Value.Write
                                    ? buffer.Buffer.GetMirrorable(_pipeline.Cbs, ref sOff, sSize, out _).Value
                                    : buffer.Buffer.Get(_pipeline.Cbs, sOff, sSize, buffer.Range?.Write ?? false).Value;
                                UploadCorrelator.NoteStageUniform(1000 + index, sb, sOff, sSize);
                            }

                            MTLRenderStages renderStages = 0;

                            if ((segment.Stages & ResourceStages.Vertex) != 0)
                            {
                                vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                vertResourceIdIndex++;

                                renderStages |= MTLRenderStages.RenderStageVertex;
                            }

                            if ((segment.Stages & ResourceStages.Fragment) != 0)
                            {
                                fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                fragResourceIdIndex++;

                                renderStages |= MTLRenderStages.RenderStageFragment;
                            }

                            // Storage buffers are writable from graphics stages too, so
                            // the residency declaration must include Write - writing to a
                            // resource declared read-only is undefined behaviour. The
                            // compute path and the image case below already declare
                            // Read | Write; only this one was left read-only.
                            AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, renderStages, in bindings);
                        }
                        break;
                    case Constants.TexturesSetIndex:
                        if (!segment.IsArray)
                        {
                            bool hasTexture = segment.Type != ResourceType.Sampler;
                            bool hasSampler = segment.Type is ResourceType.Sampler or ResourceType.TextureAndSampler;

                            for (int i = 0; i < count; i++)
                            {
                                int index = binding + i;

                                ref TextureRef texture = ref _currentState.TextureRefs[index];
                                // TextureRefs is one array for every stage. If the texture in
                                // this slot was set for another stage than the one this
                                // program's segment needs, the shader samples the wrong one.
                                if (UploadCorrelator.Enabled && hasTexture && texture.Storage != null &&
                                    (segment.Stages & (ResourceStages)(1 << (int)texture.Stage)) == 0)
                                {
                                    UploadCorrelator.NoteStageMismatch(program.DebugLabel, index, segment.Stages, texture.Stage,
                                        texture.Storage is Texture mt ? $"{mt.Width}x{mt.Height}/{mt.MtlFormat}" : "?");
                                }
                                (ulong gpuAddress, IntPtr nativePtr) = hasTexture
                                    ? AddressForTexture(ref texture)
                                    : (0, IntPtr.Zero);

                                // The resource id actually handed to the shader for the
                                // scene texture. Constant injection proved the fetch
                                // returns something that is not in that texture's memory,
                                // which is either the driver misreading it or the shader
                                // being pointed somewhere else entirely; this is the only
                                // number that separates the two.
                                if (_shadowBind && hasTexture && nativePtr != IntPtr.Zero &&
                                    bindings.ShadowTextures.Count < 28)
                                {
                                    bindings.ShadowTextures.Add(nativePtr);

                                    if (++_shadowCollected % 500000 == 1)
                                    {
                                        Logger.Info?.PrintMsg(LogClass.Gpu, $"shadow-bind collected {_shadowCollected}");
                                    }
                                }

                                // The watched program's input is sampled regardless of
                                // IsSceneClass - the upscaler's input is the 800x448 scene,
                                // which the >=1000-wide predicate excludes.
                                if (UploadCorrelator.Enabled && hasTexture &&
                                    texture.Storage is Texture watchCandidate &&
                                    _watchLabel.Length != 0 && program.DebugLabel != null &&
                                    program.DebugLabel.StartsWith(_watchLabel, StringComparison.Ordinal))
                                {
                                    UploadCorrelator.NoteCompositeOutput(watchCandidate);
                                }

                                if (UploadCorrelator.Enabled && hasTexture && _stageLabel.Length != 0 &&
                                    texture.Storage is Texture stageInput && program.DebugLabel != null &&
                                    program.DebugLabel.StartsWith(_stageLabel, StringComparison.Ordinal))
                                {
                                    UploadCorrelator.NoteStageInputBinding(index, stageInput);
                                }

                                if (UploadCorrelator.Enabled && hasTexture &&
                                    texture.Storage is Texture sceneCandidate &&
                                    Texture.IsSceneClass(sceneCandidate.Info))
                                {
                                    UploadCorrelator.NoteSceneBinding(
                                        gpuAddress, nativePtr, sceneCandidate.CanonicalPtr, program.DebugLabel,
                                        null);

                                    // The source of the pass-through blit that writes the
                                    // presented surface. That shader has no arithmetic - one
                                    // sample, written straight out - so if its source holds a
                                    // picture while the screen is a uniform fill, the fault
                                    // is in its UVs and not upstream of it at all.
                                    if (_watchLabel.Length != 0 && program.DebugLabel != null &&
                                        program.DebugLabel.StartsWith(_watchLabel, StringComparison.Ordinal))
                                    {
                                        UploadCorrelator.NoteCompositeOutput(sceneCandidate);
                                        UploadCorrelator.NoteCompositeInputWriters(sceneCandidate);

                                        // The blit's own command buffer and its input - the
                                        // other half of the writer/blit pair.
                                        UploadCorrelator.NoteBlitCb(
                                            sceneCandidate.CanonicalPtr,
                                            _pipeline.Cbs.CommandBufferIndex,
                                            _pipeline.PoolRentSeq(_pipeline.Cbs.CommandBufferIndex));
                                        UploadCorrelator.NoteBlitInputSerial(sceneCandidate.Serial);
                                        UploadCorrelator.NoteBlitInputGen(sceneCandidate.CanonicalPtr);
                                        UploadCorrelator.ArmAfterBlitSample();

                                        MTLViewport vp = _currentState.Viewports[0];
                                        MTLScissorRect sc = _currentState.Scissors[0];
                                        UploadCorrelator.NoteBlitRaster(
                                            $"vp={vp.width:F0}x{vp.height:F0} sc={sc.width}x{sc.height} cull={_currentState.CullMode}");
                                        UploadCorrelator.NoteBlitPso(_lastPsoPtr);
                                    }
                                }

                                if (HdrPassProbe.Enabled && hasTexture &&
                                    HdrPassProbe.IsWatchedTarget(_currentState.RenderTargets[0]))
                                {
                                    HdrPassProbe.NoteSampled(nativePtr, texture.Storage);
                                }

                                if (HdrPassProbe.Enabled && hasTexture)
                                {
                                    HdrPassProbe.NoteSampledUnderWatchedTarget(
                                        index, texture.Storage, _currentState.RenderTargets[0]);

                                    if (_currentState.RenderTargets[0] is Texture spanRt &&
                                        texture.Storage != null)
                                    {
                                        HdrPassProbe.NoteSpan(
                                            program.DebugLabel,
                                            texture.Storage.Width, texture.Storage.Height,
                                            spanRt.Width, spanRt.Height);
                                    }

                                    HdrPassProbe.NoteCompositeDraw(
                                        program.DebugLabel, _currentState.RenderTargets[0]);

                                    if (program.DebugLabel == _inputWatchLabel)
                                    {
                                        HdrPassProbe.NoteToneMapSlotId(index, gpuAddress, texture.Storage);
                                    }
                                }

                                if (captureToneMapBindings && hasTexture)
                                {
                                    if (index == 8)
                                    {
                                        _currentState.DrawRingTex8ResourceId = gpuAddress;
                                    }
                                    else if (index == 10)
                                    {
                                        _currentState.DrawRingTexAResourceId = gpuAddress;
                                    }
                                }

                                if (UploadCorrelator.Enabled && _stageLabel.Length != 0 && hasTexture && hasSampler &&
                                    program.DebugLabel != null && program.DebugLabel.StartsWith(_stageLabel, StringComparison.Ordinal) &&
                                    texture.Storage is Texture sampTex)
                                {
                                    UploadCorrelator.NoteStageSampler(index, gpuAddress, sampTex.Width, sampTex.Height,
                                        texture.Sampler != null ? texture.Sampler.Get(_pipeline.Cbs).Value.GpuResourceID._impl : 0);
                                }

                                ulong samplerId = hasSampler && texture.Sampler != null
                                    ? texture.Sampler.Get(_pipeline.Cbs).Value.GpuResourceID._impl
                                    : 0;

                                MTLRenderStages renderStages = 0;

                                if ((segment.Stages & ResourceStages.Vertex) != 0)
                                {
                                    if (hasTexture)
                                    {
                                        vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                        vertResourceIdIndex++;
                                    }

                                    if (hasSampler)
                                    {
                                        vertResourceIds[vertResourceIdIndex] = samplerId;
                                        vertResourceIdIndex++;
                                    }

                                    renderStages |= MTLRenderStages.RenderStageVertex;
                                }

                                if ((segment.Stages & ResourceStages.Fragment) != 0)
                                {
                                    if (hasTexture)
                                    {
                                        fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                        fragResourceIdIndex++;
                                    }

                                    if (hasSampler)
                                    {
                                        fragResourceIds[fragResourceIdIndex] = samplerId;
                                        fragResourceIdIndex++;
                                    }

                                    renderStages |= MTLRenderStages.RenderStageFragment;
                                }

                                if (hasTexture)
                                {
                                    AddResource(nativePtr, MTLResourceUsage.Read, renderStages, in bindings);
                                }
                            }
                        }
                        else
                        {
                            TextureArray textureArray = _currentState.TextureArrayRefs[binding].Array;

                            if (segment.Type != ResourceType.BufferTexture)
                            {
                                TextureRef[] textures = textureArray.GetTextureRefs();
                                bool hasTexture = segment.Type != ResourceType.Sampler;
                                bool hasSampler = segment.Type is ResourceType.Sampler or ResourceType.TextureAndSampler;

                                for (int i = 0; i < textures.Length; i++)
                                {
                                    TextureRef texture = textures[i];

                                    // The tonemap's inputs were only ever recorded from the
                                    // non-array branch. If they actually arrive through an
                                    // array segment, everything compared so far was a
                                    // different texture than the one the shader samples.
                                    if (HdrPassProbe.Enabled && hasTexture &&
                                        program.DebugLabel == _inputWatchLabel)
                                    {
                                        HdrPassProbe.NoteToneMapInput(1000 + binding + i, texture.Storage);
                                    }

                                    (ulong gpuAddress, IntPtr nativePtr) = hasTexture
                                        ? AddressForTexture(ref texture)
                                        : (0, IntPtr.Zero);

                                    MTLRenderStages renderStages = 0;

                                    if ((segment.Stages & ResourceStages.Vertex) != 0)
                                    {
                                        if (hasTexture)
                                        {
                                            vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                            vertResourceIdIndex++;
                                        }

                                        renderStages |= MTLRenderStages.RenderStageVertex;
                                    }

                                    if ((segment.Stages & ResourceStages.Fragment) != 0)
                                    {
                                        if (hasTexture)
                                        {
                                            fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                            fragResourceIdIndex++;
                                        }

                                        renderStages |= MTLRenderStages.RenderStageFragment;
                                    }

                                    if (hasTexture)
                                    {
                                        AddResource(nativePtr, MTLResourceUsage.Read, renderStages, in bindings);
                                    }
                                }

                                if (hasSampler)
                                {
                                    foreach (TextureRef texture in textures)
                                    {
                                        ulong gpuAddress = texture.Sampler != null
                                            ? texture.Sampler.Get(_pipeline.Cbs).Value.GpuResourceID._impl
                                            : 0;

                                        if ((segment.Stages & ResourceStages.Vertex) != 0)
                                        {
                                            vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                            vertResourceIdIndex++;
                                        }

                                        if ((segment.Stages & ResourceStages.Fragment) != 0)
                                        {
                                            fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                            fragResourceIdIndex++;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                TextureBuffer[] bufferTextures = textureArray.GetBufferTextureRefs();

                                for (int i = 0; i < bufferTextures.Length; i++)
                                {
                                    TextureBuffer bufferTexture = bufferTextures[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTextureBuffer(ref bufferTexture);

                                    MTLRenderStages renderStages = 0;

                                    if ((segment.Stages & ResourceStages.Vertex) != 0)
                                    {
                                        vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                        vertResourceIdIndex++;

                                        renderStages |= MTLRenderStages.RenderStageVertex;
                                    }

                                    if ((segment.Stages & ResourceStages.Fragment) != 0)
                                    {
                                        fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                        fragResourceIdIndex++;

                                        renderStages |= MTLRenderStages.RenderStageFragment;
                                    }

                                    AddResource(nativePtr, MTLResourceUsage.Read, renderStages, in bindings);
                                }
                            }
                        }
                        break;
                    case Constants.ImagesSetIndex:
                        if (!segment.IsArray)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                int index = binding + i;

                                ref ImageRef image = ref _currentState.ImageRefs[index];
                                (ulong gpuAddress, IntPtr nativePtr) = AddressForImage(ref image);

                                // A storage-image binding of the presented surface: the one
                                // writer path no other instrument sees.
                                if (UploadCorrelator.Enabled && image.Storage != null &&
                                    image.Storage.CanonicalPtr == UploadCorrelator.LastPresentedRoot)
                                {
                                    UploadCorrelator.NoteImageOnPresented(program.DebugLabel);
                                }

                                MTLRenderStages renderStages = 0;

                                if ((segment.Stages & ResourceStages.Vertex) != 0)
                                {
                                    vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                    vertResourceIdIndex++;
                                    renderStages |= MTLRenderStages.RenderStageVertex;
                                }

                                if ((segment.Stages & ResourceStages.Fragment) != 0)
                                {
                                    fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                    fragResourceIdIndex++;
                                    renderStages |= MTLRenderStages.RenderStageFragment;
                                }
                                AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, renderStages, in bindings);
                            }
                        }
                        else
                        {
                            ImageArray imageArray = _currentState.ImageArrayRefs[binding].Array;

                            if (segment.Type != ResourceType.BufferImage)
                            {
                                TextureRef[] images = imageArray.GetTextureRefs();

                                for (int i = 0; i < images.Length; i++)
                                {
                                    TextureRef image = images[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTexture(ref image);

                                    MTLRenderStages renderStages = 0;

                                    if ((segment.Stages & ResourceStages.Vertex) != 0)
                                    {
                                        vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                        vertResourceIdIndex++;
                                        renderStages |= MTLRenderStages.RenderStageVertex;
                                    }

                                    if ((segment.Stages & ResourceStages.Fragment) != 0)
                                    {
                                        fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                        fragResourceIdIndex++;
                                        renderStages |= MTLRenderStages.RenderStageFragment;
                                    }

                                    AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, renderStages, in bindings);
                                }
                            }
                            else
                            {
                                TextureBuffer[] bufferImages = imageArray.GetBufferTextureRefs();

                                for (int i = 0; i < bufferImages.Length; i++)
                                {
                                    TextureBuffer image = bufferImages[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTextureBuffer(ref image);

                                    MTLRenderStages renderStages = 0;

                                    if ((segment.Stages & ResourceStages.Vertex) != 0)
                                    {
                                        vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                        vertResourceIdIndex++;
                                        renderStages |= MTLRenderStages.RenderStageVertex;
                                    }

                                    if ((segment.Stages & ResourceStages.Fragment) != 0)
                                    {
                                        fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                        fragResourceIdIndex++;
                                        renderStages |= MTLRenderStages.RenderStageFragment;
                                    }

                                    AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, renderStages, in bindings);
                                }
                            }
                        }
                        break;
                }
            }

            if (program.ArgumentBufferSizes[setIndex] > 0)
            {
                if (UploadCorrelator.Enabled && program.IsTexelFetchComposite)
                {
                    UploadCorrelator.NoteResidency(bindings.Resources.Count);
                }

                vertArgBuffer.Holder.SetDataUnchecked(vertArgBuffer.Offset, MemoryMarshal.AsBytes(vertResourceIds));
                MTLBuffer mtlVertArgBuffer = _bufferManager.GetBuffer(vertArgBuffer.Handle, false).Get(_pipeline.Cbs).Value;
                bindings.VertexBuffers.Add(new BufferResource(mtlVertArgBuffer, (uint)vertArgBuffer.Range.Offset, SetIndexToBindingIndex(setIndex)));
            }

            if (program.FragArgumentBufferSizes[setIndex] > 0)
            {
                fragArgBuffer.Holder.SetDataUnchecked(fragArgBuffer.Offset, MemoryMarshal.AsBytes(fragResourceIds));
                MTLBuffer mtlFragArgBuffer = _bufferManager.GetBuffer(fragArgBuffer.Handle, false).Get(_pipeline.Cbs).Value;

                // The fragment Textures table of the stage program: the ids the CPU wrote and
                // the buffer+offset the GPU will read them from, so the table can be read back
                // at the pass end and compared - every object-level input is identical, so what
                // the GPU actually dereferences is the next thing to check.
                if (UploadCorrelator.Enabled && setIndex == Constants.TexturesSetIndex && _stageLabel.Length != 0 &&
                    program.DebugLabel != null && program.DebugLabel.StartsWith(_stageLabel, StringComparison.Ordinal))
                {
                    UploadCorrelator.NoteStageArgTable(mtlFragArgBuffer, fragArgBuffer.Range.Offset, fragResourceIds);
                }

                if (UploadCorrelator.Enabled && program.IsTexelFetchComposite)
                {
                    UploadCorrelator.NoteArgBuffer(
                        mtlFragArgBuffer.Contents, fragArgBuffer.Range.Offset, fragResourceIds);
                }
                bindings.FragmentBuffers.Add(new BufferResource(mtlFragArgBuffer, (uint)fragArgBuffer.Range.Offset, SetIndexToBindingIndex(setIndex)));
            }
        }

        private readonly void UpdateAndBind(Program program, uint setIndex, ref readonly ComputeEncoderBindings bindings)
        {
            ResourceBindingSegment[] bindingSegments = program.BindingSegments[setIndex];

            if (bindingSegments.Length == 0)
            {
                return;
            }

            ScopedTemporaryBuffer argBuffer = default;

            if (program.ArgumentBufferSizes[setIndex] > 0)
            {
                argBuffer = _bufferManager.ReserveOrCreate(_pipeline.Cbs, program.ArgumentBufferSizes[setIndex] * sizeof(ulong));
                bindings.TemporaryBuffers.Add(argBuffer);
            }

            Span<ulong> resourceIds = stackalloc ulong[program.ArgumentBufferSizes[setIndex]];
            int resourceIdIndex = 0;

            foreach (ResourceBindingSegment segment in bindingSegments)
            {
                int binding = segment.Binding;
                int count = segment.Count;

                switch (setIndex)
                {
                    case Constants.ConstantBuffersSetIndex:
                        for (int i = 0; i < count; i++)
                        {
                            int index = binding + i;

                            ref BufferRef buffer = ref _currentState.UniformBufferRefs[index];
                            (ulong gpuAddress, IntPtr nativePtr) = AddressForBuffer(ref buffer);

                            if ((segment.Stages & ResourceStages.Compute) != 0)
                            {
                                AddResource(nativePtr, MTLResourceUsage.Read, in bindings);
                                resourceIds[resourceIdIndex] = gpuAddress;
                                resourceIdIndex++;
                            }
                        }
                        break;
                    case Constants.StorageBuffersSetIndex:
                        for (int i = 0; i < count; i++)
                        {
                            int index = binding + i;

                            ref BufferRef buffer = ref _currentState.StorageBufferRefs[index];
                            (ulong gpuAddress, IntPtr nativePtr) = AddressForBuffer(ref buffer);

                            if (UploadCorrelator.Enabled && buffer.Buffer != null && buffer.Range.HasValue)
                            {
                                UploadCorrelator.NoteWriteTo(buffer.Buffer.GetUnsafe().Value.NativePtr, buffer.Range.Value.Offset, buffer.Range.Value.Size, "C:" + (program.DebugLabel is string cl && cl.Length > 6 ? cl[..6] : program.DebugLabel) + (buffer.Range.Value.Write ? "w" : "r"));
                            }
                            if ((segment.Stages & ResourceStages.Compute) != 0)
                            {
                                AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, in bindings);
                                resourceIds[resourceIdIndex] = gpuAddress;
                                resourceIdIndex++;
                            }
                        }
                        break;
                    case Constants.TexturesSetIndex:
                        if (!segment.IsArray)
                        {
                            bool hasTexture = segment.Type != ResourceType.Sampler;
                            bool hasSampler = segment.Type is ResourceType.Sampler or ResourceType.TextureAndSampler;

                            for (int i = 0; i < count; i++)
                            {
                                int index = binding + i;

                                ref TextureRef texture = ref _currentState.TextureRefs[index];
                                // TextureRefs is one array for every stage. If the texture in
                                // this slot was set for another stage than the one this
                                // program's segment needs, the shader samples the wrong one.
                                if (UploadCorrelator.Enabled && hasTexture && texture.Storage != null &&
                                    (segment.Stages & (ResourceStages)(1 << (int)texture.Stage)) == 0)
                                {
                                    UploadCorrelator.NoteStageMismatch(program.DebugLabel, index, segment.Stages, texture.Stage,
                                        texture.Storage is Texture mt ? $"{mt.Width}x{mt.Height}/{mt.MtlFormat}" : "?");
                                }
                                (ulong gpuAddress, IntPtr nativePtr) = hasTexture
                                    ? AddressForTexture(ref texture)
                                    : (0, IntPtr.Zero);

                                if (UploadCorrelator.Enabled && hasTexture && texture.Storage is Texture ctex && program.DebugLabel is string cpl &&
                                    (cpl.StartsWith("ae434b") || cpl.StartsWith("1f6a89")))
                                {
                                    UploadCorrelator.Seq($"CT:{cpl[..6]}:b{index}:{ctex.Width}x{ctex.Height}/{ctex.MtlFormat}#{(ctex.CanonicalPtr.ToInt64() & 0xFFFFFF):X}");
                                }
                                if ((segment.Stages & ResourceStages.Compute) != 0)
                                {
                                    if (hasTexture)
                                    {
                                        AddResource(nativePtr, MTLResourceUsage.Read, in bindings);
                                        resourceIds[resourceIdIndex] = gpuAddress;
                                        resourceIdIndex++;
                                    }

                                    if (hasSampler)
                                    {
                                        resourceIds[resourceIdIndex] = texture.Sampler != null
                                            ? texture.Sampler.Get(_pipeline.Cbs).Value.GpuResourceID._impl
                                            : 0;
                                        resourceIdIndex++;
                                    }
                                }
                            }
                        }
                        else
                        {
                            TextureArray textureArray = _currentState.TextureArrayRefs[binding].Array;

                            if (segment.Type != ResourceType.BufferTexture)
                            {
                                TextureRef[] textures = textureArray.GetTextureRefs();
                                bool hasTexture = segment.Type != ResourceType.Sampler;
                                bool hasSampler = segment.Type is ResourceType.Sampler or ResourceType.TextureAndSampler;

                                for (int i = 0; i < textures.Length; i++)
                                {
                                    TextureRef texture = textures[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = hasTexture
                                        ? AddressForTexture(ref texture)
                                        : (0, IntPtr.Zero);

                                    if ((segment.Stages & ResourceStages.Compute) != 0)
                                    {
                                        if (hasTexture)
                                        {
                                            AddResource(nativePtr, MTLResourceUsage.Read, in bindings);
                                            resourceIds[resourceIdIndex] = gpuAddress;
                                            resourceIdIndex++;
                                        }

                                    }
                                }

                                if (hasSampler)
                                {
                                    foreach (TextureRef texture in textures)
                                    {
                                        resourceIds[resourceIdIndex] = texture.Sampler != null
                                            ? texture.Sampler.Get(_pipeline.Cbs).Value.GpuResourceID._impl
                                            : 0;
                                        resourceIdIndex++;
                                    }
                                }
                            }
                            else
                            {
                                TextureBuffer[] bufferTextures = textureArray.GetBufferTextureRefs();

                                for (int i = 0; i < bufferTextures.Length; i++)
                                {
                                    TextureBuffer bufferTexture = bufferTextures[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTextureBuffer(ref bufferTexture);

                                    if ((segment.Stages & ResourceStages.Compute) != 0)
                                    {
                                        AddResource(nativePtr, MTLResourceUsage.Read, in bindings);
                                        resourceIds[resourceIdIndex] = gpuAddress;
                                        resourceIdIndex++;
                                    }
                                }
                            }
                        }
                        break;
                    case Constants.ImagesSetIndex:
                        if (!segment.IsArray)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                int index = binding + i;

                                ref ImageRef image = ref _currentState.ImageRefs[index];
                                if (UploadCorrelator.Enabled && image.Storage is Texture cimg && program.DebugLabel is string cil &&
                                    (cil.StartsWith("ae434b") || cil.StartsWith("1f6a89")))
                                {
                                    UploadCorrelator.Seq($"CI:{cil[..6]}:b{index}:{cimg.Width}x{cimg.Height}/{cimg.MtlFormat}#{(cimg.CanonicalPtr.ToInt64() & 0xFFFFFF):X}");
                                }
                                (ulong gpuAddress, IntPtr nativePtr) = AddressForImage(ref image);

                                // A storage-image binding of the presented surface: the one
                                // writer path no other instrument sees.
                                if (UploadCorrelator.Enabled && image.Storage != null &&
                                    image.Storage.CanonicalPtr == UploadCorrelator.LastPresentedRoot)
                                {
                                    UploadCorrelator.NoteImageOnPresented(program.DebugLabel);
                                }

                                if ((segment.Stages & ResourceStages.Compute) != 0)
                                {
                                    HdrPassProbe.NoteComputeImage(image.Storage);
                                    AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, in bindings);
                                    resourceIds[resourceIdIndex] = gpuAddress;
                                    resourceIdIndex++;
                                }
                            }
                        }
                        else
                        {
                            ImageArray imageArray = _currentState.ImageArrayRefs[binding].Array;

                            if (segment.Type != ResourceType.BufferImage)
                            {
                                TextureRef[] images = imageArray.GetTextureRefs();

                                for (int i = 0; i < images.Length; i++)
                                {
                                    TextureRef image = images[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTexture(ref image);

                                    if ((segment.Stages & ResourceStages.Compute) != 0)
                                    {
                                        HdrPassProbe.NoteComputeImage(image.Storage as Texture);
                                        AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, in bindings);
                                        resourceIds[resourceIdIndex] = gpuAddress;
                                        resourceIdIndex++;
                                    }
                                }
                            }
                            else
                            {
                                TextureBuffer[] bufferImages = imageArray.GetBufferTextureRefs();

                                for (int i = 0; i < bufferImages.Length; i++)
                                {
                                    TextureBuffer image = bufferImages[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTextureBuffer(ref image);

                                    if ((segment.Stages & ResourceStages.Compute) != 0)
                                    {
                                        AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, in bindings);
                                        resourceIds[resourceIdIndex] = gpuAddress;
                                        resourceIdIndex++;
                                    }
                                }
                            }
                        }
                        break;
                }
            }

            if (program.ArgumentBufferSizes[setIndex] > 0)
            {
                argBuffer.Holder.SetDataUnchecked(argBuffer.Offset, MemoryMarshal.AsBytes(resourceIds));
                MTLBuffer mtlArgBuffer = _bufferManager.GetBuffer(argBuffer.Handle, false).Get(_pipeline.Cbs).Value;
                bindings.Buffers.Add(new BufferResource(mtlArgBuffer, (uint)argBuffer.Range.Offset, SetIndexToBindingIndex(setIndex)));
            }
        }

        private static uint SetIndexToBindingIndex(uint setIndex)
        {
            return setIndex switch
            {
                Constants.ConstantBuffersSetIndex => Constants.ConstantBuffersIndex,
                Constants.StorageBuffersSetIndex => Constants.StorageBuffersIndex,
                Constants.TexturesSetIndex => Constants.TexturesIndex,
                Constants.ImagesSetIndex => Constants.ImagesIndex,
                _ => throw new NotImplementedException()
            };
        }

        private readonly void SetCullMode(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if (!_applied.Knows(renderCommandEncoder, AppliedRenderState.Field.Cull) ||
                _applied.CullMode != _currentState.CullMode)
            {
                _applied.CullMode = _currentState.CullMode;

                renderCommandEncoder.SetCullMode(_currentState.CullMode);
            }
        }

        private readonly void SetFrontFace(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if (!_applied.Knows(renderCommandEncoder, AppliedRenderState.Field.Winding) ||
                _applied.Winding != _currentState.Winding)
            {
                _applied.Winding = _currentState.Winding;

                renderCommandEncoder.SetFrontFacingWinding(_currentState.Winding);
            }
        }

        private readonly void SetStencilRefValue(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if (!_applied.Knows(renderCommandEncoder, AppliedRenderState.Field.StencilRef) ||
                _applied.FrontRefValue != _currentState.FrontRefValue ||
                _applied.BackRefValue != _currentState.BackRefValue)
            {
                _applied.FrontRefValue = _currentState.FrontRefValue;
                _applied.BackRefValue = _currentState.BackRefValue;

                renderCommandEncoder.SetStencilReferenceValues((uint)_currentState.FrontRefValue, (uint)_currentState.BackRefValue);
            }
        }
    }
}
