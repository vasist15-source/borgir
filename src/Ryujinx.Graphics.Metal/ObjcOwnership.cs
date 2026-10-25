using SharpMetal.ObjectiveCCore;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Explicit ownership for autoreleased Objective-C objects. The render thread is
    /// a plain managed thread with no draining autorelease pool, so every factory
    /// return that follows the autorelease convention (command buffers, pass
    /// encoders, drawables) leaks its +0 reference permanently - measured at 15.7KB
    /// per render encoder and 2.6KB per command buffer of phys_footprint, which at
    /// hundreds of passes per frame is GB/min. Every Retain here must be paired with
    /// exactly one Release on the same object.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class ObjcOwnership
    {
        public static void Retain(IntPtr ptr)
        {
            if (ptr != IntPtr.Zero)
            {
                ObjectiveC.IntPtr_objc_msgSend(ptr, "retain");
            }
        }

        public static void Release(IntPtr ptr)
        {
            if (ptr != IntPtr.Zero)
            {
                ObjectiveC.IntPtr_objc_msgSend(ptr, "release");
            }
        }

        // Explicit ownership pairs cannot recover the +1 that autorelease itself
        // parked in the (never-drained) pool, so the render thread needs a real
        // pool drained once per frame: pop-and-push anchored at Present.
        [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")]
        public static extern IntPtr objc_autoreleasePoolPush();

        [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")]
        public static extern void objc_autoreleasePoolPop(IntPtr pool);
    }
}
