using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Texture;
using System;
using System.Numerics;

namespace Ryujinx.Graphics.Gpu.Nvn
{
    /// <summary>
    /// NVN textures and views as <see cref="TextureInfo"/> for the texture cache. Mirrors what
    /// TexturePool.GetInfo does for Maxwell descriptors: a view's base level moves the address to
    /// that level and shrinks the size and block height with it, so the cache finds the level in an
    /// existing texture; 1D targets become 2D; multisample sizes count samples.
    /// </summary>
    static class NvnTextures
    {
        private const uint FlagLinear = 0x10;
        private const uint FlagLinearRenderTarget = 0x40;

        public static bool IsLinear(in PvTexDesc d) => (d.Flags & (FlagLinear | FlagLinearRenderTarget)) != 0;

        public static TextureInfo GetInfo(in PvTex t, in PvView v, bool hasView, out int layerSize, out bool valid)
        {
            int nvnFormat = hasView && v.SetFormat ? v.Format : t.D.Format;
            int nvnTarget = hasView && v.SetTarget ? v.Target : t.D.Target;

            (Format format, bool alphaOne) = NvnGal.Format(nvnFormat);
            (int bytes, int blockWidth, int blockHeight) = NvnGal.BlockOf(nvnFormat);

            valid = format != NvnGal.Unknown && bytes != 0 && t.Addr != 0;

            FormatInfo formatInfo = valid
                ? new FormatInfo(format, (byte)blockWidth, (byte)blockHeight, (byte)bytes, (byte)NvnGal.Components(format))
                : FormatInfo.Default;

            int samples = Math.Max(1, (int)t.D.Samples);
            int samplesInX = samples switch { 2 => 2, 4 => 2, 8 => 4, 16 => 4, _ => 1 };
            int samplesInY = samples switch { 4 => 2, 8 => 2, 16 => 4, _ => 1 };

            Target target = NvnGal.Target(nvnTarget);
            int width = (int)t.D.Width;
            int height = (int)t.D.Height;
            int depthOrLayers = (int)Math.Max(1, t.D.Depth);
            int levels = (int)Math.Max(1, t.D.Levels);

            switch (nvnTarget)
            {
                case 0: // 1D
                    target = Target.Texture2D;
                    height = 1;
                    depthOrLayers = 1;
                    break;
                case 3: // 1D_ARRAY: the layer count is in height
                    target = Target.Texture2DArray;
                    depthOrLayers = Math.Max(1, height);
                    height = 1;
                    break;
                case 8: // CUBEMAP
                    depthOrLayers = 6;
                    break;
                case 1:
                case 5:
                case 7:
                    depthOrLayers = 1;
                    break;
            }

            if (target is Target.Texture2DMultisample or Target.Texture2DMultisampleArray)
            {
                width *= samplesInX;
                height *= samplesInY;
            }

            bool isLinear = IsLinear(t.D);
            int stride = isLinear ? (int)LinearStride(t.D, bytes, blockWidth) : 0;
            int gobBlocksInY = 1 << t.L.TileH;
            int gobBlocksInZ = 1 << t.L.TileD;
            ulong gpuVa = t.Addr;

            layerSize = 0;

            if (hasView && !isLinear && target != Target.TextureBuffer && (v.SetLevels || v.SetLayers))
            {
                int baseLevel = v.SetLevels ? v.BaseLevel : 0;
                int viewLevels = v.SetLevels ? Math.Max(1, (int)v.NumLevels) : levels - baseLevel;
                int depth = TextureInfo.GetDepth(target, depthOrLayers);
                int layers = TextureInfo.GetLayers(target, depthOrLayers);

                SizeInfo sizeInfo = SizeCalculator.GetBlockLinearTextureSize(
                    width, height, depth, levels, layers, blockWidth, blockHeight, bytes, gobBlocksInY, gobBlocksInZ, 1);

                layerSize = sizeInfo.LayerSize;

                if (v.SetLayers && v.MinLayer != 0 && target != Target.Texture3D)
                {
                    gpuVa += (ulong)sizeInfo.LayerSize * v.MinLayer;
                }

                if (v.SetLayers)
                {
                    depthOrLayers = Math.Max(1, (int)v.NumLayers);

                    if (depthOrLayers == 1 && target is Target.Texture2DArray or Target.Cubemap or Target.CubemapArray && !(hasView && v.SetTarget))
                    {
                        target = Target.Texture2D;
                    }
                }

                if (baseLevel != 0 && baseLevel < levels)
                {
                    gpuVa += (ulong)sizeInfo.GetMipOffset(baseLevel);
                    width = Math.Max(1, width >> baseLevel);
                    height = Math.Max(1, height >> baseLevel);

                    if (target == Target.Texture3D)
                    {
                        depthOrLayers = Math.Max(1, depthOrLayers >> baseLevel);
                    }

                    (gobBlocksInY, gobBlocksInZ) = SizeCalculator.GetMipGobBlockSizes(height, depth, blockHeight, gobBlocksInY, gobBlocksInZ, baseLevel);
                }

                levels = Math.Min(viewLevels, levels - Math.Min(baseLevel, levels - 1));
            }

            levels = ClampLevels(target, width, height, depthOrLayers, levels);

            SwizzleComponent[] sw = new SwizzleComponent[4];

            for (int i = 0; i < 4; i++)
            {
                int texSel = i switch { 0 => t.D.SwizzleR, 1 => t.D.SwizzleG, 2 => t.D.SwizzleB, _ => t.D.SwizzleA };
                int sel = hasView && v.SetSwizzle ? (i switch { 0 => v.SwizzleR, 1 => v.SwizzleG, 2 => v.SwizzleB, _ => v.SwizzleA }) : texSel;
                SwizzleComponent c = NvnGal.Swizzle(sel);

                if (alphaOne && c == SwizzleComponent.Alpha)
                {
                    c = SwizzleComponent.One;
                }

                sw[i] = c;
            }

            DepthStencilMode depthStencilMode = DepthStencilMode.Depth;

            if (format.IsDepthOrStencil)
            {
                // NVN samples the depth plane (the layer refuses stencil-plane modes): (d, d, d, 1).
                sw[0] = sw[1] = sw[2] = SwizzleComponent.Red;
                sw[3] = SwizzleComponent.One;
            }

            return new TextureInfo(
                gpuVa,
                width,
                height,
                depthOrLayers,
                levels,
                samplesInX,
                samplesInY,
                stride,
                isLinear,
                gobBlocksInY,
                gobBlocksInZ,
                1,
                target,
                formatInfo,
                depthStencilMode,
                sw[0],
                sw[1],
                sw[2],
                sw[3]);
        }

        public static uint LinearStride(in PvTexDesc d, int bytes, int blockWidth)
        {
            if (d.Stride != 0)
            {
                return d.Stride;
            }

            uint align = (d.Flags & FlagLinearRenderTarget) != 0 ? 128u : 32u;
            uint row = (uint)((d.Width + blockWidth - 1) / blockWidth * bytes);

            return (row + align - 1) & ~(align - 1);
        }

        private static int ClampLevels(Target target, int width, int height, int depthOrLayers, int levels)
        {
            int maxSize = width;

            if (target is not Target.Texture1D and not Target.Texture1DArray)
            {
                maxSize = Math.Max(maxSize, height);
            }

            if (target == Target.Texture3D)
            {
                maxSize = Math.Max(maxSize, depthOrLayers);
            }

            return Math.Min(levels, BitOperations.Log2((uint)Math.Max(1, maxSize)) + 1);
        }
    }
}
