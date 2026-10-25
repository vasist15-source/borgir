using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Gpu.Nvn
{
    /// <summary>
    /// The NVN state a command buffer builds up between draws, as the replay sees it: what the last
    /// records set, plus dirty flags so a draw only recommits what changed. Values are kept in NVN's
    /// own encodings (NvnGal converts them when committed).
    /// </summary>
    sealed class NvnState
    {
        public const int MaxColorTargets = 8;
        public const int MaxViewports = 16;
        public const int MaxVertexAttribs = 32;
        public const int MaxVertexStreams = 32;
        public const int Stages = 6;
        public const int MaxUniformBuffers = 18;
        public const int MaxStorageBuffers = 16;
        public const int MaxTextures = 128;
        public const int MaxImages = 32;

        [Flags]
        public enum Dirty : uint
        {
            None = 0,
            RenderTargets = 1 << 0,
            Viewports = 1 << 1,
            Scissors = 1 << 2,
            Program = 1 << 3,
            Blend = 1 << 4,
            Color = 1 << 5,
            ChannelMask = 1 << 6,
            Multisample = 1 << 7,
            Polygon = 1 << 8,
            DepthStencil = 1 << 9,
            VertexAttribs = 1 << 10,
            VertexStreams = 1 << 11,
            VertexBuffers = 1 << 12,
            UniformBuffers = 1 << 13,
            StorageBuffers = 1 << 14,
            Textures = 1 << 15,
            Stencil = 1 << 16,
            PolygonOffset = 1 << 17,
            BlendColor = 1 << 18,
            DepthClamp = 1 << 19,
            RasterizerDiscard = 1 << 20,
            PrimitiveRestart = 1 << 21,
            LineWidth = 1 << 22,
            PointSize = 1 << 23,
            DepthRanges = 1 << 24,
            SampleMask = 1 << 25,
            All = 0xFFFFFFFF,
        }

        public Dirty Changed = Dirty.All;

        // Render targets (copied from OP_SET_RT).
        public readonly PvRtTarget[] Colors = new PvRtTarget[MaxColorTargets];
        public int ColorCount;
        public PvRtTarget Depth;
        public bool HasDepth;

        public readonly float[] Viewports = new float[MaxViewports * 4];
        public readonly int[] Scissors = new int[MaxViewports * 4];
        public readonly float[] DepthRanges = new float[MaxViewports * 2];
        public int ViewportCount = 1, ScissorCount = 1, DepthRangeCount = 1;

        public PvBindProgram Program;
        public bool HasProgram;

        // State objects, packed as the guest packs them (Guest/pv_types.h).
        public readonly ulong[] Blend = new ulong[MaxColorTargets];   // blend_st per target (the state carries its target)
        public uint ColorState;
        public uint ChannelMask = 0xFFFFFFFF;
        public ulong MultisampleLo, MultisampleHi;
        public uint Polygon;
        public ulong DepthStencil;

        public readonly uint[] VertexAttribs = new uint[MaxVertexAttribs];
        public int VertexAttribCount;
        public readonly uint[] StreamStride = new uint[MaxVertexStreams];
        public readonly uint[] StreamDivisor = new uint[MaxVertexStreams];
        public int VertexStreamCount;
        public readonly ulong[] VertexBufferAddress = new ulong[MaxVertexStreams];
        public readonly ulong[] VertexBufferSize = new ulong[MaxVertexStreams];

        public readonly ulong[,] UniformAddress = new ulong[Stages, MaxUniformBuffers];
        public readonly ulong[,] UniformSize = new ulong[Stages, MaxUniformBuffers];
        public readonly ulong[,] StorageAddress = new ulong[Stages, MaxStorageBuffers];
        public readonly ulong[,] StorageSize = new ulong[Stages, MaxStorageBuffers];
        // The NVN driver's constant segments as the shaders see them (MacNativeGames measured TOTK's
        // layout): c2 holds bound handles - combined texture handles at 0x20 + 8 * slot, image handles
        // at 0x120 + 8 * slot, separate textures at 0x168 + 8 * slot, separate samplers at 0x568 + 8 * slot;
        // c0 holds the storage buffer table (vertex 0x110, compute 0x310, fragment 0x510, 16 bytes per
        // slot: address, size) and, for compute, thread group counts at 0x200 and uniform buffers at
        // 0x210 + 16 * slot. Kept as images per stage so translated shaders that still read the segments
        // get them as constant buffers, and texture bindings resolve through TextureHandle.ReadPackedId.
        public const int C2Words = 0x980 / 4;
        public const int C0Bytes = 0x800;
        public const int CombinedTextureWord = 0x20 / 4;
        public const int ImageWord = 0x120 / 4;
        public const int SeparateTextureWord = 0x168 / 4;
        public const int SeparateSamplerWord = 0x568 / 4;

        public readonly int[][] C2 = CreateArrays<int>(Stages, C2Words);
        public readonly byte[][] C0 = CreateArrays<byte>(Stages, C0Bytes);

        private static T[][] CreateArrays<T>(int n, int size)
        {
            T[][] a = new T[n][];

            for (int i = 0; i < n; i++)
            {
                a[i] = new T[size];
            }

            return a;
        }

        public static int StorageTableBase(int stage) => stage switch { 0 => 0x110, 1 => 0x510, 5 => 0x310, _ => -1 };

        public readonly bool[] C2Dirty = new bool[Stages];
        public readonly bool[] C0Dirty = new bool[Stages];

        public void WriteC0(int stage, int offset, ulong address, ulong size)
        {
            byte[] c0 = C0[stage];

            if (offset + 12 <= c0.Length)
            {
                BitConverter.TryWriteBytes(c0.AsSpan(offset), address);
                BitConverter.TryWriteBytes(c0.AsSpan(offset + 8), (uint)size);
                C0Dirty[stage] = true;
            }
        }

        public ulong TexturePool;
        public int TexturePoolCount;
        public ulong SamplerPool;
        public int SamplerPoolCount;

        // Stencil: [0] front, [1] back.
        public readonly int[] StencilValueMask = { 0xFF, 0xFF };
        public readonly int[] StencilWriteMask = { 0xFF, 0xFF };
        public readonly int[] StencilRef = { 0, 0 };

        public float PolygonOffsetFactor, PolygonOffsetUnits, PolygonOffsetClamp;
        public readonly float[] BlendColor = new float[4];
        public bool DepthClamp;
        public bool RenderEnable = true;
        public bool RasterizerDiscard;
        public bool PrimitiveRestart;
        public int PrimitiveRestartIndex = -1;
        public float LineWidth = 1f;
        public float PointSize = 1f;
        public float AlphaRef;
        public int SampleMask = -1;
        public long CopyRowStride;

        // Conditional rendering (OP_RENDER_COND): mode and counter address, 0 = off.
        public int ConditionMode;
        public ulong ConditionAddress;

        public static int StageIndex(int nvnStage) => nvnStage is >= 0 and < Stages ? nvnStage : 0;

        /// <summary>The state objects' SetDefaults values (Guest/pv_obj.c), for whatever a game never binds.</summary>
        public NvnState()
        {
            const ulong One = 2, Zero = 1, Add = 1;

            for (int i = 0; i < MaxColorTargets; i++)
            {
                Blend[i] = (ulong)i | One << 3 | Zero << 10 | One << 17 | Zero << 24 | Add << 31 | Add << 34;
            }

            ColorState = 3u << 8 | 8u << 12;                 // logic op COPY, alpha test ALWAYS
            Polygon = 0x14;                                  // cull none, front CCW, fill
            MultisampleLo = 1;                               // multisampling enabled

            const ulong Less = 2, Always = 8, Keep = 1;
            DepthStencil = 1UL << 1 | Less << 2 | Always << 7 | Always << 11 |
                           Keep << 15 | Keep << 19 | Keep << 23 | Keep << 27 | Keep << 31 | Keep << 35;

            for (int i = 0; i < MaxViewports; i++)
            {
                DepthRanges[i * 2 + 1] = 1f;
                Scissors[i * 4 + 2] = 0xFFFF;
                Scissors[i * 4 + 3] = 0xFFFF;
            }
        }
    }

    /// <summary>Payload readers for the record structs (Guest/pv_types.h), on a flattened submission.</summary>
    static unsafe class NvnPayload
    {
        public static ref readonly T As<T>(byte* p) where T : unmanaged => ref *(T*)p;

        public static ReadOnlySpan<T> Array<T>(byte* p, int count) where T : unmanaged => new(p, count);

        public static int SizeOf<T>() where T : unmanaged => Marshal.SizeOf<T>();
    }
}
