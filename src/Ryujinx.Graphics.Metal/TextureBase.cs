using Ryujinx.Graphics.GAL;
using SharpMetal.Metal;
using System;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    abstract class TextureBase : IDisposable
    {
        private int _isValid = 1;

        public bool Valid => Volatile.Read(ref _isValid) != 0;

        protected readonly Pipeline Pipeline;
        protected readonly MTLDevice Device;
        protected readonly MetalRenderer Renderer;

        protected Auto<DisposableTexture> MtlTextureAuto;

        protected MTLTexture MtlTexture => MtlTextureAuto?.GetUnsafe().Value ?? new MTLTexture(IntPtr.Zero);

        public readonly TextureCreateInfo Info;
        public int Width => Info.Width;
        public int Height => Info.Height;
        public int Depth => Info.Depth;

        public MTLPixelFormat MtlFormat { get; protected set; }
        public int FirstLayer { get; protected set; }
        public int FirstLevel { get; protected set; }

        public TextureBase(MTLDevice device, MetalRenderer renderer, Pipeline pipeline, TextureCreateInfo info)
        {
            Device = device;
            Renderer = renderer;
            Pipeline = pipeline;
            Info = info;
        }

        public MTLTexture GetHandle()
        {
            if (_isValid == 0)
            {
                return new MTLTexture(IntPtr.Zero);
            }

            return MtlTexture;
        }

        public MTLTexture GetHandle(CommandBufferScoped cbs)
        {
            if (_isValid == 0 || MtlTextureAuto == null)
            {
                return new MTLTexture(IntPtr.Zero);
            }

            return MtlTextureAuto.Get(cbs).Value;
        }

        /// <summary>
        /// Handle for blit/copy operations. Metal does not allow copying through a
        /// swizzled texture view, and copies move raw texel data anyway, so they
        /// must use the identity-swizzle view of the texture.
        /// </summary>
        public virtual MTLTexture GetIdentityHandle()
        {
            return GetHandle();
        }

        public virtual MTLTexture GetIdentityHandle(CommandBufferScoped cbs)
        {
            return GetHandle(cbs);
        }

        protected void SetHandle(MTLTexture texture, params IAutoPrivate[] referencedObjs)
        {
            if (LiveResources.Enabled)
            {
                LiveResources.NoteTexture(LiveKind.TextureView, texture);
            }

            MtlTextureAuto = new Auto<DisposableTexture>(new DisposableTexture(texture), null, referencedObjs);
        }

        private static int _offThreadHandleLogs;

        /// <summary>
        /// The 14:34 crash was the backend thread segfaulting inside
        /// drawIndexedPrimitives - a native object dying under a draw being encoded.
        /// Handle replacement and disposal are the two operations that can do that. This
        /// names any caller that performs them off the backend thread, with its stack,
        /// so the racing writer is identified from a normal session instead of deduced
        /// from a crash dump. Log-only: behaviour is unchanged.
        /// </summary>
        private void NoteOffThreadHandleMutation(string what)
        {
            if (Renderer?.CommandBufferPool != null &&
                !Renderer.CommandBufferPool.OwnedByCurrentThread &&
                _offThreadHandleLogs++ < 20)
            {
                Ryujinx.Common.Logging.Logger.Warning?.PrintMsg(Ryujinx.Common.Logging.LogClass.Gpu,
                    $"off-thread texture {what} on '{System.Threading.Thread.CurrentThread.Name}' {Info.Width}x{Info.Height} {MtlFormat}\n{Environment.StackTrace}");
            }
        }

        protected void ReplaceHandle(MTLTexture texture, params IAutoPrivate[] referencedObjs)
        {
            NoteOffThreadHandleMutation("replace");

            // The underlying MTLTexture changes while CanonicalPtr keeps its creation-time
            // value, so every canonical-keyed identity measurement is blind to this exact
            // operation. The generation counter is what makes a swap observable.
            if (this is Texture swapped)
            {
                UploadCorrelator.BumpHandleGen(swapped.CanonicalPtr);
            }

            if (LiveResources.Enabled)
            {
                LiveResources.NoteTexture(LiveKind.BufferTexture, texture);
            }

            Auto<DisposableTexture> oldTexture = MtlTextureAuto;

            MtlTextureAuto = texture != IntPtr.Zero
                ? new Auto<DisposableTexture>(new DisposableTexture(texture), null, referencedObjs)
                : null;

            oldTexture?.Dispose();
        }

        protected bool TryInvalidate()
        {
            return Interlocked.Exchange(ref _isValid, 0) != 0;
        }

        protected void DisposeHandle()
        {
            NoteOffThreadHandleMutation("dispose");

            Auto<DisposableTexture> texture = MtlTextureAuto;
            MtlTextureAuto = null;
            texture?.Dispose();
        }

        public virtual void Release()
        {
            if (TryInvalidate())
            {
                DisposeHandle();
            }
        }

        public void Dispose()
        {
            Release();
        }
    }
}
