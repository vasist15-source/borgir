namespace Ryujinx.Graphics.GAL.Multithreading.Commands.Renderer
{
    struct CreateSyncCommand : IGALCommand, IGALCommand<CreateSyncCommand>
    {
        public readonly CommandType CommandType => CommandType.CreateSync;
        private ulong _id;
        private bool _strict;
        private HostSyncCreateSource _source;

        public void Set(ulong id, bool strict, HostSyncCreateSource source)
        {
            _id = id;
            _strict = strict;
            _source = source;
        }

        public static void Run(ref CreateSyncCommand command, ThreadedRenderer threaded, IRenderer renderer)
        {
            renderer.CreateSync(command._id, command._strict, command._source);

            threaded.Sync.AssignSync(command._id);
        }
    }
}
