using SharpMetal.Metal;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal.SharpMetalExtensions
{
    [SupportedOSPlatform("macOS")]
    public static class MTLCommandBufferExtensions
    {
        private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjCLibrary)]
        private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);

        /// <summary>
        /// arm64 returns a double in d0 and objc_msgSend is the right entry point for it -
        /// objc_msgSend_fpret does not exist on this architecture.
        ///
        /// SharpMetal preview21's GPUStartTime/GPUEndTime properties read the integer return
        /// register instead, so every command buffer reports start == end == its own address.
        /// A standalone probe returned 4302804208 from the property for a buffer at
        /// 0x1007794F0 - the pointer itself - while this call returned 1256768.305676 against
        /// a Stopwatch reading of 1256768.307544 taken immediately after, which also confirms
        /// the two clocks share the mach_absolute_time base.
        /// </summary>
        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static extern double DoubleMsgSend(IntPtr receiver, IntPtr selector);

        private static readonly IntPtr SelGpuStartTime = sel_registerName("GPUStartTime");
        private static readonly IntPtr SelGpuEndTime = sel_registerName("GPUEndTime");

        /// <summary>
        /// When the GPU began executing this command buffer, in seconds on the same clock
        /// Stopwatch reads. Only defined once the buffer has completed.
        /// </summary>
        public static double GetGpuStartTime(this MTLCommandBuffer commandBuffer)
            => DoubleMsgSend(commandBuffer.NativePtr, SelGpuStartTime);

        /// <summary>
        /// When the GPU finished executing this command buffer. Only defined once the buffer
        /// has completed.
        /// </summary>
        public static double GetGpuEndTime(this MTLCommandBuffer commandBuffer)
            => DoubleMsgSend(commandBuffer.NativePtr, SelGpuEndTime);
    }
}
