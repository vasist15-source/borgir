using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Gpu.Nvn
{
    /// <summary>
    /// What the shader translator asks about an NVN program stage, answered from the NVN state of the
    /// draw being translated for. Bindings come from <see cref="GpuAccessorBase"/>, as for the Maxwell
    /// path, so the Metal backend sees the same resource layout. Every answer that depends on the draw
    /// (texture targets, image formats, attribute and output types) is recorded in <see cref="Answers"/>:
    /// a translated program is reused only for draws that give the same answers.
    /// </summary>
    sealed class NvnGpuAccessor : GpuAccessorBase, IGpuAccessor
    {
        private readonly MemoryManager _memory;
        private readonly NvnShaderStage _stage;
        private readonly NvnTranslationState _state;

        public readonly NvnAnswers Answers;

        public NvnGpuAccessor(GpuContext context, MemoryManager memory, ResourceCounts counts, int stageIndex, in NvnShaderStage stage, NvnTranslationState state, NvnAnswers answers)
            : base(context, counts, stageIndex)
        {
            _memory = memory;
            _stage = stage;
            _state = state;
            Answers = answers;
        }

        public uint ConstantBuffer1Read(int offset)
        {
            byte[] c1 = _stage.C1;

            return offset >= 0 && offset + 4 <= c1.Length ? BitConverter.ToUInt32(c1, offset) : 0u;
        }

        public void Log(string message)
        {
            Logger.Warning?.Print(LogClass.Gpu, $"nvnpv shader translator: {message}");
        }

        public ReadOnlySpan<ulong> GetCode(ulong address, int minimumSize)
        {
            int size = Math.Max(minimumSize, 0x1000 - (int)(address & 0xfff));

            return MemoryMarshal.Cast<byte, ulong>(_memory.GetSpan(address, size));
        }

        public int QueryComputeLocalSizeX() => _stage.LocalSizeX;
        public int QueryComputeLocalSizeY() => _stage.LocalSizeY;
        public int QueryComputeLocalSizeZ() => _stage.LocalSizeZ;
        public int QueryComputeLocalMemorySize() => _state.ComputeLocalMemorySize;
        public int QueryComputeSharedMemorySize() => _stage.SharedMemorySize;

        // Every NVN uniform buffer slot (Maxwell c3..c16) may be read; c0..c2 are the driver's.
        public uint QueryConstantBufferUse() => 0xFFFFu;

        public GpuGraphicsState QueryGraphicsState()
        {
            Answers.UsedGraphicsState = true;

            return new GpuGraphicsState(
                false,
                _state.Topology,
                false,
                TessPatchType.Triangles,
                TessSpacing.EqualSpacing,
                _state.AlphaToCoverage,
                false,
                false,
                _state.DepthMinusOneToOne,
                false,
                1f,
                AlphaTestOp.Always,
                0f,
                in _state.AttributeTypes,
                false,
                in _state.FragmentOutputTypes,
                false,
                false,
                true,
                false);
        }

        public bool QueryHasConstantBufferDrawParameters() => false;
        public bool QueryHasUnalignedStorageBuffer() => false;
        public int QuerySamplerArrayLengthFromPool() => 32;
        public int QueryTextureArrayLengthFromBuffer(int slot) => 32;
        public int QueryTextureArrayLengthFromPool() => 32;
        public bool QueryTransformFeedbackEnabled() => false;
        public ReadOnlySpan<byte> QueryTransformFeedbackVaryingLocations(int bufferIndex) => ReadOnlySpan<byte>.Empty;
        public int QueryTransformFeedbackStride(int bufferIndex) => 0;

        public SamplerType QuerySamplerType(int handle, int cbufSlot = -1)
        {
            PvTex? t = _state.ResolveTexture(_stage.Stage, handle, cbufSlot, out PvView view, out bool hasView);
            SamplerType type = t is PvTex tex ? NvnTranslationState.SamplerTypeOf(tex, view, hasView) : SamplerType.Texture2D;

            Answers.Samplers[(handle, cbufSlot)] = type;

            return type;
        }

        public bool QueryTextureCoordNormalized(int handle, int cbufSlot = -1) => true;

        public TextureFormat QueryTextureFormat(int handle, int cbufSlot = -1)
        {
            PvTex? t = _state.ResolveTexture(_stage.Stage, handle, cbufSlot, out PvView view, out bool hasView);
            TextureFormat format = t is PvTex tex ? NvnTranslationState.ImageFormatOf(tex, view, hasView) : TextureFormat.R8G8B8A8Unorm;

            Answers.Formats[(handle, cbufSlot)] = format;

            return format;
        }

        public void RegisterTexture(int handle, int cbufSlot)
        {
        }
    }

    /// <summary>The answers a translation depended on.</summary>
    sealed class NvnAnswers
    {
        public readonly Dictionary<(int Handle, int Slot), SamplerType> Samplers = new();
        public readonly Dictionary<(int Handle, int Slot), TextureFormat> Formats = new();
        public bool UsedGraphicsState;
    }
}
