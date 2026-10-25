using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.GPFifo
{
    /// <summary>
    /// Uniform buffer contents as they were when the guest submitted the batch that binds them.
    /// </summary>
    /// <remarks>
    /// The guest can start writing its next frame's constants into memory that the batch it has just
    /// submitted still references: on hardware the GPU has consumed that batch by then. This emulator's
    /// GPU thread can still be working through it tens of milliseconds later, so a queued draw reads the
    /// next frame's data - and when a slot now holds another object, a mesh is drawn with the wrong
    /// transform for one frame. The submitting thread decodes the uniform buffer binds of each pushed batch
    /// and copies the bound ranges before it returns to the guest; the draws of that batch bind the copies,
    /// uploaded once, instead of the live memory. Only direct 3D class writes are decoded; ranges touched by
    /// inline constant updates, remapped since submission, or with GPU writes pending keep the normal path.
    /// RYUJINX_UBO_SUBMIT_SNAPSHOT=0 disables it; /tmp/ryujinx-ubo-snapshot holding 0, 1 or 2 switches it at
    /// runtime, where 2 keeps only the submission side running so its cost can be measured on its own.
    /// Large pushes are decoded in chunks on the submitting thread and helper threads, each chunk into its own sub-batch
    /// that the GPU thread uses only once sealed; the push returns once every chunk is done. RYUJINX_UBO_OPT=0, or
    /// /tmp/ryujinx-ubo-opt holding 0, decodes every push on the submitting thread instead (128 switches back). Every
    /// push of entries is timed for the status line.
    /// </remarks>
    static class UniformSubmitSnapshot
    {
        public const int SizeMethod = 0x8e0;
        public const int AddressHighMethod = 0x8e1;
        public const int AddressLowMethod = 0x8e2;
        public const int OffsetMethod = 0x8e3;
        public const int UpdateDataFirstMethod = 0x8e4;
        public const int UpdateDataLastMethod = 0x8f3;
        public const int BindVertexMethod = 0x904;
        public const int BindFragmentMethod = 0x924;
        public const int BindStageStride = 8;
        public const int MaxSnapshotSize = 16 * 1024;
        private const int MacroMethodStart = 0xe00;
        private const int MaxArenaSize = 64 * 1024 * 1024;
        private const string ToggleFile = "/tmp/ryujinx-ubo-snapshot";
        private const string OptionsFile = "/tmp/ryujinx-ubo-opt";
        private const int StatusIntervalSeconds = 5;
        private const int BigBatchEntries = 1024;

        /// <summary>
        /// Submission option, on by default: a push of at least <see cref="ParallelMinEntries"/> entries is decoded in chunks
        /// on several threads, each chunk into its own sub-batch. Every batch rented with it is used by the GPU thread only
        /// once sealed, so filling it takes no locks. The push returns once every chunk is done.
        /// </summary>
        public const int OptParallel = 0x80;
        public const int ParallelMinEntries = 1024;
        public const int ParallelMaxChunks = 4;

        /// <summary>Nothing is decoded, copied or bound.</summary>
        public const int ModeOff = 0;
        /// <summary>Ranges are copied at submission and draws bind the copies.</summary>
        public const int ModeFull = 1;
        /// <summary>Ranges are copied at submission but the GPU thread ignores them; for cost measurement.</summary>
        public const int ModeSubmitOnly = 2;

        /// <summary>Guest bytes of a GPU virtual range and the physical address they live at; empty when unreadable.</summary>
        public delegate ReadOnlySpan<byte> RangeReader(ulong gpuVa, int size, out ulong physical);

        public interface IDecodeSink
        {
            void OnBind(ulong gpuVa, int size);
            void OnInlineUpdate(ulong gpuVa, ulong size);
        }

        public interface IStagingTarget
        {
            StagingPool Pool { get; }
            BufferHandle Create(int size);
            void Write(BufferHandle handle, int offset, ReadOnlySpan<byte> data);
            void Delete(BufferHandle handle);
        }

        private static volatile int _mode = ParseMode(Environment.GetEnvironmentVariable("RYUJINX_UBO_SUBMIT_SNAPSHOT")) ?? ModeFull;
        private static volatile int _options = ParseOptions(Environment.GetEnvironmentVariable("RYUJINX_UBO_OPT")) ?? OptParallel;
        private static int _modeEpoch;
        private static int _gpuWrites;

        private static long _nextBatchId;
        private static long _lastPollTicks, _lastStatsTicks;
        private static readonly ConcurrentQueue<Batch> _pool = new();

        // Submitting threads; only rare events are counted there.
        private static long _batches, _declined, _inlineInvalidated;

        // GPU thread only; read unsynchronised for the status line.
        private static long _bindsMatched, _captureNoBatch, _captureAbsent, _captureInvalid, _captureStale;
        private static long _execInvalidated, _invalidateNoBatch;
        private static long _stagedBinds, _bindInvalid, _physicalMoved, _stagingFull, _uploads, _uploadBytes;
        private static long _skippedGpuModified, _forcedRebinds, _captureUnsealed;

        // Every push of entries, in all modes; guarded by _pushLock.
        private static readonly object _pushLock = new();
        private static long _pushBatches, _pushEntries, _pushWords, _pushTicks;
        private static long _parallelPushes, _parallelChunks, _parallelHelperChunks, _parallelWaitTicks;
        private static readonly long[] _bigTicks = new long[4096];
        private static readonly int[] _bigEntries = new int[4096];
        private static int _bigCount;

        [ThreadStatic] private static Batch _current;
        [ThreadStatic] private static long _currentId;

        public static bool Enabled => _mode != ModeOff;

        public static int Mode => _mode;

        public static int Options => _options;

        /// <summary>Changes whenever any buffer range is marked as written by the GPU.</summary>
        public static int GpuWrites => Volatile.Read(ref _gpuWrites);

        public static long NextBatchId() => Interlocked.Increment(ref _nextBatchId);

        /// <summary>
        /// Command decoder that mirrors <see cref="GPFifoProcessor"/> packet handling, tracking only the
        /// uniform buffer state of the 3D class. One per processor; state carries across entries.
        /// </summary>
        public sealed class Decoder
        {
            // Methods whose writes the decoder acts on: uniform buffer state and data, uniform binds, macros.
            private static readonly bool[] _methodMatters = CreateMethodTable();

            private int _method;
            private int _subChannel;
            private int _methodCount;
            private bool _nonIncrementing;
            private bool _incrementOnce;
            private int _skipWords;
            private ulong _pendingInline;

            private int _size;
            private uint _addressHigh;
            private uint _addressLow;
            private int _offset;
            private bool _sizeKnown;
            private bool _highKnown;
            private bool _lowKnown;
            private bool _offsetKnown;

            private static bool[] CreateMethodTable()
            {
                bool[] table = new bool[0x1000];

                for (int method = SizeMethod; method <= UpdateDataLastMethod; method++)
                {
                    table[method] = true;
                }

                for (int method = BindVertexMethod; method <= BindFragmentMethod; method += BindStageStride)
                {
                    table[method] = true;
                }

                for (int method = MacroMethodStart; method < table.Length; method++)
                {
                    table[method] = true;
                }

                return table;
            }

            /// <summary>Forget packet and register state, when words could not be decoded.</summary>
            public void Reset()
            {
                _methodCount = 0;
                _skipWords = 0;
                _pendingInline = 0;
                _sizeKnown = _highKnown = _lowKnown = _offsetKnown = false;
            }

            public void Decode(ReadOnlySpan<int> words, IDecodeSink sink)
            {
                // The packet state is kept in locals for the loop; Send only touches the register state.
                int method = _method;
                int methodCount = _methodCount;
                int skipWords = _skipWords;
                int subChannel = _subChannel;
                bool nonIncrementing = _nonIncrementing;
                bool incrementOnce = _incrementOnce;

                for (int index = 0; index < words.Length; index++)
                {
                    if (skipWords != 0)
                    {
                        // Data words of a packet that cannot touch uniform buffer state.
                        int skipped = Math.Min(skipWords, words.Length - index);
                        skipWords -= skipped;
                        index += skipped - 1;
                        continue;
                    }

                    if (methodCount != 0)
                    {
                        Send(method, words[index], subChannel, sink);

                        if (!nonIncrementing)
                        {
                            method++;
                        }

                        if (incrementOnce)
                        {
                            nonIncrementing = true;
                        }

                        if (--methodCount == 0)
                        {
                            FlushInline(sink);
                        }

                        continue;
                    }

                    uint header = (uint)words[index];
                    int address = (int)(header & 0xFFF);
                    int packetSubChannel = (int)((header >> 13) & 0x7);
                    int count = (int)((header >> 16) & 0x1FFF);
                    int secOp = (int)((header >> 29) & 0x7);

                    if (secOp == 4)
                    {
                        // ImmdDataMethod, the most common packet: most write methods nothing here depends on.
                        if (packetSubChannel == 0 && _methodMatters[address])
                        {
                            Send(address, count, packetSubChannel, sink);
                            FlushInline(sink);
                        }

                        continue;
                    }

                    // GPFifoProcessor.TryFastUniformBufferUpdate consumes the data words in one call.
                    if (address == UpdateDataFirstMethod && secOp == 3 && count < words.Length - index)
                    {
                        InlineUpdate((ulong)count * 4, sink);
                        index += count;
                        continue;
                    }

                    if ((secOp == 1 || secOp == 3 || secOp == 5) && count != 0)
                    {
                        int last = address + (secOp == 1 ? count : secOp == 5 ? Math.Min(count, 2) : 1) - 1;

                        if (packetSubChannel != 0 || address > BindFragmentMethod || last < SizeMethod)
                        {
                            // The packet cannot reach uniform state. If it reaches the macro range, every such word
                            // only forgets that state: forget it once and skip the words like the others.
                            if (packetSubChannel == 0 && last >= MacroMethodStart)
                            {
                                ForgetState(sink);
                            }

                            skipWords = count;
                            continue;
                        }

                        method = address;
                        subChannel = packetSubChannel;
                        methodCount = count;
                        incrementOnce = secOp == 5;
                        nonIncrementing = secOp == 3;
                    }
                }

                _method = method;
                _methodCount = methodCount;
                _skipWords = skipWords;
                _subChannel = subChannel;
                _nonIncrementing = nonIncrementing;
                _incrementOnce = incrementOnce;

                // A packet may continue in the next command buffer; flush so updates keep their order across entries.
                FlushInline(sink);
            }

            private void Send(int method, int argument, int subChannel, IDecodeSink sink)
            {
                if (subChannel != 0)
                {
                    return;
                }

                if (method >= UpdateDataFirstMethod && method <= UpdateDataLastMethod)
                {
                    // Consecutive data words become one update, flushed before anything else is handled.
                    _pendingInline += 4;
                    return;
                }

                FlushInline(sink);

                switch (method)
                {
                    case SizeMethod:
                        _size = argument;
                        _sizeKnown = true;
                        return;
                    case AddressHighMethod:
                        _addressHigh = (uint)argument;
                        _highKnown = true;
                        return;
                    case AddressLowMethod:
                        _addressLow = (uint)argument;
                        _lowKnown = true;
                        return;
                    case OffsetMethod:
                        _offset = argument;
                        _offsetKnown = true;
                        return;
                }

                if (method >= BindVertexMethod && method <= BindFragmentMethod && (method - BindVertexMethod) % BindStageStride == 0)
                {
                    if ((argument & 1) != 0 && _sizeKnown && _highKnown && _lowKnown && _size > 0 && _size <= MaxSnapshotSize)
                    {
                        sink.OnBind(((ulong)_addressHigh << 32) | _addressLow, _size);
                    }
                }
                else if (method >= MacroMethodStart)
                {
                    // A macro may rewrite the uniform buffer state with values this decoder cannot see.
                    ForgetState(sink);
                }
            }

            private void ForgetState(IDecodeSink sink)
            {
                FlushInline(sink);
                _sizeKnown = _highKnown = _lowKnown = _offsetKnown = false;
            }

            private void FlushInline(IDecodeSink sink)
            {
                if (_pendingInline != 0)
                {
                    ulong size = _pendingInline;
                    _pendingInline = 0;
                    InlineUpdate(size, sink);
                }
            }

            private void InlineUpdate(ulong size, IDecodeSink sink)
            {
                if (_highKnown && _lowKnown && _offsetKnown)
                {
                    sink.OnInlineUpdate((((ulong)_addressHigh << 32) | _addressLow) + (uint)_offset, size);
                    _offset += (int)size;
                }
                else
                {
                    sink.OnInlineUpdate(0, ulong.MaxValue);
                    _offsetKnown = false;
                }
            }
        }

        /// <summary>
        /// Snapshots taken for one submitted batch. Filled by the submitting thread while the GPU thread may
        /// already execute the batch's first entries, so the GPU thread locks until the batch is sealed; after
        /// that only the GPU thread touches it.
        /// </summary>
        public sealed class Batch : IDecodeSink
        {
            private struct Entry
            {
                public ulong GpuVa;
                public ulong Physical;
                public int Size;
                public int Offset;
                public int Region;
                public bool Invalid;
            }

            /// <summary>A run of consecutive copies uploaded in one write.</summary>
            private struct Region
            {
                public BufferHandle Handle;
                public int BufferOffset;
                public int ArenaStart;
            }

            /// <summary>Node of a per-page list of entries, kept in an array the next batch reuses.</summary>
            private struct PageLink
            {
                public int Entry;
                public int Next;
            }

            /// <summary>Node of a per-page list of inline-updated ranges, kept in an array the next batch reuses.</summary>
            private struct InlineRange
            {
                public ulong Start;
                public ulong End;
                public int Next;
            }

            private readonly struct RangeKey : IEquatable<RangeKey>
            {
                public readonly ulong GpuVa;
                public readonly int Size;

                public RangeKey(ulong gpuVa, int size)
                {
                    GpuVa = gpuVa;
                    Size = size;
                }

                public bool Equals(RangeKey other) => GpuVa == other.GpuVa && Size == other.Size;

                public override bool Equals(object obj) => obj is RangeKey other && Equals(other);

                // Addresses are below 2^48 and sizes below 2^16: fold the size into the free top bits, then mix.
                public override int GetHashCode() => (int)(((GpuVa ^ ((ulong)(uint)Size << 48)) * 0x9E3779B97F4A7C15UL) >> 32);
            }

            private readonly object _lock = new();
            private readonly Dictionary<RangeKey, int> _index = new();
            private readonly List<Entry> _entries = new();
            private readonly List<Region> _regions = new();
            private readonly Dictionary<ulong, int> _entryPages = new();
            private readonly Dictionary<ulong, int> _inlinePages = new();
            private PageLink[] _pageLinks = new PageLink[1024];
            private InlineRange[] _inlineRanges = new InlineRange[256];
            private int _pageLinkCount;
            private int _inlineRangeCount;
            private byte[] _arena = new byte[64 * 1024];
            private int _used;
            private int _uploadedEntries;
            private int _generation;
            private volatile bool _sealed;
            private bool _releaseRequested;
            private RangeReader _reader;

            // GPU thread only: inline writes executed while a sealed-only batch was still being filled.
            private readonly List<(ulong Va, ulong Size)> _gpuPendingInline = new();

            public long Id { get; private set; }

            /// <summary>Mode epoch the batch was rented in; any later mode change retires it.</summary>
            internal int Epoch { get; private set; }

            /// <summary>Submission options in effect when the batch was rented.</summary>
            public int Flags { get; private set; }

            /// <summary>The GPU thread uses the batch only once it is sealed, so filling it takes no locks.</summary>
            public bool SealedOnly => (Flags & OptParallel) != 0;

            /// <summary>Every sub-batch of a parallel push, this one included; null for a single batch.</summary>
            public Batch[] Siblings { get; private set; }

            internal RangeReader Reader => _reader;

            /// <summary>
            /// Changes whenever a copy of the batch, or of another sub-batch of the same parallel push, is invalidated;
            /// bindings made before must be redone.
            /// </summary>
            public int Generation
            {
                get
                {
                    Batch[] siblings = Siblings;

                    if (siblings == null)
                    {
                        return Volatile.Read(ref _generation);
                    }

                    int sum = 0;

                    foreach (Batch sibling in siblings)
                    {
                        sum += Volatile.Read(ref sibling._generation);
                    }

                    return sum;
                }
            }

            public int Count
            {
                get
                {
                    lock (_lock)
                    {
                        return _entries.Count;
                    }
                }
            }

            /// <summary>Inline-updated ranges recorded for later copies, after merging neighbours.</summary>
            internal int InlineRangeCount
            {
                get
                {
                    lock (_lock)
                    {
                        return _inlineRangeCount;
                    }
                }
            }

            /// <summary>Uploaded runs of copies.</summary>
            internal int RegionCount
            {
                get
                {
                    lock (_lock)
                    {
                        return _regions.Count;
                    }
                }
            }

            internal void Begin(long id, RangeReader reader, int epoch, int flags)
            {
                lock (_lock)
                {
                    Id = id;
                    Epoch = epoch;
                    Flags = flags;
                    Siblings = null;
                    _reader = reader;
                    _used = 0;
                    _uploadedEntries = 0;
                    _sealed = false;
                    _releaseRequested = false;
                    _index.Clear();
                    _entries.Clear();
                    _regions.Clear();
                    _entryPages.Clear();
                    _inlinePages.Clear();
                    _gpuPendingInline.Clear();
                    _pageLinkCount = 0;
                    _inlineRangeCount = 0;
                }
            }

            /// <summary>Submitting thread, after the batch's last entry was pushed.</summary>
            public void Seal()
            {
                bool release;

                lock (_lock)
                {
                    _sealed = true;
                    release = _releaseRequested;
                }

                // The GPU thread finished executing the batch before it was sealed and left it to be returned here.
                if (release)
                {
                    _pool.Enqueue(this);
                }
            }

            /// <summary>GPU thread: true when the batch may return to the pool now; otherwise sealing it returns it.</summary>
            internal bool RequestRelease()
            {
                lock (_lock)
                {
                    if (_sealed)
                    {
                        return true;
                    }

                    _releaseRequested = true;

                    return false;
                }
            }

            internal void SetSiblings(Batch[] siblings) => Siblings = siblings;

            /// <summary>
            /// Submitting thread, parallel push before sealing: marks the copies overlapping inline updates that another
            /// sub-batch decoded, as a single batch would have.
            /// </summary>
            internal void MarkInlineFrom(Batch other)
            {
                for (int index = 0; index < other._inlineRangeCount; index++)
                {
                    ref InlineRange range = ref other._inlineRanges[index];
                    Interlocked.Add(ref _inlineInvalidated, MarkInvalid(range.Start, range.End - range.Start));
                }
            }

            void IDecodeSink.OnBind(ulong gpuVa, int size)
            {
                // Only the submitting thread adds to a batch and the GPU thread only reads the table, so this
                // thread can look up its own table without the lock and skip ranges it has copied already.
                if (_index.ContainsKey(new RangeKey(gpuVa, size)))
                {
                    return;
                }

                ulong physical = 0;
                ReadOnlySpan<byte> data = _reader != null ? _reader(gpuVa, size, out physical) : default;

                if (data.Length != size)
                {
                    Interlocked.Increment(ref _declined);
                    return;
                }

                TryAdd(gpuVa, physical, data);
            }

            void IDecodeSink.OnInlineUpdate(ulong gpuVa, ulong size)
            {
                // An unknown target was set by a macro or an undecoded entry. The GPU thread invalidates the real
                // range when it flushes the update, before any later draw of the batch commits.
                if (size == ulong.MaxValue)
                {
                    return;
                }

                if (SealedOnly)
                {
                    // Nothing reads a sealed-only batch before it is sealed, and only this thread fills it.
                    RecordInlineLocked(gpuVa, size);
                    Interlocked.Add(ref _inlineInvalidated, MarkInvalid(gpuVa, size));
                    return;
                }

                lock (_lock)
                {
                    RecordInlineLocked(gpuVa, size);
                    Interlocked.Add(ref _inlineInvalidated, MarkInvalid(gpuVa, size));
                }
            }

            public bool TryAdd(ulong gpuVa, ulong physical, ReadOnlySpan<byte> data)
            {
                if (SealedOnly)
                {
                    // Nothing reads a sealed-only batch before it is sealed, and only this thread fills it.
                    return TryAddUnlocked(gpuVa, physical, data);
                }

                lock (_lock)
                {
                    return TryAddUnlocked(gpuVa, physical, data);
                }
            }

            private bool TryAddUnlocked(ulong gpuVa, ulong physical, ReadOnlySpan<byte> data)
            {
                {
                    if (_sealed)
                    {
                        return false;
                    }

                    RangeKey key = new(gpuVa, data.Length);
                    ref int slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_index, key, out bool exists);

                    if (exists)
                    {
                        return false;
                    }

                    if (_used + data.Length > _arena.Length)
                    {
                        int size = Math.Min(MaxArenaSize, Math.Max(_arena.Length * 2, _used + data.Length));

                        if (_used + data.Length > size)
                        {
                            _index.Remove(key);
                            return false;
                        }

                        Array.Resize(ref _arena, size);
                    }

                    int entryIndex = _entries.Count;
                    slot = entryIndex;

                    data.CopyTo(_arena.AsSpan(_used));
                    _entries.Add(new Entry { GpuVa = gpuVa, Physical = physical, Size = data.Length, Offset = _used, Region = -1, Invalid = OverlapsInline(gpuVa, (ulong)data.Length) });
                    _used += data.Length;

                    for (ulong page = gpuVa & ~0xFFFUL; page < gpuVa + (ulong)data.Length; page += 0x1000)
                    {
                        ref int head = ref CollectionsMarshal.GetValueRefOrAddDefault(_entryPages, page, out bool known);

                        if (_pageLinkCount == _pageLinks.Length)
                        {
                            Array.Resize(ref _pageLinks, _pageLinks.Length * 2);
                        }

                        _pageLinks[_pageLinkCount] = new PageLink { Entry = entryIndex, Next = known ? head : -1 };
                        head = _pageLinkCount++;
                    }

                    return true;
                }
            }

            /// <summary>GPU thread: an inline update wrote guest memory the batch may have copied.</summary>
            public void Invalidate(ulong gpuVa, ulong size)
            {
                if (_sealed)
                {
                    // Only this thread uses a sealed batch and nothing can be added to it any more: mark, record nothing.
                    ApplyPendingInline();
                    _execInvalidated += MarkInvalid(gpuVa, size);
                    return;
                }

                if (SealedOnly)
                {
                    // Copies may still be added: remember the write and mark them when the batch is first used sealed.
                    _gpuPendingInline.Add((gpuVa, size));
                    return;
                }

                lock (_lock)
                {
                    // Also covers a copy the submitting thread adds later from bytes it read before this write.
                    RecordInlineLocked(gpuVa, size);
                    _execInvalidated += MarkInvalid(gpuVa, size);
                }
            }

            /// <summary>
            /// The entry index; -1 when the range was not copied, -2 when its copy was invalidated, -3 when the batch is
            /// sealed-only and not sealed yet.
            /// </summary>
            public int Find(ulong gpuVa, int size)
            {
                if (_sealed)
                {
                    ApplyPendingInline();
                    return FindUnlocked(gpuVa, size);
                }

                if (SealedOnly)
                {
                    return -3;
                }

                lock (_lock)
                {
                    return FindUnlocked(gpuVa, size);
                }
            }

            /// <summary>
            /// GPU thread: the host range holding the entry's copy, uploading every copy not on the host yet, or a
            /// null handle when the copy cannot be used. <paramref name="generation"/> is read before the entry is
            /// looked at, so an invalidation racing this call changes the generation the caller keeps.
            /// </summary>
            public BufferRange Bind(int entryIndex, ulong physical, IStagingTarget target, out int generation, out int uploadedBytes)
            {
                generation = Generation;

                if (_sealed)
                {
                    return BindUnlocked(entryIndex, physical, target, out uploadedBytes);
                }

                lock (_lock)
                {
                    return BindUnlocked(entryIndex, physical, target, out uploadedBytes);
                }
            }

            /// <summary>GPU thread, sealed batch: marks the copies overlapping inline writes executed before it was sealed.</summary>
            private void ApplyPendingInline()
            {
                if (_gpuPendingInline.Count == 0)
                {
                    return;
                }

                foreach ((ulong va, ulong size) in _gpuPendingInline)
                {
                    _execInvalidated += MarkInvalid(va, size);
                }

                _gpuPendingInline.Clear();
            }

            private int FindUnlocked(ulong gpuVa, int size)
            {
                if (!_index.TryGetValue(new RangeKey(gpuVa, size), out int entryIndex))
                {
                    return -1;
                }

                return CollectionsMarshal.AsSpan(_entries)[entryIndex].Invalid ? -2 : entryIndex;
            }

            private BufferRange BindUnlocked(int entryIndex, ulong physical, IStagingTarget target, out int uploadedBytes)
            {
                uploadedBytes = 0;

                if ((uint)entryIndex >= (uint)_entries.Count)
                {
                    return default;
                }

                ref Entry entry = ref CollectionsMarshal.AsSpan(_entries)[entryIndex];

                if (entry.Invalid)
                {
                    _bindInvalid++;
                    return default;
                }

                if (entry.Physical != physical)
                {
                    _physicalMoved++;
                    return default;
                }

                if (entry.Region < 0)
                {
                    uploadedBytes = UploadPending(target);

                    if (entry.Region < 0)
                    {
                        _stagingFull++;
                        return default;
                    }
                }

                Region region = CollectionsMarshal.AsSpan(_regions)[entry.Region];

                return new BufferRange(region.Handle, region.BufferOffset + (entry.Offset - region.ArenaStart), entry.Size);
            }

            /// <summary>Uploads every copy made so far that is not on the host yet, in as few writes as the chunks allow.</summary>
            private int UploadPending(IStagingTarget target)
            {
                Span<Entry> entries = CollectionsMarshal.AsSpan(_entries);
                StagingPool pool = target.Pool;
                int uploaded = 0;

                while (_uploadedEntries < entries.Length)
                {
                    int first = _uploadedEntries;
                    int start = entries[first].Offset;

                    if (!pool.TryReserve(target, entries[first].Size, _used - start, Id, out BufferHandle handle, out int bufferOffset, out int granted))
                    {
                        break;
                    }

                    // Copies are consecutive in the arena; take the whole ones that fit.
                    int last = first;
                    int regionIndex = _regions.Count;

                    while (last < entries.Length && entries[last].Offset + entries[last].Size - start <= granted)
                    {
                        entries[last++].Region = regionIndex;
                    }

                    int length = entries[last - 1].Offset + entries[last - 1].Size - start;

                    _regions.Add(new Region { Handle = handle, BufferOffset = bufferOffset, ArenaStart = start });
                    target.Write(handle, bufferOffset, _arena.AsSpan(start, length));
                    pool.Commit(length);

                    _uploadedEntries = last;
                    uploaded += length;
                }

                return uploaded;
            }

            private bool OverlapsInline(ulong gpuVa, ulong size)
            {
                if (_inlineRangeCount == 0)
                {
                    return false;
                }

                ulong end = gpuVa + size;

                for (ulong page = gpuVa & ~0xFFFUL; page < end; page += 0x1000)
                {
                    if (!_inlinePages.TryGetValue(page, out int link))
                    {
                        continue;
                    }

                    for (; link >= 0; link = _inlineRanges[link].Next)
                    {
                        if (_inlineRanges[link].Start < end && gpuVa < _inlineRanges[link].End)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            private void RecordInlineLocked(ulong gpuVa, ulong size)
            {
                ulong end = gpuVa + size;

                for (ulong page = gpuVa & ~0xFFFUL; page < end; page += 0x1000)
                {
                    ref int head = ref CollectionsMarshal.GetValueRefOrAddDefault(_inlinePages, page, out bool known);

                    if (known)
                    {
                        ref InlineRange newest = ref _inlineRanges[head];

                        // Consecutive updates advance through the buffer: grow the newest range instead of adding one.
                        if (gpuVa <= newest.End && newest.Start <= end)
                        {
                            newest.Start = Math.Min(newest.Start, gpuVa);
                            newest.End = Math.Max(newest.End, end);
                            continue;
                        }
                    }

                    if (_inlineRangeCount == _inlineRanges.Length)
                    {
                        Array.Resize(ref _inlineRanges, _inlineRanges.Length * 2);
                    }

                    _inlineRanges[_inlineRangeCount] = new InlineRange { Start = gpuVa, End = end, Next = known ? head : -1 };
                    head = _inlineRangeCount++;
                }
            }

            private int MarkInvalid(ulong gpuVa, ulong size)
            {
                if (_pageLinkCount == 0)
                {
                    return 0;
                }

                int count = 0;
                ulong end = gpuVa + size;
                Span<Entry> entries = CollectionsMarshal.AsSpan(_entries);

                for (ulong page = gpuVa & ~0xFFFUL; page < end; page += 0x1000)
                {
                    if (!_entryPages.TryGetValue(page, out int link))
                    {
                        continue;
                    }

                    for (; link >= 0; link = _pageLinks[link].Next)
                    {
                        ref Entry entry = ref entries[_pageLinks[link].Entry];

                        if (!entry.Invalid && entry.GpuVa < end && gpuVa < entry.GpuVa + (ulong)entry.Size)
                        {
                            entry.Invalid = true;
                            count++;
                        }
                    }
                }

                if (count != 0)
                {
                    Interlocked.Increment(ref _generation);
                }

                return count;
            }
        }

        /// <summary>A bound range's link to its snapshot, valid only while its batch executes.</summary>
        public readonly struct Ref
        {
            public readonly Batch Batch;
            public readonly long BatchId;
            public readonly int Index;

            public Ref(Batch batch, long batchId, int index)
            {
                Batch = batch;
                BatchId = batchId;
                Index = index;
            }

            /// <summary>GPU thread only.</summary>
            public bool IsCurrent => Batch != null && BatchId == _currentId && BatchId != 0;
        }

        /// <summary>Submitting thread: a batch to fill, or null when disabled.</summary>
        public static Batch Rent(long batchId, RangeReader reader)
        {
            PollToggle();

            int epoch = Volatile.Read(ref _modeEpoch);

            if (_mode == ModeOff)
            {
                return null;
            }

            if (!_pool.TryDequeue(out Batch batch))
            {
                batch = new Batch();
            }

            batch.Begin(batchId, reader, epoch, _options);
            Interlocked.Increment(ref _batches);

            return batch;
        }

        /// <summary>
        /// Submitting thread: the sub-batches of a parallel push, <paramref name="first"/> included, sharing its id, epoch,
        /// reader and options. Set before any entry referring to them is queued.
        /// </summary>
        public static Batch[] RentSiblings(Batch first, int chunks)
        {
            Batch[] group = new Batch[chunks];
            group[0] = first;

            for (int index = 1; index < chunks; index++)
            {
                if (!_pool.TryDequeue(out Batch batch))
                {
                    batch = new Batch();
                }

                batch.Begin(first.Id, first.Reader, first.Epoch, first.Flags);
                group[index] = batch;
            }

            foreach (Batch batch in group)
            {
                batch.SetSiblings(group);
            }

            return group;
        }

        /// <summary>First entry of a chunk of a parallel push; chunk <c>chunks</c> starts at <paramref name="count"/>.</summary>
        public static int ChunkStart(int chunk, int chunks, int count) => (int)((long)count * chunk / chunks);

        /// <summary>
        /// Submitting thread, once every chunk of a parallel push is decoded: each sub-batch marks the copies overlapping the
        /// inline updates the others decoded, then all are sealed.
        /// </summary>
        public static void CompleteGroup(Batch[] group)
        {
            foreach (Batch target in group)
            {
                foreach (Batch source in group)
                {
                    if (source != target)
                    {
                        target.MarkInlineFrom(source);
                    }
                }
            }

            foreach (Batch batch in group)
            {
                batch.Seal();
            }
        }

        /// <summary>GPU thread, before each command buffer is processed.</summary>
        public static void EnterEntry(Batch batch, long batchId)
        {
            _current = batch;
            _currentId = batchId;
        }

        /// <summary>
        /// GPU thread, after the last command buffer of a batch was processed: its snapshots can be reused.
        /// Entries of other channels may interleave in the queue, so a batch is released by its own last
        /// entry rather than by the next batch starting.
        /// </summary>
        public static void Release(Batch batch)
        {
            if (batch == null)
            {
                return;
            }

            Batch[] siblings = batch.Siblings;

            if (siblings == null)
            {
                ReleaseOne(batch);
                return;
            }

            // Every sub-batch of a parallel push stays in use until the push's last entry: links made in one chunk are
            // bound again in later ones.
            foreach (Batch sibling in siblings)
            {
                ReleaseOne(sibling);
            }
        }

        private static void ReleaseOne(Batch batch)
        {
            if (_current == batch)
            {
                _current = null;
                _currentId = 0;
            }

            // Not sealed yet when the GPU thread gets through the batch before the submitting thread seals it.
            if (batch.RequestRelease())
            {
                _pool.Enqueue(batch);
            }
        }

        /// <summary>GPU thread, when a uniform buffer is bound.</summary>
        public static Ref Capture(ulong gpuVa, ulong size)
        {
            if (_mode != ModeFull || size == 0 || size > MaxSnapshotSize)
            {
                return default;
            }

            Batch batch = _current;

            if (batch == null)
            {
                _captureNoBatch++;
                return default;
            }

            if (batch.Epoch != Volatile.Read(ref _modeEpoch))
            {
                // Rented before a mode change: flushes may have gone unrecorded while the GPU thread ignored it.
                _captureStale++;
                return default;
            }

            int entryIndex = batch.Find(gpuVa, (int)size);

            if (entryIndex < 0)
            {
                if (entryIndex == -1)
                {
                    _captureAbsent++;
                }
                else if (entryIndex == -3)
                {
                    _captureUnsealed++;
                }
                else
                {
                    _captureInvalid++;
                }

                return default;
            }

            _bindsMatched++;

            return new Ref(batch, _currentId, entryIndex);
        }

        /// <summary>GPU thread: an inline constant update wrote guest memory.</summary>
        public static void InvalidateCurrent(ulong gpuVa, ulong size)
        {
            if (_mode != ModeFull)
            {
                return;
            }

            Batch batch = _current;

            if (batch == null)
            {
                _invalidateNoBatch++;
                return;
            }

            Batch[] siblings = batch.Siblings;

            if (siblings == null)
            {
                batch.Invalidate(gpuVa, size);
                return;
            }

            // Draws after this write may bind copies from any chunk of the push.
            foreach (Batch sibling in siblings)
            {
                sibling.Invalidate(gpuVa, size);
            }
        }

        /// <summary>
        /// GPU thread, when a draw commits a linked uniform buffer: the host range holding the submitted copy, or a
        /// null handle to keep the normal path. <paramref name="generation"/> goes with the binding to
        /// <see cref="BindingsStillValid"/>.
        /// </summary>
        public static BufferRange Bind(in Ref snapshot, ulong physical, IStagingTarget target, out int generation)
        {
            generation = 0;

            if (_mode != ModeFull || !snapshot.IsCurrent || snapshot.Batch.Epoch != Volatile.Read(ref _modeEpoch))
            {
                return default;
            }

            BufferRange range = snapshot.Batch.Bind(snapshot.Index, physical, target, out generation, out int uploadedBytes);

            if (range.Handle != BufferHandle.Null)
            {
                _stagedBinds++;
            }

            if (uploadedBytes != 0)
            {
                _uploads++;
                _uploadBytes += uploadedBytes;
            }

            return range;
        }

        /// <summary>
        /// GPU thread: whether uniform buffers bound from copies of batch <paramref name="batchId"/> may stay bound:
        /// the batch still executes in the same mode, none of its copies was invalidated and no GPU write happened
        /// since the binding was made.
        /// </summary>
        public static bool BindingsStillValid(long batchId, int generation, int gpuWrites)
        {
            Batch batch = _current;

            return batch != null &&
                _currentId == batchId &&
                _mode == ModeFull &&
                batch.Epoch == Volatile.Read(ref _modeEpoch) &&
                batch.Generation == generation &&
                Volatile.Read(ref _gpuWrites) == gpuWrites;
        }

        /// <summary>Any thread: a buffer range was marked as written by the GPU.</summary>
        public static void NoteGpuWrite() => Interlocked.Increment(ref _gpuWrites);

        public static void CountSkippedGpuModified() => _skippedGpuModified++;

        public static void CountForcedRebind() => _forcedRebinds++;

        /// <summary>Submitting thread, when a push of entries finishes: when it started, its entries and command words.</summary>
        public static void RecordPush(long startTimestamp, int entries, long words)
        {
            long ticks = Stopwatch.GetTimestamp() - startTimestamp;

            lock (_pushLock)
            {
                _pushBatches++;
                _pushEntries += entries;
                _pushWords += words;
                _pushTicks += ticks;

                if (entries >= BigBatchEntries && _bigCount < _bigTicks.Length)
                {
                    _bigTicks[_bigCount] = ticks;
                    _bigEntries[_bigCount] = entries;
                    _bigCount++;
                }
            }
        }

        /// <summary>Submitting thread, after a parallel push: its chunks, the chunks helpers took, and how long it waited for them.</summary>
        public static void RecordParallel(int chunks, int helperChunks, long waitTicks)
        {
            lock (_pushLock)
            {
                _parallelPushes++;
                _parallelChunks += chunks;
                _parallelHelperChunks += helperChunks;
                _parallelWaitTicks += waitTicks;
            }
        }

        /// <summary>
        /// Pushes of at least <see cref="BigBatchEntries"/> entries since the last call: count, median and 90th percentile
        /// time per 1000 entries in units of 100 ns, and median time per push in microseconds.
        /// </summary>
        internal static string TakePushWindow()
        {
            double[] perThousand;
            double[] micros;

            lock (_pushLock)
            {
                perThousand = new double[_bigCount];
                micros = new double[_bigCount];

                for (int index = 0; index < _bigCount; index++)
                {
                    micros[index] = _bigTicks[index] * 1e6 / Stopwatch.Frequency;
                    perThousand[index] = micros[index] * 1000.0 / _bigEntries[index];
                }

                _bigCount = 0;
            }

            if (perThousand.Length == 0)
            {
                return "bigBatches=0";
            }

            Array.Sort(perThousand);
            Array.Sort(micros);

            return $"bigBatches={perThousand.Length} bigMed100nsPerK={(long)(perThousand[perThousand.Length / 2] * 10)} " +
                $"bigP90100nsPerK={(long)(perThousand[(int)(perThousand.Length * 0.9)] * 10)} bigMedUs={(long)micros[micros.Length / 2]}";
        }

        /// <summary>
        /// Host buffers holding uploaded copies. A chunk is refilled only once no batch of the last
        /// <see cref="ReuseDelayBatches"/> wrote to it; the host backend still guards ranges it is reading.
        /// </summary>
        public sealed class StagingPool
        {
            public const int ChunkSize = 1 << 20;
            public const int Alignment = 16;
            public const int ReuseDelayBatches = 64;
            public const int MaxChunks = 128;

            private readonly List<(BufferHandle Handle, long LastBatch)> _chunks = new();
            private int _current = -1;
            private int _offset;

            public int Chunks => _chunks.Count;

            /// <summary>
            /// Space in one chunk for at least <paramref name="minimum"/> and at most <paramref name="wanted"/> bytes,
            /// marked as written by batch <paramref name="batchId"/>; <see cref="Commit"/> takes what was written.
            /// </summary>
            public bool TryReserve(IStagingTarget target, int minimum, int wanted, long batchId, out BufferHandle handle, out int offset, out int granted)
            {
                handle = default;
                offset = 0;
                granted = 0;

                if (minimum > ChunkSize)
                {
                    return false;
                }

                if (_current < 0 || ChunkSize - _offset < minimum)
                {
                    _current = -1;

                    for (int i = 0; i < _chunks.Count; i++)
                    {
                        if (_chunks[i].LastBatch <= batchId - ReuseDelayBatches)
                        {
                            _current = i;
                            break;
                        }
                    }

                    if (_current < 0)
                    {
                        if (_chunks.Count >= MaxChunks)
                        {
                            return false;
                        }

                        _chunks.Add((target.Create(ChunkSize), 0));
                        _current = _chunks.Count - 1;
                    }

                    _offset = 0;
                }

                handle = _chunks[_current].Handle;
                offset = _offset;
                granted = Math.Min(wanted, ChunkSize - _offset);
                _chunks[_current] = (handle, batchId);

                return true;
            }

            public void Commit(int length)
            {
                _offset = Math.Min(ChunkSize, (_offset + length + Alignment - 1) & ~(Alignment - 1));
            }

            /// <summary>GPU thread: deletes the pool's host buffers, when the channel that owns it is destroyed.</summary>
            public void Release(IStagingTarget target)
            {
                foreach ((BufferHandle handle, long _) in _chunks)
                {
                    target.Delete(handle);
                }

                _chunks.Clear();
                _current = -1;
                _offset = 0;
            }
        }

        private static int? ParseMode(string text) => text switch
        {
            "0" => ModeOff,
            "1" => ModeFull,
            "2" => ModeSubmitOnly,
            _ => null,
        };

        private static string ModeName(int mode) => mode switch
        {
            ModeOff => "off",
            ModeFull => "full",
            _ => "submission side only",
        };

        private static void SwitchMode(int mode)
        {
            _mode = mode;
            Interlocked.Increment(ref _modeEpoch);
        }

        /// <summary>Tests switch modes directly: the toggle file would also reach a running game.</summary>
        internal static void SetModeForTests(int mode) => SwitchMode(mode);

        /// <summary>Tests set options directly for the same reason.</summary>
        internal static void SetOptionsForTests(int options) => _options = options;

        internal static int? ParseOptions(string text) =>
            int.TryParse(text, out int value) && (value == 0 || value == OptParallel)
                ? value
                : null;

        private static void PollToggle()
        {
            long now = Stopwatch.GetTimestamp();

            if (now - Interlocked.Read(ref _lastPollTicks) < Stopwatch.Frequency)
            {
                return;
            }

            Interlocked.Exchange(ref _lastPollTicks, now);

            try
            {
                if (File.Exists(ToggleFile))
                {
                    int? mode = ParseMode(File.ReadAllText(ToggleFile).Trim());

                    if (mode.HasValue && mode.Value != _mode)
                    {
                        SwitchMode(mode.Value);
                        Logger.Warning?.Print(LogClass.Gpu, $"ubo-snapshot: switched to mode {mode.Value} ({ModeName(mode.Value)}) by {ToggleFile}");
                    }
                }

                if (File.Exists(OptionsFile))
                {
                    int? options = ParseOptions(File.ReadAllText(OptionsFile).Trim());

                    if (options.HasValue && options.Value != _options)
                    {
                        _options = options.Value;
                        Logger.Warning?.Print(LogClass.Gpu, $"ubo-snapshot: submission options set to {options.Value} by {OptionsFile}");
                    }
                }
            }
            catch (IOException)
            {
            }

            if (now - _lastStatsTicks >= StatusIntervalSeconds * Stopwatch.Frequency)
            {
                _lastStatsTicks = now;
                Logger.Info?.Print(LogClass.Gpu, Status() + " " + TakePushWindow());
            }
        }

        public static string Status() =>
            $"ubo-snapshot: mode={_mode} batches={_batches} declined={_declined} inlineInvalidated={_inlineInvalidated} execInvalidated={_execInvalidated} " +
            $"bindsMatched={_bindsMatched} captureNoBatch={_captureNoBatch} captureAbsent={_captureAbsent} captureInvalid={_captureInvalid} captureStale={_captureStale} invalidateNoBatch={_invalidateNoBatch} " +
            $"stagedBinds={_stagedBinds} bindInvalid={_bindInvalid} physicalMoved={_physicalMoved} stagingFull={_stagingFull} uploads={_uploads} uploadBytes={_uploadBytes} " +
            $"skippedGpuModified={_skippedGpuModified} forcedRebinds={_forcedRebinds} gpuWrites={_gpuWrites} " +
            $"opt={_options} pushBatches={_pushBatches} pushEntries={_pushEntries} pushWords={_pushWords} " +
            $"pushUs={_pushTicks * 1_000_000 / Stopwatch.Frequency} captureUnsealed={_captureUnsealed} " +
            $"parallelPushes={_parallelPushes} parallelChunks={_parallelChunks} parallelHelperChunks={_parallelHelperChunks} parallelWaitUs={_parallelWaitTicks * 1_000_000 / Stopwatch.Frequency}";
    }
}
