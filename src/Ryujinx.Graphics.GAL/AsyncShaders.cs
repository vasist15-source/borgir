using System.Threading;

namespace Ryujinx.Graphics.GAL
{
    /// <summary>
    /// The "Compile shaders in the background" setting. When on, binding a graphics program whose shaders are still
    /// compiling does not wait for them: the renderer skips its draws until they are ready, so a new shader costs a
    /// few frames of a missing object instead of a freeze. Fullscreen draws and compute programs still wait, since
    /// skipping those paints the whole frame wrong or loses results the game reads later.
    /// </summary>
    public static class AsyncShaders
    {
        private static int _enabled;

        public static bool Enabled
        {
            get => Volatile.Read(ref _enabled) != 0;
            set => Volatile.Write(ref _enabled, value ? 1 : 0);
        }
    }
}
