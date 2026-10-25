namespace Ryujinx.Graphics.GAL
{
    /// <summary>
    /// The running game's shader cache directory, or null when the shader cache is off. Set by the GPU shader cache
    /// as it opens, before it creates any program, so a backend can keep its own per-game caches next to it.
    /// </summary>
    public static class ShaderCacheLocation
    {
        public static string Directory { get; set; }
    }
}
