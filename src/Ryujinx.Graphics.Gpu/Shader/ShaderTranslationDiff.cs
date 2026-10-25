using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.IO;
using System.Security.Cryptography;

namespace Ryujinx.Graphics.Gpu.Shader
{
    /// <summary>
    /// Writes both translations of the same guest shader side by side: the MSL the Metal
    /// backend will run, and the GLSL the same front-end produces for OpenGL/Vulkan.
    ///
    /// The white flash exists on both backends but 175 times more often on Metal, so the
    /// trigger is plausibly guest shader semantics that both translations must compensate
    /// for and that the MSL one compensates for worse. This codegen has already produced
    /// two real bugs this week - signed-overflow UB and an invalid float literal that
    /// silently nulled a pipeline - which makes it the prior suspect rather than a guess.
    ///
    /// Doing it here rather than by running the game twice is what makes it cheap: the
    /// decoder, the IR and every optimisation pass are shared, so the two outputs differ
    /// only where the backends differ. Nothing has to reproduce, nothing has to be timed,
    /// and the pairs land on disk during ordinary shader compilation.
    ///
    /// RYUJINX_SHADER_DIFF=&lt;directory&gt;. Each pair is named by the hash of the guest code,
    /// so the same program is one pair however many times it is compiled, and the names
    /// line up with the program labels the Metal probes report.
    /// </summary>
    static class ShaderTranslationDiff
    {
        private static readonly string _path =
            Environment.GetEnvironmentVariable("RYUJINX_SHADER_DIFF");

        public static bool Enabled => !string.IsNullOrEmpty(_path);

        public static void Dump(IGpuAccessor gpuAccessor, TranslatorContext context, ShaderProgram translated, byte[] code, bool asCompute)
        {
            if (!Enabled || gpuAccessor == null || code == null || code.Length == 0)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_path);

                string name = Convert.ToHexString(MD5.HashData(code))[..16];
                string stem = Path.Combine(_path, $"{name}-{context.Stage}");

                if (File.Exists(stem + ".seen"))
                {
                    return;
                }

                File.WriteAllText(stem + ".seen", string.Empty);
                File.WriteAllBytes(stem + ".bin", code);

                // The primary artifact: whatever the backend running right now actually
                // compiles, written with no re-translation at all. Under Metal that is
                // MSL text; under Vulkan it is a SPIR-V module and Code is null. Run the
                // game once per backend into two directories and the guest-code hash in
                // the filename lines the pairs up.
                //
                // Re-deriving the other backend's output in-process was the obvious way
                // to get both from one run, and it does not work for the shader this
                // investigation is about: re-translating it to SPIR-V throws
                // InvalidCastException in SpirvGenerator, while the very same shader
                // translates fine as the primary. So the only trustworthy SPIR-V is the
                // one a Vulkan run produces for itself.
                if (translated.BinaryCode != null)
                {
                    File.WriteAllBytes(stem + ".spv", translated.BinaryCode);
                }

                if (translated.Code != null)
                {
                    File.WriteAllText(stem + ".msl", translated.Code);
                }

                // The same guest program through the same front-end, targeting OpenGL so
                // the GLSL backend runs instead of the MSL one. Everything before code
                // emission is identical, so a difference in these two files is a
                // difference between the backends and nothing else.
                TranslatorContext glslContext = context.Stage == ShaderStage.Compute
                    ? ShaderCache.DecodeComputeShader(gpuAccessor, TargetApi.OpenGL, context.Address)
                    : ShaderCache.DecodeGraphicsShader(gpuAccessor, TargetApi.OpenGL, TranslationFlags.None, context.Address);

                File.WriteAllText(stem + ".glsl", glslContext.Translate(asCompute).Code ?? "<no code>");

            }
            catch (Exception exception)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"shader diff dump failed: {exception.Message}");
            }
        }
    }
}
