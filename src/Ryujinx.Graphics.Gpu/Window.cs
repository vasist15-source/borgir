using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Texture;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Ryujinx.Graphics.Gpu
{
    /// <summary>
    /// GPU image presentation window.
    /// </summary>
    public class Window
    {
        private static readonly bool _presentTrace =
            System.Environment.GetEnvironmentVariable("RYUJINX_GPU_PRESENT_TRACE") == "1";

        // RYUJINX_GPU_FRAME_LINE=1: one line per presented frame with the texture
        // synchronisation counters, for within-run comparison of flicker frames.
        private static readonly bool _frameLine =
            System.Environment.GetEnvironmentVariable("RYUJINX_GPU_FRAME_LINE") == "1";
        private static long _frameLineCount;
        private static int _presentTraceCount;

        // Presenting the sRGB render target instead of the Unorm shadow was a white-flash
        // mitigation (present could upload stale guest memory before the sRGB target was
        // flushed). The flash has since been fixed at its source in the shader translator,
        // and the swap costs a whole transfer function: presenting through the sRGB view
        // decodes to linear once more than the display re-encodes, so the picture darkens
        // along a gamma curve - worst where the scene is darkest. Measured at the Depths
        // save, median scene luma 4.4 with the swap against 27.7 for stock upstream and
        // 28.3 with it off. Off by default; RYUJINX_GPU_PRESENT_SIBLING=1 restores it.
        private static readonly bool _presentKeepShadow =
            System.Environment.GetEnvironmentVariable("RYUJINX_GPU_PRESENT_SIBLING") != "1";
        private static int _siblingLogs;
        private static int _gameRtShapeLogs;

        private static readonly bool _presentFlush =
            System.Environment.GetEnvironmentVariable("RYUJINX_GPU_PRESENT_FLUSH") == "1";
        private static int _flushLogs;

        private static readonly bool _presentNoSync =
            System.Environment.GetEnvironmentVariable("RYUJINX_GPU_PRESENT_NOSYNC") == "1";

        private readonly GpuContext _context;

        /// <summary>
        /// Texture presented on the window.
        /// </summary>
        private readonly struct PresentationTexture
        {
            /// <summary>
            /// Texture cache where the texture might be located.
            /// </summary>
            public TextureCache Cache { get; }

            /// <summary>
            /// Texture information.
            /// </summary>
            public TextureInfo Info { get; }

            /// <summary>
            /// Physical memory locations where the texture data is located.
            /// </summary>
            public MultiRange Range { get; }

            /// <summary>
            /// Texture crop region.
            /// </summary>
            public ImageCrop Crop { get; }

            /// <summary>
            /// Texture acquire callback.
            /// </summary>
            public Action<GpuContext, object> AcquireCallback { get; }

            /// <summary>
            /// Texture release callback.
            /// </summary>
            public Action<object> ReleaseCallback { get; }

            /// <summary>
            /// User defined object, passed to the various callbacks.
            /// </summary>
            public object UserObj { get; }

            /// <summary>
            /// Creates a new instance of the presentation texture.
            /// </summary>
            /// <param name="cache">Texture cache used to look for the texture to be presented</param>
            /// <param name="info">Information of the texture to be presented</param>
            /// <param name="range">Physical memory locations where the texture data is located</param>
            /// <param name="crop">Texture crop region</param>
            /// <param name="acquireCallback">Texture acquire callback</param>
            /// <param name="releaseCallback">Texture release callback</param>
            /// <param name="userObj">User defined object passed to the release callback, can be used to identify the texture</param>
            public PresentationTexture(
                TextureCache cache,
                TextureInfo info,
                MultiRange range,
                ImageCrop crop,
                Action<GpuContext, object> acquireCallback,
                Action<object> releaseCallback,
                object userObj)
            {
                Cache = cache;
                Info = info;
                Range = range;
                Crop = crop;
                AcquireCallback = acquireCallback;
                ReleaseCallback = releaseCallback;
                UserObj = userObj;
            }
        }

        private readonly ConcurrentQueue<PresentationTexture> _frameQueue;

        private int _framesAvailable;

        public bool IsFrameAvailable => _framesAvailable != 0;

        /// <summary>
        /// Creates a new instance of the GPU presentation window.
        /// </summary>
        /// <param name="context">GPU emulation context</param>
        public Window(GpuContext context)
        {
            _context = context;

            _frameQueue = new ConcurrentQueue<PresentationTexture>();
        }

        /// <summary>
        /// Enqueues a frame for presentation.
        /// This method is thread safe and can be called from any thread.
        /// When the texture is presented and not needed anymore, the release callback is called.
        /// It's an error to modify the texture after calling this method, before the release callback is called.
        /// </summary>
        /// <param name="pid">Process ID of the process that owns the texture pointed to by <paramref name="address"/></param>
        /// <param name="address">CPU virtual address of the texture data</param>
        /// <param name="width">Texture width</param>
        /// <param name="height">Texture height</param>
        /// <param name="stride">Texture stride for linear texture, should be zero otherwise</param>
        /// <param name="isLinear">Indicates if the texture is linear, normally false</param>
        /// <param name="gobBlocksInY">GOB blocks in the Y direction, for block linear textures</param>
        /// <param name="format">Texture format</param>
        /// <param name="bytesPerPixel">Texture format bytes per pixel (must match the format)</param>
        /// <param name="crop">Texture crop region</param>
        /// <param name="acquireCallback">Texture acquire callback</param>
        /// <param name="releaseCallback">Texture release callback</param>
        /// <param name="userObj">User defined object passed to the release callback</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="pid"/> is invalid</exception>
        /// <returns>True if the frame was added to the queue, false otherwise</returns>
        public bool EnqueueFrameThreadSafe(
            ulong pid,
            ulong address,
            int width,
            int height,
            int stride,
            bool isLinear,
            int gobBlocksInY,
            Format format,
            byte bytesPerPixel,
            ImageCrop crop,
            Action<GpuContext, object> acquireCallback,
            Action<object> releaseCallback,
            object userObj)
        {
            if (!_context.PhysicalMemoryRegistry.TryGetValue(pid, out PhysicalMemory physicalMemory))
            {
                return false;
            }

            FormatInfo formatInfo = new(format, 1, 1, bytesPerPixel, 4);

            TextureInfo info = new(
                0UL,
                width,
                height,
                1,
                1,
                1,
                1,
                stride,
                isLinear,
                gobBlocksInY,
                1,
                1,
                Target.Texture2D,
                formatInfo);

            int size = SizeCalculator.GetBlockLinearTextureSize(
                width,
                height,
                1,
                1,
                1,
                1,
                1,
                bytesPerPixel,
                gobBlocksInY,
                1,
                1).TotalSize;

            MultiRange range = new(address, (ulong)size);

            _frameQueue.Enqueue(new PresentationTexture(
                physicalMemory.TextureCache,
                info,
                range,
                crop,
                acquireCallback,
                releaseCallback,
                userObj));

            return true;
        }

        /// <summary>
        /// Presents a texture on the queue.
        /// If the queue is empty, then no texture is presented.
        /// </summary>
        /// <param name="swapBuffersCallback">Callback method to call when a new texture should be presented on the screen</param>
        public void Present(Action swapBuffersCallback)
        {
            GpuContext.RefreshSyncEveryDraws();

            _context.AdvanceSequence();

            if (_frameLine)
            {
                // One line per presented frame, both backends. The map flicker lasts about
                // five frames and is far too rare to compare across runs (eight identical
                // arms measured 0.00-0.67 events per minute), so the only usable comparison
                // is a flicker frame against its own neighbours inside one run. `uploads`
                // is the interesting one: guest memory written over a region the GPU had
                // rendered.
                (int uploads, int prot, int clean, int flushes) = Common.SyncMemDiag.SnapshotAndReset();
                (int degenScissor, int clears, int draws) = Common.SyncMemDiag.SnapshotAndResetDraw();
                (int indirect, int drawTex, int rtUpdates, int rtHash) = Common.SyncMemDiag.SnapshotAndResetDraw2();
                (int texAdd, int texRem, int poolInv) = Common.SyncMemDiag.SnapshotAndResetTex();
                (int dispatches, int texCopies) = Common.SyncMemDiag.SnapshotAndResetCompute();
                (int bufUp, int ubB, int sbB) = Common.SyncMemDiag.SnapshotAndResetBuffer();
                int watched = Common.SyncMemDiag.SnapshotAndResetWatched();
                int poolstale = Common.SyncMemDiag.SnapshotPoolStale();
                (int wtex0, int wtex1) = Common.SyncMemDiag.SnapshotWatchedTex();
                (float wc0, float wc1, float wc2) = Common.SyncMemDiag.SnapshotWatchedConst();
                (long ws0, long ws1) = Common.SyncMemDiag.SnapshotWatchedSeq();
                int ptex = Common.SyncMemDiag.SnapshotPresentTex();

                Common.Logging.Logger.Warning?.PrintMsg(Common.Logging.LogClass.Gpu,
                    $"gpuframe f={++_frameLineCount} t={System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} " +
                    $"uploads={uploads} protected={prot} clean={clean} flushes={flushes} " +
                    $"degenscissor={degenScissor} clears={clears} draws={draws} " +
                    $"indirect={indirect} drawtex={drawTex} rtupd={rtUpdates} rthash={rtHash:X8} " +
                    $"texadd={texAdd} texrem={texRem} poolinv={poolInv} " +
                    $"dispatch={dispatches} texcopy={texCopies} " +
                    $"bufup={bufUp} ubbind={ubB} sbbind={sbB} watched={watched} " +
                    $"wtex0={wtex0:X8} wtex1={wtex1:X8} wc0={wc0:G6} wc1={wc1:G6} wc2={wc2:G6} ws0={ws0} ws1={ws1} ptex={ptex:X8} poolstale={poolstale}");
            }

            Image.TextureBindRing.DumpIfRequested();

            if (_frameQueue.TryDequeue(out PresentationTexture pt))
            {
                pt.AcquireCallback(_context, pt.UserObj);
                _context.PresentTraceRange = pt.Range;

                // Experiment: before the present lookup, flush every modified texture that
                // overlaps the presentation range back to guest memory, so the texture the
                // lookup returns (a shadow fed from guest memory) uploads THIS frame's data
                // rather than whatever the range held before the game's own writer flushed.
                // RYUJINX_GPU_PRESENT_FLUSH=1
                if (_presentFlush)
                {
                    int flushed = pt.Cache.FlushOverlapsToGuest(pt.Range);
                    if (_presentTrace && ++_flushLogs % 300 == 1)
                    {
                        Common.Logging.Logger.Warning?.PrintMsg(Common.Logging.LogClass.Gpu, $"present pre-flush: {flushed} texture(s) flushed on range");
                    }
                }

                Image.Texture texture = pt.Cache.FindOrCreateTexture(null, TextureSearchFlags.WithUpscale, pt.Info, 0, range: pt.Range);

                // Which texture actually gets presented. The composite draw always writes the
                // same target, but nothing has ever checked that present picks that one - a
                // frame served from a different texture at the same range would look exactly
                // like the draw producing nothing.
                Common.SyncMemDiag.NotePresentTex(texture?.GetHashCode() ?? 0);

                // The white flash: the game renders its final frame into an sRGB target and
                // presentation asks for the linear (Unorm) sibling at the same address. That
                // misses, so the cache keeps a second, never-rendered texture for present
                // whose content is only as fresh as the last guest-memory flush of the real
                // target. Frames presented before that flush lands show stale content.
                // If the chosen texture was never rendered to and a same-range texture with
                // the sRGB/linear sibling format WAS, present the rendered one - a view of
                // the same bytes, decoded linearly by the present shader either way.
                // Tell the backend which host texture is the game's true final render target
                // (the sRGB sibling), independent of which one present ends up reading.
                {
                    Image.Texture gameRt = pt.Cache.FindRenderedSibling(pt.Range, texture) ?? texture;
                    if (gameRt.HostTexture is GAL.ITexture hostRt)
                    {
                        _context.Renderer.Window.NoteGameFinalTarget(hostRt);
                    }

                    // Also hand the backend the game RT's views, so their roots join the census.
                    foreach (GAL.ITexture vh in gameRt.ViewHostTextures)
                    {
                        _context.Renderer.Window.NoteGameFinalTargetView(vh);
                    }

                    // What feeds this texture. If it is a view, its storage owns the bytes and
                    // the writers we should census belong to the storage; if it has views,
                    // writes through them land here. Log both once every ~10 s.
                    if (_presentTrace && ++_gameRtShapeLogs % 300 == 1)
                    {
                        Common.Logging.Logger.Warning?.PrintMsg(Common.Logging.LogClass.Gpu,
                            $"game RT shape: isView={gameRt.IsView} hasViews={gameRt.HasViews} viewsCount={gameRt.ViewsCount} " +
                            $"groupCopyDeps={gameRt.Group?.HasCopyDependencies} storageHost#{(gameRt.IsView ? gameRt.StorageHostHash : 0):X} " +
                            $"modSeq={gameRt.Group?.ModifiedSequence} everModified={gameRt.EverModified} views={gameRt.ViewsSummary} selfHost#{(gameRt.HostTexture?.GetHashCode() ?? 0):X}");
                    }
                }

                if (!_presentKeepShadow)
                {
                    Image.Texture rendered = pt.Cache.FindRenderedSibling(pt.Range, texture);
                    if (rendered != null)
                    {
                        if (_presentTrace && ++_siblingLogs <= 5)
                        {
                            Common.Logging.Logger.Warning?.PrintMsg(Common.Logging.LogClass.Gpu,
                                $"present: swapping shadow {texture.Info.FormatInfo.Format} for rendered sibling {rendered.Info.FormatInfo.Format}");
                        }
                        texture = rendered;
                    }
                }

                // The Metal capture shows present sampling a texture object different from
                // the frame's final render target; the correlator shows present's source is
                // a NEW host texture every frame. Log whether this lookup created or reused.
                // Why does this lookup return a texture that no draw ever wrote? For each
                // same-range overlap, report its match quality against pt.Info and how it
                // differs - the frame's RGBA8 render target must be among them.
                if (_presentTrace && _presentTraceCount > 3000 && _presentTraceCount < 3006)
                {
                    pt.Cache.DumpOverlapMatches(pt.Range, pt.Info, texture);
                }

                if (_presentTrace && ++_presentTraceCount > 3000 && _presentTraceCount <= 3006)
                {
                    Common.Logging.Logger.Warning?.PrintMsg(Common.Logging.LogClass.Gpu,
                        $"present lookup: tex#{texture.GetHashCode():X} host#{(texture.HostTexture?.GetHashCode() ?? 0):X} " +
                        $"{pt.Info.Width}x{pt.Info.Height} {pt.Info.FormatInfo.Format} range=0x{pt.Range.GetSubRange(0).Address:X}+{pt.Range.GetSubRange(0).Size} " +
                        $"cacheCount={pt.Cache.CountForRange(pt.Range)}");
                }

                pt.Cache.Tick();

                // The white frame, read from a GPU capture: the present draw samples a
                // texture that is uniform white while the frame's final render target -
                // a DIFFERENT texture object holding the correct picture - sits unread.
                // This lookup+sync is what selects the present source; RYUJINX_GPU_PRESENT_NOSYNC=1
                // skips the sync to test whether it is what overwrites or mis-selects.
                if (!_presentNoSync)
                {
                    texture.SynchronizeMemory();
                }

                ImageCrop crop = new(
                    (int)(pt.Crop.Left * texture.ScaleFactor),
                    (int)MathF.Ceiling(pt.Crop.Right * texture.ScaleFactor),
                    (int)(pt.Crop.Top * texture.ScaleFactor),
                    (int)MathF.Ceiling(pt.Crop.Bottom * texture.ScaleFactor),
                    pt.Crop.FlipX,
                    pt.Crop.FlipY,
                    pt.Crop.IsStretched,
                    pt.Crop.AspectRatioX,
                    pt.Crop.AspectRatioY);

                if (texture.Info.Width > pt.Info.Width || texture.Info.Height > pt.Info.Height)
                {
                    int top = crop.Top;
                    int bottom = crop.Bottom;
                    int left = crop.Left;
                    int right = crop.Right;

                    if (top == 0 && bottom == 0)
                    {
                        bottom = Math.Min(texture.Info.Height, pt.Info.Height);
                    }

                    if (left == 0 && right == 0)
                    {
                        right = Math.Min(texture.Info.Width, pt.Info.Width);
                    }

                    crop = new ImageCrop(left, right, top, bottom, crop.FlipX, crop.FlipY, crop.IsStretched, crop.AspectRatioX, crop.AspectRatioY);
                }

                _context.Renderer.Window.Present(texture.HostTexture, crop, swapBuffersCallback);

                pt.ReleaseCallback(pt.UserObj);
            }
        }

        /// <summary>
        /// Indicate that a frame on the queue is ready to be acquired.
        /// </summary>
        public void SignalFrameReady()
        {
            Interlocked.Increment(ref _framesAvailable);
        }

        /// <summary>
        /// Determine if any frames are available, and decrement the available count if there are.
        /// </summary>
        /// <returns>True if a frame is available, false otherwise</returns>
        public bool ConsumeFrameAvailable()
        {
            if (Interlocked.CompareExchange(ref _framesAvailable, 0, 0) != 0)
            {
                Interlocked.Decrement(ref _framesAvailable);

                return true;
            }

            return false;
        }
    }
}
