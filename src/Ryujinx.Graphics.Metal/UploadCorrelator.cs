using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Tests one falsifiable claim: flat (white) frames are the frames in which a
    /// guest-to-host upload (SetData) or a texture-to-texture copy landed on a
    /// scene-sized texture mid-frame - the shared layer resurrecting stale guest
    /// memory into a surface the frame had already rendered.
    ///
    /// Per frame it accumulates, in memory only: how many SetData/CopyTo writes hit
    /// large textures, whether the written texture had already been a colour
    /// attachment this same frame (the resurrection signature), and which shapes.
    /// At present it samples 25 pixels of the present source into a slot of a ring
    /// buffer - same grid and threshold FlashGuard's calibrated detector uses - and
    /// classifies the slot several presents later when its command buffer has long
    /// completed. Nothing is waited on, and nothing is logged per frame; a per-frame
    /// present log is known to close the race window, a per-frame GPU sync is known
    /// (from the CPU-sampling FlashGuard era, 35% flat with it active) not to.
    ///
    /// The join is a 2x2 table reported once per interval: P(big write | flat) vs
    /// P(big write | normal), plus the same split per shape. Hypothesis dead if the
    /// columns match; culprit named by shape if they do not.
    ///
    /// RYUJINX_METAL_UPLOAD_CORR=1 enables. No MSL is touched, so no CodeGenVersion
    /// bump is due.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class UploadCorrelator
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_UPLOAD_CORR") == "1";

        private const int GridSide = 5;
        private const int Pixels = GridSide * GridSide;
        private const int BytesPerPixel = 4;
        private const int Slots = 8;

        // FlashGuard's calibrated flat-frame criterion (300 captured frames of real
        // gameplay): a flat frame has >= 8 of 25 samples saturated, an ordinary one
        // at most 4. Same numbers so the labels mean the same thing they meant there.
        private const float SaturatedLuma = 235f;
        private const int SaturatedNeeded = 6;

        // Below this a texture is a shadow tile, LUT or UI atlas, not a stage of the
        // scene chain. The scene runs 1600x896 at full dynamic resolution and stays
        // far above this even scaled down; the 260x260 background-readback texture
        // stays below it.
        private const int MinPixels = 512 * 512;

        private struct Slot
        {
            public bool Valid;
            public FenceHolder Fence;
            public long Frame;
            public int Uploads;
            public int BigUploads;
            public int BigUploadsOntoRt;
            public int BigCopies;
            public int BigCopiesOntoRt;
            public ulong[] Shapes;
            public int ShapeCount;
            public Binding Binding;
            public Binding BindingLast;
            public string Signature;
            public IntPtr PresentRoot;
            public long PresentAge;
            public string Writers;
            public bool InputSampled;
            public bool AfterSampled;
            public bool AfterOutSampled;
            public IntPtr AfterOutPtr;
            public bool PreProbed;
            public string InputWriters;
            public string InputDesc;
            public string StageDraw;
            public string StageInputWriters;
            public string StageBlend;
            public int StageDrawCount;
            public int RawSplits;
            public int BarrierSkipped;
            public string StageInputLastWriters;
            public string StageInputId;
            public float[] Const;
            public bool ConstSeen;
            public IntPtr PresentSrcPtr;
            public IntPtr DrawnSrgbPtrThisFrame;
            public bool SamplerSampled;
            public MTLPixelFormat InputFmt;
            public bool InputWrittenAfter;
            public int Residency;
            public int CompositeDraws;
            public int VertexDistinct;
            public int VertexStride;
            public (int Count, int Inst, int First, int Indexed) Draw;
            public string Indices;
            public string Attrib;
            public string Raster;
            public int WriterCb;
            public int PresentCb;
            public long PresentRent;
            public int CompCb;
            public long CompRent;
            public long WriterRent;
            public int BlitCb;
            public long BlitRent;
            public bool PsoFresh;
            public string ImgWriters;
            public long InSerial;
            public long WrSerial;
            public long InGen;
            public long WrGen;
            public float[] RowW;
            public bool[] RowSeen;
            public int OutOfOrder;
            public IntPtr ArgPtr;
            public bool PresentMatchesLastRt;
            public bool PresentMatchKnown;
            public bool Rgba8Known;
            public bool Rgba8Match;
            public bool BlitPrevKnown;
            public string Triple;
            public bool SrcWasRecentDst;
            public bool RtSampled;
            public int ReplaceViews;
            public int NewTextures;
            public string ModifiedBy;
            public string RtWriters;
            public string SceneWriters;
            public bool BlitPrevMatch;
            public ulong[] ArgExpected;
            public int ArgCount;
            public string LateWriter;
            public float[] Cb;
            public bool[] CbSeen;
        }

        private static MTLBuffer _buf;
        private static readonly Slot[] _slots = new Slot[Slots];

        // Accumulators for the frame currently being encoded. Everything here runs on
        // the render thread (SetData uses the main pipeline, attachments bind at
        // encoder creation, present is the boundary), so plain fields suffice.
        private static readonly HashSet<IntPtr> _attachedThisFrame = new();
        // The scene texture's binding as the shader receives it, for the last draw of
        // the frame that sampled it. Recorded per frame, classified with the frame.
        private struct Binding
        {
            public ulong GpuAddress;
            public IntPtr NativePtr;
            public IntPtr CanonicalPtr;
            public string Program;
            public int Count;
        }

        private static Binding _frameBinding;
        private static Binding _frameBindingLast;

        // The composite's input, kept as the texture rather than an address: the white is
        // now known to be exactly uniform, so the composite is faithfully computing 0/0
        // and the question is what makes THIS flat on a fifth of frames. Identity was
        // already shown identical across outcomes, so only contents can differ.
        private static Texture _frameSceneTex;

        // The capture showed the present draw sampling a texture object DIFFERENT from the
        // one the frame's last full-resolution pass rendered into (0x9060fa300 white vs
        // 0x904c49900 correct). This records the last 1920x1080 colour attachment root
        // each frame and compares it, at present, with the object present was handed.
        private static IntPtr _frameLastFullResRt;
        private static Texture _frameLastFullResTex;
        private static IntPtr _frameLastRgba8Rt;
        private static long _rgbaMatchFlat, _rgbaMatchNormal, _rgbaMismFlat, _rgbaMismNormal, _rgbaNoneFlat, _rgbaNoneNormal;
        private static long _rtPicSrcWhiteFlat, _rtPicSrcWhiteNormal, _bothPicFlat, _bothPicNormal, _bothWhiteFlat, _bothWhiteNormal, _rtWhiteSrcPicFlat, _rtWhiteSrcPicNormal;
        private static long _presentMatchFlat, _presentMatchNormal, _presentMismatchFlat, _presentMismatchNormal;

        private static long _presentSrcSerial, _prevPresentSrcSerial;
        private static IntPtr _presentSrcRoot, _presentDstRoot;
        private static int _presentSrcW, _presentSrcH;
        private static readonly Dictionary<string, (long Flat, long Normal)> _tripleStats = new();
        private static readonly Dictionary<string, (long Flat, long Normal)> _rtWriterStats = new();

        private static readonly Queue<IntPtr> _recentDstRoots = new();
        private static long _srcWasRecentDstFlat, _srcWasRecentDstNormal, _srcNotDstFlat, _srcNotDstNormal;
        private static bool _frameSrcWasRecentDst;

        public static void NotePresentTriple(IntPtr srcRoot, IntPtr dstRoot, int w, int h, long srcSerial)
        {
            if (!Enabled) { return; }

            // Is what present READS one of the drawables present recently WROTE? The
            // writer census on src reports only the present program itself as a writer,
            // which is impossible unless src is a former dst.
            _frameSrcWasRecentDst = _recentDstRoots.Contains(srcRoot);
            _recentDstRoots.Enqueue(dstRoot);
            while (_recentDstRoots.Count > 8) { _recentDstRoots.Dequeue(); }
            _prevPresentSrcSerial = _presentSrcSerial;
            _presentSrcSerial = srcSerial;
            _presentSrcRoot = srcRoot;
            _presentDstRoot = dstRoot;
            _presentSrcW = w; _presentSrcH = h;
        }

        private static IntPtr _frameSceneSourceRoot;
        private static readonly Dictionary<string, (long Flat, long Normal)> _sceneWriterStats = new();

        public static void NoteSceneSourceForCensus(Texture t)
        {
            if (Enabled && t != null) { _frameSceneSourceRoot = t.CanonicalPtr; }
        }

        private static IntPtr _frameGameRtRoot;
        private static IntPtr _prevGameRtRoot;
        private static bool _gameRtPassOpen, _gameRtPassClear;
        private static long _gameRtPassDrawStart, _drawsSeen;
        private static int _gameRtPassLogs, _gameRtEndLogs;
        private static string _prevFrameGameRtWriters = "(unknown)";
        private static Texture _frameGameRtTex;
        private static Texture _frameGameRtViewTex;

        private static int _gameRtLogs;

        private static int _viewLogs;

        public static void NoteGameFinalTargetView(Texture t)
        {
            if (!Enabled || t == null) { return; }
            _frameGameRtViewTex = t;
            if (++_viewLogs % 300 == 1)
            {
                // The dangling-view test: does V's Metal parent equal the CURRENT A's storage,
                // and does that storage still hold a live texture?
                bool sameParent = _frameGameRtTex != null && t.CanonicalPtr == _frameGameRtTex.CanonicalPtr;
                bool parentAliveInMetal = _frameGameRtTex != null && _frameGameRtTex.GetHandle().NativePtr != IntPtr.Zero;
                bool viewAlive = t.GetHandle().NativePtr != IntPtr.Zero;
                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"DANGLING-VIEW TEST: V.canon=0x{t.CanonicalPtr:X} A.canon=0x{(_frameGameRtTex?.CanonicalPtr ?? IntPtr.Zero):X} sameParent={sameParent} viewAlive={viewAlive} parentAlive={parentAliveInMetal} V.serial={t.Serial} A.serial={_frameGameRtTex?.Serial}");
            }
        }

        private static int _frameReplaceViews, _frameNewTextures;
        private static double _rvFlat, _rvNormal, _ntFlat, _ntNormal; private static long _topoFlatN, _topoNormalN;

        private static string _frameModifiedBy = "";
        private static readonly Dictionary<string, (long Flat, long Normal)> _modByStats = new();

        public static void NoteModifiedBy(string who)
        {
            if (Enabled && !_frameModifiedBy.Contains(who)) { _frameModifiedBy += who + ","; }
        }

        public static void NoteTopologyEvent(int kind)
        {
            if (!Enabled) { return; }
            if (kind == 1) { _frameReplaceViews++; } else { _frameNewTextures++; }
        }

        public static void NoteGameFinalTarget(Texture t)
        {
            if (Enabled && t != null)
            {
                _prevGameRtRoot = _frameGameRtRoot;
                _frameGameRtRoot = t.CanonicalPtr;
                _frameGameRtTex = t;

                if (++_gameRtLogs % 600 == 1)
                {
                    // Is this object's root among this frame's attachment roots at all?
                    bool everAttached = _lastAttachmentFrame.TryGetValue(t.CanonicalPtr, out long lf);
                    Logger.Warning?.PrintMsg(LogClass.Gpu,
                        $"game RT: {t.Width}x{t.Height} {t.Info.Format} canon=0x{t.CanonicalPtr:X} native=0x{t.GetHandle().NativePtr:X} " +
                        $"isView={(t.CanonicalPtr != t.GetHandle().NativePtr)} everAttached={everAttached} lastAttachFrame={(everAttached ? lf : -1)} nowFrame={_frame} " +
                        $"attachedRootsThisFrame={_attachedThisFrame.Count}");
                }
            }
        }

        private static readonly List<string> _frameFullResAttach = new();
        private static IntPtr _frameDrawnSrgbRoot;
        private static Texture _frameDrawnSrgbTex;
        private static readonly string _watchFile = Environment.GetEnvironmentVariable("RYUJINX_METAL_WATCH_PTR_FILE");
        public static bool _inPresent;
        private static int _rootCompareLogs;

        public static void NoteFullResAttachment(Texture t)
        {
            if (Enabled && t != null && t.Width >= 1900 && t.Height >= 1000 && !t.Info.Format.IsDepthOrStencil)
            {
                if (_frameFullResAttach.Count < 16)
                {
                    string entry = $"{t.Info.Format}@0x{t.CanonicalPtr:X}/n0x{t.GetHandle().NativePtr:X}{(t.CanonicalPtr != t.GetHandle().NativePtr ? "(view)" : "")}";
                    if (!_frameFullResAttach.Contains(entry)) { _frameFullResAttach.Add(entry); }
                }
                _frameLastFullResRt = t.CanonicalPtr;
                _frameLastFullResTex = t;

                // The present source is RGBA8, not RG11B10 - track the last RGBA8 one too.
                if (t.Info.Format == Format.R8G8B8A8Unorm || t.Info.Format == Format.B8G8R8A8Unorm ||
                    t.Info.Format == Format.R8G8B8A8Srgb || t.Info.Format == Format.B8G8R8A8Srgb)
                {
                    _frameLastRgba8Rt = t.CanonicalPtr;
                }

                // The drawable is also RGBA8 sRGB and is bound during present; only the
                // game's own sRGB target (bound outside present) is wanted here.
                if (t.Info.Format == Format.R8G8B8A8Srgb && !_inPresent)
                {
                    _frameDrawnSrgbRoot = t.CanonicalPtr;
                    _frameDrawnSrgbTex = t;

                    // Hand the storage pointer to the out-of-process Metal probe (mtlspy) via
                    // a tiny file: it watches every command that touches this MTLTexture
                    // between the composite's pass and the next present, outside Ryujinx's
                    // own accounting, which records no writer at all in that window.
                    if (_watchFile != null)
                    {
                        try
                        {
                            System.IO.File.WriteAllText(_watchFile, $"{t.CanonicalPtr.ToInt64():X} {_frame}\n");
                        }
                        catch (System.IO.IOException) { }
                    }
                }
            }
        }

        // Does anything write the composite's input AFTER the composite has read it?
        // Every "the input is not flat" result here sampled that texture at present, which
        // is after every pass in the frame - so a later writer would mean the probe never
        // saw what the shader actually read. Ordering is tracked with a counter rather than
        // by moving the sample, because sampling at the draw needs the render encoder to
        // end and restart, which is the read-after-write split already known to move the
        // flash rate: the probe would perturb what it measures.
        private static IntPtr _compositeInputRoot;
        private static long _compositeSeq = -1;
        private static long _frameSeq;
        private static bool _frameInputWrittenAfter;
        private static string _frameLateWriter;
        private static long _flatLateWrites, _normalLateWrites;

        // How many resources the composite's draw actually declares to the encoder. Read
        // from the code the pairing looks exact, but the argument buffer's allocation
        // strategy moves the flash rate by eleven points for a reason that is neither
        // premature deletion nor a missing declaration by inspection - so count what is
        // issued rather than what the source says should be.
        private static int _frameResidency = -1;

        // How many times the composite draws in one frame. Nothing established this was
        // once, and the per-frame program signatures show programs repeating - so every
        // input measurement taken here may describe only the last invocation.
        private static int _frameCompositeDraws;
        private static double _flatDrawSum, _normalDrawSum;
        private static long _flatDrawN, _normalDrawN;

        // The argument buffer's own bytes, remembered at the composite draw and re-read at
        // present. Everything so far compared the id Ryujinx computed; this compares the id
        // still sitting in memory when the frame ends against the one written. They differ
        // only if something overwrote the table between the draw being encoded and the GPU
        // reading it - which would point the taps at another texture and produce exactly
        // the uniform fill that is measured, with the real input untouched.
        private const int MaxArgIds = 8;
        private static IntPtr _frameArgPtr;
        private static readonly ulong[] _frameArgExpected = new ulong[MaxArgIds];
        private static int _frameArgCount;
        private static long _flatArgMismatch, _normalArgMismatch, _flatArgChecked, _normalArgChecked;

        public static unsafe void NoteArgBuffer(IntPtr contents, int offset, ReadOnlySpan<ulong> ids)
        {
            if (!Enabled || contents == IntPtr.Zero)
            {
                return;
            }

            _frameArgPtr = (IntPtr)((byte*)contents + offset);
            _frameArgCount = Math.Min(ids.Length, MaxArgIds);

            for (int i = 0; i < _frameArgCount; i++)
            {
                _frameArgExpected[i] = ids[i];
            }
        }
        private static double _flatResidencySum, _normalResidencySum;
        private static long _flatResidencyN, _normalResidencyN;

        /// <summary>
        /// The composite's own colour attachment. The divide-by-zero reading is refuted, so
        /// there is no longer any reason to believe the composite is what makes the frame
        /// white - and if its output is not uniform while the presented surface is, the
        /// white arrives after it, somewhere nobody has looked.
        /// </summary>
        // Which programs drew into the composite's INPUT during this frame, snapshotted at
        // the composite's bind (and the census counter for that storage reset there, so the
        // list is per frame). Full-surface dumps showed the input already white when the
        // composite runs; the white therefore comes from one of these writers, or from
        // upstream of it. Tallied per outcome at classification.
        private static string _frameInputWriters = "-", _frameInputDesc = "-";
        private static readonly Dictionary<string, (long Flat, long Normal)> _inputWriterStats = new();
        private static readonly Dictionary<string, (long Flat, long Normal)> _inputDescStats = new();

        public static void NoteCompositeInputWriters(Texture input)
        {
            if (!Enabled || input == null)
            {
                return;
            }

            IntPtr root = input.CanonicalPtr;
            int count = _pendingWriterCount.TryGetValue(root, out int c) ? c : 0;
            _frameInputWriters = count > 0 ? string.Join(",", _pendingWriters[root], 0, Math.Min(count, MaxWriters)) : "-";
            _pendingWriterCount[root] = 0;
            _frameInputDesc = $"{input.Width}x{input.Height} {input.MtlFormat} 0x{root.ToInt64():X}";
            _stageTargetRoot = root;
        }
        private static IntPtr _stageTargetRoot;

        public static void NoteCompositeOutput(Texture target)
        {
            if (Enabled && target != null)
            {
                _frameSceneTex = target;

                // Also arm the late-write tracker on THIS texture. The conclusion that the
                // blit's source holds a picture on white frames rests on a sample taken at
                // present, after every pass in the frame - and the check for a later writer
                // was only ever wired to the texel-fetch shader's input, never to this one.
                // If something writes this surface between the blit reading it and the frame
                // ending, the sample shows content the blit never saw, and "the source holds
                // a picture" is not a statement about the frame that went white.
                _compositeInputRoot = target.CanonicalPtr;
                _compositeSeq = _frameSeq;
            }
        }

        public static void NoteResidency(int count)
        {
            if (Enabled)
            {
                _frameResidency = count;
            }
        }
        private static readonly Dictionary<string, (long Flat, long Normal)> _lateWriterStats = new();

        private static void NoteWriteOrdering(IntPtr root, string who)
        {
            _frameSeq++;

            if (_compositeSeq >= 0 && root != IntPtr.Zero && root == _compositeInputRoot)
            {
                _frameInputWrittenAfter = true;
                _frameLateWriter = who;
            }
        }

        // The composite's constant buffers, first four floats of each slot. The weight sum
        // does not come from the taps alone: fp_c3 feeds the fetch coordinates and fp_c1
        // carries the Newton constant, so a zeroed or stale buffer produces the same 0/0
        // over a perfectly good texture. Never looked at before, because every binding
        // check compared identities, which a constant buffer keeps while its contents move.
        private const int MaxCbSlots = 32;
        private static readonly float[] _frameCb = new float[MaxCbSlots * 4];
        private static readonly bool[] _frameCbSeen = new bool[MaxCbSlots];
        private static readonly double[] _cbFlatSum = new double[MaxCbSlots * 4];
        private static readonly double[] _cbNormalSum = new double[MaxCbSlots * 4];
        private static readonly float[] _cbFlatMin = new float[MaxCbSlots * 4];
        private static readonly float[] _cbFlatMax = new float[MaxCbSlots * 4];
        private static readonly float[] _cbNormalMin = new float[MaxCbSlots * 4];
        private static readonly float[] _cbNormalMax = new float[MaxCbSlots * 4];
        private static bool _cbExtremaInit;
        private static readonly long[] _cbFlatN = new long[MaxCbSlots];
        private static readonly long[] _cbNormalN = new long[MaxCbSlots];

        /// <summary>
        /// Four vertices a stride apart, compared with each other. A degenerate UV means the
        /// attribute is constant *across* the primitive's vertices, which reading vertex zero
        /// alone cannot see - and vertex zero came out bit-identical on both outcomes, which
        /// settles nothing. Reported as the number of distinct vertices of four.
        /// </summary>
        public static unsafe void NoteVertexSpread(IntPtr contents, int offset, int stride)
        {
            if (!Enabled || contents == IntPtr.Zero || stride <= 0)
            {
                return;
            }

            int distinct = 0;

            for (int i = 0; i < 4; i++)
            {
                float* v = (float*)((byte*)contents + offset + i * stride);
                _frameVerts[i * 3 + 0] = v[0];
                _frameVerts[i * 3 + 1] = v[1];
                _frameVerts[i * 3 + 2] = v[2];

                ulong a = *(ulong*)((byte*)contents + offset + i * stride);
                bool seen = false;

                for (int j = 0; j < i; j++)
                {
                    if (*(ulong*)((byte*)contents + offset + j * stride) == a)
                    {
                        seen = true;
                        break;
                    }
                }

                if (!seen)
                {
                    distinct++;
                }
            }

            _frameVertexDistinct = distinct;
        }

        /// <summary>
        /// The stride the fetch uses. Four distinct vertices sit in the buffer on a white
        /// frame and the shader still sees a constant attribute, so the collapse is between
        /// them - and a stride of zero makes every vertex read element zero, which is
        /// exactly that.
        /// </summary>
        public static void NoteVertexStride(int stride)
        {
            if (Enabled)
            {
                _frameVertexStride = stride;
            }
        }

        public static void NoteDrawParams(int count, int instances, int first, int indexed)
        {
            if (Enabled)
            {
                _frameDraw = (count, instances, first, indexed);
            }
        }

        private static (int Count, int Inst, int First, int Indexed) _frameDraw = (-1, -1, -1, -1);
        private static readonly Dictionary<string, (long Flat, long Normal)> _drawParamStats = new();
        public static unsafe void NoteIndices(IntPtr contents, int indexType = 2)
        {
            if (!Enabled || contents == IntPtr.Zero)
            {
                return;
            }

            // MTLIndexType: 0 = UInt16, 1 = UInt32. Anything else is this probe's own
            // default for the 32-bit pattern path.
            if (indexType == 0)
            {
                ushort* p16 = (ushort*)contents;
                _frameIndices = $"{p16[0]},{p16[1]},{p16[2]},{p16[3]},{p16[4]},{p16[5]}";
            }
            else
            {
                uint* p32 = (uint*)contents;
                _frameIndices = $"{p32[0]},{p32[1]},{p32[2]},{p32[3]},{p32[4]},{p32[5]}";
            }
        }

        public static void NoteVertexAttrib(int format, int offset, int bufferIndex)
        {
            if (Enabled)
            {
                _frameAttrib = $"fmt={format} off={offset} buf={bufferIndex}";
            }
        }

        public static void NoteOutOfOrderCommit()
        {
            if (Enabled)
            {
                _frameOutOfOrder++;
            }
        }

        private static int _frameOutOfOrder;
        private static double _flatOooSum, _normalOooSum;
        private static long _flatOooN, _normalOooN;
        /// <summary>
        /// Fixed-function state at the blit's draw. Every measurement so far compared data
        /// - buffers, textures, constants, indices - and none compared the raster state,
        /// which is the one axis that changes frame to frame and can null a draw outright.
        /// This backend has prior form: CullBoth once ignored the enable flag and produced
        /// a 0x0 scissor. A blit rasterised away leaves the pass's load contents on screen,
        /// and everything else measures correct because everything else IS correct.
        /// </summary>
        public static void NoteBlitRaster(string state)
        {
            if (Enabled)
            {
                _frameRaster = state;
            }
        }

        /// <summary>
        /// Whether the PSO bound at the blit was first seen this frame. The capped
        /// per-pointer stats showed white frames' pointers arriving after the cap filled -
        /// every visible signature had flat ~0 - which reads as white frames binding
        /// freshly created pipeline objects. Asked directly.
        /// </summary>
        public static void NoteBlitPso(IntPtr pso)
        {
            if (!Enabled)
            {
                return;
            }

            _framePsoFresh = _seenPsos.Add(pso);
        }

        private static readonly HashSet<IntPtr> _seenPsos = new();
        private static bool _framePsoFresh;
        private static long _freshFlat, _freshNormal, _staleFlat, _staleNormal;

        /// <summary>
        /// Storage-image bindings whose storage IS the presented surface. The writer census
        /// covers render attachments; CopyTo and SetData have their own hooks - but a
        /// compute or fragment shader writing through an image (usage 0x17 carries
        /// ShaderWrite, and 15,809 read-write residency declarations go by per 120 frames)
        /// is invisible to every instrument fielded so far. The last uninstrumented way to
        /// put pixels in that texture.
        /// </summary>
        public static IntPtr LastPresentedRoot => _framePresentRoot;

        public static void NoteImageOnPresented(string program)
        {
            if (Enabled && _frameImgWriters.Count < 8)
            {
                _frameImgWriters.Add(program ?? "?");
            }
        }

        private static readonly List<string> _frameImgWriters = new();

        /// <summary>
        /// The creation serial of the texture object the blit samples, against the serial
        /// of the object its writer painted this frame. Equal addresses said "same texture"
        /// for two days; serials cannot be recycled, so a mismatch here means the cache
        /// swapped the host texture between the write and the read, and the blit sampled
        /// an object nobody painted.
        /// </summary>
        public static void NoteBlitInputSerial(long serial) { if (Enabled) { _inputSerial = serial; } }

        private static readonly Dictionary<IntPtr, long> _handleGen = new();

        public static void BumpHandleGen(IntPtr root)
        {
            if (Enabled && root != IntPtr.Zero)
            {
                lock (_handleGen)
                {
                    _handleGen.TryGetValue(root, out long g);
                    _handleGen[root] = g + 1;
                }
            }
        }

        private static long GenOf(IntPtr root)
        {
            lock (_handleGen)
            {
                return _handleGen.TryGetValue(root, out long g) ? g : 0;
            }
        }

        public static void NoteBlitInputGen(IntPtr root) { if (Enabled) { _inputGen = GenOf(root); } }

        /// <summary>
        /// The in-stream witness. A .gputrace of a caught white frame needs Xcode and an
        /// operator; this encodes the same evidence automatically: the blit's input,
        /// photographed by the GPU immediately after the blit's pass ends, against the same
        /// texels photographed at present. Memory that nothing writes in between (measured:
        /// 1/2,496) cannot differ between the two - so "white just after the blit, a
        /// picture at present" is a read-visibility failure witnessed in the act, and "a
        /// picture just after the blit" convicts the draw's execution instead.
        /// </summary>
        public static void ArmAfterBlitSample() { if (Enabled) { _afterBlitArmed = true; } }

        // ---- stage dump ------------------------------------------------------------
        // Generic "photograph this program's inputs and output right after its pass" for one
        // program label (RYUJINX_METAL_STAGE_LABEL, prefix match), written for the first
        // few white frames. Used to walk upstream: the composite's input is already white,
        // its first writer this frame is ff14da - dump ff14da's inputs and output.
        private static readonly string _stageLabel = Environment.GetEnvironmentVariable("RYUJINX_METAL_STAGE_LABEL") ?? "";
        // RYUJINX_METAL_STAGE_RT=WxH arms the stage dump by render-target size instead of by
        // the composite's input storage (for stages upstream of the upscaler).
        private static readonly int _stageRtW = ParseDim(Environment.GetEnvironmentVariable("RYUJINX_METAL_STAGE_RT"), 0);
        private static readonly int _stageRtH = ParseDim(Environment.GetEnvironmentVariable("RYUJINX_METAL_STAGE_RT"), 1);
        private static int ParseDim(string v, int part)
        {
            if (string.IsNullOrEmpty(v)) { return 0; }
            string[] p = v.ToLowerInvariant().Split('x');
            return p.Length == 2 && int.TryParse(p[part], out int d) ? d : 0;
        }
        private const int StageSlots = 8, StageMaxInputs = 16;
        private static bool _stageCollect;
        private static readonly MTLBuffer[] _stageOut = new MTLBuffer[StageSlots];
        // The stage input photographed BEFORE the stage's pass (at the pre-probe point).
        // The stage pass's OWN render target, photographed at the pass START (before its
        // draws) - so "before vs after this draw" is unambiguous on the same texture.
        private static readonly MTLBuffer[] _stagePreOut = new MTLBuffer[StageSlots];
        private static readonly int[] _stagePreOutBytes = new int[StageSlots];
        private static readonly string[] _stagePreOutDesc = new string[StageSlots];
        private static readonly long[] _stagePreOutFrame = new long[StageSlots];
        private static readonly FenceHolder[] _stagePreOutFence = new FenceHolder[StageSlots];

        // 25 texels of the stage pass's render target at the pass START, EVERY frame, so the
        // scene buffer's brightness trajectory across frames is visible: a multi-frame
        // blow-up is a feedback loop, a one-frame spike is a bad read.
        private const int SceneBase = 5 * Slots * Pixels * BytesPerPixel + 4 * Slots * Pixels * 16 + StageSlots * StageCbMax * StageCbFloats * 4 + TraceBytes + TinyBytes;
        private const int SceneBytes = Slots * Pixels * BytesPerPixel;
        private static readonly bool[] _sceneSampled = new bool[Slots];
        private static readonly string[] _sceneRing = new string[400];
        private static int _sceneRingAt;

        public static void NoteStageOutputBefore(CommandBufferScoped cbs, Texture rt)
        {
            // The armed target, not the caller's rt0: this program's matching attachment can
            // sit in any MRT slot, and rt0 was a different 1920x1080 texture.
            rt = _stageOutput ?? rt;

            if (!Enabled || _stageLabel.Length == 0 || rt == null || _buf.NativePtr == IntPtr.Zero)
            {
                return;
            }

            {
                int si = (int)(_frame % Slots);
                _sceneSampled[si] = false;
                MTLTexture st = rt.GetHandle(cbs);
                if (st.NativePtr != IntPtr.Zero && rt.Width > GridSide && rt.Height > GridSide &&
                    rt.MtlFormat == MTLPixelFormat.RG11B10Float)
                {
                    MTLBlitCommandEncoder sb = cbs.Encoders.EnsureBlitEncoder();
                    for (int i = 0; i < Pixels; i++)
                    {
                        sb.CopyFromTexture(st, 0, 0,
                            new MTLOrigin { x = (ulong)(rt.Width * (i % GridSide + 1) / (GridSide + 1)), y = (ulong)(rt.Height * (i / GridSide + 1) / (GridSide + 1)), z = 0 },
                            new MTLSize { width = 1, height = 1, depth = 1 },
                            _buf, (ulong)(SceneBase + (si * Pixels + i) * BytesPerPixel), BytesPerPixel, BytesPerPixel);
                    }
                    _sceneSampled[si] = true;
                }
            }

            if (_stagePreOut[0].NativePtr == IntPtr.Zero || _stageDumpsWritten >= DumpMaxFiles)
            {
                return;
            }

            int bpp = BytesPerPixelOf(rt.MtlFormat);
            int bytes = rt.Width * rt.Height * bpp;
            if (bytes > DumpMaxBytes) { return; }

            MTLTexture mt = rt.GetHandle(cbs);
            if (mt.NativePtr == IntPtr.Zero) { return; }

            int j = (int)(_frame % StageSlots);
            MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();
            blit.CopyFromTexture(mt, 0, 0, new MTLOrigin { x = 0, y = 0, z = 0 },
                new MTLSize { width = (ulong)rt.Width, height = (ulong)rt.Height, depth = 1 },
                _stagePreOut[j], 0, (ulong)(rt.Width * bpp), (ulong)bytes);
            _stagePreOutBytes[j] = bytes;
            _stagePreOutDesc[j] = $"{rt.Width}x{rt.Height}_fmt{(int)rt.MtlFormat}";
            _stagePreOutFrame[j] = _frame;
            _stagePreOutFence[j]?.Put();
            _stagePreOutFence[j] = cbs.GetFence();
            _stagePreOutFence[j].Get();
        }

        private static readonly MTLBuffer[] _stagePre = new MTLBuffer[StageSlots];
        private static readonly int[] _stagePreBytes = new int[StageSlots];
        private static readonly string[] _stagePreDesc = new string[StageSlots];
        private static readonly long[] _stagePreFrame = new long[StageSlots];
        private static readonly FenceHolder[] _stagePreFence = new FenceHolder[StageSlots];
        // Writers of the stage input this frame (census snapshot at the stage draw).
        private static string _frameStageInputWriters = "-", _frameStageInputLastWriters = "-";
        private static readonly Dictionary<string, (long Flat, long Normal)> _stageInputLastWriterStats = new();
        private static readonly Dictionary<string, (long Flat, long Normal)> _stageInputWriterStats = new();
        private static readonly MTLBuffer[,] _stageIn = new MTLBuffer[StageSlots, StageMaxInputs];
        private static readonly FenceHolder[] _stageFence = new FenceHolder[StageSlots];
        private static readonly long[] _stageFrame = new long[StageSlots];
        private static readonly string[] _stageOutDesc = new string[StageSlots];
        private static readonly int[] _stageOutBytes = new int[StageSlots];
        private static readonly IntPtr[] _stageOutPtr = new IntPtr[StageSlots];
        private static readonly string[,] _stageInDesc = new string[StageSlots, StageMaxInputs];
        private static readonly int[,] _stageInBytes = new int[StageSlots, StageMaxInputs];
        private static readonly List<Texture> _stageInputs = new();
        private static Texture _stageOutput;
        private static bool _stageArmed;
        private static long _stageArmedFrame = -1;
        private static int _stageDumpsWritten, _stageDumpsNormal;
        private static long _stageArms, _stageSamples, _stageMisses, _stageMissLogged;

        // The stage program's constant buffers: what the CPU side holds at bind time (the
        // buffer/mirror the draw is bound to) and what the GPU sees at the pass end (blit of
        // the same range), per ring slot. The upscaler's texel-coordinate transform lives in
        // fp_c3[0] (scale.xy, offset.zw); a collapsed scale makes every output pixel read the
        // same 4x3 neighbourhood, which is exactly a uniform near-white field after the x3.5.
        private const int StageCbMax = 8, StageCbFloats = 32;
        private const int StageCbBase = 5 * Slots * Pixels * BytesPerPixel + 4 * Slots * Pixels * 16;  // after the pre-probe regions
        private static readonly MTLBuffer[] _stageCbBuf = new MTLBuffer[StageCbMax];
        private static readonly int[] _stageCbOffset = new int[StageCbMax];
        private static readonly int[] _stageCbBinding = new int[StageCbMax];
        private static int _stageCbCount;
        private static readonly float[,,] _stageCbCpu = new float[StageSlots, StageCbMax, StageCbFloats];
        private static readonly int[,] _stageCbBindingAt = new int[StageSlots, StageCbMax];
        private static readonly int[] _stageCbCountAt = new int[StageSlots];
        private static long _cbEqFlat, _cbEqNormal, _cbDiffFlat, _cbDiffNormal, _cbUnknownFlat, _cbUnknownNormal, _cbDiffLogged;
        private struct CbStat { public long Flat, Normal; public double FMin, FMax, NMin, NMax, FSum, NSum; }
        private static readonly Dictionary<string, CbStat> _cbStats = new();
        private static readonly List<string> _sbSamples = new();
        private static int _sbWhiteSamples, _sbNormalSamples;

        private static double Rg11(uint v)
        {
            uint e = (v >> 6) & 0x1f, m = v & 0x3f;
            return e == 0 ? m / 64.0 * Math.Pow(2, -14) : (e == 31 ? double.PositiveInfinity : (1 + m / 64.0) * Math.Pow(2, (int)e - 15));
        }

        private static double B10(uint v)
        {
            uint e = (v >> 5) & 0x1f, m = v & 0x1f;
            return e == 0 ? m / 32.0 * Math.Pow(2, -14) : (e == 31 ? double.PositiveInfinity : (1 + m / 32.0) * Math.Pow(2, (int)e - 15));
        }

        private static string SceneSeriesText()
        {
            if (_sceneRingAt == 0) { return ""; }
            StringBuilder sb = new();
            int n = Math.Min(_sceneRingAt, _sceneRing.Length);
            int start = _sceneRingAt >= _sceneRing.Length ? _sceneRingAt % _sceneRing.Length : 0;
            sb.Append($"\n  SCENE BUFFER mean luma at the {_stageLabel} pass START, last {n} frames (W = presented white):");
            for (int k = 0; k < n; k++)
            {
                if (k % 8 == 0) { sb.Append("\n      "); }
                sb.Append($"{_sceneRing[(start + k) % _sceneRing.Length],-22}");
            }
            return sb.ToString();
        }

        private static string TinyPreText()
        {
            if (_tinyPreStats.Count == 0) { return ""; }
            StringBuilder sb = new();
            sb.Append("\n  EXPOSURE 1x1 at the stage PASS START (before the first flare draw), by outcome:");
            foreach (KeyValuePair<string, CbStat> kv in System.Linq.Enumerable.OrderBy(_tinyPreStats, x => x.Key))
            {
                CbStat v = kv.Value;
                bool dj = v.Flat > 0 && v.Normal > 0 && (v.FMin > v.NMax || v.FMax < v.NMin);
                sb.Append($"\n      {kv.Key}: WHITE [{v.FMin:G6} .. {v.FMax:G6}] n={v.Flat}   normal [{v.NMin:G6} .. {v.NMax:G6}] n={v.Normal}{(dj ? "   <<<< DISJOINT" : "")}");
            }
            foreach (string l in _tinyPreSamples) { sb.Append("\n      " + l); }
            return sb.ToString();
        }

        private static string TinySeriesText()
        {
            if (_tinySeries.Count == 0) { return ""; }
            StringBuilder sb = new();
            sb.Append($"\n  EXPOSURE transitions ({_tinySeries.Count} runs; frames..frames xN value|outcome|ptr):");
            foreach (string l in _tinySeries) { sb.Append("\n      " + l); }
            return sb.ToString();
        }

        private static string ConstStatsText()
        {
            if (_constLabel.Length == 0 || _constStats.Count == 0) { return ""; }
            StringBuilder sb = new();
            sb.Append($"\n  const[{_constLabel}] uniform slots 0..4, floats [0..7]=vec4[0].xyzw vec4[1].xyzw, by outcome (only nonzero-spread rows):");
            foreach (KeyValuePair<int, (long Fn, double Fmin, double Fmax, double Fsum, long Nn, double Nmin, double Nmax, double Nsum)> kv in System.Linq.Enumerable.OrderBy(_constStats, x => x.Key))
            {
                (long Fn, double Fmin, double Fmax, double Fsum, long Nn, double Nmin, double Nmax, double Nsum) v = kv.Value;
                bool interesting = (v.Fn > 0 && (v.Fmax - v.Fmin) != 0) || (v.Nn > 0 && (v.Nmax - v.Nmin) != 0) || (v.Fn > 0 && v.Nn > 0 && Math.Abs(v.Fsum / Math.Max(1, v.Fn) - v.Nsum / Math.Max(1, v.Nn)) > 1e-4);
                if (!interesting) { continue; }
                sb.Append($"\n      s{kv.Key / ConstFloats}[{kv.Key % ConstFloats}] WHITE min {(v.Fn > 0 ? v.Fmin : 0):G5} max {(v.Fn > 0 ? v.Fmax : 0):G5} mean {(v.Fn > 0 ? v.Fsum / v.Fn : 0):G5} (n {v.Fn}) | normal min {(v.Nn > 0 ? v.Nmin : 0):G5} max {(v.Nn > 0 ? v.Nmax : 0):G5} mean {(v.Nn > 0 ? v.Nsum / v.Nn : 0):G5} (n {v.Nn})");
            }
            return sb.ToString();
        }

        private static string StageDrawStatsText()
        {
            StringBuilder sb = new();
            sb.Append("\n  stage INPUT writers this frame (before the stage draw) by outcome:");
            foreach (KeyValuePair<string, (long Flat, long Normal)> kv in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(_stageInputWriterStats, x => x.Value.Flat + x.Value.Normal), 10))
            {
                long tot = kv.Value.Flat + kv.Value.Normal;
                sb.Append($"\n      [{kv.Key}] flat {kv.Value.Flat} normal {kv.Value.Normal} ({(tot > 0 ? 100.0 * kv.Value.Flat / tot : 0):F1}% white)");
            }
            sb.Append("\n  stage INPUT identity vs previous frame by outcome:");
            foreach (KeyValuePair<string, (long Flat, long Normal)> kv in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(_stageInputIdStats, x => x.Value.Flat + x.Value.Normal), 8))
            {
                long tot = kv.Value.Flat + kv.Value.Normal;
                sb.Append($"\n      [{kv.Key}] flat {kv.Value.Flat} normal {kv.Value.Normal} ({(tot > 0 ? 100.0 * kv.Value.Flat / tot : 0):F1}% white)");
            }
            sb.Append("\n  stage INPUT LAST writers (last 6 in draw order, before the stage draw) by outcome:");
            foreach (KeyValuePair<string, (long Flat, long Normal)> kv in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(_stageInputLastWriterStats, x => x.Value.Flat + x.Value.Normal), 10))
            {
                long tot = kv.Value.Flat + kv.Value.Normal;
                sb.Append($"\n      [{kv.Key}] flat {kv.Value.Flat} normal {kv.Value.Normal} ({(tot > 0 ? 100.0 * kv.Value.Flat / tot : 0):F1}% white)");
            }
            sb.Append("\n  READ-AFTER-WRITE SPLITS per frame (bucketed by 25), by outcome:");
            foreach (KeyValuePair<int, (long Flat, long Normal)> kv in System.Linq.Enumerable.OrderBy(_rawSplitStats, x => x.Key))
            {
                long tot = kv.Value.Flat + kv.Value.Normal;
                sb.Append($"\n      {kv.Key,5}+: flat {kv.Value.Flat,6} normal {kv.Value.Normal,6} ({(tot > 0 ? 100.0 * kv.Value.Flat / tot : 0):F1}% white)");
            }
            sb.Append("\n  BARRIERS DROPPED (no draw yet in the pass) per frame (bucketed by 25), by outcome:");
            foreach (KeyValuePair<int, (long Flat, long Normal)> kv in System.Linq.Enumerable.OrderBy(_barrierSkipStats, x => x.Key))
            {
                long tot = kv.Value.Flat + kv.Value.Normal;
                sb.Append($"\n      {kv.Key,5}+: flat {kv.Value.Flat,6} normal {kv.Value.Normal,6} ({(tot > 0 ? 100.0 * kv.Value.Flat / tot : 0):F1}% white)");
            }
            sb.Append($"\n  {_stageLabel} DRAWS PER FRAME into the traced target, by outcome:");
            foreach (KeyValuePair<int, (long Flat, long Normal)> kv in System.Linq.Enumerable.OrderBy(_stageDrawCountStats, x => x.Key))
            {
                long tot = kv.Value.Flat + kv.Value.Normal;
                sb.Append($"\n      {kv.Key,4} draws: flat {kv.Value.Flat,6} normal {kv.Value.Normal,6} ({(tot > 0 ? 100.0 * kv.Value.Flat / tot : 0):F1}% white)");
            }
            sb.Append("\n  stage draw BLEND state by outcome:");
            foreach (KeyValuePair<string, (long Flat, long Normal)> kv in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(_stageBlendStats, x => x.Value.Flat + x.Value.Normal), 6))
            {
                long tot = kv.Value.Flat + kv.Value.Normal;
                sb.Append($"\n      [{kv.Key}] flat {kv.Value.Flat} normal {kv.Value.Normal} ({(tot > 0 ? 100.0 * kv.Value.Flat / tot : 0):F1}% white)");
            }
            sb.Append("\n  stage draw render targets by outcome:");
            foreach (KeyValuePair<string, (long Flat, long Normal)> kv in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(_stageDrawStats, x => x.Value.Flat + x.Value.Normal), 8))
            {
                long tot = kv.Value.Flat + kv.Value.Normal;
                sb.Append($"\n      [{kv.Key}] flat {kv.Value.Flat} normal {kv.Value.Normal} ({(tot > 0 ? 100.0 * kv.Value.Flat / tot : 0):F1}% white)");
            }
            return sb.ToString();
        }

        private static string CbStatsText()
        {
            StringBuilder sb = new();
            if (_sbSamples.Count > 0)
            {
                sb.Append("\n  STORAGE word[0] per frame (CPU at bind / GPU at pass end):");
                foreach (string l in _sbSamples) { sb.Append("\n      " + l); }
            }
            foreach (KeyValuePair<string, CbStat> kv in System.Linq.Enumerable.OrderBy(_cbStats, x => x.Key))
            {
                CbStat v = kv.Value;
                bool differs = v.Flat > 0 && v.Normal > 0 && (v.FMin > v.NMax || v.FMax < v.NMin);
                sb.Append($"\n      {kv.Key}: WHITE [{(v.Flat > 0 ? v.FMin : 0):G6} .. {(v.Flat > 0 ? v.FMax : 0):G6}] n={v.Flat}   normal [{(v.Normal > 0 ? v.NMin : 0):G6} .. {(v.Normal > 0 ? v.NMax : 0):G6}] n={v.Normal}{(differs ? "   <<<< DISJOINT" : "")}");
            }
            return sb.ToString();
        }

        // The vertex buffers bound at the armed draw. out.a = sample.a * fp_c3[0].w * attr1.x
        // and the blend takes src.a as the rgb factor, so the whole brightness is
        // proportional to a vertex attribute - the one input never measured.
        private static readonly Dictionary<string, CbStat> _vtxStats = new();
        private const int VtxFloats = 24;
        private static readonly float[,,] _vtxCpuN = new float[StageSlots, 4, VtxFloats];
        private static readonly bool[,] _vtxSeen = new bool[StageSlots, 4];
        private static readonly int[,] _vtxStride = new int[StageSlots, 4];
        private static readonly int[] _vtxCount = new int[StageSlots];
        private static readonly string[] _vtxDesc = new string[StageSlots];

        private const int VtxBufs = 4;

        public static unsafe void NoteStageVertexBuffer(int index, MTLBuffer buf, int offset, int stride)
        {
            if (!Enabled || !_stageCollect || buf.NativePtr == IntPtr.Zero || (uint)index >= VtxBufs)
            {
                return;
            }

            int j = (int)(_frame % StageSlots);
            IntPtr c = buf.Contents;
            if (c == IntPtr.Zero) { return; }

            float* f = (float*)((byte*)c + offset);
            for (int i = 0; i < VtxFloats; i++) { _vtxCpuN[j, index, i] = f[i]; }
            _vtxSeen[j, index] = true;
            _vtxStride[j, index] = stride;
            _vtxCount[j] = VtxFloats;
        }

        private static void ClassifyStageVertex(long presentedFrame, bool flat)
        {
            long want = presentedFrame - 1;
            for (int j = 0; j < StageSlots; j++)
            {
                if (_stageFrame[j] != want || _vtxCount[j] == 0) { continue; }
                for (int b = 0; b < VtxBufs; b++)
                {
                    if (!_vtxSeen[j, b]) { continue; }
                    for (int i = 0; i < _vtxCount[j]; i++)
                    {
                        string key = $"vb{b}s{_vtxStride[j, b]}[{i:D2}]";
                        CbStat v = _vtxStats.TryGetValue(key, out CbStat e) ? e : new CbStat { FMin = double.MaxValue, FMax = double.MinValue, NMin = double.MaxValue, NMax = double.MinValue };
                        double d = _vtxCpuN[j, b, i];
                        if (flat) { v.Flat++; v.FMin = Math.Min(v.FMin, d); v.FMax = Math.Max(v.FMax, d); }
                        else { v.Normal++; v.NMin = Math.Min(v.NMin, d); v.NMax = Math.Max(v.NMax, d); }
                        _vtxStats[key] = v;
                    }
                }
                return;
            }
        }

        private static string VtxStatsText()
        {
            if (_vtxStats.Count == 0) { return ""; }
            StringBuilder sb = new();
            sb.Append("\n  stage draw VERTEX BUFFERS (vb<index>s<stride>) by outcome:");
            foreach (KeyValuePair<string, CbStat> kv in System.Linq.Enumerable.OrderBy(_vtxStats, x => x.Key))
            {
                CbStat v = kv.Value;
                bool disjoint = v.Flat > 0 && v.Normal > 0 && (v.FMin > v.NMax || v.FMax < v.NMin);
                sb.Append($"\n      {kv.Key}: WHITE [{(v.Flat > 0 ? v.FMin : 0):G6} .. {(v.Flat > 0 ? v.FMax : 0):G6}] n={v.Flat}   normal [{(v.Normal > 0 ? v.NMin : 0):G6} .. {(v.Normal > 0 ? v.NMax : 0):G6}] n={v.Normal}{(disjoint ? "   <<<< DISJOINT" : "")}");
            }
            return sb.ToString();
        }

        // Every draw of the stage program in the frame (up to 8), every captured binding, all
        // 32 words, CPU-side at bind. Classified per outcome; only fields whose white and
        // normal ranges are DISJOINT are printed. The first-draw-only capture looked at 8
        // words and the flare's position/scale and its two multipliers live beyond them.
        private const int PdDraws = 8, PdWords = 32;
        private static readonly float[,,,] _pdCpu = new float[StageSlots, PdDraws, StageCbMax, PdWords];
        private static readonly int[,,] _pdBinding = new int[StageSlots, PdDraws, StageCbMax];
        private static readonly int[,] _pdCount = new int[StageSlots, PdDraws];
        private static readonly long[] _pdFrame = new long[StageSlots];
        private static readonly Dictionary<string, CbStat> _pdStats = new();
        private static int _pdDrawIndex = -1;   // the draw within the frame currently binding

        // ---- sampler registry + per-draw sampler/raster census for the stage program ----
        private static readonly Dictionary<ulong, string> _samplerDesc = new();
        public static void NoteSamplerCreated(ulong id, string desc)
        {
            if (Enabled) { lock (_samplerDesc) { _samplerDesc[id] = desc; } }
        }

        private static readonly StringBuilder[] _pdSamplers = new StringBuilder[PdDraws];
        private static readonly string[,] _pdSamplerAt = new string[StageSlots, PdDraws];
        private static readonly string[,] _pdRasterAt = new string[StageSlots, PdDraws];
        private static readonly Dictionary<string, (long Flat, long Normal)> _pdSamplerStats = new();
        private static readonly Dictionary<string, (long Flat, long Normal)> _pdRasterStats = new();

        public static void NoteStageSampler(int binding, ulong texId, int texW, int texH, ulong samplerId)
        {
            if (!Enabled || _pdDrawIndex < 0 || _pdDrawIndex >= PdDraws) { return; }
            string desc;
            lock (_samplerDesc) { desc = _samplerDesc.TryGetValue(samplerId, out string d) ? d : "?"; }
            StringBuilder sb = _pdSamplers[_pdDrawIndex] ??= new StringBuilder();
            if (sb.Length < 700) { sb.Append($" t{binding}:{texW}x{texH}#{texId:X}/s{samplerId:X}={desc}"); }
        }

        // The draw arguments themselves. The flare sprites are additive and the vertex
        // shader indexes by instance: an instance count of 100 instead of 1 is a 100x
        // brighter sprite with every other input identical - the one shape left.
        private static readonly string[,] _pdArgsAt = new string[StageSlots, PdDraws];
        private static readonly Dictionary<string, (long Flat, long Normal)> _pdArgsStats = new();
        // ---- GPU-time readback of the stage program's fragment Textures table ----
        private const int ArgWords = 16;
        private static readonly MTLBuffer[,] _argBuf = new MTLBuffer[StageSlots, PdDraws];
        private static readonly int[,] _argOff = new int[StageSlots, PdDraws];
        private static readonly int[,] _argN = new int[StageSlots, PdDraws];
        private static readonly ulong[,,] _argCpu = new ulong[StageSlots, PdDraws, ArgWords];
        private static readonly bool[,] _argSampled = new bool[StageSlots, PdDraws];
        private static long _argEqFlat, _argEqNormal, _argDiffFlat, _argDiffNormal;
        private static readonly List<string> _argDiffSamples = new();
        private const int ArgBase = 5 * Slots * Pixels * BytesPerPixel + 4 * Slots * Pixels * 16 + StageSlots * StageCbMax * StageCbFloats * 4 + TraceBytes + TinyBytes + SceneBytes;
        private const int ArgBytes = StageSlots * PdDraws * ArgWords * 8;

        public static void NoteStageArgTable(MTLBuffer buf, int offset, ReadOnlySpan<ulong> ids)
        {
            if (!Enabled || _pdDrawIndex < 0 || _pdDrawIndex >= PdDraws || buf.NativePtr == IntPtr.Zero) { return; }
            int j = (int)(_frame % StageSlots);
            if (_pdFrame[j] != _frame) { return; }
            int n = Math.Min(ids.Length, ArgWords);
            _argBuf[j, _pdDrawIndex] = buf;
            _argOff[j, _pdDrawIndex] = offset;
            _argN[j, _pdDrawIndex] = n;
            for (int i = 0; i < n; i++) { _argCpu[j, _pdDrawIndex, i] = ids[i]; }
            _argSampled[j, _pdDrawIndex] = false;
        }

        private static void BlitStageArgTables(int j, MTLBlitCommandEncoder blit)
        {
            for (int d = 0; d < PdDraws; d++)
            {
                if (_argN[j, d] == 0 || _argBuf[j, d].NativePtr == IntPtr.Zero || _argSampled[j, d]) { continue; }
                blit.CopyFromBuffer(_argBuf[j, d], (ulong)_argOff[j, d], _buf, (ulong)(ArgBase + (j * PdDraws + d) * ArgWords * 8), (ulong)(_argN[j, d] * 8));
                _argSampled[j, d] = true;
            }
        }

        private static unsafe void ClassifyStageArgTables(long presentedFrame, bool flat)
        {
            long want = presentedFrame - 1;
            int j = (int)(want % StageSlots);
            if (_pdFrame[j] != want) { return; }
            for (int d = 0; d < PdDraws; d++)
            {
                if (!_argSampled[j, d]) { continue; }
                ulong* g = (ulong*)((byte*)_buf.Contents + ArgBase + (j * PdDraws + d) * ArgWords * 8);
                bool diff = false; StringBuilder sb = null;
                for (int i = 0; i < _argN[j, d]; i++)
                {
                    if (g[i] != _argCpu[j, d, i])
                    {
                        diff = true; sb ??= new StringBuilder();
                        if (sb.Length < 300) { sb.Append($" w{i}: cpu {_argCpu[j, d, i]:X} gpu {g[i]:X};"); }
                    }
                }
                if (diff) { if (flat) { _argDiffFlat++; } else { _argDiffNormal++; } if (_argDiffSamples.Count < 40) { _argDiffSamples.Add($"f{presentedFrame} {(flat ? "WHITE " : "normal")} d{d}:{sb}"); } }
                else { if (flat) { _argEqFlat++; } else { _argEqNormal++; } }
                _argSampled[j, d] = false;
                _argN[j, d] = 0;
            }
        }

        private static string ArgTableText()
        {
            StringBuilder sb = new();
            sb.Append($"\n  FRAGMENT TEXTURES TABLE at GPU pass end vs CPU-written ids (per draw): equal flat {_argEqFlat} normal {_argEqNormal} | DIFFER flat {_argDiffFlat} normal {_argDiffNormal}");
            foreach (string l in _argDiffSamples) { sb.Append("\n      " + l); }
            return sb.ToString();
        }

        public static void NoteStageDrawArgs(string desc)
        {
            if (!Enabled || _pdDrawIndex < 0 || _pdDrawIndex >= PdDraws) { return; }
            int j = (int)(_frame % StageSlots);
            if (_pdFrame[j] == _frame) { _pdArgsAt[j, _pdDrawIndex] = desc; }
        }

        public static void NoteStageRaster(string desc)
        {
            if (!Enabled || _pdDrawIndex < 0 || _pdDrawIndex >= PdDraws) { return; }
            int j = (int)(_frame % StageSlots);
            if (_pdFrame[j] == _frame) { _pdRasterAt[j, _pdDrawIndex] = desc; }
        }

        public static void NoteStageDrawBinding()
        {
            if (!Enabled) { return; }
            int j = (int)(_frame % StageSlots);
            if (_pdFrame[j] != _frame)
            {
                _pdFrame[j] = _frame;
                for (int d = 0; d < PdDraws; d++) { _pdCount[j, d] = 0; _pdSamplerAt[j, d] = null; _pdRasterAt[j, d] = null; _pdArgsAt[j, d] = null; _pdSamplers[d]?.Clear(); }
                _pdDrawIndex = -1;
            }
            if (_pdDrawIndex >= 0 && _pdDrawIndex < PdDraws && _pdSamplers[_pdDrawIndex] != null)
            {
                _pdSamplerAt[j, _pdDrawIndex] = _pdSamplers[_pdDrawIndex].ToString();
                _pdSamplers[_pdDrawIndex].Clear();
            }
            _pdDrawIndex++;
        }

        public static unsafe void NoteStageUniformAny(int binding, MTLBuffer buf, int offset)
        {
            if (!Enabled || buf.NativePtr == IntPtr.Zero || _pdDrawIndex < 0 || _pdDrawIndex >= PdDraws) { return; }
            int j = (int)(_frame % StageSlots);
            if (_pdFrame[j] != _frame) { return; }
            int k = _pdCount[j, _pdDrawIndex];
            if (k >= StageCbMax) { return; }
            IntPtr c = buf.Contents;
            if (c == IntPtr.Zero) { return; }
            float* f = (float*)((byte*)c + offset);
            for (int i = 0; i < PdWords; i++) { _pdCpu[j, _pdDrawIndex, k, i] = f[i]; }
            _pdBinding[j, _pdDrawIndex, k] = binding;
            _pdCount[j, _pdDrawIndex] = k + 1;
        }

        private static void ClassifyPerDraw(long presentedFrame, bool flat)
        {
            long want = presentedFrame - 1;
            int j = (int)(want % StageSlots);
            if (_pdFrame[j] != want) { return; }
            for (int d = 0; d < PdDraws; d++)
            {
                for (int k = 0; k < _pdCount[j, d]; k++)
                {
                    int b = _pdBinding[j, d, k];
                    NoteFlareMultipliers(j, d, k, b, flat);
                    for (int i = 0; i < PdWords; i++)
                    {
                        float cpu = _pdCpu[j, d, k, i];
                        double v = b >= 1000 ? BitConverter.SingleToInt32Bits(cpu) : cpu;
                        string key = $"d{d}:{(b >= 2000 ? "v" + (b - 2000) : b >= 1000 ? "s" + (b - 1000) : "b" + b)}[{i:D2}]";
                        CbStat st = _pdStats.TryGetValue(key, out CbStat e) ? e : new CbStat { FMin = double.MaxValue, FMax = double.MinValue, NMin = double.MaxValue, NMax = double.MinValue };
                        if (flat) { st.Flat++; st.FMin = Math.Min(st.FMin, v); st.FMax = Math.Max(st.FMax, v); st.FSum += v; }
                        else { st.Normal++; st.NMin = Math.Min(st.NMin, v); st.NMax = Math.Max(st.NMax, v); st.NSum += v; }
                        _pdStats[key] = st;
                    }
                }
            }
        }

        private static void ClassifyPerDrawSamplers(long presentedFrame, bool flat)
        {
            long want = presentedFrame - 1;
            int j = (int)(want % StageSlots);
            if (_pdFrame[j] != want) { return; }
            for (int d = 0; d < PdDraws; d++)
            {
                if (_pdSamplerAt[j, d] != null)
                {
                    string k = $"d{d}:{_pdSamplerAt[j, d]}";
                    (long Flat, long Normal) v = _pdSamplerStats.TryGetValue(k, out (long Flat, long Normal) e) ? e : (0, 0);
                    _pdSamplerStats[k] = flat ? (v.Flat + 1, v.Normal) : (v.Flat, v.Normal + 1);
                }
                if (_pdRasterAt[j, d] != null)
                {
                    string k = $"d{d}:{_pdRasterAt[j, d]}";
                    (long Flat, long Normal) v = _pdRasterStats.TryGetValue(k, out (long Flat, long Normal) e) ? e : (0, 0);
                    _pdRasterStats[k] = flat ? (v.Flat + 1, v.Normal) : (v.Flat, v.Normal + 1);
                }
                if (_pdArgsAt[j, d] != null)
                {
                    string k = $"d{d}:{_pdArgsAt[j, d]}";
                    (long Flat, long Normal) v = _pdArgsStats.TryGetValue(k, out (long Flat, long Normal) e) ? e : (0, 0);
                    _pdArgsStats[k] = flat ? (v.Flat + 1, v.Normal) : (v.Flat, v.Normal + 1);
                }
            }
        }

        private static string PerDrawSamplerText()
        {
            StringBuilder sb = new();
            sb.Append("\n  PER-DRAW SAMPLERS (draw:binding:texture/samplerId=descriptor) by outcome, top entries:");
            foreach (KeyValuePair<string, (long Flat, long Normal)> kv in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(_pdSamplerStats, x => x.Value.Flat + x.Value.Normal), 16))
            {
                long tot = kv.Value.Flat + kv.Value.Normal;
                sb.Append($"\n      [{kv.Key}] flat {kv.Value.Flat} normal {kv.Value.Normal} ({(tot > 0 ? 100.0 * kv.Value.Flat / tot : 0):F1}% white)");
            }
            sb.Append("\n  PER-DRAW ARGUMENTS (vertex/instance counts) by outcome, top entries:");
            foreach (KeyValuePair<string, (long Flat, long Normal)> kv in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(_pdArgsStats, x => x.Value.Flat + x.Value.Normal), 20))
            {
                long tot = kv.Value.Flat + kv.Value.Normal;
                sb.Append($"\n      [{kv.Key}] flat {kv.Value.Flat} normal {kv.Value.Normal} ({(tot > 0 ? 100.0 * kv.Value.Flat / tot : 0):F1}% white)");
            }
            sb.Append("\n  PER-DRAW RASTER (viewport/scissor) by outcome, top entries:");
            foreach (KeyValuePair<string, (long Flat, long Normal)> kv in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(_pdRasterStats, x => x.Value.Flat + x.Value.Normal), 12))
            {
                long tot = kv.Value.Flat + kv.Value.Normal;
                sb.Append($"\n      [{kv.Key}] flat {kv.Value.Flat} normal {kv.Value.Normal} ({(tot > 0 ? 100.0 * kv.Value.Flat / tot : 0):F1}% white)");
            }
            return sb.ToString();
        }

        // vp_c3[2].w (word 11) and vp_c3[6].z (word 26) of the flare's vertex constant buffer,
        // and their product - the only factors of outAttr1.x not yet accounted for.
        private static readonly Dictionary<string, CbStat> _flareMul = new();

        private static void NoteFlareMultipliers(int j, int d, int k, int b, bool flat)
        {
            if (b < 2000) { return; }
            double w11 = _pdCpu[j, d, k, 11], w26 = _pdCpu[j, d, k, 26];
            (string, double)[] items = { ($"v{b - 2000}:c3[2].w", w11), ($"v{b - 2000}:c3[6].z", w26), ($"v{b - 2000}:product", w11 * w26) };
            foreach ((string name, double val) in items)
            {
                string key = $"d{d}:{name}";
                CbStat st = _flareMul.TryGetValue(key, out CbStat e) ? e : new CbStat { FMin = double.MaxValue, FMax = double.MinValue, NMin = double.MaxValue, NMax = double.MinValue };
                if (flat) { st.Flat++; st.FMin = Math.Min(st.FMin, val); st.FMax = Math.Max(st.FMax, val); st.FSum += val; }
                else { st.Normal++; st.NMin = Math.Min(st.NMin, val); st.NMax = Math.Max(st.NMax, val); st.NSum += val; }
                _flareMul[key] = st;
            }
        }

        private static string FlareMulText()
        {
            if (_flareMul.Count == 0) { return ""; }
            StringBuilder sb = new();
            sb.Append("\n  FLARE VERTEX MULTIPLIERS (vp_c3[2].w, vp_c3[6].z, product) by outcome:");
            foreach (KeyValuePair<string, CbStat> kv in System.Linq.Enumerable.OrderBy(_flareMul, x => x.Key))
            {
                CbStat v = kv.Value;
                if (v.Flat == 0 && v.Normal == 0) { continue; }
                double fm = v.Flat > 0 ? v.FSum / v.Flat : 0, nm = v.Normal > 0 ? v.NSum / v.Normal : 0;
                bool dj = v.Flat > 0 && v.Normal > 0 && (v.FMin > v.NMax || v.FMax < v.NMin);
                sb.Append($"\n      {kv.Key}: WHITE mean {fm:G6} [{v.FMin:G4}..{v.FMax:G4}] n={v.Flat}   normal mean {nm:G6} [{v.NMin:G4}..{v.NMax:G4}] n={v.Normal}{(dj ? "   <<<< DISJOINT" : "")}");
            }
            return sb.ToString();
        }

        private static string PerDrawStatsText()
        {
            if (_pdStats.Count == 0) { return ""; }
            StringBuilder sb = new();
            int disjoint = 0, total = 0;
            // The flare intensity is exp.x*exp.y*count*vp_c3[6].z*vp_c3[2].w; clamping the
            // result at 1000 removes the white entirely, so one of those factors is enormous.
            // Means, not just extrema: a legitimately bright flare overlaps the range.
            sb.Append("\n  FLARE FACTOR WORDS (vp_c3[2] = words 8-11, vp_c3[6] = words 24-27), mean by outcome:");
            foreach (KeyValuePair<string, CbStat> kv in System.Linq.Enumerable.OrderBy(_pdStats, x => x.Key))
            {
                CbStat v = kv.Value;
                if (v.Flat == 0 || v.Normal == 0) { continue; }
                int wi = int.Parse(kv.Key.Substring(kv.Key.IndexOf('[') + 1, 2));
                if (wi is < 8 or > 27 || (wi > 11 && wi < 24)) { continue; }
                double fm = v.FSum / v.Flat, nm = v.NSum / v.Normal;
                double ratio = Math.Abs(nm) > 1e-12 ? fm / nm : (Math.Abs(fm) > 1e-12 ? double.PositiveInfinity : 1);
                if (Math.Abs(ratio - 1) < 0.05 && Math.Abs(fm) < 1e3) { continue; }
                sb.Append($"\n      {kv.Key}: WHITE mean {fm:G6} [{v.FMin:G4}..{v.FMax:G4}]   normal mean {nm:G6} [{v.NMin:G4}..{v.NMax:G4}]   ratio {ratio:G4}");
            }
            sb.Append("\n  PER-DRAW constants/storage of the stage program, fields whose WHITE and normal MEANS differ by >50% (or whose ranges are disjoint):");
            foreach (KeyValuePair<string, CbStat> kv in System.Linq.Enumerable.OrderBy(_pdStats, x => x.Key))
            {
                CbStat v = kv.Value; total++;
                if (v.Flat == 0 || v.Normal == 0) { continue; }
                // Disjoint ranges are too strict. A factor that is zero on most frames and
                // large on the rest never separates by range, yet its MEAN can differ by an
                // order of magnitude - and the flare's intensity is proportional to exactly
                // such a factor (the occlusion count).
                double fm = v.Flat > 0 ? v.FSum / v.Flat : 0, nm = v.Normal > 0 ? v.NSum / v.Normal : 0;
                bool dj = v.FMin > v.NMax || v.FMax < v.NMin;
                double scale = Math.Max(Math.Abs(fm), Math.Abs(nm));
                bool meanDiff = scale > 1e-9 && Math.Abs(fm - nm) > 0.5 * scale;
                if (dj || meanDiff)
                {
                    disjoint++;
                    sb.Append($"\n      {kv.Key}: WHITE mean {fm:G6} [{v.FMin:G4}..{v.FMax:G4}] n={v.Flat}   normal mean {nm:G6} [{v.NMin:G4}..{v.NMax:G4}] n={v.Normal}{(dj ? "  DISJOINT" : "  MEAN")}");
                }
            }
            sb.Append($"\n      ({disjoint} of {total} fields flagged)");
            return sb.ToString();
        }

        public static unsafe void NoteStageUniform(int binding, MTLBuffer buf, int offset, int size)
        {
            NoteStageUniformAny(binding, buf, offset);

            if (!Enabled || !_stageCollect || buf.NativePtr == IntPtr.Zero || _stageCbCount >= StageCbMax)
            {
                return;
            }

            _stageCbBuf[_stageCbCount] = buf;
            _stageCbOffset[_stageCbCount] = offset;
            _stageCbBinding[_stageCbCount] = binding;
            _stageCbCount++;
        }

        private static unsafe void SnapshotStageCb(int j, MTLBlitCommandEncoder blit)
        {
            _stageCbCountAt[j] = _stageCbCount;
            for (int k = 0; k < _stageCbCount; k++)
            {
                _stageCbBindingAt[j, k] = _stageCbBinding[k];
                IntPtr c = _stageCbBuf[k].Contents;
                if (c != IntPtr.Zero)
                {
                    float* f = (float*)((byte*)c + _stageCbOffset[k]);
                    for (int i = 0; i < StageCbFloats; i++) { _stageCbCpu[j, k, i] = f[i]; }
                }
                blit.CopyFromBuffer(_stageCbBuf[k], (ulong)_stageCbOffset[k], _buf, (ulong)(StageCbBase + (j * StageCbMax + k) * StageCbFloats * 4), (ulong)(StageCbFloats * 4));
            }
            _stageCbCount = 0;
        }

        private static unsafe void ClassifyStageCb(long presentedFrame, bool flat)
        {
            long want = presentedFrame - 1;
            for (int j = 0; j < StageSlots; j++)
            {
                if (_stageFrame[j] != want || _stageFence[j] == null || !_stageFence[j].IsSignaled()) { continue; }
                int n = _stageCbCountAt[j];
                if (n == 0) { if (flat) { _cbUnknownFlat++; } else { _cbUnknownNormal++; } return; }
                bool anyDiff = false;
                StringBuilder diff = null;
                for (int k = 0; k < n; k++)
                {
                    float* g = (float*)((byte*)_buf.Contents + StageCbBase + (j * StageCbMax + k) * StageCbFloats * 4);
                    for (int i = 0; i < StageCbFloats; i++)
                    {
                        float cpu = _stageCbCpu[j, k, i];
                        if (BitConverter.SingleToInt32Bits(cpu) != BitConverter.SingleToInt32Bits(g[i]))
                        {
                            anyDiff = true;
                            diff ??= new StringBuilder();
                            if (diff.Length < 400) { diff.Append($" b{_stageCbBindingAt[j, k]}[{i}] cpu {cpu:G6} gpu {g[i]:G6};"); }
                        }
                        // per-field extrema of the CPU-side values, first 8 floats of each binding
                        if (i < 8 && _stageCbBindingAt[j, k] >= 1000)
                        {
                            // Storage buffer word, as the shader reads it: an integer.
                            string ikey = $"s{_stageCbBindingAt[j, k] - 1000}[{i}]int";
                            CbStat iv = _cbStats.TryGetValue(ikey, out CbStat ie) ? ie : new CbStat { FMin = double.MaxValue, FMax = double.MinValue, NMin = double.MaxValue, NMax = double.MinValue };
                            double id = BitConverter.SingleToInt32Bits(cpu);
                            if (flat) { iv.Flat++; iv.FMin = Math.Min(iv.FMin, id); iv.FMax = Math.Max(iv.FMax, id); }
                            else { iv.Normal++; iv.NMin = Math.Min(iv.NMin, id); iv.NMax = Math.Max(iv.NMax, id); }
                            _cbStats[ikey] = iv;
                            if (i == 0)
                            {
                                int gi = BitConverter.SingleToInt32Bits(g[i]);
                                string gkey = $"s{_stageCbBindingAt[j, k] - 1000}[0]GPU";
                                CbStat gv = _cbStats.TryGetValue(gkey, out CbStat ge) ? ge : new CbStat { FMin = double.MaxValue, FMax = double.MinValue, NMin = double.MaxValue, NMax = double.MinValue };
                                if (flat) { gv.Flat++; gv.FMin = Math.Min(gv.FMin, gi); gv.FMax = Math.Max(gv.FMax, gi); }
                                else { gv.Normal++; gv.NMin = Math.Min(gv.NMin, gi); gv.NMax = Math.Max(gv.NMax, gi); }
                                _cbStats[gkey] = gv;
                                // Separate quotas, so the white frames (a minority that arrives
                                // later) are not crowded out of the sample list by the normal ones.
                                if (flat ? _sbWhiteSamples < 40 : _sbNormalSamples < 40)
                                {
                                    if (flat) { _sbWhiteSamples++; } else { _sbNormalSamples++; }
                                    _sbSamples.Add($"f{presentedFrame} {(flat ? "WHITE " : "normal")} s{_stageCbBindingAt[j, k] - 1000}[0] cpu={BitConverter.SingleToInt32Bits(cpu)} gpu={gi}");
                                }
                            }
                        }
                        if (i < 8)
                        {
                            string key = $"b{_stageCbBindingAt[j, k]}[{i}]";
                            CbStat v = _cbStats.TryGetValue(key, out CbStat e) ? e : new CbStat { FMin = double.MaxValue, FMax = double.MinValue, NMin = double.MaxValue, NMax = double.MinValue };
                            double d = cpu;
                            if (flat) { v.Flat++; v.FMin = Math.Min(v.FMin, d); v.FMax = Math.Max(v.FMax, d); }
                            else { v.Normal++; v.NMin = Math.Min(v.NMin, d); v.NMax = Math.Max(v.NMax, d); }
                            _cbStats[key] = v;
                        }
                    }
                }
                if (anyDiff) { if (flat) { _cbDiffFlat++; } else { _cbDiffNormal++; } if (_cbDiffLogged < 6) { _cbDiffLogged++; Logger.Warning?.PrintMsg(LogClass.Gpu, $"uploadcorr: stage cb GPU!=CPU on {(flat ? "WHITE" : "normal")} frame {presentedFrame}:{diff}"); } }
                else { if (flat) { _cbEqFlat++; } else { _cbEqNormal++; } }
                return;
            }
            if (flat) { _cbUnknownFlat++; } else { _cbUnknownNormal++; }
        }
        private static string _stageInputsOnce;

        // The render-target configuration of the stage program's draw, per outcome.
        private static string _frameStageDraw = "-";
        private static readonly Dictionary<string, (long Flat, long Normal)> _stageDrawStats = new();
        private static string _frameStageBlend = "-";
        private static readonly Dictionary<string, (long Flat, long Normal)> _stageBlendStats = new();
        public static void NoteStageBlend(string desc)
        {
            if (Enabled && _stageArmedFrame == _frame) { _frameStageBlend = desc; }
        }

        public static void NoteStageDrawState(string desc, int fragmentOutputMap)
        {
            if (Enabled) { _frameStageDraw = $"{desc} fom0x{fragmentOutputMap:X}"; }
        }

        public static Texture LastStageInput { get; private set; }

        // Dedicated, self-contained probe of one program's fp_c3[0] (and fp_c3[1]) by
        // outcome. RYUJINX_METAL_CONST_LABEL selects the program (prefix). The white 1600x896
        // scene buffer is a smooth ~137 fill written by 1997e8, whose output is
        // clamp(sample+attr-1,0,1) * fp_c3[0].xyz - capped at fp_c3[0]; this reads whether
        // that cap itself is ~137 on white frames or the same as normal.
        private static readonly string _constLabel = Environment.GetEnvironmentVariable("RYUJINX_METAL_CONST_LABEL") ?? "";
        private const int ConstSlots = 5, ConstFloats = 8;   // slots 0..4, first 2 vec4 each
        private static readonly float[] _frameConst = new float[ConstSlots * ConstFloats];
        private static readonly bool[] _frameConstSlot = new bool[ConstSlots];
        private static bool _frameConstSeen;
        private static readonly Dictionary<int, (long Fn, double Fmin, double Fmax, double Fsum, long Nn, double Nmin, double Nmax, double Nsum)> _constStats = new();

        public static unsafe void NoteProgramConst(int slot, IntPtr contents, int offset)
        {
            if (!Enabled || _constLabel.Length == 0 || contents == IntPtr.Zero || (uint)slot >= ConstSlots) { return; }
            float* f = (float*)((byte*)contents + offset);
            for (int i = 0; i < ConstFloats; i++) { _frameConst[slot * ConstFloats + i] = f[i]; }
            _frameConstSlot[slot] = true;
            _frameConstSeen = true;
        }

        public static string ConstLabel => _constLabel;

        // Identity of the stage program's first input across frames. The 1x1 RGBA32Float
        // auto-exposure texture reads [1.0, 1.015, 0, 1] on every white frame and [0.13, 0, 0,
        // 1] normally - a reset default, not noise. Eye adaptation is a temporal feedback
        // (new = f(previous)), so if the texture object carrying the history is swapped or
        // re-created, the history is lost and the exposure jumps. This records whether the
        // pointer/serial changed since the previous frame, split by outcome.
        private static IntPtr _prevStageInputPtr;
        private static long _prevStageInputSerial;
        private static string _frameStageInputId = "-";
        private static readonly Dictionary<string, (long Flat, long Normal)> _stageInputIdStats = new();

        // The tiny (<=8x8) float input of the stage program - the auto-exposure value - read
        // back EVERY frame, so its trajectory can be seen: does it ramp up over frames (a
        // feedback loop converging on a bright target) or jump in one frame (a bad read)?
        // Also records the texture's identity per frame, since a ping-pong feedback pair
        // collapsed onto one host texture would break the loop outright.
        private const int TinyBase = 5 * Slots * Pixels * BytesPerPixel + 4 * Slots * Pixels * 16 + StageSlots * StageCbMax * StageCbFloats * 4 + TraceBytes;
        private const int TinyBytes = Slots * 16;
        private static Texture _tinyInput;
        private static readonly bool[] _tinySampled = new bool[Slots];
        private static readonly IntPtr[] _tinyPtr = new IntPtr[Slots];
        private static readonly List<string> _tinySeries = new();
        private static string _tinyLastKey;
        private static long _tinyLastFrame;
        private static int _tinyRepeat;

        private static readonly List<int> _stageInputBindings = new();

        public static void NoteStageInputBinding(int binding, Texture t)
        {
            if (Enabled && _stageCollect && t != null && !_stageInputs.Contains(t) && _stageInputs.Count < StageMaxInputs)
            {
                _stageInputBindings.Add(binding);
            }

            NoteStageInput(t);
        }

        public static void NoteStageInput(Texture t)
        {
            if (Enabled && _stageCollect && t != null && !_stageInputs.Contains(t) && _stageInputs.Count < StageMaxInputs)
            {
                _stageInputs.Add(t);
                if (t.Width <= 8 && t.Height <= 8 &&
                    (t.MtlFormat == MTLPixelFormat.RGBA32Float || t.MtlFormat == MTLPixelFormat.RGBA16Float))
                {
                    _tinyInput = t;
                }

                if (_stageInputs.Count == 1)
                {
                    LastStageInput = t;
                    bool ptrSame = t.CanonicalPtr == _prevStageInputPtr;
                    bool serialSame = t.Serial == _prevStageInputSerial;
                    _frameStageInputId = $"{t.Width}x{t.Height} {t.MtlFormat} ptr{(ptrSame ? "SAME" : "CHANGED")} serial{(serialSame ? "SAME" : "CHANGED")}";
                    _prevStageInputPtr = t.CanonicalPtr;
                    _prevStageInputSerial = t.Serial;
                }
            }
        }

        // Only the draw of the labelled program that targets the composite's input storage
        // (the same program label is reused by the game for many small targets), and only
        // its bindings are collected.
        // Additive blending accumulates: if this draw is issued more times on a white frame,
        // the buffer is brighter by exactly that factor. Counted per frame, tallied by
        // outcome - the multiplier is a continuum from 1.1x to 126x, which is what a varying
        // repeat count would look like.
        private static int _frameStageDrawCount;
        private static readonly Dictionary<int, (long Flat, long Normal)> _stageDrawCountStats = new();

        // Per frame: read-after-write splits actually taken, guest barriers requested, and
        // barriers dropped because no draw had been encoded in the pass yet. If a white frame
        // runs the post chain with fewer splits, its stages read each other's output inside
        // one render pass - undefined on a tiler, and exactly the profile of a continuum of
        // wrong multipliers.
        private static int _frameRawSplits, _frameBarrierReq, _frameBarrierSkipped;
        private static readonly Dictionary<int, (long Flat, long Normal)> _rawSplitStats = new();
        private static readonly Dictionary<int, (long Flat, long Normal)> _barrierSkipStats = new();

        public static void NoteRawSplit() { if (Enabled) { _frameRawSplits++; } }
        public static void NoteBarrierRequested() { if (Enabled) { _frameBarrierReq++; } }
        public static void NoteBarrierSkipped() { if (Enabled) { _frameBarrierSkipped++; } }

        public static void NoteStageDrawIssued()
        {
            if (Enabled) { _frameStageDrawCount++; }
        }

        public static void ArmStageDump(Texture[] renderTargets)
        {
            if (!Enabled || _stageLabel.Length == 0 || renderTargets == null)
            {
                return;
            }

            // The game's composite writes its output at whichever MRT slot; pick the slot
            // that is the composite's input storage (RT0 alone armed once per run, at boot).
            Texture output = null;
            foreach (Texture rt in renderTargets)
            {
                if (rt == null) { continue; }
                bool match = _stageRtW > 0
                    ? (rt.Width == _stageRtW && rt.Height == _stageRtH)
                    : (_stageTargetRoot == IntPtr.Zero || rt.CanonicalPtr == _stageTargetRoot);
                if (match) { output = rt; break; }
            }

            // Only the FIRST matching draw of the frame: this program is a generic modulated
            // blit the game issues 5-7 times per frame on this target, each with a different
            // source, and the pass trace says the brightness jump is at the first one - the
            // dumps were photographing the last.
            if (output != null && _stageArmedFrame != _frame)
            {
                // Trace exactly the texture this program writes, by identity. Matching by
                // size took the first matching attachment of each pass, which was a different
                // texture in another MRT slot - it never went bright, while this one did.
                _traceTexture = output;
                _stageArmedFrame = _frame;
                _stageOutput = output;
                _stageArmed = true;
                _stageCollect = true;
                _stageInputs.Clear();
                _stageInputBindings.Clear();
                _stageArms++;
            }
        }

        private static int BytesPerPixelOf(MTLPixelFormat f)
        {
            switch (f)
            {
                case MTLPixelFormat.RGBA16Float: case MTLPixelFormat.RGBA16Unorm: case MTLPixelFormat.RG32Float: return 8;
                case MTLPixelFormat.RGBA32Float: return 16;
                case MTLPixelFormat.R8Unorm: case MTLPixelFormat.A8Unorm: return 1;
                case MTLPixelFormat.R16Float: case MTLPixelFormat.RG8Unorm: case MTLPixelFormat.R16Unorm: return 2;
                default: return 4;
            }
        }

        public static void SampleStageAfterPass(CommandBufferScoped cbs)
        {
            if (!_stageArmed)
            {
                return;
            }
            _stageArmed = false;
            _stageCollect = false;

            // Always, regardless of the dump quota: 16 bytes of the exposure texture.
            {
                int ti = (int)(_frame % Slots);
                _tinySampled[ti] = false;
                if (_tinyInput != null && _buf.NativePtr != IntPtr.Zero)
                {
                    MTLTexture tt = _tinyInput.GetHandle(cbs);
                    if (tt.NativePtr != IntPtr.Zero)
                    {
                        MTLBlitCommandEncoder tb = cbs.Encoders.EnsureBlitEncoder();
                        tb.CopyFromTexture(tt, 0, 0, new MTLOrigin { x = 0, y = 0, z = 0 },
                            new MTLSize { width = 1, height = 1, depth = 1 },
                            _buf, (ulong)(TinyBase + ti * 16), 16, 16);
                        _tinySampled[ti] = true;
                        _tinyPtr[ti] = _tinyInput.CanonicalPtr;
                    }
                }
            }

            {
                // Per-frame snapshot that must not stop when the dump quota is reached.
                int jj = (int)(_frame % StageSlots);
                MTLBlitCommandEncoder bl = cbs.Encoders.EnsureBlitEncoder();
                BlitStageArgTables(jj, bl);
            }

            if (_stageOut[0].NativePtr == IntPtr.Zero || _stageDumpsWritten >= DumpMaxFiles)
            {
                _stageInputs.Clear();
                _stageOutput = null;
                return;
            }

            int j = (int)(_frame % StageSlots);
            MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();
            _stageOutBytes[j] = 0;

            if (_stageOutput != null)
            {
                int bpp = BytesPerPixelOf(_stageOutput.MtlFormat);
                int bytes = _stageOutput.Width * _stageOutput.Height * bpp;
                MTLTexture ot = _stageOutput.GetHandle(cbs);
                if (bytes <= DumpMaxBytes && ot.NativePtr != IntPtr.Zero)
                {
                    blit.CopyFromTexture(ot, 0, 0, new MTLOrigin { x = 0, y = 0, z = 0 },
                        new MTLSize { width = (ulong)_stageOutput.Width, height = (ulong)_stageOutput.Height, depth = 1 },
                        _stageOut[j], 0, (ulong)(_stageOutput.Width * bpp), (ulong)bytes);
                    _stageOutBytes[j] = bytes;
                    _stageOutPtr[j] = _stageOutput.CanonicalPtr;
                    _stageOutDesc[j] = $"{_stageOutput.Width}x{_stageOutput.Height}_fmt{(int)_stageOutput.MtlFormat}";
                }
            }

            for (int k = 0; k < StageMaxInputs; k++)
            {
                _stageInBytes[j, k] = 0;
                if (k >= _stageInputs.Count) { continue; }
                Texture t = _stageInputs[k];
                int bpp = BytesPerPixelOf(t.MtlFormat);
                int bytes = t.Width * t.Height * bpp;
                MTLTexture it = t.GetHandle(cbs);
                if (bytes <= DumpMaxBytes && it.NativePtr != IntPtr.Zero && t.Width > 0)
                {
                    blit.CopyFromTexture(it, 0, 0, new MTLOrigin { x = 0, y = 0, z = 0 },
                        new MTLSize { width = (ulong)t.Width, height = (ulong)t.Height, depth = 1 },
                        _stageIn[j, k], 0, (ulong)(t.Width * bpp), (ulong)bytes);
                    _stageInBytes[j, k] = bytes;
                    _stageInDesc[j, k] = $"b{(k < _stageInputBindings.Count ? _stageInputBindings[k] : -1)}_{t.Width}x{t.Height}_fmt{(int)t.MtlFormat}";
                }
            }

            if (_stageInputsOnce == null)
            {
                _stageInputsOnce = string.Join(" | ", System.Linq.Enumerable.Select(_stageInputs, t => $"{t.Width}x{t.Height} {t.MtlFormat} 0x{t.CanonicalPtr.ToInt64():X}"));
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"uploadcorr: stage {_stageLabel} output {_stageOutput?.Width}x{_stageOutput?.Height} {_stageOutput?.MtlFormat} inputs: {_stageInputsOnce}");
            }

            SnapshotStageCb(j, blit);

            _stageFence[j]?.Put();
            _stageFence[j] = cbs.GetFence();
            _stageFence[j].Get();
            _stageFrame[j] = _frame;
            _stageSamples++;
            _stageInputs.Clear();
            _stageOutput = null;
            _stageCollect = false;
        }

        private static unsafe void WriteStageDumps(long presentedFrame, bool flat)
        {
            // The stage ran in the period BEFORE the one whose present shows the result.
            long want = presentedFrame - 1;
            for (int j = 0; j < StageSlots; j++)
            {
                if (_stageFrame[j] != want || _stageFence[j] == null) { continue; }
                if (!_stageFence[j].IsSignaled()) { Logger.Warning?.PrintMsg(LogClass.Gpu, $"uploadcorr: stage dump for frame {want} not ready"); return; }
                string tag = flat ? "white" : "normal";
                try
                {
                    if (_stageOutBytes[j] > 0)
                    {
                        using var fo = new System.IO.FileStream($"/tmp/stage_{_stageLabel}_{tag}_f{presentedFrame}_out_{_stageOutDesc[j]}.raw", System.IO.FileMode.Create);
                        fo.Write(new ReadOnlySpan<byte>((void*)_stageOut[j].Contents, _stageOutBytes[j]));
                    }
                    for (int q = 0; q < StageSlots; q++)
                    {
                        if (_stagePreOutFrame[q] == want && _stagePreOutBytes[q] > 0 && _stagePreOutFence[q] != null && _stagePreOutFence[q].IsSignaled())
                        {
                            using var fb = new System.IO.FileStream($"/tmp/stage_{_stageLabel}_{tag}_f{presentedFrame}_beforeout_{_stagePreOutDesc[q]}.raw", System.IO.FileMode.Create);
                            fb.Write(new ReadOnlySpan<byte>((void*)_stagePreOut[q].Contents, _stagePreOutBytes[q]));
                        }
                    }
                    for (int q = 0; q < StageSlots; q++)
                    {
                        if (_stagePreFrame[q] == want && _stagePreBytes[q] > 0 && _stagePreFence[q] != null && _stagePreFence[q].IsSignaled())
                        {
                            using var fp = new System.IO.FileStream($"/tmp/stage_{_stageLabel}_{tag}_f{presentedFrame}_pre_{_stagePreDesc[q]}.raw", System.IO.FileMode.Create);
                            fp.Write(new ReadOnlySpan<byte>((void*)_stagePre[q].Contents, _stagePreBytes[q]));
                        }
                    }
                    for (int k = 0; k < StageMaxInputs; k++)
                    {
                        if (_stageInBytes[j, k] <= 0) { continue; }
                        using var fi = new System.IO.FileStream($"/tmp/stage_{_stageLabel}_{tag}_f{presentedFrame}_in{k}_{_stageInDesc[j, k]}.raw", System.IO.FileMode.Create);
                        fi.Write(new ReadOnlySpan<byte>((void*)_stageIn[j, k].Contents, _stageInBytes[j, k]));
                    }
                    if (flat) { _stageDumpsWritten++; } else { _stageDumpsNormal++; }
                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"uploadcorr: stage {_stageLabel} dumps written for {tag} frame {presentedFrame} (stage frame {want}) out=@{(_stageOutPtr[j].ToInt64() & 0xFFFFFF):X6} traceTex=@{((_traceTexture?.CanonicalPtr ?? IntPtr.Zero).ToInt64() & 0xFFFFFF):X6}");
                }
                catch (Exception e)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"uploadcorr: stage dump failed: {e.Message}");
                }
                return;
            }
            _stageMisses++;
            if (_stageMissLogged < 3)
            {
                _stageMissLogged++;
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"uploadcorr: stage lookup miss for presented frame {presentedFrame} (want {want}): ring frames [{string.Join(",", _stageFrame)}] arms {_stageArms} samples {_stageSamples}");
            }
        }

        // ---- pass-by-pass trace of the stage input ------------------------------------
        // At every pass end (from the first draw into the stage input onward), 5 texels of
        // the stage input are blitted into a ring together with the pass's description, so a
        // white frame prints the exact pass boundary at which that texture first reads white.
        // RYUJINX_METAL_PASS_TRACE=1 (needs RYUJINX_METAL_STAGE_LABEL so the input is known).
        private static readonly bool _passTrace = Environment.GetEnvironmentVariable("RYUJINX_METAL_PASS_TRACE") == "1";
        private const int TracePasses = 384, TraceTexels = 9;   // 3x3 spread; 268 passes/frame touch the buffer once all MRT slots are scanned
        // A dedicated 16-period ring, each entry stamped with its frame: the old code read
        // slot (index-1) of the 8-slot sample ring, which a later frame had already reused by
        // the time classification ran - that is why a normal frame's trace came back with 2
        // passes while a white one had 102. Now a trace is either the requested period or
        // explicitly absent.
        private const int TraceRing = 16;
        private static readonly long[] _traceFrame = new long[TraceRing];
        private const int TraceBase = 5 * Slots * Pixels * BytesPerPixel + 4 * Slots * Pixels * 16 + StageSlots * StageCbMax * StageCbFloats * 4;
        private const int TraceBytes = TraceRing * TracePasses * TraceTexels * 4;
        private static readonly string[,] _traceDesc = new string[TraceRing, TracePasses];
        private static readonly MTLPixelFormat[,] _traceFmt = new MTLPixelFormat[TraceRing, TracePasses];
        private static readonly int[] _traceCount = new int[TraceRing];
        private static int _traceLogged, _traceLoggedNormal;
        private static string _frameLastDrawLabel = "-";
        private static int _frameDrawsInPass;
        private static Texture _traceTexture;            // by RYUJINX_METAL_TRACE_RT=WxH, else the stage input
        private static readonly int _traceRtW = ParseDim(Environment.GetEnvironmentVariable("RYUJINX_METAL_TRACE_RT"), 0);
        private static readonly int _traceRtH = ParseDim(Environment.GetEnvironmentVariable("RYUJINX_METAL_TRACE_RT"), 1);
        public static void NoteTraceCandidate(Texture t)
        {
            if (_passTrace && _traceRtW > 0 && t != null && t.Width == _traceRtW && t.Height == _traceRtH && t.MtlFormat == MTLPixelFormat.RG11B10Float)
            {
                _traceTexture = t;
            }
        }
        private static bool _traceArmedThisFrame;

        public static void NotePassDrawLabel(string label)
        {
            _frameLastDrawLabel = label ?? "-";
            _frameDrawsInPass++;
            // Every draw of the pass, not only the last: "last=2b36a7 draws=5" named one of
            // five programs and the jump could belong to any of them.
            // Deduplicate runs (a pass can carry 500 draws of the same program) and cap high
            // enough that the interesting label is not truncated away.
            string lb = label ?? "-";
            if (_frameDrawLabels.Length < 300 && lb != _frameLastAppendedLabel)
            {
                _frameDrawLabels.Append(_frameDrawLabels.Length > 0 ? ">" : "").Append(lb);
                _frameLastAppendedLabel = lb;
            }
        }
        private static readonly StringBuilder _frameDrawLabels = new();
        private static string _frameLastAppendedLabel;
        public static string LastPassLastLabel => _frameLastDrawLabel;
        public static int LastPassDraws => _frameDrawsInPass;

        /// <summary>Called at every pass end with the pass's colour attachments.</summary>
        public static void TracePassEnd(CommandBufferScoped cbs, Texture[] rts, string passDesc)
        {
            // Any MRT slot, not only slot 0. The traced buffer sits in a non-zero slot for
            // part of the post chain, so every pass that wrote it that way was invisible.
            // Match by SIZE, not by identity: the traced dimensions are shared by more than
            // one texture and pinning to one object hid the other's writes entirely.
            Texture t = null;
            if (rts != null)
            {
                if (_traceTexture != null)
                {
                    foreach (Texture rt in rts)
                    {
                        if (rt != null && rt.CanonicalPtr == _traceTexture.CanonicalPtr) { t = rt; break; }
                    }

                    // Only that texture: a pass that does not carry it is not interesting and
                    // sampling some other attachment produced the "it never goes bright" lie.
                    if (t == null) { return; }
                }

                if (t == null)
                {
                    foreach (Texture rt in rts) { if (rt != null) { t = rt; break; } }
                }
            }

            TracePassEndInner(cbs, t, passDesc);
        }

        private static void TracePassEndInner(CommandBufferScoped cbs, Texture t, string passDesc)
        {
            if (!_passTrace || !Enabled || _buf.NativePtr == IntPtr.Zero)
            {
                return;
            }

            // Only the HDR colour pipeline: RG11B10Float / RGBA16Float, at least 16px.
            if (t == null || t.Width < 16 || t.Height < 16 ||
                (t.MtlFormat != MTLPixelFormat.RG11B10Float && t.MtlFormat != MTLPixelFormat.RGBA16Float))
            {
                return;
            }

            int idx = (int)(_frame % TraceRing);
            if (_traceFrame[idx] != _frame) { _traceFrame[idx] = _frame; _traceCount[idx] = 0; }

            int n = _traceCount[idx] % TracePasses;   // ring position; the count keeps growing

            MTLTexture mt = t.GetHandle(cbs);
            if (mt.NativePtr == IntPtr.Zero)
            {
                return;
            }

            MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();
            int w = t.Width, h = t.Height;
            // A GridSide x GridSide grid, same layout as the other probes. The old code
            // filled five entries while TraceTexels grew to 25, so twenty coordinates were
            // uninitialised stack.
            // A GridSide x GridSide grid only when there are GridSide^2 texels; with five the
            // grid formula put every point on the same row (i / GridSide == 0), so the trace
            // was reading a single horizontal band near the top and reported 1.03 for a
            // surface whose whole-surface mean was 50-180.
            Span<(int X, int Y)> pts = stackalloc (int X, int Y)[TraceTexels];
            if (TraceTexels == GridSide * GridSide)
            {
                for (int i = 0; i < TraceTexels; i++)
                {
                    pts[i] = (w * (i % GridSide + 1) / (GridSide + 1), h * (i / GridSide + 1) / (GridSide + 1));
                }
            }
            else
            {
                for (int i = 0; i < TraceTexels; i++)
                {
                    int side = (int)Math.Ceiling(Math.Sqrt(TraceTexels));
                    pts[i] = (w * (i % side + 1) / (side + 1), h * (i / side + 1) / (side + 1));
                }
            }
            for (int i = 0; i < TraceTexels; i++)
            {
                blit.CopyFromTexture(mt, 0, 0, new MTLOrigin { x = (ulong)pts[i].X, y = (ulong)pts[i].Y, z = 0 },
                    new MTLSize { width = 1, height = 1, depth = 1 },
                    _buf, (ulong)(TraceBase + ((idx * TracePasses + n) * TraceTexels + i) * 4), 4, 4);
            }
            // The storage pointer, low 6 hex digits: several textures share these dimensions
            // and the trace was following one of them while another went bright.
            _traceDesc[idx, n] = $"{w}x{h}@{(t.CanonicalPtr.ToInt64() & 0xFFFFFF):X6}:{(t.MtlFormat == MTLPixelFormat.RGBA16Float ? "16F" : "11B10")} {passDesc} draws={_frameDrawsInPass} [{_frameDrawLabels}]";
            _frameDrawLabels.Clear();
            _frameLastAppendedLabel = null;
            _traceFmt[idx, n] = t.MtlFormat;
            _traceCount[idx]++;
            _frameDrawsInPass = 0;
        }

        private static int TraceSlotFor(long frame)
        {
            int idx = (int)(frame % TraceRing);
            return _traceFrame[idx] == frame && _traceCount[idx] > 0 ? idx : -1;
        }

        private static unsafe string TraceReport(int index)
        {
            StringBuilder sb = new();
            int total = _traceCount[index];
            int n = Math.Min(total, TracePasses);
            int first = total - n;   // absolute index of the oldest entry still in the ring
            sb.Append($" (passes {first}..{total - 1} of {total})");
            for (int a = first; a < total; a++)
            {
                int k = a % TracePasses;
                uint* px = (uint*)((byte*)_buf.Contents + TraceBase + ((index * TracePasses + k) * TraceTexels) * 4);
                // RG11B10Float decode of the 5 texels -> mean luma
                double sum = 0;
                for (int i = 0; i < TraceTexels; i++)
                {
                    (float r, float g, float b) = SamplerPathProbe.DecodeRaw(px[i], _traceFmt[index, k]);
                    float lp = (r + 2 * g + b) * 0.25f;
                    sum += float.IsFinite(lp) ? lp : 1e9f;
                }
                sb.Append($"\n      pass {a,3}: luma {sum / TraceTexels:F3} {(sum / TraceTexels >= 0.9 ? "WHITE" : "     ")}  {_traceDesc[index, k]}");
            }
            return sb.ToString();
        }

        // ---- pre-composite probe -------------------------------------------------
        // The composite's fragment shader samples its input and gets white on ~20% of
        // frames, while compute reads of the same texels AFTER its pass always find a
        // picture. Two readings: the data was not visible yet when the fragment stage ran
        // (an ordering fault between the input's writer and the composite), or the fragment
        // path reads it wrong while the data is there. This reads the input with the compute
        // engine (texture.read and a sampler) right BEFORE the composite pass begins, at the
        // read-after-write split point, same command buffer - the one moment nobody looked.
        // Region P (compute read) and PS (sampler) sit after F in the ring buffer.
        private const int PreProbeReadBase = 5 * Slots * Pixels * BytesPerPixel + 2 * Slots * Pixels * 16;
        private const int PreProbeSampBase = 5 * Slots * Pixels * BytesPerPixel + 3 * Slots * Pixels * 16;
        private static bool _framePreProbed;
        private static long _preWhiteFlat, _prePicFlat, _preWhiteNormal, _prePicNormal, _preUnprobedFlat, _preUnprobedNormal;
        private static long _preSampWhiteFlat, _preSampPicFlat, _preSampWhiteNormal, _preSampPicNormal;
        private static double _preFlatMeanSum; private static long _preFlatMeanN;
        private static double _preNormalMeanSum; private static long _preNormalMeanN;
        private static long _preDistinctFlatSum, _preDistinctNormalSum;

        public static bool PreProbedThisPeriod { get; private set; }

        // The exposure texel (1x1 float) as it stands right BEFORE the first flare draw,
        // every frame. It is written earlier in the same pass window (889419, a render pass
        // into the 1x1); the flare vertex shader multiplies its intensity by tex(0.5,0.5).x*.y.
        // At the pass END it is identical on white and normal frames - this is the START.
        private const int TinyPreBase = ArgBase + ArgBytes;
        private const int TinyPreBytes = Slots * 16;
        private static readonly bool[] _tinyPreSampled = new bool[Slots];
        private static readonly Dictionary<string, CbStat> _tinyPreStats = new();
        private static readonly List<string> _tinyPreSamples = new();
        private static int _tinyPreWhite, _tinyPreNormal;

        public static void PreCompositeProbe(CommandBufferScoped cbs, Texture input)
        {
            if (!Enabled || input == null || _buf.NativePtr == IntPtr.Zero || PreProbedThisPeriod)
            {
                return;
            }
            // Once per period: the value right before the FIRST flare draw, and one split.
            PreProbedThisPeriod = true;

            if (input.Width <= 8 && input.Height <= 8 &&
                (input.MtlFormat == MTLPixelFormat.RGBA32Float || input.MtlFormat == MTLPixelFormat.RGBA16Float))
            {
                MTLTexture tt = input.GetHandle(cbs);
                int ti = (int)(_frame % Slots);
                _tinyPreSampled[ti] = false;
                if (tt.NativePtr != IntPtr.Zero)
                {
                    MTLBlitCommandEncoder tb = cbs.Encoders.EnsureBlitEncoder();
                    tb.CopyFromTexture(tt, 0, 0, new MTLOrigin { x = 0, y = 0, z = 0 },
                        new MTLSize { width = 1, height = 1, depth = 1 },
                        _buf, (ulong)(TinyPreBase + ti * 16), 16, 16);
                    _tinyPreSampled[ti] = true;
                }
                return;
            }

            if (!SamplerPathProbe.Ready)
            {
                return;
            }

            MTLTexture stex = input.GetHandle(cbs);
            if (stex.NativePtr == IntPtr.Zero || input.Width <= GridSide || input.Height <= GridSide)
            {
                return;
            }

            int idx = (int)(_frame % Slots);
            SamplerPathProbe.Read(cbs, stex, (ulong)input.Width, (ulong)input.Height, _buf, PreProbeReadBase + idx * Pixels * 16);
            SamplerPathProbe.ReadSampled(cbs, stex, _buf, PreProbeSampBase + idx * Pixels * 16);
            _framePreProbed = true;
            _preProbes++;

            // Whole surface of the input BEFORE the stage's pass, for the white frames.
            if (_stageLabel.Length != 0 && _stagePre[0].NativePtr != IntPtr.Zero && _stageDumpsWritten < DumpMaxFiles)
            {
                int j = (int)(_frame % StageSlots);
                int bpp = BytesPerPixelOf(input.MtlFormat);
                int bytes = input.Width * input.Height * bpp;
                if (bytes <= DumpMaxBytes)
                {
                    MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();
                    blit.CopyFromTexture(stex, 0, 0, new MTLOrigin { x = 0, y = 0, z = 0 },
                        new MTLSize { width = (ulong)input.Width, height = (ulong)input.Height, depth = 1 },
                        _stagePre[j], 0, (ulong)(input.Width * bpp), (ulong)bytes);
                    _stagePreBytes[j] = bytes;
                    _stagePreDesc[j] = $"{input.Width}x{input.Height}_fmt{(int)input.MtlFormat}";
                    _stagePreFrame[j] = _frame;
                    _stagePreFence[j]?.Put();
                    _stagePreFence[j] = cbs.GetFence();
                    _stagePreFence[j].Get();
                }
            }

            // Who drew into the input so far this frame (census since its last snapshot).
            {
                IntPtr root = input.CanonicalPtr;
                int count = _pendingWriterCount.TryGetValue(root, out int c) ? c : 0;
                _frameStageInputWriters = count > 0 ? string.Join(",", _pendingWriters[root], 0, Math.Min(count, MaxWriters)) : "-";
                _pendingWriterCount[root] = 0;
                _frameStageInputLastWriters = LastWritersOf(root);
            }
        }
        private static long _preProbes;

        private static bool _afterBlitArmed;
        private static bool _frameAfterOutSampled;
        private static IntPtr _frameAfterOutPtr;
        private static bool _pendingAfterOutKnown, _pendingAfterOutWhite;
        private static long _outWhiteAtDrawFlat, _outPicAtDrawFlat, _outWhiteAtDrawNormal, _outPicAtDrawNormal;

        // Same-storage, cross-frame pairing: (pointer -> was it white right after the
        // composite painted it), remembered for a few frames; consumed when present reads
        // that same pointer. This is the measurement the DECIDER should have been.
        private static readonly Dictionary<IntPtr, bool> _paintedWhiteByPtr = new();

        // Full-surface dumps. 25 points say "white, min 246 max 254 sd 2.2" - not a memory
        // fill (that would be 255/255/0) but a structured near-white. What that structure IS
        // (tile-shaped, a smooth field, an over-exposed picture with its edges intact) is the
        // one property of the white nobody has looked at, and it discriminates mechanisms
        // that no 25-point statistic can. Whole-texture blits of the presented half at
        // present (A) and of the half the composite just painted (F), per ring slot with
        // their own fences; the first few white frames are written to /tmp as raw RGBA
        // together with the SAME storage's F dump from the previous frame, keyed by pointer.
        // RYUJINX_METAL_DUMP_WHITE=1. Identity instrument, not for rate arms.
        private static readonly bool _dumpWhite = Environment.GetEnvironmentVariable("RYUJINX_METAL_DUMP_WHITE") == "1";
        private const int DumpMaxBytes = 1920 * 1080 * 4;
        private static readonly MTLBuffer[] _dumpA = new MTLBuffer[Slots];
        private static readonly MTLBuffer[] _dumpF = new MTLBuffer[Slots];
        // The composite's INPUT right after its pass, same encoder as F (raw texels; the
        // scene target is RG11B10Float, decoded offline by tools/white_dump.py).
        private static readonly MTLBuffer[] _dumpI = new MTLBuffer[Slots];
        private static readonly int[] _dumpIW = new int[Slots], _dumpIH = new int[Slots];
        private static readonly MTLPixelFormat[] _dumpIFmt = new MTLPixelFormat[Slots];
        private static readonly FenceHolder[] _dumpFFence = new FenceHolder[Slots];
        private static readonly IntPtr[] _dumpFPtr = new IntPtr[Slots];
        private static readonly int[] _dumpFW = new int[Slots], _dumpFH = new int[Slots];
        private static readonly int[] _dumpAW = new int[Slots], _dumpAH = new int[Slots];
        private static readonly bool[] _dumpAValid = new bool[Slots];
        private static int _dumpsWritten, _dumpNormalWritten, _dumpFNotReady, _dumpFUnpaired;
        private const int DumpMaxFiles = 4;
        private static long _samePicToWhiteFlat, _samePicToPicFlat, _sameWhiteToWhiteFlat, _sameWhiteToPicFlat;
        private static long _samePicToWhiteNormal, _samePicToPicNormal, _sameWhiteToWhiteNormal, _sameWhiteToPicNormal;
        private static long _sameUnpairedFlat, _sameUnpairedNormal;
        // Does present read the SAME half the composite writes in this frame (no double-buffer
        // alternation), and does that coincide with white?
        private static long _rwSameHalfFlat, _rwSameHalfNormal, _rwOtherHalfFlat, _rwOtherHalfNormal;

        public static void SampleInputAfterBlit(CommandBufferScoped cbs)
        {
            if (!Enabled || !_afterBlitArmed)
            {
                return;
            }

            _afterBlitArmed = false;

            Texture sceneTex = _frameSceneTex;

            if (sceneTex == null || _buf.NativePtr == IntPtr.Zero ||
                sceneTex.Width <= GridSide || sceneTex.Height <= GridSide)
            {
                return;
            }

            MTLTexture stex = sceneTex.GetHandle(cbs);

            if (stex.NativePtr == IntPtr.Zero)
            {
                return;
            }

            int idx = (int)(_frame % Slots);
            MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();

            // The OUTPUT of the composite, right after its pass ended, same command buffer.
            // If it is already white here, the composite painted it; if it is a picture here
            // and white at present, something between this point and present overwrote it.
            Texture outTex = _frameDrawnSrgbTex;
            _frameAfterOutSampled = false;
            if (outTex != null && outTex.Width > GridSide && outTex.Height > GridSide)
            {
                MTLTexture ot = outTex.GetHandle(cbs);
                if (ot.NativePtr != IntPtr.Zero)
                {
                    // BUG FIXED 2026-08-18: the destination offset lacked "+ i * BytesPerPixel",
                    // so all 25 copies landed on texel 0 of the region and texels 1..24 stayed
                    // zero forever. The classifier (>= SaturatedNeeded saturated of 25) could
                    // therefore never call this region white - which is why "the composite
                    // paints a picture" came out 100% on every arm since a4225946, and why the
                    // whole "in-place corruption between composite and present" line of
                    // investigation existed. Full-surface dumps (RYUJINX_METAL_DUMP_WHITE)
                    // showed the freshly painted half bit-identical to the white presented one.
                    for (int i = 0; i < Pixels; i++)
                    {
                        blit.CopyFromTexture(ot, 0, 0,
                            new MTLOrigin { x = (ulong)(outTex.Width * (i % GridSide + 1) / (GridSide + 1)), y = (ulong)(outTex.Height * (i / GridSide + 1) / (GridSide + 1)), z = 0 },
                            new MTLSize { width = 1, height = 1, depth = 1 },
                            _buf, (ulong)(4 * Slots * Pixels * BytesPerPixel + 2 * Slots * Pixels * 16 + (idx * Pixels + i) * BytesPerPixel), BytesPerPixel, BytesPerPixel);
                    }
                    _frameAfterOutSampled = true;
                    _frameAfterOutPtr = outTex.CanonicalPtr;

                    if (_dumpWhite && outTex.Width * outTex.Height * 4 <= DumpMaxBytes && _dumpsWritten < DumpMaxFiles)
                    {
                        // Whole surface, this slot; its own fence so the pairing at
                        // classification can tell a finished dump from one still in flight
                        // (the composite CB commits after the present CB on ~40% of frames).
                        blit.CopyFromTexture(ot, 0, 0,
                            new MTLOrigin { x = 0, y = 0, z = 0 },
                            new MTLSize { width = (ulong)outTex.Width, height = (ulong)outTex.Height, depth = 1 },
                            _dumpF[idx], 0, (ulong)(outTex.Width * 4), (ulong)(outTex.Width * outTex.Height * 4));
                        _dumpFFence[idx]?.Put();
                        _dumpFFence[idx] = cbs.GetFence();
                        _dumpFFence[idx].Get();
                        _dumpFPtr[idx] = outTex.CanonicalPtr;
                        _dumpFW[idx] = outTex.Width;
                        _dumpFH[idx] = outTex.Height;
                    }
                }
            }

            for (int i = 0; i < Pixels; i++)
            {
                blit.CopyFromTexture(
                    stex, 0, 0,
                    new MTLOrigin
                    {
                        // Same texels as the present-time sample, so the two snapshots
                        // compare texel for texel.
                        x = (ulong)(sceneTex.Width / 2 + i % GridSide),
                        y = (ulong)(sceneTex.Height / 2 + i / GridSide),
                        z = 0,
                    },
                    new MTLSize { width = 1, height = 1, depth = 1 },
                    _buf, (ulong)(((2 * Slots + idx) * Pixels + i) * BytesPerPixel), BytesPerPixel, BytesPerPixel);
            }

            _frameAfterSampled = true;

            if (_dumpWhite && sceneTex.Width * sceneTex.Height * 4 <= DumpMaxBytes && _dumpsWritten < DumpMaxFiles &&
                (sceneTex.MtlFormat == MTLPixelFormat.RG11B10Float || sceneTex.MtlFormat == MTLPixelFormat.RGBA8Unorm ||
                 sceneTex.MtlFormat == MTLPixelFormat.RGBA8UnormsRGB || sceneTex.MtlFormat == MTLPixelFormat.BGRA8Unorm ||
                 sceneTex.MtlFormat == MTLPixelFormat.RGBA16Float))
            {
                int bpp = sceneTex.MtlFormat == MTLPixelFormat.RGBA16Float ? 8 : 4;
                if (sceneTex.Width * sceneTex.Height * bpp <= DumpMaxBytes)
                {
                    blit.CopyFromTexture(stex, 0, 0,
                        new MTLOrigin { x = 0, y = 0, z = 0 },
                        new MTLSize { width = (ulong)sceneTex.Width, height = (ulong)sceneTex.Height, depth = 1 },
                        _dumpI[idx], 0, (ulong)(sceneTex.Width * bpp), (ulong)(sceneTex.Width * sceneTex.Height * bpp));
                    _dumpIW[idx] = sceneTex.Width;
                    _dumpIH[idx] = sceneTex.Height;
                    _dumpIFmt[idx] = sceneTex.MtlFormat;
                }
            }

            // The same texels again, through the sampler hardware. Back to back with the
            // blit-engine copies above, same stream position: the two decoders agree unless
            // the compression metadata is wrong.
            if (SamplerPathProbe.Ready)
            {
                SamplerPathProbe.Read(cbs, stex, (ulong)sceneTex.Width, (ulong)sceneTex.Height,
                    _buf, 3 * Slots * Pixels * BytesPerPixel + idx * Pixels * 16);
                _frameSamplerSampled = true;
                _frameInputFmt = sceneTex.MtlFormat;

                SamplerPathProbe.ReadSampled(cbs, stex, _buf,
                    3 * Slots * Pixels * BytesPerPixel + (Slots + idx) * Pixels * 16);
            }
        }

        private static bool _frameAfterSampled;
        private static bool _frameSamplerSampled;
        private static MTLPixelFormat _frameInputFmt;
        private static long _sampVsReadFlat, _sampSameFlat, _sampVsReadNormal, _sampSameNormal;
        private static long _twoPathDisagreeFlat, _twoPathAgreeFlat, _twoPathDisagreeNormal, _twoPathAgreeNormal;
        private static long _witFlatWhiteAfter, _witFlatPictureAfter, _witNormalWhiteAfter, _witNormalPictureAfter;
        private static double _witFlatEqSum; private static long _witFlatEqN;

        private static long _inputGen = -1, _writerGen = -1;
        private static long _genSwapFlat, _genSwapNormal, _genSameFlat, _genSameNormal;

        private static long _inputSerial = -1, _writerSerial = -1;
        private static long _matchFlat, _matchNormal, _mismatchFlat, _mismatchNormal;
        private static readonly Dictionary<string, (long Flat, long Normal)> _imgStats = new();
        private static string _frameRaster;

        /// <summary>
        /// The blit's position.w is a full projection row: dot(attr.xyz, c3[5].xyz) +
        /// c3[5].w, from guest data whose observed range spans thousands. Both backends run
        /// the same arithmetic, so this computes w on the CPU exactly as the vertex shader
        /// does, for all four vertices, against row 5 of every uniform slot bound at that
        /// draw - the real c3 identifies itself by sitting near 1 on normal frames, and the
        /// question is whether it collapses toward zero on exactly the flat ones. A w near
        /// zero at a vertex makes perspective interpolation of the UV explode across the
        /// primitive while every input value stays "correct".
        /// </summary>
        private static readonly float[] _frameVerts = new float[12];
        private static readonly float[] _frameRowW = new float[MaxCbSlots];
        private static readonly bool[] _frameRowSeen = new bool[MaxCbSlots];
        private static readonly double[] _wFlatSum = new double[MaxCbSlots];
        private static readonly double[] _wNormalSum = new double[MaxCbSlots];
        private static readonly float[] _wFlatMin = new float[MaxCbSlots];
        private static readonly float[] _wNormalMin = new float[MaxCbSlots];
        private static readonly long[] _wFlatN = new long[MaxCbSlots];
        private static readonly long[] _wNormalN = new long[MaxCbSlots];
        private static bool _wInit;

        public static unsafe void NoteBlitRow(int slot, IntPtr contents, int offset)
        {
            if (!Enabled || contents == IntPtr.Zero || (uint)slot >= MaxCbSlots)
            {
                return;
            }

            float* r = (float*)((byte*)contents + offset + 80);   // c3->data[5]
            float wmin = float.MaxValue;

            for (int i = 0; i < 4; i++)
            {
                float w = _frameVerts[i * 3] * r[0] + _frameVerts[i * 3 + 1] * r[1] +
                          _frameVerts[i * 3 + 2] * r[2] + r[3];

                if (Math.Abs(w) < Math.Abs(wmin))
                {
                    wmin = w;
                }
            }

            _frameRowW[slot] = wmin;
            _frameRowSeen[slot] = true;
        }
        private static readonly Dictionary<string, (long Flat, long Normal)> _rasterStats = new();
        private static string _frameAttrib;
        private static readonly Dictionary<string, (long Flat, long Normal)> _attribStats = new();
        private static string _frameIndices;
        private static readonly Dictionary<string, (long Flat, long Normal)> _indexStats = new();
        private static int _frameVertexStride = -1;
        private static double _flatStrideSum, _normalStrideSum;
        private static long _flatStrideN, _normalStrideN;
        private static int _flatStrideMin = int.MaxValue, _flatStrideMax = int.MinValue;
        private static int _frameVertexDistinct = -1;
        private static double _flatVertSum, _normalVertSum;
        private static long _flatVertN, _normalVertN;

        public static unsafe void NoteCompositeConstants(int slot, IntPtr contents, int offset)
        {
            if (!Enabled || contents == IntPtr.Zero || (uint)slot >= MaxCbSlots)
            {
                return;
            }

            float* f = (float*)((byte*)contents + offset);

            for (int i = 0; i < 4; i++)
            {
                _frameCb[slot * 4 + i] = f[i];
            }

            _frameCbSeen[slot] = true;

            // Slot 0 is the support buffer, and its first four floats are the alpha-test
            // field, which is why reading them showed nothing but zeros. What matters is
            // render_scale: TexelFetchScale multiplies the fetch coordinate by
            // render_scale[texture + 1].x, returning the coordinate untouched only when
            // that is exactly 1.0. A wrong scale sends every tap somewhere the frame never
            // wrote - inside the texture, since the scene target is allocated at 1600x896
            // and rendered at 800x448, which is why clamping to texture bounds and
            // injecting content both changed nothing. Parked in the last slot so the
            // reporting path needs no new plumbing.
            if (slot == 0)
            {
                float* rs = (float*)((byte*)contents + offset + SupportBuffer.GraphicsRenderScaleOffset);

                _frameCb[(MaxCbSlots - 1) * 4 + 0] = rs[0];                  // render_scale[0].x
                _frameCb[(MaxCbSlots - 1) * 4 + 1] = rs[SupportBuffer.FieldSize / sizeof(float)];
                _frameCbSeen[MaxCbSlots - 1] = true;
            }
        }

        // The programs that sampled a scene-class texture this frame, in order. The
        // count alone already steps the rate hard - zero flat in 803 frames at eleven
        // or fewer, 37.6% at thirteen - so what those draws actually are is the next
        // question, and diffing the eleven-draw signature against the thirteen-draw one
        // names the stages whose presence coincides with the fault.
        private const int MaxFrameProgs = 40;
        private const int MaxSignatures = 200;
        private static readonly string[] _frameProgs = new string[MaxFrameProgs];
        private static int _frameProgCount;
        private static readonly Dictionary<string, (long Flat, long Normal)> _signatureStats = new();
        private static readonly Dictionary<string, (long Flat, long Normal)> _bindingStats = new();
        private static long _framesWithNoBinding;

        // Classification happens only when the sampler's own fence reports signalled, and a
        // fenced arm freezes at a fixed frame count with nothing dropped - which points here
        // rather than at the ring. Counted so the next fenced arm says whether the fence
        // stopped signalling or the slots stopped being valid.
        private static long _fenceAsked, _fenceReady;

        // Mean luma of the sampled grid, split by outcome. Without it a run whose
        // picture went black reports zero flat frames and reads as a fix - which is
        // exactly how a "this build might suppress it" result was once produced.
        private static long _magentaFrames;

        private static double _lumaFlatSum, _lumaNormalSum;

        // Shape of the sampled grid on flat frames: the per-frame darkest and brightest
        // sample, the spread, and how many of the grid actually saturated. Kept separately
        // for normal frames so the two can be read against each other.
        private static double _flatMinSum, _flatMaxSum, _flatSdSum, _flatSatSum;
        private static double _normalMinSum, _normalMaxSum, _normalSdSum;

        // Distinct raw 32-bit values among the composite's 25 input samples. One means
        // the input it averages over is a single colour, which is exactly the condition
        // that drives the weight sum to zero.
        private static double _flatInputDistinctSum, _normalInputDistinctSum;
        private static int _pendingInputDistinct;
        private static bool _pendingInputKnown;
        private static long _flatInputFrames, _normalInputFrames;

        // What a viewer actually counts is flashes, not flat frames. Consecutive flat
        // frames are one flash; a change that halves the flat *frames* while leaving the
        // number of runs alone looks like no change at all, which is what was reported
        // after the read-after-write split took 45% to 24%. Runs, run length and the gap
        // between them are the perceptual quantities, so measure those too.
        private static bool _prevFlat;
        private static long _runs;
        private static long _runFrames;
        private static long _longestRun;
        private static long _currentRun;
        private static long _gapSum;
        private static long _currentGap;
        private static long _gaps;

        // Two questions the captures could not settle by eye, asked as numbers instead.
        //
        // The HUD renders correctly over the white on a flat frame, and the hardware
        // coverage counter says the composite covered all 2,073,600 pixels, so the
        // presented surface *is* written during the frame - which sits badly with three
        // GPU captures in which no encoder's attachment looked white. Judging a
        // RG11B10Float attachment by a thumbnail is not evidence; these are.
        //
        //   identity  - is a *different* storage selected for presentation on flat
        //               frames? That is the "selected already white" reading, and it is
        //               one comparison.
        //   age       - how many frames since that storage was last a colour attachment.
        //               Zero means the frame drew into it; a large value on flat frames
        //               only would mean it was presented stale.
        private static readonly Dictionary<IntPtr, long> _lastAttachmentFrame = new();
        private static readonly Dictionary<string, (long Flat, long Normal)> _presentIdentity = new();
        private static readonly Dictionary<long, (long Flat, long Normal)> _presentAge = new();
        private static IntPtr _framePresentRoot;
        private static long _framePresentAge = -1;

        // Which programs drew into the storage that will be presented, recorded for the
        // frame that writes it rather than the frame that shows it. The age table
        // measured 1461 flat and 3911 normal frames at age 1 against 1 and 26 at age 0:
        // the surface shown at frame N was last an attachment at N-1, so the content -
        // and the white - is written a frame before it appears. Every instrument aimed
        // at the frame that displays white, this one included until now, was looking a
        // frame late. A GPU capture of the writing frame showed the presented surface
        // taking only a draw-based clear inside the window, and nothing else; whether
        // that is the whole story on flat frames is what this answers, over thousands of
        // frames instead of one.
        private const int MaxWriters = 24;
        private static readonly string[] _frameWriters = new string[MaxWriters];
        private static int _frameWriterCount;
        private static readonly Dictionary<string, (long Flat, long Normal)> _writerStats = new();
        private static readonly Dictionary<IntPtr, string[]> _pendingWriters = new();
        private static readonly Dictionary<IntPtr, int> _pendingWriterCount = new();

        private static readonly ulong[] _frameShapes = new ulong[8];
        private static int _frameShapeCount;
        private static int _frameUploads;
        private static int _frameBigUploads;
        private static int _frameBigUploadsOntoRt;
        private static int _frameBigCopies;
        private static int _frameBigCopiesOntoRt;

        private static long _frame;
        private static long _dropped;

        // The join. "With" means at least one big upload/copy that frame.
        private static long _flatFrames;
        private static long _normalFrames;
        private static long _flatWithUpload;
        private static long _normalWithUpload;
        private static long _flatWithUploadOntoRt;
        private static long _normalWithUploadOntoRt;
        private static long _flatWithCopy;
        private static long _normalWithCopy;
        private static long _flatWithCopyOntoRt;
        private static long _normalWithCopyOntoRt;

        // Shape key: width<<40 | height<<16 | (kind: 1 upload, 2 copy)<<8 | ontoRt.
        private static readonly Dictionary<ulong, (long Flat, long Normal)> _shapeStats = new();

        private const int ReportInterval = 600;

        private static CommandBufferPool _pool;

        public static void AttachPool(CommandBufferPool pool)
        {
            _pool = pool;
        }

        public static void Init(MTLDevice device)
        {
            if (!Enabled)
            {
                return;
            }

            // + 2 float4 regions for the pre-composite probe (compute read / sampler read).
            _buf = device.NewBuffer((ulong)(5 * Slots * Pixels * BytesPerPixel + 4 * Slots * Pixels * 16 + StageSlots * StageCbMax * StageCbFloats * 4 + TraceBytes + TinyBytes + SceneBytes + ArgBytes + TinyPreBytes), MTLResourceOptions.ResourceStorageModeShared);
            if (VsDumpEnabled)
            {
                VsDumpBuffer = device.NewBuffer((ulong)(16 + VsdRecords * 80 + FsdRecords * 64), MTLResourceOptions.ResourceStorageModeShared);
                unsafe { *(uint*)VsDumpBuffer.Contents = 0; *((uint*)VsDumpBuffer.Contents + 1) = 0; }
            }
            SamplerPathProbe.Initialize(device);

            if (_stageLabel.Length != 0)
            {
                for (int i = 0; i < StageSlots; i++)
                {
                    _stageOut[i] = device.NewBuffer((ulong)DumpMaxBytes, MTLResourceOptions.ResourceStorageModeShared);
                    _stagePre[i] = device.NewBuffer((ulong)DumpMaxBytes, MTLResourceOptions.ResourceStorageModeShared);
                    _stagePreOut[i] = device.NewBuffer((ulong)DumpMaxBytes, MTLResourceOptions.ResourceStorageModeShared);
                    for (int k = 0; k < StageMaxInputs; k++)
                    {
                        _stageIn[i, k] = device.NewBuffer((ulong)DumpMaxBytes, MTLResourceOptions.ResourceStorageModeShared);
                    }
                }
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"uploadcorr: stage dumps armed for program {_stageLabel}");
            }

            if (_dumpWhite)
            {
                for (int i = 0; i < Slots; i++)
                {
                    _dumpA[i] = device.NewBuffer((ulong)DumpMaxBytes, MTLResourceOptions.ResourceStorageModeShared);
                    _dumpF[i] = device.NewBuffer((ulong)DumpMaxBytes, MTLResourceOptions.ResourceStorageModeShared);
                    _dumpI[i] = device.NewBuffer((ulong)DumpMaxBytes, MTLResourceOptions.ResourceStorageModeShared);
                }

                Logger.Warning?.PrintMsg(LogClass.Gpu, $"uploadcorr: full-surface white dumps armed ({Slots}x2x{DumpMaxBytes / 1048576} MB), first {DumpMaxFiles} white frames -> /tmp/white_*.rgba");
            }

            Logger.Warning?.PrintMsg(LogClass.Gpu,
                $"uploadcorr armed: ring={Slots} grid={GridSide}x{GridSide} satLuma={SaturatedLuma} need={SaturatedNeeded} minPixels={MinPixels}");
        }

        /// <summary>
        /// The resource id written into the composite's argument buffer for the scene
        /// texture. Split by outcome this answers the only question left: a different id
        /// on flat frames means the shader was pointed at something else and the fault is
        /// ours; identical ids mean the driver returned white for a correctly bound,
        /// correctly filled texture.
        /// </summary>
        /// <summary>
        /// The storage actually handed to the window this frame, and how long since
        /// anything rendered into it. Called at present, before the samples are encoded.
        /// </summary>
        public static void NotePresented(Texture src)
        {
            if (!Enabled || src == null)
            {
                return;
            }

            _framePresentRoot = src.CanonicalPtr;
            _framePresentAge = _lastAttachmentFrame.TryGetValue(src.CanonicalPtr, out long last)
                ? _frame - last
                : -1;

            // The writers recorded against this storage since it was last presented -
            // i.e. what drew into it during the frame that produced what is about to be
            // shown.
            _frameWriterCount = _pendingWriterCount.TryGetValue(src.CanonicalPtr, out int wc) ? wc : 0;

            if (_frameWriterCount > 0)
            {
                Array.Copy(_pendingWriters[src.CanonicalPtr], _frameWriters, _frameWriterCount);
            }

            _pendingWriterCount[src.CanonicalPtr] = 0;
        }

        public static void NoteSceneBinding(ulong gpuAddress, IntPtr nativePtr, IntPtr canonicalPtr, string program, Texture storage = null)
        {
            if (!Enabled)
            {
                return;
            }

            // First of the frame, not last. The consumer whose fetch comes back white
            // is the second pass of the frame; every later draw that also samples a
            // scene-class texture used to overwrite this record, so the "identical
            // binding on flat and normal frames" reading was taken from whichever draw
            // happened to be last. Keep both, and report them separately.
            if (_frameBinding.Count == 0)
            {
                _frameBinding.GpuAddress = gpuAddress;
                _frameBinding.NativePtr = nativePtr;
                _frameBinding.CanonicalPtr = canonicalPtr;
                _frameBinding.Program = program;
            }

            _frameBindingLast.GpuAddress = gpuAddress;
            _frameBindingLast.NativePtr = nativePtr;
            _frameBindingLast.CanonicalPtr = canonicalPtr;
            _frameBindingLast.Program = program;
            _frameSceneTex = storage ?? _frameSceneTex;

            if (storage != null)
            {
                _frameCompositeDraws++;
                _compositeInputRoot = storage.CanonicalPtr;
                _compositeSeq = _frameSeq;
            }

            if (_frameProgCount < MaxFrameProgs)
            {
                string label = program ?? "?";
                _frameProgs[_frameProgCount++] = label.Length > 6 ? label[..6] : label;
            }

            _frameBinding.Count++;
        }

        public static void NoteAttachment(Texture target, bool clearLoad = false)
        {
            if (!Enabled || target == null)
            {
                return;
            }

            _attachedThisFrame.Add(target.CanonicalPtr);
            _lastAttachmentFrame[target.CanonicalPtr] = _frame;
            NoteTraceCandidate(target);

            // How the game's final RT gets its content: log the pass that binds it - was
            // it cleared, how many draws followed (filled in at pass end), which program.
            if (target.CanonicalPtr == _frameGameRtRoot || target.CanonicalPtr == _prevGameRtRoot)
            {
                _gameRtPassOpen = true;
                _gameRtPassDrawStart = _drawsSeen;
                _gameRtPassClear = clearLoad;
                if (++_gameRtPassLogs % 300 == 1)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"game RT pass BEGIN frame={_frame} clearLoad={clearLoad}");
                }
            }

            if (target.CanonicalPtr == _compositeInputRoot)
            {
                _writerSerial = target.Serial;
                _writerGen = GenOf(target.CanonicalPtr);
            }

            // Also a writer entry. NoteAttachmentDraw only fires per draw, so a pass that
            // binds this surface and clears it without drawing leaves no trace - and the
            // presented storage never appears as a draw target at all, while its age says
            // it was an attachment one frame ago. A clear writes a uniform fill by
            // definition, which is the shape of this fault.
            NoteAttachmentDraw(target.CanonicalPtr, "attach");
        }

        /// <summary>
        /// A draw landing on a colour attachment, with the program that issued it.
        /// Accumulated per storage so the frame that writes the presented surface can be
        /// described by what actually drew into it.
        /// </summary>
        /// <summary>
        /// The pair that matters. Every out-of-pass witness runs after the writer of the
        /// blit's input has completed; only the blit's own fragment stage sees white. The
        /// global out-of-order counter (0.54/frame, uncorrelated) cannot resolve whether
        /// THIS writer's command buffer and THIS blit's command buffer commit in the order
        /// they were rented - so both identities are recorded here, per frame, and the
        /// pair's rent-vs-commit order is split by outcome at classification.
        /// </summary>
        private static int _frameWriterCb = -1;
        private static long _frameWriterRent = -1;
        private static int _frameBlitCb = -1;
        private static long _frameBlitRent = -1;
        private static IntPtr _frameBlitInputRoot;

        // Writers happen BEFORE the blit in the frame, so the writer cannot be matched
        // against the blit's input at the time it draws - the first version keyed on a
        // root that was not yet known and paired 19 frames of 11,399. Record every
        // attachment root's most recent writer cb instead, and look it up at the blit.
        private static readonly Dictionary<IntPtr, (int Cb, long Rent)> _lastWriterByRoot = new();

        public static void NoteWriterCb(IntPtr root, int cbIndex, long rentSeq)
        {
            if (Enabled && root != IntPtr.Zero)
            {
                _lastWriterByRoot[root] = (cbIndex, rentSeq);
            }
        }

        public static void NoteBlitCb(IntPtr inputRoot, int cbIndex, long rentSeq)
        {
            if (!Enabled)
            {
                return;
            }

            _frameBlitInputRoot = inputRoot;
            _frameBlitCb = cbIndex;
            _frameBlitRent = rentSeq;
            _lastCompositeCb = cbIndex;
            _lastCompositeRent = rentSeq;
            if (_pool != null)
            {
                FenceHolder f = _pool.GetFence(cbIndex);
                if (f != null) { f.Get(); _lastCompositeFence?.Put(); _lastCompositeFence = f; }
            }

            // The capture's shape: Render Encoder 0 (HUD, attachment X) then the RCAS blit
            // reading Y, and X != Y on the white frame. _lastAttachmentRoot is X here.
            _frameBlitReadsPrevAttachment = inputRoot == _lastAttachmentRoot;
            _frameBlitPairKnown = _lastAttachmentRoot != IntPtr.Zero;

            if (_lastWriterByRoot.TryGetValue(inputRoot, out (int Cb, long Rent) w))
            {
                _frameWriterCb = w.Cb;
                _frameWriterRent = w.Rent;
            }
        }

        // The REAL pair: composite in frame N (its cb rent/commit seq) against present in
        // frame N+1 (the cb OnPresent runs in). Held one frame.
        private static int _lastCompositeCb = -1; private static long _lastCompositeRent = -1;
        private static FenceHolder _lastCompositeFence;
        private static readonly bool _waitComposite = Environment.GetEnvironmentVariable("RYUJINX_METAL_PRESENT_WAIT_COMPOSITE") == "1";
        private static long _waitCompositeCount;

        /// <summary>
        /// Called at present, before its encoding: block until the previous frame's
        /// composite command buffer has COMPLETED on the GPU. If white frames vanish, the
        /// composite's store had not landed when present sampled - a cross-command-buffer
        /// visibility gap that commit order alone does not close.
        /// </summary>
        public static void WaitForLastComposite()
        {
            if (!_waitComposite || _lastCompositeFence == null) { return; }
            _lastCompositeFence.Wait();
            if (++_waitCompositeCount % 600 == 1)
            {
                Logger.Info?.PrintMsg(LogClass.Gpu, $"present waited for composite cb fence: {_waitCompositeCount}");
            }
        }
        private static int _prevCompositeCb = -1; private static long _prevCompositeRent = -1;
        private static long _cpSameFlat, _cpSameNormal, _cpOrderedFlat, _cpOrderedNormal, _cpInvertedFlat, _cpInvertedNormal, _cpUnkFlat, _cpUnkNormal;

        private static long _pairSameCbFlat, _pairSameCbNormal;
        private static long _pairOrderedFlat, _pairOrderedNormal;
        private static long _pairInvertedFlat, _pairInvertedNormal;
        private static long _pairUncommittedFlat, _pairUncommittedNormal;

        private static IntPtr _lastAttachmentRoot;
        private static bool _frameBlitReadsPrevAttachment, _frameBlitPairKnown;
        private static long _blitPrevMatchFlat, _blitPrevMatchNormal, _blitPrevMismatchFlat, _blitPrevMismatchNormal;

        // The LAST few writers of each storage (the census above keeps the FIRST 24 distinct
        // labels; the scene texture has more writers than that and its last one - the
        // tonemap/post pass - is the one whose output the upscaler reads).
        private static readonly Dictionary<IntPtr, string[]> _lastWriters = new();
        private static readonly Dictionary<IntPtr, int> _lastWriterPos = new();
        private const int LastWritersN = 6;

        private static void NoteLastWriter(IntPtr root, string program)
        {
            string label = program ?? "?";
            label = label.Length > 6 ? label[..6] : label;
            if (!_lastWriters.TryGetValue(root, out string[] ring)) { ring = new string[LastWritersN]; _lastWriters[root] = ring; _lastWriterPos[root] = 0; }
            int pos = _lastWriterPos[root];
            // collapse consecutive draws of the same program
            int prev = (pos + LastWritersN - 1) % LastWritersN;
            if (ring[prev] == label) { return; }
            ring[pos] = label;
            _lastWriterPos[root] = (pos + 1) % LastWritersN;
        }

        public static string LastWritersOf(IntPtr root)
        {
            if (!_lastWriters.TryGetValue(root, out string[] ring)) { return "-"; }
            int pos = _lastWriterPos[root];
            StringBuilder sb = new();
            for (int i = 0; i < LastWritersN; i++)
            {
                string l = ring[(pos + i) % LastWritersN];
                if (l != null) { if (sb.Length > 0) { sb.Append(','); } sb.Append(l); }
            }
            return sb.Length > 0 ? sb.ToString() : "-";
        }

        public static void NoteAttachmentDraw(IntPtr root, string program)
        {
            _drawsSeen++;
            _lastAttachmentRoot = root;
            NoteWriteOrdering(root, program);
            if (Enabled && root != IntPtr.Zero) { NoteLastWriter(root, program); }

            if (!Enabled || root == IntPtr.Zero)
            {
                return;
            }

            if (!_pendingWriters.TryGetValue(root, out string[] writers))
            {
                writers = new string[MaxWriters];
                _pendingWriters[root] = writers;
                _pendingWriterCount[root] = 0;
            }

            int count = _pendingWriterCount[root];
            string label = program ?? "?";
            label = label.Length > 6 ? label[..6] : label;

            for (int i = 0; i < count; i++)
            {
                if (writers[i] == label)
                {
                    return;
                }
            }

            if (count < MaxWriters)
            {
                writers[count] = label;
                _pendingWriterCount[root] = count + 1;
            }
        }

        public static void NoteUpload(Texture target)
        {
            // Copies and uploads are writers too; without them the present source's
            // writer list held only draws, and the capture shows the frame's picture and
            // the presented white living in two different textures.
            if (Enabled && target != null) { NoteAttachmentDraw(target.CanonicalPtr, "upload"); }
            NoteWriteOrdering(target.CanonicalPtr, "upload");

            Note(target, isCopy: false);
        }

        public static void NoteCopyIn(Texture target)
        {
            // Copies and uploads are writers too; without them the present source's
            // writer list held only draws, and the capture shows the frame's picture and
            // the presented white living in two different textures.
            if (Enabled && target != null) { NoteAttachmentDraw(target.CanonicalPtr, "copy"); }
            NoteWriteOrdering(target.CanonicalPtr, "copy");

            Note(target, isCopy: true);
        }

        private static void Note(Texture target, bool isCopy)
        {
            if (!Enabled || target == null)
            {
                return;
            }

            if (!isCopy)
            {
                _frameUploads++;
            }

            long pixels = (long)target.Info.Width * target.Info.Height;

            if (pixels < MinPixels)
            {
                return;
            }

            bool ontoRt = _attachedThisFrame.Contains(target.CanonicalPtr);

            if (isCopy)
            {
                _frameBigCopies++;

                if (ontoRt)
                {
                    _frameBigCopiesOntoRt++;
                }
            }
            else
            {
                _frameBigUploads++;

                if (ontoRt)
                {
                    _frameBigUploadsOntoRt++;
                }
            }

            ulong shape = ((ulong)(uint)target.Info.Width << 40)
                | ((ulong)(uint)target.Info.Height << 16)
                | ((isCopy ? 2ul : 1ul) << 8)
                | (ontoRt ? 1ul : 0ul);

            for (int i = 0; i < _frameShapeCount; i++)
            {
                if (_frameShapes[i] == shape)
                {
                    return;
                }
            }

            if (_frameShapeCount < _frameShapes.Length)
            {
                _frameShapes[_frameShapeCount++] = shape;
            }
        }

        /// <summary>
        /// Called at present, after the present blit is encoded and before the command
        /// buffer is committed. Consumes any ring slots whose command buffer has
        /// completed, then records this frame's samples and accumulator snapshot.
        /// </summary>
        public static unsafe void OnPresent(CommandBufferScoped cbs, Texture src)
        {
            if (!Enabled || _buf.NativePtr == IntPtr.Zero || src == null)
            {
                return;
            }

            // 1. Classify every slot whose fence already signalled. Never waits.
            for (int i = 0; i < Slots; i++)
            {
                ref Slot slot = ref _slots[i];

                if (slot.Valid)
                {
                    _fenceAsked++;
                }

                if (slot.Valid && slot.Fence.IsSignaled())
                {
                    _fenceReady++;
                    Classify(ref slot, i);
                }
            }

            MTLTexture tex = src.GetHandle(cbs);

            if (tex.NativePtr != IntPtr.Zero)
            {
                int idx = (int)(_frame % Slots);
                ref Slot mine = ref _slots[idx];

                if (mine.Valid)
                {
                    // The fence never signalled in a whole ring revolution. Drop the
                    // stale sample rather than wait for it.
                    mine.Fence.Put();
                    mine.Valid = false;
                    _dropped++;
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
                        _buf, (ulong)((idx * Pixels + i) * BytesPerPixel), BytesPerPixel, BytesPerPixel);
                }

                _dumpAValid[idx] = false;
                if (_dumpWhite && src.Width * src.Height * 4 <= DumpMaxBytes && _dumpsWritten < DumpMaxFiles)
                {
                    blit.CopyFromTexture(tex, 0, 0,
                        new MTLOrigin { x = 0, y = 0, z = 0 },
                        new MTLSize { width = (ulong)src.Width, height = (ulong)src.Height, depth = 1 },
                        _dumpA[idx], 0, (ulong)(src.Width * 4), (ulong)(src.Width * src.Height * 4));
                    _dumpAW[idx] = src.Width;
                    _dumpAH[idx] = src.Height;
                    _dumpAValid[idx] = true;
                }

                // Twenty-five more points, from the composite's input. Sampled as raw
                // 32-bit values rather than luma: the scene target is RG11B10Float, so
                // its bytes are not BGRA and only equality is meaningful. A flat input
                // collapses to a single distinct value.
                Texture sceneTex = _frameSceneTex;
                mine.InputSampled = false;

                if (sceneTex != null)
                {
                    MTLTexture stex = sceneTex.GetHandle(cbs);

                    if (stex.NativePtr != IntPtr.Zero && sceneTex.Width > GridSide && sceneTex.Height > GridSide)
                    {
                        for (int i = 0; i < Pixels; i++)
                        {
                            blit.CopyFromTexture(
                                stex, 0, 0,
                                new MTLOrigin
                                {
                                    // Adjacent texels around the centre, not a grid over
                                    // the whole image. Spread sampling already said the
                                    // texture carries a picture (19.72 distinct of 25);
                                    // what the shader actually averages is a 4x3
                                    // neighbourhood of one pixel.
                                    x = (ulong)(sceneTex.Width / 2 + i % GridSide),
                                    y = (ulong)(sceneTex.Height / 2 + i / GridSide),
                                    z = 0,
                                },
                                new MTLSize { width = 1, height = 1, depth = 1 },
                                _buf, (ulong)(((Slots + idx) * Pixels + i) * BytesPerPixel), BytesPerPixel, BytesPerPixel);
                        }

                        mine.InputSampled = true;
                    }
                }

                mine.InputWrittenAfter = _frameInputWrittenAfter;
                mine.Residency = _frameResidency;
                mine.CompositeDraws = _frameCompositeDraws;
                mine.VertexDistinct = _frameVertexDistinct;
                mine.VertexStride = _frameVertexStride;
                mine.Draw = _frameDraw;
                mine.Indices = _frameIndices;
                mine.Attrib = _frameAttrib;
                mine.Raster = _frameRaster;
                mine.PresentCb = cbs.CommandBufferIndex;
                mine.PresentRent = _pool != null ? _pool.RentSeqOf(cbs.CommandBufferIndex) : -1;
                mine.CompCb = _prevCompositeCb;
                mine.CompRent = _prevCompositeRent;
                _prevCompositeCb = _lastCompositeCb;
                _prevCompositeRent = _lastCompositeRent;
                mine.WriterCb = _frameWriterCb;
                mine.WriterRent = _frameWriterRent;
                mine.BlitCb = _frameBlitCb;
                mine.BlitRent = _frameBlitRent;
                mine.PsoFresh = _framePsoFresh;
                mine.ImgWriters = _frameImgWriters.Count == 0 ? "none" : string.Join(",", _frameImgWriters);
                mine.InSerial = _inputSerial;
                mine.WrSerial = _writerSerial;
                mine.AfterSampled = _frameAfterSampled;
                mine.AfterOutSampled = _frameAfterOutSampled;
                mine.AfterOutPtr = _frameAfterOutPtr;
                mine.PreProbed = _framePreProbed;
                _framePreProbed = false;
                mine.InputWriters = _frameInputWriters;
                mine.InputDesc = _frameInputDesc;
                _frameInputWriters = "-";
                mine.StageDraw = _frameStageDraw;
                _frameStageDraw = "-";
                mine.StageInputWriters = _frameStageInputWriters;
                _frameStageInputWriters = "-";
                mine.StageBlend = _frameStageBlend;
                _frameStageBlend = "-";
                mine.StageDrawCount = _frameStageDrawCount;
                _frameStageDrawCount = 0;
                {
                    int pj = (int)(_frame % StageSlots);
                    if (_pdFrame[pj] == _frame && _pdDrawIndex >= 0 && _pdDrawIndex < PdDraws && _pdSamplers[_pdDrawIndex] != null)
                    {
                        _pdSamplerAt[pj, _pdDrawIndex] = _pdSamplers[_pdDrawIndex].ToString();
                        _pdSamplers[_pdDrawIndex].Clear();
                    }
                }
                mine.RawSplits = _frameRawSplits;
                mine.BarrierSkipped = _frameBarrierSkipped;
                _frameRawSplits = 0;
                _frameBarrierReq = 0;
                _frameBarrierSkipped = 0;
                mine.StageInputLastWriters = _frameStageInputLastWriters;
                _frameStageInputLastWriters = "-";
                mine.StageInputId = _frameStageInputId;
                _frameStageInputId = "-";
                if (_frameConstSeen) { mine.Const = (float[])_frameConst.Clone(); mine.ConstSeen = true; _frameConstSeen = false; Array.Clear(_frameConstSlot); } else { mine.ConstSeen = false; }
                mine.PresentSrcPtr = src.CanonicalPtr;
                mine.DrawnSrgbPtrThisFrame = _frameAfterOutPtr;
                mine.SamplerSampled = _frameSamplerSampled;
                mine.InputFmt = _frameInputFmt;
                mine.InGen = _inputGen;
                mine.WrGen = _writerGen;
                mine.RowW ??= new float[MaxCbSlots];
                mine.RowSeen ??= new bool[MaxCbSlots];
                Array.Copy(_frameRowW, mine.RowW, MaxCbSlots);
                Array.Copy(_frameRowSeen, mine.RowSeen, MaxCbSlots);
                mine.OutOfOrder = _frameOutOfOrder;
                mine.ArgPtr = _frameArgPtr;
                mine.PresentMatchKnown = _frameLastFullResRt != IntPtr.Zero;
                mine.PresentMatchesLastRt = _frameLastFullResRt == src.CanonicalPtr;
                mine.Rgba8Known = _frameLastRgba8Rt != IntPtr.Zero;
                mine.Rgba8Match = _frameLastRgba8Rt == src.CanonicalPtr;
                mine.BlitPrevKnown = _frameBlitPairKnown;
                mine.SrcWasRecentDst = _frameSrcWasRecentDst;
                // Census the game's final RGBA8(sRGB) render target - the texture that is
                // white when the frame is white - not present's shadow of it.
                // The game's final RT was drawn LAST frame (lastAttachFrame == nowFrame - 1,
                // measured), so this frame's writer table never holds its writers. Use the
                // list snapshotted at the previous present.
                if (_frameGameRtTex != null && ++_rootCompareLogs % 300 == 1)
                {
                    // Every candidate root side by side, with its writer list, so the
                    // question "who writes the half that present will show next frame" is
                    // answered by reading one line rather than by inference.
                    string W(IntPtr r) => r != IntPtr.Zero && _pendingWriterCount.TryGetValue(r, out int c) && c > 0
                        ? string.Join(",", _pendingWriters[r], 0, Math.Min(c, MaxWriters)) : "-";
                    Logger.Warning?.PrintMsg(LogClass.Gpu,
                        $"ROOTS: gameRT(GAL)=0x{_frameGameRtRoot:X}[{W(_frameGameRtRoot)}] drawnSrgb=0x{_frameDrawnSrgbRoot:X}[{W(_frameDrawnSrgbRoot)}] presentSrc=0x{src.CanonicalPtr:X}[{W(src.CanonicalPtr)}] lastFullRes=0x{_frameLastFullResRt:X}[{W(_frameLastFullResRt)}]");

                    bool seen = false;
                    foreach (string e in _frameFullResAttach) { if (e.Contains($"0x{_frameGameRtRoot:X}")) { seen = true; break; } }
                    Logger.Warning?.PrintMsg(LogClass.Gpu,
                        $"ROOT COMPARE: A={_frameGameRtTex.Info.Format}@0x{_frameGameRtRoot:X}/n0x{_frameGameRtTex.GetHandle().NativePtr:X} seenAsAttachment={seen} | attachments this frame: {string.Join(" ; ", _frameFullResAttach)}");
                }
                _frameFullResAttach.Clear();
                mine.SceneWriters = _prevFrameGameRtWriters;
                if (_gameRtPassOpen && ++_gameRtEndLogs % 300 == 1)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"game RT pass: clear={_gameRtPassClear} drawsSince={_drawsSeen - _gameRtPassDrawStart}");
                }
                _gameRtPassOpen = false;
                // _frameGameRtRoot is what the GPU layer identifies as the game's final
                // target and it IS the half drawn this frame (ROOT COMPARE: it is in the
                // attachment list, present's source is not). Census it, attribute to next
                // frame's outcome, and reset so each frame's list is its own.
                _prevFrameGameRtWriters = _frameGameRtRoot != IntPtr.Zero && _pendingWriterCount.TryGetValue(_frameGameRtRoot, out int swc) && swc > 0
                    ? string.Join(",", _pendingWriters[_frameGameRtRoot], 0, Math.Min(swc, MaxWriters))
                    : "(none-gameRT)";
                if (_frameGameRtRoot != IntPtr.Zero) { _pendingWriterCount[_frameGameRtRoot] = 0; }
                _frameDrawnSrgbRoot = IntPtr.Zero;
                mine.RtWriters = _frameLastFullResRt != IntPtr.Zero && _pendingWriterCount.TryGetValue(_frameLastFullResRt, out int rwc) && rwc > 0
                    ? string.Join(",", _pendingWriters[_frameLastFullResRt], 0, Math.Min(rwc, MaxWriters))
                    : "(none)";
                mine.Triple = $"src{(_presentSrcRoot == _frameLastFullResRt ? "==" : "!=")}lastRT src{(_presentSrcRoot == _presentDstRoot ? "==" : "!=")}dst {_presentSrcW}x{_presentSrcH} srcSerial{(_presentSrcSerial == _prevPresentSrcSerial ? "SAME" : "new")}";
                mine.BlitPrevMatch = _frameBlitReadsPrevAttachment;
                mine.ArgCount = _frameArgCount;

                if (_frameArgCount > 0)
                {
                    mine.ArgExpected ??= new ulong[MaxArgIds];
                    Array.Copy(_frameArgExpected, mine.ArgExpected, _frameArgCount);
                }
                mine.LateWriter = _frameLateWriter;
                mine.Cb ??= new float[MaxCbSlots * 4];
                mine.CbSeen ??= new bool[MaxCbSlots];
                Array.Copy(_frameCb, mine.Cb, _frameCb.Length);
                Array.Copy(_frameCbSeen, mine.CbSeen, _frameCbSeen.Length);

                // Region E: the same 25-point grid from the frame's last full-resolution
                // colour render target - the texture the capture showed holding the
                // correct picture while present read white from another.
                mine.RtSampled = false;
                mine.ReplaceViews = _frameReplaceViews;
                mine.NewTextures = _frameNewTextures;
                mine.ModifiedBy = _frameModifiedBy.Length == 0 ? "(none)" : _frameModifiedBy;
                // Sample the VIEW (V), not A: A has no writers, so if V holds the picture on
                // white frames while A is white, the copy lands in V's storage and A is the shadow.
                Texture rtTex = _frameGameRtViewTex ?? _frameGameRtTex ?? _frameLastFullResTex;
                if (rtTex != null && rtTex.CanonicalPtr != src.CanonicalPtr)
                {
                    MTLTexture rt = rtTex.GetHandle(cbs);
                    if (rt.NativePtr != IntPtr.Zero)
                    {
                        // Sample through a BGRA-normalising path? RG11B10 raw bytes are not
                        // BGRA; decode with the sampler probe instead: use the compute read.
                        if (SamplerPathProbe.Ready)
                        {
                            SamplerPathProbe.Read(cbs, rt, (ulong)rtTex.Width, (ulong)rtTex.Height, _buf,
                                3 * Slots * Pixels * BytesPerPixel + 2 * Slots * Pixels * 16 - Slots * Pixels * 16 + idx * Pixels * 16);
                            mine.RtSampled = true;
                        }
                    }
                }

                mine.Fence = cbs.GetFence();
                mine.Fence.Get();
                mine.Frame = _frame;
                mine.Uploads = _frameUploads;
                mine.BigUploads = _frameBigUploads;
                mine.BigUploadsOntoRt = _frameBigUploadsOntoRt;
                mine.BigCopies = _frameBigCopies;
                mine.BigCopiesOntoRt = _frameBigCopiesOntoRt;
                mine.Shapes ??= new ulong[8];
                Array.Copy(_frameShapes, mine.Shapes, _frameShapeCount);
                mine.ShapeCount = _frameShapeCount;
                mine.Binding = _frameBinding;
                mine.BindingLast = _frameBindingLast;
                mine.Signature = $"n={_frameBinding.Count} " + string.Join(",", _frameProgs, 0, _frameProgCount);
                mine.PresentRoot = _framePresentRoot;
                mine.PresentAge = _framePresentAge;
                mine.Writers = _frameWriterCount == 0
                    ? "<none>"
                    : string.Join(",", _frameWriters, 0, _frameWriterCount);
                mine.Valid = true;
            }

            // 2. Reset the frame accumulators. The attachment set is per frame too.
            _attachedThisFrame.Clear();
            _frameBinding = default;
            _frameBindingLast = default;
            _frameSceneTex = null;
            // kept across frames; see the note above - clearing here starved the serial probe
            _compositeSeq = -1;
            _frameSeq = 0;
            _frameInputWrittenAfter = false;
            _frameResidency = -1;
            _frameCompositeDraws = 0;
            _frameVertexDistinct = -1;
            _frameVertexStride = -1;
            _frameDraw = (-1, -1, -1, -1);
            _frameIndices = null;
            _frameAttrib = null;
            _frameRaster = null;
            _frameWriterCb = -1;
            _frameWriterRent = -1;
            _frameBlitCb = -1;
            _frameBlitRent = -1;
            _frameBlitInputRoot = IntPtr.Zero;
            _framePsoFresh = false;
            _frameImgWriters.Clear();
            _inputSerial = -1;
            _writerSerial = -1;
            _inputGen = -1;
            _frameAfterSampled = false;
            _frameAfterOutSampled = false;
            _frameSamplerSampled = false;
            _afterBlitArmed = false;
            _writerGen = -1;
            // _compositeInputRoot deliberately NOT reset: the root is stable across frames
            // and resetting it at present starved the writer-serial trigger to 42 of 7,799.
            Array.Clear(_frameRowSeen);
            _frameOutOfOrder = 0;
            _frameArgPtr = IntPtr.Zero;
            _frameLastFullResRt = IntPtr.Zero;
            _frameReplaceViews = 0;
            _frameNewTextures = 0;
            _frameModifiedBy = "";
            _frameLastRgba8Rt = IntPtr.Zero;
            _frameSceneSourceRoot = IntPtr.Zero;
            _frameBlitPairKnown = false;
            _frameArgCount = 0;
            _frameLateWriter = null;
            Array.Clear(_frameCbSeen);
            _frameProgCount = 0;
            _framePresentRoot = IntPtr.Zero;
            _framePresentAge = -1;
            _frameShapeCount = 0;
            _frameUploads = 0;
            _frameBigUploads = 0;
            _frameBigUploadsOntoRt = 0;
            _frameBigCopies = 0;
            _frameBigCopiesOntoRt = 0;

            _frame++;
            PreProbedThisPeriod = false;

            if (_frame % ReportInterval == 0)
            {
                Report();
            }
        }

        private static unsafe void WriteDumps(ref Slot slot, int index, bool flat)
        {
            string tag = flat ? "white" : "normal";
            long fr = _frame;
            try
            {
                int w = _dumpAW[index], h = _dumpAH[index];
                string pa = $"/tmp/{tag}_A_f{fr}_{w}x{h}.rgba";
                using (var fs = new System.IO.FileStream(pa, System.IO.FileMode.Create))
                {
                    fs.Write(new ReadOnlySpan<byte>((void*)_dumpA[index].Contents, w * h * 4));
                }

                // The same storage, photographed right after the composite painted it in the
                // previous frame: slot index-1, provided its pointer matches and its CB is done.
                int prev = (index + Slots - 1) % Slots;
                string pf = "-";
                if (_dumpFPtr[prev] == slot.PresentSrcPtr && slot.PresentSrcPtr != IntPtr.Zero)
                {
                    if (_dumpFFence[prev] != null && _dumpFFence[prev].IsSignaled())
                    {
                        int fw = _dumpFW[prev], fh = _dumpFH[prev];
                        pf = $"/tmp/{tag}_F_f{fr}_{fw}x{fh}.rgba";
                        using var ff = new System.IO.FileStream(pf, System.IO.FileMode.Create);
                        ff.Write(new ReadOnlySpan<byte>((void*)_dumpF[prev].Contents, fw * fh * 4));
                    }
                    else { _dumpFNotReady++; pf = "F-not-ready"; }

                    // The composite's input from that same pass end (only meaningful when
                    // the F pair matched: same slot, same encoder).
                    if (_dumpFFence[prev] != null && _dumpFFence[prev].IsSignaled() && _dumpIW[prev] > 0)
                    {
                        int iw = _dumpIW[prev], ih = _dumpIH[prev];
                        int bpp = _dumpIFmt[prev] == MTLPixelFormat.RGBA16Float ? 8 : 4;
                        string pi = $"/tmp/{tag}_I_f{fr}_{iw}x{ih}_fmt{(int)_dumpIFmt[prev]}.raw";
                        using var fi = new System.IO.FileStream(pi, System.IO.FileMode.Create);
                        fi.Write(new ReadOnlySpan<byte>((void*)_dumpI[prev].Contents, iw * ih * bpp));
                    }
                }
                else { _dumpFUnpaired++; pf = "F-unpaired"; }

                if (flat) { _dumpsWritten++; } else { _dumpNormalWritten++; }
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"uploadcorr: dumped {tag} frame {fr}: A={pa} F={pf} (ptr 0x{slot.PresentSrcPtr:X})");
            }
            catch (Exception e)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"uploadcorr: dump failed: {e.Message}");
            }
        }

        private static unsafe void Classify(ref Slot slot, int index)
        {
            byte* p = (byte*)_buf.Contents + index * Pixels * BytesPerPixel;
            int saturated = 0;
            int magenta = 0;
            double lumaSum = 0;
            double lumaSqSum = 0;
            double lumaMin = 255;
            double lumaMax = 0;

            for (int i = 0; i < Pixels; i++)
            {
                byte* px = p + i * BytesPerPixel;
                double luma = (px[0] + px[1] + px[1] + px[2]) * 0.25;

                lumaSum += luma;
                lumaSqSum += luma * luma;

                if (luma < lumaMin)
                {
                    lumaMin = luma;
                }

                if (luma > lumaMax)
                {
                    lumaMax = luma;
                }

                if (luma >= SaturatedLuma)
                {
                    saturated++;
                }

                // Canary detection: magenta has luma ~128 and would classify as normal, so
                // it is counted directly. px[1] is green in this layout.
                if (px[0] > 200 && px[2] > 200 && px[1] < 60)
                {
                    magenta++;
                }
            }

            if (magenta >= SaturatedNeeded)
            {
                _magentaFrames++;
            }

            bool flat = saturated >= SaturatedNeeded;

            if (slot.AfterSampled && slot.SamplerSampled)
            {
                uint* rawC = (uint*)((byte*)_buf.Contents + (2 * Slots + index) * Pixels * BytesPerPixel);
                float* fD = (float*)((byte*)_buf.Contents + 3 * Slots * Pixels * BytesPerPixel + index * Pixels * 16);
                int disagree = 0;

                for (int i = 0; i < Pixels; i++)
                {
                    (float r, float g, float b) = SamplerPathProbe.DecodeRaw(rawC[i], slot.InputFmt);

                    if (!float.IsNaN(r) &&
                        (Math.Abs(r - fD[i * 4]) > 0.02f || Math.Abs(g - fD[i * 4 + 1]) > 0.02f || Math.Abs(b - fD[i * 4 + 2]) > 0.02f))
                    {
                        disagree++;
                    }
                }

                // Third path: the sampler unit against the read path, float against float.
                float* fS = (float*)((byte*)_buf.Contents + 3 * Slots * Pixels * BytesPerPixel + (Slots + index) * Pixels * 16);
                int sampDisagree = 0;

                for (int i = 0; i < Pixels; i++)
                {
                    if (Math.Abs(fS[i * 4] - fD[i * 4]) > 0.02f ||
                        Math.Abs(fS[i * 4 + 1] - fD[i * 4 + 1]) > 0.02f ||
                        Math.Abs(fS[i * 4 + 2] - fD[i * 4 + 2]) > 0.02f)
                    {
                        sampDisagree++;
                    }
                }

                if (sampDisagree >= 3) { if (flat) { _sampVsReadFlat++; } else { _sampVsReadNormal++; } }
                else { if (flat) { _sampSameFlat++; } else { _sampSameNormal++; } }

                bool paths = disagree >= 3;
                if (flat) { if (paths) { _twoPathDisagreeFlat++; } else { _twoPathAgreeFlat++; } }
                else { if (paths) { _twoPathDisagreeNormal++; } else { _twoPathAgreeNormal++; } }
            }

            if (slot.AfterSampled && slot.InputSampled)
            {
                uint* qa = (uint*)((byte*)_buf.Contents + (2 * Slots + index) * Pixels * BytesPerPixel);
                uint* qp = (uint*)((byte*)_buf.Contents + (Slots + index) * Pixels * BytesPerPixel);
                int distinctAfter = 0;
                int eq = 0;

                for (int i = 0; i < Pixels; i++)
                {
                    bool seen = false;
                    for (int j = 0; j < i; j++) { if (qa[j] == qa[i]) { seen = true; break; } }
                    if (!seen) { distinctAfter++; }
                    if (qa[i] == qp[i]) { eq++; }
                }

                // Uniform after the blit = the GPU saw a flat input when the blit ran.
                bool afterUniform = distinctAfter <= 3;

                if (flat)
                {
                    if (afterUniform) { _witFlatWhiteAfter++; } else { _witFlatPictureAfter++; }
                    _witFlatEqSum += eq; _witFlatEqN++;
                }
                else
                {
                    if (afterUniform) { _witNormalWhiteAfter++; } else { _witNormalPictureAfter++; }
                }
            }

            int inputDistinct = 0;

            // The composite draws in frame N, present shows it in N+1: the input sampled
            // in frame N belongs to frame N+1's outcome. One-frame delay line.
            int inputDistinctForThisOutcome = _pendingInputDistinct;
            bool inputKnownForThisOutcome = _pendingInputKnown;

            if (slot.InputSampled)
            {
                uint* q = (uint*)((byte*)_buf.Contents + (Slots + index) * Pixels * BytesPerPixel);

                for (int i = 0; i < Pixels; i++)
                {
                    bool seen = false;

                    for (int j = 0; j < i; j++)
                    {
                        if (q[j] == q[i])
                        {
                            seen = true;
                            break;
                        }
                    }

                    if (!seen)
                    {
                        inputDistinct++;
                    }
                }
            }

            // The shape of the white, not just the fact of it. Everything downstream of
            // this classifier has been asking who wrote white; nothing has asked what the
            // white looks like, and the two answers point at completely different faults.
            // A uniform fill - an undefined allocation, a clear, a discarded store - has
            // min == max and no spread. A real image driven to saturation by a bad
            // exposure or tonemap keeps its dark pixels and its structure, so min stays
            // well below max and the spread survives.
            double mean = lumaSum / Pixels;
            double variance = (lumaSqSum / Pixels) - (mean * mean);
            double sd = variance > 0 ? Math.Sqrt(variance) : 0;

            if (slot.SceneWriters != null && (_sceneWriterStats.Count < 40 || _sceneWriterStats.ContainsKey(slot.SceneWriters)))
            {
                (long sf2, long sn2) = _sceneWriterStats.TryGetValue(slot.SceneWriters, out (long Flat, long Normal) sv2) ? (sv2.Flat, sv2.Normal) : (0L, 0L);
                _sceneWriterStats[slot.SceneWriters] = flat ? (sf2 + 1, sn2) : (sf2, sn2 + 1);
            }

            if (slot.RtWriters != null && (_rtWriterStats.Count < 40 || _rtWriterStats.ContainsKey(slot.RtWriters)))
            {
                (long rf, long rn) = _rtWriterStats.TryGetValue(slot.RtWriters, out (long Flat, long Normal) rv) ? (rv.Flat, rv.Normal) : (0L, 0L);
                _rtWriterStats[slot.RtWriters] = flat ? (rf + 1, rn) : (rf, rn + 1);
            }

            if (slot.ModifiedBy != null && (_modByStats.Count < 16 || _modByStats.ContainsKey(slot.ModifiedBy)))
            {
                (long mf, long mn) = _modByStats.TryGetValue(slot.ModifiedBy, out (long Flat, long Normal) mv) ? (mv.Flat, mv.Normal) : (0L, 0L);
                _modByStats[slot.ModifiedBy] = flat ? (mf + 1, mn) : (mf, mn + 1);
            }

            if (flat) { _rvFlat += slot.ReplaceViews; _ntFlat += slot.NewTextures; _topoFlatN++; }
            else { _rvNormal += slot.ReplaceViews; _ntNormal += slot.NewTextures; _topoNormalN++; }

            if (slot.RtSampled)
            {
                float* fE = (float*)((byte*)_buf.Contents + 3 * Slots * Pixels * BytesPerPixel + 2 * Slots * Pixels * 16 - Slots * Pixels * 16 + index * Pixels * 16);
                double rtLumaSum = 0; int rtSat = 0;
                for (int i = 0; i < Pixels; i++)
                {
                    // RG11B10 decoded floats are linear HDR; treat > 0.9 in all channels as white
                    float r = fE[i * 4], g = fE[i * 4 + 1], b = fE[i * 4 + 2];
                    double lum = (r + g + g + b) * 0.25;
                    rtLumaSum += lum;
                    if (r > 0.9f && g > 0.9f && b > 0.9f) { rtSat++; }
                }
                bool rtWhite = rtSat >= SaturatedNeeded;
                bool srcWhite = flat;
                if (srcWhite && !rtWhite) { if (flat) { _rtPicSrcWhiteFlat++; } else { _rtPicSrcWhiteNormal++; } }
                else if (!srcWhite && !rtWhite) { if (flat) { _bothPicFlat++; } else { _bothPicNormal++; } }
                else if (srcWhite && rtWhite) { if (flat) { _bothWhiteFlat++; } else { _bothWhiteNormal++; } }
                else { if (flat) { _rtWhiteSrcPicFlat++; } else { _rtWhiteSrcPicNormal++; } }
            }

            if (slot.SrcWasRecentDst) { if (flat) { _srcWasRecentDstFlat++; } else { _srcWasRecentDstNormal++; } }
            else { if (flat) { _srcNotDstFlat++; } else { _srcNotDstNormal++; } }

            if (slot.Triple != null && _tripleStats.Count < 32)
            {
                (long tf, long tn) = _tripleStats.TryGetValue(slot.Triple, out (long Flat, long Normal) tv) ? (tv.Flat, tv.Normal) : (0L, 0L);
                _tripleStats[slot.Triple] = flat ? (tf + 1, tn) : (tf, tn + 1);
            }

            if (slot.BlitPrevKnown)
            {
                if (slot.BlitPrevMatch) { if (flat) { _blitPrevMatchFlat++; } else { _blitPrevMatchNormal++; } }
                else { if (flat) { _blitPrevMismatchFlat++; } else { _blitPrevMismatchNormal++; } }
            }

            if (!slot.Rgba8Known) { if (flat) { _rgbaNoneFlat++; } else { _rgbaNoneNormal++; } }
            else if (slot.Rgba8Match) { if (flat) { _rgbaMatchFlat++; } else { _rgbaMatchNormal++; } }
            else { if (flat) { _rgbaMismFlat++; } else { _rgbaMismNormal++; } }

            if (slot.PresentMatchKnown)
            {
                if (slot.PresentMatchesLastRt) { if (flat) { _presentMatchFlat++; } else { _presentMatchNormal++; } }
                else { if (flat) { _presentMismatchFlat++; } else { _presentMismatchNormal++; } }
            }

            if (slot.ArgCount > 0 && slot.ArgPtr != IntPtr.Zero)
            {
                ulong* now = (ulong*)slot.ArgPtr;
                bool differs = false;

                for (int i = 0; i < slot.ArgCount; i++)
                {
                    if (now[i] != slot.ArgExpected[i])
                    {
                        differs = true;
                        break;
                    }
                }

                if (flat)
                {
                    _flatArgChecked++;
                    if (differs) { _flatArgMismatch++; }
                }
                else
                {
                    _normalArgChecked++;
                    if (differs) { _normalArgMismatch++; }
                }
            }

            if (flat)
            {
                _flatDrawSum += slot.CompositeDraws;
                _flatDrawN++;
            }
            else
            {
                _normalDrawSum += slot.CompositeDraws;
                _normalDrawN++;
            }

            if (flat) { _flatOooSum += slot.OutOfOrder; _flatOooN++; }
            else { _normalOooSum += slot.OutOfOrder; _normalOooN++; }

            if (slot.RowSeen != null)
            {
                if (!_wInit)
                {
                    _wInit = true;
                    for (int c = 0; c < MaxCbSlots; c++) { _wFlatMin[c] = _wNormalMin[c] = float.MaxValue; }
                }

                for (int c = 0; c < MaxCbSlots; c++)
                {
                    if (!slot.RowSeen[c]) { continue; }
                    float w = slot.RowW[c];
                    if (flat)
                    {
                        _wFlatSum[c] += w; _wFlatN[c]++;
                        if (Math.Abs(w) < Math.Abs(_wFlatMin[c])) { _wFlatMin[c] = w; }
                    }
                    else
                    {
                        _wNormalSum[c] += w; _wNormalN[c]++;
                        if (Math.Abs(w) < Math.Abs(_wNormalMin[c])) { _wNormalMin[c] = w; }
                    }
                }
            }

            if (slot.InGen >= 0 && slot.WrGen >= 0)
            {
                if (slot.InGen != slot.WrGen) { if (flat) { _genSwapFlat++; } else { _genSwapNormal++; } }
                else { if (flat) { _genSameFlat++; } else { _genSameNormal++; } }
            }

            if (slot.InSerial >= 0 && slot.WrSerial >= 0)
            {
                bool match = slot.InSerial == slot.WrSerial;
                if (flat) { if (match) { _matchFlat++; } else { _mismatchFlat++; } }
                else { if (match) { _matchNormal++; } else { _mismatchNormal++; } }
            }

            if (slot.ImgWriters != null && _imgStats.Count < 32)
            {
                (long gf, long gn) = _imgStats.TryGetValue(slot.ImgWriters, out (long Flat, long Normal) gv) ? (gv.Flat, gv.Normal) : (0L, 0L);
                _imgStats[slot.ImgWriters] = flat ? (gf + 1, gn) : (gf, gn + 1);
            }

            if (slot.Raster != null)
            {
                if (slot.PsoFresh) { if (flat) { _freshFlat++; } else { _freshNormal++; } }
                else { if (flat) { _staleFlat++; } else { _staleNormal++; } }
            }

            if (slot.CompCb >= 0 && slot.PresentCb >= 0 && _pool != null)
            {
                if (slot.CompCb == slot.PresentCb && slot.CompRent == slot.PresentRent)
                {
                    if (flat) { _cpSameFlat++; } else { _cpSameNormal++; }
                }
                else
                {
                    long cc = _pool.CommitSeqOf(slot.CompCb);
                    long pc = _pool.CommitSeqOf(slot.PresentCb);
                    if (cc == 0 || pc == 0) { if (flat) { _cpUnkFlat++; } else { _cpUnkNormal++; } }
                    else if (cc < pc) { if (flat) { _cpOrderedFlat++; } else { _cpOrderedNormal++; } }
                    else { if (flat) { _cpInvertedFlat++; } else { _cpInvertedNormal++; } }
                }
            }

            if (slot.WriterCb >= 0 && slot.BlitCb >= 0)
            {
                if (slot.WriterCb == slot.BlitCb && slot.WriterRent == slot.BlitRent)
                {
                    if (flat) { _pairSameCbFlat++; } else { _pairSameCbNormal++; }
                }
                else
                {
                    // Commit order of the pair, read at classification time (several
                    // frames later, both long committed). Rent order says which SHOULD
                    // be first; commit order says which WAS.
                    long wc = _pool.CommitSeqOf(slot.WriterCb);
                    long bc = _pool.CommitSeqOf(slot.BlitCb);
                    bool writerFirstByRent = slot.WriterRent < slot.BlitRent;

                    if (wc == 0 || bc == 0)
                    {
                        if (flat) { _pairUncommittedFlat++; } else { _pairUncommittedNormal++; }
                    }
                    else if ((wc < bc) == writerFirstByRent)
                    {
                        if (flat) { _pairOrderedFlat++; } else { _pairOrderedNormal++; }
                    }
                    else
                    {
                        if (flat) { _pairInvertedFlat++; } else { _pairInvertedNormal++; }
                    }
                }
            }

            if (slot.Raster != null && _rasterStats.Count < 32)
            {
                (long rf, long rn) = _rasterStats.TryGetValue(slot.Raster, out (long Flat, long Normal) rv) ? (rv.Flat, rv.Normal) : (0L, 0L);
                _rasterStats[slot.Raster] = flat ? (rf + 1, rn) : (rf, rn + 1);
            }

            if (slot.Attrib != null && _attribStats.Count < 32)
            {
                (long af2, long an2) = _attribStats.TryGetValue(slot.Attrib, out (long Flat, long Normal) av2) ? (av2.Flat, av2.Normal) : (0L, 0L);
                _attribStats[slot.Attrib] = flat ? (af2 + 1, an2) : (af2, an2 + 1);
            }

            if (slot.Indices != null && _indexStats.Count < 32)
            {
                (long jf, long jn) = _indexStats.TryGetValue(slot.Indices, out (long Flat, long Normal) jv) ? (jv.Flat, jv.Normal) : (0L, 0L);
                _indexStats[slot.Indices] = flat ? (jf + 1, jn) : (jf, jn + 1);
            }

            if (slot.Draw.Count >= 0 && _drawParamStats.Count < 32)
            {
                string k = $"count={slot.Draw.Count} inst={slot.Draw.Inst} first={slot.Draw.First} indexed={slot.Draw.Indexed}";
                (long df, long dn) = _drawParamStats.TryGetValue(k, out (long Flat, long Normal) dv) ? (dv.Flat, dv.Normal) : (0L, 0L);
                _drawParamStats[k] = flat ? (df + 1, dn) : (df, dn + 1);
            }

            if (slot.VertexStride >= 0)
            {
                if (flat)
                {
                    _flatStrideSum += slot.VertexStride;
                    _flatStrideN++;
                    if (slot.VertexStride < _flatStrideMin) { _flatStrideMin = slot.VertexStride; }
                    if (slot.VertexStride > _flatStrideMax) { _flatStrideMax = slot.VertexStride; }
                }
                else
                {
                    _normalStrideSum += slot.VertexStride;
                    _normalStrideN++;
                }
            }

            if (slot.VertexDistinct >= 0)
            {
                if (flat) { _flatVertSum += slot.VertexDistinct; _flatVertN++; }
                else { _normalVertSum += slot.VertexDistinct; _normalVertN++; }
            }

            if (slot.Residency >= 0)
            {
                if (flat)
                {
                    _flatResidencySum += slot.Residency;
                    _flatResidencyN++;
                }
                else
                {
                    _normalResidencySum += slot.Residency;
                    _normalResidencyN++;
                }
            }

            if (slot.InputWrittenAfter)
            {
                if (flat)
                {
                    _flatLateWrites++;
                }
                else
                {
                    _normalLateWrites++;
                }

                if (slot.LateWriter != null && _lateWriterStats.Count < MaxSignatures)
                {
                    _lateWriterStats.TryGetValue(slot.LateWriter, out (long Flat, long Normal) w);
                    _lateWriterStats[slot.LateWriter] = flat ? (w.Flat + 1, w.Normal) : (w.Flat, w.Normal + 1);
                }
            }

            if (slot.CbSeen != null)
            {
                if (!_cbExtremaInit)
                {
                    _cbExtremaInit = true;

                    for (int i = 0; i < MaxCbSlots * 4; i++)
                    {
                        _cbFlatMin[i] = _cbNormalMin[i] = float.MaxValue;
                        _cbFlatMax[i] = _cbNormalMax[i] = float.MinValue;
                    }
                }

                double[] into = flat ? _cbFlatSum : _cbNormalSum;
                long[] n = flat ? _cbFlatN : _cbNormalN;
                float[] lo = flat ? _cbFlatMin : _cbNormalMin;
                float[] hi = flat ? _cbFlatMax : _cbNormalMax;

                for (int c = 0; c < MaxCbSlots; c++)
                {
                    if (!slot.CbSeen[c])
                    {
                        continue;
                    }

                    n[c]++;

                    for (int i = 0; i < 4; i++)
                    {
                        float v = slot.Cb[c * 4 + i];
                        into[c * 4 + i] += v;

                        if (v < lo[c * 4 + i])
                        {
                            lo[c * 4 + i] = v;
                        }

                        if (v > hi[c * 4 + i])
                        {
                            hi[c * 4 + i] = v;
                        }
                    }
                }
            }

            _pendingInputDistinct = inputDistinct;
            _pendingInputKnown = slot.InputSampled;

            bool outKnownForThisOutcome = _pendingAfterOutKnown;
            bool outWhiteForThisOutcome = _pendingAfterOutWhite;
            {
                bool w = false;
                if (slot.AfterOutSampled)
                {
                    byte* po = (byte*)_buf.Contents + 4 * Slots * Pixels * BytesPerPixel + 2 * Slots * Pixels * 16 + index * Pixels * BytesPerPixel;
                    int sat = 0;
                    for (int i = 0; i < Pixels; i++)
                    {
                        byte* px = po + i * BytesPerPixel;
                        if ((px[0] + px[1] + px[1] + px[2]) * 0.25 >= SaturatedLuma) { sat++; }
                    }
                    w = sat >= SaturatedNeeded;
                }
                _pendingAfterOutKnown = slot.AfterOutSampled;
                _pendingAfterOutWhite = w;
            }
            if (outKnownForThisOutcome)
            {
                if (flat) { if (outWhiteForThisOutcome) { _outWhiteAtDrawFlat++; } else { _outPicAtDrawFlat++; } }
                else { if (outWhiteForThisOutcome) { _outWhiteAtDrawNormal++; } else { _outPicAtDrawNormal++; } }
            }

            // Record this frame's painted half by pointer, then pair THIS frame's present
            // source (by pointer) with what it looked like when it was painted.
            if (slot.AfterOutSampled && slot.AfterOutPtr != IntPtr.Zero)
            {
                bool w = false;
                byte* po = (byte*)_buf.Contents + 4 * Slots * Pixels * BytesPerPixel + 2 * Slots * Pixels * 16 + index * Pixels * BytesPerPixel;
                int sat = 0;
                for (int i = 0; i < Pixels; i++) { byte* px = po + i * BytesPerPixel; if ((px[0] + px[1] + px[1] + px[2]) * 0.25 >= SaturatedLuma) { sat++; } }
                w = sat >= SaturatedNeeded;
                _paintedWhiteByPtr[slot.AfterOutPtr] = w;
                if (_paintedWhiteByPtr.Count > 8) { _paintedWhiteByPtr.Clear(); _paintedWhiteByPtr[slot.AfterOutPtr] = w; }
            }

            if (slot.PresentSrcPtr != IntPtr.Zero && slot.DrawnSrgbPtrThisFrame != IntPtr.Zero)
            {
                bool sameHalf = slot.PresentSrcPtr == slot.DrawnSrgbPtrThisFrame;
                if (flat) { if (sameHalf) { _rwSameHalfFlat++; } else { _rwOtherHalfFlat++; } }
                else { if (sameHalf) { _rwSameHalfNormal++; } else { _rwOtherHalfNormal++; } }
            }

            if (slot.PresentSrcPtr != IntPtr.Zero && _paintedWhiteByPtr.TryGetValue(slot.PresentSrcPtr, out bool paintedWhite))
            {
                // 'flat' is what present shows NOW for this same pointer.
                if (flat)
                {
                    if (paintedWhite) { _sameWhiteToWhiteFlat++; } else { _samePicToWhiteFlat++; }
                }
                else
                {
                    if (paintedWhite) { _sameWhiteToPicNormal++; } else { _samePicToPicNormal++; }
                }
            }
            else
            {
                if (flat) { _sameUnpairedFlat++; } else { _sameUnpairedNormal++; }
            }

            if (_sceneSampled[index])
            {
                uint* sp = (uint*)((byte*)_buf.Contents + SceneBase + index * Pixels * BytesPerPixel);
                double sum = 0;
                for (int i = 0; i < Pixels; i++)
                {
                    uint v = sp[i];
                    sum += (Rg11(v & 0x7ff) + 2 * Rg11((v >> 11) & 0x7ff) + B10((v >> 22) & 0x3ff)) * 0.25;
                }
                _sceneRing[_sceneRingAt % _sceneRing.Length] = $"f{slot.Frame} {(flat ? "W" : ".")} {sum / Pixels:G5}";
                _sceneRingAt++;
            }

            if (_tinyPreSampled[index])
            {
                float* e = (float*)((byte*)_buf.Contents + TinyPreBase + index * 16);
                for (int c = 0; c < 4; c++)
                {
                    string key = $"pre1x1[{c}]";
                    CbStat v = _tinyPreStats.TryGetValue(key, out CbStat ex) ? ex : new CbStat { FMin = double.MaxValue, FMax = double.MinValue, NMin = double.MaxValue, NMax = double.MinValue };
                    double d = float.IsFinite(e[c]) ? e[c] : 1e30;
                    if (flat) { v.Flat++; v.FMin = Math.Min(v.FMin, d); v.FMax = Math.Max(v.FMax, d); }
                    else { v.Normal++; v.NMin = Math.Min(v.NMin, d); v.NMax = Math.Max(v.NMax, d); }
                    _tinyPreStats[key] = v;
                }
                if (flat ? _tinyPreWhite < 30 : _tinyPreNormal < 30)
                {
                    if (flat) { _tinyPreWhite++; } else { _tinyPreNormal++; }
                    _tinyPreSamples.Add($"f{slot.Frame} {(flat ? "WHITE " : "normal")} pre1x1=({e[0]:G6},{e[1]:G6},{e[2]:G6},{e[3]:G6})");
                }
                _tinyPreSampled[index] = false;
            }

            if (_tinySampled[index])
            {
                float* e = (float*)((byte*)_buf.Contents + TinyBase + index * 16);
                // Only transitions: a run of identical (value, outcome) collapses to one line
                // with a repeat count, so 10k frames still show every jump.
                string key = $"{e[0]:G6},{e[1]:G6},{e[2]:G6},{e[3]:G6}|{(flat ? "WHITE" : "normal")}|0x{_tinyPtr[index].ToInt64():X}";
                if (key == _tinyLastKey)
                {
                    _tinyRepeat++;
                }
                else
                {
                    if (_tinyLastKey != null && _tinySeries.Count < 300)
                    {
                        _tinySeries.Add($"{_tinyLastFrame,6}..{slot.Frame - 1,-6} x{_tinyRepeat,-4} {_tinyLastKey}");
                    }
                    _tinyLastKey = key;
                    _tinyLastFrame = slot.Frame;
                    _tinyRepeat = 1;
                }
            }

            if (slot.ConstSeen && slot.Const != null)
            {
                for (int i = 0; i < ConstSlots * ConstFloats; i++)
                {
                    double v = slot.Const[i];
                    (long Fn, double Fmin, double Fmax, double Fsum, long Nn, double Nmin, double Nmax, double Nsum) e =
                        _constStats.TryGetValue(i, out (long Fn, double Fmin, double Fmax, double Fsum, long Nn, double Nmin, double Nmax, double Nsum) cur)
                        ? cur : (0, double.MaxValue, double.MinValue, 0, 0, double.MaxValue, double.MinValue, 0);
                    if (flat) { e = (e.Fn + 1, Math.Min(e.Fmin, v), Math.Max(e.Fmax, v), e.Fsum + v, e.Nn, e.Nmin, e.Nmax, e.Nsum); }
                    else { e = (e.Fn, e.Fmin, e.Fmax, e.Fsum, e.Nn + 1, Math.Min(e.Nmin, v), Math.Max(e.Nmax, v), e.Nsum + v); }
                    _constStats[i] = e;
                }
            }

            {
                string sik = slot.StageInputId ?? "-";
                (long Flat, long Normal) siv = _stageInputIdStats.TryGetValue(sik, out (long Flat, long Normal) sie) ? sie : (0, 0);
                _stageInputIdStats[sik] = flat ? (siv.Flat + 1, siv.Normal) : (siv.Flat, siv.Normal + 1);
            }

            {
                string slk = slot.StageInputLastWriters ?? "-";
                (long Flat, long Normal) slv = _stageInputLastWriterStats.TryGetValue(slk, out (long Flat, long Normal) sle) ? sle : (0, 0);
                _stageInputLastWriterStats[slk] = flat ? (slv.Flat + 1, slv.Normal) : (slv.Flat, slv.Normal + 1);
            }

            {
                int rs = slot.RawSplits / 25 * 25;   // bucketed
                (long Flat, long Normal) rv = _rawSplitStats.TryGetValue(rs, out (long Flat, long Normal) re) ? re : (0, 0);
                _rawSplitStats[rs] = flat ? (rv.Flat + 1, rv.Normal) : (rv.Flat, rv.Normal + 1);
                int bs = slot.BarrierSkipped / 25 * 25;
                (long Flat, long Normal) bv2 = _barrierSkipStats.TryGetValue(bs, out (long Flat, long Normal) be2) ? be2 : (0, 0);
                _barrierSkipStats[bs] = flat ? (bv2.Flat + 1, bv2.Normal) : (bv2.Flat, bv2.Normal + 1);
            }

            {
                int dc = slot.StageDrawCount;
                (long Flat, long Normal) dv = _stageDrawCountStats.TryGetValue(dc, out (long Flat, long Normal) de) ? de : (0, 0);
                _stageDrawCountStats[dc] = flat ? (dv.Flat + 1, dv.Normal) : (dv.Flat, dv.Normal + 1);
            }

            {
                string bk = slot.StageBlend ?? "-";
                (long Flat, long Normal) bv = _stageBlendStats.TryGetValue(bk, out (long Flat, long Normal) be) ? be : (0, 0);
                _stageBlendStats[bk] = flat ? (bv.Flat + 1, bv.Normal) : (bv.Flat, bv.Normal + 1);
            }

            {
                string swk = slot.StageInputWriters ?? "-";
                (long Flat, long Normal) swv = _stageInputWriterStats.TryGetValue(swk, out (long Flat, long Normal) swe) ? swe : (0, 0);
                _stageInputWriterStats[swk] = flat ? (swv.Flat + 1, swv.Normal) : (swv.Flat, swv.Normal + 1);
            }

            {
                string sdk = slot.StageDraw ?? "-";
                (long Flat, long Normal) sdv = _stageDrawStats.TryGetValue(sdk, out (long Flat, long Normal) sde) ? sde : (0, 0);
                _stageDrawStats[sdk] = flat ? (sdv.Flat + 1, sdv.Normal) : (sdv.Flat, sdv.Normal + 1);
            }

            {
                string k = slot.InputWriters ?? "-";
                (long Flat, long Normal) v = _inputWriterStats.TryGetValue(k, out (long Flat, long Normal) e) ? e : (0, 0);
                _inputWriterStats[k] = flat ? (v.Flat + 1, v.Normal) : (v.Flat, v.Normal + 1);
                string d = slot.InputDesc ?? "-";
                (long Flat, long Normal) dv = _inputDescStats.TryGetValue(d, out (long Flat, long Normal) de) ? de : (0, 0);
                _inputDescStats[d] = flat ? (dv.Flat + 1, dv.Normal) : (dv.Flat, dv.Normal + 1);
            }

            if (slot.PreProbed)
            {
                float* pr = (float*)((byte*)_buf.Contents + PreProbeReadBase + index * Pixels * 16);
                float* ps = (float*)((byte*)_buf.Contents + PreProbeSampBase + index * Pixels * 16);
                int satR = 0, satS = 0; double meanR = 0;
                for (int i = 0; i < Pixels; i++)
                {
                    float lr = (pr[i * 4] + 2 * pr[i * 4 + 1] + pr[i * 4 + 2]) * 0.25f;
                    float ls = (ps[i * 4] + 2 * ps[i * 4 + 1] + ps[i * 4 + 2]) * 0.25f;
                    meanR += lr;
                    // Linear-light threshold matching SaturatedLuma/255 in sRGB terms (~0.9).
                    if (lr >= 0.9f) { satR++; }
                    if (ls >= 0.9f) { satS++; }
                }
                // For the upscaler's input the question is not "white" but "still the clear":
                // count distinct read values too (an exact clear is 1 distinct of 25).
                int distinctR = 0;
                for (int i = 0; i < Pixels; i++)
                {
                    bool seen = false;
                    for (int j2 = 0; j2 < i; j2++) { if (pr[j2 * 4] == pr[i * 4] && pr[j2 * 4 + 1] == pr[i * 4 + 1] && pr[j2 * 4 + 2] == pr[i * 4 + 2]) { seen = true; break; } }
                    if (!seen) { distinctR++; }
                }
                if (flat) { _preDistinctFlatSum += distinctR; } else { _preDistinctNormalSum += distinctR; }
                bool preWhite = satR >= SaturatedNeeded || distinctR <= 3, preSampWhite = satS >= SaturatedNeeded;
                if (flat) { if (preWhite) { _preWhiteFlat++; } else { _prePicFlat++; } _preFlatMeanSum += meanR / Pixels; _preFlatMeanN++; }
                else { if (preWhite) { _preWhiteNormal++; } else { _prePicNormal++; } _preNormalMeanSum += meanR / Pixels; _preNormalMeanN++; }
                if (flat) { if (preSampWhite) { _preSampWhiteFlat++; } else { _preSampPicFlat++; } }
                else { if (preSampWhite) { _preSampWhiteNormal++; } else { _preSampPicNormal++; } }
            }
            else
            {
                if (flat) { _preUnprobedFlat++; } else { _preUnprobedNormal++; }
            }

            if (_dumpWhite && _dumpAValid[index] && (flat ? _dumpsWritten < DumpMaxFiles : (_dumpNormalWritten < 1 && _dumpsWritten > 0)))
            {
                WriteDumps(ref slot, index, flat);
            }

            if (_passTrace)
            {
                if (flat && _traceLogged < 4)
                {
                    _traceLogged++;
                    // This slot's trace is the frame that PRODUCED the presented white (the
                    // present of frame N shows what the game drew during period N-1); the
                    // trace recorded during period N-1 sits in slot (index-1).
                    int prev = TraceSlotFor(slot.Frame - 1);
                    Logger.Warning?.PrintMsg(LogClass.Gpu, prev < 0
                        ? $"uploadcorr: PASS TRACE for WHITE frame {slot.Frame}: period {slot.Frame - 1} expired from the ring"
                        : $"uploadcorr: PASS TRACE of the stage input for WHITE frame {slot.Frame} (period {slot.Frame - 1}, {_traceCount[prev]} passes):{TraceReport(prev)}");
                }
                else if (!flat && _traceLoggedNormal < 2 && _traceLogged > 0)
                {
                    _traceLoggedNormal++;
                    int prev = TraceSlotFor(slot.Frame - 1);
                    Logger.Warning?.PrintMsg(LogClass.Gpu, prev < 0
                        ? $"uploadcorr: PASS TRACE for a NORMAL frame {slot.Frame}: period {slot.Frame - 1} expired from the ring"
                        : $"uploadcorr: PASS TRACE of the stage input for a NORMAL frame {slot.Frame} (period {slot.Frame - 1}, {_traceCount[prev]} passes):{TraceReport(prev)}");
                }
            }

            ClassifyVsDump(slot.Frame, flat);
            ClassifySeq(slot.Frame, flat);
            ClassifyCounterReport(slot.Frame, flat);
            ClassifyProbeReadback(slot.Frame, flat);

            if (_stageLabel.Length != 0 && slot.Frame > 1)
            {
                ClassifyStageCb(slot.Frame, flat);
                ClassifyStageVertex(slot.Frame, flat);
                ClassifyPerDraw(slot.Frame, flat);
                ClassifyPerDrawSamplers(slot.Frame, flat);
                ClassifyStageArgTables(slot.Frame, flat);
                // The normal reference MUST come from the flashing regime. Taking it at frame
                // ~216 (dark area, exposure 0.13) against white frames at ~1300 (bright area,
                // exposure 1.0) compared two different scenes and produced a false root cause.
                if (flat ? _stageDumpsWritten < DumpMaxFiles : (_stageDumpsNormal < 2 && _stageDumpsWritten > 0))
                {
                    WriteStageDumps(slot.Frame, flat);
                }
            }

            if (flat)
            {
                if (inputKnownForThisOutcome)
                {
                    _flatInputDistinctSum += inputDistinctForThisOutcome;
                    _flatInputFrames++;
                }

                _flatMinSum += lumaMin;
                _flatMaxSum += lumaMax;
                _flatSdSum += sd;
                _flatSatSum += saturated;
                _lumaFlatSum += lumaSum / Pixels;
                _runFrames++;
                _currentRun++;

                if (!_prevFlat)
                {
                    _runs++;

                    if (_currentGap > 0)
                    {
                        _gapSum += _currentGap;
                        _gaps++;
                        _currentGap = 0;
                    }
                }
            }
            else
            {
                if (inputKnownForThisOutcome)
                {
                    _normalInputDistinctSum += inputDistinctForThisOutcome;
                    _normalInputFrames++;
                }

                _normalMinSum += lumaMin;
                _normalMaxSum += lumaMax;
                _normalSdSum += sd;
                _lumaNormalSum += lumaSum / Pixels;
                _currentGap++;

                if (_prevFlat)
                {
                    if (_currentRun > _longestRun)
                    {
                        _longestRun = _currentRun;
                    }

                    _currentRun = 0;
                }
            }

            _prevFlat = flat;

            if (flat)
            {
                _flatFrames++;

                if (slot.BigUploads > 0)
                {
                    _flatWithUpload++;
                }

                if (slot.BigUploadsOntoRt > 0)
                {
                    _flatWithUploadOntoRt++;
                }

                if (slot.BigCopies > 0)
                {
                    _flatWithCopy++;
                }

                if (slot.BigCopiesOntoRt > 0)
                {
                    _flatWithCopyOntoRt++;
                }
            }
            else
            {
                _normalFrames++;

                if (slot.BigUploads > 0)
                {
                    _normalWithUpload++;
                }

                if (slot.BigUploadsOntoRt > 0)
                {
                    _normalWithUploadOntoRt++;
                }

                if (slot.BigCopies > 0)
                {
                    _normalWithCopy++;
                }

                if (slot.BigCopiesOntoRt > 0)
                {
                    _normalWithCopyOntoRt++;
                }
            }

            if (slot.Binding.Count == 0)
            {
                _framesWithNoBinding++;
            }
            else
            {
                string key = $"FIRST n={slot.Binding.Count} gpu=0x{slot.Binding.GpuAddress:X} " +
                             $"tex=0x{slot.Binding.NativePtr:X} root=0x{slot.Binding.CanonicalPtr:X} " +
                             $"prog={slot.Binding.Program}";

                (long f, long n) = _bindingStats.TryGetValue(key, out (long Flat, long Normal) b) ? (b.Flat, b.Normal) : (0L, 0L);
                _bindingStats[key] = flat ? (f + 1, n) : (f, n + 1);

                string lastKey = $"LAST  gpu=0x{slot.BindingLast.GpuAddress:X} " +
                                 $"tex=0x{slot.BindingLast.NativePtr:X} root=0x{slot.BindingLast.CanonicalPtr:X} " +
                                 $"prog={slot.BindingLast.Program}";

                (long lf, long ln) = _bindingStats.TryGetValue(lastKey, out (long Flat, long Normal) lb) ? (lb.Flat, lb.Normal) : (0L, 0L);
                _bindingStats[lastKey] = flat ? (lf + 1, ln) : (lf, ln + 1);
            }

            if (slot.PresentRoot != IntPtr.Zero)
            {
                string key = $"0x{slot.PresentRoot:X}";
                (long pf, long pn) = _presentIdentity.TryGetValue(key, out (long Flat, long Normal) pv) ? (pv.Flat, pv.Normal) : (0L, 0L);
                _presentIdentity[key] = flat ? (pf + 1, pn) : (pf, pn + 1);

                (long af, long an) = _presentAge.TryGetValue(slot.PresentAge, out (long Flat, long Normal) av) ? (av.Flat, av.Normal) : (0L, 0L);
                _presentAge[slot.PresentAge] = flat ? (af + 1, an) : (af, an + 1);

                if (slot.Writers != null && (_writerStats.Count < 64 || _writerStats.ContainsKey(slot.Writers)))
                {
                    (long wf, long wn) = _writerStats.TryGetValue(slot.Writers, out (long Flat, long Normal) wv) ? (wv.Flat, wv.Normal) : (0L, 0L);
                    _writerStats[slot.Writers] = flat ? (wf + 1, wn) : (wf, wn + 1);
                }
            }

            if (slot.Signature != null &&
                (_signatureStats.Count < MaxSignatures || _signatureStats.ContainsKey(slot.Signature)))
            {
                (long sf, long sn) = _signatureStats.TryGetValue(slot.Signature, out (long Flat, long Normal) sv)
                    ? (sv.Flat, sv.Normal)
                    : (0L, 0L);

                _signatureStats[slot.Signature] = flat ? (sf + 1, sn) : (sf, sn + 1);
            }

            for (int i = 0; i < slot.ShapeCount; i++)
            {
                (long f, long n) = _shapeStats.TryGetValue(slot.Shapes[i], out (long Flat, long Normal) v) ? (v.Flat, v.Normal) : (0L, 0L);
                _shapeStats[slot.Shapes[i]] = flat ? (f + 1, n) : (f, n + 1);
            }

            slot.Fence.Put();
            slot.Valid = false;
        }

        // ---- GPU-side dump written by the flare vertex shader (RYUJINX_METAL_FLARE_CONST bit 16) ----
        // Header: uint counter; then VsdRecords records of 5 x float4 (layout in Program.FlareConst).
        // Read per classification window: the records appended since the previous window are
        // attributed to this presented frame's outcome. Alignment is at most a frame off, and
        // white frames are isolated singles, so the windows around each white frame are logged
        // verbatim to show where the records actually land.
        public static readonly bool VsDumpEnabled =
            ((int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_FLARE_CONST"), out int vfc) ? vfc : 0) & 48) != 0;
        public static MTLBuffer VsDumpBuffer;
        private const int VsdRecords = 4096;
        private static uint _vsdLast;
        private static long _vsdWinWhite, _vsdWinNormal, _vsdBigWinWhite, _vsdBigWinNormal, _vsdRecWhite, _vsdRecNormal;
        private static long _vsdBigRecWhite, _vsdBigRecNormal, _vsdOverflow, _vsdEmptyWhite, _vsdEmptyNormal;
        private static double _vsdMaxWhite, _vsdMaxNormal;
        private static long _vsdLogUntil = -1;
        private static readonly List<string> _vsdLines = new();

        // ---- texture slot set for one stage, consumed by another ----
        private static long _mismatchCount;
        private static readonly Dictionary<string, long> _mismatchByProgram = new();
        private static readonly List<string> _mismatchSamples = new();

        public static void NoteStageMismatch(string label, int binding, ResourceStages needed, ShaderStage found, string tex)
        {
            lock (_mismatchByProgram)
            {
                _mismatchCount++;
                string key = (label != null && label.Length > 6 ? label[..6] : label ?? "?") + $" b{binding} need={needed} got={found}";
                _mismatchByProgram[key] = _mismatchByProgram.TryGetValue(key, out long c) ? c + 1 : 1;
                if (_mismatchSamples.Count < 12) { _mismatchSamples.Add($"{key} tex={tex} f{System.Threading.Interlocked.Read(ref _frame)}"); }
            }
            Seq($"MM:{(label != null && label.Length > 6 ? label[..6] : label)}b{binding}");
        }

        private static string MismatchReport()
        {
            lock (_mismatchByProgram)
            {
                StringBuilder sb = new();
                sb.Append($"texref stage mismatches: {_mismatchCount}");
                int n = 0;
                foreach (KeyValuePair<string, long> kv in _mismatchByProgram) { if (n++ < 16) { sb.Append("\n  mm ").Append(kv.Key).Append(" x").Append(kv.Value); } }
                foreach (string s in _mismatchSamples) { sb.Append("\n  mm-sample ").Append(s); }
                return sb.ToString();
            }
        }

        // ---- who writes the flare's count slot ----
        private static IntPtr _flareCountPtr; private static int _flareCountOff = -1;

        public static unsafe void NoteFlareCountBinding(IntPtr ptr, int off, MTLBuffer readBuf, int readOff, bool mirrored)
        {
            if (!Enabled) { return; }
            _flareCountPtr = ptr; _flareCountOff = off;
            if (!mirrored) { _flareCountBuf = readBuf; _flareCountReadOff = readOff; }
            int cpu = 0;
            try { if (readBuf.NativePtr != IntPtr.Zero && readBuf.Contents != IntPtr.Zero) { cpu = *(int*)((byte*)readBuf.Contents + readOff); } } catch (Exception) { }
            Seq($"D:flare cnt={cpu}{(mirrored ? "m" : "")}");
        }

        // Poll the CPU-visible value of the flare's count slot at every draw; a change between
        // two draws is a GPU write landing (the CPU never writes it), and its position in the
        // sequence says which pass of which frame produced it.
        private static MTLBuffer _flareCountBuf; private static int _flareCountReadOff; private static int _cntLast = int.MinValue;
        public static unsafe void PollCount()
        {
            if (!Enabled || _flareCountBuf.NativePtr == IntPtr.Zero) { return; }
            try
            {
                IntPtr c = _flareCountBuf.Contents;
                if (c == IntPtr.Zero) { return; }
                int v = *(int*)((byte*)c + _flareCountReadOff);
                if (v != _cntLast) { _cntLast = v; Seq($"CNT->{v}"); }
            }
            catch (Exception) { }
        }

        public static void NoteWriteTo(IntPtr ptr, int off, int size, string what)
        {
            if (!Enabled || _flareCountOff < 0 || ptr != _flareCountPtr) { return; }
            if (off < _flareCountOff + 32 && off + size > _flareCountOff)
            {
                Seq($"{what}@{off - _flareCountOff}/{size}");
            }
        }

        // ---- per-frame event sequence: pass boundaries, barriers, and the draws of interest ----
        // Kept for the last SeqRing frames; the sequences of a white frame and its predecessor
        // are logged when it is classified, plus two normal frames after the first white, so
        // the order of probe draw / barrier / pass end / flare draw can be read off directly.
        private const int SeqRing = 16;
        private static readonly StringBuilder[] _seq = new StringBuilder[SeqRing];
        private static readonly long[] _seqFrame = new long[SeqRing];
        private static readonly object _seqLock = new();
        private static int _seqDumpsWhite, _seqDumpsNormal;
        private static readonly List<string> _seqLines = new();

        public static void Seq(string ev)
        {
            if (!Enabled) { return; }
            lock (_seqLock)
            {
                long f = System.Threading.Interlocked.Read(ref _frame);
                int k = (int)(f % SeqRing);
                if (_seqFrame[k] != f || _seq[k] == null) { _seqFrame[k] = f; _seq[k] = new StringBuilder(); }
                if (_seq[k].Length < 48000) { _seq[k].Append(ev).Append(' '); }
            }
        }

        private static void ClassifySeq(long presentedFrame, bool flat)
        {
            if (!Enabled) { return; }
            bool dump = flat ? _seqDumpsWhite < 4 : (_seqDumpsNormal < 2 && _seqDumpsWhite > 0);
            if (!dump) { return; }
            lock (_seqLock)
            {
                for (long f = presentedFrame - 2; f <= presentedFrame; f++)
                {
                    int k = (int)(f % SeqRing);
                    if (_seqFrame[k] == f && _seq[k] != null && _seqLines.Count < 40)
                    {
                        _seqLines.Add($"f{presentedFrame} {(flat ? "WHITE" : "normal")} seq of f{f}: {_seq[k]}");
                    }
                }
                if (flat) { _seqDumpsWhite++; } else { _seqDumpsNormal++; }
            }
        }

        // ---- Ryujinx's own SamplesPassed reports (the occlusion-query results the guest reads) ----
        private const int CntRing = 256;
        private static readonly object _cntLock = new();
        private static readonly long[] _cntFrame = new long[CntRing];
        private static readonly ulong[] _cntValue = new ulong[CntRing];
        private static long _cntTotal, _cntNonZero, _cntBig;
        private static long _cntWhiteN, _cntNormalN; private static double _cntWhiteMaxSum, _cntNormalMaxSum; private static long _cntWhiteBig, _cntNormalBig;
        private static readonly List<string> _cntLines = new();

        public static void NoteCounterReport(ulong value)
        {
            if (!Enabled) { return; }
            lock (_cntLock)
            {
                int k = (int)(_cntTotal % CntRing);
                _cntFrame[k] = System.Threading.Interlocked.Read(ref _frame);
                _cntValue[k] = value;
                _cntTotal++;
                if (value != 0) { _cntNonZero++; }
                if (value > 1000) { _cntBig++; }
            }
        }

        private static void ClassifyCounterReport(long presentedFrame, bool flat)
        {
            if (!Enabled) { return; }
            lock (_cntLock)
            {
                ulong max = 0; int n = 0; StringBuilder vals = null;
                long avail = Math.Min(_cntTotal, CntRing);
                for (long t = _cntTotal - 1; t >= _cntTotal - avail; t--)
                {
                    int k = (int)(t % CntRing);
                    if (_cntFrame[k] > presentedFrame) { continue; }
                    if (_cntFrame[k] < presentedFrame - 3) { break; }
                    n++; if (_cntValue[k] > max) { max = _cntValue[k]; }
                    if (presentedFrame <= _vsdLogUntil) { vals ??= new StringBuilder(); if (vals.Length < 160) { vals.Append($" f{_cntFrame[k]}:{_cntValue[k]}"); } }
                }
                if (flat) { _cntWhiteN++; _cntWhiteMaxSum += max; if (max > 1000) { _cntWhiteBig++; } _vsdLogUntil = Math.Max(_vsdLogUntil, presentedFrame + 2); }
                else { _cntNormalN++; _cntNormalMaxSum += max; if (max > 1000) { _cntNormalBig++; } }
                if (presentedFrame <= _vsdLogUntil && _cntLines.Count < 120)
                {
                    _cntLines.Add($"f{presentedFrame} {(flat ? "WHITE " : "normal")} reports(3f)={n} max={max}{(vals != null ? " [" + vals + " ]" : "")}");
                }
            }
        }

        // ---- the game's 260x260 R8G8 sun-occlusion probe, as the guest reads it back ----
        // Every readback of that texture (either path) is summarised here with the presented
        // frame it happened in; at classification the most recent readbacks before the frame
        // are attributed to its outcome. If white frames follow readbacks that look like the
        // cleared probe, the readback is the carrier of the bogus count.
        private const int PrbRing = 64;
        private static readonly object _prbLock = new();
        private static readonly long[] _prbFrame = new long[PrbRing];
        private static readonly float[] _prbMeanR = new float[PrbRing], _prbMeanG = new float[PrbRing];
        private static readonly float[] _prbR255 = new float[PrbRing], _prbR0 = new float[PrbRing], _prbG255 = new float[PrbRing], _prbG0 = new float[PrbRing];
        private static readonly bool[] _prbBg = new bool[PrbRing];
        private static readonly byte[][] _prbRaw = new byte[PrbRing][];
        private static long _prbTotal, _prbFgCount, _prbBgCount;
        private static long _prbWhiteN, _prbNormalN, _prbWhiteNoRb, _prbNormalNoRb;
        private static double _prbWhiteMeanR, _prbNormalMeanR, _prbWhiteR255, _prbNormalR255, _prbWhiteR0, _prbNormalR0;
        private static int _prbDumpsWhite, _prbDumpsNormal;
        private static readonly List<string> _prbLines = new();

        private static readonly int[] _prbW = new int[PrbRing], _prbH = new int[PrbRing], _prbBpp = new int[PrbRing];
        private static readonly Dictionary<string, long> _prbKinds = new();

        // Any small 2D texture the guest reads back (<= 512 on a side, 1-4 bytes per texel):
        // the sun probe's size on this save is not known in advance. Channel 0 and 1 are
        // the first two bytes of each texel.
        public static void NoteProbeReadback(int width, int height, int bpp, ReadOnlySpan<byte> data, bool background)
        {
            if (!Enabled || width > 512 || height > 512 || bpp < 1 || bpp > 4 || data.Length < width * height * bpp) { return; }
            int n = width * height;
            long sumR = 0, sumG = 0, r255 = 0, r0 = 0, g255 = 0, g0 = 0;
            for (int i = 0; i < n; i++)
            {
                byte r = data[i * bpp], g = bpp > 1 ? data[i * bpp + 1] : (byte)0;
                sumR += r; sumG += g;
                if (r == 255) { r255++; } else if (r == 0) { r0++; }
                if (g == 255) { g255++; } else if (g == 0) { g0++; }
            }
            lock (_prbLock)
            {
                string kind = $"{width}x{height}x{bpp}B";
                _prbKinds[kind] = _prbKinds.TryGetValue(kind, out long kc) ? kc + 1 : 1;
                int k = (int)(_prbTotal % PrbRing);
                _prbW[k] = width; _prbH[k] = height; _prbBpp[k] = bpp;
                _prbFrame[k] = System.Threading.Interlocked.Read(ref _frame);
                _prbMeanR[k] = sumR / (float)n; _prbMeanG[k] = sumG / (float)n;
                _prbR255[k] = r255 / (float)n; _prbR0[k] = r0 / (float)n; _prbG255[k] = g255 / (float)n; _prbG0[k] = g0 / (float)n;
                _prbBg[k] = background;
                if (_prbRaw[k] == null || _prbRaw[k].Length != n * bpp) { _prbRaw[k] = new byte[n * bpp]; }
                data[..(n * bpp)].CopyTo(_prbRaw[k]);
                _prbTotal++;
                if (background) { _prbBgCount++; } else { _prbFgCount++; }
            }
        }

        private static string PrbKinds()
        {
            StringBuilder k = new();
            foreach (KeyValuePair<string, long> kv in _prbKinds) { if (k.Length > 0) { k.Append(", "); } k.Append(kv.Key).Append(':').Append(kv.Value); }
            return k.ToString();
        }

        private static string PrbDescribe(int k)
        {
            return $"rb@f{_prbFrame[k]}{(_prbBg[k] ? "bg" : "fg")} {_prbW[k]}x{_prbH[k]}x{_prbBpp[k]}B meanR {_prbMeanR[k]:F1} R255 {_prbR255[k]:P0} R0 {_prbR0[k]:P0} meanG {_prbMeanG[k]:F1} G255 {_prbG255[k]:P0} G0 {_prbG0[k]:P0}";
        }

        private static void ClassifyProbeReadback(long presentedFrame, bool flat)
        {
            if (!Enabled) { return; }
            lock (_prbLock)
            {
                int k1 = -1, k2 = -1;
                long avail = Math.Min(_prbTotal, PrbRing);
                for (long t = _prbTotal - 1; t >= _prbTotal - avail; t--)
                {
                    int k = (int)(t % PrbRing);
                    if (_prbFrame[k] <= presentedFrame)
                    {
                        if (k1 < 0) { k1 = k; } else { k2 = k; break; }
                    }
                }
                if (k1 < 0)
                {
                    if (flat) { _prbWhiteNoRb++; } else { _prbNormalNoRb++; }
                    return;
                }
                if (flat)
                {
                    _prbWhiteN++; _prbWhiteMeanR += _prbMeanR[k1]; _prbWhiteR255 += _prbR255[k1]; _prbWhiteR0 += _prbR0[k1];
                }
                else
                {
                    _prbNormalN++; _prbNormalMeanR += _prbMeanR[k1]; _prbNormalR255 += _prbR255[k1]; _prbNormalR0 += _prbR0[k1];
                }
                if (flat) { _vsdLogUntil = Math.Max(_vsdLogUntil, presentedFrame + 2); }
                if (presentedFrame <= _vsdLogUntil && _prbLines.Count < 120)
                {
                    _prbLines.Add($"f{presentedFrame} {(flat ? "WHITE " : "normal")} last {PrbDescribe(k1)}{(k2 >= 0 ? " | prev " + PrbDescribe(k2) : "")}");
                }
                bool dump = flat ? _prbDumpsWhite < 3 : (_prbDumpsNormal < 3 && _prbDumpsWhite > 0);
                if (dump && _prbRaw[k1] != null)
                {
                    try
                    {
                        System.IO.File.WriteAllBytes($"/tmp/probe_{(flat ? "white" : "normal")}_f{presentedFrame}_rb{_prbFrame[k1]}_{_prbW[k1]}x{_prbH[k1]}x{_prbBpp[k1]}B.raw", _prbRaw[k1]);
                        if (flat) { _prbDumpsWhite++; } else { _prbDumpsNormal++; }
                    }
                    catch (Exception) { }
                }
            }
        }

        // Fragment side (bit 32). Per window: record count = covered area in 32x32 cells,
        // and the maxima of each factor of the additive contribution.
        public static readonly bool FsDumpEnabled =
            ((int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_FLARE_CONST"), out int ffc) ? ffc : 0) & 32) != 0;
        private const int FsdRecords = 16384;
        private static uint _fsdLast;
        private static long _fsdWinWhite, _fsdWinNormal, _fsdRecWhite, _fsdRecNormal, _fsdOverflow;
        private static long _fsdMaxRecWinWhite, _fsdMaxRecWinNormal;
        private static double _fsdMaxOutWhite, _fsdMaxOutNormal, _fsdMaxIWhite, _fsdMaxINormal, _fsdMaxC3White, _fsdMaxC3Normal, _fsdMaxTexWhite, _fsdMaxTexNormal;
        private static readonly List<string> _fsdLines = new();

        private static unsafe void ClassifyFsDump(long presentedFrame, bool flat)
        {
            if (!FsDumpEnabled || VsDumpBuffer.NativePtr == IntPtr.Zero) { return; }
            uint now = *((uint*)VsDumpBuffer.Contents + 1);
            uint n = now - _fsdLast;
            if (n > FsdRecords) { _fsdOverflow++; _fsdLast = now; return; }
            float* recs = (float*)((byte*)VsDumpBuffer.Contents + 16 + VsdRecords * 80);
            double maxOut = -1, maxI = -1, maxC3 = -1, maxTex = -1; string worst = null;
            for (uint i = _fsdLast; i != now; i++)
            {
                float* r = recs + (i & (FsdRecords - 1)) * 16;
                double o = Math.Max(r[12], Math.Max(r[13], r[14])) * r[11];   // out.rgb * out.a = the additive contribution
                if (double.IsNaN(o)) { o = double.PositiveInfinity; }
                maxI = Math.Max(maxI, r[2]); maxC3 = Math.Max(maxC3, Math.Max(r[8], Math.Max(r[9], r[10]))); maxTex = Math.Max(maxTex, Math.Max(r[4], Math.Max(r[5], r[6])));
                if (o > maxOut)
                {
                    maxOut = o;
                    worst = $"px=({r[0]:F0},{r[1]:F0}) I={r[2]:G4} tex=({r[4]:G3},{r[5]:G3},{r[6]:G3},{r[3]:G3}) c3=({r[8]:G3},{r[9]:G3},{r[10]:G3},{r[7]:G3}) out=({r[12]:G3},{r[13]:G3},{r[14]:G3}) a={r[11]:G4} contrib={o:G4}";
                }
            }
            _fsdLast = now;
            if (flat)
            {
                _fsdWinWhite++; _fsdRecWhite += n; _fsdMaxRecWinWhite = Math.Max(_fsdMaxRecWinWhite, n);
                _fsdMaxOutWhite = Math.Max(_fsdMaxOutWhite, maxOut); _fsdMaxIWhite = Math.Max(_fsdMaxIWhite, maxI); _fsdMaxC3White = Math.Max(_fsdMaxC3White, maxC3); _fsdMaxTexWhite = Math.Max(_fsdMaxTexWhite, maxTex);
            }
            else
            {
                _fsdWinNormal++; _fsdRecNormal += n; _fsdMaxRecWinNormal = Math.Max(_fsdMaxRecWinNormal, n);
                _fsdMaxOutNormal = Math.Max(_fsdMaxOutNormal, maxOut); _fsdMaxINormal = Math.Max(_fsdMaxINormal, maxI); _fsdMaxC3Normal = Math.Max(_fsdMaxC3Normal, maxC3); _fsdMaxTexNormal = Math.Max(_fsdMaxTexNormal, maxTex);
            }
            if (presentedFrame <= _vsdLogUntil && _fsdLines.Count < 150)
            {
                _fsdLines.Add($"f{presentedFrame} {(flat ? "WHITE " : "normal")} cells={n} maxI={maxI:G4} maxC3={maxC3:G4} maxTex={maxTex:G4} worst: {worst}");
            }
        }

        private static unsafe void ClassifyVsDump(long presentedFrame, bool flat)
        {
            if (!VsDumpEnabled || VsDumpBuffer.NativePtr == IntPtr.Zero) { ClassifyFsDump(presentedFrame, flat); return; }
            uint now = *(uint*)VsDumpBuffer.Contents;
            uint n = now - _vsdLast;
            if (n > VsdRecords) { _vsdOverflow++; _vsdLast = now; return; }
            float* recs = (float*)((byte*)VsDumpBuffer.Contents + 16);
            double maxAbs = -1, maxI = -1; long big = 0; string worst = null;
            for (uint i = _vsdLast; i != now; i++)
            {
                float* r = recs + (i & (VsdRecords - 1)) * 20;
                maxI = Math.Max(maxI, r[3]);
                double a = Math.Max(Math.Abs(r[0]), Math.Abs(r[1]));
                if (double.IsNaN(a)) { a = double.PositiveInfinity; }
                if (a > 1.5) { big++; }
                if (a > maxAbs)
                {
                    maxAbs = a;
                    worst = $"pos=({r[0]:G5},{r[1]:G5}) vid={*(uint*)(r + 2)} I={r[3]:G4} c3[1]=({r[4]:G4},{r[5]:G4},{r[6]:G4},{r[7]:G4}) c3[2]=({r[8]:G4},{r[9]:G4},{r[10]:G4},{r[11]:G4}) c3[3]=({r[12]:G4},{r[13]:G4},{r[14]:G4},{r[15]:G4}) attr0=({r[16]:G4},{r[17]:G4}) uv=({r[18]:G4},{r[19]:G4})";
                }
            }
            _vsdLast = now;
            if (flat)
            {
                _vsdWinWhite++; _vsdRecWhite += n; _vsdBigRecWhite += big;
                if (big > 0) { _vsdBigWinWhite++; }
                if (n == 0) { _vsdEmptyWhite++; }
                if (maxAbs > _vsdMaxWhite) { _vsdMaxWhite = maxAbs; }
                _vsdLogUntil = presentedFrame + 2;
            }
            else
            {
                _vsdWinNormal++; _vsdRecNormal += n; _vsdBigRecNormal += big;
                if (big > 0) { _vsdBigWinNormal++; }
                if (n == 0) { _vsdEmptyNormal++; }
                if (maxAbs > _vsdMaxNormal) { _vsdMaxNormal = maxAbs; }
            }
            if (presentedFrame <= _vsdLogUntil && _vsdLines.Count < 150)
            {
                _vsdLines.Add($"f{presentedFrame} {(flat ? "WHITE " : "normal")} records={n} big={big} maxI={maxI:G4} max|pos|={maxAbs:G4} worst: {worst}");
            }
            ClassifyFsDump(presentedFrame, flat);
        }

        private static void Report()
        {
            StringBuilder sb = new();

            if (_mismatchCount != 0)
            {
                sb.Append(MismatchReport()).Append('\n');
            }
            if (_seqLines.Count != 0)
            {
                foreach (string l in _seqLines) { sb.Append("  seq ").Append(l).Append('\n'); }
            }
            if (_cntTotal != 0)
            {
                sb.Append($"counters: reports {_cntTotal} nonzero {_cntNonZero} big(>1000) {_cntBig}; max-in-3-frames mean white {(_cntWhiteN > 0 ? _cntWhiteMaxSum / _cntWhiteN : 0):F0} (big {_cntWhiteBig}/{_cntWhiteN}) normal {(_cntNormalN > 0 ? _cntNormalMaxSum / _cntNormalN : 0):F0} (big {_cntNormalBig}/{_cntNormalN})\n");
                foreach (string l in _cntLines) { sb.Append("  cnt ").Append(l).Append('\n'); }
            }
            if (_prbTotal != 0 || _prbWhiteNoRb + _prbNormalNoRb != 0)
            {
                sb.Append($"probe260: kinds [{PrbKinds()}] readbacks {_prbTotal} (bg {_prbBgCount} fg {_prbFgCount}); frames white {_prbWhiteN} (no readback yet {_prbWhiteNoRb}) normal {_prbNormalN} (no rb {_prbNormalNoRb}); last-readback meanR white {(_prbWhiteN > 0 ? _prbWhiteMeanR / _prbWhiteN : 0):F1} normal {(_prbNormalN > 0 ? _prbNormalMeanR / _prbNormalN : 0):F1}; R255 white {(_prbWhiteN > 0 ? _prbWhiteR255 / _prbWhiteN : 0):P1} normal {(_prbNormalN > 0 ? _prbNormalR255 / _prbNormalN : 0):P1}; R0 white {(_prbWhiteN > 0 ? _prbWhiteR0 / _prbWhiteN : 0):P1} normal {(_prbNormalN > 0 ? _prbNormalR0 / _prbNormalN : 0):P1}\n");
                foreach (string l in _prbLines) { sb.Append("  prb ").Append(l).Append('\n'); }
            }
            if (VsDumpEnabled)
            {
                sb.Append($"vsdump: windows white {_vsdWinWhite} (with |pos|>1.5: {_vsdBigWinWhite}, empty {_vsdEmptyWhite}) normal {_vsdWinNormal} (big {_vsdBigWinNormal}, empty {_vsdEmptyNormal}); records white {_vsdRecWhite} (big {_vsdBigRecWhite}) normal {_vsdRecNormal} (big {_vsdBigRecNormal}); max|pos| white {_vsdMaxWhite:G4} normal {_vsdMaxNormal:G4}; overflow {_vsdOverflow}");
                foreach (string l in _vsdLines) { sb.Append("\n  vsd ").Append(l); }
                if (FsDumpEnabled)
                {
                    sb.Append($"\nfsdump: windows white {_fsdWinWhite} normal {_fsdWinNormal}; cells/window white {(_fsdWinWhite > 0 ? (double)_fsdRecWhite / _fsdWinWhite : 0):F1} (max {_fsdMaxRecWinWhite}) normal {(_fsdWinNormal > 0 ? (double)_fsdRecNormal / _fsdWinNormal : 0):F1} (max {_fsdMaxRecWinNormal}); max contrib white {_fsdMaxOutWhite:G4} normal {_fsdMaxOutNormal:G4}; max I white {_fsdMaxIWhite:G4} normal {_fsdMaxINormal:G4}; max fp_c3 white {_fsdMaxC3White:G4} normal {_fsdMaxC3Normal:G4}; max tex white {_fsdMaxTexWhite:G4} normal {_fsdMaxTexNormal:G4}; overflow {_fsdOverflow}");
                    foreach (string l in _fsdLines) { sb.Append("\n  fsd ").Append(l); }
                }
                sb.Append('\n');
            }

            sb.Append($"uploadcorr: classified={_flatFrames + _normalFrames} flat={_flatFrames} normal={_normalFrames} dropped={_dropped}");
            sb.Append($" | luma flat {(_flatFrames > 0 ? _lumaFlatSum / _flatFrames : 0):F0}, normal {(_normalFrames > 0 ? _lumaNormalSum / _normalFrames : 0):F0}");
            sb.Append($" | shape flat min {(_flatFrames > 0 ? _flatMinSum / _flatFrames : 0):F0} max {(_flatFrames > 0 ? _flatMaxSum / _flatFrames : 0):F0} sd {(_flatFrames > 0 ? _flatSdSum / _flatFrames : 0):F1} sat {(_flatFrames > 0 ? _flatSatSum / _flatFrames : 0):F1}/{Pixels}");
            sb.Append($", normal min {(_normalFrames > 0 ? _normalMinSum / _normalFrames : 0):F0} max {(_normalFrames > 0 ? _normalMaxSum / _normalFrames : 0):F0} sd {(_normalFrames > 0 ? _normalSdSum / _normalFrames : 0):F1}");
            sb.Append($" | input distinct flat {(_flatInputFrames > 0 ? _flatInputDistinctSum / _flatInputFrames : 0):F2}/{Pixels} over {_flatInputFrames}");
            sb.Append($", normal {(_normalInputFrames > 0 ? _normalInputDistinctSum / _normalInputFrames : 0):F2}/{Pixels} over {_normalInputFrames}");

            for (int c = 0; c < MaxCbSlots; c++)
            {
                if (_wFlatN[c] == 0 && _wNormalN[c] == 0) { continue; }
                sb.Append($"\n  blit w[slot{c}]: flat mean {(_wFlatN[c] > 0 ? _wFlatSum[c] / _wFlatN[c] : 0):G4} min {(_wFlatN[c] > 0 ? _wFlatMin[c] : 0):G4} n={_wFlatN[c]}  normal mean {(_wNormalN[c] > 0 ? _wNormalSum[c] / _wNormalN[c] : 0):G4} min {(_wNormalN[c] > 0 ? _wNormalMin[c] : 0):G4} n={_wNormalN[c]}");
            }

            sb.Append($"\n  COMPOSITE(N)->PRESENT(N+1) CB ORDER: same-cb flat {_cpSameFlat} normal {_cpSameNormal} | composite committed FIRST flat {_cpOrderedFlat} normal {_cpOrderedNormal} | INVERTED (present first) flat {_cpInvertedFlat} normal {_cpInvertedNormal} | unknown flat {_cpUnkFlat} normal {_cpUnkNormal}");
            sb.Append($"\n  WRITER/BLIT PAIR: same-cb flat {_pairSameCbFlat} normal {_pairSameCbNormal} | ordered flat {_pairOrderedFlat} normal {_pairOrderedNormal} | INVERTED flat {_pairInvertedFlat} normal {_pairInvertedNormal} | uncommitted flat {_pairUncommittedFlat} normal {_pairUncommittedNormal}");
            sb.Append($"\n  SAMPLER-UNIT vs read-path disagree: flat {_sampVsReadFlat}/{_sampVsReadFlat + _sampSameFlat}, normal {_sampVsReadNormal}/{_sampVsReadNormal + _sampSameNormal}");
            sb.Append($"\n  TWO-PATH sampler-vs-blit disagree: flat {_twoPathDisagreeFlat}/{_twoPathDisagreeFlat + _twoPathAgreeFlat}, normal {_twoPathDisagreeNormal}/{_twoPathDisagreeNormal + _twoPathAgreeNormal}");
            sb.Append($"\n  MAGENTA canary frames: {_magentaFrames}");
            sb.Append($"\n  blit PSO fresh-this-frame: flat {_freshFlat}/{_freshFlat + _staleFlat}, normal {_freshNormal}/{_freshNormal + _staleNormal}");
            sb.Append($"\n  WITNESS input-after-blit: flat uniform {_witFlatWhiteAfter} picture {_witFlatPictureAfter} (eq-with-present {(_witFlatEqN > 0 ? _witFlatEqSum / _witFlatEqN : 0):F1}/25), normal uniform {_witNormalWhiteAfter} picture {_witNormalPictureAfter}");
            sb.Append($"\n  handle gen swap between write and read: flat {_genSwapFlat}/{_genSwapFlat + _genSameFlat}, normal {_genSwapNormal}/{_genSwapNormal + _genSameNormal}");
            sb.Append($"\n  blit serial: flat match {_matchFlat} mismatch {_mismatchFlat}, normal match {_matchNormal} mismatch {_mismatchNormal}");

            foreach (KeyValuePair<string, (long Flat, long Normal)> g2 in _imgStats)
            {
                sb.Append($"\n  image-on-presented [{g2.Key}]: flat {g2.Value.Flat}, normal {g2.Value.Normal}");
            }

            foreach (KeyValuePair<string, (long Flat, long Normal)> r2 in _rasterStats)
            {
                sb.Append($"\n  blit raster {r2.Key}: flat {r2.Value.Flat}, normal {r2.Value.Normal}");
            }

            foreach (KeyValuePair<string, (long Flat, long Normal)> a2 in _attribStats)
            {
                sb.Append($"\n  vertex attrib0 {a2.Key}: flat {a2.Value.Flat}, normal {a2.Value.Normal}");
            }

            foreach (KeyValuePair<string, (long Flat, long Normal)> j in _indexStats)
            {
                sb.Append($"\n  blit indices [{j.Key}]: flat {j.Value.Flat}, normal {j.Value.Normal}");
            }

            foreach (KeyValuePair<string, (long Flat, long Normal)> d in _drawParamStats)
            {
                sb.Append($"\n  blit draw {d.Key}: flat {d.Value.Flat}, normal {d.Value.Normal}");
            }

            sb.Append($" | blit stride: flat {(_flatStrideN > 0 ? _flatStrideSum / _flatStrideN : 0):F1} [{(_flatStrideN > 0 ? _flatStrideMin : 0)},{(_flatStrideN > 0 ? _flatStrideMax : 0)}] over {_flatStrideN}, normal {(_normalStrideN > 0 ? _normalStrideSum / _normalStrideN : 0):F1} over {_normalStrideN}");
            sb.Append($" | blit vertex spread (distinct of 4): flat {(_flatVertN > 0 ? _flatVertSum / _flatVertN : 0):F2} over {_flatVertN}, normal {(_normalVertN > 0 ? _normalVertSum / _normalVertN : 0):F2} over {_normalVertN}");
            sb.Append($" | composite draws/frame: flat {(_flatDrawN > 0 ? _flatDrawSum / _flatDrawN : 0):F2}, normal {(_normalDrawN > 0 ? _normalDrawSum / _normalDrawN : 0):F2}");
            sb.Append($" | sampler fence: asked {_fenceAsked}, signalled {_fenceReady}");
            {
                List<KeyValuePair<string, (long Flat, long Normal)>> sw = new(_sceneWriterStats);
                sw.Sort((x, y) => (y.Value.Flat + y.Value.Normal).CompareTo(x.Value.Flat + x.Value.Normal));
                for (int i = 0; i < sw.Count && i < 10; i++)
                {
                    long tot = sw[i].Value.Flat + sw[i].Value.Normal;
                    sb.Append($"\n  SCENE-800 writers: flat {sw[i].Value.Flat,6} / {tot,6} = {(tot > 0 ? 100.0 * sw[i].Value.Flat / tot : 0),5:F1}%  {sw[i].Key}");
                }
            }
            {
                List<KeyValuePair<string, (long Flat, long Normal)>> rw = new(_rtWriterStats);
                rw.Sort((x, y) => (y.Value.Flat + y.Value.Normal).CompareTo(x.Value.Flat + x.Value.Normal));
                for (int i = 0; i < rw.Count && i < 8; i++)
                {
                    long tot = rw[i].Value.Flat + rw[i].Value.Normal;
                    sb.Append($"\n  LAST-RT writers: flat {rw[i].Value.Flat,6} / {tot,6} = {(tot > 0 ? 100.0 * rw[i].Value.Flat / tot : 0),5:F1}%  {rw[i].Key}");
                }
            }
            foreach (KeyValuePair<string, (long Flat, long Normal)> mb in _modByStats)
            {
                sb.Append($"\n  PRESENT-RANGE modified by [{mb.Key}]: flat {mb.Value.Flat} normal {mb.Value.Normal}");
            }
            sb.Append($"\n  PRESENT reads the half the composite WRITES this frame: same-half flat {_rwSameHalfFlat} normal {_rwSameHalfNormal} | other-half flat {_rwOtherHalfFlat} normal {_rwOtherHalfNormal}");
            sb.Append($"\n  SAME-STORAGE painted->presented: [pic->WHITE] flat {_samePicToWhiteFlat} | [white->white] flat {_sameWhiteToWhiteFlat} | [pic->pic] normal {_samePicToPicNormal} | [white->pic] normal {_sameWhiteToPicNormal} | unpaired flat {_sameUnpairedFlat} normal {_sameUnpairedNormal}");
            Logger.Warning?.PrintMsg(LogClass.Gpu,
                $"  PRE-STAGE input (compute read of the pre-probe program's input right BEFORE its pass, same CB): white/uniform flat {_preWhiteFlat} normal {_preWhiteNormal} | picture flat {_prePicFlat} normal {_prePicNormal} | unprobed flat {_preUnprobedFlat} normal {_preUnprobedNormal} | probes {_preProbes} | distinct-of-25 flat {(_preFlatMeanN > 0 ? (double)_preDistinctFlatSum / _preFlatMeanN : 0):F2} normal {(_preNormalMeanN > 0 ? (double)_preDistinctNormalSum / _preNormalMeanN : 0):F2} | mean linear luma flat {(_preFlatMeanN > 0 ? _preFlatMeanSum / _preFlatMeanN : 0):F3} normal {(_preNormalMeanN > 0 ? _preNormalMeanSum / _preNormalMeanN : 0):F3} | sampler path: white flat {_preSampWhiteFlat} normal {_preSampWhiteNormal} picture flat {_preSampPicFlat} normal {_preSampPicNormal}");
                {
                    StringBuilder wsb = new();
                    foreach (KeyValuePair<string, (long Flat, long Normal)> kv in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(_inputWriterStats, x => x.Value.Flat + x.Value.Normal), 10))
                    {
                        long tot = kv.Value.Flat + kv.Value.Normal;
                        wsb.Append($"\n      [{kv.Key}] flat {kv.Value.Flat} normal {kv.Value.Normal} ({(tot > 0 ? 100.0 * kv.Value.Flat / tot : 0):F1}% white)");
                    }
                    StringBuilder dsb = new();
                    foreach (KeyValuePair<string, (long Flat, long Normal)> kv in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(_inputDescStats, x => x.Value.Flat + x.Value.Normal), 6))
                    {
                        dsb.Append($"\n      [{kv.Key}] flat {kv.Value.Flat} normal {kv.Value.Normal}");
                    }
                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"  COMPOSITE INPUT writers this frame (program label prefixes, in draw order) by outcome:{wsb}\n  composite input storage:{dsb}\n  stage[{_stageLabel}]: arms {_stageArms} samples {_stageSamples} written white {_stageDumpsWritten} normal {_stageDumpsNormal} lookup misses {_stageMisses}{ConstStatsText()}{FlareMulText()}{TinyPreText()}{VtxStatsText()}{PerDrawStatsText()}{PerDrawSamplerText()}{ArgTableText()}{SceneSeriesText()}{TinySeriesText()}{StageDrawStatsText()}\n  stage cb GPU-vs-CPU: equal flat {_cbEqFlat} normal {_cbEqNormal} | DIFFER flat {_cbDiffFlat} normal {_cbDiffNormal} | unknown flat {_cbUnknownFlat} normal {_cbUnknownNormal}{CbStatsText()}");
                }
            sb.Append($"\n  COMPOSITE OUTPUT right after its draw (frame N) vs outcome (N+1): [white@draw] flat {_outWhiteAtDrawFlat} normal {_outWhiteAtDrawNormal} | [picture@draw] flat {_outPicAtDrawFlat} normal {_outPicAtDrawNormal}");
            sb.Append($"\n  TOPOLOGY/frame: replaceView flat {(_topoFlatN > 0 ? _rvFlat / _topoFlatN : 0):F3} normal {(_topoNormalN > 0 ? _rvNormal / _topoNormalN : 0):F3} | newTexture flat {(_topoFlatN > 0 ? _ntFlat / _topoFlatN : 0):F3} normal {(_topoNormalN > 0 ? _ntNormal / _topoNormalN : 0):F3}");
            sb.Append($"\n  RT-vs-SRC at present: [RT picture, SRC white] flat {_rtPicSrcWhiteFlat} normal {_rtPicSrcWhiteNormal} | [both picture] flat {_bothPicFlat} normal {_bothPicNormal} | [both white] flat {_bothWhiteFlat} normal {_bothWhiteNormal} | [RT white, SRC pic] flat {_rtWhiteSrcPicFlat} normal {_rtWhiteSrcPicNormal}");
            sb.Append($"\n  PRESENT src is a recent drawable: yes flat {_srcWasRecentDstFlat} normal {_srcWasRecentDstNormal} | no flat {_srcNotDstFlat} normal {_srcNotDstNormal}");
            foreach (KeyValuePair<string, (long Flat, long Normal)> t3 in _tripleStats)
            {
                sb.Append($"\n  PRESENT {t3.Key}: flat {t3.Value.Flat}, normal {t3.Value.Normal}");
            }
            sb.Append($" | BLIT reads the pass-before-it's attachment: match flat {_blitPrevMatchFlat} normal {_blitPrevMatchNormal} | MISMATCH flat {_blitPrevMismatchFlat} normal {_blitPrevMismatchNormal}");
            sb.Append($" | PRESENT src == last 1080p RGBA8 RT: match flat {_rgbaMatchFlat} normal {_rgbaMatchNormal} | mismatch flat {_rgbaMismFlat} normal {_rgbaMismNormal} | no-rgba8-rt flat {_rgbaNoneFlat} normal {_rgbaNoneNormal}");
            sb.Append($" | PRESENT src == last full-res RT: match flat {_presentMatchFlat} normal {_presentMatchNormal} | MISMATCH flat {_presentMismatchFlat} normal {_presentMismatchNormal}");
            sb.Append($" | argbuf overwritten by frame end: flat {_flatArgMismatch}/{_flatArgChecked}, normal {_normalArgMismatch}/{_normalArgChecked}");
            sb.Append($" | out-of-order commits/frame: flat {(_flatOooN > 0 ? _flatOooSum / _flatOooN : 0):F2}, normal {(_normalOooN > 0 ? _normalOooSum / _normalOooN : 0):F2}");
            sb.Append($" | composite residency decls: flat {(_flatResidencyN > 0 ? _flatResidencySum / _flatResidencyN : 0):F2} over {_flatResidencyN}, normal {(_normalResidencyN > 0 ? _normalResidencySum / _normalResidencyN : 0):F2} over {_normalResidencyN}");
            sb.Append($" | input written after the composite read it: flat {_flatLateWrites}/{_flatFrames}, normal {_normalLateWrites}/{_normalFrames}");

            foreach ((string who, (long Flat, long Normal) w) in _lateWriterStats)
            {
                sb.Append($"\n  late writer {who}: flat {w.Flat}, normal {w.Normal}");
            }

            for (int c = 0; c < MaxCbSlots; c++)
            {
                if (_cbFlatN[c] == 0 && _cbNormalN[c] == 0)
                {
                    continue;
                }

                sb.Append($"\n  cb{c}: flat[");
                for (int i = 0; i < 4; i++)
                {
                    sb.Append($"{(_cbFlatN[c] > 0 ? _cbFlatSum[c * 4 + i] / _cbFlatN[c] : 0):G6}{(i < 3 ? " " : string.Empty)}");
                }
                sb.Append($"] n={_cbFlatN[c]}  normal[");
                for (int i = 0; i < 4; i++)
                {
                    sb.Append($"{(_cbNormalN[c] > 0 ? _cbNormalSum[c * 4 + i] / _cbNormalN[c] : 0):G6}{(i < 3 ? " " : string.Empty)}");
                }
                sb.Append($"] n={_cbNormalN[c]}");

                // Extrema, because a mean cannot separate "always this value on flat
                // frames" from "flat frames are the subset that happened to have it".
                if (_cbFlatN[c] > 0 && _cbNormalN[c] > 0)
                {
                    sb.Append("\n        range flat");
                    for (int i = 0; i < 4; i++)
                    {
                        sb.Append($" [{_cbFlatMin[c * 4 + i]:G7},{_cbFlatMax[c * 4 + i]:G7}]");
                    }
                    sb.Append("  normal");
                    for (int i = 0; i < 4; i++)
                    {
                        sb.Append($" [{_cbNormalMin[c * 4 + i]:G7},{_cbNormalMax[c * 4 + i]:G7}]");
                    }
                }
            }
            sb.Append($" | upload: flat {_flatWithUpload}/{_flatFrames}, normal {_normalWithUpload}/{_normalFrames}");
            sb.Append($" | uploadOntoRT: flat {_flatWithUploadOntoRt}, normal {_normalWithUploadOntoRt}");
            sb.Append($" | copy: flat {_flatWithCopy}/{_flatFrames}, normal {_normalWithCopy}/{_normalFrames}");
            sb.Append($" | copyOntoRT: flat {_flatWithCopyOntoRt}, normal {_normalWithCopyOntoRt}");

            sb.Append($"\n  scene bindings seen: {_bindingStats.Count}, frames with none: {_framesWithNoBinding}");

            foreach (KeyValuePair<string, (long Flat, long Normal)> pair in _bindingStats)
            {
                sb.Append($"\n    flat {pair.Value.Flat,6}  normal {pair.Value.Normal,6}   {pair.Key}");
            }

            sb.Append($"\n  FLASHES: {_runs} runs, {(_runs > 0 ? (double)_runFrames / _runs : 0):F2} frames each, " +
                      $"longest {_longestRun}, mean gap {(_gaps > 0 ? (double)_gapSum / _gaps : 0):F1} frames" +
                      $" -> {(_runs > 0 && _flatFrames + _normalFrames > 0 ? _runs * 30.0 / (_flatFrames + _normalFrames) : 0):F2} flashes/sec at 30fps");

            sb.Append("\n  presented storage, by outcome:");

            foreach (KeyValuePair<string, (long Flat, long Normal)> pair in _presentIdentity)
            {
                sb.Append($"\n    flat {pair.Value.Flat,6}  normal {pair.Value.Normal,6}   root={pair.Key}");
            }

            sb.Append("\n  what drew into the presented storage, by outcome:");

            List<KeyValuePair<string, (long Flat, long Normal)>> writers = new(_writerStats);
            writers.Sort((a, b) => (b.Value.Flat + b.Value.Normal).CompareTo(a.Value.Flat + a.Value.Normal));

            for (int i = 0; i < writers.Count && i < 10; i++)
            {
                long total = writers[i].Value.Flat + writers[i].Value.Normal;
                sb.Append($"\n    flat {writers[i].Value.Flat,6} / {total,6} = {(total > 0 ? 100.0 * writers[i].Value.Flat / total : 0),5:F1}%  {writers[i].Key}");
            }

            sb.Append("\n  frames since that storage was last an attachment (-1 = never):");

            foreach (KeyValuePair<long, (long Flat, long Normal)> pair in _presentAge)
            {
                sb.Append($"\n    flat {pair.Value.Flat,6}  normal {pair.Value.Normal,6}   age={pair.Key}");
            }

            sb.Append($"\n  scene-sampling signatures: {_signatureStats.Count}");

            List<KeyValuePair<string, (long Flat, long Normal)>> top = new(_signatureStats);
            top.Sort((a, b) => (b.Value.Flat + b.Value.Normal).CompareTo(a.Value.Flat + a.Value.Normal));

            for (int i = 0; i < top.Count && i < 12; i++)
            {
                long total = top[i].Value.Flat + top[i].Value.Normal;
                sb.Append($"\n    flat {top[i].Value.Flat,6} / {total,6} = {(total > 0 ? 100.0 * top[i].Value.Flat / total : 0),5:F1}%  {top[i].Key}");
            }

            foreach (KeyValuePair<ulong, (long Flat, long Normal)> pair in _shapeStats)
            {
                int width = (int)(pair.Key >> 40);
                int height = (int)((pair.Key >> 16) & 0xFFFFFF);
                bool isCopy = ((pair.Key >> 8) & 0xFF) == 2;
                bool ontoRt = (pair.Key & 0xFF) != 0;

                sb.Append($"\n  {(isCopy ? "copy" : "upload")}{(ontoRt ? "+RT" : "")} {width}x{height}: flat {pair.Value.Flat}, normal {pair.Value.Normal}");
            }

            Logger.Warning?.PrintMsg(LogClass.Gpu, sb.ToString());
        }
    }
}
