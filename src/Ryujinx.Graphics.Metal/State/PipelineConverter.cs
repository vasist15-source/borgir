using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using SharpMetal.Metal;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    static class PipelineConverter
    {
        /// <summary>
        /// The pipeline state a draw would build from this GAL state, for building it ahead of
        /// the draw (<see cref="PipelinePrewarmer"/>). It has to come out equal to the key
        /// <see cref="EncoderStateManager"/> assembles from the same state one call at a time,
        /// or the prebuilt pipeline is never looked up, so every step mirrors the setter that
        /// does it there: render targets as UpdateRenderTargetsInternal (host formats through
        /// <see cref="Texture.HostFormat"/>), blend and write masks as UpdateBlendDescriptors and
        /// UpdateRenderTargetColorMasks, vertex input through the same
        /// <see cref="PipelineState.UpdateVertexState"/>. What the encoder never sets (sample
        /// count, primitive restart, patch control points) stays at the Initialize values it
        /// keeps, and alpha to coverage, which the GAL state does not record, stays off.
        /// </summary>
        public static PipelineState ToMetalPipelineState(this ProgramPipelineState state)
        {
            PipelineState pipeline = new();
            pipeline.Initialize();

            ref Array8<ColorBlendStateUid> blendState = ref pipeline.Internal.ColorBlendState;

            pipeline.ColorBlendAttachmentStateCount = Constants.MaxColorAttachments;

            for (int i = 0; i < Constants.MaxColorAttachments; i++)
            {
                uint componentMask = state.ColorWriteMask[i];

                MTLColorWriteMask mask = MTLColorWriteMask.None;

                mask |= (componentMask & (0x1 << 0)) != 0 ? MTLColorWriteMask.Red : 0;
                mask |= (componentMask & (0x1 << 1)) != 0 ? MTLColorWriteMask.Green : 0;
                mask |= (componentMask & (0x1 << 2)) != 0 ? MTLColorWriteMask.Blue : 0;
                mask |= (componentMask & (0x1 << 3)) != 0 ? MTLColorWriteMask.Alpha : 0;

                ref ColorBlendStateUid mtlBlend = ref blendState[i];

                // A masked-out attachment carries no blend state, whichever order the encoder
                // saw the blend and the mask in.
                if (mask != 0)
                {
                    BlendDescriptor blend = state.BlendDescriptors[i];

                    mtlBlend.Enable = blend.Enable;
                    mtlBlend.AlphaBlendOperation = blend.AlphaOp.Convert();
                    mtlBlend.RgbBlendOperation = blend.ColorOp.Convert();
                    mtlBlend.SourceAlphaBlendFactor = blend.AlphaSrcFactor.Convert();
                    mtlBlend.DestinationAlphaBlendFactor = blend.AlphaDstFactor.Convert();
                    mtlBlend.SourceRGBBlendFactor = blend.ColorSrcFactor.Convert();
                    mtlBlend.DestinationRGBBlendFactor = blend.ColorDstFactor.Convert();
                }
                else
                {
                    mtlBlend = new ColorBlendStateUid();
                }

                mtlBlend.WriteMask = mask;
                mtlBlend.PixelFormat = state.AttachmentEnable[i]
                    ? Texture.HostFormat(state.AttachmentFormats[i])
                    : MTLPixelFormat.Invalid;
            }

            pipeline.DepthStencilFormat = state.DepthStencilEnable
                ? Texture.HostFormat(state.DepthStencilFormat)
                : MTLPixelFormat.Invalid;

            pipeline.LogicOpEnable = state.LogicOpEnable;
            pipeline.LogicOp = state.LogicOp.Convert();
            pipeline.RasterizerDiscardEnable = state.RasterizerDiscard;

            // The encoder keeps fixed-size arrays and the GPU fills their first entries, so
            // the rest stay at their defaults there too.
            VertexBufferState[] vertexBuffers = new VertexBufferState[Constants.MaxVertexBuffers];
            VertexAttribDescriptor[] vertexAttribs = new VertexAttribDescriptor[Constants.MaxVertexAttributes];

            for (int i = 0; i < vertexBuffers.Length; i++)
            {
                BufferPipelineDescriptor buffer = state.VertexBuffers[i];

                vertexBuffers[i] = i < state.VertexBufferCount
                    ? new VertexBufferState(BufferHandle.Null, 0, 0, buffer.Divisor, buffer.Stride)
                    : VertexBufferState.Null;
            }

            state.VertexAttribs.AsSpan()[..Math.Min(state.VertexAttribCount, vertexAttribs.Length)].CopyTo(vertexAttribs);

            pipeline.UpdateVertexState(vertexBuffers, vertexAttribs);

            return pipeline;
        }
    }
}
