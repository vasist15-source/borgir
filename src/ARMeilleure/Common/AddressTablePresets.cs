namespace ARMeilleure.Common
{
    public static class AddressTablePresets
    {
        private static readonly AddressTableLevel[] _levels64Bit =
        [
            new(31, 17),
                new(23,  8),
                new(15,  8),
                new( 7,  8),
                new( 2,  5)
        ];

        private static readonly AddressTableLevel[] _levels32Bit =
        [
            new(31, 17),
                new(23,  8),
                new(15,  8),
                new( 7,  8),
                new( 1,  6)
        ];

        private static readonly AddressTableLevel[] _levels64BitMono =
        [
                new( 2, 37)
        ];

        private static readonly AddressTableLevel[] _levels32BitMono =
        [
                new( 1, 31)
        ];
        
        public static AddressTableLevel[] GetArmPreset(bool for64Bits, bool mono)
        {
            if (mono)
            {
                return for64Bits ? _levels64BitMono : _levels32BitMono;
            }
            else
            {
                return for64Bits ? _levels64Bit : _levels32Bit;
            }
        }
    }
}
