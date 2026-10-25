using Ryujinx.Graphics.Gpu.Memory;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.GPFifo
{
    /// <summary>
    /// Represents a GPU General Purpose FIFO device.
    /// </summary>
    public sealed class GPFifoDevice : IDisposable
    {
        /// <summary>
        /// Indicates if the command buffer has pre-fetch enabled.
        /// </summary>
        private enum CommandBufferType
        {
            Prefetch,
            NoPrefetch,
        }

        /// <summary>
        /// Command buffer data.
        /// </summary>
        private struct CommandBuffer
        {
            /// <summary>
            /// Processor used to process the command buffer. Contains channel state.
            /// </summary>
            public GPFifoProcessor Processor;

            /// <summary>
            /// The type of the command buffer.
            /// </summary>
            public CommandBufferType Type;

            /// <summary>
            /// Fetched data.
            /// </summary>
            public int[] Words;

            /// <summary>
            /// The GPFIFO entry address (used in <see cref="CommandBufferType.NoPrefetch"/> mode).
            /// </summary>
            public ulong EntryAddress;

            /// <summary>
            /// The count of entries inside this GPFIFO entry.
            /// </summary>
            public uint EntryCount;

            /// <summary>
            /// Uniform buffer snapshots of the batch, or of the chunk of a parallel push, this command buffer was pushed with.
            /// </summary>
            public UniformSubmitSnapshot.Batch UboSnapshots;

            /// <summary>
            /// Identifies the push this command buffer came with.
            /// </summary>
            public long UboBatchId;

            /// <summary>
            /// True for the last command buffer of its push.
            /// </summary>
            public bool UboLastInBatch;

            /// <summary>
            /// Host work run on the GPU thread in queue order instead of commands (the NVN replay).
            /// </summary>
            public Action HostAction;

            /// <summary>
            /// Get the entries for the command buffer from memory.
            /// </summary>
            /// <param name="memoryManager">The memory manager used to fetch the data</param>
            /// <param name="flush">If true, flushes potential GPU written data before reading the command buffer</param>
            /// <returns>The fetched data</returns>
            private readonly ReadOnlySpan<int> GetWords(MemoryManager memoryManager, bool flush)
            {
                return MemoryMarshal.Cast<byte, int>(memoryManager.GetSpan(EntryAddress, (int)EntryCount * 4, flush));
            }

            /// <summary>
            /// Prefetch the command buffer.
            /// </summary>
            /// <param name="memoryManager">The memory manager used to fetch the data</param>
            public void Prefetch(MemoryManager memoryManager)
            {
                Words = GetWords(memoryManager, true).ToArray();
            }

            /// <summary>
            /// Fetch the command buffer.
            /// </summary>
            /// <param name="memoryManager">The memory manager used to fetch the data</param>
            /// <param name="flush">If true, flushes potential GPU written data before reading the command buffer</param>
            /// <returns>The command buffer words</returns>
            public readonly ReadOnlySpan<int> Fetch(MemoryManager memoryManager, bool flush)
            {
                return Words ?? GetWords(memoryManager, flush);
            }
        }

        private readonly ConcurrentQueue<CommandBuffer> _commandBufferQueue;

        private GPFifoProcessor _prevChannelProcessor;

        private readonly bool _ibEnable;
        private readonly GpuContext _context;
        private readonly AutoResetEvent _event;

        private bool _interrupt;
        private int _flushSkips;

        /// <summary>
        /// Creates a new instance of the GPU General Purpose FIFO device.
        /// </summary>
        /// <param name="context">GPU context that the GPFIFO belongs to</param>
        internal GPFifoDevice(GpuContext context)
        {
            _commandBufferQueue = new ConcurrentQueue<CommandBuffer>();
            _ibEnable = true;
            _context = context;
            _event = new AutoResetEvent(false);
        }

        /// <summary>
        /// Signal the FIFO that there are new entries to process.
        /// </summary>
        public void SignalNewEntries()
        {
            _event.Set();
        }

        /// <summary>
        /// Push a GPFIFO entry in the form of a prefetched command buffer.
        /// It is intended to be used by nvservices to handle special cases.
        /// </summary>
        /// <param name="processor">Processor used to process <paramref name="commandBuffer"/></param>
        /// <param name="commandBuffer">The command buffer containing the prefetched commands</param>
        internal void PushHostCommandBuffer(GPFifoProcessor processor, int[] commandBuffer)
        {
            _commandBufferQueue.Enqueue(new CommandBuffer
            {
                Processor = processor,
                Type = CommandBufferType.Prefetch,
                Words = commandBuffer,
                EntryAddress = ulong.MaxValue,
                EntryCount = (uint)commandBuffer.Length,
            });
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the GPU thread, in order with the GPFIFO entries pushed
        /// before and after it. Used by the paravirtual NVN replay, which issues GAL calls itself.
        /// </summary>
        public void PushHostAction(Action action)
        {
            _commandBufferQueue.Enqueue(new CommandBuffer
            {
                Type = CommandBufferType.Prefetch,
                Words = Array.Empty<int>(),
                HostAction = action,
            });

            _event.Set();
        }

        /// <summary>
        /// Create a CommandBuffer from a GPFIFO entry.
        /// </summary>
        /// <param name="processor">Processor used to process the command buffer pointed to by <paramref name="entry"/></param>
        /// <param name="entry">The GPFIFO entry</param>
        /// <returns>A new CommandBuffer based on the GPFIFO entry</returns>
        private static CommandBuffer CreateCommandBuffer(GPFifoProcessor processor, GPEntry entry)
        {
            CommandBufferType type = CommandBufferType.Prefetch;

            if (entry.Entry1Sync == Entry1Sync.Wait)
            {
                type = CommandBufferType.NoPrefetch;
            }

            ulong startAddress = ((ulong)entry.Entry0Get << 2) | ((ulong)entry.Entry1GetHi << 32);

            return new CommandBuffer
            {
                Processor = processor,
                Type = type,
                Words = null,
                EntryAddress = startAddress,
                EntryCount = (uint)entry.Entry1Length,
            };
        }

        /// <summary>
        /// Finds the uniform buffer binds of a command buffer and copies the bound ranges into <paramref name="batch"/>.
        /// </summary>
        private static void DecodeUniformBinds(UniformSubmitSnapshot.Decoder decoder, GPFifoProcessor processor, in CommandBuffer commandBuffer, UniformSubmitSnapshot.Batch batch)
        {
            if (commandBuffer.Words != null)
            {
                decoder.Decode(commandBuffer.Words, batch);
            }
            else if (commandBuffer.Type != CommandBufferType.Prefetch || !TryDecodeFromMemory(decoder, processor, commandBuffer, batch))
            {
                // Command data the GPU may still write, or not mapped: nothing reliable to decode ahead of execution.
                decoder.Reset();
            }
        }

        private static bool TryDecodeFromMemory(UniformSubmitSnapshot.Decoder decoder, GPFifoProcessor processor, in CommandBuffer commandBuffer, UniformSubmitSnapshot.Batch batch)
        {
            MemoryManager memory = processor.MemoryManager;
            ulong size = (ulong)commandBuffer.EntryCount * 4;

            try
            {
                if (size == 0 || !memory.IsMapped(commandBuffer.EntryAddress) || !memory.IsMapped(commandBuffer.EntryAddress + size - 1))
                {
                    return false;
                }

                decoder.Decode(MemoryMarshal.Cast<byte, int>(memory.GetSpan(commandBuffer.EntryAddress, (int)size)), batch);

                return true;
            }
            catch (Ryujinx.Memory.InvalidMemoryRegionException)
            {
                return false;
            }
        }

        /// <summary>
        /// Runs the chunks of a parallel push (<see cref="UniformSubmitSnapshot.OptParallel"/>) on the submitting thread and
        /// helper threads. The submitting thread takes chunks too and returns only when every chunk is done, so the copies
        /// are complete before the guest runs again; a helper that is slow to wake costs only the chunks it already took.
        /// </summary>
        private sealed class UniformParallelSubmit
        {
            private const int Helpers = UniformSubmitSnapshot.ParallelMaxChunks - 1;

            public static readonly UniformParallelSubmit Instance = new();

            private sealed class Work
            {
                public GPFifoProcessor Processor;
                public CommandBuffer[] Buffers;
                public int Count;
                public int Chunks;
                public UniformSubmitSnapshot.Batch[] Group;
                public int Next = int.MaxValue / 2;
                public int Remaining;
                public int HelperChunks;
                public readonly ManualResetEventSlim Done = new(false);
            }

            private readonly SemaphoreSlim[] _wake = new SemaphoreSlim[Helpers];
            private readonly ConcurrentQueue<Work> _spare = new();
            private Work _current;

            private UniformParallelSubmit()
            {
                for (int index = 0; index < Helpers; index++)
                {
                    SemaphoreSlim wake = new(0);
                    UniformSubmitSnapshot.Decoder decoder = new();
                    string name = "GPU.UniformParallel" + index;
                    _wake[index] = wake;
                    new Thread(() => HelperLoop(wake, decoder)) { IsBackground = true, Name = name }.Start();
                }
            }

            public void Run(GPFifoProcessor processor, CommandBuffer[] buffers, int count, UniformSubmitSnapshot.Batch[] group)
            {
                if (!_spare.TryDequeue(out Work work))
                {
                    work = new Work();
                }

                work.Processor = processor;
                work.Buffers = buffers;
                work.Count = count;
                work.Group = group;
                work.Chunks = group.Length;
                work.Remaining = group.Length;
                work.HelperChunks = 0;
                work.Done.Reset();

                // Published last: a helper still holding a retired work item only ever sees a large Next.
                Volatile.Write(ref work.Next, -1);
                Volatile.Write(ref _current, work);

                for (int index = 0; index < Math.Min(Helpers, group.Length - 1); index++)
                {
                    _wake[index].Release();
                }

                while (TryRunChunk(work, processor.SubmitDecoder, false))
                {
                }

                long waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
                work.Done.Wait();
                long waited = System.Diagnostics.Stopwatch.GetTimestamp() - waitStart;

                Volatile.Write(ref _current, null);
                Volatile.Write(ref work.Next, int.MaxValue / 2);
                UniformSubmitSnapshot.RecordParallel(work.Chunks, work.HelperChunks, waited);
                work.Processor = null;
                work.Buffers = null;
                work.Group = null;
                _spare.Enqueue(work);
            }

            private static bool TryRunChunk(Work work, UniformSubmitSnapshot.Decoder decoder, bool helper)
            {
                int chunk = Interlocked.Increment(ref work.Next);

                if (chunk < 0 || chunk >= work.Chunks)
                {
                    return false;
                }

                try
                {
                    decoder.Reset();
                    UniformSubmitSnapshot.Batch batch = work.Group[chunk];
                    int end = UniformSubmitSnapshot.ChunkStart(chunk + 1, work.Chunks, work.Count);

                    for (int index = UniformSubmitSnapshot.ChunkStart(chunk, work.Chunks, work.Count); index < end; index++)
                    {
                        DecodeUniformBinds(decoder, work.Processor, work.Buffers[index], batch);
                    }
                }
                finally
                {
                    if (helper)
                    {
                        Interlocked.Increment(ref work.HelperChunks);
                    }

                    if (Interlocked.Decrement(ref work.Remaining) == 0)
                    {
                        work.Done.Set();
                    }
                }

                return true;
            }

            private void HelperLoop(SemaphoreSlim wake, UniformSubmitSnapshot.Decoder decoder)
            {
                while (true)
                {
                    wake.Wait();
                    Work work = Volatile.Read(ref _current);

                    if (work == null)
                    {
                        continue;
                    }

                    try
                    {
                        while (TryRunChunk(work, decoder, true))
                        {
                        }
                    }
                    catch (Exception)
                    {
                        // The chunk was accounted for in TryRunChunk; a failed decode only loses copies.
                    }
                }
            }
        }

        private static ReadOnlySpan<byte> ReadUniformRange(MemoryManager memory, ulong gpuVa, int size, out ulong physical)
        {
            physical = MemoryManager.PteUnmapped;

            try
            {
                ulong start = memory.Translate(gpuVa);
                ulong last = size > 0 && start != MemoryManager.PteUnmapped ? memory.Translate(gpuVa + (ulong)size - 1) : MemoryManager.PteUnmapped;

                if (last == MemoryManager.PteUnmapped)
                {
                    return default;
                }

                physical = start;

                // One physical run (the usual case): read it directly instead of translating page by page again.
                // A split range still reads correctly through the GPU mapping; draws decline it (several physical ranges).
                return last - start == (ulong)size - 1 ? memory.Physical.GetSpan(start, size) : memory.GetSpan(gpuVa, size);
            }
            catch (Ryujinx.Memory.InvalidMemoryRegionException)
            {
                return default;
            }
        }

        /// <summary>
        /// Pushes GPFIFO entries.
        /// </summary>
        /// <param name="processor">Processor used to process the command buffers pointed to by <paramref name="entries"/></param>
        /// <param name="entries">GPFIFO entries</param>
        internal void PushEntries(GPFifoProcessor processor, ReadOnlySpan<ulong> entries)
        {
            long uboPushStart = System.Diagnostics.Stopwatch.GetTimestamp();
            long uboWords = 0;
            bool beforeBarrier = true;

            // The guest may write its next frame's constants over ranges these entries bind while they are still queued:
            // copy the bound ranges now, before returning to the guest.
            long uboBatchId = UniformSubmitSnapshot.NextBatchId();
            MemoryManager uboMemory = processor.MemoryManager;
            UniformSubmitSnapshot.Batch uboSnapshots = entries.Length == 0 ? null :
                UniformSubmitSnapshot.Rent(uboBatchId, (ulong gpuVa, int size, out ulong physical) => ReadUniformRange(uboMemory, gpuVa, size, out physical));

            // A large push is decoded in chunks on several threads, each chunk into its own sub-batch.
            UniformSubmitSnapshot.Batch[] uboGroup = uboSnapshots != null && (uboSnapshots.Flags & UniformSubmitSnapshot.OptParallel) != 0 && entries.Length >= UniformSubmitSnapshot.ParallelMinEntries
                ? UniformSubmitSnapshot.RentSiblings(uboSnapshots, Math.Min(UniformSubmitSnapshot.ParallelMaxChunks, entries.Length / (UniformSubmitSnapshot.ParallelMinEntries / 2)))
                : null;
            CommandBuffer[] uboParallelBuffers = uboGroup != null ? System.Buffers.ArrayPool<CommandBuffer>.Shared.Rent(entries.Length) : null;
            int uboChunk = 0;

            for (int index = 0; index < entries.Length; index++)
            {
                ulong entry = entries[index];

                CommandBuffer commandBuffer = CreateCommandBuffer(processor, Unsafe.As<ulong, GPEntry>(ref entry));

                if (beforeBarrier && commandBuffer.Type == CommandBufferType.Prefetch)
                {
                    commandBuffer.Prefetch(processor.MemoryManager);
                }

                if (commandBuffer.Type == CommandBufferType.NoPrefetch)
                {
                    beforeBarrier = false;
                }

                if (uboSnapshots != null)
                {
                    if (uboGroup != null)
                    {
                        while (index >= UniformSubmitSnapshot.ChunkStart(uboChunk + 1, uboGroup.Length, entries.Length))
                        {
                            uboChunk++;
                        }

                        uboParallelBuffers[index] = commandBuffer;
                        commandBuffer.UboSnapshots = uboGroup[uboChunk];
                    }
                    else
                    {
                        DecodeUniformBinds(processor.SubmitDecoder, processor, commandBuffer, uboSnapshots);
                        commandBuffer.UboSnapshots = uboSnapshots;
                    }

                    commandBuffer.UboLastInBatch = index == entries.Length - 1;
                }

                commandBuffer.UboBatchId = uboBatchId;
                uboWords += commandBuffer.EntryCount;

                _commandBufferQueue.Enqueue(commandBuffer);
            }

            if (uboGroup != null)
            {
                // Decode every chunk before the guest runs again; the GPU thread uses the sub-batches once they are sealed.
                UniformParallelSubmit.Instance.Run(processor, uboParallelBuffers, entries.Length, uboGroup);
                UniformSubmitSnapshot.CompleteGroup(uboGroup);
                System.Buffers.ArrayPool<CommandBuffer>.Shared.Return(uboParallelBuffers, clearArray: true);
            }
            else
            {
                // From here on only the GPU thread uses the snapshots.
                uboSnapshots?.Seal();
            }

            UniformSubmitSnapshot.RecordPush(uboPushStart, entries.Length, uboWords);
        }

        /// <summary>
        /// Waits until commands are pushed to the FIFO.
        /// </summary>
        /// <returns>True if commands were received, false if wait timed out</returns>
        public bool WaitForCommands()
        {
            return !_commandBufferQueue.IsEmpty || (_event.WaitOne(8) && !_commandBufferQueue.IsEmpty);
        }

        /// <summary>
        /// Processes commands pushed to the FIFO.
        /// </summary>
        public void DispatchCalls()
        {
            // Use this opportunity to also dispose any pending channels that were closed.
            _context.RunDeferredActions();

            // Process command buffers.
            while (_ibEnable && !_interrupt && _commandBufferQueue.TryDequeue(out CommandBuffer entry))
            {
                if (entry.HostAction != null)
                {
                    // No GPFIFO batch: uniform buffers bound by the action must not take a batch's snapshots.
                    UniformSubmitSnapshot.EnterEntry(null, 0);
                    entry.HostAction();
                    continue;
                }

                bool flushCommandBuffer = true;

                if (_flushSkips != 0)
                {
                    _flushSkips--;
                    flushCommandBuffer = false;
                }

                ReadOnlySpan<int> words = entry.Fetch(entry.Processor.MemoryManager, flushCommandBuffer);

                // If we are changing the current channel,
                // we need to force all the host state to be updated.
                if (_prevChannelProcessor != entry.Processor)
                {
                    _prevChannelProcessor = entry.Processor;
                    entry.Processor.ForceAllDirty();
                }

                UniformSubmitSnapshot.EnterEntry(entry.UboSnapshots, entry.UboBatchId);

                entry.Processor.Process(entry.EntryAddress, words);

                if (entry.UboLastInBatch)
                {
                    UniformSubmitSnapshot.Release(entry.UboSnapshots);
                }
            }

            _interrupt = false;
        }

        /// <summary>
        /// Sets the number of flushes that should be skipped for subsequent command buffers.
        /// </summary>
        /// <remarks>
        /// This can improve performance when command buffer data only needs to be consumed by the GPU.
        /// </remarks>
        /// <param name="count">The amount of flushes that should be skipped</param>
        internal void SetFlushSkips(int count)
        {
            _flushSkips = count;
        }

        /// <summary>
        /// Interrupts command processing. This will break out of the DispatchCalls loop.
        /// </summary>
        public void Interrupt()
        {
            _interrupt = true;
            _event.Set();
        }

        /// <summary>
        /// Disposes of resources used for GPFifo command processing.
        /// </summary>
        public void Dispose() => _event.Dispose();
    }
}
