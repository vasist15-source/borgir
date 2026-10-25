using System;

namespace Ryujinx.Graphics.Gpu.Nvn
{
    /// <summary>
    /// Record decoding: state records update <see cref="NvnState"/>; actions (draws, dispatches,
    /// clears, copies, counters...) go to the Execute* methods, which commit state to GAL.
    /// </summary>
    sealed unsafe partial class NvnReplay
    {
        private readonly NvnState _state = new();

        private void Replay(byte* buffer, int length)
        {
            for (int at = 0; at + 8 <= length;)
            {
                PvOp op = (PvOp)((uint*)(buffer + at))[0];
                uint size = ((uint*)(buffer + at))[1];

                if (op == PvOp.End || size < 8 || at + size > length)
                {
                    break;
                }

                byte* p = buffer + at + 8;

                try
                {
                    Apply(op, p, (int)size - 8);
                }
                catch (Exception e)
                {
                    ReportException(op, e);
                }

                at += (int)size;
            }
        }

        private void Apply(PvOp op, byte* p, int size)
        {
            NvnState s = _state;

            switch (op)
            {
                case PvOp.SetRenderTargets:
                    {
                        int n = *(int*)p;
                        bool hasDepth = *(int*)(p + 4) != 0;
                        PvRtTarget* t = (PvRtTarget*)(p + 8);

                        s.ColorCount = Math.Min(n, NvnState.MaxColorTargets);

                        for (int i = 0; i < NvnState.MaxColorTargets; i++)
                        {
                            s.Colors[i] = i < s.ColorCount ? t[i] : default;
                        }

                        s.HasDepth = hasDepth;
                        s.Depth = hasDepth ? t[n] : default;
                        s.Changed |= NvnState.Dirty.RenderTargets;
                        RenderTargetsChanged();
                        break;
                    }

                case PvOp.ClearColor:
                    ExecuteClearColor(*(int*)p, *(int*)(p + 4), (float*)(p + 8));
                    break;

                case PvOp.ClearDepthStencil:
                    ExecuteClearDepthStencil(*(float*)p, *(int*)(p + 4) != 0, *(int*)(p + 8), *(int*)(p + 12));
                    break;

                case PvOp.Viewports:
                    {
                        int first = *(int*)p, n = *(int*)(p + 4);

                        for (int i = 0; i < n && first + i < NvnState.MaxViewports; i++)
                        {
                            new ReadOnlySpan<float>(p + 8 + i * 16, 4).CopyTo(s.Viewports.AsSpan((first + i) * 4));
                        }

                        s.ViewportCount = Math.Max(s.ViewportCount, Math.Min(first + n, NvnState.MaxViewports));
                        s.Changed |= NvnState.Dirty.Viewports;
                        break;
                    }

                case PvOp.Scissors:
                    {
                        int first = *(int*)p, n = *(int*)(p + 4);

                        for (int i = 0; i < n && first + i < NvnState.MaxViewports; i++)
                        {
                            new ReadOnlySpan<int>(p + 8 + i * 16, 4).CopyTo(s.Scissors.AsSpan((first + i) * 4));
                        }

                        s.ScissorCount = Math.Max(s.ScissorCount, Math.Min(first + n, NvnState.MaxViewports));
                        s.Changed |= NvnState.Dirty.Scissors;
                        break;
                    }

                case PvOp.DepthRanges:
                    {
                        int first = *(int*)p, n = *(int*)(p + 4);

                        for (int i = 0; i < n && first + i < NvnState.MaxViewports; i++)
                        {
                            new ReadOnlySpan<float>(p + 8 + i * 8, 2).CopyTo(s.DepthRanges.AsSpan((first + i) * 2));
                        }

                        s.DepthRangeCount = Math.Max(s.DepthRangeCount, Math.Min(first + n, NvnState.MaxViewports));
                        s.Changed |= NvnState.Dirty.Viewports;
                        break;
                    }

                case PvOp.BindProgram:
                    s.Program = *(PvBindProgram*)p;
                    s.HasProgram = s.Program.NumStages > 0;
                    s.Changed |= NvnState.Dirty.Program;
                    break;

                case PvOp.BindState:
                    {
                        int kind = *(int*)p;
                        byte* b = p + 8;

                        switch (kind)
                        {
                            case 0:
                                {
                                    ulong v = *(ulong*)b;
                                    s.Blend[(int)(v & 7)] = v;
                                    s.Changed |= NvnState.Dirty.Blend;
                                    break;
                                }
                            case 1:
                                s.ColorState = *(uint*)b;
                                s.Changed |= NvnState.Dirty.Color | NvnState.Dirty.Blend;
                                break;
                            case 2:
                                s.ChannelMask = *(uint*)b;
                                s.Changed |= NvnState.Dirty.ChannelMask;
                                break;
                            case 3:
                                s.MultisampleLo = *(ulong*)b;
                                s.MultisampleHi = *(ulong*)(b + 8);
                                s.Changed |= NvnState.Dirty.Multisample;
                                break;
                            case 4:
                                s.Polygon = *(uint*)b;
                                s.Changed |= NvnState.Dirty.Polygon;
                                break;
                            case 5:
                                s.DepthStencil = *(ulong*)b;
                                s.Changed |= NvnState.Dirty.DepthStencil;
                                break;
                        }

                        break;
                    }

                case PvOp.BindVertexAttribs:
                    {
                        int n = Math.Min(*(int*)p, NvnState.MaxVertexAttribs);
                        new ReadOnlySpan<uint>(p + 8, n).CopyTo(s.VertexAttribs);
                        s.VertexAttribs.AsSpan(n).Clear();
                        s.VertexAttribCount = n;
                        s.Changed |= NvnState.Dirty.VertexAttribs;
                        break;
                    }

                case PvOp.BindVertexStreams:
                    {
                        int n = Math.Min(*(int*)p, NvnState.MaxVertexStreams);

                        for (int i = 0; i < NvnState.MaxVertexStreams; i++)
                        {
                            s.StreamStride[i] = i < n ? ((uint*)(p + 8))[i * 2] : 0;
                            s.StreamDivisor[i] = i < n ? ((uint*)(p + 8))[i * 2 + 1] : 0;
                        }

                        s.VertexStreamCount = n;
                        s.Changed |= NvnState.Dirty.VertexStreams;
                        break;
                    }

                case PvOp.BindVertexBuffers:
                    {
                        int first = *(int*)p, n = *(int*)(p + 4);
                        ulong* r = (ulong*)(p + 8);

                        for (int i = 0; i < n && first + i < NvnState.MaxVertexStreams; i++)
                        {
                            s.VertexBufferAddress[first + i] = r[i * 2];
                            s.VertexBufferSize[first + i] = r[i * 2 + 1];
                        }

                        s.Changed |= NvnState.Dirty.VertexBuffers;
                        break;
                    }

                case PvOp.BindUniformBuffers:
                case PvOp.BindStorageBuffers:
                    {
                        int stage = NvnState.StageIndex(*(int*)p);
                        int first = *(int*)(p + 4), n = *(int*)(p + 8);
                        ulong* r = (ulong*)(p + 16);
                        bool uniform = op == PvOp.BindUniformBuffers;
                        int max = uniform ? NvnState.MaxUniformBuffers : NvnState.MaxStorageBuffers;

                        for (int i = 0; i < n && first + i < max; i++)
                        {
                            if (uniform)
                            {
                                s.UniformAddress[stage, first + i] = r[i * 2];
                                s.UniformSize[stage, first + i] = r[i * 2 + 1];

                                if (stage == 5)
                                {
                                    s.WriteC0(stage, 0x210 + (first + i) * 16, r[i * 2], r[i * 2 + 1]);
                                }
                            }
                            else
                            {
                                s.StorageAddress[stage, first + i] = r[i * 2];
                                s.StorageSize[stage, first + i] = r[i * 2 + 1];

                                int tableBase = NvnState.StorageTableBase(stage);

                                if (tableBase >= 0)
                                {
                                    s.WriteC0(stage, tableBase + (first + i) * 16, r[i * 2], r[i * 2 + 1]);
                                }
                            }
                        }

                        s.Changed |= uniform ? NvnState.Dirty.UniformBuffers : NvnState.Dirty.StorageBuffers;
                        break;
                    }

                case PvOp.BindTextures:
                case PvOp.BindImages:
                case PvOp.BindSamplers:
                    {
                        int stage = NvnState.StageIndex(*(int*)p);
                        int first = *(int*)(p + 4), n = *(int*)(p + 8), kind = *(int*)(p + 12);
                        ulong* h = (ulong*)(p + 24);

                        int word = op switch
                        {
                            PvOp.BindImages => NvnState.ImageWord,
                            PvOp.BindSamplers => NvnState.SeparateSamplerWord,
                            _ => kind == 1 ? NvnState.SeparateTextureWord : NvnState.CombinedTextureWord,
                        };

                        int[] c2 = s.C2[stage];

                        for (int i = 0; i < n; i++)
                        {
                            int at = word + (first + i) * 2;

                            if (at + 1 < c2.Length)
                            {
                                c2[at] = (int)h[i];
                                c2[at + 1] = (int)(h[i] >> 32);
                            }
                        }

                        s.C2Dirty[stage] = true;
                        s.Changed |= NvnState.Dirty.Textures;
                        break;
                    }

                case PvOp.Stencil:
                    {
                        int face = *(int*)p, value = *(int*)(p + 4), write = *(int*)(p + 8), reference = *(int*)(p + 12), which = *(int*)(p + 16);

                        for (int f = 0; f < 2; f++)
                        {
                            if ((face & (1 << f)) == 0)
                            {
                                continue;
                            }

                            switch (which)
                            {
                                case 0: s.StencilValueMask[f] = value; break;
                                case 1: s.StencilWriteMask[f] = write; break;
                                default: s.StencilRef[f] = reference; break;
                            }
                        }

                        s.Changed |= NvnState.Dirty.Stencil;
                        break;
                    }

                case PvOp.PolygonOffset:
                    s.PolygonOffsetFactor = *(float*)p;
                    s.PolygonOffsetUnits = *(float*)(p + 4);
                    s.PolygonOffsetClamp = *(float*)(p + 8);
                    s.Changed |= NvnState.Dirty.PolygonOffset;
                    break;

                case PvOp.BlendColor:
                    new ReadOnlySpan<float>(p, 4).CopyTo(s.BlendColor);
                    s.Changed |= NvnState.Dirty.BlendColor;
                    break;

                case PvOp.DepthClamp:
                    s.DepthClamp = *(int*)p != 0;
                    s.Changed |= NvnState.Dirty.DepthClamp;
                    break;

                case PvOp.RenderEnable:
                    s.RenderEnable = *(int*)p != 0;
                    s.ConditionAddress = 0;
                    break;

                case PvOp.RenderConditional:
                    s.ConditionMode = *(int*)p;
                    s.ConditionAddress = *(ulong*)(p + 8);
                    break;

                case PvOp.RasterizerDiscard:
                    s.RasterizerDiscard = *(int*)p != 0;
                    s.Changed |= NvnState.Dirty.RasterizerDiscard;
                    break;

                case PvOp.PrimitiveRestart:
                    s.PrimitiveRestart = *(int*)p != 0;
                    s.PrimitiveRestartIndex = *(int*)(p + 4);
                    s.Changed |= NvnState.Dirty.PrimitiveRestart;
                    break;

                case PvOp.LineWidth:
                    s.LineWidth = *(float*)p;
                    s.Changed |= NvnState.Dirty.LineWidth;
                    break;

                case PvOp.PointSize:
                    s.PointSize = *(float*)p;
                    s.Changed |= NvnState.Dirty.PointSize;
                    break;

                case PvOp.AlphaRef:
                    s.AlphaRef = *(float*)p;
                    break;

                case PvOp.SampleMask:
                    s.SampleMask = *(int*)p;
                    s.Changed |= NvnState.Dirty.SampleMask;
                    break;

                case PvOp.SetTexturePool:
                    s.TexturePool = *(ulong*)p;
                    s.TexturePoolCount = *(int*)(p + 8);
                    s.Changed |= NvnState.Dirty.Textures;
                    break;

                case PvOp.SetSamplerPool:
                    s.SamplerPool = *(ulong*)p;
                    s.SamplerPoolCount = *(int*)(p + 8);
                    s.Changed |= NvnState.Dirty.Textures;
                    break;

                case PvOp.CopyRowStride:
                    s.CopyRowStride = *(long*)p;
                    break;

                case PvOp.Draw:
                    ExecuteDraw(*(int*)p, *(int*)(p + 4), *(int*)(p + 8), *(int*)(p + 12), *(int*)(p + 16));
                    break;

                case PvOp.DrawIndexed:
                    ExecuteDrawIndexed(*(int*)p, *(int*)(p + 4), *(int*)(p + 8), *(int*)(p + 12), *(int*)(p + 16), *(int*)(p + 20), *(ulong*)(p + 24));
                    break;

                case PvOp.DrawIndirect:
                case PvOp.DrawIndexedIndirect:
                    ExecuteDrawIndirect(op == PvOp.DrawIndexedIndirect, *(int*)p, *(int*)(p + 4), *(ulong*)(p + 8), *(ulong*)(p + 16));
                    break;

                case PvOp.Dispatch:
                    ExecuteDispatch(*(int*)p, *(int*)(p + 4), *(int*)(p + 8));
                    break;

                case PvOp.DispatchIndirect:
                    ExecuteDispatchIndirect(*(ulong*)p);
                    break;

                case PvOp.Barrier:
                    ExecuteBarrier(*(int*)p);
                    break;

                case PvOp.UpdateUniformBuffer:
                    ExecuteUpdateBuffer(*(ulong*)p, new ReadOnlySpan<byte>(p + 16, (int)Math.Min(*(ulong*)(p + 8), (ulong)(size - 16))));
                    break;

                case PvOp.CopyBufferToBuffer:
                    ExecuteCopyBuffer(*(ulong*)p, *(ulong*)(p + 8), *(ulong*)(p + 16));
                    break;

                case PvOp.ClearBuffer:
                    ExecuteClearBuffer(*(ulong*)p, *(ulong*)(p + 8), *(uint*)(p + 16));
                    break;

                case PvOp.CopyBufferToTexture:
                case PvOp.CopyTextureToBuffer:
                    ExecuteCopyBufferTexture(op == PvOp.CopyBufferToTexture, in *(PvCopyBt*)p);
                    break;

                case PvOp.CopyTextureToTexture:
                    ExecuteCopyTexture(in *(PvCopyT2T*)p);
                    break;

                case PvOp.ClearTexture:
                    ExecuteClearTexture(in *(PvClearTex*)p);
                    break;

                case PvOp.ReportCounter:
                    ExecuteReportCounter(*(int*)p, *(ulong*)(p + 8));
                    break;

                case PvOp.ResetCounter:
                    ExecuteResetCounter(*(int*)p);
                    break;

                case PvOp.SignalEvent:
                    ExecuteSignal(*(ulong*)p, *(int*)(p + 8), *(uint*)(p + 20));
                    break;

                case PvOp.WaitEvent:
                case PvOp.DepthBounds:
                case PvOp.TiledCache:
                case PvOp.Discard:
                case PvOp.Scratch:
                case PvOp.PatchSize:
                case PvOp.DrawTexture:
                    break;
            }
        }
    }
}
