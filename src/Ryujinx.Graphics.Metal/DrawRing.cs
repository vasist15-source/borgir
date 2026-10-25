using Ryujinx.Common.Logging;
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Rolling in-memory record of recent draws, for catching single frame artefacts
    /// in the act. Enabled by RYUJINX_METAL_DRAW_RING=1; records are appended with no
    /// allocation and no I/O (per frame logging is known to perturb the very races
    /// under study), and the whole ring is dumped to a file when the trigger file
    /// appears - an external watcher touches it the moment it sees a bad frame, and
    /// the ring still holds that frame's draws.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class DrawRing
    {
        private const string TriggerPath = "/tmp/ryujinx-draw-ring-dump";
        private const int Capacity = 1 << 15;
        private const int DumpDrawCount = 1 << 13;
        private const int DumpFrameCount = 5;

        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_DRAW_RING") == "1";

        // Fragment programs whose constant buffer heads are captured per draw.
        private static readonly string[] _watch =
            (Environment.GetEnvironmentVariable("RYUJINX_METAL_RING_WATCH") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private struct Entry
        {
            public ulong Seq;
            public int Frame;
            public string Program;
            public IntPtr Rt0;
            public int RtW;
            public int RtH;
            public byte Topology;
            public int Count;
            public int Instances;
            public ulong Cb1Address;
            public ulong Cb3Address;
            public ulong WatchTex8ResourceId;
            public ulong WatchTexAResourceId;
            public bool Watched;
        }

        private struct ReadbackEntry
        {
            public int Frame;
            public int Offset;
            public int Size;
            public ulong Head;
        }

        private static readonly ReadbackEntry[] _readbacks = new ReadbackEntry[Capacity];
        private static int _rbCursor;

        private static readonly Entry[] _entries = new Entry[Capacity];
        private static int _cursor;
        private static int _frame;
        private static string _frameWall = string.Empty;

        public static bool IsWatched(string program)
        {
            return Enabled && _watch.Length != 0 && program != null && Array.IndexOf(_watch, program) >= 0;
        }

        public static void OnPresent()
        {
            _frame++;
            _frameWall = DateTime.Now.ToString("HH:mm:ss.fff");

            if (File.Exists(TriggerPath))
            {
                try
                {
                    File.Delete(TriggerPath);
                }
                catch (IOException)
                {
                    return;
                }

                Dump();
            }
        }

        /// <summary>
        /// Records a guest buffer readback: the game drives per frame CPU decisions
        /// (exposure, visibility, paging) off these, so one stale result explains one
        /// broken frame. Head is the first 8 bytes of the returned data.
        /// </summary>
        public static void RecordReadback(int offset, int size, ulong head)
        {
            ref ReadbackEntry entry = ref _readbacks[_rbCursor & (Capacity - 1)];
            _rbCursor++;

            entry.Frame = _frame;
            entry.Offset = offset;
            entry.Size = size;
            entry.Head = head;
        }

        public static void Record(EncoderState state, Ryujinx.Graphics.GAL.PrimitiveTopology topology, int count, int instances)
        {
            Program program = state.RenderProgram;
            Texture rt0 = state.RenderTargets[0];

            ref Entry entry = ref _entries[_cursor & (Capacity - 1)];
            _cursor++;

            entry.Seq = Ryujinx.Graphics.GAL.DrawDiagnostics.BindSequence;
            entry.Frame = _frame;
            entry.Program = program?.DebugLabel;
            // Do not call GetHandle from diagnostics. The render pass already owns
            // the target, and touching Metal objects here perturbs the lifetime race
            // we are trying to observe.
            entry.Rt0 = IntPtr.Zero;
            entry.RtW = rt0?.Width ?? 0;
            entry.RtH = rt0?.Height ?? 0;
            entry.Topology = (byte)topology;
            entry.Count = count;
            entry.Instances = instances;
            entry.Watched = false;

            if (IsWatched(entry.Program))
            {
                entry.Watched = true;
                entry.Cb1Address = state.DrawRingCb1Address;
                entry.Cb3Address = state.DrawRingCb3Address;
                entry.WatchTex8ResourceId = state.DrawRingTex8ResourceId;
                entry.WatchTexAResourceId = state.DrawRingTexAResourceId;
            }
        }

        private static void Dump()
        {
            string path = $"/tmp/ryujinx-draw-ring-{DateTime.Now:HHmmss-fff}.txt";

            try
            {
                StringBuilder text = new();

                text.AppendLine($"# dumped at frame {_frame} wall {_frameWall}");

                int rbStart = Math.Max(0, _rbCursor - Capacity);

                for (int i = rbStart; i < _rbCursor; i++)
                {
                    ref ReadbackEntry rb = ref _readbacks[i & (Capacity - 1)];

                    if (rb.Frame < _frame - DumpFrameCount)
                    {
                        continue;
                    }

                    text.AppendLine($"rb f={rb.Frame} off=0x{rb.Offset:X} size={rb.Size} head={rb.Head:X16}");
                }

                int start = Math.Max(0, _cursor - DumpDrawCount);

                for (int i = start; i < _cursor; i++)
                {
                    ref Entry entry = ref _entries[i & (Capacity - 1)];

                    text.Append($"f={entry.Frame} s={entry.Seq} p={entry.Program} rt=0x{entry.Rt0:X}/{entry.RtW}x{entry.RtH} " +
                        $"topo={entry.Topology} n={entry.Count} inst={entry.Instances}");

                    if (entry.Watched)
                    {
                        text.Append($" c1addr={entry.Cb1Address:X16} c3addr={entry.Cb3Address:X16}" +
                            $" t8id={entry.WatchTex8ResourceId:X16} tAid={entry.WatchTexAResourceId:X16}");
                    }

                    text.AppendLine();
                }

                File.WriteAllText(path, text.ToString());

                Logger.Warning?.PrintMsg(LogClass.Gpu, $"draw ring dumped: {path}");
            }
            catch (Exception exception)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"draw ring dump failed: {exception.Message}");
            }
        }
    }
}
