using System;
using System.Runtime.Versioning;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// A ring of every command-stream operation, so the interval between two chart
    /// samples can be dumped when the watched texture flips varied -> uniform.
    ///
    /// The plan this serves (handoff doc, "The localisation plan"): the flip is bounded
    /// by two zero-draw passes at the tail of frame N-1, and every hooked write path
    /// reports zero inside that window. Dumping the actual operation slice replaces
    /// path-by-path hypothesis testing: the writer is either in the slice - named
    /// directly - or absent, which confirms a CPU-side write to shared texture memory,
    /// the branch Phase 0 established as physically open.
    ///
    /// Recording is a few array writes per operation, gated on the probe being armed.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class OpRing
    {
        private const int Size = 1 << 13; // ~3 frames at ~300 ops/frame

        private struct Entry
        {
            public int Frame;
            public byte Kind; // 1 pass, 2 blit tex->tex, 3 buf->tex, 4 setdata, 5 dispatch, 6 tex->buf
            public IntPtr A;
            public IntPtr B;
            public string Label;
        }

        private static readonly Entry[] _ring = new Entry[Size];
        private static long _seq;
        private static int _frame;

        internal static long Seq => _seq;

        public static void OnPresent()
        {
            _frame++;
        }

        private static void Note(byte kind, IntPtr a, IntPtr b, string label = null)
        {
            if (!PresentProbe.Enabled)
            {
                return;
            }

            long s = _seq++;
            ref Entry e = ref _ring[s & (Size - 1)];
            e.Frame = _frame;
            e.Kind = kind;
            e.A = a;
            e.B = b;
            e.Label = label;
        }

        public static void NotePass(IntPtr rt0, IntPtr depth) => Note(1, rt0, depth);
        public static void NoteTexCopy(IntPtr src, IntPtr dst) => Note(2, src, dst);
        public static void NoteBufToTex(IntPtr dst) => Note(3, IntPtr.Zero, dst);
        public static void NoteSetData(IntPtr dst) => Note(4, IntPtr.Zero, dst);
        public static void NoteDispatch(string label) => Note(5, IntPtr.Zero, IntPtr.Zero, label);
        public static void NoteTexToBuf(IntPtr src) => Note(6, src, IntPtr.Zero);

        private static string KindName(byte k) => k switch
        {
            1 => "pass",
            2 => "tex->tex",
            3 => "buf->tex",
            4 => "setdata",
            5 => "dispatch",
            6 => "tex->buf",
            _ => "?",
        };

        /// <summary>
        /// The operations between two recorded sequence points, oldest first. Entries the
        /// ring has already overwritten report as lost rather than silently missing -
        /// the cap rule from the probe notes.
        /// </summary>
        public static string Describe(long fromSeq, long toSeq)
        {
            if (toSeq <= fromSeq)
            {
                return "empty";
            }

            StringBuilder sb = new();

            long lost = (_seq - Size) - fromSeq;

            if (lost > 0)
            {
                sb.Append($" ({lost} overwritten)");
                fromSeq = _seq - Size;
            }

            for (long s = fromSeq; s < toSeq && sb.Length < 1800; s++)
            {
                ref Entry e = ref _ring[s & (Size - 1)];
                sb.Append($" [{s}]{KindName(e.Kind)}");

                if (e.A != IntPtr.Zero)
                {
                    sb.Append($":0x{e.A:X}");
                }

                if (e.B != IntPtr.Zero)
                {
                    sb.Append($"->0x{e.B:X}");
                }

                if (e.Label != null)
                {
                    sb.Append($"({e.Label})");
                }
            }

            return sb.Length == 0 ? "none" : sb.ToString();
        }
    }
}
