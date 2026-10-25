using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Common.SystemInterop
{
    /// <summary>
    /// Marks the threads a frame waits on (guest CPU cores, GPU command processing, the render backend, audio) as
    /// user-interactive work, so macOS keeps them on performance cores. At the default class the scheduler moves them to
    /// efficiency cores, which run them several times slower: a frame then misses its deadline. RYUJINX_THREAD_QOS=0
    /// leaves them at the default class, for comparison.
    /// </summary>
    public static partial class ThreadQos
    {
        private const int QosClassUserInteractive = 0x21;

        private static readonly bool _enabled = OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("RYUJINX_THREAD_QOS") != "0";

        [LibraryImport("/usr/lib/libSystem.B.dylib")]
        private static partial int pthread_set_qos_class_self_np(int qosClass, int relativePriority);

        /// <summary>
        /// Raises the calling thread to the user-interactive class.
        /// </summary>
        public static void MarkTimeCritical()
        {
            if (_enabled)
            {
                pthread_set_qos_class_self_np(QosClassUserInteractive, 0);
            }
        }
    }
}
