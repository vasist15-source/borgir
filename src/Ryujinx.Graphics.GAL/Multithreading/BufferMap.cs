using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Graphics.GAL.Multithreading
{
    /// <summary>
    /// Buffer handles given to the client are not the same as those provided by the backend,
    /// as their handle is created at a later point on the queue.
    /// The handle returned is a unique identifier that will map to the real buffer when it is available.
    /// Note that any uses within the queue should be safe, but outside you must use MapBufferBlocking.
    /// </summary>
    class BufferMap
    {
        private ulong _bufferHandle = 0;

        // Read for every buffer a draw binds while other threads add and remove buffers: a locked dictionary made the
        // render thread wait on its lock (about 7% of its time in Breath of the Wild). Entries are independent, so
        // lock-free reads are enough.
        private readonly ConcurrentDictionary<BufferHandle, BufferHandle> _bufferMap = new();
        private readonly HashSet<BufferHandle> _inFlight = [];
        private readonly AutoResetEvent _inFlightChanged = new(false);

        internal BufferHandle CreateBufferHandle()
        {
            ulong handle64 = Interlocked.Increment(ref _bufferHandle);

            BufferHandle threadedHandle = Unsafe.As<ulong, BufferHandle>(ref handle64);

            lock (_inFlight)
            {
                _inFlight.Add(threadedHandle);
            }

            return threadedHandle;
        }

        internal void AssignBuffer(BufferHandle threadedHandle, BufferHandle realHandle)
        {
            _bufferMap[threadedHandle] = realHandle;

            lock (_inFlight)
            {
                _inFlight.Remove(threadedHandle);
            }

            _inFlightChanged.Set();
        }

        internal void UnassignBuffer(BufferHandle threadedHandle)
        {
            _bufferMap.TryRemove(threadedHandle, out _);
        }

        internal BufferHandle MapBuffer(BufferHandle handle)
        {
            // Maps a threaded buffer to a backend one.
            // Threaded buffers are returned on creation as the buffer
            // isn't actually created until the queue runs the command.

            return _bufferMap.TryGetValue(handle, out BufferHandle result) ? result : BufferHandle.Null;
        }

        internal BufferHandle MapBufferBlocking(BufferHandle handle)
        {
            // Blocks until the handle is available.

            if (_bufferMap.TryGetValue(handle, out BufferHandle result))
            {
                return result;
            }

            bool signal = false;

            while (true)
            {
                lock (_inFlight)
                {
                    if (!_inFlight.Contains(handle))
                    {
                        break;
                    }
                }

                _inFlightChanged.WaitOne();
                signal = true;
            }

            if (signal)
            {
                // Signal other threads which might still be waiting.
                _inFlightChanged.Set();
            }

            return MapBuffer(handle);
        }

        internal BufferRange MapBufferRange(BufferRange range)
        {
            return new BufferRange(MapBuffer(range.Handle), range.Offset, range.Size, range.Write);
        }

        internal Span<BufferRange> MapBufferRanges(Span<BufferRange> ranges)
        {
            // Rewrite the buffer ranges to point to the mapped handles.

            for (int i = 0; i < ranges.Length; i++)
            {
                ref BufferRange range = ref ranges[i];

                if (!_bufferMap.TryGetValue(range.Handle, out BufferHandle result))
                {
                    result = BufferHandle.Null;
                }

                range = new BufferRange(result, range.Offset, range.Size, range.Write);
            }

            return ranges;
        }

        internal Span<BufferAssignment> MapBufferRanges(Span<BufferAssignment> ranges)
        {
            // Rewrite the buffer ranges to point to the mapped handles.

            for (int i = 0; i < ranges.Length; i++)
            {
                ref BufferAssignment assignment = ref ranges[i];
                BufferRange range = assignment.Range;

                if (!_bufferMap.TryGetValue(range.Handle, out BufferHandle result))
                {
                    result = BufferHandle.Null;
                }

                assignment = new BufferAssignment(ranges[i].Binding, new BufferRange(result, range.Offset, range.Size, range.Write));
            }

            return ranges;
        }

        internal Span<VertexBufferDescriptor> MapBufferRanges(Span<VertexBufferDescriptor> ranges)
        {
            // Rewrite the buffer ranges to point to the mapped handles.

            for (int i = 0; i < ranges.Length; i++)
            {
                BufferRange range = ranges[i].Buffer;

                if (!_bufferMap.TryGetValue(range.Handle, out BufferHandle result))
                {
                    result = BufferHandle.Null;
                }

                range = new BufferRange(result, range.Offset, range.Size, range.Write);

                ranges[i] = new VertexBufferDescriptor(range, ranges[i].Stride, ranges[i].Divisor);
            }

            return ranges;
        }
    }
}
