using System;

namespace Ryujinx.Graphics.Gpu
{
    /// <summary>
    /// General GPU and graphics configuration.
    /// </summary>
    public static class GraphicsConfig
    {
        /// <summary>
        /// Resolution scale.
        /// </summary>
        public static float ResScale { get; set; } = 1f;

        /// <summary>
        /// Max Anisotropy. Values range from 0 - 16. Set to -1 to let the game decide.
        /// </summary>
        public static float MaxAnisotropy { get; set; } = -1;

        /// <summary>
        /// Base directory used to write shader code dumps.
        /// Set to null to disable code dumping.
        /// </summary>
        public static string ShadersDumpPath { get; set; }

        /// <summary>
        /// Fast GPU time calculates the internal GPU time ticks as if the GPU was capable of
        /// processing commands almost instantly, instead of using the host timer.
        /// This can avoid lower resolution on some games when GPU performance is poor.
        /// </summary>
        public static bool FastGpuTime { get; set; } = true;

        /// <summary>
        /// Enables or disables fast 2d engine texture copies entirely on CPU when possible.
        /// Reduces stuttering and # of textures in games that copy textures around for streaming,
        /// as textures will not need to be created for the copy, and the data does not need to be
        /// flushed from GPU.
        /// </summary>
        public static bool Fast2DCopy { get; set; } = true;

        /// <summary>
        /// Enables or disables the Just-in-Time compiler for GPU Macro code.
        /// </summary>
        public static bool EnableMacroJit { get; set; } = true;

        /// <summary>
        /// Enables or disables high-level emulation of common GPU Macro code.
        /// </summary>
        public static bool EnableMacroHLE { get; set; } = true;

        /// <summary>
        /// Title id of the current running game.
        /// Used by the shader cache.
        /// </summary>
        public static string TitleId { get; set; }

        /// <summary>
        /// Enables or disables the shader cache.
        /// </summary>
        public static bool EnableShaderCache { get; set; }

        /// <summary>
        /// Enables or disables shader SPIR-V compilation.
        /// </summary>
        public static bool EnableSpirvCompilationOnVulkan { get; set; } = true;

        /// <summary>
        /// Indicates that the active renderer is the native Metal backend.
        /// Set by the frontend before the emulated console is initialized. The shader
        /// disk cache uses a separate directory in this case, because Metal builds use
        /// a different shader codegen version than the stock Vulkan releases and the
        /// two would otherwise invalidate (and concurrently clobber) each other's
        /// shared cache files.
        /// </summary>
        public static bool MetalBackendActive { get; set; }

        /// <summary>
        /// Maximum number of Metal shader cache entries to preload at startup.
        /// Set RYUJINX_METAL_SHADER_CACHE_PRELOAD_LIMIT to override. Use 0 to disable
        /// Metal preloading, or a negative value to load the full cache.
        /// The full cache is loaded by default now that Metal has its own cache
        /// directory: it starts empty and grows organically, and host binaries are
        /// valid on reload, so preloading no longer risks a long first-boot freeze.
        /// </summary>
        public static int MetalShaderCachePreloadLimit { get; set; } =
            GetEnvironmentInt("RYUJINX_METAL_SHADER_CACHE_PRELOAD_LIMIT", -1);

        /// <summary>
        /// Forces Metal WaitForIdle host syncs to be submitted immediately, matching
        /// the upstream behaviour of every other backend. Strict is the default again:
        /// the deferred mode predates the adaptive auto-flush and saved little after
        /// it, while breaking pipelines that read GPU-written data on the CPU right
        /// after a WaitForIdle (the macro JIT path for indirect-count multi-draws
        /// fetches GPU-computed draw parameters, and non-strict syncs let it observe
        /// stale data, seen as flickering foliage in TOTK's Depths).
        /// Set RYUJINX_METAL_STRICT_WFI=0 to get the old deferred mode for A/B tests.
        /// </summary>
        public static bool MetalStrictWaitForIdle { get; set; } =
            GetEnvironmentInt("RYUJINX_METAL_STRICT_WFI", 1) != 0;

        /// <summary>
        /// Enables or disables recompression of compressed textures that are not natively supported by the host.
        /// </summary>
        public static bool EnableTextureRecompression { get; set; } = false;

        /// <summary>
        /// Enables or disables color space passthrough, if available.
        /// </summary>
        public static bool EnableColorSpacePassthrough { get; set; } = false;

        private static int GetEnvironmentInt(string name, int defaultValue)
        {
            string value = Environment.GetEnvironmentVariable(name);

            return int.TryParse(value, out int result) ? result : defaultValue;
        }
    }
}
