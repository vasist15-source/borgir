using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.Shader;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Nvn
{
    /// <summary>
    /// Execution: committing NVN state to GAL and the actions that need it (draws, dispatches,
    /// clears, copies, counters). Runs on the GPU thread only.
    /// </summary>
    sealed unsafe partial class NvnReplay
    {
        private IPipeline Pipeline => _renderer.Pipeline;

        // Render targets as bound (references held through SignalModifying, as TextureManager does).
        private readonly Image.Texture[] _rtColors = new Image.Texture[NvnState.MaxColorTargets];
        private readonly ITexture[] _rtBaseHost = new ITexture[NvnState.MaxColorTargets];   // the texture's own host storage (differs from _rtHost for 3D slices)
        private readonly Dictionary<(ITexture, int), ITexture> _sliceViews = new();
        private Image.Texture _rtDepth;
        private readonly ITexture[] _rtHost = new ITexture[NvnState.MaxColorTargets];
        private ITexture _rtHostDepth;
        private bool _rtDirty = true;

        private NvnProgram _boundProgram;
        private bool _submissionStarted;

        // Draw-to-draw caches: what the last draw committed, so a draw only redoes what changed (as the
        // Maxwell path's managers do). A dispatch or a new submission forces everything to be rebound.
        private NvnProgram _drawProgram;
        private InputTopology _drawTopology = unchecked((InputTopology)0xFF);
        private bool _forceRebind = true;
        private readonly List<MultiRange> _syncRanges = new();          // bound uniform and storage buffers
        private readonly List<MultiRange> _vertexRanges = new();        // bound vertex buffers
        private readonly List<BoundTexture> _boundTextures = new();
        private ulong _indexAddress, _indexSize;
        private int _indexType = -1;
        private MultiRange _indexRange;

        private struct BoundTexture
        {
            public ShaderStage Stage;
            public int Binding;
            public bool Image;
            public Target Target;
            public Image.Texture Texture;
            public ITexture Host;
            public ISampler Sampler;
        }

        // What the backend has bound, as the replay last set it. The Metal backend marks its argument
        // buffers dirty on every set, so unchanged bindings are not sent again (TextureBindingsManager and
        // BufferManager filter the same way). An entry counts only in the current epoch: anything that may
        // have changed the backend's bindings behind the replay's back starts a new one.
        private struct BufferBinding
        {
            public int Epoch;
            public BufferRange Range;
        }

        private struct TextureBinding
        {
            public int Epoch;
            public ShaderStage Stage;
            public ITexture Host;
            public ISampler Sampler;
        }

        private readonly BufferBinding[] _uniformBindings = new BufferBinding[256];
        private readonly BufferBinding[] _storageBindings = new BufferBinding[256];
        private readonly TextureBinding[] _textureBindings = new TextureBinding[256];
        private readonly TextureBinding[] _imageBindings = new TextureBinding[256];
        private readonly VertexAttribDescriptor[] _sentAttribs = new VertexAttribDescriptor[Constants.TotalVertexAttribs];
        private readonly VertexBufferDescriptor[] _sentVertexBuffers = new VertexBufferDescriptor[Constants.TotalVertexBuffers];
        private int _sentVertexBufferCount = -1;
        private bool _sentAttribsValid;
        private int _bindEpoch = 1;
        private bool _buffersModified;

        /// <summary>The backend's bindings may no longer be what the replay last sent.</summary>
        private void InvalidateBindings()
        {
            _bindEpoch++;
            _sentAttribsValid = false;
            _sentVertexBufferCount = -1;
            _indexType = -1;
            _forceRebind = true;
        }

        /// <summary>The buffer cache merged or replaced buffers: every bound range may name a dead buffer.</summary>
        private void OnBuffersModified()
        {
            _buffersModified = true;
            InvalidateBindings();
        }

        private static bool SameRange(in BufferRange a, in BufferRange b)
        {
            return a.Handle == b.Handle && a.Offset == b.Offset && a.Size == b.Size && a.Write == b.Write;
        }

        private void SetTexture(ShaderStage stage, int binding, ITexture host, ISampler sampler)
        {
            ref TextureBinding t = ref _textureBindings[binding & 0xFF];

            if (t.Epoch == _bindEpoch && t.Stage == stage && t.Host == host && t.Sampler == sampler)
            {
                return;
            }

            t = new TextureBinding { Epoch = _bindEpoch, Stage = stage, Host = host, Sampler = sampler };
            Pipeline.SetTextureAndSampler(stage, binding, host, sampler);
            _textureSets++;
        }

        private void SetImage(ShaderStage stage, int binding, ITexture host)
        {
            ref TextureBinding t = ref _imageBindings[binding & 0xFF];

            if (t.Epoch == _bindEpoch && t.Stage == stage && t.Host == host)
            {
                return;
            }

            t = new TextureBinding { Epoch = _bindEpoch, Stage = stage, Host = host };
            Pipeline.SetImage(stage, binding, host);
        }

        private long _textureSets, _uniformSets, _segmentUploads, _textureCacheHits, _uniformCacheHits;

        /// <summary>Guest address of pv_ctl.pool_gen: bumped by the guest after every descriptor registration.</summary>
        public ulong PoolGenerationAddress { get; set; }

        private ulong _poolGeneration;

        // Per (stage, binding): the texture and sampler the last resolve gave, valid while the handle, the
        // pools and their contents (pool generation) are the same.
        private struct TextureCacheEntry
        {
            public int Epoch;
            public ulong Generation, TexturePool, SamplerPool;
            public int Packed;
            public TextureFormat ImageFormat;
            public Image.Texture Texture;
            public ISampler Sampler;
        }

        private readonly TextureCacheEntry[] _textureCache = new TextureCacheEntry[NvnState.Stages * 256];
        private readonly TextureCacheEntry[] _imageCache = new TextureCacheEntry[NvnState.Stages * 256];

        // Per uniform slot / vertex stream: the buffer range the last lookup of an address gave.
        private struct RangeCacheEntry
        {
            public int Epoch;
            public ulong Address, Size;
            public BufferRange Range;
            public MultiRange Memory;
        }

        private readonly RangeCacheEntry[] _uniformCache = new RangeCacheEntry[NvnState.Stages * NvnState.MaxUniformBuffers];
        private readonly RangeCacheEntry[] _streamCache = new RangeCacheEntry[Constants.TotalVertexBuffers];

        /// <summary>The buffer range for an address, from the slot's cache when the address and size are the same.</summary>
        private BufferRange CachedBuffer(ref RangeCacheEntry c, ulong address, ulong size, BufferStage stage, out MultiRange memory)
        {
            if (c.Epoch == _bindEpoch && c.Address == address && c.Size == size)
            {
                memory = c.Memory;

                if (c.Range.Handle != BufferHandle.Null)
                {
                    _physical.BufferCache.SynchronizeBufferRange(memory);
                }

                _uniformCacheHits++;

                return c.Range;
            }

            BufferRange range = GetBuffer(address, size, stage, false, out memory);
            c = new RangeCacheEntry { Epoch = _bindEpoch, Address = address, Size = size, Range = range, Memory = memory };

            return range;
        }

        private static bool SameVertexBuffer(in VertexBufferDescriptor a, in VertexBufferDescriptor b)
        {
            return SameRange(a.Buffer, b.Buffer) && a.Stride == b.Stride && a.Divisor == b.Divisor;
        }

        // Replay time per phase (Stopwatch ticks), reported with the stats line.
        private long _tProgram, _tFixed, _tVertex, _tResources, _tSubmit, _tUniforms, _tStorages, _tTextures, _rebinds, _syncOnly;

        private bool _supportsScaledVertexFormats => _context.Capabilities.SupportsScaledVertexFormats && !_mayConvertVtgToCompute;
        private bool _mayConvertVtgToCompute => ShaderCache.MayConvertVtgToCompute(ref _context.Capabilities);

        // Sampled and storage textures per texture pool slot, held while bound to the slot's entry.
        private readonly Dictionary<(ulong Table, int Id, int Format), PoolTexture> _poolTextures = new();

        private sealed class PoolTexture
        {
            public PvTexEntry Entry;
            public Image.Texture Texture;
        }

        // The driver's constant segments (c0, c2) per stage and the shaders' c1 data, as host buffers.
        private readonly BufferHandle[] _c0Buffers = new BufferHandle[NvnState.Stages];
        private readonly BufferHandle[] _c2Buffers = new BufferHandle[NvnState.Stages];
        private readonly Dictionary<Hash128, (BufferHandle Handle, int Size)> _c1Buffers = new();

        private readonly BufferAssignment[] _uniforms = new BufferAssignment[96];
        private readonly BufferAssignment[] _storages = new BufferAssignment[96];
        private readonly VertexAttribDescriptor[] _attribs = new VertexAttribDescriptor[Constants.TotalVertexAttribs];
        private readonly VertexBufferDescriptor[] _vertexBuffers = new VertexBufferDescriptor[Constants.TotalVertexBuffers];
        private readonly Viewport[] _viewports = new Viewport[NvnState.MaxViewports];
        private readonly Rectangle<int>[] _scissors = new Rectangle<int>[NvnState.MaxViewports];
        private readonly uint[] _colorMasks = new uint[NvnState.MaxColorTargets];

        /// <summary>Depth mode the game chose (nvnDeviceSetDepthMode): true = GL's -1..1 clip range (TOTK).</summary>
        public bool DepthMinusOneToOne { get; set; } = true;

        /// <summary>
        /// Window origin the game chose (nvnDeviceSetWindowOriginMode): true = upper left (TOTK), where
        /// clip-space +Y is the first row, as in Metal. GAL viewports follow GL (the Metal backend flips
        /// them and the winding), so an upper-left viewport goes down with a negative height and the
        /// front face swapped - what the Maxwell path ends up passing for the same NVN state.
        /// </summary>
        public bool OriginUpperLeft { get; set; } = true;

        // ---------- Submission boundaries ----------

        private void BeginSubmission()
        {
            if (_submissionStarted)
            {
                return;
            }

            _submissionStarted = true;
            ReleaseFinalized();

            // CPU writes since the last submission become visible to buffer and texture synchronisation.
            _context.AdvanceSequence();

            // The translated shaders read the support buffer (render scale, viewport size): created and
            // bound on its first commit, as the Maxwell path does before its first draw.
            _context.SupportBufferUpdater.Commit();
            _state.Changed = NvnState.Dirty.All;
            _rtDirty = true;
            _boundProgram = null;
            _drawProgram = null;
            InvalidateBindings();

            for (int i = 0; i < NvnState.Stages; i++)
            {
                _state.C0Dirty[i] = true;
                _state.C2Dirty[i] = true;
            }
        }

        private void EndSubmission()
        {
            if (!_submissionStarted)
            {
                return;
            }

            _submissionStarted = false;

            // Render targets stay bound across submissions but must count as modified again, as
            // TextureManager.RefreshModifiedTextures does at every sync point.
            for (int i = 0; i < _rtColors.Length; i++)
            {
                _rtColors[i]?.SignalModifying(false);
                _rtColors[i] = null;
                _rtHost[i] = null;
                _rtBaseHost[i] = null;
            }

            _rtDepth?.SignalModifying(false);
            _rtDepth = null;
            _rtHostDepth = null;
            _rtDirty = true;

            _renderer.UpdateCounters();
        }

        // ---------- Render targets ----------

        private void RenderTargetsChanged() => _rtDirty = true;

        private Image.Texture FindTexture(in PvTex tex, in PvView view, bool hasView, TextureSearchFlags flags)
        {
            TextureInfo info = NvnTextures.GetInfo(tex, view, hasView, out int layerSize, out bool valid);

            if (!valid)
            {
                Report($"texture format 0x{(hasView && view.SetFormat ? view.Format : tex.D.Format):X} has no GAL format");
                return null;
            }

            return _physical.TextureCache.FindOrCreateTexture(_gmm, flags, info, layerSize);
        }

        /// <summary>
        /// The cache may give a bound texture new host storage (recreated, rescaled, replaced by a view):
        /// the pipeline must then get the new one, as TextureManager re-reads HostTexture on every commit.
        /// </summary>
        private bool RenderTargetHostChanged()
        {
            for (int i = 0; i < _rtColors.Length; i++)
            {
                if (_rtColors[i] != null && _rtColors[i].HostTexture != _rtBaseHost[i])
                {
                    return true;
                }
            }

            return _rtDepth != null && _rtDepth.HostTexture != _rtHostDepth;
        }

        /// <summary>A render target view of one slice of a 3D texture (cached per host texture and slice).</summary>
        private ITexture SliceView(Image.Texture volume, int slice)
        {
            ITexture host = volume.HostTexture;

            if (!_sliceViews.TryGetValue((host, slice), out ITexture view))
            {
                TextureInfo vi = volume.Info;
                FormatInfo fi = vi.FormatInfo;

                view = host.CreateView(
                    new TextureCreateInfo(vi.Width, vi.Height, 1, 1, 1, fi.BlockWidth, fi.BlockHeight, fi.BytesPerPixel, fi.Format,
                        DepthStencilMode.Depth, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha),
                    slice,
                    0);

                _sliceViews[(host, slice)] = view;
            }

            return view;
        }

        private void CommitRenderTargets()
        {
            NvnState s = _state;
            bool changed = false;

            for (int i = 0; i < NvnState.MaxColorTargets; i++)
            {
                ref PvRtTarget t = ref s.Colors[i];
                bool present = i < s.ColorCount && t.Present != 0;

                // One slice of a 3D texture: the whole volume at the view's level, rendered through a slice view.
                int slice = -1;
                Image.Texture texture;

                if (present && t.Tex.D.Target == 2 && t.HasView != 0 && t.View.SetLayers)
                {
                    PvView level = t.View;
                    level.Set = (byte)(level.Set & ~2);
                    slice = t.View.MinLayer;
                    texture = FindTexture(t.Tex, level, true, TextureSearchFlags.WithUpscale);
                }
                else
                {
                    texture = present ? FindTexture(t.Tex, t.View, t.HasView != 0, TextureSearchFlags.WithUpscale) : null;
                }

                if (texture != _rtColors[i])
                {
                    _rtColors[i]?.SignalModifying(false);

                    if (texture != null)
                    {
                        texture.SynchronizeMemory();
                        texture.SignalModifying(true);
                    }

                    _rtColors[i] = texture;
                }

                ITexture baseHost = texture?.HostTexture;
                ITexture host = baseHost;

                if (slice >= 0 && baseHost != null)
                {
                    host = SliceView(texture, slice);
                }

                _rtBaseHost[i] = baseHost;

                if (_rtHost[i] != host)
                {
                    _rtHost[i] = host;
                    changed = true;
                }
            }

            Image.Texture depth = s.HasDepth && s.Depth.Present != 0 ? FindTexture(s.Depth.Tex, s.Depth.View, s.Depth.HasView != 0, TextureSearchFlags.WithUpscale) : null;

            if (depth != _rtDepth)
            {
                _rtDepth?.SignalModifying(false);

                if (depth != null)
                {
                    depth.SynchronizeMemory();
                    depth.SignalModifying(true);
                }

                _rtDepth = depth;
            }

            if (_rtHostDepth != depth?.HostTexture)
            {
                _rtHostDepth = depth?.HostTexture;
                changed = true;
            }

            if (changed)
            {
                Pipeline.SetRenderTargets(_rtHost, _rtHostDepth);
            }

            _rtDirty = false;
        }

        // ---------- Conditional rendering ----------

        private bool RenderEnabled()
        {
            NvnState s = _state;

            if (s.ConditionAddress == 0)
            {
                return s.RenderEnable;
            }

            // An occlusion result still on its way: render, as the Maxwell path does when its counter is pending.
            if (CounterPending(s.ConditionAddress) || CounterPending(s.ConditionAddress + 16))
            {
                return true;
            }

            // Two NVNcounterData {counter, timestamp} 16 bytes apart; render when the counters are
            // equal (mode 0) or not equal (mode 1). TOTK gates clears with counters the CPU sets and
            // a timestamp report resets, which the replay writes in order (ExecuteReportCounter).
            ulong a = _physical.Read<ulong>(s.ConditionAddress);
            ulong b = _physical.Read<ulong>(s.ConditionAddress + 16);

            return s.ConditionMode == 0 ? a == b : a != b;
        }

        // ---------- Draws ----------

        private void UpdateTranslationState(int mode, bool compute)
        {
            NvnState s = _state;
            NvnTranslationState x = _xlate;

            x.DepthMinusOneToOne = DepthMinusOneToOne;
            x.Topology = mode switch
            {
                0 => InputTopology.Points,
                1 or 2 or 3 => InputTopology.Lines,
                10 or 11 => InputTopology.LinesAdjacency,
                12 or 13 => InputTopology.TrianglesAdjacency,
                _ => InputTopology.Triangles,
            };
            x.AlphaToCoverage = (s.MultisampleLo >> 16 & 0xFF) != 0;

            if (compute)
            {
                return;
            }

            for (int i = 0; i < 32; i++)
            {
                uint va = s.VertexAttribs[i];
                int fmt = (int)(va & 0xFF);

                x.AttributeTypes[i] = fmt != 0 && i < s.VertexAttribCount
                    ? NvnTranslationState.VertexAttributeTypeOf(NvnGal.Format(fmt).Format, _supportsScaledVertexFormats, _mayConvertVtgToCompute)
                    : AttributeType.Float;
            }

            for (int i = 0; i < 8; i++)
            {
                ref PvRtTarget t = ref s.Colors[i];
                AttributeType type = AttributeType.Float;

                if (i < s.ColorCount && t.Present != 0)
                {
                    int fmt = t.HasView != 0 && t.View.SetFormat ? t.View.Format : t.Tex.D.Format;
                    type = NvnTranslationState.AttributeTypeOf(NvnGal.Format(fmt).Format);
                }

                x.FragmentOutputTypes[i] = type;
            }
        }

        private static InputTopology TopologyOf(int mode) => mode switch
        {
            0 => InputTopology.Points,
            1 or 2 or 3 => InputTopology.Lines,
            10 or 11 => InputTopology.LinesAdjacency,
            12 or 13 => InputTopology.TrianglesAdjacency,
            _ => InputTopology.Triangles,
        };

        private const NvnState.Dirty TranslationInputs =
            NvnState.Dirty.Program | NvnState.Dirty.VertexAttribs | NvnState.Dirty.RenderTargets | NvnState.Dirty.Multisample | NvnState.Dirty.Textures;

        private bool PrepareDraw(int mode)
        {
            BeginSubmission();

            if (!RenderEnabled())
            {
                return false;
            }

            NvnState s = _state;

            if (!s.HasProgram)
            {
                return false;
            }

            long t0 = Stopwatch.GetTimestamp();
            NvnState.Dirty d = s.Changed;
            bool rebind = _forceRebind;

            if (PoolGenerationAddress != 0)
            {
                _poolGeneration = _physical.Read<ulong>(PoolGenerationAddress);
            }

            if (_rtDirty || RenderTargetHostChanged())
            {
                CommitRenderTargets();
                d |= NvnState.Dirty.RenderTargets;
            }

            InputTopology topology = TopologyOf(mode);

            if (topology != _drawTopology)
            {
                _drawTopology = topology;
                d |= NvnState.Dirty.Program;
            }

            NvnProgram program = _drawProgram;

            if (program == null || rebind || (d & TranslationInputs) != 0)
            {
                UpdateTranslationState(mode, false);
                program = _programs.Get(s.Program, _xlate, out string error);

                if (program == null || program.Failed || program.Host == null || program.Compute)
                {
                    Report($"program not drawable: {error ?? (program?.Compute == true ? "compute program bound for a draw" : "no host program")}");
                    _drawProgram = null;
                    return false;
                }
            }

            if (program != _drawProgram)
            {
                _drawProgram = program;
                rebind = true;
            }

            long t1 = Stopwatch.GetTimestamp();
            CommitFixedFunction(d);
            long t2 = Stopwatch.GetTimestamp();

            _buffersModified = false;
            CommitVertexInput(d, rebind);
            long t3 = Stopwatch.GetTimestamp();
            CommitResources(program, rebind || (d & (NvnState.Dirty.UniformBuffers | NvnState.Dirty.StorageBuffers | NvnState.Dirty.Textures)) != 0);

            // Buffers the cache merged while bindings were being resolved: ranges resolved before the merge
            // name the old buffers, so everything is resolved again.
            if (_buffersModified)
            {
                _buffersModified = false;
                CommitVertexInput(d, true);
                CommitResources(program, true);
            }

            // Binding textures can give a bound render target new storage: TextureManager re-reads after binding too.
            if (RenderTargetHostChanged())
            {
                CommitRenderTargets();
            }

            if (_boundProgram != program)
            {
                Pipeline.SetProgram(program.Host);
                _boundProgram = program;
            }

            Pipeline.SetPrimitiveTopology(NvnGal.Topology(mode));

            s.Changed = NvnState.Dirty.None;
            _forceRebind = false;

            bool rt3D = TraceEnabled && s.ColorCount > 0 && s.Colors[0].Present != 0 && s.Colors[0].Tex.D.Target == 2 && s.Colors[0].Tex.D.Format != 0x1 && _rt3DTraced < 80;
            bool rtAddress = TraceRtAddress != 0 && _rtTraced < 60 && s.ColorCount > 0 && s.Colors[0].Tex.Addr == TraceRtAddress;

            if (_tracing || rt3D || rtAddress)
            {
                if (!_tracing)
                {
                    _rtTraced += rtAddress ? 1 : 0;
                    _rt3DTraced += rt3D ? 1 : 0;
                    _tracingRt = true;
                }

                TraceDraw(mode, program);
            }

            long t4 = Stopwatch.GetTimestamp();
            _tProgram += t1 - t0;
            _tFixed += t2 - t1;
            _tVertex += t3 - t2;
            _tResources += t4 - t3;

            return true;
        }

        private void ExecuteDraw(int mode, int first, int count, int baseInstance, int instances)
        {
            if (count <= 0 || instances <= 0 || !PrepareDraw(mode))
            {
                return;
            }

            Pipeline.Draw(count, instances, first, baseInstance);
            _draws++;
            _frameDraws++;

            if (_tracing || _tracingRt)
            {
                _tracingRt = false;
                DumpAfterDraw();
            }
        }

        private void ExecuteDrawIndexed(int mode, int type, int count, int baseVertex, int baseInstance, int instances, ulong indexAddress)
        {
            if (count <= 0 || instances <= 0 || !PrepareDraw(mode))
            {
                return;
            }

            int elementSize = 1 << Math.Clamp(type, 0, 2);
            ulong size = (ulong)count * (ulong)elementSize;

            if (indexAddress == _indexAddress && size <= _indexSize && type == _indexType)
            {
                _physical.BufferCache.SynchronizeBufferRange(_indexRange);
            }
            else
            {
                BufferRange indices = GetBuffer(indexAddress, size, BufferStage.IndexBuffer, false, out MultiRange range);

                if (indices.Handle == BufferHandle.Null)
                {
                    Report("index buffer not mapped");
                    _indexType = -1;
                    return;
                }

                Pipeline.SetIndexBuffer(indices, NvnGal.IndexType(type));
                _indexAddress = indexAddress;
                _indexSize = size;
                _indexType = type;
                _indexRange = range;
            }

            Pipeline.DrawIndexed(count, instances, 0, baseVertex, baseInstance);
            _draws++;
            _frameDraws++;

            if (_tracing || _tracingRt)
            {
                _tracingRt = false;
                DumpAfterDraw();
            }
        }

        private void ExecuteDrawIndirect(bool indexed, int mode, int type, ulong indexAddress, ulong indirectAddress)
        {
            if (!PrepareDraw(mode))
            {
                return;
            }

            BufferRange indirect = GetBuffer(indirectAddress, indexed ? 20UL : 16UL, BufferStage.Indirect, false);

            if (indexed)
            {
                // The index count is only known to the GPU; the buffer to its mapped end is bound.
                ulong size = _gmm.GetMappedSize(indexAddress, 64UL << 20);
                Pipeline.SetIndexBuffer(GetBuffer(indexAddress, size, BufferStage.IndexBuffer, false), NvnGal.IndexType(type));
                _indexType = -1;
                Pipeline.DrawIndexedIndirect(indirect);
            }
            else
            {
                Pipeline.DrawIndirect(indirect);
            }

            _draws++;
        }

        // ---------- Compute ----------

        private void ExecuteDispatch(int x, int y, int z)
        {
            BeginSubmission();

            if (x <= 0 || y <= 0 || z <= 0 || !_state.HasProgram)
            {
                return;
            }

            UpdateTranslationState(0, true);

            NvnProgram program = _programs.Get(_state.Program, _xlate, out string error);

            if (program == null || program.Failed || program.Host == null || !program.Compute)
            {
                Report($"program not dispatchable: {error ?? "not a compute program"}");
                return;
            }

            // The driver keeps the group counts in c0 for the shader to read.
            byte[] c0 = _state.C0[5];
            BitConverter.TryWriteBytes(c0.AsSpan(0x200), x);
            BitConverter.TryWriteBytes(c0.AsSpan(0x204), y);
            BitConverter.TryWriteBytes(c0.AsSpan(0x208), z);
            _state.C0Dirty[5] = true;

            CommitResources(program, true);

            Pipeline.SetProgram(program.Host);
            _boundProgram = program;
            Pipeline.DispatchCompute(x, y, z);
            _dispatches++;

            if (TraceEnabled && _rtTraced < 60)
            {
                foreach (BoundTexture b in _boundTextures)
                {
                    if (b.Image && b.Texture.Info.Target == Target.Texture3D)
                    {
                        _rtTraced++;
                        System.Text.StringBuilder sb = new();
                        sb.Append($"nvnpv trace dispatch {x}x{y}x{z} cs {program.Stages[5].Hash.Low:X16}");

                        foreach (BoundTexture t in _boundTextures)
                        {
                            TextureInfo ti = t.Texture.Info;
                            sb.Append($" | {(t.Image ? "img" : "tex")}{t.Binding} 0x{ti.GpuAddress:X} {ti.Width}x{ti.Height}x{ti.DepthOrLayers} {ti.FormatInfo.Format} {ti.Target}");
                        }

                        Logger.Info?.Print(LogClass.Gpu, sb.ToString());
                        break;
                    }
                }
            }

            // GAL binding numbers are shared between compute and graphics: the next draw rebinds everything.
            InvalidateBindings();
        }

        private void ExecuteDispatchIndirect(ulong address)
        {
            int x = (int)_physical.Read<uint>(address);
            int y = (int)_physical.Read<uint>(address + 4);
            int z = (int)_physical.Read<uint>(address + 8);

            ExecuteDispatch(x, y, z);
        }

        // ---------- Fixed-function state ----------

        private void CommitFixedFunction(NvnState.Dirty d)
        {
            NvnState s = _state;

            if (d == NvnState.Dirty.None)
            {
                return;
            }

            if ((d & NvnState.Dirty.Viewports) != 0)
            {
                int n = Math.Max(1, Math.Max(s.ViewportCount, s.DepthRangeCount));

                for (int i = 0; i < n; i++)
                {
                    float near = s.DepthRanges[i * 2];
                    float far = s.DepthRanges[i * 2 + 1];

                    float x = s.Viewports[i * 4], y = s.Viewports[i * 4 + 1], w = s.Viewports[i * 4 + 2], h = s.Viewports[i * 4 + 3];

                    _viewports[i] = new Viewport(
                        OriginUpperLeft ? new Rectangle<float>(x, y + h, w, -h) : new Rectangle<float>(x, y, w, h),
                        ViewportSwizzle.PositiveX,
                        ViewportSwizzle.PositiveY,
                        ViewportSwizzle.PositiveZ,
                        ViewportSwizzle.PositiveW,
                        near,
                        far);
                }

                Pipeline.SetDepthMode(DepthMinusOneToOne ? DepthMode.MinusOneToOne : DepthMode.ZeroToOne);
                Pipeline.SetViewports(_viewports.AsSpan(0, n));
            }

            CommitScissors();

            if ((d & (NvnState.Dirty.Blend | NvnState.Dirty.Color | NvnState.Dirty.BlendColor)) != 0)
            {
                ColorF constant = new(s.BlendColor[0], s.BlendColor[1], s.BlendColor[2], s.BlendColor[3]);

                for (int i = 0; i < NvnState.MaxColorTargets; i++)
                {
                    ulong b = s.Blend[i];
                    bool enable = (s.ColorState >> i & 1) != 0;

                    Pipeline.SetBlendState(i, new BlendDescriptor(
                        enable,
                        constant,
                        NvnGal.BlendOp((int)(b >> 31 & 7)),
                        NvnGal.BlendFactor((int)(b >> 3 & 0x7F)),
                        NvnGal.BlendFactor((int)(b >> 10 & 0x7F)),
                        NvnGal.BlendOp((int)(b >> 34 & 7)),
                        NvnGal.BlendFactor((int)(b >> 17 & 0x7F)),
                        NvnGal.BlendFactor((int)(b >> 24 & 0x7F))));
                }

                int logicOp = (int)(s.ColorState >> 8 & 0xF);
                Pipeline.SetLogicOpState(logicOp != 3, NvnGal.LogicalOp(logicOp));

                int alphaFunc = (int)(s.ColorState >> 12 & 0xF);
                Pipeline.SetAlphaTest(alphaFunc != 0 && alphaFunc != 8, s.AlphaRef, NvnGal.CompareOp(alphaFunc == 0 ? 8 : alphaFunc));
            }

            if ((d & NvnState.Dirty.ChannelMask) != 0)
            {
                for (int i = 0; i < NvnState.MaxColorTargets; i++)
                {
                    _colorMasks[i] = s.ChannelMask >> (i * 4) & 0xF;
                }

                Pipeline.SetRenderTargetColorMasks(_colorMasks);
            }

            if ((d & (NvnState.Dirty.DepthStencil | NvnState.Dirty.Stencil)) != 0)
            {
                ulong v = s.DepthStencil;

                Pipeline.SetDepthTest(new DepthTestDescriptor((v & 1) != 0, (v >> 1 & 1) != 0, NvnGal.CompareOp((int)(v >> 2 & 0xF))));
                Pipeline.SetStencilTest(new StencilTestDescriptor(
                    (v >> 6 & 1) != 0,
                    NvnGal.CompareOp((int)(v >> 7 & 0xF)),
                    NvnGal.StencilOp((int)(v >> 15 & 0xF)),
                    NvnGal.StencilOp((int)(v >> 23 & 0xF)),
                    NvnGal.StencilOp((int)(v >> 19 & 0xF)),
                    s.StencilRef[0],
                    s.StencilValueMask[0],
                    s.StencilWriteMask[0],
                    NvnGal.CompareOp((int)(v >> 11 & 0xF)),
                    NvnGal.StencilOp((int)(v >> 27 & 0xF)),
                    NvnGal.StencilOp((int)(v >> 35 & 0xF)),
                    NvnGal.StencilOp((int)(v >> 31 & 0xF)),
                    s.StencilRef[1],
                    s.StencilValueMask[1],
                    s.StencilWriteMask[1]));
            }

            if ((d & (NvnState.Dirty.Polygon | NvnState.Dirty.PolygonOffset)) != 0)
            {
                uint p = s.Polygon;
                (bool cull, Face face) = NvnGal.Cull((int)(p & 3));
                PolygonMode mode = NvnGal.PolygonMode((int)(p >> 3 & 3));

                Pipeline.SetFaceCulling(cull, face);
                FrontFace front = NvnGal.FrontFace((int)(p >> 2 & 1));

                if (OriginUpperLeft)
                {
                    front = front == FrontFace.Clockwise ? FrontFace.CounterClockwise : FrontFace.Clockwise;
                }

                Pipeline.SetFrontFace(front);
                Pipeline.SetPolygonMode(mode, mode);
                Pipeline.SetDepthBias((PolygonModeMask)(p >> 5 & 7), s.PolygonOffsetFactor, s.PolygonOffsetUnits, s.PolygonOffsetClamp);
            }

            if ((d & NvnState.Dirty.Multisample) != 0)
            {
                bool a2c = (s.MultisampleLo >> 16 & 0xFF) != 0;
                bool dither = (s.MultisampleLo >> 24 & 0xFF) != 0;
                Pipeline.SetMultisampleState(new MultisampleDescriptor(a2c, dither, false));
            }

            if ((d & NvnState.Dirty.DepthClamp) != 0)
            {
                Pipeline.SetDepthClamp(s.DepthClamp);
            }

            if ((d & NvnState.Dirty.RasterizerDiscard) != 0)
            {
                Pipeline.SetRasterizerDiscard(s.RasterizerDiscard);
            }

            if ((d & NvnState.Dirty.PrimitiveRestart) != 0)
            {
                Pipeline.SetPrimitiveRestart(s.PrimitiveRestart, s.PrimitiveRestartIndex);
            }

            if ((d & NvnState.Dirty.LineWidth) != 0)
            {
                Pipeline.SetLineParameters(s.LineWidth, false);
            }

            if ((d & NvnState.Dirty.PointSize) != 0)
            {
                Pipeline.SetPointParameters(s.PointSize, true, false, Origin.UpperLeft);
            }
        }

        // ---------- Vertex input ----------

        private void CommitVertexInput(NvnState.Dirty d, bool rebind)
        {
            NvnState s = _state;
            BufferCache cache = _physical.BufferCache;

            if (!rebind && (d & (NvnState.Dirty.VertexAttribs | NvnState.Dirty.VertexStreams | NvnState.Dirty.VertexBuffers)) == 0)
            {
                foreach (MultiRange range in _vertexRanges)
                {
                    cache.SynchronizeBufferRange(range);
                }

                return;
            }

            int maxStream = -1;

            for (int i = 0; i < _attribs.Length; i++)
            {
                uint va = s.VertexAttribs[i];
                int fmt = (int)(va & 0xFF);

                if (i >= s.VertexAttribCount || fmt == 0)
                {
                    _attribs[i] = new VertexAttribDescriptor(0, 0, true, Format.R32G32B32A32Float);
                    continue;
                }

                int stream = (int)(va >> 27);
                Format format = NvnGal.Format(fmt).Format;

                if (format == NvnGal.Unknown)
                {
                    format = Format.R32G32B32A32Float;
                }
                else if (!_supportsScaledVertexFormats)
                {
                    format = NvnGal.NonScaled(format);
                }

                _attribs[i] = new VertexAttribDescriptor(stream, (int)(va >> 8 & 0x7FFFF), false, format);
                maxStream = Math.Max(maxStream, stream);
            }

            if (!_sentAttribsValid || !_attribs.AsSpan().SequenceEqual(_sentAttribs))
            {
                Pipeline.SetVertexAttribs(_attribs);
                _attribs.CopyTo(_sentAttribs, 0);
                _sentAttribsValid = true;
            }

            int streams = Math.Min(Math.Max(maxStream + 1, s.VertexStreamCount), _vertexBuffers.Length);
            _vertexRanges.Clear();

            for (int i = 0; i < _vertexBuffers.Length; i++)
            {
                ulong address = i < streams ? s.VertexBufferAddress[i] : 0;
                ulong size = s.VertexBufferSize[i];

                if (address == 0 || size == 0)
                {
                    _vertexBuffers[i] = new VertexBufferDescriptor(BufferRange.Empty, 0, 0);
                    continue;
                }

                BufferRange range = CachedBuffer(ref _streamCache[i], address, size, BufferStage.VertexBuffer, out MultiRange memory);

                if (range.Handle != BufferHandle.Null)
                {
                    _vertexRanges.Add(memory);
                }

                _vertexBuffers[i] = new VertexBufferDescriptor(range, (int)s.StreamStride[i], (int)s.StreamDivisor[i]);
            }

            int count = Math.Max(streams, 1);

            bool same = count == _sentVertexBufferCount;

            for (int i = 0; same && i < count; i++)
            {
                same = SameVertexBuffer(_vertexBuffers[i], _sentVertexBuffers[i]);
            }

            if (!same)
            {
                Pipeline.SetVertexBuffers(_vertexBuffers.AsSpan(0, count));
                _vertexBuffers.AsSpan(0, count).CopyTo(_sentVertexBuffers);
                _sentVertexBufferCount = count;
            }
        }

        // ---------- Buffers ----------

        private BufferRange GetBuffer(ulong gpuVa, ulong size, BufferStage stage, bool write, bool storage = false)
        {
            return GetBuffer(gpuVa, size, stage, write, out _, storage);
        }

        private BufferRange GetBuffer(ulong gpuVa, ulong size, BufferStage stage, bool write, out MultiRange range, bool storage = false)
        {
            range = default;

            if (gpuVa == 0 || size == 0)
            {
                return BufferRange.Empty;
            }

            BufferCache cache = _physical.BufferCache;
            range = cache.TranslateAndCreateBuffer(_gmm, gpuVa, size, stage);

            if (range.Count == 1 && range.GetSubRange(0).Address == MemoryManager.PteUnmapped)
            {
                range = default;
                return BufferRange.Empty;
            }

            BufferRange result = storage ? cache.GetBufferRangeAligned(range, stage, write) : cache.GetBufferRange(range, stage, write);
            cache.SynchronizeBufferRange(range);

            return result;
        }

        private static ShaderStage GalStage(int nvnStage) => nvnStage switch
        {
            NvnShaderStage.StageVertex => ShaderStage.Vertex,
            NvnShaderStage.StageFragment => ShaderStage.Fragment,
            NvnShaderStage.StageGeometry => ShaderStage.Geometry,
            NvnShaderStage.StageTessControl => ShaderStage.TessellationControl,
            NvnShaderStage.StageTessEvaluation => ShaderStage.TessellationEvaluation,
            _ => ShaderStage.Compute,
        };

        private static BufferStage BufferStageOf(int nvnStage) => nvnStage switch
        {
            NvnShaderStage.StageVertex => BufferStage.Vertex,
            NvnShaderStage.StageFragment => BufferStage.Fragment,
            NvnShaderStage.StageGeometry => BufferStage.Geometry,
            NvnShaderStage.StageTessControl => BufferStage.TessellationControl,
            NvnShaderStage.StageTessEvaluation => BufferStage.TessellationEvaluation,
            _ => BufferStage.Compute,
        };

        private BufferRange DriverSegment(BufferHandle[] buffers, int stage, ReadOnlySpan<byte> image, bool dirty)
        {
            if (buffers[stage] == BufferHandle.Null)
            {
                buffers[stage] = _renderer.CreateBuffer(image.Length, BufferAccess.Default);
                dirty = true;
            }

            if (dirty)
            {
                _renderer.SetBufferData(buffers[stage], 0, image);
            }

            return new BufferRange(buffers[stage], 0, image.Length);
        }

        private BufferRange C1Buffer(in NvnShaderStage stage)
        {
            if (!_c1Buffers.TryGetValue(stage.Hash, out (BufferHandle Handle, int Size) b))
            {
                int size = Math.Max(16, (stage.C1.Length + 15) & ~15);
                byte[] data = new byte[size];
                stage.C1.CopyTo(data, 0);

                b = (_renderer.CreateBuffer(size, BufferAccess.Default), size);
                _renderer.SetBufferData(b.Handle, 0, data);
                _c1Buffers[stage.Hash] = b;
            }

            return new BufferRange(b.Handle, 0, b.Size);
        }

        // ---------- Shader resources ----------

        /// <summary>
        /// Binds what the program's stages use, or, when nothing they read changed since the last draw,
        /// only synchronizes the bound buffers and textures with guest memory (and picks up host storage
        /// the texture cache replaced), as BufferManager and TextureBindingsManager do.
        /// </summary>
        private void CommitResources(NvnProgram program, bool rebind)
        {
            NvnState s = _state;

            for (int nvn = 0; nvn < NvnState.Stages; nvn++)
            {
                if (program.Info[nvn] == null)
                {
                    continue;
                }

                // The driver segments are the replay's own buffers: new contents, same bindings - uploaded
                // only for stages whose translated code still reads them.
                if (s.C0Dirty[nvn] && program.ReadsC0[nvn] && _c0Buffers[nvn] != BufferHandle.Null)
                {
                    _renderer.SetBufferData(_c0Buffers[nvn], 0, s.C0[nvn]);
                    s.C0Dirty[nvn] = false;
                    _segmentUploads++;
                }

                if (s.C2Dirty[nvn] && program.ReadsC2[nvn] && _c2Buffers[nvn] != BufferHandle.Null)
                {
                    _renderer.SetBufferData(_c2Buffers[nvn], 0, MemoryMarshal.AsBytes(s.C2[nvn].AsSpan()));
                    s.C2Dirty[nvn] = false;
                    _segmentUploads++;
                }
            }

            if (!rebind)
            {
                _syncOnly++;
                BufferCache cache = _physical.BufferCache;

                foreach (MultiRange range in _syncRanges)
                {
                    cache.SynchronizeBufferRange(range);
                }

                for (int i = 0; i < _boundTextures.Count; i++)
                {
                    BoundTexture b = _boundTextures[i];
                    b.Texture.SynchronizeMemory();

                    ITexture host = b.Target == Target.TextureBuffer ? null : b.Texture.GetTargetTexture(b.Target);

                    if (host != b.Host)
                    {
                        b.Host = host;
                        _boundTextures[i] = b;

                        if (b.Image)
                        {
                            SetImage(b.Stage, b.Binding, host);
                        }
                        else
                        {
                            SetTexture(b.Stage, b.Binding, host, b.Sampler);
                        }
                    }
                }

                return;
            }

            _rebinds++;
            _syncRanges.Clear();
            _boundTextures.Clear();

            int uniforms = 0, storages = 0;

            for (int nvn = 0; nvn < NvnState.Stages; nvn++)
            {
                ShaderProgramInfo info = program.Info[nvn];

                if (info == null)
                {
                    continue;
                }

                ShaderStage galStage = GalStage(nvn);
                BufferStage bufferStage = BufferStageOf(nvn);

                long tu = Stopwatch.GetTimestamp();

                for (int ci = 0; ci < info.CBuffers.Count; ci++)
                {
                    BufferDescriptor cb = info.CBuffers[ci];
                    BufferRange range;

                    switch (cb.Slot)
                    {
                        case 0:
                            range = DriverSegment(_c0Buffers, nvn, s.C0[nvn], s.C0Dirty[nvn]);
                            s.C0Dirty[nvn] = false;
                            break;
                        case 1:
                            range = program.Stages[nvn].C1 != null ? C1Buffer(program.Stages[nvn]) : BufferRange.Empty;
                            break;
                        case 2:
                            range = DriverSegment(_c2Buffers, nvn, MemoryMarshal.AsBytes(s.C2[nvn].AsSpan()), s.C2Dirty[nvn]);
                            s.C2Dirty[nvn] = false;
                            break;
                        default:
                            range = BufferRange.Empty;

                            if (cb.Slot - 3 < NvnState.MaxUniformBuffers)
                            {
                                int slot = cb.Slot - 3;
                                range = CachedBuffer(ref _uniformCache[nvn * NvnState.MaxUniformBuffers + slot], s.UniformAddress[nvn, slot], Math.Min(s.UniformSize[nvn, slot], 0x10000), bufferStage, out MultiRange memory);

                                if (range.Handle != BufferHandle.Null)
                                {
                                    _syncRanges.Add(memory);
                                }
                            }

                            break;
                    }

                    ref BufferBinding bound = ref _uniformBindings[cb.Binding & 0xFF];

                    if ((bound.Epoch != _bindEpoch || !SameRange(bound.Range, range)) && uniforms < _uniforms.Length)
                    {
                        bound.Epoch = _bindEpoch;
                        bound.Range = range;
                        _uniforms[uniforms++] = new BufferAssignment(cb.Binding, range);
                    }
                }

                long ts = Stopwatch.GetTimestamp();
                _tUniforms += ts - tu;

                for (int si = 0; si < info.SBuffers.Count; si++)
                {
                    BufferDescriptor sb = info.SBuffers[si];
                    (ulong address, ulong size) = StorageBufferOf(nvn, sb);
                    bool write = (sb.Flags & BufferUsageFlags.Write) != 0;
                    BufferStage stage = bufferStage | (write ? BufferStage.StorageWrite : BufferStage.StorageRead);
                    BufferRange range = GetBuffer(address, size, stage, write, out MultiRange memory, storage: true);

                    if (range.Handle != BufferHandle.Null)
                    {
                        _syncRanges.Add(memory);
                    }

                    ref BufferBinding bound = ref _storageBindings[sb.Binding & 0xFF];

                    if ((bound.Epoch != _bindEpoch || !SameRange(bound.Range, range)) && storages < _storages.Length)
                    {
                        bound.Epoch = _bindEpoch;
                        bound.Range = range;
                        _storages[storages++] = new BufferAssignment(sb.Binding, range);
                    }
                }

                long tt = Stopwatch.GetTimestamp();
                _tStorages += tt - ts;

                for (int ti = 0; ti < info.Textures.Count; ti++)
                {
                    BindTexture(nvn, galStage, info.Textures[ti], false);
                }

                for (int ii = 0; ii < info.Images.Count; ii++)
                {
                    BindTexture(nvn, galStage, info.Images[ii], true);
                }

                _tTextures += Stopwatch.GetTimestamp() - tt;
            }

            if (uniforms != 0)
            {
                Pipeline.SetUniformBuffers(_uniforms.AsSpan(0, uniforms));
                _uniformSets++;
            }

            if (storages != 0)
            {
                Pipeline.SetStorageBuffers(_storages.AsSpan(0, storages));
            }
        }

        /// <summary>
        /// A storage buffer the translator found behind an address the shader loads: from the driver's
        /// table in c0 (NVN storage buffer bindings, or compute uniform buffers), or from one of the
        /// game's own uniform buffers.
        /// </summary>
        private (ulong Address, ulong Size) StorageBufferOf(int nvn, in BufferDescriptor sb)
        {
            NvnState s = _state;
            int offset = sb.SbCbOffset * 4;

            if (sb.SbCbSlot == 0)
            {
                ReadOnlySpan<byte> c0 = s.C0[nvn];

                if (offset + 12 > c0.Length)
                {
                    return (0, 0);
                }

                ulong address = BitConverter.ToUInt64(c0[offset..]);
                ulong size = BitConverter.ToUInt32(c0[(offset + 8)..]);

                return (address, size);
            }

            if (sb.SbCbSlot >= 3 && sb.SbCbSlot - 3 < NvnState.MaxUniformBuffers)
            {
                ulong ubo = s.UniformAddress[nvn, sb.SbCbSlot - 3];

                if (ubo == 0)
                {
                    return (0, 0);
                }

                ulong address = _physical.Read<ulong>(ubo + (ulong)offset);

                return (address, address != 0 ? _gmm.GetMappedSize(address, Constants.MaxUnknownStorageSize) : 0);
            }

            return (0, 0);
        }

        private static Target TargetOf(SamplerType type)
        {
            bool array = (type & SamplerType.Array) != 0;
            bool ms = (type & SamplerType.Multisample) != 0;

            return (type & SamplerType.Mask) switch
            {
                SamplerType.Texture1D => array ? Target.Texture1DArray : Target.Texture1D,
                SamplerType.TextureBuffer => Target.TextureBuffer,
                SamplerType.Texture3D => Target.Texture3D,
                SamplerType.TextureCube => array ? Target.CubemapArray : Target.Cubemap,
                _ => ms ? (array ? Target.Texture2DMultisampleArray : Target.Texture2DMultisample) : (array ? Target.Texture2DArray : Target.Texture2D),
            };
        }

        private void BindTexture(int nvn, ShaderStage galStage, in Ryujinx.Graphics.Shader.TextureDescriptor td, bool image)
        {
            if (td.ArrayLength > 1)
            {
                Report("texture arrays are not replayed yet");
                return;
            }

            int packed = _xlate.ReadPackedId(nvn, td.HandleIndex, td.CbufSlot);
            TextureFormat imageFormat = image ? td.Format : 0;
            Target target = TargetOf(td.Type);
            ITexture host = null;
            Image.Texture texture;
            ISampler sampler = null;

            NvnState s = _state;
            ref TextureCacheEntry c = ref (image ? _imageCache : _textureCache)[nvn * 256 + (td.Binding & 0xFF)];

            if (c.Epoch == _bindEpoch && c.Generation == _poolGeneration && c.Packed == packed && c.ImageFormat == imageFormat &&
                c.TexturePool == s.TexturePool && c.SamplerPool == s.SamplerPool && c.Texture?.HostTexture != null && PoolGenerationAddress != 0)
            {
                texture = c.Texture;
                sampler = c.Sampler;
                texture.SynchronizeMemory();
                _textureCacheHits++;
            }
            else
            {
                texture = PoolTextureOf(TextureHandle.UnpackTextureId(packed), imageFormat);

                if (!image && !td.Separate)
                {
                    if (_xlate.TryGetSampler(TextureHandle.UnpackSamplerId(packed), out PvSamp samp))
                    {
                        sampler = _samplers.Get(samp);
                    }
                }

                c = new TextureCacheEntry
                {
                    Epoch = _bindEpoch,
                    Generation = _poolGeneration,
                    TexturePool = s.TexturePool,
                    SamplerPool = s.SamplerPool,
                    Packed = packed,
                    ImageFormat = imageFormat,
                    Texture = texture,
                    Sampler = sampler,
                };
            }

            if (texture != null)
            {
                if (target == Target.TextureBuffer)
                {
                    Report("buffer textures are not replayed yet");
                }
                else
                {
                    host = texture.GetTargetTexture(target);
                }
            }

            if (image)
            {
                if (texture != null && (td.Flags & TextureUsageFlags.ImageStore) != 0)
                {
                    texture.SignalModified();
                }

                SetImage(galStage, td.Binding, host);
            }
            else
            {
                SetTexture(galStage, td.Binding, host, sampler);
            }

            if (texture != null)
            {
                _boundTextures.Add(new BoundTexture { Stage = galStage, Binding = td.Binding, Image = image, Target = target, Texture = texture, Host = host, Sampler = sampler });
            }
        }

        private Image.Texture PoolTextureOf(int id, TextureFormat imageFormat)
        {
            if (!_xlate.TryGetTextureEntry(id, out PvTexEntry entry))
            {
                return null;
            }

            var key = (_state.TexturePool, id, (int)imageFormat);

            if (_poolTextures.TryGetValue(key, out PoolTexture cached) &&
                cached.Texture != null &&
                cached.Texture.HostTexture != null &&
                SameEntry(cached.Entry, entry))
            {
                cached.Texture.SynchronizeMemory();

                return cached.Texture;
            }

            PvView view = entry.View;
            bool hasView = entry.HasView != 0;

            if (imageFormat != 0)
            {
                // Images are bound with the shader's format (a view where it differs).
                int nvnFormat = NvnFormatOfImage(imageFormat);

                if (nvnFormat != 0)
                {
                    view.Set |= 4;
                    view.Format = (ushort)nvnFormat;
                    hasView = true;
                }
            }

            Image.Texture texture = FindTexture(entry.Tex, view, hasView, TextureSearchFlags.ForSampler);

            // The new reference first: the entry may resolve to the texture it already held, whose last
            // reference this could otherwise be.
            texture?.IncrementReferenceCount();

            if (cached != null)
            {
                cached.Texture?.DecrementReferenceCount();
            }
            else
            {
                cached = new PoolTexture();
                _poolTextures[key] = cached;
            }

            texture?.SynchronizeMemory();

            cached.Entry = entry;
            cached.Texture = texture;

            return texture;
        }

        /// <summary>
        /// The game finalized a texture: pool references to its storage are dropped, in submission
        /// order (the game finalizes only what completed work used). Without this, ids the game never
        /// registers again would keep finalized textures alive.
        /// </summary>
        public void TextureFinalized(ulong address)
        {
            _finalized.Enqueue(address);
        }

        private readonly System.Collections.Concurrent.ConcurrentQueue<ulong> _finalized = new();
        private readonly HashSet<ulong> _finalizedSet = new();
        private readonly List<(ulong, int, int)> _dead = new();

        /// <summary>
        /// Drops pool references to textures the game finalized since the last submission (one scan of
        /// the pool references however many finalizations - TOTK finalizes thousands a second).
        /// </summary>
        private void ReleaseFinalized()
        {
            if (_finalized.IsEmpty)
            {
                return;
            }

            while (_finalized.TryDequeue(out ulong address))
            {
                _finalizedSet.Add(address);
            }

            foreach (KeyValuePair<(ulong Table, int Id, int Format), PoolTexture> kv in _poolTextures)
            {
                if (_finalizedSet.Contains(kv.Value.Entry.Tex.Addr))
                {
                    _dead.Add(kv.Key);
                }
            }

            foreach ((ulong, int, int) key in _dead)
            {
                _poolTextures[key].Texture?.DecrementReferenceCount();
                _poolTextures.Remove(key);
            }

            if (_dead.Count != 0)
            {
                // Binding caches may name the released textures.
                InvalidateBindings();
                _finalizedReleases += _dead.Count;
            }

            _dead.Clear();
            _finalizedSet.Clear();
        }

        private long _finalizedReleases;

        private static bool SameEntry(in PvTexEntry a, in PvTexEntry b)
        {
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in a, 1)).SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in b, 1)));
        }

        private static int NvnFormatOfImage(TextureFormat format) => format switch
        {
            TextureFormat.R8Unorm => 0x01,
            TextureFormat.R8Snorm => 0x02,
            TextureFormat.R8Uint => 0x03,
            TextureFormat.R8Sint => 0x04,
            TextureFormat.R16Float => 0x05,
            TextureFormat.R16Unorm => 0x06,
            TextureFormat.R16Snorm => 0x07,
            TextureFormat.R16Uint => 0x08,
            TextureFormat.R16Sint => 0x09,
            TextureFormat.R32Float => 0x0A,
            TextureFormat.R32Uint => 0x0B,
            TextureFormat.R32Sint => 0x0C,
            TextureFormat.R8G8Unorm => 0x0D,
            TextureFormat.R8G8Snorm => 0x0E,
            TextureFormat.R8G8Uint => 0x0F,
            TextureFormat.R8G8Sint => 0x10,
            TextureFormat.R16G16Float => 0x11,
            TextureFormat.R16G16Unorm => 0x12,
            TextureFormat.R16G16Snorm => 0x13,
            TextureFormat.R16G16Uint => 0x14,
            TextureFormat.R16G16Sint => 0x15,
            TextureFormat.R32G32Float => 0x16,
            TextureFormat.R32G32Uint => 0x17,
            TextureFormat.R32G32Sint => 0x18,
            TextureFormat.R8G8B8A8Unorm => 0x25,
            TextureFormat.R8G8B8A8Snorm => 0x26,
            TextureFormat.R8G8B8A8Uint => 0x27,
            TextureFormat.R8G8B8A8Sint => 0x28,
            TextureFormat.R16G16B16A16Float => 0x29,
            TextureFormat.R16G16B16A16Unorm => 0x2A,
            TextureFormat.R16G16B16A16Snorm => 0x2B,
            TextureFormat.R16G16B16A16Uint => 0x2C,
            TextureFormat.R16G16B16A16Sint => 0x2D,
            TextureFormat.R32G32B32A32Float => 0x2E,
            TextureFormat.R32G32B32A32Uint => 0x2F,
            TextureFormat.R32G32B32A32Sint => 0x30,
            TextureFormat.R10G10B10A2Unorm => 0x3D,
            TextureFormat.R10G10B10A2Uint => 0x3E,
            TextureFormat.R11G11B10Float => 0x3F,
            _ => 0,
        };

        // ---------- Clears ----------

        /// <summary>Clears are scissored (the Metal backend's draw-based clears keep the current scissor).</summary>
        private void CommitScissors()
        {
            NvnState s = _state;

            if ((s.Changed & NvnState.Dirty.Scissors) == 0)
            {
                return;
            }

            int n = Math.Max(1, s.ScissorCount);

            for (int i = 0; i < n; i++)
            {
                _scissors[i] = new Rectangle<int>(s.Scissors[i * 4], s.Scissors[i * 4 + 1], s.Scissors[i * 4 + 2], s.Scissors[i * 4 + 3]);
            }

            Pipeline.SetScissors(_scissors.AsSpan(0, n));
            s.Changed &= ~NvnState.Dirty.Scissors;
        }

        private void ExecuteClearColor(int index, int mask, float* rgba)
        {
            BeginSubmission();

            if (!RenderEnabled() || index < 0 || index >= NvnState.MaxColorTargets)
            {
                return;
            }

            if (_rtDirty)
            {
                CommitRenderTargets();
            }

            CommitScissors();

            if (_rtColors[index] == null)
            {
                return;
            }

            Pipeline.ClearRenderTargetColor(index, 0, Math.Max(1, _rtColors[index].Info.GetLayers()), (uint)mask & 0xF, new ColorF(rgba[0], rgba[1], rgba[2], rgba[3]));
            _clears++;
        }

        private void ExecuteClearDepthStencil(float depth, bool depthMask, int stencil, int stencilMask)
        {
            BeginSubmission();

            if (!RenderEnabled())
            {
                return;
            }

            if (_rtDirty || RenderTargetHostChanged())
            {
                CommitRenderTargets();
            }

            if (_rtDepth == null)
            {
                return;
            }

            CommitScissors();
            Pipeline.ClearRenderTargetDepthStencil(0, Math.Max(1, _rtDepth.Info.GetLayers()), depth, depthMask, stencil, stencilMask);
            _clears++;
        }

        // ---------- Barriers ----------

        private void ExecuteBarrier(int bits)
        {
            BeginSubmission();

            // NVNbarrierBits: ORDER_PRIMITIVES 1, ORDER_FRAGMENTS 2, ORDER_FRAGMENTS_TILED 4, ORDER_INDIRECT_DATA 8,
            // INVALIDATE_TEXTURE 0x10, INVALIDATE_SHADER 0x20, INVALIDATE_TEXTURE_DESCRIPTOR 0x40, INVALIDATE_ZCULL 0x80.
            if ((bits & 4) != 0)
            {
                Pipeline.TextureBarrierTiled();
            }
            else if ((bits & (2 | 0x10)) != 0)
            {
                Pipeline.TextureBarrier();
            }

            if ((bits & (1 | 8 | 0x20)) != 0)
            {
                Pipeline.Barrier();
            }
        }

        // ---------- Buffer writes and copies ----------

        private void ExecuteUpdateBuffer(ulong address, ReadOnlySpan<byte> data)
        {
            BeginSubmission();

            if (data.IsEmpty)
            {
                return;
            }

            // As the Maxwell path's inline constant buffer updates: guest memory takes the data and the
            // range is forced dirty, so later draws upload it while earlier ones keep the old contents.
            ulong cpu = _gmm.Translate(address);

            if (cpu == MemoryManager.PteUnmapped)
            {
                Report("uniform update to unmapped memory");
                return;
            }

            if (_physical.WriteWithRedundancyCheck(cpu, data))
            {
                _physical.BufferCache.ForceDirty(_gmm, address, (ulong)data.Length);
            }
        }

        private void ExecuteCopyBuffer(ulong src, ulong dst, ulong size)
        {
            BeginSubmission();

            if (size == 0)
            {
                return;
            }

            // Through the buffer cache, as the DMA engine copies: guest memory gets the bytes too when the
            // source is not GPU-modified (shader code is uploaded this way, and translation reads it there),
            // otherwise the destination is marked GPU-modified.
            _physical.BufferCache.CopyBuffer(_gmm, src, dst, size);
        }

        private void ExecuteClearBuffer(ulong address, ulong size, uint value)
        {
            BeginSubmission();

            if (size != 0)
            {
                _physical.BufferCache.ClearBuffer(_gmm, address, size, value);
            }
        }

        private void ExecuteCopyBufferTexture(bool toTexture, in PvCopyBt copy)
        {
            BeginSubmission();

            // Through guest memory, as the DMA path does for copies it cannot do on the host: reads flush
            // what the GPU wrote, writes invalidate the cached texture or buffer.
            ulong linear = _gmm.Translate(copy.Buffer);

            if (linear == MemoryManager.PteUnmapped)
            {
                Report("buffer/texture copy: buffer not mapped");
                return;
            }

            if (CopyLinear(copy.Tex, copy.View, copy.HasView != 0, copy.Region, linear, _state.CopyRowStride, 0, toTexture))
            {
                if (toTexture)
                {
                    _copiesToTexture++;
                }
                else
                {
                    _copiesToBuffer++;
                }
            }
        }

        private void ExecuteCopyTexture(in PvCopyT2T copy)
        {
            BeginSubmission();

            Image.Texture src = FindTexture(copy.Src, copy.SrcView, copy.HasSrcView != 0, TextureSearchFlags.ForCopy);
            Image.Texture dst = FindTexture(copy.Dst, copy.DstView, copy.HasDstView != 0, TextureSearchFlags.ForCopy);

            if (src == null || dst == null)
            {
                return;
            }

            src.SynchronizeMemory();
            dst.SynchronizeMemory();

            Extents2D srcRegion = new(copy.SrcRegion.X, copy.SrcRegion.Y, copy.SrcRegion.X + copy.SrcRegion.Width, copy.SrcRegion.Y + copy.SrcRegion.Height);
            Extents2D dstRegion = new(copy.DstRegion.X, copy.DstRegion.Y, copy.DstRegion.X + copy.DstRegion.Width, copy.DstRegion.Y + copy.DstRegion.Height);

            src.HostTexture.CopyTo(dst.HostTexture, srcRegion, dstRegion, (copy.Flags & 1) != 0);
            dst.SignalModified();
        }

        private void ExecuteClearTexture(in PvClearTex clear)
        {
            BeginSubmission();
            Report("texture clears are not replayed yet");
        }

        // ---------- Counters and events ----------

        // CPU addresses of SAMPLES_PASSED reports whose result the GPU has not given yet.
        private readonly HashSet<ulong> _pendingSamples = new();
        private static readonly Stopwatch _clock = Stopwatch.StartNew();

        // Event signals of the submission being replayed, written when its fence completes.
        private List<(ulong Address, int Mode, uint Value)> _signals = new();

        // Tegra GPU timestamps tick at 19.2 MHz.
        private static ulong GpuTicks() => (ulong)(_clock.Elapsed.TotalSeconds * 19_200_000.0);

        private bool CounterPending(ulong gpuVa)
        {
            ulong cpu = _gmm.Translate(gpuVa);

            lock (_pendingSamples)
            {
                return _pendingSamples.Contains(cpu);
            }
        }

        private void ExecuteReportCounter(int type, ulong address)
        {
            BeginSubmission();

            ulong cpu = _gmm.Translate(address);

            if (cpu == MemoryManager.PteUnmapped)
            {
                return;
            }

            if (type == 1)   // SAMPLES_PASSED: the GPU's count, written when it is known
            {
                lock (_pendingSamples)
                {
                    _pendingSamples.Add(cpu);
                }

                _renderer.ReportCounter(CounterType.SamplesPassed, (sender, result) =>
                {
                    Span<ulong> record = stackalloc ulong[2];
                    record[0] = result;
                    record[1] = GpuTicks();
                    _physical.Write(cpu, MemoryMarshal.AsBytes(record));

                    lock (_pendingSamples)
                    {
                        _pendingSamples.Remove(cpu);
                    }
                }, 1f, false);

                return;
            }

            // Timestamps (and counters not emulated) report counter 0 with the time: TOTK's conditional
            // clears rely on the 0. Written in replay order, which is what conditionals read.
            Span<ulong> now = stackalloc ulong[2];
            now[0] = 0;
            now[1] = GpuTicks();
            _physical.Write(cpu, MemoryMarshal.AsBytes(now));
        }

        private void ExecuteResetCounter(int type)
        {
            BeginSubmission();

            if (type == 1)
            {
                _renderer.ResetCounter(CounterType.SamplesPassed);
            }
        }

        private void FlushCounters()
        {
        }

        private void ExecuteSignal(ulong address, int mode, uint value)
        {
            BeginSubmission();

            // The game learns from an event that the GPU got past it: written once the submission's
            // fence completed, never early.
            _signals.Add((address, mode, value));
        }

        private List<(ulong Address, int Mode, uint Value)> TakeSignals()
        {
            if (_signals.Count == 0)
            {
                return null;
            }

            List<(ulong Address, int Mode, uint Value)> taken = _signals;
            _signals = new();

            return taken;
        }

        private void ApplySignals(List<(ulong Address, int Mode, uint Value)> signals)
        {
            foreach ((ulong address, int mode, uint value) in signals)
            {
                ulong cpu = _gmm.Translate(address);

                if (cpu == MemoryManager.PteUnmapped)
                {
                    cpu = address;   // events live in CPU-visible pools; the address is the guest's
                }

                uint result = mode == 1 ? _physical.Read<uint>(cpu) + value : value;   // NVN_EVENT_SIGNAL_MODE_ADD = 1
                _physical.Write(cpu, BitConverter.GetBytes(result));
            }
        }

        private long _draws, _dispatches, _clears, _copiesToTexture, _copiesToBuffer;

        // ---------- Frame trace (RYUJINX_NVN_PV_TRACE=1): menus and loading screens ----------

        private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("RYUJINX_NVN_PV_TRACE") == "1";
        private bool _tracing;
        private long _frame, _frameDraws, _traces;

        /// <summary>Called at each present (GPU thread): traces one light frame now and then.</summary>
        private void FrameBoundary()
        {
            if (_tracing)
            {
                _tracing = false;
                _traceDraw = 0;
                Logger.Info?.Print(LogClass.Gpu, $"nvnpv trace: end of frame {_frame}");
            }

            _frame++;

            // Menus and loading screens (save select draws about 134 a frame, the loading map 64, the title
            // 183): at most two traces per draw count, so each screen gets its own.
            if (TraceEnabled && _frame % 60 == 0 && _frameDraws >= 40 && _frameDraws <= 200 && _traces < 10)
            {
                _traceCounts.TryGetValue(_frameDraws, out int seen);

                if (seen < 2)
                {
                    _traceCounts[_frameDraws] = seen + 1;
                    _tracing = true;
                    _traces++;
                    _dumps = 0;
                    _dumpedTextures.Clear();
                    Logger.Info?.Print(LogClass.Gpu, $"nvnpv trace: frame {_frame} (previous frame had {_frameDraws} draws)");
                }
            }

            _frameDraws = 0;
        }

        private readonly Dictionary<long, int> _traceCounts = new();

        // RYUJINX_NVN_PV_TRACE_RT=<hex GPU address>: trace every draw into that colour target, with dumps.
        private static readonly ulong TraceRtAddress = ulong.TryParse(Environment.GetEnvironmentVariable("RYUJINX_NVN_PV_TRACE_RT"), System.Globalization.NumberStyles.HexNumber, null, out ulong rt) ? rt : 0;
        private int _rtTraced, _rt3DTraced;
        private bool _tracingRt;
        private static readonly string DumpDirectory = Environment.GetEnvironmentVariable("RYUJINX_NVN_PV_DUMP");
        private int _dumps, _traceDraw;

        /// <summary>
        /// After a traced draw: the colour target's contents, read back from the GPU (synchronously - a
        /// debugging stall), as raw RGBA8 named frame_draw_WxH.rgba. Shows which draw of a chain first
        /// produced a wrong colour.
        /// </summary>
        private void DumpAfterDraw()
        {
            _traceDraw++;

            if (DumpDirectory == null || _dumps >= 40 || _rtColors[0] == null)
            {
                return;
            }

            Image.Texture rt = _rtColors[0];
            Format format = rt.Info.FormatInfo.Format;

            if (format != Format.R8G8B8A8Unorm && format != Format.R8G8B8A8Srgb)
            {
                return;
            }

            try
            {
                using var data = rt.HostTexture.GetData(0, 0);
                System.IO.Directory.CreateDirectory(DumpDirectory);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(DumpDirectory, $"f{_frame}_d{_traceDraw:D3}_{rt.Info.Width}x{rt.Info.Height}_{rt.Info.GpuAddress:X}.rgba"), data.Get().ToArray());
                _dumps++;
            }
            catch (Exception e)
            {
                Report($"dump failed: {e.Message}");
            }
        }

        private readonly HashSet<ulong> _dumpedTextures = new();

        /// <summary>The colour textures a traced draw samples, read back once per traced frame.</summary>
        private void DumpBoundTextures()
        {
            if (DumpDirectory == null)
            {
                return;
            }

            foreach (BoundTexture b in _boundTextures)
            {
                TextureInfo ti = b.Texture.Info;
                Format f = ti.FormatInfo.Format;

                if ((f != Format.R8G8B8A8Unorm && f != Format.R8G8B8A8Srgb && f != Format.R8Unorm && f != Format.Bc4Unorm) ||
                    ti.Target != Target.Texture2D || !_dumpedTextures.Add(ti.GpuAddress) || _dumpedTextures.Count > 24)
                {
                    continue;
                }

                try
                {
                    using var data = b.Texture.HostTexture.GetData(0, 0);
                    System.IO.Directory.CreateDirectory(DumpDirectory);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(DumpDirectory, $"f{_frame}_d{_traceDraw + 1:D3}_tex{b.Binding}_{ti.Width}x{ti.Height}_{ti.GpuAddress:X}.rgba"), data.Get().ToArray());
                }
                catch (Exception e)
                {
                    Report($"texture dump failed: {e.Message}");
                }
            }
        }

        private void TraceDraw(int mode, NvnProgram program)
        {
            DumpBoundTextures();

            NvnState s = _state;
            System.Text.StringBuilder sb = new();
            sb.Append($"nvnpv trace draw {_traceDraw + 1}: mode {mode} vs {program.Stages[0].Hash.Low:X16} fs {program.Stages[1].Hash.Low:X16}");

            for (int i = 0; i < s.ColorCount && i < NvnState.MaxColorTargets; i++)
            {
                ref PvRtTarget t = ref s.Colors[i];

                if (t.Present != 0)
                {
                    int fmt = t.HasView != 0 && t.View.SetFormat ? t.View.Format : t.Tex.D.Format;
                    sb.Append($" | rt{i} 0x{t.Tex.Addr:X} {t.Tex.D.Width}x{t.Tex.D.Height}x{t.Tex.D.Depth} target {t.Tex.D.Target} tile {t.Tex.L.TileH}/{t.Tex.L.TileD} nvn 0x{fmt:X} {NvnGal.Format(fmt).Format}");

                    if (t.HasView != 0)
                    {
                        sb.Append($" view set {t.View.Set:X} levels {t.View.BaseLevel}+{t.View.NumLevels} layers {t.View.MinLayer}+{t.View.NumLayers} target {t.View.Target}");
                    }

                    if (_rtColors[i] != null)
                    {
                        TextureInfo ri = _rtColors[i].Info;
                        sb.Append($" -> cache {ri.Target} {ri.Width}x{ri.Height}x{ri.DepthOrLayers} at 0x{ri.GpuAddress:X}");
                    }
                }
            }

            ulong b0 = s.Blend[0];
            sb.Append($" | blend {(s.ColorState & 1)} rgb {b0 >> 3 & 0x7F:X}/{b0 >> 10 & 0x7F:X} op {b0 >> 31 & 7} a {b0 >> 17 & 0x7F:X}/{b0 >> 24 & 0x7F:X} mask {s.ChannelMask & 0xF:X}");

            foreach (BoundTexture b in _boundTextures)
            {
                TextureInfo ti = b.Texture.Info;
                sb.Append($" | {(b.Image ? "img" : "tex")}{b.Binding} 0x{ti.GpuAddress:X} {ti.Width}x{ti.Height}x{ti.DepthOrLayers} {ti.FormatInfo.Format} {ti.Target} sw {ti.SwizzleR}{ti.SwizzleG}{ti.SwizzleB}{ti.SwizzleA}");
            }

            Logger.Info?.Print(LogClass.Gpu, sb.ToString());
        }

        /// <summary>Counts since the last call, for the periodic log line.</summary>
        public long Translations => _programs.Count;

        public long TakeSubmittedBytes() => Interlocked.Exchange(ref _submittedBytes, 0);

        public string TakeStats(int frames)
        {
            frames = Math.Max(frames, 1);
            double ms = 1000.0 / Stopwatch.Frequency / frames;
            string line = $"replay per frame: draws {_draws / (double)frames:F0}, dispatches {_dispatches / (double)frames:F0}, clears {_clears / (double)frames:F0}, " +
                          $"buffer->texture {_copiesToTexture}, texture->buffer {_copiesToBuffer}, texels written {_texelWrites} read {_texelReads}, " +
                          $"programs {_programs.Count}, pool textures {_poolTextures.Count} (released {_finalizedReleases}), slice views {_sliceViews.Count}, c1 buffers {_c1Buffers.Count}, " +
                          $"errors {Interlocked.Read(ref _errors)}; ms per frame: submit {_tSubmit * ms:F2} " +
                          $"(program {_tProgram * ms:F2}, fixed {_tFixed * ms:F2}, vertex {_tVertex * ms:F2}, resources {_tResources * ms:F2}: " +
                          $"uniforms {_tUniforms * ms:F2}, storage {_tStorages * ms:F2}, textures {_tTextures * ms:F2}; rebinds {_rebinds / (double)frames:F0}, sync-only {_syncOnly / (double)frames:F0}; " +
                          $"sent: texture sets {_textureSets / (double)frames:F0}, uniform sets {_uniformSets / (double)frames:F0}, segment uploads {_segmentUploads / (double)frames:F0}; " +
                          $"cache hits: textures {_textureCacheHits / (double)frames:F0}, buffers {_uniformCacheHits / (double)frames:F0})";

            _draws = _dispatches = _clears = _copiesToTexture = _copiesToBuffer = 0;
            _tSubmit = _tProgram = _tFixed = _tVertex = _tResources = _tUniforms = _tStorages = _tTextures = _rebinds = _syncOnly = 0;
            _textureSets = _uniformSets = _segmentUploads = _textureCacheHits = _uniformCacheHits = 0;

            return line;
        }
    }
}
