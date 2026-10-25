using Ryujinx.Common.Logging;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Where a heap-placed texture lives. Releasing an MTLTexture that was created from
    /// a placement heap destroys the texture object and nothing else - the bytes behind
    /// it stay reserved until <see cref="TextureHeapAllocator.Free"/> is told otherwise -
    /// so this has to travel with the texture all the way down to
    /// <see cref="DisposableTexture.Dispose"/>, which is the only place that knows the
    /// GPU is finished with it.
    /// </summary>
    [SupportedOSPlatform("macos")]
    readonly struct TextureHeapAllocation
    {
        public MTLHeap Heap { get; }
        public int HeapIndex { get; }
        public ulong Offset { get; }
        public ulong Size { get; }

        /// <summary>
        /// False for the default value, which is what every standalone texture carries.
        /// </summary>
        public bool IsValid => Heap.NativePtr != IntPtr.Zero;

        public TextureHeapAllocation(MTLHeap heap, int heapIndex, ulong offset, ulong size)
        {
            Heap = heap;
            HeapIndex = heapIndex;
            Offset = offset;
            Size = size;
        }
    }

    /// <summary>
    /// Suballocates scene-class render targets out of MTLHeaps instead of letting each
    /// one be its own <c>Device.NewTexture</c> allocation.
    ///
    /// This exists to test one hypothesis: that the driver's hazard tracking is coarser
    /// than a single texture, and that whether two scene targets share a tracking unit
    /// changes what the emulator observes. Standalone textures are each their own
    /// tracked resource; textures placed in one heap are ranges of a single tracked
    /// resource, and Metal's automatic hazard tracking on a heap works at heap
    /// granularity, not per-range. If the granularity is what matters, moving the scene
    /// class into one heap changes the behaviour and moving it back changes it again.
    ///
    /// That is also why the heap is created <see cref="MTLHazardTrackingMode.Tracked"/>
    /// explicitly: a heap defaults to Untracked, which would hand every hazard back to
    /// us as fences we do not emit, and the difference between A and B would then be
    /// "barriers vs no barriers" rather than "one tracking unit vs many". Tracked keeps
    /// the driver inserting the same barriers it inserts for standalone textures so the
    /// granularity is the only variable.
    ///
    /// The suballocator is a first-fit free list, deliberately. This is an experiment
    /// with a handful of live textures in it, and a wrong-but-simple allocator that can
    /// be read in one sitting is worth more here than a good one.
    ///
    /// RYUJINX_METAL_TEXTURE_HEAP=1, off by default.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class TextureHeapAllocator
    {
        // Any level enables the allocator; Level selects which textures it takes.
        public static bool Enabled => Level >= 1;

        /// <summary>
        /// 1 places only scene-class textures, which the repro's 800x448 dynamic resolution
        /// excludes; 2 places every private colour texture of any size worth heaping.
        /// </summary>
        private static int _placed;

        public static readonly int Level =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_TEXTURE_HEAP"), out int lv) ? lv : 0;

        /// <summary>
        /// Placement heaps commit their whole size up front, so this is a real reservation
        /// and not an upper bound. Scene targets run about 6 MB each, so this holds twenty
        /// or so live at once; anything past that grows a second heap rather than failing.
        /// </summary>
        private const ulong DefaultHeapSize = 128UL * 1024 * 1024;

        private readonly struct FreeRange
        {
            public ulong Offset { get; }
            public ulong Size { get; }

            public ulong End => Offset + Size;

            public FreeRange(ulong offset, ulong size)
            {
                Offset = offset;
                Size = size;
            }
        }

        private sealed class HeapBlock
        {
            public MTLHeap Heap;

            /// <summary>
            /// Kept sorted by offset and fully coalesced, which is what makes the
            /// neighbour checks in <see cref="Release"/> a constant amount of work.
            /// </summary>
            public readonly List<FreeRange> Free = [];
        }

        private static readonly List<HeapBlock> _heaps = [];
        private static readonly Lock _lock = new();

        /// <summary>
        /// Set once the device has told us something about heaps we did not expect. Every
        /// such failure is systematic rather than incidental, so retrying it per texture
        /// only burns address space; the already-placed textures stay valid and everything
        /// created afterwards is standalone.
        /// </summary>
        private static bool _disabled;

        /// <summary>
        /// Places <paramref name="descriptor"/> in a heap and returns the resulting
        /// texture. Returns false when the texture cannot legally or practically live in a
        /// heap, in which case the caller must fall back to a standalone allocation - a
        /// declined placement must never leave the caller without a texture.
        /// </summary>
        public static bool TryAllocate(
            MTLDevice device,
            MTLTextureDescriptor descriptor,
            out MTLTexture texture,
            out TextureHeapAllocation allocation)
        {
            texture = default;
            allocation = default;

            // A placement heap imposes its storage mode on everything inside it, and a
            // resource whose descriptor disagrees with its heap does not throw - it comes
            // back nil. RYUJINX_METAL_SHARED_TEXTURES flips the descriptor to Shared, and
            // two experiments overlapping like that would produce no texture at all rather
            // than a result, so decline instead of guessing.
            if (descriptor.StorageMode != MTLStorageMode.Private)
            {
                return false;
            }

            // The size a texture occupies in a heap is not its standalone allocation size -
            // it carries the driver's own padding and tiling decisions - so the only valid
            // source for both numbers is the device, asked with the exact descriptor the
            // texture will be created from.
            MTLSizeAndAlign sizeAndAlign = device.HeapTextureSizeAndAlign(descriptor);

            if (sizeAndAlign.size == 0)
            {
                return false;
            }

            ulong align = sizeAndAlign.align == 0 ? 1 : sizeAndAlign.align;

            lock (_lock)
            {
                if (_disabled)
                {
                    return false;
                }

                for (int i = 0; i < _heaps.Count; i++)
                {
                    if (TryPlace(_heaps[i], i, descriptor, sizeAndAlign.size, align, ref texture, ref allocation))
                    {
                        if (++_placed == 1 || _placed % 200 == 0)
                        {
                            Logger.Info?.PrintMsg(LogClass.Gpu, $"heap-placed: {_placed} textures");
                        }

                        return true;
                    }

                    if (_disabled)
                    {
                        return false;
                    }
                }

                HeapBlock block = CreateHeap(device, Math.Max(DefaultHeapSize, AlignUp(sizeAndAlign.size, align)));

                if (block == null)
                {
                    return false;
                }

                _heaps.Add(block);

                return TryPlace(block, _heaps.Count - 1, descriptor, sizeAndAlign.size, align, ref texture, ref allocation);
            }
        }

        /// <summary>
        /// Hands a range back. The caller must already have made the texture aliasable and
        /// released it: until it does, the heap still considers those bytes live and
        /// handing them to a second texture is a use-after-free the validation layer will
        /// not catch for us.
        /// </summary>
        public static void Free(TextureHeapAllocation allocation)
        {
            if (!allocation.IsValid)
            {
                return;
            }

            lock (_lock)
            {
                if ((uint)allocation.HeapIndex >= (uint)_heaps.Count)
                {
                    return;
                }

                Release(_heaps[allocation.HeapIndex], allocation.Offset, allocation.Size);
            }
        }

        private static bool TryPlace(
            HeapBlock block,
            int heapIndex,
            MTLTextureDescriptor descriptor,
            ulong size,
            ulong align,
            ref MTLTexture texture,
            ref TextureHeapAllocation allocation)
        {
            for (int i = 0; i < block.Free.Count; i++)
            {
                FreeRange range = block.Free[i];
                ulong offset = AlignUp(range.Offset, align);

                // offset < range.Offset catches the alignment round-up wrapping, which is
                // the one way this arithmetic can produce an offset that looks valid.
                if (offset < range.Offset || offset + size > range.End)
                {
                    continue;
                }

                MTLTexture placed = block.Heap.NewTexture(descriptor, offset);

                if (placed.NativePtr == IntPtr.Zero)
                {
                    // The arithmetic fits but Metal refused the placement, so our idea of
                    // the size or alignment does not match the driver's - which will be
                    // just as true for the next texture. Give up on the heap path for the
                    // rest of the process instead of retrying and growing a fresh 128 MiB
                    // heap per scene texture. The range is left uncarved: nothing was
                    // placed in it.
                    _disabled = true;

                    Logger.Error?.PrintMsg(LogClass.Gpu,
                        $"Metal texture heap: placement of {size} bytes at offset {offset} was refused; " +
                        "disabling the heap path and using standalone textures.");

                    return false;
                }

                Carve(block, i, offset, size);

                texture = placed;
                allocation = new TextureHeapAllocation(block.Heap, heapIndex, offset, size);

                        if (++_placed == 1 || _placed % 200 == 0)
                        {
                            Logger.Info?.PrintMsg(LogClass.Gpu, $"heap-placed: {_placed} textures");
                        }

                return true;
            }

            return false;
        }

        private static void Carve(HeapBlock block, int index, ulong offset, ulong size)
        {
            FreeRange range = block.Free[index];
            ulong tail = offset + size;

            block.Free.RemoveAt(index);

            // The alignment padding in front of the placement goes back on the free list as
            // its own range instead of being charged to the allocation, so a later texture
            // with a weaker alignment requirement can still use it - and so the range
            // Free() hands back is exactly the one the texture occupied.
            //
            // Inserted tail first so both pieces end up in offset order at index.
            if (tail < range.End)
            {
                block.Free.Insert(index, new FreeRange(tail, range.End - tail));
            }

            if (offset > range.Offset)
            {
                block.Free.Insert(index, new FreeRange(range.Offset, offset - range.Offset));
            }
        }

        private static void Release(HeapBlock block, ulong offset, ulong size)
        {
            int index = 0;

            while (index < block.Free.Count && block.Free[index].Offset < offset)
            {
                index++;
            }

            block.Free.Insert(index, new FreeRange(offset, size));

            // Merge forwards before backwards: the backwards pass then sees the already
            // extended range, so one pass in each direction is enough to keep the list
            // fully coalesced and a heap that empties out ends up as a single range again.
            if (index + 1 < block.Free.Count && block.Free[index].End == block.Free[index + 1].Offset)
            {
                block.Free[index] = new FreeRange(block.Free[index].Offset, block.Free[index].Size + block.Free[index + 1].Size);
                block.Free.RemoveAt(index + 1);
            }

            if (index > 0 && block.Free[index - 1].End == block.Free[index].Offset)
            {
                block.Free[index - 1] = new FreeRange(block.Free[index - 1].Offset, block.Free[index - 1].Size + block.Free[index].Size);
                block.Free.RemoveAt(index);
            }
        }

        private static HeapBlock CreateHeap(MTLDevice device, ulong size)
        {
            MTLHeapDescriptor descriptor = new()
            {
                Size = size,
                Type = MTLHeapType.Placement,

                // Private to match what the standalone path allocates, and required to be
                // the same as the descriptors placed into it.
                StorageMode = MTLStorageMode.Private,

                // Explicit, because the heap default is Untracked. See the class comment:
                // the point of the experiment is tracking granularity, not the absence of
                // tracking.
                HazardTrackingMode = MTLHazardTrackingMode.Tracked,
            };

            MTLHeap heap = device.NewHeap(descriptor);

            descriptor.Dispose();

            if (heap.NativePtr == IntPtr.Zero)
            {
                _disabled = true;

                Logger.Error?.PrintMsg(LogClass.Gpu,
                    $"Metal texture heap: failed to create a {size >> 20} MiB placement heap; " +
                    "disabling the heap path and using standalone textures.");

                return null;
            }

            Logger.Info?.PrintMsg(LogClass.Gpu,
                $"Metal texture heap: created placement heap #{_heaps.Count} of {size >> 20} MiB (tracked).");

            HeapBlock block = new()
            {
                Heap = heap,
            };

            block.Free.Add(new FreeRange(0, size));

            return block;
        }

        // Division rather than the usual power-of-two mask: nothing in Metal's contract
        // promises the alignment it reports is a power of two, and a mask would silently
        // produce overlapping placements if it ever were not.
        private static ulong AlignUp(ulong value, ulong alignment)
        {
            return ((value + alignment - 1) / alignment) * alignment;
        }
    }
}
