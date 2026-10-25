using System.Threading;

namespace Ryujinx.Common
{
    /// <summary>
    /// Static per-frame counters for diagnosing SynchronizeMemory behavior.
    /// Incremented by TextureGroup, read/reset by PresentProbe.
    /// </summary>
    public static class SyncMemDiag
    {
        /// <summary>Count of handles where dirty=true but Modified=false → CPU data uploaded over GPU result.</summary>
        private static int _uploads;

        /// <summary>Count of handles where dirty=true and Modified=true → upload suppressed by GPU ownership.</summary>
        private static int _protected;

        /// <summary>Count of handles where dirty=false → no action needed.</summary>
        private static int _clean;

        /// <summary>Count of FlushAction calls (each call clears Modified on one handle).</summary>
        private static int _flushActions;

        public static void IncrementUpload() => Interlocked.Increment(ref _uploads);
        public static void IncrementProtected() => Interlocked.Increment(ref _protected);
        public static void IncrementClean() => Interlocked.Increment(ref _clean);
        public static void IncrementFlushAction() => Interlocked.Increment(ref _flushActions);

        /// <summary>Scissor regions computed with a non-positive width or height. A draw
        /// clipped to nothing still counts as a draw, so this is what the flat per-frame
        /// draw count during the map flicker cannot rule out on its own.</summary>
        private static int _degenerateScissors;

        /// <summary>Clear operations issued.</summary>
        private static int _clears;

        /// <summary>Draws issued, counted in the shared layer so both backends report it.</summary>
        private static int _draws;

        public static void IncrementDegenerateScissor() => Interlocked.Increment(ref _degenerateScissors);
        public static void IncrementClear() => Interlocked.Increment(ref _clears);
        public static void IncrementDraw() => Interlocked.Increment(ref _draws);

        // Extra host syncs from the sync-cadence experiment (RYUJINX_GPU_SYNC_EVERY_DRAWS),
        // counted here so the Metal stats line can report them without the backend having
        // to see the GPU core.
        private static long _extraSyncs;
        public static void IncrementExtraSync() => Interlocked.Increment(ref _extraSyncs);
        public static long SnapshotAndResetExtraSync() => Interlocked.Exchange(ref _extraSyncs, 0);

        /// <summary>Indirect draws - the first draw counter missed these entirely.</summary>
        private static int _indirectDraws;

        /// <summary>DrawTexture (the blit-style path).</summary>
        private static int _drawTextures;

        /// <summary>Render target binding updates, and a rolling hash of what was bound.</summary>
        private static int _rtUpdates;
        private static int _rtHash;

        public static void IncrementIndirectDraw() => Interlocked.Increment(ref _indirectDraws);
        public static void IncrementDrawTexture() => Interlocked.Increment(ref _drawTextures);

        public static void NoteRenderTarget(int identity)
        {
            Interlocked.Increment(ref _rtUpdates);
            Interlocked.Exchange(ref _rtHash, _rtHash * 31 + identity);
        }

        public static (int degenerateScissors, int clears, int draws) SnapshotAndResetDraw()
        {
            return (Interlocked.Exchange(ref _degenerateScissors, 0),
                    Interlocked.Exchange(ref _clears, 0),
                    Interlocked.Exchange(ref _draws, 0));
        }

        /// <summary>Textures added to and removed from the cache. If the map's terrain layer
        /// is drawn once into a texture and sampled thereafter, the per-frame draw counts
        /// cannot see its content going away - but the texture being recreated would show
        /// up here.</summary>
        private static int _texAdd, _texRemove;

        public static void IncrementTexAdd() => Interlocked.Increment(ref _texAdd);
        public static void IncrementTexRemove() => Interlocked.Increment(ref _texRemove);

        /// <summary>Texture pool range invalidations - the pool re-resolving an id can hand
        /// back a different texture than the one the previous frame sampled.</summary>
        private static int _poolInvalidate;

        private static int _poolStale;

        /// <summary>A pool slot handed back a texture that no longer matches its descriptor.</summary>
        public static void IncrementPoolStale() => Interlocked.Increment(ref _poolStale);

        public static int SnapshotPoolStale() => Interlocked.Exchange(ref _poolStale, 0);

        public static void IncrementPoolInvalidate() => Interlocked.Increment(ref _poolInvalidate);

