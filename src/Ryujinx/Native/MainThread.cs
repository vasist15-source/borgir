using Ryujinx.Common.Logging;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Ava.Native
{
    /// <summary>
    /// Queues work on the process' main thread, which the native UI's run loop owns.
    /// </summary>
    internal static unsafe partial class MainThread
    {
        private const string LibSystem = "/usr/lib/libSystem.B.dylib";

        [LibraryImport(LibSystem)]
        private static partial void dispatch_async_f(nint queue, nint context, delegate* unmanaged<nint, void> work);

        // dispatch_get_main_queue() is a macro for &_dispatch_main_q.
        private static readonly nint _mainQueue = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_dispatch_main_q");

        public static void Post(Action action)
        {
            GCHandle handle = GCHandle.Alloc(action);

            dispatch_async_f(_mainQueue, GCHandle.ToIntPtr(handle), &Invoke);
        }

        [UnmanagedCallersOnly]
        private static void Invoke(nint context)
        {
            GCHandle handle = GCHandle.FromIntPtr(context);
            Action action = (Action)handle.Target;

            handle.Free();

            try
            {
                action();
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Main thread work failed: {ex}");
            }
        }
    }
}
