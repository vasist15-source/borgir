using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.Gpu.Shader.DiskCache;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Nvn
{
    /// <summary>
    /// The replay's translated programs on disk (games/&lt;title&gt;/cache/nvnpv/programs.bin), apart from
    /// the Maxwell path's shader cache. An entry is a translation and what it depended on: the code
    /// hashes, the draw state key and the translator's answers about textures. Loaded programs are
    /// matched exactly as fresh ones; the code's c1 data comes from the guest at bind time (the hash
    /// covers it). Entries are appended by a background thread; a version change drops the file.
    /// </summary>
    sealed class NvnProgramCache : IDisposable
    {
        private const uint FileMagic = 0x4350564E;   // "NVPC"
        private const uint EntryMagic = 0x4550564E;  // "NVPE"
        private const uint FormatVersion = 1;

        private readonly GpuContext _context;
        private readonly string _path;
        private readonly BlockingCollection<byte[]> _pending = new();
        private Thread _writer;

        public bool Enabled => _path != null;

        public NvnProgramCache(GpuContext context)
        {
            _context = context;

            if (GraphicsConfig.EnableShaderCache && GraphicsConfig.TitleId != null && Environment.GetEnvironmentVariable("RYUJINX_NVN_PV_PROGRAM_CACHE") != "0")
            {
                _path = Path.Combine(AppDataManager.GamesDirPath, GraphicsConfig.TitleId.ToLowerInvariant(), "cache", "nvnpv", "programs.bin");
            }
        }

        private static uint Version => DiskCacheHostStorage.CodeGenVersion ^ (FormatVersion << 24);

        // ---------- Loading ----------

        /// <summary>Reads every entry and creates its host program (compiled in the background by the backend).</summary>
        public int Load(Dictionary<(Hash128, Hash128, Hash128), List<NvnProgram>> programs)
        {
            if (_path == null || !File.Exists(_path))
            {
                return 0;
            }

            int loaded = 0;

            try
            {
                using FileStream file = File.OpenRead(_path);
                using BinaryReader reader = new(file);

                if (file.Length < 8 || reader.ReadUInt32() != FileMagic || reader.ReadUInt32() != Version)
                {
                    Logger.Info?.Print(LogClass.Gpu, "nvnpv: program cache is from another version; starting a new one");
                    file.Dispose();
                    File.Delete(_path);
                    return 0;
                }

                while (file.Position < file.Length)
                {
                    long start = file.Position;

                    if (!TryReadEntry(reader, out var key, out NvnProgram program))
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"nvnpv: program cache entry at {start} unreadable; the rest is ignored");
                        break;
                    }

                    if (!programs.TryGetValue(key, out List<NvnProgram> list))
                    {
                        list = new List<NvnProgram>();
                        programs[key] = list;
                    }

                    list.Add(program);
                    loaded++;
                }
            }
            catch (Exception e)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"nvnpv: program cache not loaded: {e.Message}");
            }

            return loaded;
        }

        private bool TryReadEntry(BinaryReader r, out (Hash128, Hash128, Hash128) key, out NvnProgram program)
        {
            key = default;
            program = null;

            if (r.ReadUInt32() != EntryMagic)
            {
                return false;
            }

            int length = r.ReadInt32();
            byte[] body = r.ReadBytes(length);

            if (body.Length != length)
            {
                return false;
            }

            using BinaryReader b = new(new MemoryStream(body));

            key = (ReadHash(b), ReadHash(b), ReadHash(b));
            program = new NvnProgram { GraphicsKey = ReadHash(b), Compute = b.ReadBoolean() };

            int lx = b.ReadInt32(), ly = b.ReadInt32(), lz = b.ReadInt32();
            List<ShaderSource> sources = new();
            ShaderInfoBuilder graphics = program.Compute ? null : new ShaderInfoBuilder(_context, false, false);
            ShaderProgramInfo computeInfo = null;

            // Graphics stages are stored in translation order (vertex .. fragment), each with its NVN stage.
            int stages = b.ReadInt32();

            for (int i = 0; i < stages; i++)
            {
                int nvn = b.ReadInt32();
                NvnAnswers answers = new() { UsedGraphicsState = b.ReadBoolean() };

                for (int n = b.ReadInt32(); n > 0; n--)
                {
                    answers.Samplers[(b.ReadInt32(), b.ReadInt32())] = (SamplerType)b.ReadInt32();
                }

                for (int n = b.ReadInt32(); n > 0; n--)
                {
                    answers.Formats[(b.ReadInt32(), b.ReadInt32())] = (TextureFormat)b.ReadInt32();
                }

                ShaderProgramInfo info = ReadInfo(b);
                string code = b.ReadBoolean() ? b.ReadString() : null;
                int binaryLength = b.ReadInt32();
                byte[] binary = binaryLength > 0 ? b.ReadBytes(binaryLength) : null;
                ShaderStage stage = (ShaderStage)b.ReadInt32();
                TargetLanguage language = (TargetLanguage)b.ReadInt32();

                sources.Add(new ShaderSource(code, binary, stage, language));
                program.Info[nvn] = info;
                program.Answers[nvn] = answers;
                program.NoteSegments(nvn, info);

                if (program.Compute)
                {
                    computeInfo = info;
                }
                else
                {
                    graphics.AddStageInfo(info);
                }
            }

            ShaderInfo shaderInfo = program.Compute
                ? ShaderInfoBuilder.BuildForCompute(_context, computeInfo, new ComputeSize(lx, ly, lz))
                : graphics.Build(null);

            program.Host = _context.Renderer.CreateProgram(sources.ToArray(), shaderInfo);
            program.LocalSize = program.Compute ? new[] { lx, ly, lz } : null;
            program.FromCache = true;

            return true;
        }

        // ---------- Saving ----------

        /// <summary>Queues a fresh translation for the file.</summary>
        public void Append((Hash128, Hash128, Hash128) key, NvnProgram program, ShaderSource[] sources, int[] nvnOrder, int lx, int ly, int lz)
        {
            if (_path == null || program.Failed || program.Host == null)
            {
                return;
            }

            using MemoryStream body = new();
            using (BinaryWriter b = new(body, Encoding.UTF8, true))
            {
                WriteHash(b, key.Item1);
                WriteHash(b, key.Item2);
                WriteHash(b, key.Item3);
                WriteHash(b, program.GraphicsKey);
                b.Write(program.Compute);
                b.Write(lx);
                b.Write(ly);
                b.Write(lz);
                b.Write(sources.Length);

                for (int i = 0; i < sources.Length; i++)
                {
                    int nvn = nvnOrder[i];
                    NvnAnswers answers = program.Answers[nvn] ?? new NvnAnswers();

                    b.Write(nvn);
                    b.Write(answers.UsedGraphicsState);
                    b.Write(answers.Samplers.Count);

                    foreach (KeyValuePair<(int Handle, int Slot), SamplerType> kv in answers.Samplers)
                    {
                        b.Write(kv.Key.Handle);
                        b.Write(kv.Key.Slot);
                        b.Write((int)kv.Value);
                    }

                    b.Write(answers.Formats.Count);

                    foreach (KeyValuePair<(int Handle, int Slot), TextureFormat> kv in answers.Formats)
                    {
                        b.Write(kv.Key.Handle);
                        b.Write(kv.Key.Slot);
                        b.Write((int)kv.Value);
                    }

                    WriteInfo(b, program.Info[nvn]);

                    ShaderSource source = sources[i];
                    b.Write(source.Code != null);

                    if (source.Code != null)
                    {
                        b.Write(source.Code);
                    }

                    b.Write(source.BinaryCode?.Length ?? 0);

                    if (source.BinaryCode != null)
                    {
                        b.Write(source.BinaryCode);
                    }

                    b.Write((int)source.Stage);
                    b.Write((int)source.Language);
                }
            }

            byte[] bytes = body.ToArray();
            byte[] entry = new byte[8 + bytes.Length];
            BitConverter.TryWriteBytes(entry.AsSpan(0), EntryMagic);
            BitConverter.TryWriteBytes(entry.AsSpan(4), bytes.Length);
            bytes.CopyTo(entry, 8);

            _pending.Add(entry);

            if (_writer == null)
            {
                _writer = new Thread(WriteLoop) { Name = "NvnReplay.ProgramCache", IsBackground = true };
                _writer.Start();
            }
        }

        private void WriteLoop()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));

                bool fresh = !File.Exists(_path);
                using FileStream file = new(_path, FileMode.Append, FileAccess.Write, FileShare.Read);

                if (fresh || file.Length == 0)
                {
                    file.Write(BitConverter.GetBytes(FileMagic));
                    file.Write(BitConverter.GetBytes(Version));
                }

                foreach (byte[] entry in _pending.GetConsumingEnumerable())
                {
                    file.Write(entry);

                    if (_pending.Count == 0)
                    {
                        file.Flush();
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"nvnpv: program cache not written: {e.Message}");
            }
        }

        // ---------- Pieces ----------

        private static Hash128 ReadHash(BinaryReader b) => new(b.ReadUInt64(), b.ReadUInt64());

        private static void WriteHash(BinaryWriter b, Hash128 h)
        {
            b.Write(h.Low);
            b.Write(h.High);
        }

        private static void WriteArray<T>(BinaryWriter b, IReadOnlyList<T> items) where T : unmanaged
        {
            T[] array = new T[items.Count];

            for (int i = 0; i < array.Length; i++)
            {
                array[i] = items[i];
            }

            b.Write(array.Length);
            b.Write(MemoryMarshal.AsBytes(array.AsSpan()));
        }

        private static T[] ReadArray<T>(BinaryReader b) where T : unmanaged
        {
            int count = b.ReadInt32();
            T[] array = new T[count];
            byte[] bytes = b.ReadBytes(count * Marshal.SizeOf<T>());
            bytes.AsSpan().CopyTo(MemoryMarshal.AsBytes(array.AsSpan()));

            return array;
        }

        private static void WriteInfo(BinaryWriter b, ShaderProgramInfo info)
        {
            WriteArray(b, info.CBuffers);
            WriteArray(b, info.SBuffers);
            WriteArray(b, info.Textures);
            WriteArray(b, info.Images);
            b.Write((int)info.Stage);
            b.Write(info.GeometryVerticesPerPrimitive);
            b.Write(info.GeometryMaxOutputVertices);
            b.Write(info.ThreadsPerInputPrimitive);
            b.Write(info.UsesFragCoord);
            b.Write(info.UsesInstanceId);
            b.Write(info.UsesDrawParameters);
            b.Write(info.UsesRtLayer);
            b.Write(info.ClipDistancesWritten);
            b.Write(info.FragmentOutputMap);
        }

        private static ShaderProgramInfo ReadInfo(BinaryReader b)
        {
            BufferDescriptor[] cbuffers = ReadArray<BufferDescriptor>(b);
            BufferDescriptor[] sbuffers = ReadArray<BufferDescriptor>(b);
            TextureDescriptor[] textures = ReadArray<TextureDescriptor>(b);
            TextureDescriptor[] images = ReadArray<TextureDescriptor>(b);

            return new ShaderProgramInfo(
                cbuffers,
                sbuffers,
                textures,
                images,
                (ShaderStage)b.ReadInt32(),
                b.ReadInt32(),
                b.ReadInt32(),
                b.ReadInt32(),
                b.ReadBoolean(),
                b.ReadBoolean(),
                b.ReadBoolean(),
                b.ReadBoolean(),
                b.ReadByte(),
                b.ReadInt32());
        }

        public void Dispose()
        {
            _pending.CompleteAdding();
            _writer?.Join(2000);
        }
    }
}
