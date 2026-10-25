using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Gpu.Synchronization;
using Ryujinx.Graphics.Shader;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Nvn
{
    /// <summary>
    /// Replays the paravirtual NVN layer's submissions through GAL.
    ///
    /// GPU addresses are guest addresses: every GPU-visible memory pool is mapped one to one into a
    /// GPU address space of the replay's own (virtual pools map to the pages of their physical pools),
    /// so the texture, buffer and shader caches of the process work unchanged. Submissions are queued
    /// on the GPU thread in order with everything else it runs; a host sync after each one tells the
    /// guest when its fence completes. Presents go through the GPU window's frame queue.
    /// </summary>
    public sealed unsafe partial class NvnReplay : IDisposable
    {
        private readonly GpuContext _context;
        private readonly IRenderer _renderer;
        private readonly MemoryManager _gmm;
        private readonly PhysicalMemory _physical;
        private readonly NvnSamplers _samplers;
        private readonly NvnPrograms _programs;
        private readonly NvnTranslationState _xlate;
        private readonly ulong _pid;

        private readonly BlockingCollection<(ulong Fence, ulong Sync, Action<ulong> Completed, List<(ulong, int, uint)> Signals)> _fences = new();
        private readonly Thread _fenceThread;

        private long _errors;
        private readonly Dictionary<string, int> _errorCounts = new();

        public NvnReplay(GpuContext context, ulong pid, ulong dramSize)
        {
            _context = context;
            _renderer = context.Renderer;
            _pid = pid;
            _gmm = context.CreateMemoryManager(pid, dramSize);
            _physical = _gmm.Physical;
            _samplers = new NvnSamplers(_renderer, GraphicsConfig.MaxAnisotropy);
            _programs = new NvnPrograms(context, _gmm);
            _xlate = new NvnTranslationState(_physical, _state);
            _physical.BufferCache.NotifyBuffersModified += OnBuffersModified;

            _fenceThread = new Thread(FenceLoop) { Name = "NvnReplay.Fences", IsBackground = true };
            _fenceThread.Start();
        }

        // ---------- Memory ----------

        private const uint PoolCpuNoAccess = 0x1;
        private const uint PoolGpuNoAccess = 0x8;
        private const uint PoolVirtual = 0x200;

        /// <summary>A memory pool was initialised or finalised: GPU address = guest address.</summary>
        public void PoolChanged(ulong addr, ulong size, uint flags, bool add)
        {
            if ((flags & (PoolVirtual | PoolGpuNoAccess)) != 0 || size == 0)
            {
                return;
            }

            ulong start = addr & ~(MemoryManager.PageSize - 1);
            ulong end = (addr + size + MemoryManager.PageSize - 1) & ~(MemoryManager.PageSize - 1);

            if (add)
            {
                _gmm.Map(start, start, end - start, PteKind.Pitch);
            }
            else
            {
                _gmm.Unmap(start, end - start);
            }
        }

        /// <summary>Pages of a virtual pool now show memory of a physical pool (cpuVa = 0: unmapped).</summary>
        public void VirtualMapping(ulong gpuVa, ulong cpuVa, ulong size)
        {
            if (cpuVa == 0)
            {
                _gmm.Unmap(gpuVa, size);
            }
            else
            {
                _gmm.Map(cpuVa, gpuVa, size, PteKind.Pitch);
            }
        }

        // ---------- Submissions and fences ----------

        public void Submit(byte[] records, int length, ulong fence, Action<ulong> completed)
        {
            // Pooled: a frame submits a few hundred KB of records, and fresh arrays that large land on
            // the large object heap, whose collections stall every thread.
            byte[] copy = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
            System.Buffer.BlockCopy(records, 0, copy, 0, length);
            Interlocked.Add(ref _submittedBytes, length);

            _context.GPFifo.PushHostAction(() => RunSubmission(copy, length, fence, completed));
        }

        private long _submittedBytes;

        private void RunSubmission(byte[] records, int length, ulong fence, Action<ulong> completed)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

            fixed (byte* b = records)
            {
                Replay(b, length);
            }

            System.Buffers.ArrayPool<byte>.Shared.Return(records);

            EndSubmission();
            _tSubmit += System.Diagnostics.Stopwatch.GetTimestamp() - t0;

            // A strict host sync starts the GPU on everything so far; its completion is the fence's.
            _context.CreateHostSyncIfNeeded(HostSyncFlags.Force | HostSyncFlags.Strict);
            _fences.Add((fence, _context.SyncNumber - 1, completed, TakeSignals()));
        }

        private void FenceLoop()
        {
            foreach ((ulong fence, ulong sync, Action<ulong> completed, List<(ulong, int, uint)> signals) in _fences.GetConsumingEnumerable())
            {
                try
                {
                    _renderer.WaitSync(sync);

                    if (signals != null)
                    {
                        ApplySignals(signals);
                    }
                }
                catch (Exception e)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"nvnpv: waiting for submission {fence} failed: {e.Message}");
                }

                FlushCounters();
                completed(fence);
            }
        }

        // ---------- Present ----------

        public void Present(PvTex texture, int crop0, int crop1, int crop2, int crop3, Action released)
        {
            _context.GPFifo.PushHostAction(() =>
            {
                EndSubmission();
                FrameBoundary();

                (Format format, _) = NvnGal.Format(texture.D.Format);
                (int bytes, _, _) = NvnGal.BlockOf(texture.D.Format);
                int width = (int)texture.D.Width;
                int height = (int)texture.D.Height;

                // Crop as NVN gives it (x, y, w, h); none = the whole texture.
                ImageCrop crop = crop2 > 0 && crop3 > 0
                    ? new ImageCrop(crop0, crop0 + crop2, crop1, crop1 + crop3, false, false, false, 16, 9)
                    : new ImageCrop(0, width, 0, height, false, false, false, 16, 9);

                bool queued = _context.Window.EnqueueFrameThreadSafe(
                    _pid,
                    texture.Addr,
                    width,
                    height,
                    NvnTextures.IsLinear(texture.D) ? (int)NvnTextures.LinearStride(texture.D, bytes, 1) : 0,
                    NvnTextures.IsLinear(texture.D),
                    1 << texture.L.TileH,
                    format,
                    (byte)bytes,
                    crop,
                    (ctx, obj) => { },
                    obj => released(),
                    null);

                if (queued)
                {
                    _context.Window.SignalFrameReady();
                }
                else
                {
                    released();
                }
            });
        }

        // ---------- Errors ----------

        private void ReportException(PvOp op, Exception e)
        {
            Interlocked.Increment(ref _errors);
            Report($"{op}: {e.GetType().Name}: {e.Message}", e);
        }

        private void Report(string what, Exception e = null)
        {
            lock (_errorCounts)
            {
                _errorCounts.TryGetValue(what, out int n);
                _errorCounts[what] = n + 1;

                if (n < 3)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"nvnpv replay: {what}" + (e != null && n == 0 ? $"\n{e.StackTrace}" : ""));
                }
            }
        }

        public void Dispose()
        {
            _physical.BufferCache.NotifyBuffersModified -= OnBuffersModified;
            _fences.CompleteAdding();
            _samplers.Dispose();
            _programs.Dispose();
        }
    }
}
