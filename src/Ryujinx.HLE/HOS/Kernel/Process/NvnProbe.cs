using Ryujinx.Common.Logging;
using Ryujinx.HLE.Loaders.Elf;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Ryujinx.HLE.HOS.Kernel.Process
{
    /// <summary>
    /// Read-only inventory of how a game reaches NVN, Nintendo's graphics API, taken once
    /// when the application's main thread is about to start.
    ///
    /// Whether NVN calls can be intercepted cheaply depends on how the game gets at the
    /// functions: exported one by one from the SDK module, handed out through a single
    /// bootstrap entry point, or called through a table of function pointers the game fills
    /// at start-up (pfnc_nvn*). This lists every dynamic symbol with "nvn" in its name, per
    /// module, so the next step can be chosen from what the game actually does.
    ///
    /// Nothing is changed. RYUJINX_NVN_PROBE=1; writes /tmp/ryujinx-nvn-symbols.txt.
    /// </summary>
    static class NvnProbe
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("RYUJINX_NVN_PROBE") is "1" or "2";

        private const string ReportPath = "/tmp/ryujinx-nvn-symbols.txt";

        public static void Inventory(KProcess process)
        {
            try
            {
                var images = process.Debugger.GetLoadedImages();
                StringBuilder sb = new();
                int total = 0;

                foreach (HleProcessDebugger.Image image in images)
                {
                    ElfSymbol[] nvn = image.Symbols.Where(s => s.Name.Contains("nvn", StringComparison.OrdinalIgnoreCase)).ToArray();
                    int defined = nvn.Count(s => s.ShIdx != 0 && s.Value != 0);

                    sb.AppendLine($"module {image.Name} base 0x{image.BaseAddress:X} size 0x{image.Size:X}: {image.Symbols.Length} symbols, " +
                                  $"{nvn.Length} with \"nvn\" ({defined} defined, {nvn.Length - defined} imported)");

                    foreach (ElfSymbol s in nvn.OrderBy(s => s.Name, StringComparer.Ordinal))
                    {
                        bool isDefined = s.ShIdx != 0 && s.Value != 0;
                        sb.AppendLine(isDefined
                            ? $"  def  0x{image.BaseAddress + s.Value:X12} size {s.Size,6} {s.Type,-8} {s.Name}"
                            : $"  imp                            {s.Type,-8} {s.Name}");
                    }

                    total += nvn.Length;
                }

                File.WriteAllText(ReportPath, sb.ToString());

                // RYUJINX_DUMP_CODE=addr:len,... writes those guest bytes to /tmp/ryujinx-code-<addr>.bin.
                foreach (string item in (Environment.GetEnvironmentVariable("RYUJINX_DUMP_CODE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = item.Split(':');
                    ulong address = Convert.ToUInt64(parts[0], 16);
                    int length = Convert.ToInt32(parts[1], 16);
                    byte[] bytes = new byte[length];
                    process.CpuMemory.Read(address, bytes);
                    File.WriteAllBytes($"/tmp/ryujinx-code-{address:X}.bin", bytes);
                }
                Logger.Warning?.Print(LogClass.Loader, $"nvn-probe: {images.Count} modules, {total} symbols with \"nvn\" -> {ReportPath}");
            }
            catch (Exception e)
            {
                Logger.Warning?.Print(LogClass.Loader, $"nvn-probe: inventory failed: {e.Message}");
            }
        }
    }
}
