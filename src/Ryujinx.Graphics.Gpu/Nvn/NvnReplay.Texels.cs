using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Texture;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Nvn
{
    /// <summary>
    /// nvnTextureWriteTexels / ReadTexels: the CPU copying between linear memory and a texture's own
    /// storage, as the NVN driver does it - in guest memory, through the tracked accessors, so the
    /// texture cache sees writes like any CPU write and flushes GPU-modified data before reads. Runs on
    /// the calling guest thread; touches no replay state.
    /// </summary>
    public sealed unsafe partial class NvnReplay
    {
        public void Texels(in PvTexels q)
        {
            try
            {
                CopyTexels(q);
            }
            catch (Exception e)
            {
                Report($"texels: {e.GetType().Name}: {e.Message}", e);
            }
        }

        private void CopyTexels(in PvTexels q)
        {
            bool write = q.Write != 0;

            if (CopyLinear(q.Tex, q.View, q.HasView != 0, q.Region, q.Data, q.Row, q.Image, write))
            {
                Interlocked.Increment(ref write ? ref _texelWrites : ref _texelReads);
            }
        }

        /// <summary>
        /// Copies a texture region between its storage and linear memory at a guest address (row and
        /// image strides 0 = tightly packed), through guest memory. toTexture: linear to texture.
        /// </summary>
        private bool CopyLinear(in PvTex tex, in PvView view, bool hasView, in PvCopyRegion r, ulong linearVa, long row, long image, bool toTexture)
        {
            TextureInfo info = NvnTextures.GetInfo(tex, view, hasView, out int layerSize, out bool valid);

            if (!valid)
            {
                Report("linear copy: texture format has no GAL format");
                return false;
            }

            int nvnFormat = hasView && view.SetFormat ? view.Format : tex.D.Format;
            (int bpb, int bw, int bh) = NvnGal.BlockOf(nvnFormat);
            bool is3D = info.Target == GAL.Target.Texture3D;

            int x0 = r.X / bw, y0 = r.Y / bh;
            int w = (r.Width + bw - 1) / bw, h = (r.Height + bh - 1) / bh, depth = Math.Max(1, r.Depth);
            int levelWidth = (info.Width + bw - 1) / bw, levelHeight = (info.Height + bh - 1) / bh;

            if (w <= 0 || h <= 0 || x0 < 0 || y0 < 0 || x0 + w > levelWidth || y0 + h > levelHeight)
            {
                Report($"linear copy: region {r.X},{r.Y} {r.Width}x{r.Height} outside a {info.Width}x{info.Height} level");
                return false;
            }

            row = row != 0 ? row : (long)w * bpb;
            image = image != 0 ? image : row * h;

            // A 3D texture is one block-linear volume (slices interleave by gobBlocksInZ); array layers
            // and 2D textures are planes layerSize apart.
            TiledLayout layout = new(levelWidth, levelHeight, info.Stride, info.IsLinear, info.GobBlocksInY, is3D ? info.GobBlocksInZ : 1, bpb);
            int planes = is3D ? 1 : depth;
            int z0 = is3D ? r.Z : 0, zCount = is3D ? depth : 1;

            int rangeOffset = layout.Offset(x0, y0, z0);
            int rangeSize = layout.Offset(x0 + w - 1, y0 + h - 1, z0 + zCount - 1) + bpb - rangeOffset;
            int linearSize = (int)(image * (zCount - 1) + row * (h - 1) + (long)w * bpb);

            for (int plane = 0; plane < planes; plane++)
            {
                ulong gpuVa = info.GpuAddress + (ulong)(is3D ? 0 : (r.Z + plane) * (long)layerSize) + (ulong)rangeOffset;
                ulong cpuVa = _gmm.Translate(gpuVa);

                if (cpuVa == MemoryManager.PteUnmapped)
                {
                    Report("linear copy: texture storage not mapped");
                    return false;
                }

                ulong lineVa = linearVa + (ulong)(plane * image);

                // The region's bytes as they are now (a write keeps whatever lies between its texels).
                byte[] tiled = _physical.GetSpan(cpuVa, rangeSize, tracked: true).ToArray();
                byte[] linear = _physical.GetSpan(lineVa, linearSize, tracked: true).ToArray();

                for (int z = 0; z < zCount; z++)
                {
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int t = layout.Offset(x0 + x, y0 + y, z0 + z) - rangeOffset;
                            int l = (int)(z * image + y * row) + x * bpb;

                            if (toTexture)
                            {
                                linear.AsSpan(l, bpb).CopyTo(tiled.AsSpan(t, bpb));
                            }
                            else
                            {
                                tiled.AsSpan(t, bpb).CopyTo(linear.AsSpan(l, bpb));
                            }
                        }
                    }
                }

                if (toTexture)
                {
                    _physical.Write(cpuVa, tiled);

                    // RYUJINX_NVN_PV_SELFCHECK=1 only: a check decodes the whole level (13 MB for a 5120x5120
                    // map tile), and the map is written in hundreds of small regions.
                    if (SelfCheck && _selfChecks3D < 48 && !info.IsLinear)
                    {
                        _selfChecks3D++;

                        // A 2D plane is checked as a one-slice volume at its own address.
                        TextureInfo checkInfo = is3D ? info : new TextureInfo(
                            info.GpuAddress + (ulong)(is3D ? 0 : (r.Z + plane) * (long)layerSize), info.Width, info.Height, 1, 1, 1, 1, 0, false,
                            info.GobBlocksInY, 1, 1, GAL.Target.Texture2D, info.FormatInfo);

                        SelfCheck3D(checkInfo, nvnFormat, bw, bh, bpb, x0, y0, is3D ? z0 : 0, w, h, zCount, linear, row, image);
                    }
                }
                else
                {
                    _physical.Write(lineVa, linear);
                }
            }

            return true;
        }

        private int _selfChecks3D;
        private static readonly bool SelfCheck = Environment.GetEnvironmentVariable("RYUJINX_NVN_PV_SELFCHECK") == "1";

        /// <summary>
        /// Decodes a 3D texture's first level from guest memory with the texture cache's own decoder and
        /// compares the texels just written: proves (or disproves) that this file's block-linear offsets
        /// agree with what the GPU side will read.
        /// </summary>
        private void SelfCheck3D(TextureInfo info, int nvnFormat, int bw, int bh, int bpb, int x0, int y0, int z0, int w, int h, int d, byte[] linear, long row, long image)
        {
            int depth = info.GetDepth();
            SizeInfo size = SizeCalculator.GetBlockLinearTextureSize(info.Width, info.Height, depth, 1, 1, bw, bh, bpb, info.GobBlocksInY, info.GobBlocksInZ, 1);
            ulong cpuVa = _gmm.Translate(info.GpuAddress);
            ReadOnlySpan<byte> storage = _physical.GetSpan(cpuVa, size.TotalSize);

            using var decoded = LayoutConverter.ConvertBlockLinearToLinear(info.Width, info.Height, depth, depth, 1, 1, bw, bh, bpb, info.GobBlocksInY, info.GobBlocksInZ, 1, size, storage);

            int widthBlocks = (info.Width + bw - 1) / bw, heightBlocks = (info.Height + bh - 1) / bh;
            int stride = (widthBlocks * bpb + LayoutConverter.HostStrideAlignment - 1) & ~(LayoutConverter.HostStrideAlignment - 1);
            ReadOnlySpan<byte> output = decoded.Span;
            int mismatches = 0;
            string first = null;

            for (int z = 0; z < d; z++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        ReadOnlySpan<byte> got = output.Slice(((z0 + z) * heightBlocks + y0 + y) * stride + (x0 + x) * bpb, bpb);
                        ReadOnlySpan<byte> want = linear.AsSpan((int)(z * image + y * row) + x * bpb, bpb);

                        if (!got.SequenceEqual(want))
                        {
                            first ??= $"({x0 + x},{y0 + y},{z0 + z}) got {Convert.ToHexString(got)} want {Convert.ToHexString(want)}";
                            mismatches++;
                        }
                    }
                }
            }

            Logger.Info?.Print(LogClass.Gpu,
                $"nvnpv texel self-check: {info.Width}x{info.Height}x{depth} nvn 0x{nvnFormat:X} {info.FormatInfo.Format} gob {info.GobBlocksInY}x{info.GobBlocksInZ}, " +
                $"region {x0},{y0},{z0} {w}x{h}x{d}: {(mismatches == 0 ? "matches the cache's decoder" : $"{mismatches} texels differ, first {first}")}");
        }

        /// <summary>Byte offsets of blocks in a pitch-linear or block-linear (GOB) level, as BlockLinearLayout computes them.</summary>
        private readonly struct TiledLayout
        {
            private const int GobStride = 64, GobHeight = 8, GobSize = 512;

            private readonly bool _linear;
            private readonly int _stride, _sliceStride, _bpb;
            private readonly int _bhShift, _bdShift, _bhMask, _bdMask, _xShift, _robSize, _sliceSize;

            public TiledLayout(int width, int height, int stride, bool linear, int gobBlocksInY, int gobBlocksInZ, int bpb)
            {
                _linear = linear;
                _stride = stride;
                _sliceStride = stride * height;
                _bpb = bpb;

                _bhShift = System.Numerics.BitOperations.TrailingZeroCount(gobBlocksInY);
                _bdShift = System.Numerics.BitOperations.TrailingZeroCount(gobBlocksInZ);
                _bhMask = gobBlocksInY - 1;
                _bdMask = gobBlocksInZ - 1;
                _xShift = System.Numerics.BitOperations.TrailingZeroCount(GobSize * gobBlocksInY * gobBlocksInZ);

                int widthInGobs = (width * bpb + GobStride - 1) / GobStride;
                _robSize = GobSize * gobBlocksInY * gobBlocksInZ * widthInGobs;
                _sliceSize = (height + gobBlocksInY * GobHeight - 1) / (gobBlocksInY * GobHeight) * _robSize;
            }

            public int Offset(int x, int y, int z)
            {
                if (_linear)
                {
                    return z * _sliceStride + y * _stride + x * _bpb;
                }

                int xb = x * _bpb;
                int yh = y / GobHeight;

                int offset = (z >> _bdShift) * _sliceSize + (yh >> _bhShift) * _robSize;
                offset += (xb / GobStride) << _xShift;
                offset += (yh & _bhMask) * GobSize;
                offset += ((z & _bdMask) * GobSize) << _bhShift;
                offset += ((xb & 0x3f) >> 5) << 8;
                offset += ((y & 0x07) >> 1) << 6;
                offset += ((xb & 0x1f) >> 4) << 5;
                offset += (y & 0x01) << 4;
                offset += xb & 0x0f;

                return offset;
            }
        }

        private long _texelWrites, _texelReads;
    }
}
