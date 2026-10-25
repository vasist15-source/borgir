using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    interface IAuto
    {
        bool HasCommandBufferDependency(CommandBufferScoped cbs);

        void IncrementReferenceCount();
        void DecrementReferenceCount(int cbIndex);
        void DecrementReferenceCount();
    }

    interface IAutoPrivate : IAuto
    {
        void AddCommandBufferDependencies(CommandBufferScoped cbs);
    }

    /// <summary>
    /// Something that can hand out a stand-in for a range of itself, so that data
    /// written while the real resource is still being read by the GPU lands somewhere
    /// else instead of forcing the write to be ordered against those reads.
    /// </summary>
    interface IMirrorable<T> where T : IDisposable
    {
        Auto<T> GetMirrorable(CommandBufferScoped cbs, ref int offset, int size, out bool mirrored);
        void ClearMirrors(CommandBufferScoped cbs, int offset, int size);
    }

    [SupportedOSPlatform("macos")]
    class Auto<T> : IAutoPrivate, IDisposable where T : IDisposable
    {
        private int _referenceCount;
        private T _value;

        private readonly BitMap _cbOwnership;
        private readonly MultiFenceHolder _waitable;
        private readonly IAutoPrivate[] _referencedObjs;
        private readonly IMirrorable<T> _mirrorable;

        private bool _disposed;
        private bool _destroyed;

        public Auto(T value)
        {
            _referenceCount = 1;
            _value = value;
            _cbOwnership = new BitMap(CommandBufferPool.MaxCommandBuffers);
        }

        public Auto(T value, MultiFenceHolder waitable, params IAutoPrivate[] referencedObjs) : this(value)
        {
            _waitable = waitable;
            _referencedObjs = referencedObjs;

            for (int i = 0; i < referencedObjs.Length; i++)
            {
                referencedObjs[i].IncrementReferenceCount();
            }
        }

        public Auto(T value, IMirrorable<T> mirrorable, MultiFenceHolder waitable, params IAutoPrivate[] referencedObjs) : this(value, waitable, referencedObjs)
        {
            _mirrorable = mirrorable;
        }

        public T GetMirrorable(CommandBufferScoped cbs, ref int offset, int size, out bool mirrored)
        {
            Auto<T> mirror = _mirrorable.GetMirrorable(cbs, ref offset, size, out mirrored);

            mirror._waitable?.AddBufferUse(cbs.CommandBufferIndex, offset, size, false);

            return mirror.Get(cbs);
        }

        public T Get(CommandBufferScoped cbs, int offset, int size, bool write = false)
        {
            _mirrorable?.ClearMirrors(cbs, offset, size);
            _waitable?.AddBufferUse(cbs.CommandBufferIndex, offset, size, write);
            return Get(cbs);
        }

        public T GetUnsafe()
        {
            return _value;
        }

        public T Get(CommandBufferScoped cbs)
        {
            if (!_destroyed)
            {
                AddCommandBufferDependencies(cbs);
            }

            return _value;
        }

        public bool HasCommandBufferDependency(CommandBufferScoped cbs)
        {
            return _cbOwnership.IsSet(cbs.CommandBufferIndex);
        }

        public bool HasRentedCommandBufferDependency(CommandBufferPool cbp)
        {
            return _cbOwnership.AnySet();
        }

        public void AddCommandBufferDependencies(CommandBufferScoped cbs)
        {
            // We don't want to add a reference to this object to the command buffer
            // more than once, so if we detect that the command buffer already has ownership
            // of this object, then we can just return without doing anything else.
            if (_cbOwnership.Set(cbs.CommandBufferIndex))
            {
                if (_waitable != null)
                {
                    cbs.AddWaitable(_waitable);
                }

                cbs.AddDependant(this);

                // A Metal texture view does not make command-buffer lifetime
                // tracking automatic for the texture or buffer that owns its
                // storage. Propagate the dependency so that backing resources
                // cannot be released before the GPU has finished using the view.
                if (_referencedObjs != null)
                {
                    for (int i = 0; i < _referencedObjs.Length; i++)
                    {
                        _referencedObjs[i].AddCommandBufferDependencies(cbs);
                    }
                }
            }
        }

        public bool TryIncrementReferenceCount()
        {
            int lastValue;
            do
            {
                lastValue = _referenceCount;

                if (lastValue == 0)
                {
                    return false;
                }
            }
            while (Interlocked.CompareExchange(ref _referenceCount, lastValue + 1, lastValue) != lastValue);

            return true;
        }

        public void IncrementReferenceCount()
        {
            if (Interlocked.Increment(ref _referenceCount) == 1)
            {
                Interlocked.Decrement(ref _referenceCount);
                throw new InvalidOperationException("Attempted to increment the reference count of an object that was already destroyed.");
            }
        }

        public void DecrementReferenceCount(int cbIndex)
        {
            _cbOwnership.Clear(cbIndex);
            DecrementReferenceCount();
        }

        public void DecrementReferenceCount()
        {
            if (Interlocked.Decrement(ref _referenceCount) == 0)
            {
                _value.Dispose();
                _value = default;
                _destroyed = true;

                if (_referencedObjs != null)
                {
                    for (int i = 0; i < _referencedObjs.Length; i++)
                    {
                        _referencedObjs[i].DecrementReferenceCount();
                    }
                }
            }

            Debug.Assert(_referenceCount >= 0);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                DecrementReferenceCount();
                _disposed = true;
            }
        }
    }
}
