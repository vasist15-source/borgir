using Ryujinx.Common.Logging;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// RYUJINX_METAL_PASS_TIMES=1: GPU time of every render, compute and blit pass, from Metal's timestamp counters
    /// at the pass's stage boundaries, added up by the kind of pass (its targets, how they load, why it ended) and
    /// logged with the 120-frame stats. The command buffer times in the stats line say how busy the GPU is; this says
    /// which work keeps it busy. On Apple GPUs the timestamps are nanoseconds. A pass's vertex work can overlap the
    /// previous pass's fragment work, so the per-pass times add up to more than the GPU's busy time.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class PassTimer
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("RYUJINX_METAL_PASS_TIMES") == "1";

        private const int MaxPassesPerBuffer = 2048;
        private const ulong DontSample = ulong.MaxValue;
        private const int ReportedKinds = 16;

        private sealed class Batch
        {
            public MTLCounterSampleBuffer Samples;
            public int Count;
            public readonly string[] Keys = new string[MaxPassesPerBuffer];
            public readonly int[] Draws = new int[MaxPassesPerBuffer];
        }

        private struct Totals
        {
            public double Ms;
            public int Passes;
            public long Draws;
        }

        private static readonly Lock _lock = new();
        private static readonly Dictionary<IntPtr, Batch> _batches = [];
        private static readonly Stack<Batch> _free = new();
        private static readonly Dictionary<string, Totals> _totals = [];
        private static MTLDevice _device;
        private static MTLCounterSet _timestamps;
        private static bool _unavailable;
        private static int _overflow;
        private static int _unsampled;

        public static void AttachRender(MTLCommandBuffer commandBuffer, MTLRenderPassDescriptor descriptor)
        {
            if (!TryTake(commandBuffer, RenderKey(descriptor), out Batch batch, out int index))
            {
                return;
            }

            MTLRenderPassSampleBufferAttachmentDescriptor attachment = descriptor.SampleBufferAttachments.Object(0);
            attachment.SampleBuffer = batch.Samples;
            attachment.StartOfVertexSampleIndex = (ulong)index * 2;
            attachment.EndOfVertexSampleIndex = DontSample;
            attachment.StartOfFragmentSampleIndex = DontSample;
            attachment.EndOfFragmentSampleIndex = (ulong)index * 2 + 1;
        }

        public static void AttachCompute(MTLCommandBuffer commandBuffer, MTLComputePassDescriptor descriptor)
        {
            if (!TryTake(commandBuffer, "compute", out Batch batch, out int index))
            {
                return;
            }

            MTLComputePassSampleBufferAttachmentDescriptor attachment = descriptor.SampleBufferAttachments.Object(0);
            attachment.SampleBuffer = batch.Samples;
            attachment.StartOfEncoderSampleIndex = (ulong)index * 2;
            attachment.EndOfEncoderSampleIndex = (ulong)index * 2 + 1;
        }

        public static void AttachBlit(MTLCommandBuffer commandBuffer, MTLBlitPassDescriptor descriptor)
        {
            if (!TryTake(commandBuffer, "blit", out Batch batch, out int index))
            {
                return;
            }

            MTLBlitPassSampleBufferAttachmentDescriptor attachment = descriptor.SampleBufferAttachments.Object(0);
            attachment.SampleBuffer = batch.Samples;
            attachment.StartOfEncoderSampleIndex = (ulong)index * 2;
            attachment.EndOfEncoderSampleIndex = (ulong)index * 2 + 1;
        }

        /// <summary>The pass last started in this command buffer is ending, for this reason and after this many draws.</summary>
        public static void NoteEnd(MTLCommandBuffer commandBuffer, PassEndReason reason, int draws)
        {
            lock (_lock)
            {
                if (_batches.TryGetValue(commandBuffer.NativePtr, out Batch batch) && batch.Count > 0)
                {
                    int last = batch.Count - 1;

                    if (!batch.Keys[last].Contains(" end="))
                    {
                        batch.Keys[last] += $" end={reason}";
                        batch.Draws[last] = draws;
                    }
                }
            }
        }

        /// <summary>The command buffer has completed: reads its passes' timestamps.</summary>
        public static void Complete(MTLCommandBuffer commandBuffer)
        {
            Batch batch;

            lock (_lock)
            {
                if (!_batches.Remove(commandBuffer.NativePtr, out batch))
                {
                    return;
                }
            }

            // The resolved data is autoreleased; this may run on a thread with no pool of its own.
            IntPtr pool = ObjcOwnership.objc_autoreleasePoolPush();

            if (batch.Count > 0)
            {
                NSData data = batch.Samples.ResolveCounterRange(new NSRange { location = 0, length = (ulong)batch.Count * 2 });

                if (data.NativePtr != IntPtr.Zero && data.Length >= (ulong)batch.Count * 16)
                {
                    IntPtr bytes = ObjectiveC.IntPtr_objc_msgSend(data.NativePtr, "bytes");

                    lock (_lock)
                    {
                        for (int i = 0; i < batch.Count; i++)
                        {
                            ulong start = (ulong)Marshal.ReadInt64(bytes, i * 16);
                            ulong end = (ulong)Marshal.ReadInt64(bytes, i * 16 + 8);

                            // A stage that did not run (a pass with no draws has no vertex stage) is not sampled.
                            if (start == 0 || end == 0 || start == ulong.MaxValue || end == ulong.MaxValue || end < start || end - start > 1_000_000_000)
                            {
                                _unsampled++;
                                continue;
                            }

                            Totals totals = _totals.GetValueOrDefault(batch.Keys[i]);
                            totals.Ms += (end - start) / 1e6;
                            totals.Passes++;
                            totals.Draws += batch.Draws[i];
                            _totals[batch.Keys[i]] = totals;
                        }
                    }
                }
            }

            ObjcOwnership.objc_autoreleasePoolPop(pool);

            batch.Count = 0;

            lock (_lock)
            {
                _free.Push(batch);
            }
        }

        /// <summary>The kinds of pass that took the most GPU time over the last <paramref name="frames"/> frames.</summary>
        public static string TakeReport(int frames)
        {
            lock (_lock)
            {
                if (_totals.Count == 0)
                {
                    return _unavailable ? "pass times: GPU timestamp counters unavailable" : null;
                }

                double totalMs = _totals.Values.Sum(t => t.Ms);
                int totalPasses = _totals.Values.Sum(t => t.Passes);
                StringBuilder report = new($"pass times over {frames} frames: {totalMs / frames:F1} ms of passes per frame in {totalPasses / (double)frames:F0} passes (overlapping), {_unsampled} unsampled, {_overflow} over capacity. By kind, ms per frame / passes per frame / avg ms / avg draws:");

                foreach ((string key, Totals totals) in _totals.OrderByDescending(kv => kv.Value.Ms).Take(ReportedKinds))
                {
                    report.Append($"\n    {totals.Ms / frames,6:F2} ms  {totals.Passes / (double)frames,5:F1}  {totals.Ms / totals.Passes,6:F3}  {totals.Draws / (double)totals.Passes,6:F1}  {key}");
                }

                _totals.Clear();
                _unsampled = 0;
                _overflow = 0;

                return report.ToString();
            }
        }

        private static bool TryTake(MTLCommandBuffer commandBuffer, string key, out Batch batch, out int index)
        {
            batch = null;
            index = 0;

            if (!Enabled || _unavailable)
            {
                return false;
            }

            lock (_lock)
            {
                if (!_batches.TryGetValue(commandBuffer.NativePtr, out batch))
                {
                    if (!_free.TryPop(out batch))
                    {
                        batch = CreateBatch(commandBuffer.Device);

                        if (batch == null)
                        {
                            return false;
                        }
                    }

                    _batches[commandBuffer.NativePtr] = batch;
                }

                if (batch.Count == MaxPassesPerBuffer)
                {
                    _overflow++;

                    return false;
                }

                index = batch.Count++;
                batch.Keys[index] = key;
                batch.Draws[index] = 0;

                return true;
            }
        }

        private static Batch CreateBatch(MTLDevice device)
        {
            if (_timestamps.NativePtr == IntPtr.Zero)
            {
                _device = device;

                if (!device.SupportsCounterSampling(MTLCounterSamplingPoint.AtStageBoundary))
                {
                    return Unavailable("the GPU cannot sample counters at stage boundaries");
                }

                NSArray sets = device.CounterSets;

                for (ulong i = 0; i < sets.Count; i++)
                {
                    MTLCounterSet set = new(sets.Object(i));

                    if (StringHelper.String(set.Name) == "timestamp")
                    {
                        _timestamps = set;
                    }
                }

                if (_timestamps.NativePtr == IntPtr.Zero)
                {
                    return Unavailable("no timestamp counter set");
                }

                Logger.Info?.Print(LogClass.Gpu, "Pass times: timing every pass with GPU timestamp counters");
            }

            using MTLCounterSampleBufferDescriptor descriptor = new()
            {
                CounterSet = _timestamps,
                StorageMode = MTLStorageMode.Shared,
                SampleCount = MaxPassesPerBuffer * 2,
            };

            NSError error = new(IntPtr.Zero);
            MTLCounterSampleBuffer samples = _device.NewCounterSampleBuffer(descriptor, ref error);

            if (error != IntPtr.Zero || samples.NativePtr == IntPtr.Zero)
            {
                return Unavailable($"could not create a counter sample buffer ({(error != IntPtr.Zero ? StringHelper.String(error.LocalizedDescription) : "no buffer")})");
            }

            return new Batch { Samples = samples };
        }

        private static Batch Unavailable(string reason)
        {
            _unavailable = true;
            Logger.Warning?.Print(LogClass.Gpu, $"Pass times: off, {reason}");

            return null;
        }

        private static string RenderKey(MTLRenderPassDescriptor descriptor)
        {
            StringBuilder key = new("render");
            MTLRenderPassColorAttachmentDescriptorArray colors = descriptor.ColorAttachments;

            for (ulong i = 0; i < Constants.MaxColorAttachments; i++)
            {
                MTLRenderPassColorAttachmentDescriptor color = colors.Object(i);
                MTLTexture texture = color.Texture;

                if (texture.NativePtr != IntPtr.Zero)
                {
                    key.Append($" c{i}={texture.Width}x{texture.Height}/{texture.PixelFormat}/{Action(color.LoadAction)}{Action(color.StoreAction)}");
                }
            }

            MTLRenderPassDepthAttachmentDescriptor depth = descriptor.DepthAttachment;

            if (depth.Texture.NativePtr != IntPtr.Zero)
            {
                key.Append($" d={depth.Texture.Width}x{depth.Texture.Height}/{depth.Texture.PixelFormat}/{Action(depth.LoadAction)}{Action(depth.StoreAction)}");
            }

            return key.ToString();
        }

        private static string Action(MTLLoadAction action) => action switch
        {
            MTLLoadAction.Load => "L",
            MTLLoadAction.Clear => "C",
            _ => "-",
        };

        private static string Action(MTLStoreAction action) => action switch
        {
            MTLStoreAction.Store => "S",
            MTLStoreAction.Unknown => "?",
            MTLStoreAction.DontCare => "-",
            _ => "s",
        };
    }
}