        /// <summary>Compute dispatches and texture-to-texture copies. The map's layers are
        /// composed offscreen, and the per-frame draw counters never covered either path -
        /// so "who writes the layer that goes missing" was still unmeasured.</summary>
        private static int _dispatches, _texCopies;

        public static void IncrementDispatch() => Interlocked.Increment(ref _dispatches);
        public static void IncrementTexCopy() => Interlocked.Increment(ref _texCopies);

        /// <summary>Buffer uploads to the host, and uniform/storage buffer rebinds. With the
        /// command stream identical on flicker frames, what the draws are fed is the last
        /// thing that can still differ - the white flash was exactly that shape.</summary>
        private static int _bufUploads, _ubBinds, _sbBinds;

        public static void IncrementBufUpload() => Interlocked.Increment(ref _bufUploads);
        public static void IncrementUbBind() => Interlocked.Increment(ref _ubBinds);
        public static void IncrementSbBind() => Interlocked.Increment(ref _sbBinds);

        /// <summary>Draws made by one watched program. TOTK's Depths map composites its
        /// terrain layer with a single fullscreen quad per frame, so losing it is invisible
        /// in a per-frame draw total that already jitters by four.</summary>
        private static int _watchedDraws;

        public static void IncrementWatchedDraw() => Interlocked.Increment(ref _watchedDraws);

        public static int SnapshotAndResetWatched() => Interlocked.Exchange(ref _watchedDraws, 0);

        private static int _watchedTex0, _watchedTex1;

        public static void NoteWatchedTex(int t0, int t1)
        {
            Interlocked.Exchange(ref _watchedTex0, t0);
            Interlocked.Exchange(ref _watchedTex1, t1);
        }

        public static (int t0, int t1) SnapshotWatchedTex() => (_watchedTex0, _watchedTex1);

        private static long _ws0, _ws1;

        public static void NoteWatchedSeq(long a, long b) { _ws0 = a; _ws1 = b; }

        public static (long a, long b) SnapshotWatchedSeq() => (_ws0, _ws1);

        /// <summary>Hash of the texture present actually picked this frame.</summary>
        private static int _presentTex;

        public static void NotePresentTex(int h) => Interlocked.Exchange(ref _presentTex, h);

        public static int SnapshotPresentTex() => _presentTex;

        private static float _wc0, _wc1, _wc2;

        public static void NoteWatchedConst(float a, float b, float c)
        {
            _wc0 = a; _wc1 = b; _wc2 = c;
        }

        public static (float a, float b, float c) SnapshotWatchedConst() => (_wc0, _wc1, _wc2);

        public static (int bufUploads, int ubBinds, int sbBinds) SnapshotAndResetBuffer()
        {
            return (Interlocked.Exchange(ref _bufUploads, 0),
                    Interlocked.Exchange(ref _ubBinds, 0),
                    Interlocked.Exchange(ref _sbBinds, 0));
        }

        public static (int dispatches, int texCopies) SnapshotAndResetCompute()
        {
            return (Interlocked.Exchange(ref _dispatches, 0), Interlocked.Exchange(ref _texCopies, 0));
        }

        public static (int added, int removed, int poolInvalidate) SnapshotAndResetTex()
        {
            return (Interlocked.Exchange(ref _texAdd, 0),
                    Interlocked.Exchange(ref _texRemove, 0),
                    Interlocked.Exchange(ref _poolInvalidate, 0));
        }

        public static (int indirect, int drawTexture, int rtUpdates, int rtHash) SnapshotAndResetDraw2()
        {
            return (Interlocked.Exchange(ref _indirectDraws, 0),
                    Interlocked.Exchange(ref _drawTextures, 0),
                    Interlocked.Exchange(ref _rtUpdates, 0),
                    Interlocked.Exchange(ref _rtHash, 0));
        }

        /// <summary>
        /// Snapshot and reset all counters atomically (per-counter).
        /// Call once per present from PresentProbe.
        /// </summary>
        public static (int uploads, int protectedCount, int clean, int flushActions) SnapshotAndReset()
        {
            int u = Interlocked.Exchange(ref _uploads, 0);
            int p = Interlocked.Exchange(ref _protected, 0);
            int c = Interlocked.Exchange(ref _clean, 0);
            int f = Interlocked.Exchange(ref _flushActions, 0);
            return (u, p, c, f);
        }
    }
}
