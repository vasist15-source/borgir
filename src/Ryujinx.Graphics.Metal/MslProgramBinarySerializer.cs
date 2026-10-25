using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System.IO;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    static class MslProgramBinarySerializer
    {
        private const uint Magic = 0x4D534C52;
        private const int Version = 1;
        private const int MaxShaderCount = 8;

        public static byte[] Pack(ShaderSource[] shaders)
        {
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(shaders.Length);

            foreach (ShaderSource shader in shaders)
            {
                byte[] code = Encoding.UTF8.GetBytes(shader.Code ?? string.Empty);

                writer.Write((int)shader.Stage);
                writer.Write((int)shader.Language);
                writer.Write(code.Length);
                writer.Write(code);
            }

            writer.Flush();
            return stream.ToArray();
        }

        public static ShaderSource[] Unpack(byte[] binary)
        {
            using MemoryStream stream = new(binary, writable: false);
            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
            {
                throw new InvalidDataException("Invalid Metal shader cache header.");
            }

            int count = reader.ReadInt32();
            if (count <= 0 || count > MaxShaderCount)
            {
                throw new InvalidDataException("Invalid Metal shader count.");
            }

            ShaderSource[] shaders = new ShaderSource[count];

            for (int i = 0; i < shaders.Length; i++)
            {
                ShaderStage stage = (ShaderStage)reader.ReadInt32();
                TargetLanguage language = (TargetLanguage)reader.ReadInt32();
                int length = reader.ReadInt32();

                if (length < 0 || length > stream.Length - stream.Position)
                {
                    throw new InvalidDataException("Invalid Metal shader source length.");
                }

                byte[] code = reader.ReadBytes(length);
                shaders[i] = new ShaderSource(Encoding.UTF8.GetString(code), stage, language);
            }

            return shaders;
        }
    }
}
