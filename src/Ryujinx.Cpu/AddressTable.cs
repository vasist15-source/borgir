using ARMeilleure.Common;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryujinx.Cpu
{
    /// <summary>
    /// Represents a table of guest address to a value.
    /// </summary>
    /// <typeparam name="TEntry">Type of the value</typeparam>
    public unsafe class AddressTable<TEntry> : IAddressTable<TEntry> where TEntry : unmanaged
    {
        private bool _disposed;
        private TEntry** _table;
        private readonly List<nint> _pages;

        /// <inheritdoc/>
        public AddressTableType TableType => AddressTableType.Default;

        /// <inheritdoc/>
        public ulong Mask { get; }

        /// <inheritdoc/>
        public AddressTableLevel[] Levels { get; }

        /// <inheritdoc/>
        public TEntry Fill { get; set; }

        /// <inheritdoc/>
        public nint Base
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                lock (_pages)
                {
                    return (nint)GetRootPage();
                }
            }
        }

        /// <summary>
        /// Constructs a new instance of the <see cref="AddressTable{TEntry}"/> class with the specified list of
        /// <see cref="AddressTableLevel"/>.
        /// </summary>
        /// <param name="levels">Levels for the address table</param>
        /// <exception cref="ArgumentNullException"><paramref name="levels"/> is null</exception>
        /// <exception cref="ArgumentException">Length of <paramref name="levels"/> is less than 2</exception>
        public AddressTable(AddressTableLevel[] levels)
        {
            ArgumentNullException.ThrowIfNull(levels);

            _pages = new List<nint>(capacity: 16);

            Levels = levels;
            Mask = 0;

            foreach (AddressTableLevel level in Levels)
            {
                Mask |= level.Mask;
            }
        }

        /// <summary>
        /// Create an <see cref="AddressTable{TEntry}"/> instance for an ARM function table.
        /// Selects the best table structure for A32/A64, taking into account the selected memory manager type.
        /// </summary>
        /// <param name="for64Bits">True if the guest is A64, false otherwise</param>
        /// <returns>An <see cref="AddressTable{TEntry}"/> for ARM function lookup</returns>
        public static AddressTable<TEntry> CreateForArm(bool for64Bits)
        {
            return new AddressTable<TEntry>(AddressTablePresets.GetArmPreset(for64Bits, false));
        }

        /// <summary>
        /// Signal that the given code range exists.
        /// </summary>
        /// <param name="address"></param>
        /// <param name="size"></param>
        public void SignalCodeRange(ulong address, ulong size) { }

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

            lock (_pages)
            {
                TEntry* page = GetPage(address);

                long index = Levels[^1].GetValue(address);

                return ref page[index];
            }
        }

        /// <summary>
        /// Gets the leaf page for the specified guest <paramref name="address"/>.
        /// </summary>
        /// <param name="address">Guest address</param>
        /// <returns>Leaf page for the specified guest <paramref name="address"/></returns>
        private TEntry* GetPage(ulong address)
        {
            TEntry** page = GetRootPage();

            for (int i = 0; i < Levels.Length - 1; i++)
            {
                ref AddressTableLevel level = ref Levels[i];
                ref TEntry* nextPage = ref page![level.GetValue(address)];

                if (nextPage == null)
                {
                    ref AddressTableLevel nextLevel = ref Levels[i + 1];

                    if (i == Levels.Length - 2)
                    {
                        nextPage = (TEntry*)Allocate(1 << nextLevel.Length, Fill);
                    }
                    else
                    {
                        nextPage = (TEntry*)Allocate(1 << nextLevel.Length, nint.Zero);
                    }
                }

                page = (TEntry**)nextPage;
            }

            return (TEntry*)page;
        }

        /// <summary>
        /// Lazily initialize and get the root page of the <see cref="AddressTable{TEntry}"/>.
        /// </summary>
        /// <returns>Root page of the <see cref="AddressTable{TEntry}"/></returns>
        private TEntry** GetRootPage()
        {
            if (_table == null)
            {
                if (Levels.Length == 1)
                    _table = (TEntry**)Allocate(1 << Levels[0].Length, Fill);
                else
                    _table = (TEntry**)Allocate(1 << Levels[0].Length, nint.Zero);
            }

            return _table;
        }

        /// <summary>
        /// Allocates a block of memory of the specified type and length.
        /// </summary>
        /// <typeparam name="T">Type of elements</typeparam>
        /// <param name="length">Number of elements</param>
        /// <param name="fill">Fill value</param>
        /// <returns>Allocated block</returns>
        private nint Allocate<T>(int length, T fill) where T : unmanaged
        {
            int size = sizeof(T) * length;

            nint address = (nint)NativeAllocator.Instance.Allocate((uint)size);

            Span<T> span = new((void*)address, length);
            span.Fill(fill);

            _pages.Add(address);

            //TranslatorEventSource.Log.AddressTableAllocated(size, leaf);

            return address;
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

            foreach (nint page in _pages)
            {
                Marshal.FreeHGlobal(page);
            }

            _disposed = true;
        }

        /// <summary>
        /// Frees resources used by the <see cref="AddressTable{TEntry}"/> instance.
        /// </summary>
        ~AddressTable()
        {
            Dispose(false);
        }
    }
}
