using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Crash context that survives the process: a ring of fixed-width records in a
    /// memory-mapped file. Every record is plain memory stores - no I/O, no lock, no
    /// allocation - so it costs nanoseconds per event and never perturbs timing. When
    /// the Metal driver segfaults inside a draw, the mapping is still on disk with the
    /// last few thousand render-thread events: which draw, which pipeline object, which
    /// index buffer, which encoder generation, and what was disposed shortly before.
    /// Default on; RYUJINX_METAL_CRASH_RING=0 disables. Reader: scratchpad crash_ring_read.py.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class CrashRing
    {
        public const string Path = "/tmp/ryujinx-metal-crash-ring.bin";
        private const int HeaderSize = 64;
        private const int RecordSize = 64;
        private const int Capacity = 1 << 15;
        // Disposals are rare and matter minutes later; they get their own ring so the
        // draw firehose (~17k records a second) cannot overwrite them.
        private const int DisposeCapacity = 1 << 12;

        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_CRASH_RING") != "0";

        private static readonly unsafe byte* _base = Map();
        private static long _next;
        private static long _nextDispose;
        public static int Frame;

        public enum Kind : byte
        {
            None = 0,
            DrawIndexed = 1,
            DrawArrays = 2,
            DrawIndirect = 3,
            PsoSet = 4,
            EncoderBegin = 5,
            EncoderEnd = 6,
            BufferDispose = 7,
            ProgramDispose = 8,
            Present = 9,
        }

        private static unsafe byte* Map()
        {
            if (!Enabled)
            {
                return null;
            }

            try
            {
                long size = HeaderSize + (long)RecordSize * (Capacity + DisposeCapacity);
                using FileStream fs = new(Path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                fs.SetLength(size);
                MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(fs, null, size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
                MemoryMappedViewAccessor view = mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
                byte* ptr = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                // Header: magic, capacity, record size, pid, start time.
                *(ulong*)ptr = 0x474E4952485352C3UL;                 // "CRSHRING" tag
                *(int*)(ptr + 8) = Capacity;
                *(int*)(ptr + 12) = RecordSize;
                *(int*)(ptr + 20) = DisposeCapacity;
                *(int*)(ptr + 16) = Environment.ProcessId;
                *(long*)(ptr + 24) = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return ptr;
            }
            catch (Exception e)
            {
                Ryujinx.Common.Logging.Logger.Warning?.PrintMsg(Ryujinx.Common.Logging.LogClass.Gpu, $"crash ring unavailable: {e.Message}");
                return null;
            }
        }

        private static byte ThreadKind()
        {
            string name = Thread.CurrentThread.Name;
            if (name == null) return 3;
            if (name.StartsWith("GUI.Render", StringComparison.Ordinal)) return 0;
            if (name.StartsWith("GPU.", StringComparison.Ordinal)) return 1;
            return 2;
        }

        private static unsafe void Write(Kind kind, long a, long b, long c, long d, string label)
        {
            byte* basePtr = _base;
            if (basePtr == null)
            {
                return;
            }

            bool disposal = kind == Kind.BufferDispose || kind == Kind.ProgramDispose;
            long seq = Interlocked.Increment(ref _next);
            byte* rec;

            if (disposal)
            {
                long slot = Interlocked.Increment(ref _nextDispose);
                rec = basePtr + HeaderSize + ((long)Capacity + (slot & (DisposeCapacity - 1))) * RecordSize;
            }
            else
            {
                rec = basePtr + HeaderSize + (seq & (Capacity - 1)) * RecordSize;
            }

            *(long*)(rec + 0) = seq;
            rec[8] = (byte)kind;
            rec[9] = ThreadKind();
            *(int*)(rec + 12) = Frame;
            *(long*)(rec + 16) = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            *(long*)(rec + 24) = a;
            *(long*)(rec + 32) = b;
            *(long*)(rec + 40) = c;
            *(long*)(rec + 48) = d;

            for (int i = 0; i < 8; i++)
            {
                rec[56 + i] = (byte)(label != null && i < label.Length ? label[i] : 0);
            }

            // The header's write indices are what the reader trusts; store them last.
            *(long*)(basePtr + 32) = seq;
            if (disposal)
            {
                *(long*)(basePtr + 40) = Interlocked.Read(ref _nextDispose);
            }
        }

        public static void Draw(Kind kind, string program, IntPtr pso, IntPtr indexBuffer, IntPtr encoder, long generation, int count)
            => Write(kind, pso.ToInt64(), indexBuffer.ToInt64(), encoder.ToInt64(), (generation << 32) | (uint)count, program);

        public static void PsoSet(IntPtr pso, IntPtr encoder, long generation, string program)
            => Write(Kind.PsoSet, pso.ToInt64(), 0, encoder.ToInt64(), generation, program);

        public static void EncoderBegin(IntPtr encoder, long generation) => Write(Kind.EncoderBegin, 0, 0, encoder.ToInt64(), generation, null);
        public static void EncoderEnd(IntPtr encoder, long generation) => Write(Kind.EncoderEnd, 0, 0, encoder.ToInt64(), generation, null);
        public static void BufferDispose(IntPtr buffer, long size) => Write(Kind.BufferDispose, buffer.ToInt64(), size, 0, 0, null);
        public static void ProgramDispose(string program, IntPtr firstPso, int psoCount) => Write(Kind.ProgramDispose, firstPso.ToInt64(), psoCount, 0, 0, program);
        public static void Present(int frame) { Frame = frame; Write(Kind.Present, 0, 0, 0, frame, null); }
    }
}
