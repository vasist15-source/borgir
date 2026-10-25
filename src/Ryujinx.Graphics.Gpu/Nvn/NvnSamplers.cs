using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Gpu.Nvn
{
    /// <summary>
    /// GAL samplers for NVN sampler descriptors, shared by every descriptor with the same contents
    /// (the game registers the same few dozen samplers in many pool slots).
    /// </summary>
    sealed class NvnSamplers : IDisposable
    {
        private readonly IRenderer _renderer;
        private readonly Dictionary<PvSamp, ISampler> _samplers = new(SampComparer.Instance);
        private readonly float _maxAnisotropy;

        public NvnSamplers(IRenderer renderer, float forcedAnisotropy)
        {
            _renderer = renderer;
            _maxAnisotropy = forcedAnisotropy;
        }

        public ISampler Get(in PvSamp s)
        {
            if (_samplers.TryGetValue(s, out ISampler sampler))
            {
                return sampler;
            }

            float aniso = Math.Clamp(s.MaxAnisotropy, 1f, 16f);

            // Like the Maxwell path: a forced anisotropy (the user's setting) applies to filtered, mipmapped samplers only.
            if (_maxAnisotropy > 0f && s.MinFilter >= 2 && s.MagFilter == 1)
            {
                aniso = _maxAnisotropy;
            }

            sampler = _renderer.CreateSampler(new SamplerCreateInfo(
                NvnGal.MinFilter(s.MinFilter),
                NvnGal.MagFilter(s.MagFilter),
                true,
                NvnGal.AddressMode(s.WrapS),
                NvnGal.AddressMode(s.WrapT),
                NvnGal.AddressMode(s.WrapR),
                s.CompareMode != 0 ? CompareMode.CompareRToTexture : CompareMode.None,
                NvnGal.CompareOp(s.CompareFunc),
                new ColorF(s.Border0, s.Border1, s.Border2, s.Border3),
                s.LodMin,
                s.LodMax,
                s.LodBias,
                aniso));

            _samplers[s] = sampler;

            return sampler;
        }

        public void Dispose()
        {
            foreach (ISampler sampler in _samplers.Values)
            {
                sampler.Dispose();
            }

            _samplers.Clear();
        }

        private sealed class SampComparer : IEqualityComparer<PvSamp>
        {
            public static readonly SampComparer Instance = new();

            public bool Equals(PvSamp x, PvSamp y) => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref x, 1)).SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref y, 1)));

            public int GetHashCode(PvSamp s)
            {
                HashCode h = new();
                h.AddBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref s, 1)));

                return h.ToHashCode();
            }
        }
    }
}
