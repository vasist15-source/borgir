using SharpMetal.Metal;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    readonly struct DisposableTexture : IDisposable
    {
        public MTLTexture Value { get; }

        /// <summary>
        /// Default for everything allocated standalone; set only for textures placed in a
        /// <see cref="TextureHeapAllocator"/> heap.
        /// </summary>
        private readonly TextureHeapAllocation _heapAllocation;

        public DisposableTexture(MTLTexture texture) : this(texture, default)
        {
        }

        public DisposableTexture(MTLTexture texture, TextureHeapAllocation heapAllocation)
        {
            Value = texture;
            _heapAllocation = heapAllocation;
        }

        public void Dispose()
        {
            if (Value != IntPtr.Zero)
            {
                // Getting here means Auto<T> dropped the last reference, which it only does
                // once every command buffer that touched this texture has retired - so the
                // GPU is provably finished and the bytes can be handed to someone else.
                //
                // Releasing a heap-placed texture frees the texture object and leaves its
                // storage reserved. MakeAliasable is what marks the range reusable, and it
                // has to happen while we still hold a reference: after Dispose there is no
                // object left to call it on. The free list is only updated afterwards, so
                // no other allocation can be handed this range before Metal has been told
                // it is aliasable.
                if (_heapAllocation.IsValid)
                {
                    Value.MakeAliasable();
                }

                if (LiveResources.Enabled)
                {
                    LiveResources.Forget(Value.NativePtr);
                }

                Value.Dispose();

                if (_heapAllocation.IsValid)
                {
                    TextureHeapAllocator.Free(_heapAllocation);
                }
            }
        }
    }
}
