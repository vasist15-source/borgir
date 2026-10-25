using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Nvn
{
    /// <summary>
    /// NVN enumerations to GAL. NVN values from nvn.h (h1k421's reconstruction); most NVN state enums
    /// share GAL's order and only need an offset.
    /// </summary>
    static class NvnGal
    {
        /// <summary>GAL format of an NVN format, and whether alpha reads as one (the RGBX / RGB_DXT1 kinds).</summary>
        public static (Format Format, bool AlphaOne) Format(int nvn)
        {
            if (nvn >= 0x79 && nvn <= 0x94)
            {
                int k = (nvn - 0x79) % 14;

                return ((nvn < 0x87 ? GAL.Format.Astc4x4Unorm : GAL.Format.Astc4x4Srgb) + k, false);
            }

            return nvn switch
            {
                0x01 => (GAL.Format.R8Unorm, false),
                0x02 => (GAL.Format.R8Snorm, false),
                0x03 => (GAL.Format.R8Uint, false),
                0x04 => (GAL.Format.R8Sint, false),
                0x05 => (GAL.Format.R16Float, false),
                0x06 => (GAL.Format.R16Unorm, false),
                0x07 => (GAL.Format.R16Snorm, false),
                0x08 => (GAL.Format.R16Uint, false),
                0x09 => (GAL.Format.R16Sint, false),
                0x0A => (GAL.Format.R32Float, false),
                0x0B => (GAL.Format.R32Uint, false),
                0x0C => (GAL.Format.R32Sint, false),
                0x0D => (GAL.Format.R8G8Unorm, false),
                0x0E => (GAL.Format.R8G8Snorm, false),
                0x0F => (GAL.Format.R8G8Uint, false),
                0x10 => (GAL.Format.R8G8Sint, false),
                0x11 => (GAL.Format.R16G16Float, false),
                0x12 => (GAL.Format.R16G16Unorm, false),
                0x13 => (GAL.Format.R16G16Snorm, false),
                0x14 => (GAL.Format.R16G16Uint, false),
                0x15 => (GAL.Format.R16G16Sint, false),
                0x16 => (GAL.Format.R32G32Float, false),
                0x17 => (GAL.Format.R32G32Uint, false),
                0x18 => (GAL.Format.R32G32Sint, false),
                0x19 => (GAL.Format.R8G8B8Unorm, false),
                0x1A => (GAL.Format.R8G8B8Snorm, false),
                0x1B => (GAL.Format.R8G8B8Uint, false),
                0x1C => (GAL.Format.R8G8B8Sint, false),
                0x1D => (GAL.Format.R16G16B16Float, false),
                0x1E => (GAL.Format.R16G16B16Unorm, false),
                0x1F => (GAL.Format.R16G16B16Snorm, false),
                0x20 => (GAL.Format.R16G16B16Uint, false),
                0x21 => (GAL.Format.R16G16B16Sint, false),
                0x22 => (GAL.Format.R32G32B32Float, false),
                0x23 => (GAL.Format.R32G32B32Uint, false),
                0x24 => (GAL.Format.R32G32B32Sint, false),
                0x25 => (GAL.Format.R8G8B8A8Unorm, false),
                0x26 => (GAL.Format.R8G8B8A8Snorm, false),
                0x27 => (GAL.Format.R8G8B8A8Uint, false),
                0x28 => (GAL.Format.R8G8B8A8Sint, false),
                0x29 => (GAL.Format.R16G16B16A16Float, false),
                0x2A => (GAL.Format.R16G16B16A16Unorm, false),
                0x2B => (GAL.Format.R16G16B16A16Snorm, false),
                0x2C => (GAL.Format.R16G16B16A16Uint, false),
                0x2D => (GAL.Format.R16G16B16A16Sint, false),
                0x2E => (GAL.Format.R32G32B32A32Float, false),
                0x2F => (GAL.Format.R32G32B32A32Uint, false),
                0x30 => (GAL.Format.R32G32B32A32Sint, false),
                0x31 => (GAL.Format.S8Uint, false),
                0x32 => (GAL.Format.D16Unorm, false),
                0x33 => (GAL.Format.X8UintD24Unorm, false),
                0x34 => (GAL.Format.D32Float, false),
                0x35 => (GAL.Format.D24UnormS8Uint, false),
                0x36 => (GAL.Format.D32FloatS8Uint, false),
                0x37 => (GAL.Format.R8G8B8A8Srgb, true),
                0x38 => (GAL.Format.R8G8B8A8Srgb, false),
                0x39 => (GAL.Format.R4G4B4A4Unorm, false),
                0x3A => (GAL.Format.R5G5B5X1Unorm, true),
                0x3B => (GAL.Format.R5G5B5A1Unorm, false),
                0x3C => (GAL.Format.R5G6B5Unorm, false),
                0x3D => (GAL.Format.R10G10B10A2Unorm, false),
                0x3E => (GAL.Format.R10G10B10A2Uint, false),
                0x3F => (GAL.Format.R11G11B10Float, false),
                0x40 => (GAL.Format.R9G9B9E5Float, false),
                0x41 => (GAL.Format.Bc1RgbaUnorm, true),
                0x42 => (GAL.Format.Bc1RgbaUnorm, false),
                0x43 => (GAL.Format.Bc2Unorm, false),
                0x44 => (GAL.Format.Bc3Unorm, false),
                0x45 => (GAL.Format.Bc1RgbaSrgb, true),
                0x46 => (GAL.Format.Bc1RgbaSrgb, false),
                0x47 => (GAL.Format.Bc2Srgb, false),
                0x48 => (GAL.Format.Bc3Srgb, false),
                0x49 => (GAL.Format.Bc4Unorm, false),
                0x4A => (GAL.Format.Bc4Snorm, false),
                0x4B => (GAL.Format.Bc5Unorm, false),
                0x4C => (GAL.Format.Bc5Snorm, false),
                0x4D => (GAL.Format.Bc7Unorm, false),
                0x4E => (GAL.Format.Bc7Srgb, false),
                0x4F => (GAL.Format.Bc6HSfloat, false),
                0x50 => (GAL.Format.Bc6HUfloat, false),
                0x51 => (GAL.Format.R8Uscaled, false),
                0x52 => (GAL.Format.R8Sscaled, false),
                0x53 => (GAL.Format.R16Uscaled, false),
                0x54 => (GAL.Format.R16Sscaled, false),
                0x55 => (GAL.Format.R32Uscaled, false),
                0x56 => (GAL.Format.R32Sscaled, false),
                0x57 => (GAL.Format.R8G8Uscaled, false),
                0x58 => (GAL.Format.R8G8Sscaled, false),
                0x59 => (GAL.Format.R16G16Uscaled, false),
                0x5A => (GAL.Format.R16G16Sscaled, false),
                0x5B => (GAL.Format.R32G32Uscaled, false),
                0x5C => (GAL.Format.R32G32Sscaled, false),
                0x5D => (GAL.Format.R8G8B8Uscaled, false),
                0x5E => (GAL.Format.R8G8B8Sscaled, false),
                0x5F => (GAL.Format.R16G16B16Uscaled, false),
                0x60 => (GAL.Format.R16G16B16Sscaled, false),
                0x61 => (GAL.Format.R32G32B32Uscaled, false),
                0x62 => (GAL.Format.R32G32B32Sscaled, false),
                0x63 => (GAL.Format.R8G8B8A8Uscaled, false),
                0x64 => (GAL.Format.R8G8B8A8Sscaled, false),
                0x65 => (GAL.Format.R16G16B16A16Uscaled, false),
                0x66 => (GAL.Format.R16G16B16A16Sscaled, false),
                0x67 => (GAL.Format.R32G32B32A32Uscaled, false),
                0x68 => (GAL.Format.R32G32B32A32Sscaled, false),
                0x69 => (GAL.Format.R10G10B10A2Snorm, false),
                0x6A => (GAL.Format.R10G10B10A2Sint, false),
                0x6B => (GAL.Format.R10G10B10A2Uscaled, false),
                0x6C => (GAL.Format.R10G10B10A2Sscaled, false),
                0x6D => (GAL.Format.R8G8B8A8Unorm, true),
                0x6E => (GAL.Format.R8G8B8A8Snorm, true),
                0x6F => (GAL.Format.R8G8B8A8Uint, true),
                0x70 => (GAL.Format.R8G8B8A8Sint, true),
                0x71 => (GAL.Format.R16G16B16A16Float, true),
                0x72 => (GAL.Format.R16G16B16A16Unorm, true),
                0x73 => (GAL.Format.R16G16B16A16Snorm, true),
                0x74 => (GAL.Format.R16G16B16A16Uint, true),
                0x75 => (GAL.Format.R16G16B16A16Sint, true),
                0x76 => (GAL.Format.R32G32B32A32Float, true),
                0x77 => (GAL.Format.R32G32B32A32Uint, true),
                0x78 => (GAL.Format.R32G32B32A32Sint, true),
                0x95 => (GAL.Format.B5G6R5Unorm, false),
                0x96 => (GAL.Format.B5G5R5A1Unorm, true),
                0x97 => (GAL.Format.B5G5R5A1Unorm, false),
                0x98 => (GAL.Format.A1B5G5R5Unorm, false),
                0x99 => (GAL.Format.B8G8R8A8Unorm, true),
                0x9A => (GAL.Format.B8G8R8A8Unorm, false),
                0x9B => (GAL.Format.B8G8R8A8Srgb, true),
                0x9C => (GAL.Format.B8G8R8A8Srgb, false),
                _ => (Unknown, false),
            };
        }

        /// <summary>What <see cref="Format(int)"/> gives for an NVN format it does not know (GAL's 0 is R8Unorm).</summary>
        public const GAL.Format Unknown = (GAL.Format)(-1);

        private static readonly Dictionary<GAL.Format, GAL.Format> _nonScaled = BuildNonScaled();

        private static Dictionary<GAL.Format, GAL.Format> BuildNonScaled()
        {
            Dictionary<GAL.Format, GAL.Format> map = new();

            foreach (GAL.Format f in Enum.GetValues<GAL.Format>())
            {
                string name = f.ToString();

                if (name.EndsWith("scaled", StringComparison.Ordinal) &&
                    Enum.TryParse(name.Replace("Uscaled", "Uint").Replace("Sscaled", "Sint"), out GAL.Format integer))
                {
                    map[f] = integer;
                }
            }

            return map;
        }

        /// <summary>
        /// The integer format behind a scaled vertex format, for hosts without scaled vertex formats: the
        /// translated shader converts (attribute type Sscaled / Uscaled), as on the Maxwell path.
        /// </summary>
        public static GAL.Format NonScaled(GAL.Format f) => _nonScaled.TryGetValue(f, out GAL.Format integer) ? integer : f;

        /// <summary>
        /// Bytes per block and block size of an NVN format (the guest's pv_tex.c table), or 0 bytes for
        /// an unknown format.
        /// </summary>
        public static (int Bytes, int BlockWidth, int BlockHeight) BlockOf(int f)
        {
            if (f >= 0x79 && f <= 0x94)
            {
                int k = (f - 0x79) % 14;
                ReadOnlySpan<byte> astc = [4, 4, 5, 4, 5, 5, 6, 5, 6, 6, 8, 5, 8, 6, 8, 8, 10, 5, 10, 6, 10, 8, 10, 10, 12, 10, 12, 12];

                return (16, astc[k * 2], astc[k * 2 + 1]);
            }

            if (f >= 0x41 && f <= 0x50)
            {
                bool eight = f <= 0x42 || f == 0x45 || f == 0x46 || f == 0x49 || f == 0x4A;

                return (eight ? 8 : 16, 4, 4);
            }

            ReadOnlySpan<byte> ranges =
            [
                0x01, 0x04, 1, 0x05, 0x09, 2, 0x0A, 0x0C, 4, 0x0D, 0x10, 2, 0x11, 0x15, 4, 0x16, 0x18, 8,
                0x19, 0x1C, 3, 0x1D, 0x21, 6, 0x22, 0x24, 12, 0x25, 0x28, 4, 0x29, 0x2D, 8, 0x2E, 0x30, 16,
                0x31, 0x31, 1, 0x32, 0x32, 2, 0x33, 0x35, 4, 0x36, 0x36, 8, 0x37, 0x38, 4, 0x39, 0x3C, 2, 0x3D, 0x40, 4,
                0x51, 0x52, 1, 0x53, 0x54, 2, 0x55, 0x56, 4, 0x57, 0x58, 2, 0x59, 0x5A, 4, 0x5B, 0x5C, 8,
                0x5D, 0x5E, 3, 0x5F, 0x60, 6, 0x61, 0x62, 12, 0x63, 0x64, 4, 0x65, 0x66, 8, 0x67, 0x68, 16,
                0x69, 0x6C, 4, 0x6D, 0x70, 4, 0x71, 0x75, 8, 0x76, 0x78, 16, 0x95, 0x98, 2, 0x99, 0x9C, 4,
            ];

            for (int i = 0; i < ranges.Length; i += 3)
            {
                if (f >= ranges[i] && f <= ranges[i + 1])
                {
                    return (ranges[i + 2], 1, 1);
                }
            }

            return (0, 1, 1);
        }

        /// <summary>Components a GAL format carries (what the texture cache compares views by).</summary>
        public static int Components(Format format)
        {
            string name = format.ToString();

            if (name.StartsWith("Bc4", StringComparison.Ordinal) || name is "S8Uint" or "D16Unorm" or "D32Float" or "X8UintD24Unorm")
            {
                return 1;
            }

            if (name.StartsWith("Bc5", StringComparison.Ordinal) || name is "D24UnormS8Uint" or "S8UintD24Unorm" or "D32FloatS8Uint")
            {
                return 2;
            }

            if (name.StartsWith("Bc6", StringComparison.Ordinal) || name is "R5G6B5Unorm" or "B5G6R5Unorm" or "R11G11B10Float" or "R9G9B9E5Float")
            {
                return 3;
            }

            if (name.StartsWith("Bc", StringComparison.Ordinal) || name.StartsWith("Astc", StringComparison.Ordinal) || name.StartsWith("Etc2", StringComparison.Ordinal))
            {
                return 4;
            }

            int n = 0;

            foreach (char c in name)
            {
                if (c is 'R' or 'G' or 'B' or 'A')
                {
                    n++;
                }

                if (char.IsLower(c) && n > 0 && !char.IsDigit(c))
                {
                    break;
                }
            }

            return Math.Clamp(n, 1, 4);
        }

        public static Target Target(int nvn) => nvn switch
        {
            0 => GAL.Target.Texture1D,
            1 => GAL.Target.Texture2D,
            2 => GAL.Target.Texture3D,
            3 => GAL.Target.Texture1DArray,
            4 => GAL.Target.Texture2DArray,
            5 => GAL.Target.Texture2DMultisample,
            6 => GAL.Target.Texture2DMultisampleArray,
            7 => GAL.Target.Texture2D,           // RECTANGLE
            8 => GAL.Target.Cubemap,
            9 => GAL.Target.CubemapArray,
            10 => GAL.Target.TextureBuffer,
            _ => GAL.Target.Texture2D,
        };

        public static SwizzleComponent Swizzle(int nvn) => nvn switch
        {
            0 => SwizzleComponent.Zero,
            1 => SwizzleComponent.One,
            2 => SwizzleComponent.Red,
            3 => SwizzleComponent.Green,
            4 => SwizzleComponent.Blue,
            5 => SwizzleComponent.Alpha,
            _ => SwizzleComponent.Red,
        };

        // NVN 0x01..0x13 match GAL's non-GL values; the constant factors are 0x61..0x64 in NVN.
        public static BlendFactor BlendFactor(int nvn) => nvn switch
        {
            0x61 => GAL.BlendFactor.ConstantColor,
            0x62 => GAL.BlendFactor.OneMinusConstantColor,
            0x63 => GAL.BlendFactor.ConstantAlpha,
            0x64 => GAL.BlendFactor.OneMinusConstantAlpha,
            _ => (BlendFactor)nvn,
        };

        public static BlendOp BlendOp(int nvn) => (BlendOp)nvn;              // ADD 1 .. MAX 5
        public static CompareOp CompareOp(int nvn) => (CompareOp)nvn;        // NEVER 1 .. ALWAYS 8 (depth, stencil, compare, alpha)
        public static StencilOp StencilOp(int nvn) => (StencilOp)nvn;        // KEEP 1 .. DECR_WRAP 8
        public static PrimitiveTopology Topology(int nvn) => (PrimitiveTopology)nvn;
        public static IndexType IndexType(int nvn) => (IndexType)nvn;
        public static LogicalOp LogicalOp(int nvn) => (LogicalOp)(0x1500 + nvn);
        public static PolygonMode PolygonMode(int nvn) => nvn switch { 0 => GAL.PolygonMode.Point, 1 => GAL.PolygonMode.Line, _ => GAL.PolygonMode.Fill };
        public static FrontFace FrontFace(int nvn) => nvn == 0 ? GAL.FrontFace.Clockwise : GAL.FrontFace.CounterClockwise;
        public static MinFilter MinFilter(int nvn) => (MinFilter)(nvn + 1);
        public static MagFilter MagFilter(int nvn) => (MagFilter)(nvn + 1);

        public static AddressMode AddressMode(int nvn) => nvn switch
        {
            0 => GAL.AddressMode.Clamp,
            1 => GAL.AddressMode.Repeat,
            2 => GAL.AddressMode.MirrorClamp,
            3 => GAL.AddressMode.MirrorClampToEdge,
            4 => GAL.AddressMode.MirrorClampToBorder,
            5 => GAL.AddressMode.ClampToBorder,
            6 => GAL.AddressMode.MirroredRepeat,
            7 => GAL.AddressMode.ClampToEdge,
            _ => GAL.AddressMode.Repeat,
        };

        /// <summary>Culling from an NVN face: (enabled, face).</summary>
        public static (bool Enable, Face Face) Cull(int nvn) => nvn switch
        {
            1 => (true, Face.Front),
            2 => (true, Face.Back),
            3 => (true, Face.FrontAndBack),
            _ => (false, Face.Back),
        };
    }
}
