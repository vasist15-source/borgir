using Ryujinx.Common.Logging;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Serves a draw that reads the R32Float attachment it also writes without ending the
    /// pass. A split would store the attachment and let the draw sample memory: the value
    /// from before the draw began, constant for the whole draw. The same value sits in
    /// tile memory, so right before the draw a tile kernel copies that slot's tile into a
    /// memoryless snapshot attachment nobody else writes, and the draw's fetch variant
    /// reads the snapshot through [[color(dst)]]. Split semantics, no round trip to memory.
    /// Off by default: RYUJINX_METAL_TILE_SNAPSHOT=1, or /tmp/ryujinx-metal-tile-snapshot.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class TileSnapshot
    {
        private static readonly bool _default = Environment.GetEnvironmentVariable("RYUJINX_METAL_TILE_SNAPSHOT") == "1";
        private static bool _failed;

        public static bool Enabled { get; private set; } = _default;

        /// <summary>1 = snapshot and fetch (the real thing); 2 = dispatch the kernel but still split, to price the dispatch alone; 3 = fetch without a dispatch, to price the fetch variant alone (wrong picture).</summary>
        public static int Mode { get; private set; } = ParseMode(Environment.GetEnvironmentVariable("RYUJINX_METAL_TILE_SNAPSHOT_MODE"));

        private static int ParseMode(string text) => int.TryParse(text?.Trim(), out int m) && m >= 1 && m <= 3 ? m : 1;
        public static long Dispatches;
        public static long Failures;

        private static readonly Dictionary<(int Width, int Height), MTLTexture> _memoryless = new();
        private static readonly Dictionary<string, MTLRenderPipelineState> _pipelines = new();

        private const string Source = @"
#include <metal_stdlib>
using namespace metal;

struct Snapshot
{
    float4 source [[color(SRC)]];
    float4 copy [[color(DST)]];
};

kernel void snapshotMain(imageblock<Snapshot, imageblock_layout_implicit> block,
                         ushort2 position [[thread_position_in_threadgroup]])
{
    // The implicit layout is the pass's own attachments, packed by the hardware, so
    // it is read and written by value rather than through a pointer.
    Snapshot pixel = block.read(position);
    pixel.copy = pixel.source;
    block.write(pixel, position);
}
";

        public static void Refresh()
        {
            try
            {
                Enabled = !_failed && (System.IO.File.Exists("/tmp/ryujinx-metal-tile-snapshot")
                    ? System.IO.File.ReadAllText("/tmp/ryujinx-metal-tile-snapshot").Trim() == "1"
                    : _default);
                Mode = System.IO.File.Exists("/tmp/ryujinx-metal-tile-snapshot-mode")
                    ? ParseMode(System.IO.File.ReadAllText("/tmp/ryujinx-metal-tile-snapshot-mode"))
                    : ParseMode(Environment.GetEnvironmentVariable("RYUJINX_METAL_TILE_SNAPSHOT_MODE"));
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; the next frame picks it up.
            }
        }

        /// <summary>The snapshot attachment for a pass of this size: memoryless, so it never touches memory.</summary>
        public static MTLTexture GetMemoryless(MTLDevice device, int width, int height)
        {
            if (!_memoryless.TryGetValue((width, height), out MTLTexture texture))
            {
                MTLTextureDescriptor descriptor = new()
                {
                    PixelFormat = MTLPixelFormat.R32Float,
                    TextureType = MTLTextureType.Type2D,
                    Width = (ulong)width,
                    Height = (ulong)height,
                    MipmapLevelCount = 1,
                    SampleCount = 1,
                    Usage = MTLTextureUsage.RenderTarget,
                    StorageMode = MTLStorageMode.Memoryless,
                };

                texture = device.NewTexture(descriptor);
                descriptor.Dispose();
                _memoryless[(width, height)] = texture;
            }

            return texture;
        }

        /// <summary>Copies slot <paramref name="src"/> into slot <paramref name="dst"/> for every pixel of every tile, on the open encoder.</summary>
        public static bool Dispatch(MTLDevice device, MTLRenderCommandEncoder encoder, int src, int dst, ReadOnlySpan<MTLPixelFormat> formats)
        {
            string key = $"{src}>{dst}:{string.Join(",", formats.ToArray())}";

            if (!_pipelines.TryGetValue(key, out MTLRenderPipelineState pipeline))
            {
                pipeline = Build(device, src, dst, formats);
                _pipelines[key] = pipeline;
            }

            if (pipeline.NativePtr == IntPtr.Zero)
            {
                Failures++;
                return false;
            }

            encoder.SetRenderPipelineState(pipeline);
            encoder.DispatchThreadsPerTile(new MTLSize { width = encoder.TileWidth, height = encoder.TileHeight, depth = 1 });
            Dispatches++;

            return true;
        }

        private static MTLRenderPipelineState Build(MTLDevice device, int src, int dst, ReadOnlySpan<MTLPixelFormat> formats)
        {
            string source = Source.Replace("SRC", src.ToString()).Replace("DST", dst.ToString());

            using MTLCompileOptions options = new();
            NSError error = new(IntPtr.Zero);
            MTLLibrary library = device.NewLibrary(StringHelper.NSString(source), options, ref error);

            if (error != IntPtr.Zero || library.NativePtr == IntPtr.Zero)
            {
                Fail($"tile snapshot kernel {src}->{dst} failed to compile: {(error != IntPtr.Zero ? StringHelper.String(error.LocalizedDescription) : "no library")}");
                return default;
            }

            MTLFunction function = library.NewFunction(StringHelper.NSString("snapshotMain"));

            MTLTileRenderPipelineDescriptor descriptor = new();
            descriptor.TileFunction = function;
            descriptor.ThreadgroupSizeMatchesTileSize = true;
            descriptor.RasterSampleCount = 1;

            for (int i = 0; i < formats.Length; i++)
            {
                if (formats[i] != MTLPixelFormat.Invalid)
                {
                    MTLTileRenderPipelineColorAttachmentDescriptor attachment = descriptor.ColorAttachments.Object((ulong)i);
                    attachment.PixelFormat = formats[i];
                }
            }

            error = new(IntPtr.Zero);
            MTLRenderPipelineState pipeline = device.NewRenderPipelineState(descriptor, MTLPipelineOption.None, IntPtr.Zero, ref error);
            descriptor.Dispose();

            if (error != IntPtr.Zero || pipeline.NativePtr == IntPtr.Zero)
            {
                Fail($"tile snapshot pipeline {src}->{dst} [{string.Join(",", formats.ToArray())}] failed: {(error != IntPtr.Zero ? StringHelper.String(error.LocalizedDescription) : "null")}");
                return default;
            }

            Logger.Info?.PrintMsg(LogClass.Gpu, $"tile snapshot pipeline {src}->{dst} ready [{string.Join(",", formats.ToArray())}]");

            return pipeline;
        }

        private static void Fail(string message)
        {
            Logger.Warning?.PrintMsg(LogClass.Gpu, message + " - tile snapshots disabled, splits resume");
            _failed = true;
            Enabled = false;
        }
    }
}
