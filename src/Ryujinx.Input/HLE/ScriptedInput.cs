using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Hid;
using System;
using System.Diagnostics;
using System.IO;

namespace Ryujinx.Input.HLE
{
    /// <summary>
    /// Button presses for unattended runs, delivered through a file rather than the window
    /// system: nothing needs focus, and nothing can reach another application the way
    /// synthesized system-wide key events do when the emulator is not in front.
    ///
    /// RYUJINX_SCRIPTED_INPUT=1. Writing ControllerKeys names ("A", "A+B", "DpadDown") and
    /// an optional hold time in milliseconds ("A 300") to /tmp/ryujinx-press holds those
    /// buttons on player 1 for that long, 150 ms by default. The file is deleted once read.
    /// </summary>
    static class ScriptedInput
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("RYUJINX_SCRIPTED_INPUT") == "1";

        private const string TriggerPath = "/tmp/ryujinx-press";
        private const int PollIntervalMs = 50;
        private const int DefaultHoldMs = 150;

        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static long _nextPollMs;
        private static long _releaseMs;
        private static ControllerKeys _held;

        /// <summary>The scripted buttons held right now; called once per input update.</summary>
        public static ControllerKeys Current()
        {
            long now = _clock.ElapsedMilliseconds;

            if (now >= _nextPollMs)
            {
                _nextPollMs = now + PollIntervalMs;

                if (File.Exists(TriggerPath))
                {
                    Trigger(now);
                }
            }

            return now < _releaseMs ? _held : 0;
        }

        private static void Trigger(long now)
        {
            string text;

            try
            {
                text = File.ReadAllText(TriggerPath);
                File.Delete(TriggerPath);
            }
            catch (IOException)
            {
                return; // Still being written; the next poll picks it up.
            }

            ControllerKeys keys = 0;
            int holdMs = DefaultHoldMs;

            foreach (string token in text.Split([' ', '+', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, out int ms))
                {
                    holdMs = Math.Clamp(ms, 16, 10000);
                }
                else if (Enum.TryParse(token, true, out ControllerKeys key))
                {
                    keys |= key;
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Hid, $"scripted input: unknown button \"{token}\"");
                }
            }

            _held = keys;
            _releaseMs = now + holdMs;

            Logger.Info?.Print(LogClass.Hid, $"scripted input: {keys} for {holdMs} ms");
        }
    }
}
