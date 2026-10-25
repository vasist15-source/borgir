using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL.Multithreading.Model;
using Ryujinx.Graphics.GAL.Multithreading.Resources;

namespace Ryujinx.Graphics.GAL.Multithreading.Commands
{
    struct SetProgramCommand : IGALCommand, IGALCommand<SetProgramCommand>
    {
        public readonly CommandType CommandType => CommandType.SetProgram;
        private TableRef<IProgram> _program;

        public void Set(TableRef<IProgram> program)
        {
            _program = program;
        }

        public static void Run(ref SetProgramCommand command, ThreadedRenderer threaded, IRenderer renderer)
        {
            ThreadedProgram program = command._program.GetAs<ThreadedProgram>(threaded);

            long ledger = program.Compiled ? 0 : StallLedger.Begin();
            threaded.Programs.WaitForProgram(program);
            StallLedger.End(StallKind.RenderProgramWait, ledger);

            renderer.Pipeline.SetProgram(program.Base);
        }
    }
}
