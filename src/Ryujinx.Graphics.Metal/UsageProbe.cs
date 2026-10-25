using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Read-only census for the lossless-compression question.
    ///
    /// Every non-depth, non-block-compressed colour texture is created with ShaderWrite and
    /// PixelFormatView, and Apple GPUs disable lossless compression for either flag, so the
    /// store/load round trip of every split render pass moves uncompressed data. Dropping the
    /// flags is only safe for a texture that is never bound as a storage image and never
    /// viewed in a pixel format other than its own or its sRGB twin (Apple does not require
    /// PixelFormatView for sRGB-only or swizzle-only views). This counts, per root texture,
    /// how often each of those actually happens, and how often the texture is a colour
    /// attachment - the textures whose round trips compression would make cheaper.
    ///
    /// Nothing here changes behaviour: it only increments counters on paths that already
    /// run. Default off, RYUJINX_METAL_USAGE_PROBE=1. Every 1800 presented frames it writes
    /// /tmp/ryujinx-usage-probe.txt and one summary line to the log.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class UsageProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_USAGE_PROBE") == "1";

        private const int ReportEveryFrames = 1800;
        private const string ReportPath = "/tmp/ryujinx-usage-probe.txt";

        public sealed class Root
        {
            public MTLPixelFormat Format;
            public int Width;
            public int Height;
            public int Samples;
            public bool Candidate;

            public long RenderTargetBinds;
            public long StorageBinds;
            public long ViewsSame;
            public long ViewsSrgb;
            public long ViewsSwizzle;
            public long ViewsOther;
            public HashSet<MTLPixelFormat> OtherFormats;
        }

        private static readonly object _lock = new();
        private static readonly List<Root> _roots = new();
        private static long _frame;

        public static Root NoteRoot(TextureCreateInfo info, MTLPixelFormat format)
        {
            Root root = new()
            {
                Format = format,
                Width = info.Width,
                Height = info.Height,
                Samples = info.Samples,
                Candidate = !info.Format.IsDepthOrStencil && !info.IsCompressed,
            };

            lock (_lock)
            {
                _roots.Add(root);
            }

            return root;
        }

        public static void NoteView(Root root, MTLPixelFormat viewFormat, bool swizzled)
        {
            if (root == null)
            {
                return;
            }

            if (viewFormat == root.Format)
            {
                if (swizzled)
                {
                    Interlocked.Increment(ref root.ViewsSwizzle);
                }
                else
                {
                    Interlocked.Increment(ref root.ViewsSame);
                }
            }
            else if (IsSrgbTwin(root.Format, viewFormat))
            {
                Interlocked.Increment(ref root.ViewsSrgb);
            }
            else
            {
                Interlocked.Increment(ref root.ViewsOther);

                lock (_lock)
                {
                    (root.OtherFormats ??= new HashSet<MTLPixelFormat>()).Add(viewFormat);
                }
            }
        }

        // Totals seen by the hooks themselves, rootless ones included, so a census that
        // reports "nothing is storage-bound" can be told apart from a hook that never fired.
        private static long _storageSeen;
        private static long _attachmentSeen;
        private static long _rootless;

        public static void NoteStorage(Root root)
        {
            Interlocked.Increment(ref _storageSeen);

            if (root != null)
            {
                Interlocked.Increment(ref root.StorageBinds);
            }
            else
            {
                Interlocked.Increment(ref _rootless);
            }
        }

        public static void NoteRenderTarget(Root root)
        {
            Interlocked.Increment(ref _attachmentSeen);

            if (root != null)
            {
                Interlocked.Increment(ref root.RenderTargetBinds);
            }
            else
            {
                Interlocked.Increment(ref _rootless);
            }
        }

        public static void OnPresent()
        {
            if (!Enabled || ++_frame % ReportEveryFrames != 0)
            {
                return;
            }

            Root[] roots;

            lock (_lock)
            {
                roots = _roots.Where(r => r.Candidate).ToArray();
            }

            Report(roots);
        }

        internal static bool IsSrgbTwin(MTLPixelFormat a, MTLPixelFormat b)
        {
            string na = a.ToString();
            string nb = b.ToString();

            return na != nb && na.Replace("sRGB", "") == nb.Replace("sRGB", "");
        }

        private static bool NeedsShaderWrite(Root r) => r.StorageBinds > 0;

        private static bool NeedsFormatView(Root r) => r.ViewsOther > 0;

        private static bool NeedsNeither(Root r) => !NeedsShaderWrite(r) && !NeedsFormatView(r);

        private static void Report(Root[] roots)
        {
            Root[] rt = roots.Where(r => r.RenderTargetBinds > 0).ToArray();

            long rtBinds = rt.Sum(r => r.RenderTargetBinds);
            long leanBinds = rt.Where(NeedsNeither).Sum(r => r.RenderTargetBinds);

            string summary =
                $"usage-probe f={_frame}: {roots.Length} colour roots, {rt.Length} ever a colour attachment; " +
                $"of those {rt.Count(NeedsShaderWrite)} storage-bound, {rt.Count(NeedsFormatView)} with other-format views, " +
                $"{rt.Count(NeedsNeither)} need neither = {Percent(leanBinds, rtBinds)} of {rtBinds} attachment binds " +
                $"(hooks saw {_attachmentSeen} attachment and {_storageSeen} storage binds, {_rootless} rootless)";

            StringBuilder sb = new();
            sb.AppendLine(summary);
            sb.AppendLine();
            sb.AppendLine("Colour-attachment classes by attachment binds (class = size + format):");
            sb.AppendLine("      binds  roots  storage roots/binds  views same/srgb/swizzle/other  other view formats              class");

            foreach (IGrouping<string, Root> g in rt.GroupBy(ClassKey).OrderByDescending(g => g.Sum(r => r.RenderTargetBinds)))
            {
                AppendClass(sb, g);
            }

            sb.AppendLine();
            sb.AppendLine("Storage-image classes that were never a colour attachment:");

            foreach (IGrouping<string, Root> g in roots.Where(r => r.RenderTargetBinds == 0 && r.StorageBinds > 0)
                         .GroupBy(ClassKey).OrderByDescending(g => g.Sum(r => r.StorageBinds)))
            {
                AppendClass(sb, g);
            }

            try
            {
                File.WriteAllText(ReportPath, sb.ToString());
            }
            catch (IOException)
            {
                // The log line below still carries the headline.
            }

            Logger.Warning?.Print(LogClass.Gpu, summary + $" -> {ReportPath}");
        }

        private static void AppendClass(StringBuilder sb, IGrouping<string, Root> g)
        {
            string others;

            lock (_lock)
            {
                others = string.Join(",", g.Where(r => r.OtherFormats != null)
                    .SelectMany(r => r.OtherFormats).Distinct().Take(4));
            }

            string views = $"{g.Sum(r => r.ViewsSame)}/{g.Sum(r => r.ViewsSrgb)}/{g.Sum(r => r.ViewsSwizzle)}/{g.Sum(r => r.ViewsOther)}";

            sb.AppendLine(
                $"  {g.Sum(r => r.RenderTargetBinds),9}  {g.Count(),5}  {g.Count(NeedsShaderWrite),6}/{g.Sum(r => r.StorageBinds),-12}  " +
                $"{views,-29}  {(others.Length == 0 ? "-" : others),-30}  {g.Key}");
        }

        private static string ClassKey(Root r) =>
            $"{r.Width}x{r.Height} {r.Format}" + (r.Samples > 1 ? $" x{r.Samples}" : "");

        private static string Percent(long part, long whole) =>
            whole == 0 ? "n/a" : $"{100.0 * part / whole:0.0}%";
    }
}
