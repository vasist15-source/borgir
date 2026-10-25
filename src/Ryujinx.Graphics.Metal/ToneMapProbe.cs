using Ryujinx.Common.Logging;
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Minimal frame-level probe for the final guest tone-map pass. Unlike
    /// DrawRing, this records neither every draw nor buffer readbacks: it copies
    /// four GPU addresses that UpdateAndBind has already generated, once per
    /// watched frame. This keeps the diagnostic below the timing/noise level that
    /// made the generic draw ring turn the flicker race into an AGX crash.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class ToneMapProbe
    {
        private const string TriggerPath = "/tmp/ryujinx-tonemap-probe-dump";
        private const int Capacity = 1 << 8;
        private const int DumpFrameCount = 8;
        private const string DefaultWatch = "3ebc3a8f6b77cc8f";

        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_TONEMAP_PROBE") == "1";

        private static readonly string[] _watch =
            (Environment.GetEnvironmentVariable("RYUJINX_METAL_TONEMAP_WATCH") ?? DefaultWatch)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private struct Entry
        {
            public int Frame;
            public string Program;
            public ulong Cb1Address;
            public ulong Cb3Address;
            public ulong Tex8ResourceId;
            public ulong TexAResourceId;
        }

        private static readonly Entry[] _entries = new Entry[Capacity];
        private static int _frame;
        private static string _frameWall = string.Empty;

        public static bool IsWatched(string program)
        {
            return Enabled && program != null && Array.IndexOf(_watch, program) >= 0;
        }

        public static void Record(EncoderState state)
        {
            string program = state.RenderProgram?.DebugLabel;

            if (!IsWatched(program))
            {
                return;
            }

            ref Entry entry = ref _entries[_frame & (Capacity - 1)];
            entry.Frame = _frame;
            entry.Program = program;
            entry.Cb1Address = state.DrawRingCb1Address;
            entry.Cb3Address = state.DrawRingCb3Address;
            entry.Tex8ResourceId = state.DrawRingTex8ResourceId;
            entry.TexAResourceId = state.DrawRingTexAResourceId;
        }

        public static void OnPresent()
        {
            _frameWall = DateTime.Now.ToString("HH:mm:ss.fff");

            if (File.Exists(TriggerPath))
            {
                try
                {
                    File.Delete(TriggerPath);
                    Dump();
                }
                catch (IOException exception)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"tone-map probe trigger failed: {exception.Message}");
                }
            }

            _frame++;
        }

        private static void Dump()
        {
            string path = $"/tmp/ryujinx-tonemap-probe-{DateTime.Now:HHmmss-fff}.txt";

            try
            {
                StringBuilder text = new();
                text.AppendLine($"# tone-map probe dumped at frame {_frame} wall {_frameWall}");

                int start = Math.Max(0, _frame - DumpFrameCount + 1);

                for (int frame = start; frame <= _frame; frame++)
                {
                    ref Entry entry = ref _entries[frame & (Capacity - 1)];

                    if (entry.Frame != frame || entry.Program == null)
                    {
                        continue;
                    }

                    text.AppendLine(
                        $"f={entry.Frame} p={entry.Program} " +
                        $"c1addr={entry.Cb1Address:X16} c3addr={entry.Cb3Address:X16} " +
                        $"t8id={entry.Tex8ResourceId:X16} tAid={entry.TexAResourceId:X16}");
                }

                File.WriteAllText(path, text.ToString());
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"tone-map probe dumped: {path}");
            }
            catch (Exception exception)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"tone-map probe dump failed: {exception.Message}");
            }
        }
    }
}
