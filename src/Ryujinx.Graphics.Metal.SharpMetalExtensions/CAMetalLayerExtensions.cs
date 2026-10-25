using SharpMetal;
using SharpMetal.Foundation;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
// ReSharper disable InconsistentNaming

namespace Ryujinx.Graphics.Metal.SharpMetalExtensions
{
    [SupportedOSPlatform("macOS")]
    public static class CAMetalLayerExtensions
    {
        private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string CoreGraphicsFramework = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        private const uint CFStringEncodingUTF8 = 0x08000100;

        private static readonly Selector sel_developerHUDProperties = "developerHUDProperties";
        private static readonly Selector sel_setDeveloperHUDProperties = "setDeveloperHUDProperties:";
        private static readonly Selector sel_colorspace = "colorspace";
        private static readonly Selector sel_setColorspace = "setColorspace:";

        [DllImport(CoreFoundationFramework)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string cString, uint encoding);

        [DllImport(CoreFoundationFramework)]
        private static extern void CFRelease(IntPtr cf);

        [DllImport(CoreGraphicsFramework)]
        private static extern IntPtr CGColorSpaceCreateWithName(IntPtr name);

        public static NSDictionary GetDeveloperHudProperties(this CAMetalLayer metalLayer)
            => new(ObjectiveCRuntime.IntPtr_objc_msgSend(metalLayer.NativePtr, sel_developerHUDProperties));

        public static void SetDeveloperHudProperties(this CAMetalLayer metalLayer, NSDictionary dictionary)
            => ObjectiveCRuntime.objc_msgSend(metalLayer.NativePtr, sel_setDeveloperHUDProperties, dictionary);

        public static IntPtr GetColorspace(this CAMetalLayer metalLayer)
            => ObjectiveCRuntime.IntPtr_objc_msgSend(metalLayer.NativePtr, sel_colorspace);

        public static void SetColorspace(this CAMetalLayer metalLayer, IntPtr cgColorSpace)
            => ObjectiveCRuntime.objc_msgSend(metalLayer.NativePtr, sel_setColorspace, cgColorSpace);

        /// <summary>
        /// Creates a CGColorSpace from one of the well-known CoreGraphics color space
        /// names (for example "kCGColorSpaceSRGB"). Returns IntPtr.Zero on failure.
        /// The returned object is owned by the caller and is intended to be cached
        /// for the process lifetime.
        /// </summary>
        public static IntPtr CreateNamedColorSpace(string name)
        {
            IntPtr cfName = CFStringCreateWithCString(IntPtr.Zero, name, CFStringEncodingUTF8);

            if (cfName == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                return CGColorSpaceCreateWithName(cfName);
            }
            finally
            {
                CFRelease(cfName);
            }
        }
    }
}
