using Ryujinx.Common.Logging;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using System;
using System.Runtime.Versioning;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Counts the fragments that actually reach each pass into the full resolution
    /// composite, using Metal's visibility result buffer.
    ///
    /// Every instrument built for the white flash so far measures either what was bound
    /// or what the pixels became: pass counts, draw counts, sampled texture identities,
    /// constant buffer contents, per pass pixel samples, in-shader value markers. All of
    /// them report a flat frame and the frame before it as identical, and the notes
    /// already contain the reading that would explain every one of those null results at
    /// once - that on a flat frame these draws produce no fragments, leaving the frame
    /// showing whatever the load action brought in. That reading has never been tested,
    /// because nothing here measures whether a draw covered anything.
    ///
    /// The visibility counter is hardware. The GPU writes it after the pass has run, so it
    /// cannot be fooled by a stale state cache, by a texture view answering to several
    /// pointers, or by shader arithmetic - the three traps that produced retracted
    /// conclusions in this investigation. It also compares a flat frame against its own
    /// immediate predecessor inside a single run, so there are no arms, no camera angle
    /// confound, and no sample size to argue about. One flat frame settles it:
    ///
    ///   coverage collapses -> the draws rasterise nothing, and the cause is geometry or
    ///     fixed function state: scissor, viewport, depth test, vertex data
    ///   coverage unchanged -> the draws cover the frame and write white, and the cause is
    ///     what they read
    ///
    /// The scissor and viewport in force when each pass opened are recorded beside the
    /// count, so if it is the first of those, the same run names which one.
    ///
    /// Enable with RYUJINX_METAL_COVERAGE=1. While it is on, a watched pass writes this
    /// buffer instead of the occlusion query buffer, so guest SamplesPassed queries lose
    /// what those passes would have contributed. They are post-process passes, but that is
    /// a real change to rendering, which is why this is off by default.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class CoverageProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_COVERAGE") == "1";

        // Matches the present probe's slot count, and Pipeline hands out the same
        // _presentCount % 4, so a slot here lines up with the frame that probe classified.
        private const int Slots = 4;

        // The composite takes four passes per frame in every capture so far; the spare
        // room reports itself through Overflow rather than silently dropping passes.
        private const int MaxPasses = 12;
        private const int ResultBytes = 8;

        private static MTLBuffer _buf;

        private struct PassInfo
        {
            public int TargetWidth;
            public int TargetHeight;
            public int ScissorX;
            public int ScissorY;
            public int ScissorWidth;
            public int ScissorHeight;
            public int ViewportWidth;
            public int ViewportHeight;
            public MTLCompareFunction DepthCompare;
        }

        private static readonly PassInfo[][] _passes = CreatePasses();
        private static readonly int[] _counts = new int[Slots];
        private static readonly int[] _overflow = new int[Slots];

        private static PassInfo[][] CreatePasses()
        {
            PassInfo[][] slots = new PassInfo[Slots][];

            for (int i = 0; i < Slots; i++)
            {
                slots[i] = new PassInfo[MaxPasses];
            }

            return slots;
        }

        public static unsafe void Init(MTLDevice device)
        {
            if (!Enabled)
            {
                return;
            }

            int bytes = Slots * MaxPasses * ResultBytes;

            _buf = device.NewBuffer((ulong)bytes, MTLResourceOptions.ResourceStorageModeShared);

            // A new buffer reads as zero, and zero is a meaningful answer here. Seed the
            // sentinel so the first frames - which no present has primed yet - report
            // PENDING rather than a coverage of none.
            new Span<byte>((void*)_buf.Contents, bytes).Fill(0xFF);

            // Say so in the log. Several results in these notes were read as negatives
            // when the diagnostic they relied on had never been switched on.
            Logger.Warning?.PrintMsg(LogClass.Gpu,
                $"CoverageProbe: armed (buf=0x{_buf.NativePtr:X}, {MaxPasses} passes/frame) - " +
                "watched passes write this instead of the occlusion query buffer");
        }

        /// <summary>
        /// Claims a visibility result slot for a pass whose colour target 0 is the watched
        /// composite. Returns false for every other pass, which then keeps the occlusion
        /// query behaviour it had.
        /// </summary>
        public static bool TryTakePass(
            int frameSlot,
            Texture target,
            MTLScissorRect scissor,
            MTLViewport viewport,
            MTLCompareFunction depthCompare,
            MTLRenderPassDescriptor descriptor,
            out ulong offset)
        {
            offset = 0;

            if (!Enabled || _buf.NativePtr == IntPtr.Zero || !HdrPassProbe.IsWatchedTarget(target))
            {
                return false;
            }

            int slot = frameSlot % Slots;
            int index = _counts[slot];

            if (index >= MaxPasses)
            {
                _overflow[slot]++;

                return false;
            }

            _counts[slot] = index + 1;

            _passes[slot][index] = new PassInfo
            {
                TargetWidth = target.Width,
                TargetHeight = target.Height,
                ScissorX = (int)scissor.x,
                ScissorY = (int)scissor.y,
                ScissorWidth = (int)scissor.width,
                ScissorHeight = (int)scissor.height,
                ViewportWidth = (int)viewport.width,
                ViewportHeight = (int)viewport.height,
                DepthCompare = depthCompare,
            };

            descriptor.VisibilityResultBuffer = _buf;
            offset = (ulong)((slot * MaxPasses + index) * ResultBytes);

            return true;
        }

        /// <summary>
        /// Primes the slot the next frame will write, in the frame's own command buffer so
        /// the write is ordered ahead of that frame's passes rather than racing them from
        /// the CPU.
        ///
        /// The fill value is all ones, not zero, and that choice is the whole point of the
        /// instrument. Zero is the answer being looked for - a pass that covered nothing -
        /// and it is also what an unwritten slot would hold, so a readback taken before the
        /// GPU finished would manufacture exactly the result this is meant to detect. With
        /// a sentinel the two are distinguishable: Pending means the count has not landed
        /// yet and the reading must be thrown away.
        /// </summary>
        public static void OnPresent(CommandBufferScoped cbs, int nextFrameSlot)
        {
            if (!Enabled || _buf.NativePtr == IntPtr.Zero)
            {
                return;
            }

            int slot = nextFrameSlot % Slots;

            _counts[slot] = 0;
            _overflow[slot] = 0;

            MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();

            blit.FillBuffer(
                _buf,
                new NSRange
                {
                    location = (ulong)(slot * MaxPasses * ResultBytes),
                    length = (ulong)(MaxPasses * ResultBytes),
                },
                0xFF);
        }

        public static unsafe string Describe(int slot)
        {
            if (!Enabled || _buf.NativePtr == IntPtr.Zero)
            {
                return "off";
            }

            slot %= Slots;

            int count = _counts[slot];

            if (count == 0)
            {
                return "no watched pass";
            }

            ulong* results = (ulong*)_buf.Contents;
            StringBuilder sb = new();

            for (int i = 0; i < count; i++)
            {
                ref PassInfo p = ref _passes[slot][i];

                ulong result = results[slot * MaxPasses + i];

                sb.Append($" p{i}:cov={(result == ulong.MaxValue ? "PENDING" : result.ToString())}");
                sb.Append($",sc={p.ScissorX},{p.ScissorY},{p.ScissorWidth}x{p.ScissorHeight}");
                sb.Append($",vp={p.ViewportWidth}x{p.ViewportHeight}");
                sb.Append($",rt={p.TargetWidth}x{p.TargetHeight}");

                // Counting mode counts samples that pass depth and stencil, so a zero has
                // two readings. With the compare function on the line, "nothing rasterised"
                // and "rasterised and then rejected" stay apart.
                sb.Append($",dcmp={p.DepthCompare}");
            }

            if (_overflow[slot] > 0)
            {
                sb.Append($" (+{_overflow[slot]} passes past the slot limit)");
            }

            return sb.ToString();
        }
    }
}
