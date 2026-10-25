using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Counts draws that sample a texture which is simultaneously one of their own colour
    /// attachments. Metal leaves that undefined, and undefined reads on this hardware
    /// return near-white - the flash's exact value - while no write occurs, which is why
    /// every write-side hook in these notes came back empty.
    ///
    /// Reports distinct (attachment index, size, format) combinations once each, plus a
    /// running total, so a real occurrence is unmistakable and a clean run is a genuine
    /// negative rather than a silent one.
    ///
    /// RYUJINX_METAL_FEEDBACK=1.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class FeedbackProbe
    {
        // Detection runs whenever either the report or the fix is wanted.
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_FEEDBACK") != "0";

        // Hot-swappable so both arms run in one session against the same save.
        private static bool _fixLive;

        public static void RefreshToggle()
        {
            try
            {
                _fixLive = System.IO.File.Exists("/tmp/ryujinx-metal-feedback-fix") &&
                    System.IO.File.ReadAllText("/tmp/ryujinx-metal-feedback-fix").Trim() == "1";
            }
            catch (System.IO.IOException)
            {
            }
        }

        public static bool FixLive => _fixLive;

        private static readonly bool _report =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_FEEDBACK") == "1";

        /// <summary>
        /// Treat a detected feedback as an implicit texture barrier: if the pass has
        /// already encoded draws, end it so the sampled content is finished writes rather
        /// than undefined. Guest code written against an API that permits this without an
        /// explicit barrier gets undefined data on Metal otherwise.
        /// OFF by default: splitting from inside GetOrCreateRenderEncoder, which is itself
        /// half way through acquiring an encoder, faults the driver in
        /// drawIndexedPrimitives with the same signature as the encoder-ABA bug fixed at
        /// the start of this investigation. A correct version has to decide before the
        /// prepass runs, from bound state alone. It also measured no effect on the flash
        /// when detection was over-broad. RYUJINX_METAL_FEEDBACK_FIX=1 opts in.
        /// </summary>
        public static readonly bool Fix =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_FEEDBACK_FIX") == "1";

        [ThreadStatic]
        private static bool _detected;

        public static void Detect() => _detected = true;

        public static bool TakeDetected()
        {
            bool value = _detected;
            _detected = false;

            return value;
        }

        private static readonly HashSet<string> _seen = [];
        private static long _total;

        // The live pass's attachments, snapshotted as its descriptor is built.
        private const int MaxAttachments = 8;

        [ThreadStatic] private static IntPtr[] _liveRoot;
        [ThreadStatic] private static int[] _liveLevel;
        [ThreadStatic] private static int[] _liveLayer;

        public static void BeginPass()
        {
            _liveRoot ??= new IntPtr[MaxAttachments];
            _liveLevel ??= new int[MaxAttachments];
            _liveLayer ??= new int[MaxAttachments];

            Array.Clear(_liveRoot);
        }

        public static void NoteAttachment(int index, Texture target)
        {
            if (!Enabled || index >= MaxAttachments || target == null)
            {
                return;
            }

            _liveRoot ??= new IntPtr[MaxAttachments];
            _liveLevel ??= new int[MaxAttachments];
            _liveLayer ??= new int[MaxAttachments];

            _liveRoot[index] = target.CanonicalPtr != IntPtr.Zero ? target.CanonicalPtr : target.GetHandle().NativePtr;
            _liveLevel[index] = target.FirstLevel;
            _liveLayer[index] = target.FirstLayer;
        }

        /// <summary>
        /// True feedback: same storage, same level, same layer, and the target is an
        /// attachment of the pass that is actually open - not one left in the state from
        /// an earlier pass, which is what two previous versions of this counted.
        /// </summary>
        public static bool CheckAgainstLiveAttachments(Texture sampled)
        {
            if (!Enabled || _liveRoot == null || sampled == null)
            {
                return false;
            }

            IntPtr sampledRoot = sampled.CanonicalPtr != IntPtr.Zero ? sampled.CanonicalPtr : sampled.GetHandle().NativePtr;

            for (int i = 0; i < MaxAttachments; i++)
            {
                if (_liveRoot[i] == sampledRoot &&
                    _liveLevel[i] == sampled.FirstLevel &&
                    _liveLayer[i] == sampled.FirstLayer)
                {
                    Note(i, sampled.Width, sampled.Height, sampled.MtlFormat.ToString(), sampledRoot);

                    return true;
                }
            }

            return false;
        }

        public static void Note(int attachmentIndex, int width, int height, string format, IntPtr root)
        {
            _detected = true;
            _total++;

            if (!_report)
            {
                return;
            }

            string key = $"{attachmentIndex}:{width}x{height}:{format}";

            lock (_seen)
            {
                if (!_seen.Add(key) && (_total % 5000) != 0)
                {
                    return;
                }
            }

            Logger.Warning?.PrintMsg(LogClass.Gpu,
                $"FEEDBACK: sampling attachment {attachmentIndex} {width}x{height} {format} " +
                $"root=0x{root:X} (total {_total})");
        }
    }
}
