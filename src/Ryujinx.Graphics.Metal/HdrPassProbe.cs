using Ryujinx.Common.Logging;
using SharpMetal.Metal;
using System;
using System.Runtime.Versioning;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Per frame record of the passes that write the full resolution stages of the
    /// scene chain, so a white frame can be told apart from a good one by what
    /// actually ran.
    ///
    /// The chain sweep narrowed the fault to one stage: the 1600x896 scene colour is
    /// never uniform on a white frame, while the 1920x1080 RG11B10Float target above
    /// it is uniform on essentially every one. This records, for each full resolution
    /// target, how many passes wrote it, how many draws those passes contained and
    /// how many loaded with a clear - which separates the three ways that stage can
    /// come out uniform: no pass ran at all, a pass ran but drew nothing (leaving the
    /// clear), or a pass drew and produced white anyway.
    ///
    /// Bookkeeping only. Nothing here touches a Metal object or ends a pass, so it
    /// cannot move the race being measured.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class HdrPassProbe
    {
        public static bool Enabled => PresentProbe.Enabled;

        // Matches PresentProbe's ring so a frame classified two presents late can still
        // be described.
        private const int Slots = 4;
        // A frame touches more than sixteen targets, and the cap silently dropped the rest -
        // which is how the sRGB view of the present surface came to look as though nothing
        // ever wrote it. Raised, and what still gets dropped is now reported.
        private const int MaxTargets = 64;

        // Below this the target is a shadow map, a luminance reduction or a UI atlas,
        // not a full resolution stage of the chain under study.
        private const int MinWidth = 256;

        private struct Entry
        {
            public IntPtr Target;
            public int Width;
            public int Height;
            public MTLPixelFormat Format;
            public int Passes;
            public ulong Draws;
            public int Cleared;
        }

        private static readonly Entry[][] _slots = CreateSlots();
        private static readonly int[] _slotCount = new int[Slots];

        private static readonly Entry[] _pending = new Entry[MaxTargets];
        private static int _pendingCount;
        private static int _truncated;

        // The pass currently being encoded, so its draws can be attributed on end.
        private static int _openIndex = -1;

        // Per pass detail for the one stage the chain sweep implicated: the full
        // resolution HDR target. A white frame carries exactly one more pass on it than
        // a good frame while the draw total is unchanged, so the extra pass is empty -
        // this records where in the sequence it falls and what ended it.
        // The 1600x896 RG11B10Float stage takes about thirty passes carrying twenty-nine
        // draws - roughly one draw per pass, a post-process chain rather than the scene
        // geometry, which goes to the RGBA8 target at some 1500 draws. Twelve entries cut
        // that chain off two thirds of the way through, so the walk could never reach the
        // pass that matters. A flat frame also carries about nine more passes than its
        // predecessor at the same draw total, and those empty passes are only visible if
        // the whole sequence fits.
        private const int MaxWatched = 40;

        // Which RG11B10Float stage the per-pass content sampling follows. It was fixed at
        // the full resolution composite; now that the composite has been shown to read an
        // already-white 1600x896 scene texture, the same walk has to run one stage earlier,
        // and the stage after that if it comes to it. Exact width rather than a minimum, so
        // pointing it at 1600 does not also pick up 1920 and interleave two chains in one
        // sequence.
        private static readonly int WatchedWidth =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_WATCH_WIDTH"), out int w) && w > 0 ? w : 1920;

        private struct PassDetail
        {
            public ulong Draws;
            public PassEndReason Reason;
            public bool Cleared;

            // Which pass of the frame this sample was taken before, so the sequence
            // brackets where in the frame the content changed. The walk only fires on
            // encoder transitions - eight samples across ~170 passes - so without the
            // ordinal the reading "varied ... varied, then white at present" cannot say
            // what ran in between.
            public int Ordinal;
        }

        private static readonly PassDetail[][] _slotWatched = CreateWatched();

        // The op-ring sequence at each chart sample point, so the flip between two
        // samples names an interval of actual operations rather than a span of time.
        private static readonly long[][] _slotSampleSeq = CreateSampleSeq();
        private static readonly long[] _pendingSampleSeq = new long[MaxWatched + 1];

        // The raw handle each chart entry actually sampled. The chart interleaves both
        // 1600x896 textures - their traffic is wildly asymmetric - so "varied then white"
        // may be texture A then texture B rather than one texture flipping. With the
        // handle on every entry, one WHITE block decides it.
        private static readonly IntPtr[][] _slotSampleHandle = CreateSampleHandles();
        private static readonly IntPtr[] _pendingSampleHandle = new IntPtr[MaxWatched + 1];

        private static IntPtr[][] CreateSampleHandles()
        {
            IntPtr[][] slots = new IntPtr[Slots][];

            for (int i = 0; i < Slots; i++)
            {
                slots[i] = new IntPtr[MaxWatched + 1];
            }

            return slots;
        }

        private static long[][] CreateSampleSeq()
        {
            long[][] slots = new long[Slots][];

            for (int i = 0; i < Slots; i++)
            {
                slots[i] = new long[MaxWatched + 1];
            }

            return slots;
        }
        private static readonly int[] _slotWatchedCount = new int[Slots];

        private static readonly PassDetail[] _pendingWatched = new PassDetail[MaxWatched];
        private static int _pendingWatchedCount;

        private static bool _openWatched;
        private static Texture _openWatchedTarget;

        // Per pass content of the watched target. Knowing only its state at present
        // time says the stage ends up white but not which pass made it so; sampling
        // after every pass that writes it names the draws responsible.
        private const int SamplePixels = 9;
        private const int SampleBytes = SamplePixels * 4;

        private static MTLBuffer _sampleBuf;
        private static MTLDevice _device;

        // What the watched stage READ. Every instrument so far measured the write side -
        // which target, how many passes, how many draws - and all of those come out
        // identical on a white frame. Identical draws producing white can only be a
        // difference in what they sampled, so this records the distinct textures bound
        // for the draws that write the watched target.
        private const int MaxSampled = 12;

        private static readonly IntPtr[][] _slotSampled = CreateSampled();
        private static readonly int[] _slotSampledCount = new int[Slots];

        private static readonly IntPtr[] _pendingSampled = new IntPtr[MaxSampled];
        private static readonly Texture[] _pendingSampledTex = new Texture[MaxSampled];
        private static int _pendingSampledCount;

        // Latched on first sight so the readback keeps sampling the same set even as
        // bindings churn; the set was already shown to be identical on white and good
        // frames, so what is left to compare is their contents.
        private static readonly Texture[] _watchTex = new Texture[MaxSampled];
        private static int _watchCount;

        private static IntPtr[][] CreateSampled()
        {
            IntPtr[][] slots = new IntPtr[Slots][];

            for (int i = 0; i < Slots; i++)
            {
                slots[i] = new IntPtr[MaxSampled];
            }

            return slots;
        }

        /// <summary>
        /// Whether this target is the stage the chain sweep implicated.
        /// </summary>
        /// <summary>
        /// Identity that survives views. A view carries its own MTLTexture pointer, so
        /// comparing handles or object references misses every write made through one.
        /// </summary>
        private static IntPtr RootOf(Texture t)
        {
            if (t == null)
            {
                return IntPtr.Zero;
            }

            // Canonical identity only. The old fallback chain (ViewRootPtr, else the
            // swizzled handle) hashed one resource to different values depending on which
            // path observed it, which is why several "nothing writes this" results in the
            // white-flash notes were false.
            return t.CanonicalPtr != IntPtr.Zero ? t.CanonicalPtr : t.GetHandle().NativePtr;
        }

        /// <summary>
        /// The scene texture actually worth following, latched by how much is drawn into
        /// it rather than by size and format.
        ///
        /// Two 1600x896 RG11B10Float textures exist per frame - one taking around 1528
        /// draws and one taking only a clear - so matching on width and format picks
        /// whichever the current pass happens to target, and the content walk has been
        /// following them interchangeably. Latching the one with draws makes the walk
        /// follow the texture the composite samples, which is the one whose end-of-frame
        /// state becomes the next frame's picture.
        /// </summary>
        private static IntPtr _watchRoot;

        public static bool IsWatchedTarget(Texture target)
        {
            if (target == null || target.Width != WatchedWidth ||
                target.MtlFormat != MTLPixelFormat.RG11B10Float)
            {
                return false;
            }

            // Both identities chart. The census shows the storage under at least two roots,
            // one taking a single zero-draw pass per frame, and a chart keyed to one root
            // leaves that pass - which sits somewhere in the window where the content
            // flips - off the map entirely.
            return true;
        }

        private static void LatchWatchRoot(int slot)
        {
            Entry[] entries = _slots[slot];

            for (int i = 0; i < _slotCount[slot]; i++)
            {
                if (entries[i].Width == WatchedWidth &&
                    entries[i].Format == MTLPixelFormat.RG11B10Float &&
                    entries[i].Draws > 100)
                {
                    if (_watchRoot != entries[i].Target)
                    {
                        _watchRoot = entries[i].Target;

                        Logger.Warning?.PrintMsg(LogClass.Gpu,
                            $"hdrwatch: following 0x{_watchRoot:X} ({entries[i].Draws} draws over {entries[i].Passes} passes)");
                    }

                    return;
                }
            }
        }

        // The GPU memory dumps put the fault between the 1600x896 scene targets, none of
        // which ever hold the flat value, and the 1920x1080 composite, which does. This
        // names the shaders that span the two by reporting every distinct
        // sampled-size -> target-size pair, so the composite draw can be identified from
        // what it actually reads rather than from an assumption about it.
        private static readonly System.Collections.Generic.HashSet<string> _spanSeen = [];

        private static readonly System.Collections.Generic.HashSet<string> _copySeen = [];

        // Every identity a full resolution texture answers to. A view carries its own
        // MTLTexture pointer, and a write made through one is attributed to whichever
        // identity the bookkeeping happened to use - which is exactly how an earlier
        // "nothing ever writes the presented image" conclusion in these notes came about
        // and had to be retracted. Printing all three per texture makes the collision
        // visible instead of leaving two records that never line up.
        private static readonly System.Collections.Generic.HashSet<string> _identSeen = [];

        public static void NoteIdentity(string role, Texture t)
        {
            // 1500 rather than 1900, so the two 1600x896 RG11B10Float textures are both
            // printed. One takes 28 draws and one takes none, the composite samples the
            // second, and whether they are views of one storage or separate allocations
            // decides which fault this is: an ordering problem between a write through one
            // and a read through the other, or a copy that has to connect them and is late.
            if (t == null || t.Width < 1500)
            {
                return;
            }

            string key = $"{role}:0x{t.GetHandle().NativePtr:X}";

            lock (_identSeen)
            {
                if (!_identSeen.Add(key))
                {
                    return;
                }
            }

            Logger.Warning?.PrintMsg(LogClass.Gpu,
                $"hdrident {role} {t.Width}x{t.Height} {t.MtlFormat} " +
                $"handle=0x{t.GetHandle().NativePtr:X} canonical=0x{t.CanonicalPtr:X} " +
                $"viewRoot=0x{t.ViewRootPtr:X}");
        }

        // The composite's own draws, by shader. hdrspan mixed in the UI shaders that
        // write the sRGB view of the same storage; this counts only draws whose target is
        // the RG11B10Float composite, which is where the 96-97 draws land.
        private static readonly System.Collections.Generic.Dictionary<string, int> _compositeDraws = new();

        public static void NoteCompositeDraw(string program, Texture target)
        {
            if (program == null || !IsWatchedTarget(target))
            {
                return;
            }

            lock (_compositeDraws)
            {
                _compositeDraws.TryGetValue(program, out int n);
                _compositeDraws[program] = n + 1;

                if ((n + 1) % 2000 != 0)
                {
                    return;
                }
            }

            System.Text.StringBuilder sb = new();

            lock (_compositeDraws)
            {
                foreach (var kv in _compositeDraws)
                {
                    sb.Append($" {kv.Key}={kv.Value}");
                }
            }

            Logger.Warning?.PrintMsg(LogClass.Gpu, $"hdrcomposite{sb}");
        }

        /// <summary>
        /// Every copy landing on a scene-sized RG11B10Float texture, per frame, keyed on
        /// the raw handles.
        ///
        /// The scene renders into one 1600x896 RG11B10Float texture and the composite
        /// samples a different one - both base textures, neither a view of the other - so a
        /// copy has to connect them every frame, and on a flat frame the composite reads
        /// before it lands or it never runs. This counts them.
        ///
        /// Two rules from these notes are deliberately applied. No identity matching: the
        /// existing hooks compare RootOf against the tracked input list, which is how
        /// nonRenderWrites reported none while a copy was evidently happening. And no width
        /// filter above 1600: NoteCopy's own dstWidth >= 1900 is exactly what kept this
        /// destination out of the log until now.
        /// </summary>
        private const int MaxSceneCopies = 8;

        private static readonly (IntPtr Src, IntPtr Dst)[][] _slotSceneCopy = CreateSceneCopies();
        private static readonly int[] _slotSceneCopyCount = new int[Slots];
        private static readonly (IntPtr Src, IntPtr Dst)[] _pendingSceneCopy = new (IntPtr, IntPtr)[MaxSceneCopies];
        private static int _pendingSceneCopyCount;
        private static int _pendingSceneCopyDropped;
        private static readonly int[] _slotSceneCopyDropped = new int[Slots];

        private static (IntPtr, IntPtr)[][] CreateSceneCopies()
        {
            (IntPtr, IntPtr)[][] slots = new (IntPtr, IntPtr)[Slots][];

            for (int i = 0; i < Slots; i++)
            {
                slots[i] = new (IntPtr, IntPtr)[MaxSceneCopies];
            }

            return slots;
        }

        public static void NoteSceneCopy(Texture source, Texture destination)
        {
            if (!Enabled || destination == null || destination.Width != 1600 ||
                destination.MtlFormat != MTLPixelFormat.RG11B10Float)
            {
                return;
            }

            if (_pendingSceneCopyCount >= MaxSceneCopies)
            {
                _pendingSceneCopyDropped++;

                return;
            }

            _pendingSceneCopy[_pendingSceneCopyCount++] =
                (source?.GetHandle().NativePtr ?? IntPtr.Zero, destination.GetHandle().NativePtr);
        }

        /// <summary>
        /// Clears landing on a scene-sized RG11B10Float target, with the colour.
        ///
        /// ClearRenderTargetColor goes through a helper draw rather than a load action, so
        /// it opens a pass the census counts and issues a draw the census does not - which
        /// is what p=1 d=0 means on the texture the composite samples, and why the stain
        /// placed at creation is overwritten while no copy hook ever fires.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> _clearSeen = [];

        public static void NoteSceneClear(Texture destination, Ryujinx.Graphics.GAL.ColorF color)
        {
            if (!Enabled || destination == null || destination.Width != 1600 ||
                destination.MtlFormat != MTLPixelFormat.RG11B10Float)
            {
                return;
            }

            NoteSceneCopy(null, destination);

            string key = $"0x{destination.GetHandle().NativePtr:X}:{color.Red:F3},{color.Green:F3},{color.Blue:F3},{color.Alpha:F3}";

            lock (_clearSeen)
            {
                if (!_clearSeen.Add(key))
                {
                    return;
                }
            }

            Logger.Warning?.PrintMsg(LogClass.Gpu, $"sceneclear {key}");
        }

        /// <summary>
        /// Compute dispatches that bind the watched storage as a writable image, with the
        /// pass ordinal counter's value at bind time.
        ///
        /// The flat state lands in the last two passes of the causing frame, in a window
        /// whose only render pass has zero draws and no clear - which cannot change
        /// content. Copies, render blits and SetData are hooked and silent. A compute
        /// image write is the one mechanism none of that covers, it is what would have
        /// beaten the creation-time stain, and the frame runs about 38 dispatches.
        /// </summary>
        private static int _pendingComputeImageBinds;
        private static int _pendingComputeImageLastOrdinal = -1;
        private static readonly int[] _slotComputeImageBinds = new int[Slots];
        private static readonly int[] _slotComputeImageLastOrdinal = new int[Slots];

        public static void NoteComputeImage(Texture storage)
        {
            // Width and format, not root. The sibling sampling proved two different roots
            // share this content - CanonicalPtr on a view of a view names the intermediate
            // view, not the base - so filtering on the latched root would miss a dispatch
            // binding the same storage through another identity, exactly the way every
            // other identity-keyed hook in these notes has missed its target.
            if (!Enabled || storage == null || storage.Width != 1600 ||
                storage.MtlFormat != MTLPixelFormat.RG11B10Float)
            {
                return;
            }

            _pendingComputeImageBinds++;
            _pendingComputeImageLastOrdinal = _passOrdinal;
        }

        public static string DescribeComputeImage(int slot)
        {
            return _slotComputeImageBinds[slot] == 0
                ? "never bound as image"
                : $"bound as writable image {_slotComputeImageBinds[slot]}x, last at ordinal {_slotComputeImageLastOrdinal[slot]}";
        }

        public static string DescribeSceneCopies(int slot)
        {
            int count = _slotSceneCopyCount[slot];

            if (count == 0)
            {
                return _slotSceneCopyDropped[slot] > 0
                    ? $"none recorded (+{_slotSceneCopyDropped[slot]} past the limit)"
                    : "NONE";
            }

            StringBuilder sb = new();

            for (int i = 0; i < count; i++)
            {
                sb.Append($" 0x{_slotSceneCopy[slot][i].Src:X}->0x{_slotSceneCopy[slot][i].Dst:X}");
            }

            if (_slotSceneCopyDropped[slot] > 0)
            {
                sb.Append($" (+{_slotSceneCopyDropped[slot]} past the limit)");
            }

            return sb.ToString();
        }

        public static void NoteCopy(int srcW, int srcH, string srcFmt, int dstW, int dstH, string dstFmt)
        {
            string key = $"{srcW}x{srcH}:{srcFmt}->{dstW}x{dstH}:{dstFmt}";

            lock (_copySeen)
            {
                if (!_copySeen.Add(key))
                {
                    return;
                }
            }

            Logger.Warning?.PrintMsg(LogClass.Gpu, $"hdrcopy {key}");
        }

        public static void NoteSpan(string program, int srcW, int srcH, int dstW, int dstH)
        {
            if (program == null || dstW < 900)
            {
                return;
            }

            lock (_spanSeen)
            {
                if (!_spanSeen.Add($"{program}:{srcW}x{srcH}->{dstW}x{dstH}"))
                {
                    return;
                }
            }

            Logger.Warning?.PrintMsg(LogClass.Gpu,
                $"hdrspan program={program} samples {srcW}x{srcH} -> writes {dstW}x{dstH}");
        }

        public static void NoteSampled(IntPtr texture, TextureBase storage = null)
        {
            if (texture == IntPtr.Zero)
            {
                return;
            }

            for (int i = 0; i < _pendingSampledCount; i++)
            {
                if (_pendingSampled[i] == texture)
                {
                    return;
                }
            }

            if (_pendingSampledCount < MaxSampled)
            {
                _pendingSampledTex[_pendingSampledCount] = storage as Texture;
                _pendingSampled[_pendingSampledCount++] = texture;

                if (_watchCount < MaxSampled && storage is Texture t && t.Width >= 256)
                {
                    for (int i = 0; i < _watchCount; i++)
                    {
                        if (ReferenceEquals(_watchTex[i], t))
                        {
                            return;
                        }
                    }

                    _watchTex[_watchCount++] = t;
                }
            }
        }

        private static PassDetail[][] CreateWatched()
        {
            PassDetail[][] slots = new PassDetail[Slots][];

            for (int i = 0; i < Slots; i++)
            {
                slots[i] = new PassDetail[MaxWatched];
            }

            return slots;
        }

        private static Entry[][] CreateSlots()
        {
            Entry[][] slots = new Entry[Slots][];

            for (int i = 0; i < Slots; i++)
            {
                slots[i] = new Entry[MaxTargets];
            }

            return slots;
        }

        /// <summary>
        /// Called as a render pass descriptor is built, with its first colour attachment.
        /// </summary>
        /// <summary>
        /// Records every colour attachment of the pass, not just the first. Only tracking
        /// attachment 0 is why a good frame's present source appeared to have no writer:
        /// a pass that binds it at attachment 1 or above was invisible.
        /// </summary>
        // Every target attached this pass, so EndPass can credit the draws to all of them.
        // Crediting only colour target 0 made any texture that is exclusively a secondary
        // attachment report d=0 - which reads as "nothing ever draws into this" and is a
        // bookkeeping artefact, not a fact about the frame. A conclusion was nearly built
        // on one of those zeroes.
        private static readonly int[] _openIndices = new int[Constants.MaxColorAttachments];
        private static int _openIndexCount;

        /// <summary>
        /// Where the composite pass falls in the frame relative to the scene passes.
        ///
        /// The one fact that survives every check is that the composite fetches white out of
        /// the scene texture while that same texture holds the scene by the end of the
        /// frame. Barriers, residency and command buffer splitting have all been ruled out
        /// at adequate power, which leaves the plain ordering of the passes themselves: if
        /// the composite is encoded before the scene has finished writing, it reads what was
        /// there before.
        ///
        /// Counting ordinals is enough to answer it and costs nothing - no Metal call, no
        /// pass split. A flat frame whose composite ordinal sits below its last scene
        /// ordinal, against a predecessor where it sits above, is the fault stated in one
        /// line.
        /// </summary>
        private static int _passOrdinal;
        private static int _pendingCompositeOrdinal = -1;
        private static int _pendingLastSceneOrdinal = -1;
        private static readonly int[] _slotCompositeOrdinal = new int[Slots];
        private static readonly int[] _slotLastSceneOrdinal = new int[Slots];
        private static readonly int[] _slotPassTotal = new int[Slots];

        public static string DescribeOrdinals(int slot)
        {
            return $"composite@{_slotCompositeOrdinal[slot]} lastScene@{_slotLastSceneOrdinal[slot]} of {_slotPassTotal[slot]}" +
                (_slotCompositeOrdinal[slot] >= 0 && _slotLastSceneOrdinal[slot] > _slotCompositeOrdinal[slot]
                    ? "  <-- composite encoded BEFORE the last scene pass"
                    : string.Empty);
        }

        /// <summary>
        /// Two other 1600-wide attachments of the same pass set, sampled at present beside
        /// the one the composite reads.
        ///
        /// With attachment crediting corrected, three 1600x896 targets report 1502 draws
        /// over 18 passes each - MRT siblings of one pass set - and the texture the
        /// composite samples sits just above them. So on a frame that causes a flash, one
        /// attachment ends in a one-step range while the frame it belongs to displays
        /// correctly. Whether its siblings do the same separates a fault in this attachment
        /// from one shared by the whole pass set.
        ///
        /// Sampled at present, where the blit encoder already exists, so this costs nothing
        /// in timing - which matters, because per-pass sampling would cost about 170 encoder
        /// switches a frame and that is the intervention already measured to move the rate.
        /// </summary>
        private static readonly Texture[] _siblingTex = new Texture[2];

        private static void LatchSibling(Texture target)
        {
            if (target == null || target.Width != 1600 || RootOf(target) == _watchRoot)
            {
                return;
            }

            for (int i = 0; i < _siblingTex.Length; i++)
            {
                if (_siblingTex[i] != null)
                {
                    if (RootOf(_siblingTex[i]) == RootOf(target))
                    {
                        return;
                    }

                    continue;
                }

                _siblingTex[i] = target;

                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"hdrsibling {i}: 0x{RootOf(target):X} {target.Width}x{target.Height} {target.MtlFormat}");

                return;
            }
        }

        private static void NoteOrdinal(Texture target)
        {
            LatchSibling(target);

            if (target == null || target.MtlFormat != MTLPixelFormat.RG11B10Float)
            {
                return;
            }

            if (target.Width >= 1900 && _pendingCompositeOrdinal < 0)
            {
                _pendingCompositeOrdinal = _passOrdinal;
            }
            else if (target.Width == 1600)
            {
                _pendingLastSceneOrdinal = _passOrdinal;
            }
        }

        public static void BeginPassAll(Texture[] targets, bool clearLoadAction)
        {
            _passOrdinal++;

            for (int i = 0; i < targets.Length; i++)
            {
                NoteOrdinal(targets[i]);
            }

            _openIndexCount = 0;

            BeginPass(targets.Length > 0 ? targets[0] : null, clearLoadAction);

            if (_openIndex >= 0)
            {
                _openIndices[_openIndexCount++] = _openIndex;
            }

            for (int i = 1; i < targets.Length; i++)
            {
                RecordTarget(targets[i], clearLoadAction);
            }
        }

        private static void RecordTarget(Texture target, bool clearLoadAction)
        {
            if (target == null || target.Width < MinWidth)
            {
                return;
            }

            // The chain previously charted only passes where the watched storage is colour
            // target 0 - seven per frame - while the census counts 22-24. The passes where
            // it is a secondary attachment were neither charted nor sampled, and the flat
            // writer can be one of them. Chart them all; content sampling still only
            // happens at encoder transitions, so this adds bookkeeping and nothing else.
            if (IsWatchedTarget(target) && _pendingWatchedCount < MaxWatched && !_openWatched)
            {
                _pendingWatched[_pendingWatchedCount] = new PassDetail { Cleared = clearLoadAction, Ordinal = _passOrdinal };
                _openWatched = true;
                _openWatchedTarget = target;
            }

            IntPtr handle = RootOf(target);

            if (handle == IntPtr.Zero)
            {
                return;
            }

            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pending[i].Target == handle)
                {
                    _pending[i].Passes++;
                    NoteOpenIndex(i);
                    return;
                }
            }

            if (_pendingCount == MaxTargets)
            {
                _truncated++;
                return;
            }

            _pending[_pendingCount] = new Entry
            {
                Target = handle,
                Width = target.Width,
                Height = target.Height,
                Format = target.MtlFormat,
                Passes = 1,
            };

            NoteOpenIndex(_pendingCount);

            _pendingCount++;
        }

        private static void NoteOpenIndex(int index)
        {
            if (_openIndexCount < _openIndices.Length)
            {
                _openIndices[_openIndexCount++] = index;
            }
        }

        public static void BeginPass(Texture target, bool clearLoadAction)
        {
            _openIndex = -1;
            _openWatched = false;

            if (target == null || target.Width < MinWidth)
            {
                return;
            }

            if (IsWatchedTarget(target) && _pendingWatchedCount < MaxWatched)
            {
                _pendingWatched[_pendingWatchedCount] = new PassDetail { Cleared = clearLoadAction, Ordinal = _passOrdinal };
                _openWatched = true;
                _openWatchedTarget = target;
            }

            IntPtr handle = RootOf(target);

            if (handle == IntPtr.Zero)
            {
                return;
            }

            int index = -1;

            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pending[i].Target == handle)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                if (_pendingCount == MaxTargets)
                {
                    _truncated++;

                    return;
                }

                index = _pendingCount++;

                _pending[index] = new Entry
                {
                    Target = handle,
                    Width = target.Width,
                    Height = target.Height,
                    Format = target.MtlFormat,
                };
            }

            _pending[index].Passes++;

            if (clearLoadAction)
            {
                _pending[index].Cleared++;
            }

            _openIndex = index;
        }

        /// <summary>
        /// Called when that pass ends, with the draws it contained.
        /// </summary>
        public static void Initialize(MTLDevice device)
        {
            _device = device;
            _inputBuf = device.NewBuffer(
                (ulong)(Slots * InputsPerFrame * InputBytes),
                MTLResourceOptions.ResourceStorageModeShared);

            _sampleBuf = device.NewBuffer(
                (ulong)(Slots * MaxWatched * SampleBytes),
                MTLResourceOptions.ResourceStorageModeShared);
        }

        /// <summary>
        /// Blits a 3x3 grid out of the watched target right after a pass that wrote it.
        /// The pass has already ended, so this only adds a blit encoder between passes.
        /// </summary>
        /// <summary>
        /// Samples the watched target just BEFORE a pass that writes it, so the sequence
        /// across a frame says which pass left it white.
        ///
        /// Must be called from a point that is allowed to switch encoders. An earlier
        /// version sampled from the pass-ended callback, where the render encoder has not
        /// been closed yet, and Metal asserted on the blit encoder it tried to open.
        /// </summary>
        /// <summary>
        /// The last watched target seen this frame, kept after the pass closes so the
        /// content can be sampled once more at present.
        ///
        /// The chain above samples before each pass, which reads as "after the previous
        /// one" for every pass but the last - and the last one's output is exactly what the
        /// next stage reads. Walking the 1600x896 stage produced eight "varied" entries and
        /// no verdict for that reason, while the shader reading this texture was measuring
        /// 254 in every channel. The missing sample is the whole answer.
        /// </summary>
        private static Texture _lastWatchedTarget;

        // The watched target whose pass just ended, consumed by SampleBoundary from
        // Pipeline.EndCurrentPass - the only point where a blit is legal again and the
        // pass's output actually exists.
        private static Texture _justEndedWatched;

        public static void SampleBoundary(CommandBufferScoped cbs, int frameSlot)
        {
            if (_justEndedWatched == null)
            {
                return;
            }

            Texture t = _justEndedWatched;
            _justEndedWatched = null;

            SampleWatched(cbs, t, frameSlot, _pendingWatchedCount);

            if (_pendingWatchedCount <= MaxWatched)
            {
                _pendingSampleSeq[_pendingWatchedCount] = OpRing.Seq;
            }
        }

        public static void SampleAtPresent(CommandBufferScoped cbs, int frameSlot)
        {
            if (_lastWatchedTarget == null)
            {
                return;
            }

            if (_pendingWatchedCount >= MaxWatched)
            {
                return;
            }

            SampleWatched(cbs, _lastWatchedTarget, frameSlot, _pendingWatchedCount);

            // Claim the slot so Commit carries it and DescribePassContent prints it as the
            // final entry. It belongs to no pass, hence the zero draws.
            _pendingWatched[_pendingWatchedCount] = new PassDetail { Ordinal = _passOrdinal };
            _pendingWatchedCount++;
            _lastWatchedTarget = null;
        }

        public static void SampleBeforePass(CommandBufferScoped cbs, Texture target, int frameSlot)
        {
            if (_sampleBuf.NativePtr == IntPtr.Zero || !IsWatchedTarget(target))
            {
                return;
            }

            _openWatchedTarget = target;
            _lastWatchedTarget = target;

            SampleWatched(cbs, target, frameSlot, _pendingWatchedCount);
        }

        private static void SampleWatched(CommandBufferScoped cbs, Texture target, int frameSlot, int passIndex)
        {
            if (_sampleBuf.NativePtr == IntPtr.Zero || target == null)
            {
                return;
            }

            if (passIndex <= MaxWatched)
            {
                _pendingSampleSeq[passIndex] = OpRing.Seq;
                _pendingSampleHandle[passIndex] = target.GetHandle().NativePtr;
            }

            _openWatchedTarget = target;

            if (passIndex >= MaxWatched)
            {
                return;
            }

            MTLTexture tex = _openWatchedTarget.GetHandle();

            if (tex.NativePtr == IntPtr.Zero)
            {
                return;
            }

            int w = _openWatchedTarget.Width;
            int h = _openWatchedTarget.Height;
            ulong baseOffset = (ulong)((frameSlot * MaxWatched + passIndex) * SampleBytes);

            MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();

            for (int gy = 0; gy < 3; gy++)
            {
                for (int gx = 0; gx < 3; gx++)
                {
                    blit.CopyFromTexture(
                        tex, 0, 0,
                        new MTLOrigin { x = (ulong)(w * (gx + 1) / 4), y = (ulong)(h * (gy + 1) / 4), z = 0 },
                        new MTLSize { width = 1, height = 1, depth = 1 },
                        _sampleBuf,
                        baseOffset + (ulong)((gy * 3 + gx) * 4),
                        4, 4);
                }
            }
        }

        /// <summary>
        /// Which pass first left the watched target uniform, read back once the frame is
        /// long finished. RG11B10Float packs into 32 bits, so equal words mean equal
        /// texels without needing to decode the format.
        /// </summary>
        public static unsafe string DescribePassContent(int slot)
        {
            if (_sampleBuf.NativePtr == IntPtr.Zero)
            {
                return "n/a";
            }

            int count = _slotWatchedCount[slot];
            StringBuilder sb = new();
            uint* words = (uint*)_sampleBuf.Contents;

            for (int p = 0; p < count && p < MaxWatched; p++)
            {
                uint* px = words + (slot * MaxWatched + p) * SamplePixels;
                uint min = uint.MaxValue, max = 0;

                for (int i = 0; i < SamplePixels; i++)
                {
                    if (px[i] < min) min = px[i];
                    if (px[i] > max) max = px[i];
                }

                if (sb.Length > 0)
                {
                    sb.Append(" -> ");
                }

                sb.Append($"[@{_slotWatched[slot][p].Ordinal}]0x{(ulong)_slotSampleHandle[slot][p]:X}:d{_slotWatched[slot][p].Draws}{(_slotWatched[slot][p].Cleared ? "c" : "")}:");

                // Strict equality could not tell the scene from the flat state: the flat
                // value spans one quantisation step (0x800), which fails equality just as
                // a real scene does, so every entry printed "varied" and the sequence said
                // nothing. Classify by span instead - the scene spans hundreds of steps.
                ulong span = (ulong)max - min;

                sb.Append(span == 0 ? $"UNIFORM(0x{px[0]:X8})"
                    : span <= 0x1000 ? $"STEP(0x{min:X8})"
                    : "varied");
            }

            return sb.Length == 0 ? "none" : sb.ToString();
        }

        /// <summary>
        /// The operation slice between the last varied sample and the first uniform one -
        /// the interval that contains the writer, whoever it is. "none" with a non-empty
        /// interval is itself the answer: no command wrote it, so the CPU did.
        /// </summary>
        public static unsafe string DescribeFlipOps(int slot)
        {
            if (_sampleBuf.NativePtr == IntPtr.Zero)
            {
                return "n/a";
            }

            int count = _slotWatchedCount[slot];
            uint* words = (uint*)_sampleBuf.Contents;
            int prevVaried = -1;

            for (int p = 0; p < count && p < MaxWatched; p++)
            {
                uint* px = words + (slot * MaxWatched + p) * SamplePixels;
                bool uniform = true;

                for (int i = 1; i < SamplePixels; i++)
                {
                    if (px[i] != px[0])
                    {
                        uniform = false;
                        break;
                    }
                }

                if (_slotSampleSeq[slot][p] < 0)
                {
                    continue; // not freshly sampled this frame - stale pixels, skip
                }

                // Only the white signature counts as a flip. The first uniform of any
                // value is the frame's ordinary black clear, and returning on it hid the
                // white flip behind it on every frame.
                bool white = uniform && px[0] >= 0x77D00000u && px[0] <= 0x78200000u;

                if (!white)
                {
                    prevVaried = p;
                }
                else if (prevVaried >= 0)
                {
                    // The flipped-to pass's own draw count and load action, inline: d0
                    // with clr=False makes that pass's load action the prime suspect for
                    // this one pass, whatever the global audit said about the others.
                    PassDetail d = p < MaxWatched ? _slotWatched[slot][p] : default;

                    return $"flip@{prevVaried}->{p}(0x{px[0]:X8},d{d.Draws},clr={d.Cleared}) ops:" +
                        OpRing.Describe(_slotSampleSeq[slot][prevVaried], _slotSampleSeq[slot][p]);
                }
            }

            return "no flip";
        }

        public static void EndPass(ulong drawsInPass, PassEndReason reason)
        {
            // Credit the draws to every attachment of this pass, not just colour target 0.
            for (int i = 0; i < _openIndexCount; i++)
            {
                _pending[_openIndices[i]].Draws += drawsInPass;
            }

            _openIndexCount = 0;
            _openIndex = -1;

            if (_openWatched)
            {
                _pendingWatched[_pendingWatchedCount].Draws = drawsInPass;
                _pendingWatched[_pendingWatchedCount].Reason = reason;
                _pendingWatchedCount++;
                _openWatched = false;
                _justEndedWatched = _openWatchedTarget;
                _openWatchedTarget = null;
            }
        }

        /// <summary>
        /// Moves the frame just encoded into its slot and starts a fresh accumulator.
        /// </summary>
        public static void Commit(int slot)
        {
            Entry[] target = _slots[slot];

            for (int i = 0; i < _pendingCount; i++)
            {
                target[i] = _pending[i];
            }

            _slotCount[slot] = _pendingCount;

            LatchWatchRoot(slot);

            PassDetail[] watched = _slotWatched[slot];

            for (int i = 0; i < _pendingWatchedCount; i++)
            {
                watched[i] = _pendingWatched[i];
            }

            _slotWatchedCount[slot] = _pendingWatchedCount;

            long[] seqs = _slotSampleSeq[slot];

            IntPtr[] sampleHandles = _slotSampleHandle[slot];

            for (int i = 0; i <= MaxWatched; i++)
            {
                seqs[i] = _pendingSampleSeq[i];
                _pendingSampleSeq[i] = -1;
                sampleHandles[i] = _pendingSampleHandle[i];
                _pendingSampleHandle[i] = IntPtr.Zero;
            }

            IntPtr[] toneHandles = _slotToneHandle[slot];

            for (int i = 0; i < MaxToneInputs; i++)
            {
                toneHandles[i] = _pendingToneHandle[i];
            }

            (IntPtr, IntPtr)[] sceneCopies = _slotSceneCopy[slot];

            for (int i = 0; i < _pendingSceneCopyCount; i++)
            {
                sceneCopies[i] = _pendingSceneCopy[i];
            }

            _slotCompositeOrdinal[slot] = _pendingCompositeOrdinal;
            _slotLastSceneOrdinal[slot] = _pendingLastSceneOrdinal;
            _slotPassTotal[slot] = _passOrdinal;
            _pendingCompositeOrdinal = -1;
            _pendingLastSceneOrdinal = -1;
            _passOrdinal = 0;

            _slotComputeImageBinds[slot] = _pendingComputeImageBinds;
            _slotComputeImageLastOrdinal[slot] = _pendingComputeImageLastOrdinal;
            _pendingComputeImageBinds = 0;
            _pendingComputeImageLastOrdinal = -1;

            _slotSceneCopyCount[slot] = _pendingSceneCopyCount;
            _slotSceneCopyDropped[slot] = _pendingSceneCopyDropped;
            _pendingSceneCopyCount = 0;
            _pendingSceneCopyDropped = 0;

            IntPtr[] sampled = _slotSampled[slot];

            for (int i = 0; i < _pendingSampledCount; i++)
            {
                sampled[i] = _pendingSampled[i];
            }

            _slotSampledCount[slot] = _pendingSampledCount;

            _slotW[slot][0] = _pendingW0; _slotW[slot][1] = _pendingW1;
            _slotW[slot][2] = _pendingW2; _slotW[slot][3] = _pendingW3;
            _slotWSeen[slot] = _pendingWSeen;
            _pendingWSeen = false;

            _slotArgId[slot] = _pendingArgId;
            _slotTexResId[slot] = _pendingTexId;

            _slotOrder[slot] = _wSeen
                ? (_toneSeen
                    ? $"sceneWrites[{_wFirst}..{_wLast}] tonemapAt={_toneAt} {(_toneAt < _wFirst ? "TONEMAP-FIRST" : "writes-first")}"
                    : $"sceneWrites[{_wFirst}..{_wLast}] tonemap=absent")
                : (_toneSeen ? $"noSceneWrites tonemapAt={_toneAt}" : "neither");

            _wSeen = false;
            _toneSeen = false;
            _writeIndexThisFrame = 0;

            lock (_pendingWrites)
            {
                _slotWrites[slot] = _pendingWrites.Count == 0 ? "none" : string.Join(",", _pendingWrites);
                _pendingWrites.Clear();
            }

            _slotMaxAbs[slot] = _pendingMaxAbs;
            _slotNonFinite[slot] = _pendingNonFinite;
            _slotScanned[slot] = _pendingScanned;

            _pendingCount = 0;
            _pendingWatchedCount = 0;
            _pendingSampledCount = 0;
            _pendingMaxAbs = 0f;
            _pendingNonFinite = 0;
            _pendingScanned = 0;
            _openIndex = -1;
            _openWatched = false;
        }

        // The last unexamined input: the constants those draws read. The presented white
        // decodes to ~0.99, not an overflowed magnitude, so the stage is tonemapped
        // output rather than raw radiance - something is mapping the whole scene to
        // white, which is what a blown-up exposure or a non-finite factor does. This
        // scans every constant the watched draws bind, for exactly that.
        private static float _pendingMaxAbs;
        private static int _pendingNonFinite;
        private static int _pendingScanned;

        private static readonly float[] _slotMaxAbs = new float[Slots];
        private static readonly int[] _slotNonFinite = new int[Slots];
        private static readonly int[] _slotScanned = new int[Slots];

        // The tonemap's luminance weights. If these reach the shader as zero the
        // luminance collapses to a fixed multiple of one channel, the divide by it
        // yields a constant well above 1, and every channel clamps to white - which is
        // why a white frame is uniform regardless of what the scene contains.
        private static float _pendingW0, _pendingW1, _pendingW2, _pendingW3;
        private static bool _pendingWSeen;

        private static readonly float[][] _slotW = CreateW();
        private static readonly bool[] _slotWSeen = new bool[Slots];

        private static float[][] CreateW()
        {
            float[][] a = new float[Slots][];
            for (int i = 0; i < Slots; i++)
            {
                a[i] = new float[4];
            }
            return a;
        }

        // The two images the tonemap reads: scene at texture index 8, bloom at index 10.
        // Its curve asymptotes to 1.0, so it turns white legitimately when handed an
        // out-of-range image - this samples both inputs so the blown one can be named.
        private static Texture _pendingSceneTex, _pendingBloomTex;

        private static MTLBuffer _inputBuf;
        // 25, not 9. Xcode shows one of these inputs as almost entirely black while a
        // nine-point sample of it read ~1.0 - every point had landed on sky. Too few
        // samples is the same mistake the flat-frame criterion made.
        private const int InputGridSide = 5;
        private const int InputPixels = InputGridSide * InputGridSide;
        private const int InputBytes = InputPixels * 4;
        private const int InputsPerFrame = 4;

        private const int MaxToneInputs = 8;
        private static readonly int[] _toneIdx = new int[MaxToneInputs];
        private static readonly Texture[] _toneTex = new Texture[MaxToneInputs];
        private static int _toneCount;

        /// <summary>
        /// The handle actually bound each frame, kept per slot.
        ///
        /// The report used to read the live _toneTex field for both the flat frame and its
        /// predecessor, which is the same current value printed twice - so "the same raw
        /// handle on both" was never a paired comparison, and the conclusion drawn from it
        /// (that the composite is not reaching a different texture) does not hold. Two
        /// 1600x896 RG11B10Float textures exist on a flat frame, one of which takes no
        /// draws at all, so which one was bound is exactly the question this has to answer.
        /// </summary>
        private static readonly IntPtr[][] _slotToneHandle = CreateToneHandles();
        private static readonly IntPtr[] _pendingToneHandle = new IntPtr[MaxToneInputs];

        private static IntPtr[][] CreateToneHandles()
        {
            IntPtr[][] slots = new IntPtr[Slots][];

            for (int i = 0; i < Slots; i++)
            {
                slots[i] = new IntPtr[MaxToneInputs];
            }

            return slots;
        }

        /// <summary>
        /// Every texture the tonemap program binds, latched on first sight. The slot
        /// numbers were guessed before and the guess was wrong, so nothing is assumed
        /// here - each one is reported with its size and format so the scene input can
        /// be picked out by inspection.
        /// </summary>
        // What actually lands in the argument buffer for the tonemap's scene slot, next
        // to the resource id of the texture we believe is bound. Every structural theory
        // is exhausted and the shader still reads 1.0, so the remaining question is
        // whether the id handed to the GPU is the one the binding intended.
        private static ulong _pendingArgId, _pendingTexId;
        private static readonly ulong[] _slotArgId = new ulong[Slots];
        private static readonly ulong[] _slotTexResId = new ulong[Slots];

        public static void NoteToneMapSlotId(int index, ulong argId, TextureBase storage)
        {
            if (!Enabled || index != 128 || storage is not Texture t)
            {
                return;
            }

            _pendingArgId = argId;
            _pendingTexId = t.GetHandle().GpuResourceID._impl;
        }

        public static string DescribeSlotId(int slot)
        {
            ulong a = _slotArgId[slot], t = _slotTexResId[slot];
            Texture tex = _toneCount > 0 ? _toneTex[0] : null;
            string mips = tex == null
                ? "?"
                : $"levels={tex.Info.Levels} firstLevel={tex.FirstLevel} depth={tex.Info.Depth} firstLayer={tex.FirstLayer}";
            return $"argBufId=0x{a:X} texResId=0x{t:X} {(a == t ? "match" : "MISMATCH")} {mips}";
        }

        /// <summary>
        /// Registers a texture sampled while the watched full-resolution target is bound.
        /// Identified by shape, not by shader label: labels are not stable across runs,
        /// and a label-keyed target set silently stayed empty in an earlier bisect, which
        /// made "skipping the writes changes nothing" mean "nothing was ever skipped".
        /// </summary>
        // Set while binding resources for a draw that samples the scene buffer into the
        // full-resolution target - the tonemap draw, identified by shape so it survives
        // DebugLabel changing between runs.
        private static bool _toneDrawPending;

        public static bool TakeToneDrawFlag()
        {
            bool v = _toneDrawPending;
            _toneDrawPending = false;
            return v;
        }

        /// <summary>
        /// Whether the tonemap's own draw should be dropped this frame. Paired: applied
        /// on even present indices only, with odd frames as the control.
        /// </summary>
        public static bool ShouldSkipToneDraw()
        {
            return Enabled && _skipToneDraw && (PresentProbe.Frame & 1) == 0;
        }

        private static bool _skipToneDraw;
        private static bool _skipPresentWrites;

        public static void SetSkipPresentWrites(bool value)
        {
            _skipPresentWrites = value;
        }

        /// <summary>
        /// Draws into the present-sized LDR buffer - the stage between the tonemap's
        /// float target and the texture handed to Present, which is the last one that has
        /// never been isolated. Paired on even present indices.
        /// </summary>
        public static bool ShouldSkipPresentWrite(Texture rt0)
        {
            if (!Enabled || !_skipPresentWrites || (PresentProbe.Frame & 1) != 0)
            {
                return false;
            }

            return rt0 != null &&
                rt0.Width >= 1900 &&
                (rt0.MtlFormat == MTLPixelFormat.RGBA8Unorm ||
                 rt0.MtlFormat == MTLPixelFormat.RGBA8UnormsRGB ||
                 rt0.MtlFormat == MTLPixelFormat.BGRA8Unorm);
        }

        public static void SetSkipToneDraw(bool value)
        {
            _skipToneDraw = value;
        }

        public static void NoteSampledUnderWatchedTarget(int index, TextureBase storage, Texture renderTarget)
        {
            if (!IsWatchedTarget(renderTarget) || storage is not Texture t)
            {
                return;
            }

            if (t.Width < 1280 || t.MtlFormat != MTLPixelFormat.RG11B10Float)
            {
                return;
            }

            _toneDrawPending = true;

            NoteToneMapInput(index, storage);
        }

        public static void NoteToneMapInput(int index, TextureBase storage)
        {
            if (storage is not Texture t || _toneCount >= MaxToneInputs)
            {
                return;
            }

            for (int i = 0; i < _toneCount; i++)
            {
                if (_toneIdx[i] == index)
                {
                    _toneTex[i] = t;
                    _pendingToneHandle[i] = t.GetHandle().NativePtr;

                    return;
                }
            }

            _toneIdx[_toneCount] = index;
            _toneTex[_toneCount] = t;
            _pendingToneHandle[_toneCount] = t.GetHandle().NativePtr;
            _toneCount++;
        }

        /// <summary>
        /// Blits both tonemap inputs into the readback buffer. Called from present, where
        /// no pass is open and a blit encoder is legal.
        /// </summary>
        public static void SampleInputs(CommandBufferScoped cbs, int slot)
        {
            if (_inputBuf.NativePtr == IntPtr.Zero || _toneCount == 0)
            {
                return;
            }

            MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();

            for (int k = 0; k < _toneCount && k < InputsPerFrame; k++)
            {
                Texture t = _toneTex[k];
                MTLTexture tex = t.GetHandle();

                if (tex.NativePtr == IntPtr.Zero)
                {
                    continue;
                }

                ulong baseOffset = (ulong)((slot * InputsPerFrame + k) * InputBytes);

                for (int i = 0; i < InputPixels; i++)
                {
                    blit.CopyFromTexture(
                        tex, 0, 0,
                        new MTLOrigin
                        {
                            // 5x5 to match InputPixels = 25. The old 3x3 indexing sent
                            // samples 12..24 past the texture's height - thirteen of the
                            // twenty-five reads were out of bounds garbage, identical for
                            // every source, which made three distinct textures report
                            // byte-identical spans and poisoned every per-slot comparison.
                            x = (ulong)(t.Width * (i % 5 + 1) / 6),
                            y = (ulong)(t.Height * (i / 5 + 1) / 6),
                            z = 0,
                        },
                        new MTLSize { width = 1, height = 1, depth = 1 },
                        _inputBuf, baseOffset + (ulong)(i * 4), 4, 4);
                }
            }

            // The spare slots carry the siblings, so one line compares all three.
            for (int k = 0; k < _siblingTex.Length; k++)
            {
                Texture t = _siblingTex[k];
                int slotIndex = 2 + k;

                if (t == null || slotIndex >= InputsPerFrame)
                {
                    continue;
                }

                MTLTexture tex = t.GetHandle();

                if (tex.NativePtr == IntPtr.Zero)
                {
                    continue;
                }

                _siblingLabel[k] = $"sib{k}:{t.Width}x{t.Height}:{t.MtlFormat}";

                ulong baseOffset = (ulong)((slot * InputsPerFrame + slotIndex) * InputBytes);

                for (int i = 0; i < InputPixels; i++)
                {
                    blit.CopyFromTexture(
                        tex, 0, 0,
                        new MTLOrigin
                        {
                            // 5x5 to match InputPixels = 25. The old 3x3 indexing sent
                            // samples 12..24 past the texture's height - thirteen of the
                            // twenty-five reads were out of bounds garbage, identical for
                            // every source, which made three distinct textures report
                            // byte-identical spans and poisoned every per-slot comparison.
                            x = (ulong)(t.Width * (i % 5 + 1) / 6),
                            y = (ulong)(t.Height * (i / 5 + 1) / 6),
                            z = 0,
                        },
                        new MTLSize { width = 1, height = 1, depth = 1 },
                        _inputBuf, baseOffset + (ulong)(i * 4), 4, 4);
                }
            }
        }

        private static readonly string[] _siblingLabel = new string[2];

        // Non-render writes to the tonemap's scene input. No render pass ever names it
        // as an attachment, yet its contents change every frame - so whatever fills it
        // is a copy or an upload, and its ordering against the tonemap's read is what
        // decides whether the frame comes out white.
        private static readonly System.Collections.Generic.List<string> _pendingWrites = [];
        private static readonly string[] _slotWrites = new string[Slots];

        public static void NoteNonRenderWrite(Texture target, string kind)
        {
            // Every non-render write funnels through here (copy, copyLayer, blitScaled,
            // upload). Register each as a writer of the target's root for the correlator's
            // census - the present scene texture's census came back empty, so its writes
            // arrive by a route the attachment hook does not see.
            if (UploadCorrelator.Enabled && target != null)
            {
                UploadCorrelator.NoteAttachmentDraw(target.CanonicalPtr, kind);
            }

            if (!Enabled || target == null || _toneCount == 0)
            {
                return;
            }

            for (int i = 0; i < _toneCount; i++)
            {
                if (RootOf(_toneTex[i]) != IntPtr.Zero && RootOf(_toneTex[i]) == RootOf(target))
                {
                    lock (_pendingWrites)
                    {
                        if (_pendingWrites.Count < 32)
                        {
                            _pendingWrites.Add($"{kind}@slot{_toneIdx[i]}");
                        }
                    }

                    return;
                }
            }
        }

        // Which programs draw into the tonemap's scene input. Ordering is excluded and
        // the shader reads 1.0, so the writes themselves must be producing it - these are
        // the shaders to examine next.
        private static readonly System.Collections.Generic.HashSet<string> _writers = [];

        public static void NoteWriterProgram(Texture target, string label)
        {
            if (!Enabled || label == null || _toneCount == 0)
            {
                return;
            }

            IntPtr root = RootOf(target);

            for (int i = 0; i < _toneCount; i++)
            {
                if (root != IntPtr.Zero && root == RootOf(_toneTex[i]))
                {
                    lock (_writers)
                    {
                        _writers.Add(label);
                    }

                    return;
                }
            }
        }

        // Encode order inside one frame: where the writes to the tonemap's input land in
        // the draw sequence, and where the tonemap's own draw lands. If the tonemap is
        // encoded before the writes, no GPU-side ordering fix can help - the command
        // stream itself is built in the wrong order.
        private static ulong _wFirst, _wLast, _toneAt;
        private static bool _wSeen, _toneSeen;

        private static readonly string[] _slotOrder = new string[Slots];

        // Runtime bisect. Shader DebugLabels are not stable across runs, so a label list
        // captured in one run cannot be painted in the next - and the compile-time patch
        // is also defeated by a warm shader cache. Selecting by position among the draws
        // that write the tonemap's input avoids both: it needs no labels, no recompile,
        // and can be switched while the game runs.
        private static int _skipLo = -1, _skipHi = -1;
        private static int _writeIndexThisFrame;

        public static void SetSkipRange(int lo, int hi)
        {
            _skipLo = lo;
            _skipHi = hi;
        }

        /// <summary>
        /// Whether this draw writes the tonemap's input and falls in the skipped range.
        /// </summary>
        public static bool ShouldSkipDraw(Texture rt0)
        {
            if (!Enabled || _toneCount == 0 || _skipLo < 0)
            {
                return false;
            }

            IntPtr root = RootOf(rt0);

            if (root == IntPtr.Zero)
            {
                return false;
            }

            for (int i = 0; i < _toneCount; i++)
            {
                if (root == RootOf(_toneTex[i]))
                {
                    // Even frames only: the odd frames are the paired control.
                    if ((PresentProbe.Frame & 1) != 0)
                    {
                        return false;
                    }

                    int index = _writeIndexThisFrame;
                    return index >= _skipLo && index < _skipHi;
                }
            }

            return false;
        }

        public static void NoteDraw(Texture rt0, string label, ulong drawIndex)
        {
            if (!Enabled || _toneCount == 0)
            {
                return;
            }

            if (label == "3ebc3a8f6b77cc8f")
            {
                if (!_toneSeen)
                {
                    _toneAt = drawIndex;
                    _toneSeen = true;
                }

                return;
            }

            IntPtr root = RootOf(rt0);

            if (root == IntPtr.Zero)
            {
                return;
            }

            for (int i = 0; i < _toneCount; i++)
            {
                if (root == RootOf(_toneTex[i]))
                {
                    if (!_wSeen)
                    {
                        _wFirst = drawIndex;
                        _wSeen = true;
                    }

                    _wLast = drawIndex;
                    _writeIndexThisFrame++;
                    return;
                }
            }
        }

        public static string DescribeOrder(int slot)
        {
            return _slotOrder[slot] ?? "n/a";
        }

        public static string DescribeWriterPrograms()
        {
            lock (_writers)
            {
                return _writers.Count == 0 ? "none" : string.Join(",", _writers);
            }
        }

        // Was the texture actually handed to Present written by anything this frame?
        // If white frames are exactly the frames where it was not, the symptom is a
        // missing composite rather than a wrong colour.
        private static readonly bool[] _slotPresentWritten = new bool[Slots];
        private static readonly int[] _slotPresentPasses = new int[Slots];
        private static readonly ulong[] _slotPresentDraws = new ulong[Slots];

        // The present source is never a render pass attachment, on white and good frames
        // alike, so whatever fills it is a copy. These are the roots seen at Present -
        // they alternate between a couple of textures - so a copy landing on one of them
        // can be counted during the frame.
        private static readonly IntPtr[] _presentRoots = new IntPtr[4];
        private static int _presentRootCount;
        private static int _pendingPresentCopies;
        private static readonly int[] _slotPresentCopies = new int[Slots];

        private static void RememberPresentRoot(IntPtr root)
        {
            if (root == IntPtr.Zero)
            {
                return;
            }

            for (int i = 0; i < _presentRootCount; i++)
            {
                if (_presentRoots[i] == root)
                {
                    return;
                }
            }

            if (_presentRootCount < _presentRoots.Length)
            {
                _presentRoots[_presentRootCount++] = root;
            }
        }

        public static void NoteCopyInto(Texture destination)
        {
            if (!Enabled)
            {
                return;
            }

            IntPtr root = RootOf(destination);

            for (int i = 0; i < _presentRootCount; i++)
            {
                if (_presentRoots[i] == root)
                {
                    _pendingPresentCopies++;
                    return;
                }
            }
        }

        public static void NotePresentSource(Texture src, int slot)
        {
            RememberPresentRoot(RootOf(src));
            _slotPresentCopies[slot] = _pendingPresentCopies;
            _pendingPresentCopies = 0;

            IntPtr root = RootOf(src);
            bool found = false;
            int passes = 0;
            ulong draws = 0;

            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pending[i].Target == root)
                {
                    found = true;
                    passes = _pending[i].Passes;
                    draws = _pending[i].Draws;
                    break;
                }
            }

            _slotPresentWritten[slot] = found;
            _slotPresentPasses[slot] = passes;
            _slotPresentDraws[slot] = draws;
        }

        // Self-check: a good frame's present source must have a writer. If this fires,
        // attribution is broken again and no ownership result can be trusted.
        private static int _attributionFailures;

        public static void CheckAttribution(int slot, bool frameWasWhite)
        {
            if (frameWasWhite || _slotPresentWritten[slot] || _slotPresentCopies[slot] > 0)
            {
                return;
            }

            if (++_attributionFailures % 120 == 1)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"hdrprobe ATTRIBUTION-BROKEN: a good frame's present source has no writer " +
                    $"({_attributionFailures} so far). Ownership results are not trustworthy.");
            }
        }

        public static string DescribePresentWritten(int slot)
        {
            return _slotPresentWritten[slot]
                ? $"passes={_slotPresentPasses[slot]} draws={_slotPresentDraws[slot]} copies={_slotPresentCopies[slot]}"
                : $"no-pass copies={_slotPresentCopies[slot]}";
        }

        public static string DescribeWrites(int slot)
        {
            return _slotWrites[slot] ?? "none";
        }

        public static unsafe string DescribeInputs(int slot)
        {
            if (_inputBuf.NativePtr == IntPtr.Zero)
            {
                return "n/a";
            }

            uint* words = (uint*)_inputBuf.Contents;
            StringBuilder sb = new();

            for (int k = 0; k < InputsPerFrame; k++)
            {
                uint* px = words + (slot * InputsPerFrame + k) * InputPixels;
                uint min = uint.MaxValue, max = 0;

                for (int i = 0; i < InputPixels; i++)
                {
                    if (px[i] < min) min = px[i];
                    if (px[i] > max) max = px[i];
                }

                Texture t = k < _toneCount ? _toneTex[k] : null;

                if (t == null && k >= 2 && _siblingLabel[k - 2] != null)
                {
                    sb.Append($" [{_siblingLabel[k - 2]}]0x{min:X8}..0x{max:X8}");
                    continue;
                }


                // The raw handle as well as the canonical one. A white frame carries two
                // 1600x896 RG11B10Float textures - one taking 29 draws across 25 passes,
                // and one with a single pass and no draws at all - and if they alias the
                // same guest memory their canonical pointers are equal, so a report keyed
                // on canonical identity cannot tell which of the two was sampled. That is
                // the merge that has produced four wrong conclusions in these notes, and
                // here it would hide exactly the case worth checking: the composite reading
                // the one nothing ever drew into.
                sb.Append(t == null
                    ? $" [{k}:none]"
                    : $" [slot{_toneIdx[k]}:{t.Width}x{t.Height}:{t.MtlFormat}:" +
                      $"bound0x{_slotToneHandle[slot][k]:X}:root0x{RootOf(t):X}]");
                sb.Append($"0x{min:X8}..0x{max:X8}");
            }

            return sb.ToString();
        }

        public static unsafe void NoteToneMapWeights(IntPtr contents, int offset)
        {
            if (contents == IntPtr.Zero)
            {
                return;
            }

            float* d = (float*)((byte*)contents + offset);
            _pendingW0 = d[0]; _pendingW1 = d[1]; _pendingW2 = d[2]; _pendingW3 = d[3];
            _pendingWSeen = true;
        }

        public static string DescribeWeights(int slot)
        {
            if (!_slotWSeen[slot])
            {
                return "not-bound";
            }

            float[] w = _slotW[slot];
            return $"cb1[0]=({w[0]:G6},{w[1]:G6},{w[2]:G6},{w[3]:G6})";
        }

        public static unsafe void NoteUniform(IntPtr contents, int offset, int size)
        {
            if (contents == IntPtr.Zero || size <= 0)
            {
                return;
            }

            int floats = Math.Min(size, 1024) / sizeof(float);
            float* data = (float*)((byte*)contents + offset);

            for (int i = 0; i < floats; i++)
            {
                float v = data[i];

                if (float.IsNaN(v) || float.IsInfinity(v))
                {
                    _pendingNonFinite++;
                    continue;
                }

                float a = MathF.Abs(v);

                if (a > _pendingMaxAbs)
                {
                    _pendingMaxAbs = a;
                }
            }

            _pendingScanned++;
        }

        public static string DescribeUniforms(int slot)
        {
            return $"bufs={_slotScanned[slot]},maxAbs={_slotMaxAbs[slot]:G6},nonFinite={_slotNonFinite[slot]}";
        }

        /// <summary>
        /// The distinct textures the watched stage sampled, in first-seen order.
        /// </summary>
        public static string DescribeSampled(int slot)
        {
            int count = _slotSampledCount[slot];

            if (count == 0)
            {
                return "none";
            }

            IntPtr[] sampled = _slotSampled[slot];
            StringBuilder sb = new();

            for (int i = 0; i < count; i++)
            {
                if (sb.Length > 0)
                {
                    sb.Append(',');
                }

                sb.Append($"0x{sampled[i]:X}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// The full resolution HDR target's passes in encode order, as draws(endReason).
        /// </summary>
        public static string DescribeWatched(int slot)
        {
            int count = _slotWatchedCount[slot];

            if (count == 0)
            {
                return "none";
            }

            PassDetail[] details = _slotWatched[slot];
            StringBuilder sb = new();

            for (int i = 0; i < count; i++)
            {
                ref PassDetail d = ref details[i];

                if (sb.Length > 0)
                {
                    sb.Append(" -> ");
                }

                sb.Append($"{d.Draws}d({d.Reason}{(d.Cleared ? ",CLEAR" : "")})");
            }

            return sb.ToString();
        }

        public static string Describe(int slot)
        {
            int count = _slotCount[slot];

            if (count == 0)
            {
                return "none";
            }

            Entry[] entries = _slots[slot];
            StringBuilder sb = new();

            for (int i = 0; i < count; i++)
            {
                ref Entry e = ref entries[i];

                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append($"[0x{e.Target:X}:{e.Width}x{e.Height}:{e.Format}:p={e.Passes},d={e.Draws},clr={e.Cleared}]");
            }

            return sb.ToString();
        }
    }
}
