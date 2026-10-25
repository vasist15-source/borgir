using ARMeilleure.Common;
using Ryujinx.Common;
using Ryujinx.Cpu.Signal;
using Ryujinx.Memory;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using static Ryujinx.Cpu.MemoryEhMeilleure;

namespace Ryujinx.Cpu
{
    /// <summary>
    /// Represents a table of guest address to a value.
    /// </summary>
    /// <typeparam name="TEntry">Type of the value</typeparam>
    public unsafe class SparseAddressTable<TEntry> : IAddressTable<TEntry> where TEntry : unmanaged
    {
        /// <summary>
        /// A sparsely mapped block of memory with a signal handler to map pages as they're accessed.
        /// </summary>
        private readonly struct TableSparseBlock : IDisposable
        {
            public readonly SparseMemoryBlock Block;
            private readonly TrackingEventDelegate _trackingEvent;

            public TableSparseBlock(ulong size, Action<nint> ensureMapped, PageInitDelegate pageInit, MemoryBlock fill)
            {
                SparseMemoryBlock block = new(size, pageInit, fill);

                _trackingEvent = (address, _, _) =>
                {
                    ulong pointer = (ulong)block.Block.Pointer + address;
                    ensureMapped((nint)pointer);
                    return pointer;
                };

                bool added = NativeSignalHandler.AddTrackedRegion(
                (nuint)block.Block.Pointer,
                (nuint)(block.Block.Pointer + (nint)block.Block.Size),
                Marshal.GetFunctionPointerForDelegate(_trackingEvent));

                if (!added)
                {
                    throw new InvalidOperationException("Number of allowed tracked regions exceeded.");
                }

                Block = block;
            }

            public void Dispose()
            {
                NativeSignalHandler.RemoveTrackedRegion((nuint)Block.Block.Pointer);

                Block.Dispose();
            }
        }

        private bool _disposed;
        private TEntry* _table;
        private TEntry _fill;

        private readonly TableSparseBlock _block;
        private readonly MemoryBlock _fillBlock;

        /// <inheritdoc/>
        public AddressTableType TableType => AddressTableType.Sparse;
        
        /// <inheritdoc/>
        public ulong Mask { get; }

        /// <inheritdoc/>
        public AddressTableLevel[] Levels { get; }

        /// <inheritdoc/>
        public TEntry Fill
        {
            get
            {
                return _fill;
            }
            set
            {
                UpdateFill(value);
            }
        }

        /// <inheritdoc/>
        public nint Base
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                return (nint)_table;
            }
        }

        /// <summary>
        /// Constructs a new instance of the <see cref="AddressTable{TEntry}"/> class with the specified list of
        /// <see cref="AddressTableLevel"/>.
        /// </summary>
        /// <param name="levels">Levels for the address table</param>
        /// <exception cref="ArgumentNullException"><paramref name="levels"/> is null</exception>
        /// <exception cref="ArgumentException">Length of <paramref name="levels"/> is less than 2</exception>
        public SparseAddressTable(AddressTableLevel[] levels)
        {
            ArgumentNullException.ThrowIfNull(levels);

            Levels = levels;
            Mask = 0;

            foreach (AddressTableLevel level in Levels)
            {
                Mask |= level.Mask;
            }
            
            _fillBlock = new MemoryBlock(MemoryBlock.GetPageSize() << 10, MemoryAllocationFlags.Mirrorable);
            
            // We need to use the full size, as some games dynamically expand the code range (e.g. SSBU)
            // Limiting the size to only the requested code range will cause crashes
            // This should not be an issue tho, as the SparseBlock dynamically allocates memory as needed, and
            // Falls back to the fill block in case guest code tries to call an unmapped function.
            ulong bottomLevelSize = (1ul << Levels.Last().Length) * (ulong)sizeof(TEntry);

            _block = new TableSparseBlock(bottomLevelSize, EnsureMapped, InitLeafPage, _fillBlock);

            _table = (TEntry*)_block.Block.Block.Pointer;
        }

        /// <summary>
        /// Create an <see cref="AddressTable{TEntry}"/> instance for an ARM function table.
        /// Selects the best table structure for A32/A64, taking into account the selected memory manager type.
        /// </summary>
        /// <param name="for64Bits">True if the guest is A64, false otherwise</param>
        /// <returns>An <see cref="AddressTable{TEntry}"/> for ARM function lookup</returns>
        public static SparseAddressTable<TEntry> CreateForArm(bool for64Bits)
        {
            return new SparseAddressTable<TEntry>(AddressTablePresets.GetArmPreset(for64Bits, true));
        }

        /// <summary>
        /// Update the fill value for the bottom level of the table.
        /// </summary>
        /// <param name="fillValue">New fill value</param>
        private void UpdateFill(TEntry fillValue)
        {
            if (_fillBlock != null)
            {
                Span<byte> span = _fillBlock.GetSpan(0, (int)_fillBlock.Size);
                MemoryMarshal.Cast<byte, TEntry>(span).Fill(fillValue);
            }

            _fill = fillValue;
        }

        /// <summary>
        /// Signal that the given code range exists.
        /// </summary>
        /// <param name="address"></param>
        /// <param name="size"></param>
        public void SignalCodeRange(ulong address, ulong size)
        {

        }

        /// <inheritdoc/>
        public bool IsValid(ulong address)
        {
            return (address & ~Mask) == 0;
        }

        /// <inheritdoc/>
        public ref TEntry GetValue(ulong address)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!IsValid(address))
            {
                throw new ArgumentException($"Address 0x{address:X} is not mapped onto the table.", nameof(address));
            }

            long index = Levels.Last().GetValue(address);

            EnsureMapped((nint)(_table + index));

            return ref _table[index];
        }

        /// <summary>
        /// Ensure the given pointer is mapped in any overlapping sparse reservations.
        /// </summary>
        /// <param name="ptr">Pointer to be mapped</param>
        private void EnsureMapped(nint ptr)
        {
            SparseMemoryBlock sparse = _block.Block;

            if (ptr >= sparse.Block.Pointer && ptr < sparse.Block.Pointer + (nint)sparse.Block.Size)
            {
                sparse.EnsureMapped((ulong)(ptr - sparse.Block.Pointer));
            }
        }

        /// <summary>
        /// Initialize a leaf page with the fill value.
        /// </summary>
        /// <param name="page">Page to initialize</param>
        private void InitLeafPage(Span<byte> page)
        {
            MemoryMarshal.Cast<byte, TEntry>(page).Fill(_fill);
        }

        /// <summary>
        /// Releases all resources used by the <see cref="AddressTable{TEntry}"/> instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases all unmanaged and optionally managed resources used by the <see cref="AddressTable{TEntry}"/>
        /// instance.
        /// </summary>
        /// <param name="disposing"><see langword="true"/> to dispose managed resources also; otherwise just unmanaged resouces</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _block.Dispose();
            _fillBlock.Dispose();

            _disposed = true;
        }

        /// <summary>
        /// Frees resources used by the <see cref="AddressTable{TEntry}"/> instance.
        /// </summary>
        ~SparseAddressTable()
        {
            Dispose(false);
        }
    }
}
