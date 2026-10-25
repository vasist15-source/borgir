using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    readonly struct ScopedTemporaryBuffer : IDisposable
    {
        private readonly BufferManager _bufferManager;
        private readonly bool _isReserved;

        public readonly BufferRange Range;
        public readonly BufferHolder Holder;

        public BufferHandle Handle => Range.Handle;
        public int Offset => Range.Offset;

        public ScopedTemporaryBuffer(BufferManager bufferManager, BufferHolder holder, BufferHandle handle, int offset, int size, bool isReserved)
        {
            _bufferManager = bufferManager;

            Range = new BufferRange(handle, offset, size);
            Holder = holder;

            _isReserved = isReserved;
        }

        public void Dispose()
        {
            if (!_isReserved)
            {
                // Deferred, not immediate. These are disposed straight after the draw is
                // encoded - Pipeline.DisposeRenderTemporaryBuffers runs on the line after
                // drawPrimitives - and one of them is the argument buffer holding the
                // resource ids the shader dereferences. Deleting it there destroys the
                // MTLBuffer before the GPU has run the draw, so the shader reads freed
                // memory where the texture ids should be and samples whatever now lives
                // there. That is the white frame: every tap returns the same value, the
                // weight sum is zero, and the composite's divide produces a uniform fill
                // while its real input sits untouched and full of picture.
                _bufferManager.DeleteWhenComplete(Range.Handle);
            }
        }
    }

    [SupportedOSPlatform("macos")]
    class BufferManager : IDisposable
    {
        private readonly IdList<BufferHolder> _buffers;

        private readonly MTLDevice _device;
        private readonly MetalRenderer _renderer;
        private readonly Pipeline _pipeline;

        public int BufferCount { get; private set; }

        public StagingBuffer StagingBuffer { get; }

        public BufferManager(MTLDevice device, MetalRenderer renderer, Pipeline pipeline)
        {
            _device = device;
            _renderer = renderer;
            _pipeline = pipeline;
            _buffers = new IdList<BufferHolder>();

            StagingBuffer = new StagingBuffer(_renderer, this);
        }

        public BufferHandle Create(nint pointer, int size)
        {
            // TODO: This is the wrong Metal method, we need no-copy which SharpMetal isn't giving us.
            MTLBuffer buffer = _device.NewBuffer(pointer, (ulong)size, MTLResourceOptions.ResourceStorageModeShared);

            if (buffer == IntPtr.Zero)
            {
                Logger.Error?.PrintMsg(LogClass.Gpu, $"Failed to create buffer with size 0x{size:X}, and pointer 0x{pointer:X}.");

                return BufferHandle.Null;
            }

            BufferHolder holder = new(_renderer, _pipeline, buffer, size);

            BufferCount++;

            ulong handle64 = (uint)_buffers.Add(holder);

            return Unsafe.As<ulong, BufferHandle>(ref handle64);
        }

        public BufferHandle CreateWithHandle(int size)
        {
            return CreateWithHandle(size, out _);
        }

        public BufferHandle CreateWithHandle(int size, out BufferHolder holder)
        {
            holder = Create(size);

            if (holder == null)
            {
                return BufferHandle.Null;
            }

            BufferCount++;

            ulong handle64 = (uint)_buffers.Add(holder);

            return Unsafe.As<ulong, BufferHandle>(ref handle64);
        }

        /// <summary>
        /// Argument buffers can be forced out of the shared staging ring and onto their own
        /// allocation. The composite reads a texture that demonstrably holds a picture and
        /// still produces a uniform fill, which means the taps are not returning that
        /// texture - and what the GPU dereferences is the resource id in the Tier 2
        /// argument buffer, not the binding Ryujinx recorded. If a reused staging range is
        /// handing the shader someone else's ids, this separates the two.
        /// Measured, and it makes the flash **worse**: 34.79% against a 21.6% baseline on
        /// a gated arm, some twenty-five standard errors. Off by default for that reason,
        /// kept because the size of the move is the point - the argument buffer path is
        /// causally connected to the fault, and a fresh allocation per draw is the version
        /// of it that fails most often. The likely reason is residency: the encoder's
        /// useResource list is built around the staging buffer, so a brand-new MTLBuffer
        /// each draw can be dereferenced while not resident, and the shader reads garbage
        /// resource ids - which is exactly the mechanism this was meant to test.
        /// RYUJINX_METAL_ARGBUF_OWN=1 to turn it on.
        /// </summary>
        private static readonly bool _argBufOwnAllocation =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_ARGBUF_OWN") == "1";

        public ScopedTemporaryBuffer ReserveOrCreate(CommandBufferScoped cbs, int size, bool ownAllocation = false)
        {
            if (ownAllocation && _argBufOwnAllocation)
            {
                BufferHandle fresh = CreateWithHandle(size, out BufferHolder freshHolder);

                return new ScopedTemporaryBuffer(this, freshHolder, fresh, 0, size, false);
            }

            StagingBufferReserved? result = StagingBuffer.TryReserveData(cbs, size);

            if (result.HasValue)
            {
                return new ScopedTemporaryBuffer(this, result.Value.Buffer, StagingBuffer.Handle, result.Value.Offset, result.Value.Size, true);
            }
            else
            {
                // Create a temporary buffer.
                BufferHandle handle = CreateWithHandle(size, out BufferHolder holder);

                return new ScopedTemporaryBuffer(this, holder, handle, 0, size, false);
            }
        }

        public BufferHolder Create(int size)
        {
            MTLBuffer buffer = _device.NewBuffer((ulong)size, MTLResourceOptions.ResourceStorageModeShared);

            if (buffer != IntPtr.Zero)
            {
                return new BufferHolder(_renderer, _pipeline, buffer, size);
            }

            Logger.Error?.PrintMsg(LogClass.Gpu, $"Failed to create buffer with size 0x{size:X}.");

            return null;
        }

        public Auto<DisposableBuffer> GetBuffer(BufferHandle handle, bool isWrite, out int size)
        {
            if (TryGetBuffer(handle, out BufferHolder holder))
            {
                size = holder.Size;
                return holder.GetBuffer(isWrite);
            }

            size = 0;
            return null;
        }

        public Auto<DisposableBuffer> GetBuffer(BufferHandle handle, int offset, int size, bool isWrite)
        {
            if (TryGetBuffer(handle, out BufferHolder holder))
            {
                return holder.GetBuffer(offset, size, isWrite);
            }

            return null;
        }

        public Auto<DisposableBuffer> GetBuffer(BufferHandle handle, bool isWrite)
        {
            if (TryGetBuffer(handle, out BufferHolder holder))
            {
                return holder.GetBuffer(isWrite);
            }

            return null;
        }

        public Auto<DisposableBuffer> GetBufferI8ToI16(CommandBufferScoped cbs, BufferHandle handle, int offset, int size)
        {
            if (TryGetBuffer(handle, out BufferHolder holder))
            {
                return holder.GetBufferI8ToI16(cbs, offset, size);
            }

            return null;
        }

        public Auto<DisposableBuffer> GetBufferTopologyConversion(CommandBufferScoped cbs, BufferHandle handle, int offset, int size, IndexBufferPattern pattern, int indexSize)
        {
            if (TryGetBuffer(handle, out BufferHolder holder))
            {
                return holder.GetBufferTopologyConversion(cbs, offset, size, pattern, indexSize);
            }

            return null;
        }

        // Diagnostic: log small GPU->CPU buffer read-backs. TOTK's Depths gloom
        // damage is backend-differential (Metal kills Link on ground Vulkan treats as
        // safe) yet does not read the rendered gloom, so it reads some other
        // GPU-produced coverage back to the CPU. Enable with RYUJINX_METAL_LOG_READBACK
        // and diff the same scene against the Vulkan backend to find which read-back
        // differs.
        private static readonly bool _logReadback =
            System.Environment.GetEnvironmentVariable("RYUJINX_METAL_LOG_READBACK") == "1";

        public PinnedSpan<byte> GetData(BufferHandle handle, int offset, int size)
        {
            if (TryGetBuffer(handle, out BufferHolder holder))
            {
                PinnedSpan<byte> result = holder.GetData(offset, size);

                if (_logReadback && size <= 64)
                {
                    System.Text.StringBuilder builder = new();
                    builder.Append($"readback buf off={offset} size={size}:");

                    ReadOnlySpan<byte> bytes = result.Get();

                    for (int i = 0; i < bytes.Length && i < 64; i++)
                    {
                        builder.Append(bytes[i].ToString("x2"));
                    }

                    Ryujinx.Common.Logging.Logger.Warning?.PrintMsg(Ryujinx.Common.Logging.LogClass.Gpu, builder.ToString());
                }

                return result;
            }

            return new PinnedSpan<byte>();
        }

        public void SetData<T>(BufferHandle handle, int offset, ReadOnlySpan<T> data) where T : unmanaged
        {
            SetData(handle, offset, MemoryMarshal.Cast<T, byte>(data), null);
        }

        public void SetData(BufferHandle handle, int offset, ReadOnlySpan<byte> data, CommandBufferScoped? cbs)
        {
            if (TryGetBuffer(handle, out BufferHolder holder))
            {
                holder.SetData(offset, data, cbs);
            }
        }

        /// <summary>
        /// Buffers whose command buffer has not finished with them yet, each held with the
        /// fence of the command buffer that referenced it. Only the fallback path from
        /// <see cref="ReserveOrCreate"/> lands here: a range reserved inside the staging
        /// buffer is already lifetime-tracked, which is why the fault only appears when the
        /// staging ring is full and the fallback runs.
        /// </summary>
        private readonly Queue<(BufferHandle Handle, FenceHolder Fence)> _pendingDeletes = new();

        public void DeleteWhenComplete(BufferHandle handle)
        {
            DrainPendingDeletes();

            FenceHolder fence = _pipeline?.Cbs.GetFence();

            if (fence == null)
            {
                Delete(handle);
                return;
            }

            fence.Get();
            _pendingDeletes.Enqueue((handle, fence));
        }

        private void DrainPendingDeletes()
        {
            while (_pendingDeletes.TryPeek(out (BufferHandle Handle, FenceHolder Fence) pending) &&
                   pending.Fence.IsSignaled())
            {
                _pendingDeletes.Dequeue();
                Delete(pending.Handle);
                pending.Fence.Put();
            }
        }

        public void Delete(BufferHandle handle)
        {
            if (TryGetBuffer(handle, out BufferHolder holder))
            {
                holder.Dispose();
                _buffers.Remove((int)Unsafe.As<BufferHandle, ulong>(ref handle));
            }
        }

        private bool TryGetBuffer(BufferHandle handle, out BufferHolder holder)
        {
            return _buffers.TryGetValue((int)Unsafe.As<BufferHandle, ulong>(ref handle), out holder);
        }

        public void Dispose()
        {
            StagingBuffer.Dispose();

            foreach (BufferHolder buffer in _buffers)
            {
                buffer.Dispose();
            }
        }
    }
}
