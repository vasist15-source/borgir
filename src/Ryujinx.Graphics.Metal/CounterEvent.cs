using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.SharpMetalExtensions;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    class CounterEvent : ICounterEvent
    {
        private readonly CounterManager _manager;
        private readonly MultiFenceHolder _waitable = new();

        /// <summary>
        /// A slot or result buffer this counter wrote, held until the GPU is done with it.
        /// </summary>
        private sealed class Held
        {
            // Slot index, or -1 for a result buffer of its own.
            public int Slot;
            public bool PerPass;
            public MTLBuffer Buffer;
            // Written since the last reset, so it counts towards the result.
            public bool Counted;
            // The command buffer of the last pass that wrote it, referenced.
            public FenceHolder Fence;
        }

        // In the order they were rented, which is the order their passes were encoded.
        //
        // A counter used to hold everything it wrote until it was reported, and per-pass
        // mode writes a new slot every pass whether or not the guest ever reads the counter.
        // Mario Bros. Wonder reports none for a whole session: the 4,095 per-pass slots ran
        // out within a second and every pass after that allocated an 8-byte result buffer of
        // its own, which nothing released - 70 passes a frame, 4,000 MTLBuffers a second, and
        // once the driver's small-buffer pool was full one 16 KB page each, 65 MB/s of device
        // memory until the machine swapped. Entries are now retired as soon as their command
        // buffer completes and nothing will write them again: the value goes into _retired
        // and the slot or buffer goes back to the manager, so a counter holds only what is in
        // flight however long it stays open.
        private readonly List<Held> _held = [];
        private Held _active;
        private ulong _retired;

        private MTLBuffer _activeBuffer;
        // Slot the encoder is counting into, or -1 when this counter writes a buffer of its
        // own (slot mode off, or the pool was exhausted).
        private int _activeSlot = -1;
        // The active slot is in the per-pass buffer: it belongs to one pass and is never
        // reused by the next one (see CounterManager.PerPassEnabled).
        private bool _activePerPass;
        private EventHandler<ulong> _resultHandler;
        private double _divisor;

        internal bool ClearCounter { get; private set; }
        internal bool Completed { get; private set; }

        public bool Invalid { get; set; }

        public CounterEvent(CounterManager manager)
        {
            _manager = manager;
            _divisor = 1d;
            Completed = manager == null;
        }

        /// <summary>This counter accumulates into the manager's shared slot buffer.</summary>
        internal bool UsesSlotBuffer => _activeSlot >= 0 && !_activePerPass;

        /// <summary>This counter's open pass counts into a slot of the per-pass buffer.</summary>
        internal bool UsesPerPassSlot => _activeSlot >= 0 && _activePerPass;

        private void Hold(CommandBufferScoped cbs, int slot, bool perPass, MTLBuffer buffer)
        {
            FenceHolder fence = cbs.GetFence();
            fence.Get();

            _active = new Held { Slot = slot, PerPass = perPass, Buffer = buffer, Counted = true, Fence = fence };
            _held.Add(_active);

            CounterManager.NoteHeld(_held.Count);
        }

        private void Hold(CommandBufferScoped cbs, int slot, bool perPass) => Hold(cbs, slot, perPass, default);

        private void Hold(CommandBufferScoped cbs, MTLBuffer buffer) => Hold(cbs, -1, false, buffer);

        /// <summary>
        /// The active slot or buffer is about to be written by another pass, possibly of a
        /// later command buffer; it can only be retired after that one.
        /// </summary>
        private void TouchActive(CommandBufferScoped cbs)
        {
            FenceHolder fence = cbs.GetFence();

            if (_active != null && !ReferenceEquals(_active.Fence, fence))
            {
                fence.Get();
                _active.Fence.Put();
                _active.Fence = fence;
            }
        }

        private ulong ReadHeld(Held held)
        {
            if (held.Slot >= 0)
            {
                return held.PerPass ? _manager.ReadPerPassSlot(held.Slot) : _manager.ReadSlot(held.Slot);
            }

            long value = Marshal.ReadInt64(held.Buffer.Contents);

            return value > 0 ? (ulong)value : 0;
        }

        private void Return(Held held)
        {
            if (held.Slot < 0)
            {
                _manager.ReturnResultBuffer(held.Buffer);
            }
            else if (held.PerPass)
            {
                _manager.ReturnPerPassSlot(held.Slot);
            }
            else
            {
                _manager.ReturnSlot(held.Slot);
            }

            held.Fence.Put();
            held.Fence = null;
        }

        /// <summary>
        /// Folds every entry that is no longer active and whose last command buffer has
        /// completed into <see cref="_retired"/> and gives it back. Command buffers complete
        /// in the order they were committed, so the scan stops at the first entry still in
        /// flight; one committed out of order only waits for a later scan.
        /// </summary>
        private void Retire()
        {
            int kept = 0;
            int index = 0;
            FenceHolder checkedFence = null;

            for (; index < _held.Count; index++)
            {
                Held held = _held[index];

                if (held == _active)
                {
                    _held[kept++] = held;

                    continue;
                }

                // Consecutive entries usually share a command buffer: ask it once.
                if (!ReferenceEquals(held.Fence, checkedFence))
                {
                    if (!held.Fence.IsSignaled())
                    {
                        break;
                    }

                    checkedFence = held.Fence;
                }

                if (held.Counted)
                {
                    _retired += ReadHeld(held);
                }

                Return(held);
                CounterManager.NoteRetired();
            }

            if (kept != index)
            {
                for (; index < _held.Count; index++)
                {
                    _held[kept++] = _held[index];
                }

                _held.RemoveRange(kept, _held.Count - kept);
            }
        }

        /// <summary>
        /// Give this counter a slot of the shared buffer the open pass already bound, so a
        /// counter change needs no new pass. The pass's command buffer has to wait on this
        /// counter too: from here on its draws accumulate into this slot.
        /// </summary>
        internal bool TryBindToSlotBuffer(CommandBufferScoped cbs, out ulong offset)
        {
            if (_activeBuffer.NativePtr == IntPtr.Zero || _activePerPass)
            {
                if (!_manager.TryRentSlot(out int slot))
                {
                    offset = 0;

                    return false;
                }

                _activeSlot = slot;
                _activePerPass = false;
                Hold(cbs, slot, false);
                _activeBuffer = _manager.SlotBuffer;
                cbs.AddWaitable(_waitable);
            }
            else if (_activeSlot < 0)
            {
                // Already writing a buffer of its own; it cannot move mid-pass.
                offset = 0;

                return false;
            }
            else
            {
                TouchActive(cbs);
            }

            offset = (ulong)_activeSlot * sizeof(ulong);

            return true;
        }

        /// <summary>
        /// The per-pass counterpart of <see cref="TryBindToSlotBuffer"/>: a counter that starts
        /// (or restarts after a reset) inside a pass bound to the per-pass buffer takes a slot of
        /// its own there for the rest of that pass.
        /// </summary>
        internal bool TryBindToPerPassSlot(CommandBufferScoped cbs, out ulong offset)
        {
            // PrepareRenderPass rents a fresh per-pass slot for every pass, so an active
            // per-pass slot here is this pass's own and can be kept.
            if (_activeBuffer.NativePtr == IntPtr.Zero || !_activePerPass || _activeSlot < 0)
            {
                if (!_manager.TryRentPerPassSlot(out int slot))
                {
                    offset = 0;

                    return false;
                }

                _activeSlot = slot;
                _activePerPass = true;
                Hold(cbs, slot, true);
                _activeBuffer = _manager.PerPassBuffer;
                cbs.AddWaitable(_waitable);
            }
            else
            {
                TouchActive(cbs);
            }

            offset = (ulong)_activeSlot * sizeof(ulong);

            return true;
        }

        internal ulong PrepareRenderPass(MTLRenderPassDescriptor descriptor, CommandBufferScoped cbs)
        {
            // Every pass rents, so every pass also retires; per-pass mode replaces the
            // active entry below, which makes the previous pass's slot retirable next time.
            Retire();

            if (_manager.PerPassEnabled)
            {
                // A slot for this pass alone. Metal orders every encoder that writes a tracked
                // resource after the previous one that wrote it, so one result accumulated by
                // every pass chained all of them end to end: no pass's vertex work could start
                // before the previous pass's fragment work had finished (a Metal System Trace
                // showed 0% overlap). Distinct slots of an untracked buffer leave nothing to
                // order; the passes' counts are summed when the result is read.
                if (_manager.TryRentPerPassSlot(out int slot))
                {
                    _activeSlot = slot;
                    _activePerPass = true;
                    _activeBuffer = _manager.PerPassBuffer;
                    Hold(cbs, slot, true);
                }
                else
                {
                    _activeSlot = -1;
                    _activePerPass = false;
                    _activeBuffer = _manager.RentResultBuffer();
                    Hold(cbs, _activeBuffer);
                }

                descriptor.VisibilityResultBuffer = _activeBuffer;
                descriptor.SetVisibilityResultTypeReset();
            }
            else
            {
                if (_activeBuffer.NativePtr == IntPtr.Zero || _activePerPass)
                {
                    if (_manager.TryRentSlot(out int slot))
                    {
                        _activeSlot = slot;
                        _activeBuffer = _manager.SlotBuffer;
                        Hold(cbs, slot, false);
                    }
                    else
                    {
                        _activeSlot = -1;
                        _activeBuffer = _manager.RentResultBuffer();
                        Hold(cbs, _activeBuffer);
                    }

                    _activePerPass = false;
                }
                else
                {
                    TouchActive(cbs);
                }

                // The same counter can span many Metal render encoders. macOS 26's
                // accumulate mode makes every encoder add to this one 64-bit result.
                descriptor.VisibilityResultBuffer = _activeBuffer;
                descriptor.SetVisibilityResultTypeAccumulate();
            }

            // A result is only CPU-readable after every command buffer that wrote it
            // has completed. This also keeps the fence alive if the pool recycles its
            // command-buffer slot before the guest asks for the counter.
            cbs.AddWaitable(_waitable);

            return _activeSlot >= 0 ? (ulong)_activeSlot * sizeof(ulong) : 0;
        }

        internal void Reset()
        {
            // Do not clear a buffer that earlier commands will write later. Give the
            // post-reset interval a fresh result buffer and retain older buffers until
            // all their command buffers complete; only what is written from here on counts.
            _activeBuffer = default;
            _activeSlot = -1;
            _activePerPass = false;
            _active = null;
            _retired = 0;

            foreach (Held held in _held)
            {
                held.Counted = false;
            }

            ClearCounter = true;
        }

        internal void Complete(EventHandler<ulong> resultHandler, float divisor)
        {
            _resultHandler = resultHandler;
            _divisor = divisor > 0f ? divisor : 1d;
        }

        internal bool IsReady()
        {
            return _waitable.WaitForFences(false);
        }

        internal void Wait()
        {
            _waitable.WaitForFences();
        }

        internal ulong ReadResult()
        {
            ulong sum = _retired;

            foreach (Held held in _held)
            {
                if (held.Counted)
                {
                    sum += ReadHeld(held);
                }
            }

            return sum;
        }

        internal void Signal(ulong result)
        {
            Completed = true;
            _resultHandler?.Invoke(this, result);
        }

        internal ulong ScaleResult(ulong result)
        {
            return _divisor == 1d ? result : (ulong)Math.Ceiling(result / _divisor);
        }

        internal void ReleaseBuffers()
        {
            foreach (Held held in _held)
            {
                Return(held);
            }

            _held.Clear();
            _active = null;
            _retired = 0;
            _activeBuffer = default;
            _activeSlot = -1;
            _activePerPass = false;
        }

        public bool ReserveForHostAccess()
        {
            // Host conditional rendering is not implemented by the Metal pipeline;
            // the shared result is flushed and compared on the CPU instead.
            return false;
        }

        public void Flush()
        {
            if (!Completed && _manager != null)
            {
                _manager.FlushTo(this);
            }
        }

        public void Dispose()
        {
            // The counter queue owns result storage until GPU completion. CounterCache
            // may keep this lightweight event after completion, just like Vulkan.
        }
    }
}
