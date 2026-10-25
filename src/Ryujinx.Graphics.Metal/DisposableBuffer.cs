using SharpMetal.Metal;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    readonly struct DisposableBuffer : IDisposable
    {
        public MTLBuffer Value { get; }

        /// <summary>
        /// The buffer's GPU address, read once: binding asked Metal for it on every draw, about a tenth of the render
        /// thread's time in Breath of the Wild. A buffer's address never changes.
        /// </summary>
        public ulong GpuAddress { get; }

        public DisposableBuffer(MTLBuffer buffer)
        {
            Value = buffer;
            GpuAddress = buffer != IntPtr.Zero ? buffer.GpuAddress : 0;
        }

        // Marking the buffer volatile here tells the system its contents may be
        // discarded immediately - and Metal's validation layer says it is being done
        // while the resource is still in use: "Cannot set purgeability state to volatile
        // while resource is in use by a command buffer." These buffers include the
        // argument buffers that carry texture resource ids and the constant buffers a
        // draw reads, so a discard lands exactly where this investigation has been
        // measuring "the shader read something that is not in memory": the texture id is
        // recorded correctly and the buffer holding it is gone. MoltenVK never does this,
        // which is why the Vulkan path does not flash.
        //
        // RYUJINX_METAL_PURGE_ON_DISPOSE=1 restores the old behaviour for A/B.
        private static readonly bool _purgeOnDispose =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_PURGE_ON_DISPOSE") == "1";

        public void Dispose()
        {
            if (Value != IntPtr.Zero)
            {
                if (_purgeOnDispose)
                {
                    Value.SetPurgeableState(MTLPurgeableState.Empty);
                }

                if (LiveResources.Enabled)
                {
                    LiveResources.Forget(Value.NativePtr);
                }

                Value.Dispose();
            }
        }
    }
}
