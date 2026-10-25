using Ryujinx.Common;
using Ryujinx.Graphics.Gpu.Memory;
using System;
using System.Buffers.Binary;

namespace Ryujinx.Graphics.Gpu.Nvn
{
    /// <summary>
    /// One stage of an NVN program: NVNshaderData {data = GPU address of the code area, control =
    /// address of the compiler's control section}. Layout as MacNativeGames found it for TOTK
    /// (totk_shader_rt.m): graphics code areas start with a 0x30-byte header before the shader
    /// program header; control +0x700 is where c1 starts in the code area and +0x704 its total
    /// length (c1, the constant data BRX jump tables live in, is its tail); +0x714 the NVN stage;
    /// +0x748 the compute thread group size; +0x20 the control section's own length.
    /// </summary>
    readonly struct NvnShaderStage
    {
        public const int StageVertex = 0;
        public const int StageFragment = 1;
        public const int StageGeometry = 2;
        public const int StageTessControl = 3;
        public const int StageTessEvaluation = 4;
        public const int StageCompute = 5;

        private const int GraphicsHeader = 0x30;

        public readonly int Stage;
        public readonly ulong Data;
        public readonly int CodeOffset;     // where the translator starts (the shader program header for graphics stages)
        public readonly int CodeLength;     // bytes from CodeOffset to the end of the code area
        public readonly byte[] C1;
        public readonly int LocalSizeX, LocalSizeY, LocalSizeZ;
        public readonly Hash128 Hash;
        public readonly byte[] ControlTail;   // control +0x700..+0x760, kept for compute stages (field hunting)

        /// <summary>
        /// Shared memory bytes of a compute stage: control +0x754 (TOTK, 31 kernels: non-zero - 0x80,
        /// 0x180, 0x380 - exactly for the six that use shared memory), in hardware granules of 256.
        /// </summary>
        public int SharedMemorySize => ControlTail == null ? 0 : (BitConverter.ToInt32(ControlTail, 0x54) + 255) & ~255;

        private NvnShaderStage(int stage, ulong data, int codeOffset, int codeLength, byte[] c1, int lx, int ly, int lz, Hash128 hash, byte[] controlTail)
        {
            ControlTail = controlTail;
            Stage = stage;
            Data = data;
            CodeOffset = codeOffset;
            CodeLength = codeLength;
            C1 = c1;
            LocalSizeX = lx;
            LocalSizeY = ly;
            LocalSizeZ = lz;
            Hash = hash;
        }

        public ulong CodeAddress => Data + (ulong)CodeOffset;

        /// <param name="gpu">The replay's GPU memory manager: the code is at a GPU address (a virtual pool's
        /// differs from any CPU address), the control section at a CPU one.</param>
        public static bool TryRead(MemoryManager gpu, ulong data, ulong control, out NvnShaderStage stage, out string error)
        {
            stage = default;

            if (data == 0 || control == 0)
            {
                error = "null shader data or control section";
                return false;
            }

            PhysicalMemory memory = gpu.Physical;
            ReadOnlySpan<byte> ctl = memory.GetSpan(control, 0x760);
            uint c1Offset = BinaryPrimitives.ReadUInt32LittleEndian(ctl[0x700..]);
            uint total = BinaryPrimitives.ReadUInt32LittleEndian(ctl[0x704..]);
            int nvnStage = (int)BinaryPrimitives.ReadUInt32LittleEndian(ctl[0x714..]);
            int skip = nvnStage == StageCompute ? 0 : GraphicsHeader;

            if (nvnStage < 0 || nvnStage > StageCompute || total == 0 || total > 16 << 20 || c1Offset > total || c1Offset < skip)
            {
                error = $"implausible control section: stage {nvnStage}, c1 0x{c1Offset:X}, code area 0x{total:X}";
                return false;
            }

            int lx = 1, ly = 1, lz = 1;

            if (nvnStage == StageCompute)
            {
                lx = (int)BinaryPrimitives.ReadUInt32LittleEndian(ctl[0x748..]);
                ly = (int)BinaryPrimitives.ReadUInt32LittleEndian(ctl[0x74C..]);
                lz = (int)BinaryPrimitives.ReadUInt32LittleEndian(ctl[0x750..]);

                if (lx <= 0 || ly <= 0 || lz <= 0 || (long)lx * ly * lz > 1024)
                {
                    error = $"implausible compute thread group size {lx} x {ly} x {lz}";
                    return false;
                }
            }

            // Tracked: code the GPU copied into place is flushed first.
            ReadOnlySpan<byte> area = gpu.GetSpan(data, (int)total, tracked: true);
            byte[] c1 = area[(int)c1Offset..].ToArray();

            // The code (c1 included) identifies the stage; the thread group size is part of a compute stage.
            Hash128 hash = Hash128.ComputeHash(area[skip..]);

            if (nvnStage == StageCompute)
            {
                hash.Low ^= (ulong)lx * 0x9E3779B97F4A7C15UL ^ (ulong)ly << 21 ^ (ulong)lz << 42;
            }

            stage = new NvnShaderStage(nvnStage, data, skip, (int)total - skip, c1, lx, ly, lz, hash, nvnStage == StageCompute ? ctl[0x700..0x760].ToArray() : null);
            error = null;

            return true;
        }
    }
}
