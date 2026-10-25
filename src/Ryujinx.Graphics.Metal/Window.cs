using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.Effects;
using Ryujinx.Graphics.Metal.SharpMetalExtensions;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    class Window : IWindow, IDisposable
    {
        public bool ScreenCaptureRequested { get; set; }

        private readonly MetalRenderer _renderer;
        private CAMetalLayer _metalLayer;

        private int _width;
        private int _height;

        private int _requestedWidth;
        private int _requestedHeight;

        private AntiAliasing _currentAntiAliasing;
        private bool _updateEffect;
        private IPostProcessingEffect _effect;
        private IScalingFilter _scalingFilter;
        private bool _isLinear;

        public bool IsVSyncEnabled => _metalLayer.DisplaySyncEnabled;

        private float _scalingFilterLevel = 80f;
        private bool _updateScalingFilter;
        private ScalingFilter _currentScalingFilter;
        private bool _useFsrSharpener;
        private int _lastLoggedPresentSrcWidth;
        private int _lastLoggedPresentSrcHeight;
        private int _lastLoggedPresentDstWidth;
        private int _lastLoggedPresentDstHeight;
        private int _lastLoggedPresentDrawableWidth;
        private int _lastLoggedPresentDrawableHeight;
        private ScalingFilter _lastLoggedPresentScalingFilter;
        private float _lastLoggedPresentScalingLevel = -1f;
        private bool _colorSpacePassthroughEnabled;

        // The emulated console renders in sRGB. Without an explicit layer color space
        // the compositor does no color matching, so on wide-gamut (P3) displays the
        // output is oversaturated with a cyan/green shift and deeper shadows compared
        // to the Vulkan (MoltenVK) backend, which declares an sRGB surface.
        private static readonly IntPtr _srgbColorSpace =
            CAMetalLayerExtensions.CreateNamedColorSpace("kCGColorSpaceSRGB");

        public Window(MetalRenderer renderer, CAMetalLayer metalLayer)
        {
            _renderer = renderer;
            _metalLayer = metalLayer;

            ApplyColorSpace();
        }

        private void ApplyColorSpace()
        {
            if (_colorSpacePassthroughEnabled)
            {
                _metalLayer.SetColorspace(IntPtr.Zero);

                Logger.Info?.PrintMsg(LogClass.Gpu, "Metal layer color space: passthrough (no color matching).");
            }
            else if (_srgbColorSpace != IntPtr.Zero)
            {
                _metalLayer.SetColorspace(_srgbColorSpace);

                Logger.Info?.PrintMsg(LogClass.Gpu, "Metal layer color space: sRGB.");
            }
            else
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, "Failed to create sRGB color space; Metal layer keeps the system default.");
            }
        }

        private void ResizeIfNeeded()
        {
            if (_requestedWidth != 0 && _requestedHeight != 0)
            {
                // TODO: This is actually a CGSize, but there is no overload for that, so fill the first two fields of rect with the size.
                NSRect rect = new(_requestedWidth, _requestedHeight, 0, 0);

                ObjectiveC.objc_msgSend(_metalLayer, "setDrawableSize:", rect);

                _requestedWidth = 0;
                _requestedHeight = 0;
            }
        }

        public void NoteModifiedBy(string who) => UploadCorrelator.NoteModifiedBy(who);

        public void NoteTopologyEvent(int kind) => UploadCorrelator.NoteTopologyEvent(kind);

        public void NoteGameFinalTargetView(ITexture texture)
        {
            if (texture is Texture t)
            {
                UploadCorrelator.NoteGameFinalTargetView(t);
            }
        }

        public void NoteGameFinalTarget(ITexture texture)
        {
            if (texture is Texture t)
            {
                UploadCorrelator.NoteGameFinalTarget(t);
            }
        }

        public void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback)
        {
            if (_renderer.Pipeline is Pipeline pipeline && texture is Texture tex)
            {
                StoreLiveness.NoteRead(tex.CanonicalPtr);

                ResizeIfNeeded();

                CAMetalDrawable drawable = new(ObjectiveC.IntPtr_objc_msgSend(_metalLayer, "nextDrawable"));

                // nextDrawable returns an autoreleased object and this thread has no
                // draining pool; own it until presentation (released after
                // PresentDrawable in Pipeline.Present).
                ObjcOwnership.Retain(drawable.NativePtr);

                _width = (int)drawable.Texture.Width;
                _height = (int)drawable.Texture.Height;

                UpdateEffect();

                if (_effect != null)
                {
                    // TODO: Run Effects
                    // view = _effect.Run()
                }

                int srcX0, srcX1, srcY0, srcY1;

                if (crop.Left == 0 && crop.Right == 0)
                {
                    srcX0 = 0;
                    srcX1 = tex.Width;
                }
                else
                {
                    srcX0 = crop.Left;
                    srcX1 = crop.Right;
                }

                if (crop.Top == 0 && crop.Bottom == 0)
                {
                    srcY0 = 0;
                    srcY1 = tex.Height;
                }
                else
                {
                    srcY0 = crop.Top;
                    srcY1 = crop.Bottom;
                }

                if (ScreenCaptureRequested)
                {
                    CaptureFrame(tex, srcX0, srcY0, srcX1, srcY1, crop.FlipX, crop.FlipY);

                    ScreenCaptureRequested = false;
                }

                float ratioX = crop.IsStretched ? 1.0f : MathF.Min(1.0f, _height * crop.AspectRatioX / (_width * crop.AspectRatioY));
                float ratioY = crop.IsStretched ? 1.0f : MathF.Min(1.0f, _width * crop.AspectRatioY / (_height * crop.AspectRatioX));

                int dstWidth = (int)(_width * ratioX);
                int dstHeight = (int)(_height * ratioY);

                int dstPaddingX = (_width - dstWidth) / 2;
                int dstPaddingY = (_height - dstHeight) / 2;

                int dstX0 = crop.FlipX ? _width - dstPaddingX : dstPaddingX;
                int dstX1 = crop.FlipX ? dstPaddingX : _width - dstPaddingX;

                int dstY0 = crop.FlipY ? _height - dstPaddingY : dstPaddingY;
                int dstY1 = crop.FlipY ? dstPaddingY : _height - dstPaddingY;

                if (_scalingFilter != null)
                {
                    // TODO: Run scaling filter
                }

                LogPresentConfigurationIfChanged(tex, srcX0, srcX1, srcY0, srcY1, dstWidth, dstHeight);

                pipeline.Present(
                    drawable,
                    tex,
                    new Extents2D(srcX0, srcY0, srcX1, srcY1),
                    new Extents2D(dstX0, dstY0, dstX1, dstY1),
                    _isLinear,
                    _useFsrSharpener,
                    _scalingFilterLevel);
            }
        }

        /// <summary>
        /// Reads back the part of the game's frame that is on screen and hands it to the host as a screenshot.
        /// </summary>
        private void CaptureFrame(Texture texture, int x0, int y0, int x1, int y1, bool flipX, bool flipY)
        {
            // Games present 8-bit colour; other formats would need converting before they could be saved.
            if (texture.Info.BytesPerPixel != 4)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Screenshots of {texture.Info.Format} frames are not supported.");
                return;
            }

            int left = Math.Min(x0, x1);
            int top = Math.Min(y0, y1);
            int width = Math.Min(Math.Abs(x1 - x0), texture.Width - left);
            int height = Math.Min(Math.Abs(y1 - y0), texture.Height - top);

            if (width <= 0 || height <= 0)
            {
                return;
            }

            try
            {
                using PinnedSpan<byte> data = texture.GetData(0, 0);

                ReadOnlySpan<byte> pixels = data.Get();
                int stride = texture.Info.GetMipStride(0);
                int rowBytes = width * 4;
                byte[] bitmap = new byte[rowBytes * height];

                for (int row = 0; row < height; row++)
                {
                    pixels.Slice((top + row) * stride + left * 4, rowBytes).CopyTo(bitmap.AsSpan(row * rowBytes));
                }

                _renderer.OnScreenCaptured(new ScreenCaptureImageInfo(width, height, texture.Info.Format.IsBgr, bitmap, flipX, flipY));
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.Gpu, $"Screenshot readback failed: {ex.Message}");
            }
        }

        public void SetSize(int width, int height)
        {
            _requestedWidth = width;
            _requestedHeight = height;
        }

        public void ChangeVSyncMode(VSyncMode vSyncMode)
        {
            _metalLayer.DisplaySyncEnabled = vSyncMode is VSyncMode.Switch;
        }

        public void SetAntiAliasing(AntiAliasing effect)
        {
            if (_currentAntiAliasing == effect && _effect != null)
            {
                return;
            }

            _currentAntiAliasing = effect;

            _updateEffect = true;
        }

        public void SetScalingFilter(ScalingFilter type)
        {
            if (_currentScalingFilter == type && _effect != null)
            {
                return;
            }

            _currentScalingFilter = type;

            _updateScalingFilter = true;
        }

        public void SetScalingFilterLevel(float level)
        {
            _scalingFilterLevel = level;
            _updateScalingFilter = true;
        }

        public void SetColorSpacePassthrough(bool colorSpacePassThroughEnabled)
        {
            _colorSpacePassthroughEnabled = colorSpacePassThroughEnabled;

            ApplyColorSpace();
        }

        private void UpdateEffect()
        {
            if (_updateEffect)
            {
                _updateEffect = false;

                switch (_currentAntiAliasing)
                {
                    case AntiAliasing.Fxaa:
                        _effect?.Dispose();
                        Logger.Warning?.PrintMsg(LogClass.Gpu, "FXAA not implemented for Metal backend!");
                        break;
                    case AntiAliasing.None:
                        _effect?.Dispose();
                        _effect = null;
                        break;
                    case AntiAliasing.SmaaLow:
                    case AntiAliasing.SmaaMedium:
                    case AntiAliasing.SmaaHigh:
                    case AntiAliasing.SmaaUltra:
                        // var quality = _currentAntiAliasing - AntiAliasing.SmaaLow;
                        Logger.Warning?.PrintMsg(LogClass.Gpu, "SMAA not implemented for Metal backend!");
                        break;
                }
            }

            if (_updateScalingFilter)
            {
                _updateScalingFilter = false;

                switch (_currentScalingFilter)
                {
                    case ScalingFilter.Bilinear:
                    case ScalingFilter.Nearest:
                        _scalingFilter?.Dispose();
                        _scalingFilter = null;
                        _isLinear = _currentScalingFilter == ScalingFilter.Bilinear;
                        _useFsrSharpener = false;
                        break;
                    case ScalingFilter.Fsr:
                        _scalingFilter?.Dispose();
                        _scalingFilter = null;
                        _isLinear = true;
                        _useFsrSharpener = true;
                        Logger.Info?.PrintMsg(LogClass.Gpu, "Using Metal present sharpener for FSR scaling filter.");
                        break;
                }
            }
        }

        private void LogPresentConfigurationIfChanged(
            Texture tex,
            int srcX0,
            int srcX1,
            int srcY0,
            int srcY1,
            int dstWidth,
            int dstHeight)
        {
            int srcWidth = Math.Abs(srcX1 - srcX0);
            int srcHeight = Math.Abs(srcY1 - srcY0);

            if (srcWidth == _lastLoggedPresentSrcWidth &&
                srcHeight == _lastLoggedPresentSrcHeight &&
                dstWidth == _lastLoggedPresentDstWidth &&
                dstHeight == _lastLoggedPresentDstHeight &&
                _width == _lastLoggedPresentDrawableWidth &&
                _height == _lastLoggedPresentDrawableHeight &&
                _currentScalingFilter == _lastLoggedPresentScalingFilter &&
                Math.Abs(_scalingFilterLevel - _lastLoggedPresentScalingLevel) < 0.01f)
            {
                return;
            }

            _lastLoggedPresentSrcWidth = srcWidth;
            _lastLoggedPresentSrcHeight = srcHeight;
            _lastLoggedPresentDstWidth = dstWidth;
            _lastLoggedPresentDstHeight = dstHeight;
            _lastLoggedPresentDrawableWidth = _width;
            _lastLoggedPresentDrawableHeight = _height;
            _lastLoggedPresentScalingFilter = _currentScalingFilter;
            _lastLoggedPresentScalingLevel = _scalingFilterLevel;

            Logger.Info?.PrintMsg(
                LogClass.Gpu,
                $"Metal present: src {srcWidth}x{srcHeight} texture {tex.Width}x{tex.Height} format {tex.Info.Format}/{tex.MtlFormat} " +
                $"dst {dstWidth}x{dstHeight} drawable {_width}x{_height} scaling {_currentScalingFilter} level {_scalingFilterLevel:0.##}.");
        }

        public void Dispose()
        {
            _metalLayer.Dispose();
        }
    }
}
