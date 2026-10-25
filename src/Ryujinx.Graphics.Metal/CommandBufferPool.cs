using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Metal.SharpMetalExtensions;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    class CommandBufferPool : IDisposable
    {
        private static readonly bool _waitEveryCommit =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_WAIT_EVERY_COMMIT") == "1";

        public const int MaxCommandBuffers = 16;

        private readonly int _totalCommandBuffers;
        private readonly int _totalCommandBuffersMask;
        private readonly MTLCommandQueue _queue;
        private Thread _owner;
        private IEncoderFactory _defaultEncoderFactory;

        public bool OwnedByCurrentThread => _owner == Thread.CurrentThread;

        /// <summary>
        /// Make the calling thread the pool's owner. For the renderer's Dispose only:
        /// ThreadedRenderer stops its command loop and joins the GPU thread before it
        /// disposes the base renderer, so by contract nothing else touches the pool from
        /// then on, but the thread that ran the loop (GUI.RenderThread) is still alive -
        /// AppHost keeps it around until the renderer is gone - and Dispose runs on the
        /// caller (GUI.WindowThread). Without the hand-over the ownership guards report
        /// every buffer of the shutdown as an off-thread dispose and marshal the final
        /// flush onto a loop that no longer runs, which never returns.
        /// </summary>
        public void AdoptCurrentThread()
        {
            _owner = Thread.CurrentThread;
        }

        [SupportedOSPlatform("macos")]
        private struct ReservedCommandBuffer
        {
            public bool InUse;
            public bool InConsumption;
            public int SubmissionCount;
            public MTLCommandBuffer CommandBuffer;
            public CommandBufferEncoder Encoders;
            public FenceHolder Fence;

            public List<IAuto> Dependants;
            public List<MultiFenceHolder> Waitables;

            public void Use(MTLCommandQueue queue, IEncoderFactory stateManager, bool background)
            {
                MTLCommandBufferDescriptor descriptor = new();
#if DEBUG
                descriptor.ErrorOptions = MTLCommandBufferErrorOption.EncoderExecutionStatus;
#endif

                CommandBuffer = queue.CommandBuffer(descriptor);

                // The command buffer is autoreleased and nothing drains a pool on
                // this thread; take ownership (released in WaitAndDecrementRef).
                // The descriptor is owned (+1 from new) and was leaked per rent.
                ObjcOwnership.Retain(CommandBuffer.NativePtr);
                descriptor.Dispose();

                if (LiveResources.Enabled)
                {
                    LiveResources.Note(background ? LiveKind.BackgroundCommandBuffer : LiveKind.MainCommandBuffer, CommandBuffer.NativePtr, 0);
                }

                Fence = new FenceHolder(CommandBuffer);

                Encoders.Initialize(CommandBuffer, stateManager);

                InUse = true;
            }

            public void Initialize()
            {
                Dependants = [];
                Waitables = [];
                Encoders = new CommandBufferEncoder();
            }
        }

        private readonly ReservedCommandBuffer[] _commandBuffers;

        private readonly int[] _queuedIndexes;
        private int _queuedIndexesPtr;
        private int _queuedCount;
        private int _inUseCount;
        private readonly long[] _rentSeq;
        private long _rentCounter;
        private long _lastCommittedRentSeq;
        private long _commitCounter;
        private readonly long[] _commitSeq = new long[64];

        // GapAnatomy only: when each slot's buffer opened its first encoder and was committed.
        private readonly long[] _gapEncodeTicks = new long[64];
        private readonly long[] _gapCommitTicks = new long[64];

        // The two most recently committed buffers, retained, so the recording thread can
        // tell how much work the GPU has left (StarveFlush). Both are only touched on the
        // thread that commits, which is the thread that records.
        private MTLCommandBuffer _lastCommitted;
        private MTLCommandBuffer _previousCommitted;

        private void TrackCommitted(MTLCommandBuffer commandBuffer)
        {
            ObjcOwnership.Retain(commandBuffer.NativePtr);

            if (_previousCommitted.NativePtr != IntPtr.Zero)
            {
                ObjcOwnership.Release(_previousCommitted.NativePtr);
            }

            _previousCommitted = _lastCommitted;
            _lastCommitted = commandBuffer;
        }

        /// <summary>
        /// Level 1: the last committed buffer has completed, so the GPU is idle.
        /// Level 2: the one before it has completed, so the GPU holds its last buffer.
        /// Metal runs one queue in commit order, which is what makes either statement true.
        /// </summary>
        public bool GpuRunningDry(int level)
        {
            MTLCommandBuffer probe = level >= 2 ? _previousCommitted : _lastCommitted;

            return probe.NativePtr != IntPtr.Zero && probe.Status >= MTLCommandBufferStatus.Completed;
        }

        /// <summary>The rental sequence stamped on this command buffer when it was rented.</summary>
        public long RentSeqOf(int cbIndex) => (uint)cbIndex < (uint)_rentSeq.Length ? _rentSeq[cbIndex] : -1;

        /// <summary>The commit sequence, or 0 if not yet committed since its rental.</summary>
        public long CommitSeqOf(int cbIndex) => (uint)cbIndex < (uint)_commitSeq.Length ? _commitSeq[cbIndex] : -1;

        public CommandBufferPool(MTLCommandQueue queue, bool isLight = false)
        {
            _queue = queue;
            _owner = Thread.CurrentThread;

            _totalCommandBuffers = isLight ? 2 : MaxCommandBuffers;
            _totalCommandBuffersMask = _totalCommandBuffers - 1;

            _commandBuffers = new ReservedCommandBuffer[_totalCommandBuffers];

            _queuedIndexes = new int[_totalCommandBuffers];
            _rentSeq = new long[_totalCommandBuffers];
            _queuedIndexesPtr = 0;
            _queuedCount = 0;
        }

        public void Initialize(IEncoderFactory encoderFactory)
        {
            _defaultEncoderFactory = encoderFactory;

            for (int i = 0; i < _totalCommandBuffers; i++)
            {
                _commandBuffers[i].Initialize();
                WaitAndDecrementRef(i);
            }
        }

        public void AddDependant(int cbIndex, IAuto dependant)
        {
            dependant.IncrementReferenceCount();
            _commandBuffers[cbIndex].Dependants.Add(dependant);
        }

        public void AddWaitable(MultiFenceHolder waitable)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InConsumption)
                    {
                        AddWaitable(i, waitable);
                    }
                }
            }
        }

        public void AddInUseWaitable(MultiFenceHolder waitable)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse)
                    {
                        AddWaitable(i, waitable);
                    }
                }
            }
        }

        public void AddWaitable(int cbIndex, MultiFenceHolder waitable)
        {
            ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];
            if (waitable.AddFence(cbIndex, entry.Fence))
            {
                entry.Waitables.Add(waitable);
            }
        }

        public bool IsFenceOnRentedCommandBuffer(FenceHolder fence)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse && entry.Fence == fence)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public FenceHolder GetFence(int cbIndex)
        {
            return _commandBuffers[cbIndex].Fence;
        }

        public int GetSubmissionCount(int cbIndex)
        {
            return _commandBuffers[cbIndex].SubmissionCount;
        }

        private int FreeConsumed(bool wait)
        {
            int freeEntry = 0;

            while (_queuedCount > 0)
            {
                int index = _queuedIndexes[_queuedIndexesPtr];

                ref ReservedCommandBuffer entry = ref _commandBuffers[index];

                if (wait || !entry.InConsumption || entry.Fence.IsSignaled())
                {
                    WaitAndDecrementRef(index);

                    wait = false;
                    freeEntry = index;

                    _queuedCount--;
                    _queuedIndexesPtr = (_queuedIndexesPtr + 1) % _totalCommandBuffers;
                }
                else
                {
                    break;
                }
            }

            return freeEntry;
        }

        public CommandBufferScoped ReturnAndRent(CommandBufferScoped cbs)
        {
            Return(cbs);
            return Rent();
        }

        public CommandBufferScoped Rent()
        {
            lock (_commandBuffers)
            {
                int cursor = FreeConsumed(_inUseCount + _queuedCount == _totalCommandBuffers);

                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[cursor];

                    if (!entry.InUse && !entry.InConsumption)
                    {
                        entry.Use(_queue, _defaultEncoderFactory, _totalCommandBuffers != MaxCommandBuffers);

                        _inUseCount++;
                        _rentSeq[cursor] = ++_rentCounter;
                        if ((uint)cursor < (uint)_commitSeq.Length) { _commitSeq[cursor] = 0; }

                        return new CommandBufferScoped(this, entry.CommandBuffer, entry.Encoders, cursor);
                    }

                    cursor = (cursor + 1) & _totalCommandBuffersMask;
                }
            }

            throw new InvalidOperationException($"Out of command buffers (In use: {_inUseCount}, queued: {_queuedCount}, total: {_totalCommandBuffers})");
        }

        public void Return(CommandBufferScoped cbs)
        {
            // Ensure the encoder is committed.
            cbs.Encoders.EndCurrentPass();

            lock (_commandBuffers)
            {
                int cbIndex = cbs.CommandBufferIndex;

                ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];

                Debug.Assert(entry.InUse);
                Debug.Assert(entry.CommandBuffer.NativePtr == cbs.CommandBuffer.NativePtr);
                entry.InUse = false;
                entry.InConsumption = true;
                entry.SubmissionCount++;
                _inUseCount--;

                MTLCommandBuffer commandBuffer = entry.CommandBuffer;
                // Commit order against rental order. Splitting passes only orders work
                // inside one command buffer; between them the GPU follows commit order, and
                // a thread that encodes logically-earlier work but commits later inverts
                // the dependency. Thirty-two command buffers a frame here against
                // MoltenVK's twenty-four, and MoltenVK cannot have this problem at all -
                // kMVKQueueCountPerQueueFamily = 1 leaves it nothing to race with.
                long seq = _rentSeq[cbs.CommandBufferIndex];

                if (seq < _lastCommittedRentSeq)
                {
                    UploadCorrelator.NoteOutOfOrderCommit();
                }

                _lastCommittedRentSeq = seq;

                if ((uint)cbs.CommandBufferIndex < (uint)_commitSeq.Length)
                {
                    _commitSeq[cbs.CommandBufferIndex] = ++_commitCounter;
                }

                if (GapAnatomy.Enabled && (uint)cbIndex < (uint)_gapCommitTicks.Length)
                {
                    _gapEncodeTicks[cbIndex] = entry.Encoders.FirstEncodeTicks;
                    _gapCommitTicks[cbIndex] = Stopwatch.GetTimestamp();
                }

                commandBuffer.Commit();
                TrackCommitted(commandBuffer);

                // The one ordering experiment never run: make the whole GPU synchronous.
                // Intra-command-buffer serialisation left 17%; this also closes ordering
                // BETWEEN the ~32 command buffers a frame (and across threads). If the white
                // survives this, it is not an ordering fault of any kind.
                // RYUJINX_METAL_WAIT_EVERY_COMMIT=1.
                if (_waitEveryCommit)
                {
                    commandBuffer.WaitUntilCompleted();
                }

                int ptr = (_queuedIndexesPtr + _queuedCount) % _totalCommandBuffers;
                _queuedIndexes[ptr] = cbIndex;
                _queuedCount++;
            }
        }

        private void WaitAndDecrementRef(int cbIndex)
        {
            ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];

            if (entry.InConsumption)
            {
                if (!entry.Fence.IsSignaled())
                {
                    entry.Fence.Wait();
                }

                entry.InConsumption = false;

                // The pool already requests EncoderExecutionStatus on every command
                // buffer; this is where the answer is finally read. A buffer that failed
                // used to wedge silently - fences never signalling, presents frozen, no
                // message anywhere - which is precisely how the store-action experiment's
                // intermittent stall presented. One line here names the violation instead.
                if (entry.CommandBuffer.Status == MTLCommandBufferStatus.Error)
                {
                    Logger.Error?.PrintMsg(LogClass.Gpu,
                        $"command buffer failed: {StringHelper.String(entry.CommandBuffer.Error.LocalizedDescription)}");
                }

                // The buffer is complete, which is the only state in which these two are
                // defined. They are what separates "the GPU is saturated" from "the GPU is
                // starving" - the distinction every remaining performance decision needs
                // and that no counter here has ever measured.
                if (GpuTimeline.Enabled)
                {
                    GpuTimeline.Note(entry.CommandBuffer.GetGpuStartTime(), entry.CommandBuffer.GetGpuEndTime());
                }

                if (GapAnatomy.Enabled && (uint)cbIndex < (uint)_gapCommitTicks.Length)
                {
                    GapAnatomy.NoteBuffer(_gapEncodeTicks[cbIndex], _gapCommitTicks[cbIndex],
                        entry.CommandBuffer.GetGpuStartTime(), entry.CommandBuffer.GetGpuEndTime());
                    _gapCommitTicks[cbIndex] = 0;
                }
            }

            foreach (IAuto dependant in entry.Dependants)
            {
                dependant.DecrementReferenceCount(cbIndex);
            }

            foreach (MultiFenceHolder waitable in entry.Waitables)
            {
                waitable.RemoveFence(cbIndex);
                waitable.RemoveBufferUses(cbIndex);
            }

            entry.Dependants.Clear();
            entry.Waitables.Clear();

            if (LiveResources.Enabled)
            {
                LiveResources.Forget(entry.CommandBuffer.NativePtr);
            }

            entry.Fence?.Dispose();

            // Balance the retain taken in Use(). The FenceHolder holds its own
            // retain for waiters that outlive this slot.
            ObjcOwnership.Release(entry.CommandBuffer.NativePtr);
            entry.CommandBuffer = default;
        }

        public void Dispose()
        {
            for (int i = 0; i < _totalCommandBuffers; i++)
            {
                WaitAndDecrementRef(i);
            }

            foreach (MTLCommandBuffer tracked in new[] { _lastCommitted, _previousCommitted })
            {
                if (tracked.NativePtr != IntPtr.Zero)
                {
                    ObjcOwnership.Release(tracked.NativePtr);
                }
            }

            _lastCommitted = default;
            _previousCommitted = default;
        }
    }
}
