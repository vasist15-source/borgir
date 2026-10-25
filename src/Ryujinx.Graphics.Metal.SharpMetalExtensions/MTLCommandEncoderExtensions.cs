using SharpMetal;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal.SharpMetalExtensions
{
    [SupportedOSPlatform("macOS")]
    public static class MTLCommandEncoderExtensions
    {
        private static readonly Selector SelUseRenderResources = "useResources:count:usage:stages:";
        private static readonly Selector SelUseComputeResources = "useResources:count:usage:";
        private static readonly Selector SelSetVisibilityResultType = "setVisibilityResultType:";

        /// <summary>
        /// Configures a macOS 26 render pass to add visibility results to the value
        /// already in the result buffer instead of replacing it. SharpMetal
        /// preview21 predates this Metal API, so call the Objective-C selector
        /// directly until the binding can be updated.
        /// </summary>
        /// <summary>The default result type: each pass writes its own counts (nothing accumulates).</summary>
        public static void SetVisibilityResultTypeReset(this MTLRenderPassDescriptor descriptor)
        {
            const ulong MTLVisibilityResultTypeReset = 0;

            ObjectiveCRuntime.objc_msgSend(
                descriptor.NativePtr,
                SelSetVisibilityResultType,
                MTLVisibilityResultTypeReset);
        }

        public static void SetVisibilityResultTypeAccumulate(this MTLRenderPassDescriptor descriptor)
        {
            const ulong MTLVisibilityResultTypeAccumulate = 1;

            ObjectiveCRuntime.objc_msgSend(
                descriptor.NativePtr,
                SelSetVisibilityResultType,
                MTLVisibilityResultTypeAccumulate);
        }

        public static unsafe void UseResourcesCompat(
            this MTLRenderCommandEncoder encoder,
            MTLResource[] resources,
            ulong count,
            MTLResourceUsage usage,
            MTLRenderStages stages)
        {
            fixed (MTLResource* resourcesPtr = resources)
            {
                ObjectiveCRuntime.objc_msgSend(
                    encoder.NativePtr,
                    SelUseRenderResources,
                    (IntPtr)resourcesPtr,
                    count,
                    (ulong)usage,
                    (ulong)stages);
            }
        }

        public static unsafe void UseResourcesCompat(
            this MTLComputeCommandEncoder encoder,
            MTLResource[] resources,
            ulong count,
            MTLResourceUsage usage)
        {
            fixed (MTLResource* resourcesPtr = resources)
            {
                ObjectiveCRuntime.objc_msgSend(
                    encoder.NativePtr,
                    SelUseComputeResources,
                    (IntPtr)resourcesPtr,
                    count,
                    (ulong)usage);
            }
        }
    }
}
