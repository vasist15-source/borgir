using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Engine.Threed.ComputeDraw;
using Ryujinx.Graphics.Gpu.Engine.Types;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Memory.Range;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    /// <summary>
    /// Draw manager.
    /// </summary>
    class DrawManager : IDisposable
    {
        // Since we don't know the index buffer size for indirect draws,
        // we must assume a minimum and maximum size and use that for buffer data update purposes.
        private const int MinIndirectIndexCount = 0x10000;
        private const int MaxIndirectIndexCount = 0x4000000;

        private readonly GpuContext _context;
        private readonly GpuChannel _channel;
        private readonly DeviceStateWithShadow<ThreedClassState> _state;
        private readonly DrawState _drawState;
        private readonly SpecializationStateUpdater _currentSpecState;
        private readonly VtgAsCompute _vtgAsCompute;
        private bool _topologySet;

        private bool _instancedDrawPending;
        private bool _instancedIndexed;
        private bool _instancedIndexedInline;

        private int _instancedFirstIndex;
        private int _instancedFirstVertex;
        private int _instancedFirstInstance;
        private int _instancedIndexCount;
        private int _instancedDrawStateFirst;
        private int _instancedDrawStateCount;

        private int _instanceIndex;

        private const int VertexBufferFirstMethodOffset = 0x35d;
        private const int IndexBufferCountMethodOffset = 0x5f8;

        // Backend-independent draw trace: touch /tmp/ryujinx-gal-trace while a
        // game runs to log the next TraceDrawBudget draws with a guest-code-based
        // program hash, so inventories from different backends (same spot, same
        // save) can be diffed directly.
        private const string TraceTriggerPath = "/tmp/ryujinx-gal-trace";

        // Diagnostic: RYUJINX_SKIP_PROGS=<hash>[,<hash>...] drops every draw whose
        // guest-shader hash (the same label the draw trace prints) is listed. Running
        // the backend that renders a feature correctly and bisecting over the program
        // list identifies which shader draws that feature, without a GPU capture.
        private static System.Collections.Generic.HashSet<string> _skipProgs =
            new(((System.Environment.GetEnvironmentVariable("RYUJINX_SKIP_PROGS") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)));

        // The skip list is also reloadable at runtime from /tmp/ryujinx-skip-progs
        // (comma or newline separated hashes). Bisecting a feature otherwise needs one
        // process launch per candidate set, and each launch has to be focused by hand
        // before input can be injected.
        private const string SkipListPath = "/tmp/ryujinx-skip-progs";

        // RYUJINX_WATCH_PROG=<label>: count this program's draws per frame. One fullscreen
        // quad lost among four hundred draws needs its own counter to be visible.
        private static readonly string _watchProg =
            Environment.GetEnvironmentVariable("RYUJINX_WATCH_PROG");

        private static bool _watchStagesLogged;

        private static readonly bool _watchConst =
            Environment.GetEnvironmentVariable("RYUJINX_WATCH_CONST") == "1";

        private int _watchTexPrev;
        private int _watchIdPrev = -1;
        private int _watchPackedPrev;

        private static readonly bool _watchBarrier =
            Environment.GetEnvironmentVariable("RYUJINX_WATCH_BARRIER") == "1";
        private static long _skipListStamp = -1;
        private static int _skipCheckCounter;
        private static long _skipCheckTime;
        private static long _traceCheckTime;

        /// <summary>
        /// Whether a second has passed since the last look for a diagnostic trigger file.
        /// </summary>
        private static bool DiagnosticFileCheckDue(ref long lastCheck)
        {
            long now = Environment.TickCount64;

            if (now - lastCheck < 1000)
            {
                return false;
            }

            lastCheck = now;
            return true;
        }

        private static void ReloadSkipListIfChanged()
        {
            if ((++_skipCheckCounter & 0xFF) != 0 || !DiagnosticFileCheckDue(ref _skipCheckTime))
            {
                return;
            }

            try
            {
                long stamp = File.Exists(SkipListPath) ? File.GetLastWriteTimeUtc(SkipListPath).Ticks : 0;

                if (stamp == _skipListStamp)
                {
                    return;
                }

                _skipListStamp = stamp;

                _skipProgs = stamp == 0
                    ? []
                    : new System.Collections.Generic.HashSet<string>(File.ReadAllText(SkipListPath)
                        .Split([',', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries));

                Logger.Warning?.Print(LogClass.Gpu, $"Draw skip list reloaded: {_skipProgs.Count} programs.");
            }
            catch (IOException)
            {
                // Keep the previous list if the file is mid-write.
            }
        }
        private const int TraceDrawBudget = 15000;

        private static int _traceRemaining;
        private static int _traceCheckCounter;
        private static readonly ConditionalWeakTable<CachedShaderProgram, string> _traceProgramLabels = new();

        private static string GetTraceProgramLabel(CachedShaderProgram program)
        {
            if (program == null)
            {
                return "none";
            }

            if (!_traceProgramLabels.TryGetValue(program, out string label))
            {
                int totalLength = 0;

                foreach (CachedShaderStage stage in program.Shaders)
                {
                    totalLength += stage?.Code?.Length ?? 0;
                }

                byte[] combined = new byte[totalLength];
                int position = 0;

                foreach (CachedShaderStage stage in program.Shaders)
                {
                    if (stage?.Code != null)
                    {
                        stage.Code.CopyTo(combined, position);
                        position += stage.Code.Length;
                    }
                }

                label = Hash128.ComputeHash(combined).ToString()[..16];

                _traceProgramLabels.Add(program, label);
            }

            return label;
        }

        private void TraceDrawIfActive(int count, int instanceCount, int firstIndex, int firstVertex, int firstInstance, bool indexed)
        {
            if (_traceRemaining <= 0)
            {
                // Only look for the trigger file every 512 draws, and at most once a second:
                // at 4000 draws a frame even every 512th draw was about 2% of the GPU thread.
                if ((++_traceCheckCounter & 0x1FF) != 0 || !DiagnosticFileCheckDue(ref _traceCheckTime))
                {
                    return;
                }

                if (!File.Exists(TraceTriggerPath))
                {
                    return;
                }

                try
                {
                    File.Delete(TraceTriggerPath);
                }
                catch (IOException)
                {
                    return;
                }

                _traceRemaining = TraceDrawBudget;

                Logger.Warning?.PrintMsg(LogClass.Gpu, $"GAL draw trace started for {TraceDrawBudget} draws.");
            }

            if (--_traceRemaining == 0)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, "GAL draw trace finished.");
            }

            ref RtColorState rt0 = ref _state.State.RtColorState[0];

            Logger.Warning?.PrintMsg(
                LogClass.Gpu,
                $"galdraw {(indexed ? "idx" : "arr")} count={count} inst={instanceCount} first={firstIndex} firstVtx={firstVertex} firstInst={firstInstance} " +
                $"topo={_drawState.Topology} vac={(_drawState.VertexAsCompute != null ? 1 : 0)} " +
                $"rt0={rt0.Format}/{rt0.WidthOrStride}x{rt0.Height} zeta={_state.State.RtDepthStencilState.Format} " +
                $"prog={GetTraceProgramLabel(_currentSpecState.CurrentGraphicsShader)}");
        }

        /// <summary>
        /// Creates a new instance of the draw manager.
        /// </summary>
        /// <param name="context">GPU context</param>
        /// <param name="channel">GPU channel</param>
        /// <param name="state">Channel state</param>
        /// <param name="drawState">Draw state</param>
        /// <param name="spec">Specialization state updater</param>
        public DrawManager(GpuContext context, GpuChannel channel, DeviceStateWithShadow<ThreedClassState> state, DrawState drawState, SpecializationStateUpdater spec)
        {
            _context = context;
            _channel = channel;
            _state = state;
            _drawState = drawState;
            _currentSpecState = spec;
            _vtgAsCompute = new(context, channel, state);
        }

        /// <summary>
        /// Marks the entire state as dirty, forcing a full host state update before the next draw.
        /// </summary>
        public void ForceStateDirty()
        {
            _topologySet = false;
        }

        /// <summary>
        /// Pushes four 8-bit index buffer elements.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void VbElementU8(int argument)
        {
            _drawState.IbStreamer.VbElementU8(_context.Renderer, argument);
        }

        /// <summary>
        /// Pushes two 16-bit index buffer elements.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void VbElementU16(int argument)
        {
            _drawState.IbStreamer.VbElementU16(_context.Renderer, argument);
        }

        /// <summary>
        /// Pushes one 32-bit index buffer element.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void VbElementU32(int argument)
        {
            _drawState.IbStreamer.VbElementU32(_context.Renderer, argument);
        }

        /// <summary>
        /// Finishes the draw call.
        /// This draws geometry on the bound buffers based on the current GPU state.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="argument">Method call argument</param>
        public void DrawEnd(ThreedClass engine, int argument)
        {
            _drawState.DrawUsesEngineState = true;

            DrawEnd(
                engine,
                _state.State.IndexBufferState.First,
                (int)_state.State.IndexBufferCount,
                _state.State.VertexBufferDrawState.First,
                _state.State.VertexBufferDrawState.Count);
        }

        /// <summary>
        /// Finishes the draw call.
        /// This draws geometry on the bound buffers based on the current GPU state.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="firstIndex">Index of the first index buffer element used on the draw</param>
        /// <param name="indexCount">Number of index buffer elements used on the draw</param>
        /// <param name="drawFirstVertex">Index of the first vertex used on the draw</param>
        /// <param name="drawVertexCount">Number of vertices used on the draw</param>
        private void DrawEnd(ThreedClass engine, int firstIndex, int indexCount, int drawFirstVertex, int drawVertexCount)
        {
            ConditionalRenderEnabled renderEnable = ConditionalRendering.GetRenderEnable(
                _context,
                _channel.MemoryManager,
                _state.State.RenderEnableAddress,
                _state.State.RenderEnableCondition);

            if (renderEnable == ConditionalRenderEnabled.False || _instancedDrawPending)
            {
                if (renderEnable == ConditionalRenderEnabled.False)
                {
                    PerformDeferredDraws(engine);
                }

                _drawState.DrawIndexed = false;

                if (renderEnable == ConditionalRenderEnabled.Host)
                {
                    _context.Renderer.Pipeline.EndHostConditionalRendering();
                }

                return;
            }

            _drawState.FirstIndex = firstIndex;
            _drawState.IndexCount = indexCount;
            _drawState.DrawFirstVertex = drawFirstVertex;
            _drawState.DrawVertexCount = drawVertexCount;
            _currentSpecState.SetHasConstantBufferDrawParameters(false);

            engine.UpdateState();

            bool instanced = _drawState.VsUsesInstanceId || _drawState.IsAnyVbInstanced;

            if (instanced)
            {
                _instancedDrawPending = true;

                int ibCount = _drawState.IbStreamer.InlineIndexCount;

                _instancedIndexed = _drawState.DrawIndexed;
                _instancedIndexedInline = ibCount != 0;

                _instancedFirstIndex = firstIndex;
                _instancedFirstVertex = (int)_state.State.FirstVertex;
                _instancedFirstInstance = (int)_state.State.FirstInstance;

                _instancedIndexCount = ibCount != 0 ? ibCount : indexCount;

                _instancedDrawStateFirst = drawFirstVertex;
                _instancedDrawStateCount = drawVertexCount;

                _drawState.DrawIndexed = false;

                if (renderEnable == ConditionalRenderEnabled.Host)
                {
                    _context.Renderer.Pipeline.EndHostConditionalRendering();
                }

                return;
            }

            int firstInstance = (int)_state.State.FirstInstance;

            if (DrawCensus.Enabled)
            {
                DrawCensus.Start();
                Interlocked.Increment(ref DrawCensus.Plain);

                if (_instanceIndex > 0)
                {
                    Interlocked.Increment(ref DrawCensus.PlainSplitInstance);
                }
            }

            int inlineIndexCount = _drawState.IbStreamer.GetAndResetInlineIndexCount(_context.Renderer);

            if (inlineIndexCount != 0)
            {
                int firstVertex = (int)_state.State.FirstVertex;

                BufferRange br = new(_drawState.IbStreamer.GetInlineIndexBuffer(), 0, inlineIndexCount * 4);

                _channel.BufferManager.SetIndexBuffer(br, IndexType.UInt);

                DrawImpl(engine, inlineIndexCount, 1, firstIndex, firstVertex, firstInstance, indexed: true);
            }
            else if (_drawState.DrawIndexed)
            {
                int firstVertex = (int)_state.State.FirstVertex;

                DrawImpl(engine, indexCount, 1, firstIndex, firstVertex, firstInstance, indexed: true);
            }
            else
            {
                DrawImpl(engine, drawVertexCount, 1, 0, drawFirstVertex, firstInstance, indexed: false);
            }

            _drawState.DrawIndexed = false;

            if (renderEnable == ConditionalRenderEnabled.Host)
            {
                _context.Renderer.Pipeline.EndHostConditionalRendering();
            }
        }

        /// <summary>
        /// Starts draw.
        /// This sets primitive type and instanced draw parameters.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="argument">Method call argument</param>
        public void DrawBegin(ThreedClass engine, int argument)
        {
            bool incrementInstance = (argument & (1 << 26)) != 0;
            bool resetInstance = (argument & (1 << 27)) == 0;

            PrimitiveType type = (PrimitiveType)(argument & 0xffff);
            DrawBegin(engine, incrementInstance, resetInstance, type);
        }

        /// <summary>
        /// Starts draw.
        /// This sets primitive type and instanced draw parameters.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="incrementInstance">Indicates if the current instance should be incremented</param>
        /// <param name="resetInstance">Indicates if the current instance should be set to zero</param>
        /// <param name="primitiveType">Primitive type</param>
        private void DrawBegin(ThreedClass engine, bool incrementInstance, bool resetInstance, PrimitiveType primitiveType)
        {
            if (incrementInstance)
            {
                _instanceIndex++;
            }
            else if (resetInstance)
            {
                PerformDeferredDraws(engine);

                _instanceIndex = 0;
            }

            PrimitiveTopology topology;

            if (_state.State.PrimitiveTypeOverrideEnable)
            {
                PrimitiveTypeOverride typeOverride = _state.State.PrimitiveTypeOverride;
                topology = typeOverride.Convert();
            }
            else
            {
                topology = primitiveType.Convert();
            }

            UpdateTopology(topology);
        }

        /// <summary>
        /// Updates the current primitive topology if needed.
        /// </summary>
        /// <param name="topology">New primitive topology</param>
        private void UpdateTopology(PrimitiveTopology topology)
        {
            if (_drawState.Topology != topology || !_topologySet)
            {
                _context.Renderer.Pipeline.SetPrimitiveTopology(topology);
                _currentSpecState.SetTopology(topology);
                _drawState.Topology = topology;
                _topologySet = true;
            }
        }

        /// <summary>
        /// Sets the index buffer count.
        /// This also sets internal state that indicates that the next draw is an indexed draw.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void SetIndexBufferCount(int argument)
        {
            _drawState.DrawIndexed = true;
        }

        // TODO: Verify if the index type is implied from the method that is called,
        // or if it uses the state index type on hardware.

        /// <summary>
        /// Performs a indexed draw with 8-bit index buffer elements.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="argument">Method call argument</param>
        public void DrawIndexBuffer8BeginEndInstanceFirst(ThreedClass engine, int argument)
        {
            DrawIndexBufferBeginEndInstance(engine, argument, false);
        }

        /// <summary>
        /// Performs a indexed draw with 16-bit index buffer elements.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="argument">Method call argument</param>
        public void DrawIndexBuffer16BeginEndInstanceFirst(ThreedClass engine, int argument)
        {
            DrawIndexBufferBeginEndInstance(engine, argument, false);
        }

        /// <summary>
        /// Performs a indexed draw with 32-bit index buffer elements.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="argument">Method call argument</param>
        public void DrawIndexBuffer32BeginEndInstanceFirst(ThreedClass engine, int argument)
        {
            DrawIndexBufferBeginEndInstance(engine, argument, false);
        }

        /// <summary>
        /// Performs a indexed draw with 8-bit index buffer elements,
        /// while also pre-incrementing the current instance value.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="argument">Method call argument</param>
        public void DrawIndexBuffer8BeginEndInstanceSubsequent(ThreedClass engine, int argument)
        {
            DrawIndexBufferBeginEndInstance(engine, argument, true);
        }

        /// <summary>
        /// Performs a indexed draw with 16-bit index buffer elements,
        /// while also pre-incrementing the current instance value.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="argument">Method call argument</param>
        public void DrawIndexBuffer16BeginEndInstanceSubsequent(ThreedClass engine, int argument)
        {
            DrawIndexBufferBeginEndInstance(engine, argument, true);
        }

        /// <summary>
        /// Performs a indexed draw with 32-bit index buffer elements,
        /// while also pre-incrementing the current instance value.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="argument">Method call argument</param>
        public void DrawIndexBuffer32BeginEndInstanceSubsequent(ThreedClass engine, int argument)
        {
            DrawIndexBufferBeginEndInstance(engine, argument, true);
        }

        /// <summary>
        /// Performs a indexed draw with a low number of index buffer elements,
        /// while optionally also pre-incrementing the current instance value.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="argument">Method call argument</param>
        /// <param name="instanced">True to increment the current instance value, false otherwise</param>
        private void DrawIndexBufferBeginEndInstance(ThreedClass engine, int argument, bool instanced)
        {
            DrawBegin(engine, instanced, !instanced, (PrimitiveType)((argument >> 28) & 0xf));

            int firstIndex = argument & 0xffff;
            int indexCount = (argument >> 16) & 0xfff;

            bool oldDrawIndexed = _drawState.DrawIndexed;

            _drawState.DrawIndexed = true;
            _drawState.DrawUsesEngineState = false;
            engine.ForceStateDirty(IndexBufferCountMethodOffset * 4);

            DrawEnd(engine, firstIndex, indexCount, 0, 0);

            _drawState.DrawIndexed = oldDrawIndexed;
        }

        /// <summary>
        /// Performs a non-indexed draw with the specified topology, index and count.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="argument">Method call argument</param>
        public void DrawVertexArrayBeginEndInstanceFirst(ThreedClass engine, int argument)
        {
            DrawVertexArrayBeginEndInstance(engine, argument, false);
        }

        /// <summary>
        /// Performs a non-indexed draw with the specified topology, index and count,
        /// while incrementing the current instance.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="argument">Method call argument</param>
        public void DrawVertexArrayBeginEndInstanceSubsequent(ThreedClass engine, int argument)
        {
            DrawVertexArrayBeginEndInstance(engine, argument, true);
        }

        /// <summary>
        /// Performs a indexed draw with a low number of index buffer elements,
        /// while optionally also pre-incrementing the current instance value.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="argument">Method call argument</param>
        /// <param name="instanced">True to increment the current instance value, false otherwise</param>
        private void DrawVertexArrayBeginEndInstance(ThreedClass engine, int argument, bool instanced)
        {
            DrawBegin(engine, instanced, !instanced, (PrimitiveType)((argument >> 28) & 0xf));

            int firstVertex = argument & 0xffff;
            int vertexCount = (argument >> 16) & 0xfff;

            bool oldDrawIndexed = _drawState.DrawIndexed;

            _drawState.DrawIndexed = false;
            _drawState.DrawUsesEngineState = false;
            engine.ForceStateDirty(VertexBufferFirstMethodOffset * 4);

            DrawEnd(engine, 0, 0, firstVertex, vertexCount);

            _drawState.DrawIndexed = oldDrawIndexed;
        }

        /// <summary>
        /// Performs a texture draw with a source texture and sampler ID, along with source
        /// and destination coordinates and sizes.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="argument">Method call argument</param>
        public void DrawTexture(ThreedClass engine, int argument)
        {
            static float FixedToFloat(int fixedValue)
            {
                return fixedValue * (1f / 4096);
            }

            float dstX0 = FixedToFloat(_state.State.DrawTextureDstX);
            float dstY0 = FixedToFloat(_state.State.DrawTextureDstY);
            float dstWidth = FixedToFloat(_state.State.DrawTextureDstWidth);
            float dstHeight = FixedToFloat(_state.State.DrawTextureDstHeight);

            // TODO: Confirm behaviour on hardware.
            // When this is active, the origin appears to be on the bottom.
            if ((_state.State.YControl & YControl.NegateY) != 0)
            {
                dstY0 -= dstHeight;
            }

            float dstX1 = dstX0 + dstWidth;
            float dstY1 = dstY0 + dstHeight;

            float srcX0 = FixedToFloat(_state.State.DrawTextureSrcX);
            float srcY0 = FixedToFloat(_state.State.DrawTextureSrcY);
            float srcX1 = ((float)_state.State.DrawTextureDuDx / (1UL << 32)) * dstWidth + srcX0;
            float srcY1 = ((float)_state.State.DrawTextureDvDy / (1UL << 32)) * dstHeight + srcY0;

            engine.UpdateState(ulong.MaxValue & ~(1UL << StateUpdater.ShaderStateIndex));

            _channel.TextureManager.UpdateRenderTargets();

            int textureId = _state.State.DrawTextureTextureId;
            int samplerId = _state.State.DrawTextureSamplerId;

            (Image.Texture texture, Sampler sampler) = _channel.TextureManager.GetGraphicsTextureAndSampler(textureId, samplerId);

            srcX0 *= texture.ScaleFactor;
            srcY0 *= texture.ScaleFactor;
            srcX1 *= texture.ScaleFactor;
            srcY1 *= texture.ScaleFactor;

            float dstScale = _channel.TextureManager.RenderTargetScale;

            dstX0 *= dstScale;
            dstY0 *= dstScale;
            dstX1 *= dstScale;
            dstY1 *= dstScale;

            Ryujinx.Common.SyncMemDiag.IncrementDrawTexture();
            _context.Renderer.Pipeline.DrawTexture(
                texture?.HostTexture,
                sampler?.GetHostSampler(texture),
                new Extents2DF(srcX0, srcY0, srcX1, srcY1),
                new Extents2DF(dstX0, dstY0, dstX1, dstY1));
        }

        /// <summary>
        /// Performs a indexed or non-indexed draw.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="topology">Primitive topology</param>
        /// <param name="count">Index count for indexed draws, vertex count for non-indexed draws</param>
        /// <param name="instanceCount">Instance count</param>
        /// <param name="firstIndex">First index on the index buffer for indexed draws, ignored for non-indexed draws</param>
        /// <param name="firstVertex">First vertex on the vertex buffer</param>
        /// <param name="firstInstance">First instance</param>
        /// <param name="indexed">True if the draw is indexed, false otherwise</param>
        public void Draw(
            ThreedClass engine,
            PrimitiveTopology topology,
            int count,
            int instanceCount,
            int firstIndex,
            int firstVertex,
            int firstInstance,
            bool indexed)
        {
            UpdateTopology(topology);

            ConditionalRenderEnabled renderEnable = ConditionalRendering.GetRenderEnable(
                _context,
                _channel.MemoryManager,
                _state.State.RenderEnableAddress,
                _state.State.RenderEnableCondition);

            if (renderEnable == ConditionalRenderEnabled.False)
            {
                _drawState.DrawIndexed = false;
                return;
            }

            if (indexed)
            {
                _drawState.FirstIndex = firstIndex;
                _drawState.IndexCount = count;
                _state.State.FirstVertex = (uint)firstVertex;
                engine.ForceStateDirty(IndexBufferCountMethodOffset * 4);
            }
            else
            {
                _drawState.DrawFirstVertex = firstVertex;
                _drawState.DrawVertexCount = count;
                engine.ForceStateDirty(VertexBufferFirstMethodOffset * 4);
            }

            _state.State.FirstInstance = (uint)firstInstance;

            _drawState.DrawIndexed = indexed;
            _drawState.DrawUsesEngineState = true;
            _currentSpecState.SetHasConstantBufferDrawParameters(true);

            engine.UpdateState();

            if (DrawCensus.Enabled)
            {
                DrawCensus.Start();
                Interlocked.Increment(ref DrawCensus.Macro);
                Interlocked.Add(ref DrawCensus.MacroInstances, instanceCount);
                DrawCensus.NoteMacroDraw(indexed ? _state.State.IndexBufferState.Address.Pack() : 0, firstIndex, count, firstVertex, instanceCount, firstInstance);
                DrawCensus.MaybeWatch(_channel.MemoryManager, count);
            }

            DrawImpl(engine, count, instanceCount, firstIndex, firstVertex, firstInstance, indexed);

            if (indexed)
            {
                _state.State.FirstVertex = 0;
            }

            _state.State.FirstInstance = 0;

            _drawState.DrawIndexed = false;

            if (renderEnable == ConditionalRenderEnabled.Host)
            {
                _context.Renderer.Pipeline.EndHostConditionalRendering();
            }
        }

        /// <summary>
        /// Performs a indexed or non-indexed draw.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="count">Index count for indexed draws, vertex count for non-indexed draws</param>
        /// <param name="instanceCount">Instance count</param>
        /// <param name="firstIndex">First index on the index buffer for indexed draws, ignored for non-indexed draws</param>
        /// <param name="firstVertex">First vertex on the vertex buffer</param>
        /// <param name="firstInstance">First instance</param>
        /// <param name="indexed">True if the draw is indexed, false otherwise</param>
        private void DrawImpl(
            ThreedClass engine,
            int count,
            int instanceCount,
            int firstIndex,
            int firstVertex,
            int firstInstance,
            bool indexed)
        {
            TraceDrawIfActive(count, instanceCount, firstIndex, firstVertex, firstInstance, indexed);

            // Skip-list check sits here, next to the trace, so it sees exactly the
            // program the trace labels (the current shader is only valid after the
            // engine state update that precedes this call).
            ReloadSkipListIfChanged();

            if (_watchProg != null &&
                GetTraceProgramLabel(_currentSpecState.CurrentGraphicsShader) == _watchProg)
            {
                Ryujinx.Common.SyncMemDiag.IncrementWatchedDraw();

                if (_watchConst)
                {
                    // out.rgb = (texA * texB) * fp_c3.data[0] + fp_c3.data[1] and the UVs
                    // come from vp_c3.data[33] - so a black frame from a draw that runs
                    // with the right textures bound has to come from these constants.
                    // Fragment stage is 4, vertex 0; the shaders bind c3 as index 3.
                    ulong fAddr = _channel.BufferManager.GetGraphicsUniformBufferAddress(4, 3);
                    ulong vAddr = _channel.BufferManager.GetGraphicsUniformBufferAddress(0, 3);
                    float f0 = 0, f1 = 0, v33 = 0;

                    if (fAddr != 0)
                    {
                        ReadOnlySpan<byte> fb = _channel.MemoryManager.Physical.GetSpan(fAddr, 32);
                        f0 = BitConverter.ToSingle(fb[..4]);
                        f1 = BitConverter.ToSingle(fb.Slice(16, 4));
                    }

                    if (vAddr != 0)
                    {
                        ReadOnlySpan<byte> vb = _channel.MemoryManager.Physical.GetSpan(vAddr + 33 * 16, 4);
                        v33 = BitConverter.ToSingle(vb);
                    }

                    Ryujinx.Common.SyncMemDiag.NoteWatchedConst(f0, f1, v33);
                }

                if (!_watchStagesLogged)
                {
                    // Name the watched program's stages by the same guest-code hash that
                    // RYUJINX_SHADER_DIFF uses for its dumps, so the translated source of
                    // the draw that composites the map can actually be read.
                    _watchStagesLogged = true;
                    CachedShaderProgram wp = _currentSpecState.CurrentGraphicsShader;
                    int si = 0;
                    foreach (CachedShaderStage st in wp.Shaders)
                    {
                        if (st?.Code != null)
                        {
                            string h = System.Convert.ToHexString(
                                System.Security.Cryptography.MD5.HashData(st.Code))[..16];
                            Logger.Warning?.PrintMsg(LogClass.Gpu,
                                $"watchstage idx={si} hash={h} bytes={st.Code.Length}");
                        }
                        si++;
                    }
                }

                if (_watchBarrier)
                {
                    // The map's layers are composed into a texture and then blitted to the
                    // HDR target by this single fullscreen quad. On a flicker frame the quad
                    // runs and binds the same texture as always, yet nothing appears - which
                    // is what a read that outruns the writes into that texture looks like.
                    // If forcing the writes to land first removes the flicker, that is the
                    // hazard. RYUJINX_WATCH_BARRIER=1.
                    _context.Renderer.Pipeline.Barrier();
                }
                Ryujinx.Common.SyncMemDiag.NoteWatchedTex(
                    Image.TextureBindingsManager.LastBoundTex[128],
                    Image.TextureBindingsManager.LastBoundTex[129]);
                Ryujinx.Common.SyncMemDiag.NoteWatchedSeq(
                    Image.TextureBindingsManager.LastBoundSeq[128],
                    Image.TextureBindingsManager.LastBoundSeq[129]);

                // One frame in a thousand this binding resolves to a different texture and the
                // map layer vanishes. Print only the transition, and only for this program:
                // binding 128 is shared by hundreds of shaders, logging every swap there buried
                // this draw under 540k lines a minute.
                int wtNow = Image.TextureBindingsManager.LastBoundTex[128];

                if (wtNow != _watchTexPrev)
                {
                    int why = Image.TextureBindingsManager.LastBoundWhy[128];
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                        $"texswap {_watchTexPrev:X8}->{wtNow:X8} id={_watchIdPrev}->{Image.TextureBindingsManager.LastBoundId[128]} " +
                        $"packed={_watchPackedPrev:X8}->{Image.TextureBindingsManager.LastBoundPacked[128]:X8} " +
                        $"why[poolMod={(why & 1) != 0} idChg={(why & 2) != 0} cachedNull={(why & 4) != 0} " +
                        $"invSeqBad={(why & 8) != 0} smpGone={(why & 16) != 0} fastPath={(why & 32) != 0}] " +
                        $"desc[{Image.TextureBindingsManager.LastBoundDesc[128]}] " +
                        $"tex[{Image.TextureBindingsManager.LastBoundTexInfo[128]}]");

                    _watchTexPrev = wtNow;
                    _watchIdPrev = Image.TextureBindingsManager.LastBoundId[128];
                    _watchPackedPrev = Image.TextureBindingsManager.LastBoundPacked[128];
                }
            }

            if (_skipProgs.Count != 0 &&
                _skipProgs.Contains(GetTraceProgramLabel(_currentSpecState.CurrentGraphicsShader)))
            {
                return;
            }

            if (instanceCount > 1)
            {
                _channel.BufferManager.SetInstancedDrawVertexCount(count);
            }

            if (_drawState.VertexAsCompute != null)
            {
                _vtgAsCompute.DrawAsCompute(
                    engine,
                    _drawState.VertexAsCompute,
                    _drawState.GeometryAsCompute,
                    _drawState.VertexPassthrough,
                    _drawState.Topology,
                    count,
                    instanceCount,
                    firstIndex,
                    firstVertex,
                    firstInstance,
                    indexed);

                if (_drawState.GeometryAsCompute != null)
                {
                    // Geometry draws need to change the topology, so we need to set it here again
                    // if we are going to do a regular draw.
                    // Would have been better to do that on the callee, but doing it here
                    // avoids having to pass the draw manager instance.
                    ForceStateDirty();
                }
            }
            else
            {
                if (indexed)
                {
                    Ryujinx.Common.SyncMemDiag.IncrementDraw();
                    _context.Renderer.Pipeline.DrawIndexed(count, instanceCount, firstIndex, firstVertex, firstInstance);
                }
                else
                {
                    Ryujinx.Common.SyncMemDiag.IncrementDraw();
                    _context.Renderer.Pipeline.Draw(count, instanceCount, firstVertex, firstInstance);
                }
            }

            _context.NoteDrawForSyncCadence();
        }

        /// <summary>
        /// Performs a indirect draw, with parameters from a GPU buffer.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="topology">Primitive topology</param>
        /// <param name="indirectBufferRange">Memory range of the buffer with the draw parameters, such as count, first index, etc</param>
        /// <param name="parameterBufferRange">Memory range of the buffer with the draw count</param>
        /// <param name="maxDrawCount">Maximum number of draws that can be made</param>
        /// <param name="stride">Distance in bytes between each entry on the data pointed to by <paramref name="indirectBufferAddress"/></param>
        /// <param name="indexCount">Maximum number of indices that the draw can consume</param>
        /// <param name="drawType">Type of the indirect draw, which can be indexed or non-indexed, with or without a draw count</param>
        public void DrawIndirect(
            ThreedClass engine,
            PrimitiveTopology topology,
            MultiRange indirectBufferRange,
            MultiRange parameterBufferRange,
            int maxDrawCount,
            int stride,
            int indexCount,
            IndirectDrawType drawType)
        {
            UpdateTopology(topology);

            ConditionalRenderEnabled renderEnable = ConditionalRendering.GetRenderEnable(
                _context,
                _channel.MemoryManager,
                _state.State.RenderEnableAddress,
                _state.State.RenderEnableCondition);

            if (renderEnable == ConditionalRenderEnabled.False)
            {
                _drawState.DrawIndexed = false;
                return;
            }

            PhysicalMemory memory = _channel.MemoryManager.Physical;

            bool hasCount = (drawType & IndirectDrawType.Count) != 0;
            bool indexed = (drawType & IndirectDrawType.Indexed) != 0;

            if (indexed)
            {
                indexCount = Math.Clamp(indexCount, MinIndirectIndexCount, MaxIndirectIndexCount);
                _drawState.FirstIndex = 0;
                _drawState.IndexCount = indexCount;
                engine.ForceStateDirty(IndexBufferCountMethodOffset * 4);
            }

            _drawState.DrawIndexed = indexed;
            _drawState.DrawIndirect = true;
            _drawState.DrawUsesEngineState = true;
            _currentSpecState.SetHasConstantBufferDrawParameters(true);

            engine.UpdateState();

            if (_traceRemaining > 0)
            {
                ref RtColorState rt0 = ref _state.State.RtColorState[0];

                Logger.Warning?.PrintMsg(
                    LogClass.Gpu,
                    $"galdraw {(indexed ? "indirect-idx" : "indirect-arr")}{(hasCount ? "-count" : "")} maxDraws={maxDrawCount} stride={stride} " +
                    $"topo={_drawState.Topology} rt0={rt0.Format}/{rt0.WidthOrStride}x{rt0.Height} zeta={_state.State.RtDepthStencilState.Format} " +
                    $"prog={GetTraceProgramLabel(_currentSpecState.CurrentGraphicsShader)}");
            }

            if (hasCount)
            {
                BufferRange indirectBuffer = memory.BufferCache.GetBufferRange(indirectBufferRange, BufferStage.Indirect);
                BufferRange parameterBuffer = memory.BufferCache.GetBufferRange(parameterBufferRange, BufferStage.Indirect);

                if (indexed)
                {
                    Ryujinx.Common.SyncMemDiag.IncrementIndirectDraw();
                    _context.Renderer.Pipeline.DrawIndexedIndirectCount(indirectBuffer, parameterBuffer, maxDrawCount, stride);
                }
                else
                {
                    Ryujinx.Common.SyncMemDiag.IncrementIndirectDraw();
                    _context.Renderer.Pipeline.DrawIndirectCount(indirectBuffer, parameterBuffer, maxDrawCount, stride);
                }
            }
            else
            {
                BufferRange indirectBuffer = memory.BufferCache.GetBufferRange(indirectBufferRange, BufferStage.Indirect);

                if (indexed)
                {
                    Ryujinx.Common.SyncMemDiag.IncrementIndirectDraw();
                    _context.Renderer.Pipeline.DrawIndexedIndirect(indirectBuffer);
                }
                else
                {
                    Ryujinx.Common.SyncMemDiag.IncrementIndirectDraw();
                    _context.Renderer.Pipeline.DrawIndirect(indirectBuffer);
                }
            }

            _drawState.DrawIndexed = false;
            _drawState.DrawIndirect = false;

            if (renderEnable == ConditionalRenderEnabled.Host)
            {
                _context.Renderer.Pipeline.EndHostConditionalRendering();
            }
        }

        /// <summary>
        /// Perform any deferred draws.
        /// This is used for instanced draws.
        /// Since each instance is a separate draw, we defer the draw and accumulate the instance count.
        /// Once we detect the last instanced draw, then we perform the host instanced draw,
        /// with the accumulated instance count.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        public void PerformDeferredDraws(ThreedClass engine)
        {
            // Perform any pending instanced draw.
            if (_instancedDrawPending)
            {
                _instancedDrawPending = false;

                int instanceCount = _instanceIndex + 1;
                int firstInstance = _instancedFirstInstance;
                bool indexedInline = _instancedIndexedInline;

                if (DrawCensus.Enabled)
                {
                    DrawCensus.Start();
                    Interlocked.Increment(ref DrawCensus.Merged);
                    Interlocked.Add(ref DrawCensus.MergedInstances, instanceCount);
                }

                if (_instancedIndexed || indexedInline)
                {
                    int indexCount = _instancedIndexCount;

                    if (indexedInline)
                    {
                        int inlineIndexCount = _drawState.IbStreamer.GetAndResetInlineIndexCount(_context.Renderer);
                        BufferRange br = new(_drawState.IbStreamer.GetInlineIndexBuffer(), 0, inlineIndexCount * 4);

                        _channel.BufferManager.SetIndexBuffer(br, IndexType.UInt);
                        indexCount = inlineIndexCount;
                    }

                    int firstIndex = _instancedFirstIndex;
                    int firstVertex = _instancedFirstVertex;

                    DrawImpl(engine, indexCount, instanceCount, firstIndex, firstVertex, firstInstance, indexed: true);
                }
                else
                {
                    int vertexCount = _instancedDrawStateCount;
                    int firstVertex = _instancedDrawStateFirst;

                    DrawImpl(engine, vertexCount, instanceCount, 0, firstVertex, firstInstance, indexed: false);
                }
            }
        }

        /// <summary>
        /// Clears the current color and depth-stencil buffers.
        /// Which buffers should be cleared can also be specified with the argument.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="argument">Method call argument</param>
        public void Clear(ThreedClass engine, int argument)
        {
            Clear(engine, argument, 1);
        }

        /// <summary>
        /// Clears the current color and depth-stencil buffers.
        /// Which buffers should be cleared can also specified with the arguments.
        /// </summary>
        /// <param name="engine">3D engine where this method is being called</param>
        /// <param name="argument">Method call argument</param>
        /// <param name="layerCount">For array and 3D textures, indicates how many layers should be cleared</param>
        public void Clear(ThreedClass engine, int argument, int layerCount)
        {
            ConditionalRenderEnabled renderEnable = ConditionalRendering.GetRenderEnable(
                _context,
                _channel.MemoryManager,
                _state.State.RenderEnableAddress,
                _state.State.RenderEnableCondition);

            if (renderEnable == ConditionalRenderEnabled.False)
            {
                return;
            }

            bool clearDepth = (argument & 1) != 0;
            bool clearStencil = (argument & 2) != 0;
            uint componentMask = (uint)((argument >> 2) & 0xf);
            int index = (argument >> 6) & 0xf;
            int layer = (argument >> 10) & 0x3ff;

            RenderTargetUpdateFlags updateFlags = RenderTargetUpdateFlags.SingleColor;

            if (layer != 0 || layerCount > 1)
            {
                updateFlags |= RenderTargetUpdateFlags.Layered;
            }

            bool clearDS = clearDepth || clearStencil;

            if (clearDS)
            {
                updateFlags |= RenderTargetUpdateFlags.UpdateDepthStencil;
            }

            // If there is a mismatch on the host clip region and the one explicitly defined by the guest
            // on the screen scissor state, then we need to force only one texture to be bound to avoid
            // host clipping.
            ScreenScissorState screenScissorState = _state.State.ScreenScissorState;
            
            Span<ScissorState> scissorStateSpan = _state.State.ScissorState.AsSpan();

            bool clearAffectedByStencilMask = (_state.State.ClearFlags & 1) != 0;
            bool clearAffectedByScissor = (_state.State.ClearFlags & 0x100) != 0;

            if (clearDS || componentMask == 15)
            {
                // A full clear if scissor is disabled, or it matches the screen scissor state.

                bool fullClear = screenScissorState.X == 0 && screenScissorState.Y == 0;

                if (fullClear && clearAffectedByScissor && scissorStateSpan[0].Enable)
                {
                    ref ScissorState scissorState = ref scissorStateSpan[0];

                    fullClear = scissorState.X1 == screenScissorState.X &&
                        scissorState.Y1 == screenScissorState.Y &&
                        scissorState.X2 >= screenScissorState.X + screenScissorState.Width &&
                        scissorState.Y2 >= screenScissorState.Y + screenScissorState.Height;
                }

                if (fullClear && clearDS)
                {
                    // Must clear all aspects of the depth-stencil format.

                    FormatInfo dsFormat = _state.State.RtDepthStencilState.Format.Convert();

                    bool hasDepth = dsFormat.Format.HasDepth;
                    bool hasStencil = dsFormat.Format.HasStencil;

                    if (hasStencil && (!clearStencil || (clearAffectedByStencilMask && _state.State.StencilTestState.FrontMask != 0xff)))
                    {
                        fullClear = false;
                    }
                    else if (hasDepth && !clearDepth)
                    {
                        fullClear = false;
                    }
                }

                if (fullClear)
                {
                    updateFlags |= RenderTargetUpdateFlags.DiscardClip;
                }
            }

            engine.UpdateRenderTargetState(updateFlags, singleUse: componentMask != 0 ? index : -1);

            // Must happen after UpdateRenderTargetState to have up-to-date clip region values.
            bool clipMismatch = (screenScissorState.X | screenScissorState.Y) != 0 ||
                                screenScissorState.Width != _channel.TextureManager.ClipRegionWidth ||
                                screenScissorState.Height != _channel.TextureManager.ClipRegionHeight;

            bool needsCustomScissor = !clearAffectedByScissor || clipMismatch;

            // Scissor and rasterizer discard also affect clears.
            ulong updateMask = 1UL << StateUpdater.RasterizerStateIndex;

            if (!needsCustomScissor)
            {
                updateMask |= 1UL << StateUpdater.ScissorStateIndex;
            }

            engine.UpdateState(updateMask);

            if (needsCustomScissor)
            {
                int scissorX = screenScissorState.X;
                int scissorY = screenScissorState.Y;
                int scissorW = screenScissorState.Width;
                int scissorH = screenScissorState.Height;

                if (clearAffectedByScissor && scissorStateSpan[0].Enable)
                {
                    ref ScissorState scissorState = ref scissorStateSpan[0];

                    scissorX = Math.Max(scissorX, scissorState.X1);
                    scissorY = Math.Max(scissorY, scissorState.Y1);
                    scissorW = Math.Min(scissorW, scissorState.X2 - scissorState.X1);
                    scissorH = Math.Min(scissorH, scissorState.Y2 - scissorState.Y1);
                }

                float scale = _channel.TextureManager.RenderTargetScale;
                if (scale != 1f)
                {
                    scissorX = (int)(scissorX * scale);
                    scissorY = (int)(scissorY * scale);
                    scissorW = (int)MathF.Ceiling(scissorW * scale);
                    scissorH = (int)MathF.Ceiling(scissorH * scale);
                }

                Span<Rectangle<int>> scissors =
                [
                    new(scissorX, scissorY, scissorW, scissorH)
                ];

                _context.Renderer.Pipeline.SetScissors(scissors);
            }

            _channel.TextureManager.UpdateRenderTargets();

            if (componentMask != 0)
            {
                ClearColors clearColor = _state.State.ClearColors;

                ColorF color = new(clearColor.Red, clearColor.Green, clearColor.Blue, clearColor.Alpha);

                _context.Renderer.Pipeline.ClearRenderTargetColor(index, layer, layerCount, componentMask, color);
            }

            if (clearDepth || clearStencil)
            {
                float depthValue = _state.State.ClearDepthValue;
                int stencilValue = (int)_state.State.ClearStencilValue;

                int stencilMask = 0;

                if (clearStencil)
                {
                    stencilMask = clearAffectedByStencilMask ? _state.State.StencilTestState.FrontMask : 0xff;
                }

                if (clipMismatch)
                {
                    _channel.TextureManager.UpdateRenderTargetDepthStencil();
                }

                _context.Renderer.Pipeline.ClearRenderTargetDepthStencil(
                    layer,
                    layerCount,
                    depthValue,
                    clearDepth,
                    stencilValue,
                    stencilMask);
            }

            if (needsCustomScissor)
            {
                engine.UpdateScissorState();
            }

            engine.UpdateRenderTargetState(RenderTargetUpdateFlags.UpdateAll);

            if (renderEnable == ConditionalRenderEnabled.Host)
            {
                _context.Renderer.Pipeline.EndHostConditionalRendering();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _vtgAsCompute.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
