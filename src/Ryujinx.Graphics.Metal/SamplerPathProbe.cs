using Ryujinx.Common.Logging;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Reads texels through the GPU's sampler path so they can be compared against
    /// the blit engine reading the same texels.
    ///
    /// On Apple GPUs a render target's contents sit behind lossless compression, and
    /// every reader decodes them through the compression metadata. The texture
    /// sampling hardware (what a shader's read()/sample() goes through) and the blit
    /// engine (CopyFromTexture) are separate decoders of the same metadata: they
    /// agree on every texel while the metadata is consistent with the stored bits,
    /// and only a stale or corrupt metadata block can make them disagree. Every
    /// input sample taken so far - UploadCorrelator's centre grids included - went
    /// through the blit engine alone, so a metadata fault would be invisible to all
    /// of it: the blit could faithfully report a picture while the composite's real
    /// fetches, going through the sampler hardware, see something else. This
    /// dispatches one 25-thread threadgroup whose thread i reads texel
    /// (width/2 + i%5, height/2 + i/5) - the same centre grid the blit samples -
    /// with tex.read(), and writes the decoded float4.
    ///
    /// The kernel necessarily outputs decoded floats (read() returns float4) while
    /// the blit path copies raw texel bytes, so the join happens CPU-side: DecodeRaw
    /// unpacks the blit's raw 32-bit texel for the formats the scene chain uses
    /// (RG11B10Float and the 8-bit unorms), and the caller counts a texel equal when
    /// every channel differs by less than 0.01. Agreement exonerates compression
    /// metadata; disagreement convicts it.
    ///
    /// The kernel is compiled from source at runtime and never enters the shader
    /// cache, so no CodeGenVersion bump is due.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class SamplerPathProbe
    {
        private const int GridSide = 5;
        private const int Pixels = GridSide * GridSide;

        /// <summary>
        /// Bytes the kernel writes per texel (one float4); Read needs
        /// Pixels * BytesPerTexel bytes at dstOffsetBytes.
        /// </summary>
        public const int BytesPerTexel = 16;

        private const string KernelSource = """
            #include <metal_stdlib>
            using namespace metal;

            kernel void probeRead(texture2d<float> tex [[texture(0)]],
                                  device float4* dst [[buffer(0)]],
                                  uint tid [[thread_position_in_threadgroup]])
            {
                if (tid >= 25)
                {
                    return;
                }

                uint2 c = uint2(tex.get_width() / 2 + tid % 5, tex.get_height() / 2 + tid / 5);
                dst[tid] = tex.read(c, 0);
            }

            kernel void probeSample(texture2d<float> tex [[texture(0)]],
                                    device float4* dst [[buffer(0)]],
                                    uint tid [[thread_position_in_threadgroup]])
            {
                if (tid >= 25)
                {
                    return;
                }

                // The sampler hardware unit - the path the blit's fragment shader and the
                // window pass actually take - at the same texels, unfiltered.
                constexpr sampler s(coord::pixel, filter::nearest, mip_filter::none);
                float2 c = float2(tex.get_width() / 2 + tid % 5, tex.get_height() / 2 + tid / 5) + 0.5;
                dst[tid] = tex.sample(s, c);
            }
            """;

        private static MTLComputePipelineState _pipeline;

        public static bool Ready { get; private set; }

        public static void Initialize(MTLDevice device)
        {
            using MTLCompileOptions compileOptions = new();

            NSError error = new(IntPtr.Zero);
            MTLLibrary library = device.NewLibrary(StringHelper.NSString(KernelSource), compileOptions, ref error);

            if (error != IntPtr.Zero)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"SamplerPathProbe kernel compilation failed: \n{StringHelper.String(error.LocalizedDescription)}");
                return;
            }

            MTLFunction function = library.NewFunction(StringHelper.NSString("probeRead"));
            MTLFunction sampleFn = library.NewFunction(StringHelper.NSString("probeSample"));

            if (function.NativePtr == IntPtr.Zero)
            {
                Logger.Warning?.Print(LogClass.Gpu, "SamplerPathProbe kernel function missing after compilation");
                return;
            }

            error = new(IntPtr.Zero);
            _pipeline = device.NewComputePipelineState(function, ref error);

            if (error != IntPtr.Zero || _pipeline.NativePtr == IntPtr.Zero)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"SamplerPathProbe pipeline creation failed: {StringHelper.String(error.LocalizedDescription)}");
                return;
            }

            if (sampleFn.NativePtr != IntPtr.Zero)
            {
                error = new(IntPtr.Zero);
                _samplePipeline = device.NewComputePipelineState(sampleFn, ref error);
                SampleReady = error == IntPtr.Zero && _samplePipeline.NativePtr != IntPtr.Zero;
            }

            Ready = true;
        }

        private static MTLComputePipelineState _samplePipeline;

        public static bool SampleReady { get; private set; }

        /// <summary>
        /// Same dispatch through the probeSample kernel - the sampler unit's decode of the
        /// same texels the read-path and blit-path snapshots cover.
        /// </summary>
        public static void ReadSampled(CommandBufferScoped cbs, MTLTexture tex, MTLBuffer dst, int dstOffsetBytes)
        {
            if (!SampleReady || tex.NativePtr == IntPtr.Zero)
            {
                return;
            }

            MTLComputeCommandEncoder encoder = cbs.Encoders.EnsureComputeEncoder();
            encoder.SetComputePipelineState(_samplePipeline);
            encoder.SetTexture(tex, 0);
            encoder.SetBuffer(dst, (ulong)dstOffsetBytes, 0);
            encoder.DispatchThreadgroups(
                new MTLSize { width = 1, height = 1, depth = 1 },
                new MTLSize { width = 25, height = 1, depth = 1 });
            cbs.Encoders.EndCurrentPass();
        }

        /// <summary>
        /// Encodes the sampler-path read of the 25-texel centre grid into dst at
        /// dstOffsetBytes (16-byte aligned; Pixels * BytesPerTexel = 400 bytes are
        /// written). Encode the blit-path CopyFromTexture of the same texels into the
        /// same command buffer so both readers see one ordering of writers.
        /// </summary>
        public static void Read(CommandBufferScoped cbs, MTLTexture tex, ulong texWidth, ulong texHeight, MTLBuffer dst, int dstOffsetBytes)
        {
            if (!Ready || tex.NativePtr == IntPtr.Zero || dst.NativePtr == IntPtr.Zero)
            {
                return;
            }

            // The kernel's furthest tap is (dim/2 + GridSide-1); keep every tap inside
            // the texture or the read is undefined and the comparison meaningless.
            if (texWidth <= 2 * (GridSide - 1) || texHeight <= 2 * (GridSide - 1))
            {
                return;
            }

            MTLComputeCommandEncoder encoder = cbs.Encoders.EnsureComputeEncoder();

            encoder.SetComputePipelineState(_pipeline);
            encoder.SetTexture(tex, 0);
            encoder.SetBuffer(dst, (ulong)dstOffsetBytes, 0);
            encoder.DispatchThreadgroups(
                new MTLSize { width = 1, height = 1, depth = 1 },
                new MTLSize { width = Pixels, height = 1, depth = 1 });

            // Leave no live compute encoder carrying the probe's pipeline.
            // GetOrCreateComputeEncoder only signals compute state dirty when the
            // encoder type changes, and RebindComputeState skips
            // SetComputePipelineState unless the ComputePipeline dirty bit is set -
            // so a game dispatch landing on this same encoder would silently run the
            // probe's kernel. Ending the pass makes the next compute user begin a
            // fresh encoder and rebind everything.
            cbs.Encoders.EndCurrentPass();
        }

        /// <summary>
        /// Decodes one raw 32-bit texel, as the blit engine copies it, into the floats
        /// the sampler path returns for that texel. Only the formats the scene chain
        /// uses are implemented; anything else comes back NaN, which fails every
        /// comparison, so an unhandled format can never masquerade as agreement.
        /// </summary>
        public static (float r, float g, float b) DecodeRaw(uint raw, MTLPixelFormat fmt)
        {
            switch (fmt)
            {
                case MTLPixelFormat.RG11B10Float:
                    // R in bits 0-10 and G in bits 11-21 (5-bit exponent, 6-bit
                    // mantissa), B in bits 22-31 (5-bit exponent, 5-bit mantissa).
                    return (DecodeSmallFloat(raw & 0x7FF, 6),
                            DecodeSmallFloat((raw >> 11) & 0x7FF, 6),
                            DecodeSmallFloat(raw >> 22, 5));
                case MTLPixelFormat.RGBA8Unorm:
                    return ((raw & 0xFF) / 255f,
                            ((raw >> 8) & 0xFF) / 255f,
                            ((raw >> 16) & 0xFF) / 255f);
                case MTLPixelFormat.BGRA8Unorm:
                    return (((raw >> 16) & 0xFF) / 255f,
                            ((raw >> 8) & 0xFF) / 255f,
                            (raw & 0xFF) / 255f);
                // read() on an sRGB view returns linearized values, so the CPU decode
                // must linearize too - otherwise every texel of a healthy texture
                // mismatches and the probe convicts metadata that did nothing wrong.
                case MTLPixelFormat.RGBA8UnormsRGB:
                    return (SrgbToLinear((raw & 0xFF) / 255f),
                            SrgbToLinear(((raw >> 8) & 0xFF) / 255f),
                            SrgbToLinear(((raw >> 16) & 0xFF) / 255f));
                case MTLPixelFormat.BGRA8UnormsRGB:
                    return (SrgbToLinear(((raw >> 16) & 0xFF) / 255f),
                            SrgbToLinear(((raw >> 8) & 0xFF) / 255f),
                            SrgbToLinear((raw & 0xFF) / 255f));
                default:
                    return (float.NaN, float.NaN, float.NaN);
            }
        }

        /// <summary>
        /// Unpacks the unsigned small floats RG11B10Float is built from: a 5-bit
        /// excess-15 exponent over a mantissa of mantissaBits bits, denormal at
        /// exponent 0, infinity/NaN at exponent 31 - the same rules the sampler
        /// hardware applies, so the two paths are compared in the same unit.
        /// </summary>
        private static float DecodeSmallFloat(uint bits, int mantissaBits)
        {
            uint exponent = (bits >> mantissaBits) & 0x1F;
            uint mantissa = bits & ((1u << mantissaBits) - 1);
            float fraction = mantissa / (float)(1 << mantissaBits);

            if (exponent == 0x1F)
            {
                return mantissa == 0 ? float.PositiveInfinity : float.NaN;
            }

            if (exponent == 0)
            {
                // Denormal: fraction * 2^-14. Exact in float32, which every value of
                // these formats is.
                return fraction * (1f / 16384f);
            }

            // (1 + fraction) * 2^(exponent - 15), the power of two built by bit
            // construction so the decode is exact rather than pow()-approximate.
            float scale = BitConverter.Int32BitsToSingle(((int)exponent - 15 + 127) << 23);

            return (1f + fraction) * scale;
        }

        private static float SrgbToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
    }
}
