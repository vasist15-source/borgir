using Ryujinx.Graphics.GAL;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    class BufferHolder : IMirrorable<DisposableBuffer>, IDisposable
    {
        private CacheByRange<BufferHolder> _cachedConvertedBuffers;

        public int Size { get; }

        private readonly IntPtr _map;
        private readonly MetalRenderer _renderer;
        private readonly Pipeline _pipeline;

        private readonly MultiFenceHolder _waitable;
        private readonly Auto<DisposableBuffer> _buffer;

        // Mirroring: a write to a range the GPU is still reading is kept here rather
        // than ordered against those reads. On a tile based GPU that ordering costs a
        // blit encoder, and a blit cannot be inside a render pass, so every such write
        // used to split the pass. At bind time the pending data is materialised into a
        // staging reservation and that is bound instead, leaving the real buffer alone.
        private byte[] _pendingData;
        private BufferMirrorRangeList _pendingDataRanges;
        private Dictionary<ulong, StagingBufferReserved> _mirrors;
        private readonly bool _useMirrors = Environment.GetEnvironmentVariable("RYUJINX_METAL_BUFFER_MIRRORS") != "0";
        private static readonly bool _forceDeferred = Environment.GetEnvironmentVariable("RYUJINX_METAL_FORCE_DEFERRED") == "1";

        private readonly ReaderWriterLockSlim _flushLock;
        private FenceHolder _flushFence;
        private int _flushWaiting;

        public BufferHolder(MetalRenderer renderer, Pipeline pipeline, MTLBuffer buffer, int size)
        {
            _renderer = renderer;
            _pipeline = pipeline;
            _map = buffer.Contents;
            _waitable = new MultiFenceHolder(size);
            _buffer = new Auto<DisposableBuffer>(new(buffer), this, _waitable);

            if (LiveResources.Enabled)
            {
                LiveResources.Note(LiveKind.Buffer, buffer.NativePtr, size);
            }

            _flushLock = new ReaderWriterLockSlim();

            Size = size;
        }

        public Auto<DisposableBuffer> GetBuffer()
        {
            return _buffer;
        }

        private static ulong ToMirrorKey(int offset, int size)
        {
            return ((ulong)offset << 32) | (uint)size;
        }

        private static (int Offset, int Size) FromMirrorKey(ulong key)
        {
            return ((int)(key >> 32), (int)key);
        }

        private unsafe bool TryGetMirror(CommandBufferScoped cbs, ref int offset, int size, out Auto<DisposableBuffer> buffer)
        {
            size = Math.Min(size, Size - offset);

            if (!_pendingDataRanges.OverlapsWith(offset, size))
            {
                buffer = null;

                return false;
            }

            ulong key = ToMirrorKey(offset, size);

            if (_mirrors.TryGetValue(key, out StagingBufferReserved reserved))
            {
                buffer = reserved.Buffer.GetBuffer();
                offset = reserved.Offset;

                return true;
            }

            // A range with an in-flight write cannot be served from a mirror, because
            // the write's result is not in the pending data. Upload for real instead.
            if (_waitable.IsBufferRangeInUse(offset, size, true))
            {
                ClearMirrors(cbs, offset, size);

                buffer = null;

                return false;
            }

            Span<byte> baseData = new((void*)(_map + offset), size);
            Span<byte> modData = _pendingData.AsSpan(offset, size);

            StagingBufferReserved? newMirror = _renderer.BufferManager.StagingBuffer.TryReserveData(cbs, size);

            if (newMirror == null)
            {
                // Out of staging space; fall back to uploading the pending data.
                ClearMirrors(cbs, offset, size);

                buffer = null;

                return false;
            }

            StagingBufferReserved mirror = newMirror.Value;

            _pendingDataRanges.FillData(baseData, modData, offset, new Span<byte>((void*)(mirror.Buffer._map + mirror.Offset), size));

            if (_mirrors.Count == 0)
            {
                _pipeline.RegisterActiveMirror(this);
            }

            _mirrors.Add(key, mirror);

            buffer = mirror.Buffer.GetBuffer();
            offset = mirror.Offset;

            return true;
        }

        public Auto<DisposableBuffer> GetMirrorable(CommandBufferScoped cbs, ref int offset, int size, out bool mirrored)
        {
            if (_pendingData != null && TryGetMirror(cbs, ref offset, size, out Auto<DisposableBuffer> result))
            {
                mirrored = true;

                return result;
            }

            mirrored = false;

            return _buffer;
        }

        /// <summary>
        /// Drops every mirror without flushing, for when the command buffer changes and
        /// all staging reservations are released with it.
        /// </summary>
        public void ClearMirrors()
        {
            if (_pendingData != null)
            {
                _mirrors.Clear();
            }
        }

        public void ClearMirrors(CommandBufferScoped cbs, int offset, int size)
        {
            if (_pendingData == null)
            {
                return;
            }

            bool hadMirrors = _mirrors.Count > 0 && RemoveOverlappingMirrors(offset, size);

            if (_pendingDataRanges.Count() != 0)
            {
                UploadPendingData(cbs, offset, size);
            }

            if (hadMirrors)
            {
                _pipeline.RebindBufferRange(_buffer, offset, size);
            }
        }

        private void UploadPendingData(CommandBufferScoped cbs, int offset, int size)
        {
            List<BufferMirrorRangeList.Range> ranges = _pendingDataRanges.FindOverlaps(offset, size);

            if (ranges == null)
            {
                return;
            }

            _pendingDataRanges.Remove(offset, size);

            foreach (BufferMirrorRangeList.Range range in ranges)
            {
                int rangeOffset = Math.Max(offset, range.Offset);
                int rangeSize = Math.Min(offset + size, range.End) - rangeOffset;

                SetData(rangeOffset, _pendingData.AsSpan(rangeOffset, rangeSize), cbs, false);
            }
        }

        /// <summary>
        /// Writes back anything held in the pending ranges for the given region, so the
        /// buffer itself is authoritative again. Used before a readback.
        /// </summary>
        public unsafe void FlushPendingData(int offset, int size)
        {
            if (_pendingData == null || _pendingDataRanges.Count() == 0)
            {
                return;
            }

            List<BufferMirrorRangeList.Range> ranges = _pendingDataRanges.FindOverlaps(offset, size);

            if (ranges == null)
            {
                return;
            }

            _pendingDataRanges.Remove(offset, size);

            foreach (BufferMirrorRangeList.Range range in ranges)
            {
                int rangeOffset = Math.Max(offset, range.Offset);
                int rangeSize = Math.Min(offset + size, range.End) - rangeOffset;

                // The pending data was only ever accepted when nothing had an in-flight
                // write to the range, and the caller is about to wait for the GPU, so
                // writing it straight into the mapping is safe here.
                WaitForFences(rangeOffset, rangeSize);

                _pendingData.AsSpan(rangeOffset, rangeSize)
                    .CopyTo(new Span<byte>((void*)(_map + rangeOffset), rangeSize));
            }

            if (RemoveOverlappingMirrors(offset, size))
            {
                _pipeline.RebindBufferRange(_buffer, offset, size);
            }
        }

        public bool RemoveOverlappingMirrors(int offset, int size)
        {
            List<ulong> toRemove = null;

            foreach (ulong key in _mirrors.Keys)
            {
                (int keyOffset, int keySize) = FromMirrorKey(key);

                if (!(offset + size <= keyOffset || offset >= keyOffset + keySize))
                {
                    toRemove ??= [];

                    toRemove.Add(key);
                }
            }

            if (toRemove == null)
            {
                return false;
            }

            foreach (ulong key in toRemove)
            {
                _mirrors.Remove(key);
            }

            return true;
        }

        public Auto<DisposableBuffer> GetBuffer(bool isWrite)
        {
            if (isWrite)
            {
                SignalWrite(0, Size);
            }

            return _buffer;
        }

        public Auto<DisposableBuffer> GetBuffer(int offset, int size, bool isWrite)
        {
            if (isWrite)
            {
                SignalWrite(offset, size);
            }

            return _buffer;
        }

        public void SignalWrite(int offset, int size)
        {
            if (offset == 0 && size == Size)
            {
                _cachedConvertedBuffers.Clear();
            }
            else
            {
                _cachedConvertedBuffers.ClearRange(offset, size);
            }
        }

        private void ClearFlushFence()
        {
            // Assumes _flushLock is held as writer.

            if (_flushFence != null)
            {
                if (_flushWaiting == 0)
                {
                    _flushFence.Put();
                }

                _flushFence = null;
            }
        }

        private void WaitForFlushFence()
        {
            if (_flushFence == null)
            {
                return;
            }

            // If storage has changed, make sure the fence has been reached so that the data is in place.
            _flushLock.ExitReadLock();
            _flushLock.EnterWriteLock();

            if (_flushFence != null)
            {
                FenceHolder fence = _flushFence;
                Interlocked.Increment(ref _flushWaiting);

                // Don't wait in the lock.

                _flushLock.ExitWriteLock();

                fence.Wait();

                _flushLock.EnterWriteLock();

                if (Interlocked.Decrement(ref _flushWaiting) == 0)
                {
                    fence.Put();
                }

                _flushFence = null;
            }

            // Assumes the _flushLock is held as reader, returns in same state.
            _flushLock.ExitWriteLock();
            _flushLock.EnterReadLock();
        }

        public unsafe PinnedSpan<byte> GetData(int offset, int size)
        {
            // A read has to see writes that are still only in the pending ranges, so put
            // them into the buffer before handing out a view of it. Nothing is left in a
            // mirror that the caller could miss.
            FlushPendingData(offset, size);

            if (DrawRing.Enabled && size <= 512 && _map != IntPtr.Zero && offset + 8 <= Size)
            {
                DrawRing.RecordReadback(offset, size, *(ulong*)((byte*)_map + offset));
            }

            _flushLock.EnterReadLock();

            WaitForFlushFence();

            Span<byte> result;

            if (_map != IntPtr.Zero)
            {
                result = GetDataStorage(offset, size);

                // Need to be careful here, the buffer can't be unmapped while the data is being used.
                _buffer.IncrementReferenceCount();

                _flushLock.ExitReadLock();

                return PinnedSpan<byte>.UnsafeFromSpan(result, _buffer.DecrementReferenceCount);
            }

            throw new InvalidOperationException("The buffer is not mapped");
        }

        public unsafe Span<byte> GetDataStorage(int offset, int size)
        {
            int mappingSize = Math.Min(size, Size - offset);

            if (_map != IntPtr.Zero)
            {
                return new Span<byte>((void*)(_map + offset), mappingSize);
            }

            throw new InvalidOperationException("The buffer is not mapped.");
        }

        private static readonly bool _unsafePreload =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_UNSAFE_PRELOAD") == "1";

        // Diagnostic: which gate sends an upload down the staging path, where it needs
        // a blit encoder and therefore splits whatever render pass is open.
        private static readonly bool _uploadTrace =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_UPLOAD_TRACE") == "1";

        private static readonly int[] _uploadGates = new int[4];

        // Size histogram of the uploads that are forced onto the staging path, since
        // what can replace it depends on how big they are.
        private static readonly int[] _blockedSizes = new int[6];
        private static readonly string[] _blockedSizeNames = ["<=256B", "<=1K", "<=4K", "<=16K", "<=64K", ">64K"];

        public static string TakeUploadGates()
        {
            if (!_uploadTrace)
            {
                return null;
            }

            string[] names = ["direct", "unmapped", "rangeInUse", "preloaded"];
            string text = string.Join(", ", names.Select((n, i) => $"{n}={Interlocked.Exchange(ref _uploadGates[i], 0)}"));
            string sizes = string.Join(" ", _blockedSizeNames.Select((n, i) => $"{n}:{Interlocked.Exchange(ref _blockedSizes[i], 0)}"));

            return $"{text}; blocked sizes {sizes}";
        }

        public unsafe void SetData(int offset, ReadOnlySpan<byte> data, CommandBufferScoped? cbs = null, bool allowCbsWait = true)
        {
            int dataSize = Math.Min(data.Length, Size - offset);
            if (UploadCorrelator.Enabled && dataSize > 0)
            {
                UploadCorrelator.NoteWriteTo(_buffer.GetUnsafe().Value.NativePtr, offset, dataSize, "SD");
            }
            if (dataSize == 0)
            {
                return;
            }

            if (_map == IntPtr.Zero && _uploadTrace)
            {
                Interlocked.Increment(ref _uploadGates[1]);
            }

            if (_map != IntPtr.Zero)
            {
                // If persistently mapped, set the data directly if the buffer is not currently in use.
                bool isRented = _buffer.HasRentedCommandBufferDependency(_renderer.CommandBufferPool);

                // If the buffer is rented, take a little more time and check if the use overlaps this handle.
                // RYUJINX_METAL_FORCE_DEFERRED=1: never write straight into the buffer while
                // a command buffer is in play, whatever the range tracking says. If a white
                // frame is a direct write landing in a range the GPU has not read yet, this
                // arm removes it; if the tracking is right, only the upload path changes.
                bool needsFlush = _forceDeferred
                    ? (cbs != null || isRented)
                    : (isRented && _waitable.IsBufferRangeInUse(offset, dataSize, false));

                if (_uploadTrace)
                {
                    Interlocked.Increment(ref _uploadGates[needsFlush ? 2 : 0]);

                    if (needsFlush)
                    {
                        int bucket = dataSize <= 256 ? 0 : dataSize <= 1024 ? 1 : dataSize <= 4096 ? 2 :
                            dataSize <= 16384 ? 3 : dataSize <= 65536 ? 4 : 5;

                        Interlocked.Increment(ref _blockedSizes[bucket]);
                    }
                }

                if (!needsFlush)
                {
                    WaitForFences(offset, dataSize);

                    data[..dataSize].CopyTo(new Span<byte>((void*)(_map + offset), dataSize));

                    if (_pendingData != null)
                    {
                        // The real buffer now holds newer data than the pending ranges
                        // and any mirror built from them.
                        bool removed = _pendingDataRanges.Remove(offset, dataSize);

                        if (RemoveOverlappingMirrors(offset, dataSize) || removed)
                        {
                            _pipeline.RebindBufferRange(_buffer, offset, dataSize);
                        }
                    }

                    SignalWrite(offset, dataSize);

                    return;
                }
            }

            // The GPU is reading this range, so the write cannot go straight in. As long
            // as nothing has an in-flight *write* to it, keep the data here instead of
            // paying for a staging blit - and the render pass split that blit forces.
            if (_useMirrors && allowCbsWait && cbs != null && !_waitable.IsBufferRangeInUse(offset, dataSize, true))
            {
                if (_pendingData == null)
                {
                    _pendingData = new byte[Size];
                    _mirrors = [];
                }

                data[..dataSize].CopyTo(_pendingData.AsSpan(offset, dataSize));
                _pendingDataRanges.Add(offset, dataSize);

                RemoveOverlappingMirrors(offset, dataSize);

                // Anything bound over this range must be rebound so it picks up a mirror.
                _pipeline.RebindBufferRange(_buffer, offset, dataSize);

                return;
            }

            if (_pendingData != null)
            {
                _pendingDataRanges.Remove(offset, dataSize);
            }

            // Ceiling probe, NOT correct: RYUJINX_METAL_UNSAFE_PRELOAD=1 preloads even
            // when the destination range is already in use by the current command
            // buffer, to measure what removing the staging path's render pass splits
            // would be worth before building a safe way to remove them.
            if (cbs != null &&
                cbs.Value.Encoders.CurrentEncoderType == EncoderType.Render &&
                (_unsafePreload ||
                 !(_buffer.HasCommandBufferDependency(cbs.Value) &&
                   _waitable.IsBufferRangeInUse(cbs.Value.CommandBufferIndex, offset, dataSize))))
            {
                // If the buffer hasn't been used on the command buffer yet, try to preload the data.
                // This avoids ending and beginning render passes on each buffer data upload.

                if (_uploadTrace)
                {
                    Interlocked.Increment(ref _uploadGates[3]);
                }

                cbs = _pipeline.GetPreloadCommandBuffer();
            }

            if (allowCbsWait)
            {
                _renderer.BufferManager.StagingBuffer.PushData(_renderer.CommandBufferPool, cbs, this, offset, data);
            }
            else
            {
                bool rentCbs = cbs == null;
                if (rentCbs)
                {
                    cbs = _renderer.CommandBufferPool.Rent();
                }

                if (!_renderer.BufferManager.StagingBuffer.TryPushData(cbs.Value, this, offset, data))
                {
                    // Need to do a slow upload.
                    BufferHolder srcHolder = _renderer.BufferManager.Create(dataSize);
                    srcHolder.SetDataUnchecked(0, data);

                    Auto<DisposableBuffer> srcBuffer = srcHolder.GetBuffer();
                    Auto<DisposableBuffer> dstBuffer = this.GetBuffer(true);

                    Copy(cbs.Value, srcBuffer, dstBuffer, 0, offset, dataSize);

                    srcHolder.Dispose();
                }

                if (rentCbs)
                {
                    cbs.Value.Dispose();
                }
            }
        }

        public unsafe void SetDataUnchecked(int offset, ReadOnlySpan<byte> data)
        {
            int dataSize = Math.Min(data.Length, Size - offset);
            if (dataSize == 0)
            {
                return;
            }

            if (_map != IntPtr.Zero)
            {
                data[..dataSize].CopyTo(new Span<byte>((void*)(_map + offset), dataSize));
            }
        }

        public void SetDataUnchecked<T>(int offset, ReadOnlySpan<T> data) where T : unmanaged
        {
            SetDataUnchecked(offset, MemoryMarshal.AsBytes(data));
        }

        public static void Copy(
            CommandBufferScoped cbs,
            Auto<DisposableBuffer> src,
            Auto<DisposableBuffer> dst,
            int srcOffset,
            int dstOffset,
            int size,
            bool registerSrcUsage = true)
        {
            MTLBuffer srcBuffer = registerSrcUsage ? src.Get(cbs, srcOffset, size).Value : src.GetUnsafe().Value;
            MTLBuffer dstbuffer = dst.Get(cbs, dstOffset, size, true).Value;

            cbs.Encoders.EnsureBlitEncoder().CopyFromBuffer(
                srcBuffer,
                (ulong)srcOffset,
                dstbuffer,
                (ulong)dstOffset,
                (ulong)size);
        }

        public void WaitForFences()
        {
            _waitable.WaitForFences();
        }

        public void WaitForFences(int offset, int size)
        {
            _waitable.WaitForFences(offset, size);
        }

        private bool BoundToRange(int offset, ref int size)
        {
            if (offset >= Size)
            {
                return false;
            }

            size = Math.Min(Size - offset, size);

            return true;
        }

        public Auto<DisposableBuffer> GetBufferI8ToI16(CommandBufferScoped cbs, int offset, int size)
        {
            if (!BoundToRange(offset, ref size))
            {
                return null;
            }

            I8ToI16CacheKey key = new(_renderer);

            if (!_cachedConvertedBuffers.TryGetValue(offset, size, key, out BufferHolder holder))
            {
                holder = _renderer.BufferManager.Create((size * 2 + 3) & ~3);

                _renderer.HelperShader.ConvertI8ToI16(cbs, this, holder, offset, size);

                key.SetBuffer(holder.GetBuffer());

                _cachedConvertedBuffers.Add(offset, size, key, holder);
            }

            return holder.GetBuffer();
        }

        public Auto<DisposableBuffer> GetBufferTopologyConversion(CommandBufferScoped cbs, int offset, int size, IndexBufferPattern pattern, int indexSize)
        {
            if (!BoundToRange(offset, ref size))
            {
                return null;
            }

            TopologyConversionCacheKey key = new(_renderer, pattern, indexSize);

            if (!_cachedConvertedBuffers.TryGetValue(offset, size, key, out BufferHolder holder))
            {
                // The destination index size is always I32.

                int indexCount = size / indexSize;

                int convertedCount = pattern.GetConvertedCount(indexCount);

                holder = _renderer.BufferManager.Create(convertedCount * 4);

                _renderer.HelperShader.ConvertIndexBuffer(cbs, this, holder, pattern, indexSize, offset, indexCount);

                key.SetBuffer(holder.GetBuffer());

                _cachedConvertedBuffers.Add(offset, size, key, holder);
            }

            return holder.GetBuffer();
        }

        public bool TryGetCachedConvertedBuffer(int offset, int size, ICacheKey key, out BufferHolder holder)
        {
            return _cachedConvertedBuffers.TryGetValue(offset, size, key, out holder);
        }

        public void AddCachedConvertedBuffer(int offset, int size, ICacheKey key, BufferHolder holder)
        {
            _cachedConvertedBuffers.Add(offset, size, key, holder);
        }

        public void AddCachedConvertedBufferDependency(int offset, int size, ICacheKey key, Dependency dependency)
        {
            _cachedConvertedBuffers.AddDependency(offset, size, key, dependency);
        }

        public void RemoveCachedConvertedBuffer(int offset, int size, ICacheKey key)
        {
            _cachedConvertedBuffers.Remove(offset, size, key);
        }


        private static int _offThreadDisposeLogs;

        public void Dispose()
        {
            // Same instrument as TextureBase.NoteOffThreadHandleMutation: a buffer dying
            // off the backend thread while a draw referencing it is being encoded is the
            // shape of the 14:34 drawIndexedPrimitives segfault. Log-only.
            if (!_renderer.CommandBufferPool.OwnedByCurrentThread && _offThreadDisposeLogs++ < 20)
            {
                Ryujinx.Common.Logging.Logger.Warning?.PrintMsg(Ryujinx.Common.Logging.LogClass.Gpu,
                    $"off-thread buffer dispose on '{System.Threading.Thread.CurrentThread.Name}' size={Size}\n{Environment.StackTrace}");
            }

            _pipeline.FlushCommandsIfWeightExceeding(_buffer, (ulong)Size);

            CrashRing.BufferDispose(_buffer.GetUnsafe().Value.NativePtr, Size);
            _buffer.Dispose();
            _cachedConvertedBuffers.Dispose();

            _flushLock.EnterWriteLock();

            ClearFlushFence();

            _flushLock.ExitWriteLock();
        }
    }
}
