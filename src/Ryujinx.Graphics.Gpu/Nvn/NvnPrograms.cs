using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Nvn
{
    /// <summary>The draw state a translation may depend on, and the lookups it needs.</summary>
    sealed class NvnTranslationState
    {
        private readonly PhysicalMemory _physical;
        private readonly NvnState _state;

        public InputTopology Topology = InputTopology.Triangles;
        public bool AlphaToCoverage;
        public bool DepthMinusOneToOne;
        public Array32<AttributeType> AttributeTypes;
        public Array8<AttributeType> FragmentOutputTypes;
        public int ComputeLocalMemorySize;   // control section field not located yet: the translator's default applies

        public NvnTranslationState(PhysicalMemory physical, NvnState state)
        {
            _physical = physical;
            _state = state;
        }

        /// <summary>
        /// The texture pool entry a shader handle names for a stage: the handle's word in the driver's
        /// c2 image (or in a bound uniform buffer, for handles the shader loads from one) packs the
        /// texture id (low 20 bits) and the sampler id (high 12).
        /// </summary>
        public PvTex? ResolveTexture(int nvnStage, int handle, int cbufSlot, out PvView view, out bool hasView)
        {
            view = default;
            hasView = false;

            int packed = ReadPackedId(nvnStage, handle, cbufSlot);
            int id = TextureHandle.UnpackTextureId(packed);

            if (!TryGetTextureEntry(id, out PvTexEntry entry))
            {
                return null;
            }

            view = entry.View;
            hasView = entry.HasView != 0;

            return entry.Tex;
        }

        public int ReadPackedId(int nvnStage, int handle, int cbufSlot)
        {
            int stage = NvnState.StageIndex(nvnStage);
            (int textureSlot, int samplerSlot) = TextureHandle.UnpackSlots(cbufSlot, 2);

            if (textureSlot == 2 && samplerSlot == 2)
            {
                return TextureHandle.ReadPackedId(handle, _state.C2[stage], _state.C2[stage]);
            }

            // Handles in other constant buffers: the game's own uniform buffers (Maxwell c3 = NVN slot 0).
            ReadOnlySpan<int> textureBuffer = ReadHandleBuffer(stage, textureSlot);
            ReadOnlySpan<int> samplerBuffer = samplerSlot == textureSlot ? textureBuffer : ReadHandleBuffer(stage, samplerSlot);

            return TextureHandle.ReadPackedId(handle, textureBuffer, samplerBuffer);
        }

        private ReadOnlySpan<int> ReadHandleBuffer(int stage, int slot)
        {
            if (slot == 2)
            {
                return _state.C2[stage];
            }

            if (slot >= 3 && slot - 3 < NvnState.MaxUniformBuffers)
            {
                ulong address = _state.UniformAddress[stage, slot - 3];
                ulong size = Math.Min(_state.UniformSize[stage, slot - 3], 0x10000);

                if (address != 0 && size != 0)
                {
                    return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(_physical.GetSpan(address, (int)size));
                }
            }

            return ReadOnlySpan<int>.Empty;
        }

        public bool TryGetTextureEntry(int id, out PvTexEntry entry)
        {
            entry = default;

            if (_state.TexturePool == 0 || id < 0 || id >= _state.TexturePoolCount)
            {
                return false;
            }

            entry = _physical.Read<PvTexEntry>(_state.TexturePool + (ulong)id * 120);

            return entry.Valid != 0;
        }

        public bool TryGetSampler(int id, out PvSamp sampler)
        {
            sampler = default;

            if (_state.SamplerPool == 0 || id < 0 || id >= _state.SamplerPoolCount)
            {
                return false;
            }

            PvSampEntry entry = _physical.Read<PvSampEntry>(_state.SamplerPool + (ulong)id * 56);
            sampler = entry.Samp;

            return entry.Valid != 0;
        }

        public static SamplerType SamplerTypeOf(in PvTex t, in PvView v, bool hasView)
        {
            int target = hasView && v.SetTarget ? v.Target : t.D.Target;

            return target switch
            {
                0 => SamplerType.Texture1D,
                1 => SamplerType.Texture2D,
                2 => SamplerType.Texture3D,
                3 => SamplerType.Texture1D | SamplerType.Array,
                4 => SamplerType.Texture2D | SamplerType.Array,
                5 => SamplerType.Texture2D | SamplerType.Multisample,
                6 => SamplerType.Texture2D | SamplerType.Multisample | SamplerType.Array,
                7 => SamplerType.Texture2D,
                8 => SamplerType.TextureCube,
                9 => SamplerType.TextureCube | SamplerType.Array,
                10 => SamplerType.TextureBuffer,
                _ => SamplerType.Texture2D,
            };
        }

        public static TextureFormat ImageFormatOf(in PvTex t, in PvView v, bool hasView)
        {
            int nvnFormat = hasView && v.SetFormat ? v.Format : t.D.Format;
            (Format format, _) = NvnGal.Format(nvnFormat);

            return format != NvnGal.Unknown && Enum.TryParse(format.ToString(), out TextureFormat tf) ? tf : TextureFormat.R8G8B8A8Unorm;
        }

        /// <summary>
        /// A vertex attribute's type for the translator, as the Maxwell path's specialization state gives
        /// it: scaled formats stay scaled when the host lacks them (the shader converts), and with VTG
        /// stages possibly run as compute, 10:10:10:2 and 11:11:10 inputs are marked packed.
        /// </summary>
        public static AttributeType VertexAttributeTypeOf(Format format, bool supportsScaled, bool mayConvertVtgToCompute)
        {
            if (format == NvnGal.Unknown)
            {
                return AttributeType.Float;
            }

            AttributeType type = AttributeTypeOf(format);

            if (supportsScaled && type is AttributeType.Sscaled or AttributeType.Uscaled)
            {
                type = AttributeType.Float;
            }

            if (mayConvertVtgToCompute)
            {
                switch (format)
                {
                    case Format.R10G10B10A2Unorm:
                    case Format.R10G10B10A2Uint:
                    case Format.R10G10B10A2Uscaled:
                    case Format.R11G11B10Float:
                        type |= AttributeType.Packed;
                        break;
                    case Format.R10G10B10A2Snorm:
                    case Format.R10G10B10A2Sint:
                    case Format.R10G10B10A2Sscaled:
                        type |= AttributeType.Packed | AttributeType.PackedRgb10A2Signed;
                        break;
                }
            }

            return type;
        }

        private static readonly AttributeType[] _attributeTypes = BuildAttributeTypes();

        private static AttributeType[] BuildAttributeTypes()
        {
            Format[] formats = Enum.GetValues<Format>();
            int max = 0;

            foreach (Format f in formats)
            {
                max = Math.Max(max, (int)f);
            }

            AttributeType[] table = new AttributeType[max + 1];

            foreach (Format f in formats)
            {
                string name = f.ToString();

                table[(int)f] = name.EndsWith("Sint", StringComparison.Ordinal) ? AttributeType.Sint
                    : name.EndsWith("Uint", StringComparison.Ordinal) ? AttributeType.Uint
                    : name.EndsWith("Sscaled", StringComparison.Ordinal) ? AttributeType.Sscaled
                    : name.EndsWith("Uscaled", StringComparison.Ordinal) ? AttributeType.Uscaled
                    : AttributeType.Float;
            }

            return table;
        }

        public static AttributeType AttributeTypeOf(Format format)
        {
            int i = (int)format;

            return i >= 0 && i < _attributeTypes.Length ? _attributeTypes[i] : AttributeType.Float;
        }
    }

    /// <summary>A translated NVN program and what its translation depended on.</summary>
    sealed class NvnProgram
    {
        public IProgram Host;
        public ShaderProgramInfo[] Info = new ShaderProgramInfo[6];   // by NVN stage
        public NvnAnswers[] Answers = new NvnAnswers[6];
        public NvnShaderStage[] Stages = new NvnShaderStage[6];        // by NVN stage; C1 == null where absent
        public bool[] ReadsC0 = new bool[6];                           // the translated stage still reads driver c0 / c2
        public bool[] ReadsC2 = new bool[6];

        public void NoteSegments(int nvn, ShaderProgramInfo info)
        {
            foreach (BufferDescriptor cb in info.CBuffers)
            {
                ReadsC0[nvn] |= cb.Slot == 0;
                ReadsC2[nvn] |= cb.Slot == 2;
            }
        }
        public int[] LocalSize;
        public bool Compute;
        public bool Failed;
        public string Error;
        public Hash128 GraphicsKey;  // the draw state a graphics translation depended on (default for compute)
        public bool FromCache;       // loaded from the program cache: Stages are filled in by the first match
    }

    /// <summary>
    /// Translates NVN programs with the Ryujinx shader translator and keeps them by the hashes of
    /// their stages. A program is reused for a draw only when every draw-dependent answer its
    /// translation recorded is the same for the draw's state.
    /// </summary>
    sealed class NvnPrograms : IDisposable
    {
        private readonly GpuContext _context;
        private readonly MemoryManager _memory;
        private readonly Dictionary<(Hash128, Hash128, Hash128), List<NvnProgram>> _programs = new();
        private int _failures;
        private (Hash128, Hash128, Hash128) _cacheKey;
        private (ShaderSource[] Sources, int[] Order) _pendingCacheAppend;
        private int _computeLogs;

        private readonly NvnProgramCache _cache;
        private bool _cacheLoaded;

        public NvnPrograms(GpuContext context, MemoryManager memory)
        {
            _context = context;
            _memory = memory;
            _cache = new NvnProgramCache(context);
        }

        public int Count { get; private set; }

        /// <summary>The stages a program object's SetShaders gave (read once per serial: SetShaders bumps it).</summary>
        private sealed class Bound
        {
            public readonly NvnShaderStage[] Stages = new NvnShaderStage[6];
            public readonly bool[] Present = new bool[6];
            public List<NvnProgram> Variants;
            public string Error;
            public (Hash128, Hash128, Hash128) Key;
        }

        private readonly Dictionary<ulong, Bound> _bySerial = new();

        public NvnProgram Get(in PvBindProgram bind, NvnTranslationState state, out string error)
        {
            if (!_cacheLoaded)
            {
                _cacheLoaded = true;

                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int loaded = _cache.Load(_programs);
                Count += loaded;

                if (_cache.Enabled)
                {
                    Logger.Info?.Print(LogClass.Gpu, $"nvnpv: {loaded} programs from the program cache in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms");
                }
            }

            if (!_bySerial.TryGetValue(bind.Serial, out Bound b))
            {
                b = Read(bind);
                _bySerial[bind.Serial] = b;
            }

            error = b.Error;

            if (b.Error != null)
            {
                return null;
            }

            Hash128 graphicsKey = b.Present[NvnShaderStage.StageCompute] ? default : GraphicsKeyOf(state);

            foreach (NvnProgram p in b.Variants)
            {
                if (p.GraphicsKey == graphicsKey && Matches(p, state))
                {
                    if (p.FromCache)
                    {
                        // Same code (the hash covers c1 too): the stages read now complete the cached translation.
                        for (int s = 0; s < 6; s++)
                        {
                            if (b.Present[s] && p.Stages[s].C1 == null)
                            {
                                p.Stages[s] = b.Stages[s];
                            }
                        }
                    }

                    return p;
                }
            }

            NvnProgram program = Translate(b.Stages, b.Present, state, b.Key);
            program.GraphicsKey = graphicsKey;

            if (_pendingCacheAppend.Sources != null)
            {
                _cache.Append(b.Key, program, _pendingCacheAppend.Sources, _pendingCacheAppend.Order, 0, 0, 0);
                _pendingCacheAppend = default;
            }
            b.Variants.Add(program);
            Count++;

            error = program.Failed ? program.Error : null;

            return program;
        }

        private Bound Read(in PvBindProgram bind)
        {
            Bound b = new();
            Hash128 h0 = default, h1 = default, h2 = default;

            for (int i = 0; i < Math.Min(bind.NumStages, 6); i++)
            {
                PvShaderData sd = i switch { 0 => bind.Sd0, 1 => bind.Sd1, 2 => bind.Sd2, 3 => bind.Sd3, 4 => bind.Sd4, _ => bind.Sd5 };

                if (!NvnShaderStage.TryRead(_memory, sd.Data, sd.Control, out NvnShaderStage stage, out string error))
                {
                    b.Error = error;
                    return b;
                }

                if (b.Present[stage.Stage])
                {
                    continue;
                }

                b.Stages[stage.Stage] = stage;
                b.Present[stage.Stage] = true;

                switch (stage.Stage)
                {
                    case NvnShaderStage.StageVertex: h0 = stage.Hash; break;
                    case NvnShaderStage.StageFragment: h1 = stage.Hash; break;
                    default: h2 = new Hash128(h2.Low ^ stage.Hash.Low * (ulong)(stage.Stage + 1), h2.High ^ stage.Hash.High); break;
                }
            }

            // Programs with the same code share their translations, whichever object binds them.
            var key = (h0, h1, h2);

            if (!_programs.TryGetValue(key, out List<NvnProgram> list))
            {
                list = new List<NvnProgram>();
                _programs[key] = list;
            }

            b.Variants = list;
            b.Key = key;

            return b;
        }

        private static bool Matches(NvnProgram p, NvnTranslationState state)
        {
            for (int s = 0; s < 6; s++)
            {
                NvnAnswers a = p.Answers[s];

                if (a == null)
                {
                    continue;
                }

                foreach (KeyValuePair<(int Handle, int Slot), SamplerType> kv in a.Samplers)
                {
                    PvTex? t = state.ResolveTexture(s, kv.Key.Handle, kv.Key.Slot, out PvView view, out bool hasView);
                    SamplerType now = t is PvTex tex ? NvnTranslationState.SamplerTypeOf(tex, view, hasView) : SamplerType.Texture2D;

                    if (now != kv.Value)
                    {
                        return false;
                    }
                }

                foreach (KeyValuePair<(int Handle, int Slot), TextureFormat> kv in a.Formats)
                {
                    PvTex? t = state.ResolveTexture(s, kv.Key.Handle, kv.Key.Slot, out PvView view, out bool hasView);
                    TextureFormat now = t is PvTex tex ? NvnTranslationState.ImageFormatOf(tex, view, hasView) : TextureFormat.R8G8B8A8Unorm;

                    if (now != kv.Value)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static Hash128 GraphicsKeyOf(NvnTranslationState state)
        {
            Span<byte> key = stackalloc byte[3 + 32 + 8];
            key[0] = (byte)state.Topology;
            key[1] = (byte)(state.AlphaToCoverage ? 1 : 0);
            key[2] = (byte)(state.DepthMinusOneToOne ? 1 : 0);

            for (int i = 0; i < 32; i++)
            {
                key[3 + i] = (byte)state.AttributeTypes[i];
            }

            for (int i = 0; i < 8; i++)
            {
                key[35 + i] = (byte)state.FragmentOutputTypes[i];
            }

            return Hash128.ComputeHash(key);
        }

        private NvnProgram Translate(NvnShaderStage[] stages, bool[] present, NvnTranslationState state, (Hash128, Hash128, Hash128) key)
        {
            _cacheKey = key;

            NvnProgram program = new();

            for (int s = 0; s < 6; s++)
            {
                if (present[s])
                {
                    program.Stages[s] = stages[s];
                }
            }

            try
            {
                if (present[NvnShaderStage.StageCompute])
                {
                    TranslateCompute(program, stages[NvnShaderStage.StageCompute], state);
                }
                else
                {
                    TranslateGraphics(program, stages, present, state);
                }
            }
            catch (Exception e)
            {
                program.Failed = true;
                program.Error = $"{e.GetType().Name}: {e.Message}";

                if (_failures++ < 20)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"nvnpv: shader translation failed: {e}");
                }
            }

            return program;
        }

        private void TranslateCompute(NvnProgram program, NvnShaderStage stage, NvnTranslationState state)
        {
            ResourceCounts counts = new();
            NvnAnswers answers = new();
            NvnGpuAccessor accessor = new(_context, _memory, counts, 0, stage, state, answers);
            accessor.InitializeReservedCounts(false, false);

            TranslatorContext context = ShaderCache.DecodeComputeShader(accessor, _context.Capabilities.Api, stage.CodeAddress);
            ShaderProgram translated = context.Translate();

            ShaderSource[] sources = { ShaderCache.CreateShaderSource(translated) };
            ShaderInfo info = ShaderInfoBuilder.BuildForCompute(_context, translated.Info, new ComputeSize(stage.LocalSizeX, stage.LocalSizeY, stage.LocalSizeZ));

            program.Host = _context.Renderer.CreateProgram(sources, info);
            program.Info[NvnShaderStage.StageCompute] = translated.Info;
            program.NoteSegments(NvnShaderStage.StageCompute, translated.Info);

            // The control section's shared and local memory sizes are not located yet: the translator's
            // defaults apply (4 KiB shared, 128 bytes local). Log the section's tail for finding them.
            if (_computeLogs++ < 80)
            {
                System.Text.StringBuilder sb = new();

                for (int o = 0; o < stage.ControlTail.Length; o += 4)
                {
                    sb.Append($" {BitConverter.ToUInt32(stage.ControlTail, o):X}");
                }

                string usesShared = sources[0].Code?.Contains("shared_memory", StringComparison.Ordinal) == true ? "shared" : "-";
                string usesLocal = sources[0].Code?.Contains("local_memory", StringComparison.Ordinal) == true ? "local" : "-";

                Logger.Info?.Print(LogClass.Gpu, $"nvnpv compute {stage.Hash} at 0x{stage.Data:X} {stage.LocalSizeX}x{stage.LocalSizeY}x{stage.LocalSizeZ} {usesShared} {usesLocal} ctl+0x700:{sb}");
            }
            program.Answers[NvnShaderStage.StageCompute] = answers;
            program.LocalSize = new[] { stage.LocalSizeX, stage.LocalSizeY, stage.LocalSizeZ };
            program.Compute = true;
            _cache.Append(_cacheKey, program, sources, new[] { NvnShaderStage.StageCompute }, stage.LocalSizeX, stage.LocalSizeY, stage.LocalSizeZ);
        }

        // NVN stage -> Ryujinx graphics stage index (vertex 0, tess control 1, tess evaluation 2, geometry 3, fragment 4).
        private static int GraphicsIndexOf(int nvnStage) => nvnStage switch
        {
            NvnShaderStage.StageVertex => 0,
            NvnShaderStage.StageTessControl => 1,
            NvnShaderStage.StageTessEvaluation => 2,
            NvnShaderStage.StageGeometry => 3,
            NvnShaderStage.StageFragment => 4,
            _ => -1,
        };

        private void TranslateGraphics(NvnProgram program, NvnShaderStage[] stages, bool[] present, NvnTranslationState state)
        {
            ResourceCounts counts = new();
            TranslatorContext[] contexts = new TranslatorContext[5];
            NvnGpuAccessor[] accessors = new NvnGpuAccessor[5];
            int[] nvnOf = new int[5];
            TranslatorContext next = null;

            for (int g = 4; g >= 0; g--)
            {
                int nvn = g switch { 0 => NvnShaderStage.StageVertex, 1 => NvnShaderStage.StageTessControl, 2 => NvnShaderStage.StageTessEvaluation, 3 => NvnShaderStage.StageGeometry, _ => NvnShaderStage.StageFragment };

                if (!present[nvn])
                {
                    continue;
                }

                NvnAnswers answers = new();
                NvnGpuAccessor accessor = new(_context, _memory, counts, g, stages[nvn], state, answers);
                TranslatorContext current = ShaderCache.DecodeGraphicsShader(accessor, _context.Capabilities.Api, ShaderCache.DefaultFlags, stages[nvn].CodeAddress);

                if (next != null)
                {
                    current.SetNextStage(next);
                }

                contexts[g] = current;
                accessors[g] = accessor;
                nvnOf[g] = nvn;
                next = current;
            }

            ShaderInfoBuilder infoBuilder = new(_context, false, false);
            List<ShaderSource> sources = new();
            List<int> order = new();

            for (int g = 0; g < 5; g++)
            {
                if (contexts[g] == null)
                {
                    continue;
                }

                order.Add(nvnOf[g]);

                accessors[g].InitializeReservedCounts(false, false);
                ShaderProgram translated = contexts[g].Translate();

                sources.Add(ShaderCache.CreateShaderSource(translated));
                infoBuilder.AddStageInfo(translated.Info);

                program.Info[nvnOf[g]] = translated.Info;
                program.NoteSegments(nvnOf[g], translated.Info);
                program.Answers[nvnOf[g]] = accessors[g].Answers;
            }

            ShaderSource[] sourceArray = sources.ToArray();
            program.Host = _context.Renderer.CreateProgram(sourceArray, infoBuilder.Build(null));
            _pendingCacheAppend = (sourceArray, order.ToArray());
        }

        public void Dispose()
        {
            _cache.Dispose();

            foreach (List<NvnProgram> list in _programs.Values)
            {
                foreach (NvnProgram p in list)
                {
                    p.Host?.Dispose();
                }
            }

            _programs.Clear();
        }
    }
}
