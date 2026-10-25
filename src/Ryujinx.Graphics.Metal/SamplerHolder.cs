using Ryujinx.Graphics.GAL;
using SharpMetal.Metal;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    class SamplerHolder : ISampler
    {
        private readonly MetalRenderer _renderer;
        private readonly Auto<DisposableSampler> _sampler;

        public SamplerHolder(MetalRenderer renderer, MTLDevice device, SamplerCreateInfo info)
        {
            _renderer = renderer;

            renderer.Samplers.Add(this);

            (MTLSamplerMinMagFilter minFilter, MTLSamplerMipFilter mipFilter) = info.MinFilter.Convert();

            float minLod = info.MinLod;
            float maxLod = info.MaxLod;

            if (info.MinFilter is MinFilter.Nearest or MinFilter.Linear)
            {
                minLod = 0;
                maxLod = 0.25f;
            }

            MTLSamplerBorderColor borderColor = GetConstrainedBorderColor(info.BorderColor, out _);
            uint maxAnisotropy = Math.Clamp((uint)MathF.Ceiling(info.MaxAnisotropy), 1, 16);

            // The sampler's parameters keyed by the GPU resource id it ends up with, so a
            // draw's bound sampler id can be named later. MTLSamplerState exposes no
            // getters, and sampler ids are recycled - a stale id bound at a draw resolves
            // here to whatever sampler NOW owns it, which is exactly what the GPU samples with.
            string samplerDesc = $"min{(int)minFilter}mag{(int)info.MagFilter.Convert()}mip{(int)mipFilter}_S{(int)info.AddressU.Convert()}T{(int)info.AddressV.Convert()}R{(int)info.AddressP.Convert()}_lod[{minLod:G3},{maxLod:G3}]b{info.MipLodBias:G3}_border{(int)borderColor}_cmp{(int)info.CompareOp.Convert()}_aniso{maxAnisotropy}";

            using MTLSamplerDescriptor descriptor = new()
            {
                BorderColor = borderColor,
                MinFilter = minFilter,
                MagFilter = info.MagFilter.Convert(),
                MipFilter = mipFilter,
                CompareFunction = info.CompareOp.Convert(),
                LodMinClamp = minLod,
                LodMaxClamp = maxLod,
                LodBias = info.MipLodBias,
                LodAverage = false,
                MaxAnisotropy = maxAnisotropy,
                SAddressMode = info.AddressU.Convert(),
                TAddressMode = info.AddressV.Convert(),
                RAddressMode = info.AddressP.Convert(),
                SupportArgumentBuffers = true
            };

            MTLSamplerState sampler = device.NewSamplerState(descriptor);


            UploadCorrelator.NoteSamplerCreated(sampler.GpuResourceID._impl, samplerDesc);

            _sampler = new Auto<DisposableSampler>(new DisposableSampler(sampler));
        }

        private static MTLSamplerBorderColor GetConstrainedBorderColor(ColorF arbitraryBorderColor, out bool cantConstrain)
        {
            float r = arbitraryBorderColor.Red;
            float g = arbitraryBorderColor.Green;
            float b = arbitraryBorderColor.Blue;
            float a = arbitraryBorderColor.Alpha;

            if (r == 0f && g == 0f && b == 0f)
            {
                if (a == 1f)
                {
                    cantConstrain = false;
                    return MTLSamplerBorderColor.OpaqueBlack;
                }

                if (a == 0f)
                {
                    cantConstrain = false;
                    return MTLSamplerBorderColor.TransparentBlack;
                }
            }
            else if (r == 1f && g == 1f && b == 1f && a == 1f)
            {
                cantConstrain = false;
                return MTLSamplerBorderColor.OpaqueWhite;
            }

            cantConstrain = true;
            return MTLSamplerBorderColor.OpaqueBlack;
        }

        public Auto<DisposableSampler> GetSampler()
        {
            return _sampler;
        }

        public void Dispose()
        {
            if (_renderer.Samplers.Remove(this))
            {
                _sampler.Dispose();
            }
        }
    }
}
