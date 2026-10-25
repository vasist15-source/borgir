using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Gpu.Nvn
{
    // Mirrors of the paravirtual NVN guest layer's records (Ryujinx.HLE HOS/Kernel/Process/NvnPv/Guest/pv_types.h and pv_core.h). Sizes are checked at start-up (NvnPvRecords.Check),
    // so a layout change on one side alone stops the layer instead of misreading records.

    [StructLayout(LayoutKind.Sequential)]
    public struct PvTexDesc
    {
        public uint Width, Height, Depth, Levels;
        public uint Stride;
        public uint Flags;
        public ushort Format;
        public byte Target;
        public byte Samples;
        public sbyte PkgBlockH;
        public byte SwizzleR, SwizzleG, SwizzleB, SwizzleA;
        public byte Pad0, Pad1, Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PvTexLayout
    {
        public ulong Size, LayerSize;
        public uint Align, Layers, Bpb;
        public byte TileH, TileD;
        public ushort Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PvTex
    {
        public ulong Addr;
        public ulong Serial;
        public PvTexDesc D;
        public uint Pad;
        public PvTexLayout L;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PvView
    {
        public byte Set;          // bit 0 levels, 1 layers, 2 format, 3 target, 4 swizzle
        public byte BaseLevel, NumLevels, Target;
        public ushort MinLayer, NumLayers;
        public ushort Format;
        public byte SwizzleR, SwizzleG, SwizzleB, SwizzleA;
        public ushort Pad;

        public readonly bool SetLevels => (Set & 1) != 0;
        public readonly bool SetLayers => (Set & 2) != 0;
        public readonly bool SetFormat => (Set & 4) != 0;
        public readonly bool SetTarget => (Set & 8) != 0;
        public readonly bool SetSwizzle => (Set & 16) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PvTexEntry
    {
        public PvTex Tex;
        public PvView View;
        public uint HasView, Valid;
        public ulong Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PvSamp
    {
        public byte MinFilter, MagFilter, WrapS, WrapT, WrapR, CompareMode, CompareFunc, Reduction;
        public float LodMin, LodMax, LodBias, MaxAnisotropy;
        public float Border0, Border1, Border2, Border3;
        public float LodSnap;
        public uint BorderInt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PvSampEntry
    {
        public PvSamp Samp;
        public uint Valid, Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PvCopyRegion
    {
        public int X, Y, Z, Width, Height, Depth;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PvRtTarget
    {
        public PvTex Tex;
        public PvView View;
        public uint Present, HasView;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PvShaderData
    {
        public ulong Data;      // GPU (= guest) address of the code
        public ulong Control;   // guest address of the control section
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PvBindProgram
    {
        public int Stages, NumStages;
        public ulong Serial;
        public PvShaderData Sd0, Sd1, Sd2, Sd3, Sd4, Sd5;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PvCopyBt
    {
        public ulong Buffer;
        public PvTex Tex;
        public PvView View;
        public int HasView, Flags;
        public PvCopyRegion Region;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PvCopyT2T
    {
        public PvTex Src, Dst;
        public PvView SrcView, DstView;
        public int HasSrcView, HasDstView;
        public PvCopyRegion SrcRegion, DstRegion;
        public int Flags, Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PvClearTex
    {
        public PvTex Tex;
        public PvView View;
        public int HasView, Mask, IsUint, Pad;
        public PvCopyRegion Region;
        public uint C0, C1, C2, C3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PvTexels
    {
        public ulong Write;
        public ulong Data;
        public long Row, Image;
        public PvTex Tex;
        public PvView View;
        public uint HasView, Pad;
        public PvCopyRegion Region;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PvVmapReq
    {
        public ulong PhysicalAddr, VirtualOffset, Size, PhysicalSize;
    }

    /// <summary>Record operations (Guest/pv_types.h enum).</summary>
    public enum PvOp : uint
    {
        End = 0, Jump, Call,
        SetRenderTargets, ClearColor, ClearDepthStencil, Viewports, Scissors, DepthRanges,
        BindProgram, BindState, BindVertexAttribs, BindVertexStreams, BindVertexBuffers,
        BindUniformBuffers, BindStorageBuffers, BindTextures, BindImages, BindSamplers,
        Stencil, PolygonOffset, BlendColor, DepthClamp, RenderEnable, RenderConditional, RasterizerDiscard,
        Draw, DrawIndexed, DrawIndirect, DrawIndexedIndirect, Dispatch, DispatchIndirect,
        Barrier, SetTexturePool, SetSamplerPool,
        UpdateUniformBuffer, CopyBufferToBuffer, CopyBufferToTexture, CopyTextureToBuffer, CopyTextureToTexture, CopyRowStride, ClearTexture, ClearBuffer,
        ReportCounter, ResetCounter, SignalEvent, WaitEvent,
        PrimitiveRestart, LineWidth, PointSize, AlphaRef, SampleMask, DepthBounds,
        TiledCache, Discard, Scratch, PatchSize, DrawTexture,
        Count,
    }

    public static class NvnPvRecords
    {
        public const ulong SegmentHeader = 16;
        public const uint MarkMagic = 0x4B52414Du; // "MARK"

        public static void Check()
        {
            Expect<PvTexDesc>(36);
            Expect<PvTexLayout>(32);
            Expect<PvTex>(88);
            Expect<PvView>(16);
            Expect<PvTexEntry>(120);
            Expect<PvSamp>(48);
            Expect<PvSampEntry>(56);
            Expect<PvRtTarget>(112);
            Expect<PvBindProgram>(112);
            Expect<PvTexels>(168);
            Expect<PvCopyBt>(144);
            Expect<PvClearTex>(160);
            Expect<PvCopyT2T>(272);
        }

        private static void Expect<T>(int size) where T : unmanaged
        {
            if (Unsafe.SizeOf<T>() != size)
            {
                throw new InvalidOperationException($"nvnpv: {typeof(T).Name} is {Unsafe.SizeOf<T>()} bytes, the guest layout says {size}");
            }
        }
    }
}
