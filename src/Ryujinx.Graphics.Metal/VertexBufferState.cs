using Ryujinx.Graphics.GAL;
using SharpMetal.Metal;
using System;
using System.Runtime.Versioning;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    readonly internal struct VertexBufferState
    {
        public static VertexBufferState Null => new(BufferHandle.Null, 0, 0, 0);

        private readonly BufferHandle _handle;
        private readonly int _offset;
        private readonly int _size;

        public readonly int Stride;
        public readonly int Divisor;

        public VertexBufferState(BufferHandle handle, int offset, int size, int divisor, int stride = 0)
        {
            _handle = handle;
            _offset = offset;
            _size = size;

            Stride = stride;
            Divisor = divisor;
        }

        public (MTLBuffer, int) GetVertexBuffer(BufferManager bufferManager, CommandBufferScoped cbs)
        {
            Auto<DisposableBuffer> autoBuffer = null;

            if (_handle != BufferHandle.Null)
            {
                // TODO: Handle restride if necessary

                autoBuffer = bufferManager.GetBuffer(_handle, false, out int size);

                // The original stride must be reapplied in case it was rewritten.
                // TODO: Handle restride if necessary

                if (_offset >= size)
                {
                    autoBuffer = null;
                }
            }

            if (autoBuffer != null)
            {
                int offset = _offset;
                MTLBuffer buffer = autoBuffer.Get(cbs, offset, _size).Value;

                return (buffer, offset);
            }

            return (new MTLBuffer(IntPtr.Zero), 0);
        }

        /// <summary>
        /// Diagnostic: dump the CPU-visible bytes of one vertex record as seen at
        /// encode time (all buffers are storageModeShared). Returns null when this
        /// binding has no buffer or no stride.
        /// </summary>
        public unsafe string DescribeRecordForTrace(BufferManager bufferManager, int recordIndex)
        {
            if (_handle == BufferHandle.Null || Stride <= 0)
            {
                return null;
            }

            Auto<DisposableBuffer> autoBuffer = bufferManager.GetBuffer(_handle, false, out int bufferSize);

            if (autoBuffer == null)
            {
                return "buffer-missing";
            }

            MTLBuffer mtlBuffer = autoBuffer.GetUnsafe().Value;

            if (mtlBuffer.NativePtr == IntPtr.Zero || mtlBuffer.Contents == IntPtr.Zero)
            {
                return "unmapped";
            }

            long byteOffset = _offset + (long)recordIndex * Stride;

            if (byteOffset < 0 || byteOffset + Stride > bufferSize)
            {
                return $"OOB(rec={recordIndex} byteOff={byteOffset} bindOff={_offset} bindSize={_size} bufSize={bufferSize})";
            }

            StringBuilder builder = new();

            if (byteOffset + Stride > _offset + (long)_size)
            {
                builder.Append("BEYOND-BINDING ");
            }

            byte* data = (byte*)mtlBuffer.Contents + byteOffset;
            int dumpBytes = Math.Min(Stride, 32);

            for (int i = 0; i < dumpBytes; i++)
            {
                builder.Append(data[i].ToString("x2"));
            }

            builder.Append(" f32[");

            int floatCount = Math.Min(dumpBytes / 4, 8);

            for (int i = 0; i < floatCount; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(((float*)data)[i].ToString("G6"));
            }

            builder.Append(']');

            return builder.ToString();
        }
    }
}
