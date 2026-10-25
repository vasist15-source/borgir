using ARMeilleure.Common;
using ARMeilleure.Memory;
using ARMeilleure.State;
using ARMeilleure.Translation;
using Ryujinx.Cpu;
using Ryujinx.Cpu.Jit;

namespace Ryujinx.Tests.Cpu
{
    public class CpuContext
    {
        private readonly Translator _translator;

        public CpuContext(IMemoryManager memory, bool for64Bit)
        {
            bool sparse = memory.Type is not MemoryManagerType.SoftwareMmu and not MemoryManagerType.SoftwarePageTable;
            IAddressTable<ulong> functionTable = sparse ? SparseAddressTable<ulong>.CreateForArm(for64Bit) : AddressTable<ulong>.CreateForArm(for64Bit);
            
            _translator = new Translator(new JitMemoryAllocator(), memory, functionTable);
            memory.UnmapEvent += UnmapHandler;
        }

        private void UnmapHandler(ulong address, ulong size)
        {
            _translator.InvalidateJitCacheRegion(address, size);
        }

        public static ExecutionContext CreateExecutionContext()
        {
            return new ExecutionContext(new JitMemoryAllocator(), new TickSource(19200000));
        }

        public void Execute(ExecutionContext context, ulong address)
        {
            _translator.Execute(context, address);
        }

        public void InvalidateCacheRegion(ulong address, ulong size)
        {
            _translator.InvalidateJitCacheRegion(address, size);
        }
    }
}
