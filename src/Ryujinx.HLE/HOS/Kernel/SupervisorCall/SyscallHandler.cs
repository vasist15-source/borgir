using Ryujinx.Cpu;
using Ryujinx.HLE.HOS.Kernel.Threading;

namespace Ryujinx.HLE.HOS.Kernel.SupervisorCall
{
    partial class SyscallHandler
    {
        private readonly KernelContext _context;

        public SyscallHandler(KernelContext context)
        {
            _context = context;
        }

        public void SvcCall(IExecutionContext context, ulong address, int id)
        {
            KThread currentThread = KernelStatic.GetCurrentThread();

            // NVN call counter's own supervisor calls (ids above anything Horizon defines).
            if (Process.NvnCallCounter.TryHandleSvc(context, id) || Process.NvnPv.NvnPvHost.TryHandleSvc(context, id))
            {
                currentThread.HandlePostSyscall();

                return;
            }

            if (currentThread.Owner != null &&
                currentThread.GetUserDisableCount() != 0 &&
                currentThread.Owner.PinnedThreads[currentThread.CurrentCore] == null)
            {
                _context.CriticalSection.Enter();

                currentThread.Owner.PinThread(currentThread);

                currentThread.SetUserInterruptFlag();

                _context.CriticalSection.Leave();
            }

            if (context.IsAarch32)
            {
                SyscallDispatch.Dispatch32(_context.Syscall, context, id);
            }
            else
            {
                SyscallDispatch.Dispatch64(_context.Syscall, context, id);
            }

            currentThread.HandlePostSyscall();
        }
    }
}
