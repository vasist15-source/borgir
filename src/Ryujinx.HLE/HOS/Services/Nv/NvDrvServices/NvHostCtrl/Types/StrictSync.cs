namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrl
{
    /// <summary>
    /// RYUJINX_STRICT_SYNC=1 closes two fence early-release windows in the nvhost
    /// event path. Both let the guest believe a fence passed while queued GPU work
    /// still reads the memory the guest then rewrites; six instrumented captures of
    /// TOTK's single-frame washes all showed guest binding state from the wrong
    /// frame with the emulator's own resolution blameless, and these are the only
    /// paths that release the guest without GPU progress.
    /// </summary>
    static class StrictSync
    {
        public static readonly bool Enabled =
            System.Environment.GetEnvironmentVariable("RYUJINX_STRICT_SYNC") == "1";
    }
}
