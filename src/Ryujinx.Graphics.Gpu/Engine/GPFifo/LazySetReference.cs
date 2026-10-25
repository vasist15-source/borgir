using Ryujinx.Common.Logging;
using System;
using System.IO;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.GPFifo
{
    /// <summary>
    /// A/B switch: on Metal, make the host sync behind SetReference lazy like WaitForIdle's
    /// (attached to the current command buffer and flushed only when something waits on it)
    /// instead of strict (submitted at once). A strict SetReference commits mid-pass, so every
    /// one costs a render pass split; few of them are ever waited on.
    ///
    /// RYUJINX_METAL_LAZY_SETREF=1 sets the default; /tmp/ryujinx-metal-lazy-setref (1 or 0)
    /// overrides it and is re-read twice a second, so one session can alternate both.
    /// </summary>
    static class LazySetReference
    {
        private const string HotFile = "/tmp/ryujinx-metal-lazy-setref";

        private static readonly bool _default = Environment.GetEnvironmentVariable("RYUJINX_METAL_LAZY_SETREF") == "1";
        private static volatile bool _active = _default;
        private static int _started;

        public static bool Active
        {
            get
            {
                if (Interlocked.Exchange(ref _started, 1) == 0)
                {
                    new Thread(Poll) { Name = "LazySetReference", IsBackground = true }.Start();
                }

                return _active;
            }
        }

        private static void Poll()
        {
            while (true)
            {
                bool next = _default;

                try
                {
                    if (File.Exists(HotFile))
                    {
                        next = File.ReadAllText(HotFile).Trim() == "1";
                    }
                }
                catch (IOException)
                {
                    next = _active;
                }

                if (next != _active)
                {
                    _active = next;
                    Logger.Info?.Print(LogClass.Gpu, $"lazy SetReference: {(next ? "on" : "off")}");
                }

                Thread.Sleep(500);
            }
        }
    }
}
