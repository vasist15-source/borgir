using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Read-only probe: was each render pass store ever consumed? Every pass end
    /// stores its attachments back to memory; the store was worth its bandwidth only if
    /// something later read that memory - a later pass loading the attachment, a sample,
    /// a copy, a readback, the present. A store followed by a clear, a full overwrite,
    /// or another store with nothing in between paid for a picture nobody looked at.
    /// The frame structure repeats, so the answer here is what a learned store-action
    /// policy could collect. Off by default; RYUJINX_METAL_STORE_LIVENESS=1 enables it.
    ///
    /// What it found (2026-09-04, two saves, 1600-3500 draws/frame): 700-1030 stores a
    /// frame, of which 30-40 came back dead - and all but one per frame were the bloom
    /// chain's 1280x720 RG11B10 targets re-stored with no bind in between, a reader the
    /// probe does not see (most likely a compute pass bound before the producing pass
    /// ended). The one real dead store per frame is a target cleared right after it was
    /// stored: about 15 MB a frame at 1440p, a third of a percent of the bandwidth. A
    /// learned store-action policy has nothing worth collecting here.
    /// </summary>
    static class StoreLiveness
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("RYUJINX_METAL_STORE_LIVENESS") == "1";

        private readonly record struct Pending(ulong Bytes, int Width, int Height, MTLPixelFormat Format, bool Depth);

        private static readonly object _lock = new();
        private static readonly Dictionary<IntPtr, Pending> _pending = new();
        private static readonly Dictionary<(int Width, int Height, MTLPixelFormat Format, bool Depth), (long Count, ulong Bytes)> _deadShapes = new();
        private static long _stores, _consumed, _dead, _deadClear, _deadOverwrite, _deadRestore;
        private static ulong _storeBytes, _deadBytes;

        public static void NoteStore(IntPtr ptr, int width, int height, MTLPixelFormat format, int bytesPerPixel, bool depth)
        {
            if (!Enabled || ptr == IntPtr.Zero)
            {
                return;
            }

            ulong bytes = (ulong)width * (ulong)height * (ulong)Math.Max(1, bytesPerPixel);

            lock (_lock)
            {
                // Stored again with no load and no read in between: the earlier store
                // was never looked at.
                if (_pending.TryGetValue(ptr, out Pending earlier))
                {
                    Dead(earlier, ref _deadRestore);
                }

                _pending[ptr] = new Pending(bytes, width, height, format, depth);
                _stores++;
                _storeBytes += bytes;
            }
        }

        /// <summary>A pass binds the texture as an attachment: Load consumes the pending store, Clear/DontCare buries it.</summary>
        public static void NoteLoad(IntPtr ptr, bool loads)
        {
            if (!Enabled || ptr == IntPtr.Zero)
            {
                return;
            }

            lock (_lock)
            {
                if (_pending.Remove(ptr, out Pending p))
                {
                    if (loads)
                    {
                        _consumed++;
                    }
                    else
                    {
                        Dead(p, ref _deadClear);
                    }
                }
            }
        }

        /// <summary>Sampled, copied from, read back, or presented: the store was needed.</summary>
        public static void NoteRead(IntPtr ptr)
        {
            if (!Enabled || ptr == IntPtr.Zero)
            {
                return;
            }

            lock (_lock)
            {
                if (_pending.Remove(ptr))
                {
                    _consumed++;
                }
            }
        }

        /// <summary>The whole texture is replaced (full copy destination, full upload): the store was dead.</summary>
        public static void NoteOverwrite(IntPtr ptr)
        {
            if (!Enabled || ptr == IntPtr.Zero)
            {
                return;
            }

            lock (_lock)
            {
                if (_pending.Remove(ptr, out Pending p))
                {
                    Dead(p, ref _deadOverwrite);
                }
            }
        }

        private static void Dead(Pending p, ref long kind)
        {
            _dead++;
            kind++;
            _deadBytes += p.Bytes;
            (int, int, MTLPixelFormat, bool) key = (p.Width, p.Height, p.Format, p.Depth);
            _deadShapes.TryGetValue(key, out (long Count, ulong Bytes) s);
            _deadShapes[key] = (s.Count + 1, s.Bytes + p.Bytes);
        }

        /// <summary>Snapshot and reset the window, formatted for the stats line; null when idle.</summary>
        public static string Take(int frames)
        {
            if (!Enabled)
            {
                return null;
            }

            lock (_lock)
            {
                if (_stores == 0)
                {
                    return null;
                }

                double mb = 1.0 / (1024 * 1024);
                string shapes = string.Join(", ", _deadShapes.OrderByDescending(kv => kv.Value.Bytes).Take(4)
                    .Select(kv => $"{kv.Key.Width}x{kv.Key.Height}/{kv.Key.Format}{(kv.Key.Depth ? "(depth)" : "")}={kv.Value.Count / frames}/frame {kv.Value.Bytes * mb / frames:F1}MB"));
                string text =
                    $" stores/frame: {_stores / frames} ({_storeBytes * mb / frames:F0}MB), consumed {_consumed / frames}, " +
                    $"dead {_dead / frames} ({_deadBytes * mb / frames:F1}MB: clear {_deadClear / frames}, overwrite {_deadOverwrite / frames}, restored {_deadRestore / frames}), " +
                    $"undecided {_pending.Count}." + (shapes.Length != 0 ? $" dead shapes: {shapes}." : string.Empty);

                _stores = _consumed = _dead = _deadClear = _deadOverwrite = _deadRestore = 0;
                _storeBytes = _deadBytes = 0;
                _deadShapes.Clear();

                return text;
            }
        }
    }
}
