using System;

namespace Ryujinx.Memory.Tracking
{
    /// <summary>
    /// The guest instruction behind the memory-tracking event being handled on this thread,
    /// for diagnostics that need to know which guest code wrote a tracked region.
    /// Set by CPU backends that know it (Apple Hypervisor data aborts); 0 otherwise.
    /// </summary>
    public static class GuestFaultContext
    {
        [ThreadStatic]
        public static ulong Pc;

        [ThreadStatic]
        public static ulong Address;
    }
}
