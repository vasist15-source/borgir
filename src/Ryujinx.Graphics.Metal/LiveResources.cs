using Ryujinx.Common.Logging;
using SharpMetal.Metal;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    enum LiveKind
    {
        Texture,
        TextureView,
        BufferTexture,
        Buffer,
        CopyTemp,
        VisibilityBuffer,
        MainCommandBuffer,
        BackgroundCommandBuffer,
    }

    /// <summary>
    /// A census of the Metal objects this backend creates, for finding what keeps
    /// MTLDevice.currentAllocatedSize climbing.
    ///
    /// Two numbers per kind matter. "live" is what the backend still owns: created and
    /// not yet released by its Auto. "zombie" is what the backend released but the
    /// runtime has not deallocated - a weak reference taken just before the final
    /// release is still loadable. A command buffer retains every resource it encoded
    /// until it is deallocated itself, so zombie command buffers pin zombie textures and
    /// buffers, and an autoreleased object on a thread without a draining pool is a
    /// zombie forever. Growth in "live" is an ownership leak in this backend; growth in
    /// "zombie" is a retain held outside it.
    ///
    /// RYUJINX_METAL_LIVE_STATS=1, off by default; logs every <see cref="ReportFrames"/> presents.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static unsafe class LiveResources
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("RYUJINX_METAL_LIVE_STATS") == "1";

        private const int ReportFrames = 600;

        private const string ObjcLibrary = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjcLibrary)]
        private static extern IntPtr objc_initWeak(IntPtr* location, IntPtr obj);

        [DllImport(ObjcLibrary)]
        private static extern IntPtr objc_loadWeakRetained(IntPtr* location);

        [DllImport(ObjcLibrary)]
        private static extern void objc_destroyWeak(IntPtr* location);

        private readonly struct Entry
        {
            public readonly LiveKind Kind;
            public readonly long Bytes;
            public readonly string Shape;
            public readonly long Frame;

            public Entry(LiveKind kind, long bytes, string shape, long frame)
            {
                Kind = kind;
                Bytes = bytes;
                Shape = shape;
                Frame = frame;
            }
        }

        private sealed class Zombie
        {
            public IntPtr* Slot;
            public Entry Entry;
            public long ReleasedFrame;
        }

        private static readonly int _kinds = Enum.GetValues<LiveKind>().Length;
        private static readonly ConcurrentDictionary<IntPtr, Entry> _live = new();
        private static readonly long[] _created = new long[_kinds];
        private static readonly long[] _released = new long[_kinds];
        private static readonly List<Zombie> _zombies = [];
        private static readonly Lock _zombieLock = new();
        private static readonly ConcurrentDictionary<string, long> _backgroundRents = new();

        private static long _frame;

        public static void Note(LiveKind kind, IntPtr native, long bytes, string shape = null)
        {
            if (native == IntPtr.Zero)
            {
                return;
            }

            _live[native] = new Entry(kind, bytes, shape, Volatile.Read(ref _frame));
            Interlocked.Increment(ref _created[(int)kind]);

            if (kind == LiveKind.BackgroundCommandBuffer)
            {
                _backgroundRents.AddOrUpdate(Thread.CurrentThread.Name ?? $"tid {Environment.CurrentManagedThreadId}", 1, (_, n) => n + 1);
            }
        }

        public static void NoteTexture(LiveKind kind, MTLTexture texture)
        {
            if (texture.NativePtr == IntPtr.Zero)
            {
                return;
            }

            long bytes = kind == LiveKind.Texture ? (long)texture.AllocatedSize : 0;
            string shape = $"{texture.Width}x{texture.Height}x{Math.Max(texture.Depth, texture.ArrayLength)} {texture.PixelFormat} L{texture.MipmapLevelCount}";

            Note(kind, texture.NativePtr, bytes, shape);
        }

        /// <summary>
        /// Must run before the caller's final release, while the object is still alive:
        /// a weak reference can only be formed to a live object.
        /// </summary>
        public static void Forget(IntPtr native)
        {
            if (native == IntPtr.Zero || !_live.TryRemove(native, out Entry entry))
            {
                return;
            }

            Interlocked.Increment(ref _released[(int)entry.Kind]);

            IntPtr* slot = (IntPtr*)NativeMemory.Alloc((nuint)sizeof(IntPtr));
            *slot = IntPtr.Zero;
            objc_initWeak(slot, native);

            lock (_zombieLock)
            {
                _zombies.Add(new Zombie { Slot = slot, Entry = entry, ReleasedFrame = Volatile.Read(ref _frame) });
            }
        }

        public static void OnPresent(ulong deviceAllocated)
        {
            long frame = Interlocked.Increment(ref _frame);

            if (frame % ReportFrames == 0)
            {
                Logger.Info?.PrintMsg(LogClass.Gpu, Report(frame, deviceAllocated));
            }
        }

        private static string Report(long frame, ulong deviceAllocated)
        {
            long[] liveCount = new long[_kinds];
            long[] liveBytes = new long[_kinds];
            Dictionary<string, (long Count, long Bytes)> liveShapes = [];

            foreach (Entry entry in _live.Values)
            {
                liveCount[(int)entry.Kind]++;
                liveBytes[(int)entry.Kind] += entry.Bytes;

                if (entry.Kind == LiveKind.Texture)
                {
                    liveShapes.TryGetValue(entry.Shape, out (long Count, long Bytes) s);
                    liveShapes[entry.Shape] = (s.Count + 1, s.Bytes + entry.Bytes);
                }
            }

            long[] zombieCount = new long[_kinds];
            long[] zombieBytes = new long[_kinds];
            long[] zombieOld = new long[_kinds];
            Dictionary<string, (long Count, long Bytes)> zombieShapes = [];

            lock (_zombieLock)
            {
                for (int i = _zombies.Count - 1; i >= 0; i--)
                {
                    Zombie zombie = _zombies[i];
                    IntPtr alive = objc_loadWeakRetained(zombie.Slot);

                    if (alive == IntPtr.Zero)
                    {
                        objc_destroyWeak(zombie.Slot);
                        NativeMemory.Free(zombie.Slot);

                        _zombies[i] = _zombies[^1];
                        _zombies.RemoveAt(_zombies.Count - 1);

                        continue;
                    }

                    ObjcOwnership.Release(alive);

                    int k = (int)zombie.Entry.Kind;

                    zombieCount[k]++;
                    zombieBytes[k] += zombie.Entry.Bytes;

                    // The render thread's pool drains at every present, so anything still
                    // alive a few frames after its release is held by something else.
                    if (frame - zombie.ReleasedFrame > 4)
                    {
                        zombieOld[k]++;

                        if (zombie.Entry.Shape != null)
                        {
                            zombieShapes.TryGetValue(zombie.Entry.Shape, out (long Count, long Bytes) s);
                            zombieShapes[zombie.Entry.Shape] = (s.Count + 1, s.Bytes + zombie.Entry.Bytes);
                        }
                    }
                }
            }

            StringBuilder builder = new();

            builder.Append($"Metal live resources at frame {frame} (device allocated {deviceAllocated >> 20} MiB):");

            for (int k = 0; k < _kinds; k++)
            {
                builder.Append($" {(LiveKind)k}: live {liveCount[k]} ({liveBytes[k] >> 20} MiB), " +
                    $"made {Interlocked.Exchange(ref _created[k], 0)}, released {Interlocked.Exchange(ref _released[k], 0)}, " +
                    $"zombie {zombieCount[k]} ({zombieBytes[k] >> 20} MiB, {zombieOld[k]} older than 4 frames);");
            }

            builder.Append(" largest live texture shapes: ");
            builder.Append(string.Join(", ", liveShapes.OrderByDescending(s => s.Value.Bytes).Take(6)
                .Select(s => $"{s.Key} x{s.Value.Count} {s.Value.Bytes >> 20} MiB")));

            if (zombieShapes.Count != 0)
            {
                builder.Append("; old zombie shapes: ");
                builder.Append(string.Join(", ", zombieShapes.OrderByDescending(s => s.Value.Count).Take(6)
                    .Select(s => $"{s.Key} x{s.Value.Count} {s.Value.Bytes >> 20} MiB")));
            }

            if (!_backgroundRents.IsEmpty)
            {
                builder.Append("; background command buffers by thread: ");
                builder.Append(string.Join(", ", _backgroundRents.Select(r => $"{r.Key}={r.Value}")));
            }

            return builder.ToString();
        }
    }
}
