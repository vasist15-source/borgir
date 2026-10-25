using Ryujinx.Common.Logging;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// GPU time attribution. While /tmp/ryujinx-metal-encoder-labels exists (checked once a frame)
    /// every render, compute and blit encoder is labelled with an id ("r1234.56": kind, frame,
    /// sequence in the window) and what it is for, so a Metal System Trace taken with xctrace can be
    /// joined back to the backend's own record of each render pass: its end reason, draw count and
    /// the programs it ran, with their draw counts. Removing the file writes the records to
    /// /tmp/ryujinx-metal-pass-records.txt. Off, it costs one File.Exists a frame.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class EncoderLabels
    {
        private const string HotFile = "/tmp/ryujinx-metal-encoder-labels";
        private const string RecordsPath = "/tmp/ryujinx-metal-pass-records.txt";

        public static bool Active { get; private set; }

        public static int Frame;

        private static int _sequence;
        private static readonly object _recordsLock = new();
        // Records are streamed as they come: holding them for one write when the window closes
        // stalled the render thread for 18 s on a 20 s window (xctrace takes that long to save),
        // and the process was closed before the write finished.
        private static StreamWriter _writer;
        private static long _written;

        public static void RefreshPerFrame(int frame)
        {
            Frame = frame;

            bool next;

            try
            {
                next = File.Exists(HotFile);
            }
            catch (IOException)
            {
                return;
            }

            if (next == Active)
            {
                return;
            }

            if (next)
            {
                lock (_recordsLock)
                {
                    try
                    {
                        _writer = new StreamWriter(RecordsPath, append: false) { AutoFlush = false };
                        _writer.WriteLine("# id\tend reason\tdraws\tprograms (label:draws)\tfragment stores");
                    }
                    catch (IOException)
                    {
                        _writer = null;
                    }

                    _written = 0;
                }

                _sequence = 0;
            }
            else
            {
                Flush();
            }

            Active = next;
            Logger.Info?.Print(LogClass.Gpu, $"encoder labels {(next ? "on" : "off")} at frame {frame}");
        }

        public static string NextId(char kind) => $"{kind}{Frame}.{++_sequence}";

        public static void Record(string line)
        {
            lock (_recordsLock)
            {
                _writer?.WriteLine(line);
                _written++;
            }
        }

        public static void Label(MTLRenderCommandEncoder encoder, string text)
        {
            NSString label = Make(text);
            encoder.Label = label;
            ObjcOwnership.Release(label.NativePtr);
        }

        public static void Label(MTLComputeCommandEncoder encoder, string text)
        {
            NSString label = Make(text);
            encoder.Label = label;
            ObjcOwnership.Release(label.NativePtr);
        }

        public static void Label(MTLBlitCommandEncoder encoder, string text)
        {
            NSString label = Make(text);
            encoder.Label = label;
            ObjcOwnership.Release(label.NativePtr);
        }

        /// <summary>The first method outside the encoder plumbing that asked for this encoder.</summary>
        public static string Caller()
        {
            System.Diagnostics.StackTrace trace = new(2, false);

            for (int i = 0; i < trace.FrameCount; i++)
            {
                System.Reflection.MethodBase method = trace.GetFrame(i)?.GetMethod();
                string type = method?.DeclaringType?.Name;

                if (type != null && type != nameof(CommandBufferEncoder) && type != nameof(EncoderLabels) &&
                    !method.Name.StartsWith("GetOrCreate", StringComparison.Ordinal) &&
                    !method.Name.StartsWith("Begin", StringComparison.Ordinal))
                {
                    return $"{type}.{method.Name}";
                }
            }

            return "?";
        }

        public static string DescribeTargets(Texture[] colors, Texture depth)
        {
            StringBuilder sb = new();

            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i] is Texture t)
                {
                    sb.Append($" c{i}:{t.Width}x{t.Height}/{t.MtlFormat}");
                }
            }

            if (depth != null)
            {
                sb.Append($" d:{depth.Width}x{depth.Height}/{depth.MtlFormat}");
            }

            return sb.ToString();
        }

        private static NSString Make(string text)
        {
            IntPtr allocated = ObjectiveC.IntPtr_objc_msgSend(new ObjectiveCClass("NSString"), "alloc");

            return new NSString(ObjectiveC.IntPtr_objc_msgSend(allocated, "initWithUTF8String:", text));
        }

        private static void Flush()
        {
            lock (_recordsLock)
            {
                try
                {
                    _writer?.WriteLine("# end");
                    _writer?.Dispose();
                    Logger.Info?.Print(LogClass.Gpu, $"encoder labels: {_written} pass records -> {RecordsPath}");
                }
                catch (Exception e)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"encoder labels: closing the records failed: {e.GetType().Name}: {e.Message}");
                }

                _writer = null;
            }
        }
    }
}
