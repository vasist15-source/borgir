using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Ryujinx.HLE.HOS.Kernel.Process.NvnPv
{
    /// <summary>
    /// Links one AArch64 ELF relocatable object (the guest side of the paravirtual NVN layer,
    /// compiled by clang with -fno-pic) at a chosen guest address. Allocated sections are laid
    /// out in two images: executable sections in the code image, everything else (read-only data
    /// included) in the data image, each starting page-aligned. Only the relocation types a
    /// small-code-model, non-PIC compile produces are handled; anything else is an error, so a
    /// compiler change cannot silently produce a mislinked image.
    /// </summary>
    sealed class GuestObject
    {
        private const ushort ShnUndef = 0;
        private const ushort ShnAbs = 0xFFF1;
        private const ulong ShfWrite = 1;
        private const ulong ShfAlloc = 2;
        private const ulong ShfExecInstr = 4;
        private const uint ShtNoBits = 8;
        private const uint ShtRela = 4;
        private const uint ShtSymTab = 2;

        private readonly struct Section
        {
            public readonly string Name;
            public readonly uint Type;
            public readonly ulong Flags;
            public readonly ulong Offset;
            public readonly ulong Size;
            public readonly uint Link;
            public readonly uint Info;
            public readonly ulong Align;

            public Section(string name, uint type, ulong flags, ulong offset, ulong size, uint link, uint info, ulong align)
            {
                Name = name;
                Type = type;
                Flags = flags;
                Offset = offset;
                Size = size;
                Link = link;
                Info = info;
                Align = align;
            }

            public bool Allocated => (Flags & ShfAlloc) != 0;
            public bool Executable => (Flags & ShfExecInstr) != 0;
        }

        private readonly struct Symbol
        {
            public readonly string Name;
            public readonly ushort Section;
            public readonly ulong Value;

            public Symbol(string name, ushort section, ulong value)
            {
                Name = name;
                Section = section;
                Value = value;
            }
        }

        private readonly byte[] _elf;
        private readonly Section[] _sections;
        private readonly Symbol[] _symbols;

        // Per allocated section: which image and where in it.
        private readonly bool[] _inCode;
        private readonly ulong[] _imageOffset;

        public ulong CodeSize { get; }
        public ulong DataSize { get; }

        public GuestObject(byte[] elf)
        {
            _elf = elf;

            if (elf.Length < 64 || elf[0] != 0x7F || elf[1] != (byte)'E' || elf[2] != (byte)'L' || elf[3] != (byte)'F' || elf[4] != 2 || elf[5] != 1)
            {
                throw new InvalidOperationException("not a little-endian ELF64 object");
            }

            if (U16(16) != 1 || U16(18) != 183)
            {
                throw new InvalidOperationException("not an AArch64 relocatable object");
            }

            ulong shoff = U64(0x28);
            int shentsize = U16(0x3A);
            int shnum = U16(0x3C);
            int shstrndx = U16(0x3E);

            _sections = new Section[shnum];

            ulong shstrOffset = U64((int)shoff + shstrndx * shentsize + 0x18);

            for (int i = 0; i < shnum; i++)
            {
                int h = (int)shoff + i * shentsize;

                _sections[i] = new Section(
                    CString((int)(shstrOffset + U32(h))),
                    U32(h + 4),
                    U64(h + 8),
                    U64(h + 0x18),
                    U64(h + 0x20),
                    U32(h + 0x28),
                    U32(h + 0x2C),
                    Math.Max(1UL, U64(h + 0x30)));
            }

            int symtab = Array.FindIndex(_sections, s => s.Type == ShtSymTab);

            if (symtab < 0)
            {
                throw new InvalidOperationException("object has no symbol table");
            }

            Section st = _sections[symtab];
            ulong strOffset = _sections[st.Link].Offset;

            _symbols = new Symbol[st.Size / 24];

            for (int i = 0; i < _symbols.Length; i++)
            {
                int e = (int)st.Offset + i * 24;

                _symbols[i] = new Symbol(CString((int)(strOffset + U32(e))), U16(e + 6), U64(e + 8));
            }

            _inCode = new bool[shnum];
            _imageOffset = new ulong[shnum];

            ulong code = 0;
            ulong data = 0;

            for (int i = 0; i < shnum; i++)
            {
                Section s = _sections[i];

                if (!s.Allocated || s.Size == 0)
                {
                    continue;
                }

                if (s.Executable)
                {
                    code = Align(code, s.Align);
                    _inCode[i] = true;
                    _imageOffset[i] = code;
                    code += s.Size;
                }
                else
                {
                    data = Align(data, s.Align);
                    _imageOffset[i] = data;
                    data += s.Size;
                }
            }

            CodeSize = Align(code, 0x1000);
            DataSize = Align(Math.Max(data, 1), 0x1000);
        }

        /// <summary>
        /// Produces both images for the given load addresses. Throws on undefined symbols and
        /// on relocations out of range or of an unsupported type.
        /// </summary>
        public (byte[] Code, byte[] Data) Link(ulong codeBase, ulong dataBase)
        {
            byte[] code = new byte[CodeSize];
            byte[] data = new byte[DataSize];

            for (int i = 0; i < _sections.Length; i++)
            {
                Section s = _sections[i];

                if (!s.Allocated || s.Size == 0 || s.Type == ShtNoBits)
                {
                    continue;
                }

                Array.Copy(_elf, (long)s.Offset, _inCode[i] ? code : data, (long)_imageOffset[i], (long)s.Size);
            }

            for (int r = 0; r < _sections.Length; r++)
            {
                Section rela = _sections[r];

                if (rela.Type != ShtRela)
                {
                    continue;
                }

                int target = (int)rela.Info;

                if (!_sections[target].Allocated)
                {
                    continue; // debug or metadata sections
                }

                byte[] image = _inCode[target] ? code : data;
                ulong sectionAddress = SectionAddress(target, codeBase, dataBase);

                for (ulong e = rela.Offset; e < rela.Offset + rela.Size; e += 24)
                {
                    ulong offset = U64((int)e);
                    ulong info = U64((int)e + 8);
                    long addend = (long)U64((int)e + 16);

                    int symIndex = (int)(info >> 32);
                    uint type = (uint)info;

                    ulong s = SymbolAddress(symIndex, codeBase, dataBase);
                    ulong p = sectionAddress + offset;
                    int at = (int)(_imageOffset[target] + offset);

                    Apply(image, at, type, s, addend, p, symIndex);
                }
            }

            return (code, data);
        }

        public ulong GetSymbolAddress(string name, ulong codeBase, ulong dataBase)
        {
            for (int i = 0; i < _symbols.Length; i++)
            {
                if (_symbols[i].Name == name && _symbols[i].Section != ShnUndef)
                {
                    return SymbolAddress(i, codeBase, dataBase);
                }
            }

            throw new KeyNotFoundException($"guest object has no symbol {name}");
        }

        private ulong SectionAddress(int section, ulong codeBase, ulong dataBase)
        {
            return (_inCode[section] ? codeBase : dataBase) + _imageOffset[section];
        }

        private ulong SymbolAddress(int index, ulong codeBase, ulong dataBase)
        {
            Symbol sym = _symbols[index];

            if (sym.Section == ShnAbs)
            {
                return sym.Value;
            }

            if (sym.Section == ShnUndef)
            {
                // __start_X / __stop_X: bounds of section X (the linker convention for sections named like identifiers).
                foreach ((string prefix, bool end) in new[] { ("__start_", false), ("__stop_", true) })
                {
                    if (sym.Name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        int section = Array.FindIndex(_sections, s => s.Allocated && s.Name == sym.Name[prefix.Length..]);

                        if (section >= 0)
                        {
                            return SectionAddress(section, codeBase, dataBase) + (end ? _sections[section].Size : 0);
                        }
                    }
                }
            }

            if (sym.Section == ShnUndef || sym.Section >= _sections.Length || !_sections[sym.Section].Allocated)
            {
                throw new InvalidOperationException($"guest object: undefined or unallocated symbol '{sym.Name}'");
            }

            return SectionAddress(sym.Section, codeBase, dataBase) + sym.Value;
        }

        private void Apply(byte[] image, int at, uint type, ulong s, long a, ulong p, int symIndex)
        {
            ulong value = (ulong)((long)s + a);

            switch (type)
            {
                case 257: // R_AARCH64_ABS64
                    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(at), value);
                    break;

                case 258: // R_AARCH64_ABS32
                    CheckRange((long)value, 32, false, type, symIndex);
                    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), (uint)value);
                    break;

                case 260: // R_AARCH64_PREL64
                    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(at), value - p);
                    break;

                case 261: // R_AARCH64_PREL32
                    {
                        long d = (long)(value - p);
                        CheckRange(d, 32, true, type, symIndex);
                        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), (uint)d);
                        break;
                    }

                case 273: // R_AARCH64_LD_PREL_LO19
                case 280: // R_AARCH64_CONDBR19
                    {
                        long d = (long)(value - p);
                        CheckRange(d, 21, true, type, symIndex);
                        Patch(image, at, 0x00FFFFE0u, (uint)(((d >> 2) & 0x7FFFF) << 5));
                        break;
                    }

                case 274: // R_AARCH64_ADR_PREL_LO21
                    {
                        long d = (long)(value - p);
                        CheckRange(d, 21, true, type, symIndex);
                        Patch(image, at, 0x60FFFFE0u, AdrImm(d));
                        break;
                    }

                case 275: // R_AARCH64_ADR_PREL_PG_HI21
                case 276: // R_AARCH64_ADR_PREL_PG_HI21_NC
                    {
                        long d = (long)((value & ~0xFFFUL) - (p & ~0xFFFUL)) >> 12;
                        if (type == 275)
                        {
                            CheckRange(d, 21, true, type, symIndex);
                        }
                        Patch(image, at, 0x60FFFFE0u, AdrImm(d));
                        break;
                    }

                case 277: // R_AARCH64_ADD_ABS_LO12_NC
                case 278: // R_AARCH64_LDST8_ABS_LO12_NC
                    Patch(image, at, 0x003FFC00u, (uint)((value & 0xFFF) << 10));
                    break;

                case 284: // R_AARCH64_LDST16_ABS_LO12_NC
                    Patch(image, at, 0x003FFC00u, (uint)(((value & 0xFFF) >> 1) << 10));
                    break;

                case 285: // R_AARCH64_LDST32_ABS_LO12_NC
                    Patch(image, at, 0x003FFC00u, (uint)(((value & 0xFFF) >> 2) << 10));
                    break;

                case 286: // R_AARCH64_LDST64_ABS_LO12_NC
                    Patch(image, at, 0x003FFC00u, (uint)(((value & 0xFFF) >> 3) << 10));
                    break;

                case 299: // R_AARCH64_LDST128_ABS_LO12_NC
                    Patch(image, at, 0x003FFC00u, (uint)(((value & 0xFFF) >> 4) << 10));
                    break;

                case 279: // R_AARCH64_TSTBR14
                    {
                        long d = (long)(value - p);
                        CheckRange(d, 16, true, type, symIndex);
                        Patch(image, at, 0x0007FFE0u, (uint)(((d >> 2) & 0x3FFF) << 5));
                        break;
                    }

                case 282: // R_AARCH64_JUMP26
                case 283: // R_AARCH64_CALL26
                    {
                        long d = (long)(value - p);
                        CheckRange(d, 28, true, type, symIndex);
                        Patch(image, at, 0x03FFFFFFu, (uint)((d >> 2) & 0x3FFFFFF));
                        break;
                    }

                default:
                    throw new InvalidOperationException($"guest object: unsupported relocation type {type} against '{_symbols[symIndex].Name}'");
            }
        }

        private static uint AdrImm(long d)
        {
            uint immlo = (uint)(d & 3);
            uint immhi = (uint)((d >> 2) & 0x7FFFF);

            return (immlo << 29) | (immhi << 5);
        }

        private void CheckRange(long value, int bits, bool signed, uint type, int symIndex)
        {
            bool ok = signed
                ? value >= -(1L << (bits - 1)) && value < (1L << (bits - 1))
                : (ulong)value < (1UL << bits) || value >= -(1L << (bits - 1));

            if (!ok)
            {
                throw new InvalidOperationException($"guest object: relocation {type} against '{_symbols[symIndex].Name}' out of range ({value:X})");
            }
        }

        private static void Patch(byte[] image, int at, uint mask, uint bits)
        {
            uint insn = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at));

            insn = (insn & ~mask) | (bits & mask);

            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), insn);
        }

        private static ulong Align(ulong value, ulong align) => (value + align - 1) & ~(align - 1);

        private ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(_elf.AsSpan(at));
        private uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(_elf.AsSpan(at));
        private ulong U64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(_elf.AsSpan(at));

        private string CString(int at)
        {
            int end = at;

            while (end < _elf.Length && _elf[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(_elf, at, end - at);
        }
    }
}
