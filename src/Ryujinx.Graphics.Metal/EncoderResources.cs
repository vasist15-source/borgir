using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    public struct RenderEncoderBindings
    {
        public List<Resource> Resources = [];
        public List<BufferResource> VertexBuffers = [];
        public List<BufferResource> FragmentBuffers = [];

        // Shadow direct binds: textures also bound to spare fragment slots so the driver's
        // automatic hazard tracking covers them fully, independent of useResource. The
        // shader never reads these slots; they exist to make the driver see the reads it
        // is otherwise asked to infer through the argument buffer.
        public List<IntPtr> ShadowTextures = [];
        internal List<ScopedTemporaryBuffer> TemporaryBuffers = [];
        private MTLResource[] _resourceScratch = [];

        public RenderEncoderBindings() { }

        public readonly void Clear()
        {
            DisposeTemporaryBuffers();
            Resources.Clear();
            ShadowTextures.Clear();
            VertexBuffers.Clear();
            FragmentBuffers.Clear();
        }

        public readonly void DisposeTemporaryBuffers()
        {
            foreach (ScopedTemporaryBuffer buffer in TemporaryBuffers)
            {
                buffer.Dispose();
            }

            TemporaryBuffers.Clear();
        }

        public MTLResource[] GetResourceScratch(int minimumLength)
        {
            if (_resourceScratch.Length < minimumLength)
            {
                Array.Resize(ref _resourceScratch, minimumLength);
            }

            return _resourceScratch;
        }
    }

    [SupportedOSPlatform("macos")]
    public struct ComputeEncoderBindings
    {
        public List<Resource> Resources = [];
        public List<BufferResource> Buffers = [];
        internal List<ScopedTemporaryBuffer> TemporaryBuffers = [];
        private MTLResource[] _resourceScratch = [];

        public ComputeEncoderBindings() { }

        public readonly void Clear()
        {
            DisposeTemporaryBuffers();
            Resources.Clear();
            Buffers.Clear();
        }

        public readonly void DisposeTemporaryBuffers()
        {
            foreach (ScopedTemporaryBuffer buffer in TemporaryBuffers)
            {
                buffer.Dispose();
            }

            TemporaryBuffers.Clear();
        }

        public MTLResource[] GetResourceScratch(int minimumLength)
        {
            if (_resourceScratch.Length < minimumLength)
            {
                Array.Resize(ref _resourceScratch, minimumLength);
            }

            return _resourceScratch;
        }
    }

    public struct BufferResource
    {
        public MTLBuffer Buffer;
        public ulong Offset;
        public ulong Binding;

        public BufferResource(MTLBuffer buffer, ulong offset, ulong binding)
        {
            Buffer = buffer;
            Offset = offset;
            Binding = binding;
        }
    }

    public struct Resource
    {
        public MTLResource MtlResource;
        public MTLResourceUsage ResourceUsage;
        public MTLRenderStages Stages;

        public Resource(MTLResource resource, MTLResourceUsage resourceUsage, MTLRenderStages stages)
        {
            MtlResource = resource;
            ResourceUsage = resourceUsage;
            Stages = stages;
        }
    }
}
