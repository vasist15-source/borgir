using Ryujinx.Common.Configuration;
using Ryujinx.Input.HLE;
using Ryujinx.SDL3.Common;
using SharpMetal.QuartzCore;
using System.Runtime.Versioning;
using SDL;
using static SDL.SDL3;

namespace Ryujinx.Headless
{
    [SupportedOSPlatform("macos")]
    class MetalWindow : WindowBase
    {
        private CAMetalLayer _caMetalLayer;
        private nint _metalView;

        public CAMetalLayer GetLayer()
        {
            return _caMetalLayer;
        }

        public MetalWindow(
            InputManager inputManager,
            GraphicsDebugLevel glLogLevel,
            AspectRatio aspectRatio,
            bool enableMouse,
            HideCursorMode hideCursorMode,
            bool ignoreControllerApplet)
            : base(inputManager, glLogLevel, aspectRatio, enableMouse, hideCursorMode, ignoreControllerApplet) { }

        public override SDL_WindowFlags WindowFlags => SDL_WindowFlags.SDL_WINDOW_METAL;

        protected override unsafe void InitializeWindowRenderer()
        {
            void CreateLayer()
            {
                nint metalView = SDL_Metal_CreateView(WindowHandle);
                _metalView = metalView;
                _caMetalLayer = new CAMetalLayer((nint)SDL_Metal_GetLayer(metalView));
            }

            if (SDL3Driver.MainThreadDispatcher != null)
            {
                SDL3Driver.MainThreadDispatcher(CreateLayer);
            }
            else
            {
                CreateLayer();
            }
        }

        protected override void InitializeRenderer() { }

        protected override unsafe void FinalizeWindowRenderer()
        {
            if (_metalView != 0)
            {
                SDL_Metal_DestroyView(_metalView);
                _metalView = 0;
            }
        }

        protected override void SwapBuffers() { }
    }
}
