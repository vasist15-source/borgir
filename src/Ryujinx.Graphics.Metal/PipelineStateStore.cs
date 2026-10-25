using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Every render pipeline state a game's draws have built, kept per game next to its shader cache
    /// (pipelines.bin), so that the next session builds them ahead of use (<see cref="PipelinePrewarmer"/>).
    ///
    /// The shader cache records one pipeline state per program, the first it was drawn with, and the pre-warm builds
    /// that one. Breath of the Wild draws about 260 of its programs with other states as well, and those were built
    /// at their first draw every session: a fullscreen one is built in place, which was 35-130 ms of a freeze. The
    /// OS keeps the compiled GPU code between launches, but for these it often missed anyway.
    ///
    /// Programs are matched by a hash of their MSL, which the shader cache keeps unchanged between sessions.
    /// RYUJINX_METAL_PIPELINE_STORE=0 turns it off.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class PipelineStateStore
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("RYUJINX_METAL_PIPELINE_STORE") != "0";

        private const string FileName = "pipelines.bin";
        private const uint Magic = 0x43535042; // "BPSC"
        private const int Version = 1;

        // Bounds on what one game can grow to: a program drawn with many vertex layouts would otherwise add a state
        // per layout forever.
        private const int MaxStatesPerProgram = 64;
        private const int MaxStates = 50000;

        private static readonly long _saveIntervalTicks = Stopwatch.Frequency * 60;

        private static readonly Lock _lock = new();
        private static string _directory;
        private static Dictionary<ulong, List<PipelineUid>> _states = [];
        private static int _count;
        private static int _loaded;
        private static int _recorded;
        private static bool _dirty;
        private static long _lastSave;
        private static int _saving;

        public static long BuiltAhead;

        /// <summary>
        /// The states saved for a program, for the pre-warm to build; null when there are none. Opens the running
        /// game's store first, if this is the first lookup since it started.
        /// </summary>
        public static PipelineUid[] Lookup(ulong programHash)
        {
            if (!Enabled)
            {
                return null;
            }

            lock (_lock)
            {
                OpenLocked();

                return _states.TryGetValue(programHash, out List<PipelineUid> states) ? [.. states] : null;
            }
        }

        /// <summary>Render thread: a draw is building this state for this program.</summary>
        public static void Record(Program program, ref PipelineUid key)
        {
            if (!Enabled)
            {
                return;
            }

            ulong programHash = program.SourceHash;

            lock (_lock)
            {
                OpenLocked();

                if (_directory == null || _count >= MaxStates)
                {
                    return;
                }

                if (!_states.TryGetValue(programHash, out List<PipelineUid> states))
                {
                    _states[programHash] = states = [];
                }

                if (states.Count >= MaxStatesPerProgram)
                {
                    return;
                }

                foreach (PipelineUid state in states)
                {
                    PipelineUid known = state;

                    if (known.Equals(ref key))
                    {
                        return;
                    }
                }

                states.Add(key);
                _count++;
                _recorded++;
                _dirty = true;
            }
        }

        /// <summary>Render thread, once per presented frame: saves new states in the background now and then.</summary>
        public static void NotePresent()
        {
            if (!Enabled || !_dirty || Stopwatch.GetTimestamp() - _lastSave < _saveIntervalTicks)
            {
                return;
            }

            _lastSave = Stopwatch.GetTimestamp();

            if (Interlocked.Exchange(ref _saving, 1) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    try
                    {
                        Save();
                    }
                    finally
                    {
                        Volatile.Write(ref _saving, 0);
                    }
                }, null);
            }
        }

        /// <summary>The game is stopping: saves what it recorded and closes the store.</summary>
        public static void Close()
        {
            if (!Enabled)
            {
                return;
            }

            Save();

            lock (_lock)
            {
                if (_directory != null)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"Pipeline states: {_loaded} loaded, {Interlocked.Read(ref BuiltAhead)} built ahead of use, {_recorded} new this session saved to {FileName}");
                }

                _directory = null;
                _states = [];
                _count = _loaded = _recorded = 0;
                _dirty = false;
                Interlocked.Exchange(ref BuiltAhead, 0);
            }
        }

        private static void OpenLocked()
        {
            string directory = ShaderCacheLocation.Directory;

            if (directory == _directory)
            {
                return;
            }

            _directory = directory;
            _states = [];
            _count = _loaded = _recorded = 0;
            _dirty = false;
            _lastSave = Stopwatch.GetTimestamp();

            if (directory == null)
            {
                return;
            }

            string path = Path.Combine(directory, FileName);

            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                using BinaryReader reader = new(File.OpenRead(path));

                if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version || reader.ReadInt32() != Unsafe.SizeOf<PipelineUid>())
                {
                    Logger.Info?.Print(LogClass.Gpu, $"Pipeline states: {FileName} is from another build, starting over");

                    return;
                }

                int count = Math.Min(reader.ReadInt32(), MaxStates);
                Span<byte> buffer = stackalloc byte[Unsafe.SizeOf<PipelineUid>()];

                for (int i = 0; i < count; i++)
                {
                    ulong programHash = reader.ReadUInt64();
                    reader.BaseStream.ReadExactly(buffer);

                    if (!_states.TryGetValue(programHash, out List<PipelineUid> states))
                    {
                        _states[programHash] = states = [];
                    }

                    states.Add(MemoryMarshal.Read<PipelineUid>(buffer));
                    _count++;
                }

                _loaded = _count;
            }
            catch (Exception exception) when (exception is IOException or EndOfStreamException or UnauthorizedAccessException)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Pipeline states: could not read {FileName}: {exception.Message}");
                _states = [];
                _count = _loaded = 0;
            }
        }

        private static void Save()
        {
            string directory;
            List<(ulong, PipelineUid)> entries = [];

            lock (_lock)
            {
                if (!_dirty || _directory == null)
                {
                    return;
                }

                directory = _directory;

                foreach ((ulong programHash, List<PipelineUid> states) in _states)
                {
                    foreach (PipelineUid state in states)
                    {
                        entries.Add((programHash, state));
                    }
                }

                _dirty = false;
            }

            string path = Path.Combine(directory, FileName);
            string temporary = path + ".tmp";

            try
            {
                Directory.CreateDirectory(directory);

                using (BinaryWriter writer = new(File.Create(temporary)))
                {
                    writer.Write(Magic);
                    writer.Write(Version);
                    writer.Write(Unsafe.SizeOf<PipelineUid>());
                    writer.Write(entries.Count);

                    foreach ((ulong programHash, PipelineUid state) in entries)
                    {
                        PipelineUid copy = state;

                        writer.Write(programHash);
                        writer.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<PipelineUid>(ref copy)));
                    }
                }

                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Pipeline states: could not save {FileName}: {exception.Message}");

                lock (_lock)
                {
                    _dirty = true;
                }
            }
        }

        /// <summary>A program's identity between sessions: a hash of its stages and their MSL.</summary>
        public static ulong HashSources(ShaderSource[] shaders)
        {
            ulong hash = 0xcbf29ce484222325;

            foreach (ShaderSource shader in shaders)
            {
                Hash128 code = Hash128.ComputeHash(MemoryMarshal.AsBytes((shader.Code ?? string.Empty).AsSpan()));

                hash = (hash ^ (ulong)shader.Stage) * 0x100000001b3;
                hash = (hash ^ code.Low) * 0x100000001b3;
                hash = (hash ^ code.High) * 0x100000001b3;
            }

            return hash;
        }
    }
}
