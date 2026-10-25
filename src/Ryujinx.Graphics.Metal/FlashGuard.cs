using Ryujinx.Common.Logging;
using SharpMetal.Metal;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Symptom mitigation for the TOTK white flash, not a fix for it.
    ///
    /// On this backend a fraction of frames reach Present with the source image a flat
    /// near-white - the scene is gone while the HUD still composites over it. The cause
    /// is unresolved (see docs/METAL_WHITE_FLASH_HANDOFF_2026-08-08.md); what is well
    /// established is that such a frame is uniform, which is something a rendered scene
    /// never is. This samples the source just before the present blit and, when enough of
    /// those samples are saturated, presents the last frame that was not flat instead.
    ///
    /// The test counts saturated samples rather than asking them to be equal. A flat frame
    /// keeps its HUD, so a uniformity test is decided by whether a sample lands on the
    /// minimap - which is why an earlier version of this fired on some camera angles and
    /// not others, and reported success it had not achieved.
    ///
    /// Two costs, both real:
    /// - one GPU sync per frame, to have the samples on the CPU before deciding
    /// - a repeated frame whenever it fires, so motion stutters instead of flashing
    ///
    /// Off unless RYUJINX_METAL_FLASHGUARD=1. /tmp/ryujinx-metal-flashguard overrides it
    /// with 0 or 1, re-read once a frame, so both arms can be measured in one session.
    ///
    /// Measured with the corrected criterion, alternating both arms twice inside one
    /// session and judging from compositor screenshots: 35.0% of frames flat with it off,
    /// 0.0% with it on, and the luma still varying (151..162) rather than frozen on a
    /// repeated frame. It does suppress the artefact.
    ///
    /// Still off by default, because it destabilises the emulator: with it on, the
    /// process exits during ordinary play, silently, within a minute or two of camera
    /// movement. The signature seen when a report was produced is EXC_BAD_ACCESS inside
    /// AGX FramebufferGen3, reached from renderCommandEncoderWithDescriptor - the driver
    /// faulting while it builds this pass's descriptor. Rebuilding the keep texture on
    /// resize was one cause and is fixed (this game has dynamic resolution, so the old
    /// code released a texture an in-flight command buffer was about to attach); the
    /// remainder is unresolved. Its usage flags do include RenderTarget, so that is not
    /// it. Do not enable this for anyone until the exit is understood.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class FlashGuard
    {
        private const int GridSide = 5;
        private const int Pixels = GridSide * GridSide;

        // Same threshold the probe and KeepGood.metal use.
        private const int SaturatedNeeded = 6;
        private const int BytesPerPixel = 4;

        // Calibrated on 300 captured frames of real gameplay: a flat frame has at least
        // 8 of 25 samples saturated (median 24), an ordinary one at most 4 (median 2).
        private const float SaturatedLuma = 235f;

        private static bool _enabled;
        private static MTLBuffer _buf;

        private static Texture _previousSource;
        private static int _suppressed;
        private static int _seen;

        public static bool Enabled => _enabled;

        private static Texture _keep;
        private static int _keepWidth;
        private static int _keepHeight;

        /// <summary>
        /// A texture the size of the present source that holds the most recent frame that
        /// was not flat. Separate from the rotating present surfaces on purpose: those
        /// alternate every frame, so the "previous" one is flat as often as the current.
        /// </summary>
        public static Texture GetKeepTexture(MTLDevice device, MetalRenderer renderer, Pipeline pipeline, Texture like)
        {
            if (like == null)
            {
                return null;
            }

            // Created once and never resized. The previous version rebuilt it whenever the
            // source changed size and released the old one immediately - and this game has
            // dynamic resolution, so that fired during play and freed a texture a command
            // buffer still in flight was about to name as an attachment. That is what
            // faulted the driver inside renderCommandEncoderWithDescriptor while it built
            // the pass descriptor. The shader samples with normalised coordinates, so one
            // fixed size serves every source size.
            if (_keep != null)
            {
                return _keep;
            }

            _keep = new Texture(device, renderer, pipeline, like.Info);
            _keepWidth = like.Width;
            _keepHeight = like.Height;

            // The blocking question is why attaching this faults the driver, and the
            // answer has to start with how it differs from the surface the game itself
            // renders to. Printed once.
            MTLTexture kt = _keep.GetHandle();
            MTLTexture st = like.GetHandle();

            Logger.Warning?.PrintMsg(LogClass.Gpu,
                $"flashguard keep: {_keep.Width}x{_keep.Height} fmt={_keep.MtlFormat} " +
                $"usage={kt.Usage} storage={kt.StorageMode} samples={kt.SampleCount} " +
                $"type={kt.TextureType} mips={kt.MipmapLevelCount} slices={kt.ArrayLength} " +
                $"ptr=0x{kt.NativePtr:X}");

            Logger.Warning?.PrintMsg(LogClass.Gpu,
                $"flashguard src : {like.Width}x{like.Height} fmt={like.MtlFormat} " +
                $"usage={st.Usage} storage={st.StorageMode} samples={st.SampleCount} " +
                $"type={st.TextureType} mips={st.MipmapLevelCount} slices={st.ArrayLength} " +
                $"ptr=0x{st.NativePtr:X}");

            return _keep;
        }

        public static void Init(MTLDevice device)
        {
            _buf = device.NewBuffer(Pixels * BytesPerPixel, MTLResourceOptions.ResourceStorageModeShared);
        }

        // Off by default again, on evidence taken while actually playing.
        //
        // It was promoted to default-on after measuring 35% -> 0% flat frames with the
        // picture alive and no crash while turning the camera. That acceptance was taken
        // standing still, and standing still is where this fault hides. Three arms, same
        // build, same save, the same scripted four minutes of walking and panning:
        //
        //     guard on,  one queue   crash at 2:16   (commit an already committed buffer)
        //     guard on,  two queues  crash at 2:11   (silent exit, no assertion)
        //     guard off             alive at 7:07   130 roam cycles, no errors
        //
        // So the warning this comment used to carry - "the process exits during ordinary
        // play, within a minute or two of camera movement" - was never actually fixed;
        // eight unattended runs today sat still for five to eight minutes each and never
        // saw it. The guard does not create the race, it widens it: Texture.SetData takes
        // Pipeline.Cbs and opens a blit encoder on it with no thread check at all, while
        // Texture.GetData carefully asks OwnedByCurrentThread first and routes background
        // callers to their own pool. Movement means terrain streaming means SetData from
        // thread-pool workers, and the guard's extra full-screen pass widens the window
        // between that encoder opening and the render thread committing.
        //
        // Fix that asymmetry and this can be reconsidered. Until then the artefact is
        // preferable to the exit, and the cost was never only the crash: at the
        // reproducing save 39-41% of frames are flat, so the guard repeats four frames in
        // ten there and the motion judders accordingly.
        //
        // RYUJINX_METAL_FLASHGUARD=1 opts in.
        private static readonly bool _defaultEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_FLASHGUARD") == "1";

        public static void RefreshToggle()
        {
            try
            {
                if (System.IO.File.Exists("/tmp/ryujinx-metal-flashguard"))
                {
                    _enabled = System.IO.File.ReadAllText("/tmp/ryujinx-metal-flashguard").Trim() == "1";
                }
                else
                {
                    _enabled = _defaultEnabled;
                }
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; the next frame picks it up.
            }
        }

        /// <summary>
        /// Samples the source. Returns the texture that should actually be presented:
        /// the previous frame's surface when this one is a flat white, otherwise the
        /// source unchanged.
        /// </summary>
        public static unsafe Texture Filter(CommandBufferScoped cbs, Texture src, Action flushAndWait)
        {
            if (!_enabled || _buf.NativePtr == IntPtr.Zero || src == null)
            {
                _previousSource = src;
                return src;
            }

            MTLTexture tex = src.GetHandle();

            if (tex.NativePtr == IntPtr.Zero)
            {
                return src;
            }

            MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();

            for (int i = 0; i < Pixels; i++)
            {
                blit.CopyFromTexture(
                    tex, 0, 0,
                    new MTLOrigin
                    {
                        x = (ulong)(src.Width * (i % GridSide + 1) / (GridSide + 1)),
                        y = (ulong)(src.Height * (i / GridSide + 1) / (GridSide + 1)),
                        z = 0,
                    },
                    new MTLSize { width = 1, height = 1, depth = 1 },
                    _buf, (ulong)(i * BytesPerPixel), BytesPerPixel, BytesPerPixel);
            }

            // The samples have to be on the CPU before the present blit is chosen, so the
            // frame is committed and waited on here. This is the mitigation's main cost.
            flushAndWait();

            byte* p = (byte*)_buf.Contents;
            int saturated = 0;

            for (int i = 0; i < Pixels; i++)
            {
                byte* px = p + i * BytesPerPixel;

                if ((px[0] + px[1] + px[1] + px[2]) * 0.25f >= SaturatedLuma)
                {
                    saturated++;
                }
            }

            _seen++;

            bool flat = saturated >= SaturatedNeeded;

            if (flat && _previousSource != null && !ReferenceEquals(_previousSource, src))
            {
                _suppressed++;

                if (_suppressed % 300 == 1)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu,
                        $"flashguard: repeated the previous frame {_suppressed} times out of {_seen}");
                }

                return _previousSource;
            }

            _previousSource = src;

            return src;
        }
    }
}
