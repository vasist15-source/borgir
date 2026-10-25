using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Ava.Systems.Configuration.System;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.HLE;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Path = System.IO.Path;

namespace Ryujinx.Ava.Native
{
    /// <summary>
    /// Settings a game uses instead of the global ones, kept as NativeSettings keys and values in
    /// games/&lt;id&gt;/borgir-settings.json. While that game runs they are applied to ConfigurationState, so the
    /// emulator and the interface see them like any setting, and saving puts the global values back in the file.
    /// Changing one of those settings during the game (from the overlay, say) changes the game's own value.
    /// </summary>
    internal static class NativeGameSettings
    {
        private const string FileName = "borgir-settings.json";

        /// <summary>
        /// The settings a game can have its own value for, and how to copy each between two settings files.
        /// </summary>
        private static readonly Dictionary<string, Action<ConfigurationFileFormat, ConfigurationFileFormat>> _overridable = new()
        {
            ["resScale"] = (from, to) =>
            {
                to.ResScale = from.ResScale;
                to.ResScaleCustom = from.ResScaleCustom;
            },
            ["aspectRatio"] = (from, to) => to.AspectRatio = from.AspectRatio,
            ["maxAnisotropy"] = (from, to) => to.MaxAnisotropy = from.MaxAnisotropy,
            ["scalingFilter"] = (from, to) => to.ScalingFilter = from.ScalingFilter,
            ["scalingFilterLevel"] = (from, to) => to.ScalingFilterLevel = from.ScalingFilterLevel,
            ["vsyncMode"] = (from, to) =>
            {
                to.VSyncMode = from.VSyncMode;
                to.EnableCustomVSyncInterval = from.EnableCustomVSyncInterval;
            },
            ["customRefreshRate"] = (from, to) => to.CustomVSyncInterval = from.CustomVSyncInterval,
            ["asyncShaders"] = (from, to) => to.EnableAsyncShaders = from.EnableAsyncShaders,
            ["dockedMode"] = (from, to) => to.DockedMode = from.DockedMode,
            ["language"] = (from, to) => to.SystemLanguage = from.SystemLanguage,
            ["region"] = (from, to) => to.SystemRegion = from.SystemRegion,
            ["ignoreMissingServices"] = (from, to) => to.IgnoreMissingServices = from.IgnoreMissingServices,
            ["hypervisor"] = (from, to) => to.UseHypervisor = from.UseHypervisor,
            ["memoryManagerMode"] = (from, to) => to.MemoryManagerMode = from.MemoryManagerMode,
            ["dramSize"] = (from, to) => to.DramSize = from.DramSize,
        };

        private static readonly Lock _lock = new();

        // The running game whose own settings are applied (0 when none), those settings, the global values they
        // replaced, and the global settings file as it was, to save the replaced values from.
        private static ulong _titleId;
        private static JsonObject _overrides;
        private static JsonObject _globalValues;
        private static ConfigurationFileFormat _globalFile;

        public static bool IsOverridable(string key) => _overridable.ContainsKey(key);

        /// <summary>
        /// The keys the running game has its own value for.
        /// </summary>
        public static JsonArray ActiveKeys()
        {
            lock (_lock)
            {
                JsonArray keys = [];

                foreach (string key in _overrides?.Select(pair => pair.Key) ?? [])
                {
                    keys.Add((JsonNode)key);
                }

                return keys;
            }
        }

        /// <summary>
        /// The keys a game has its own value for, for the library's detail pane.
        /// </summary>
        public static JsonArray Keys(ulong titleId)
        {
            JsonArray keys = [];

            foreach (string key in Read(titleId).Select(pair => pair.Key))
            {
                keys.Add((JsonNode)key);
            }

            return keys;
        }

        /// <summary>
        /// Applies a game's own settings as it starts.
        /// </summary>
        public static void BeginSession(ulong titleId)
        {
            JsonObject saved = Read(titleId);

            lock (_lock)
            {
                Clear();

                if (saved.Count == 0)
                {
                    return;
                }

                JsonObject global = NativeSettings.Snapshot();

                _globalFile = ConfigurationState.Instance.ToFileFormat();
                _globalValues = new JsonObject();
                _overrides = new JsonObject();

                foreach ((string key, JsonNode value) in saved)
                {
                    try
                    {
                        NativeSettings.Set(key, value);
                    }
                    catch (ArgumentException ex)
                    {
                        Logger.Warning?.Print(LogClass.Application, $"Ignoring the game's setting {key}: {ex.Message}");
                        continue;
                    }

                    _globalValues[key] = global[key]?.DeepClone();
                    _overrides[key] = NativeSettings.Snapshot()[key]?.DeepClone();
                }

                if (_overrides.Count == 0)
                {
                    Clear();
                    return;
                }

                _titleId = titleId;

                Logger.Info?.Print(LogClass.Application, $"Using the game's own settings: {_overrides.ToJsonString()}");
            }

            NativeApp.UpdateGraphicsConfig();
        }

        /// <summary>
        /// Forgets the running game's settings once it has stopped and its last changes were saved. The caller reloads
        /// the global settings from disk afterwards.
        /// </summary>
        public static void EndSession()
        {
            lock (_lock)
            {
                Clear();
            }
        }

        public static bool IsActive
        {
            get
            {
                lock (_lock)
                {
                    return _titleId != 0;
                }
            }
        }

        /// <summary>
        /// Called with the settings about to be saved: puts the global values back in place of the running game's
        /// own, and saves any change made to the game's own values while it ran.
        /// </summary>
        public static void RestoreGlobalValues(ConfigurationFileFormat file)
        {
            lock (_lock)
            {
                if (_titleId == 0)
                {
                    return;
                }

                JsonObject current = NativeSettings.Snapshot();
                bool changed = false;

                foreach ((string key, JsonNode value) in _overrides.ToList())
                {
                    if (!JsonNode.DeepEquals(value, current[key]))
                    {
                        _overrides[key] = current[key]?.DeepClone();
                        changed = true;
                    }

                    _overridable[key](_globalFile, file);
                }

                if (changed)
                {
                    Write(_titleId, _overrides);
                }
            }
        }

        public static JsonObject Get(string id)
        {
            if (!TryParseTitle(id, out ulong titleId, out JsonObject error))
            {
                return error;
            }

            JsonObject global = NativeSettings.Snapshot();
            JsonObject own;

            lock (_lock)
            {
                // A game running with its own settings shows them in the snapshot; report the global values instead.
                foreach ((string key, JsonNode value) in _globalValues ?? new JsonObject())
                {
                    global[key] = value?.DeepClone();
                }

                own = _titleId == titleId ? (JsonObject)_overrides.DeepClone() : Read(titleId);
            }

            JsonObject globalOverridable = new();

            foreach (string key in _overridable.Keys)
            {
                globalOverridable[key] = global[key]?.DeepClone();
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["own"] = own,
                ["global"] = globalOverridable,
            };
        }

        /// <summary>
        /// Gives a game its own value for a setting, or (with a null value) makes it use the global one again. Applies
        /// at once when that game is running.
        /// </summary>
        public static JsonObject Set(string id, string key, JsonNode value)
        {
            if (!TryParseTitle(id, out ulong titleId, out JsonObject error))
            {
                return error;
            }

            if (!_overridable.ContainsKey(key))
            {
                return NativeBridge.Error($"A game can't have its own '{key}' setting.");
            }

            JsonNode normalized = null;

            if (value != null)
            {
                try
                {
                    normalized = Normalize(key, value);
                }
                catch (ArgumentException ex)
                {
                    return NativeBridge.Error(ex.Message);
                }
            }

            // Outside _lock: starting a game takes the host lock first and this one second.
            bool gameRunning = NativeApp.RunningTitleId == titleId;

            lock (_lock)
            {
                JsonObject own = _titleId == titleId ? _overrides : Read(titleId);

                if (_titleId == titleId || (_titleId == 0 && gameRunning))
                {
                    ApplyToRunningGame(titleId, key, normalized);
                    own = _overrides ?? new JsonObject();

                    if (own.Count == 0)
                    {
                        Clear();
                    }
                }
                else if (normalized == null)
                {
                    own.Remove(key);
                }
                else
                {
                    own[key] = normalized;
                }

                Write(titleId, own);
            }

            NativeApp.UpdateGraphicsConfig();
            NativeApp.SaveConfig();

            return Get(id);
        }

        /// <summary>
        /// Changes one of the running game's own settings, or hands it back to the global value. Called under _lock.
        /// </summary>
        private static void ApplyToRunningGame(ulong titleId, string key, JsonNode value)
        {
            if (value == null)
            {
                if (_overrides?.ContainsKey(key) == true)
                {
                    NativeSettings.Set(key, _globalValues[key]);

                    _overrides.Remove(key);
                    _globalValues.Remove(key);
                }

                return;
            }

            if (_titleId == 0)
            {
                _titleId = titleId;
                _overrides = new JsonObject();
                _globalValues = new JsonObject();
                _globalFile = ConfigurationState.Instance.ToFileFormat();
            }

            if (!_overrides.ContainsKey(key))
            {
                // Until now the game used the global value; remember it for saving and for handing back.
                _globalValues[key] = NativeSettings.Snapshot()[key]?.DeepClone();
                _overridable[key](ConfigurationState.Instance.ToFileFormat(), _globalFile);
            }

            NativeSettings.Set(key, value);
            _overrides[key] = NativeSettings.Snapshot()[key]?.DeepClone();
        }

        /// <summary>
        /// Checks a value the way NativeSettings.Set would and returns it as the snapshot shows it.
        /// </summary>
        private static JsonNode Normalize(string key, JsonNode value)
        {
            switch (key)
            {
                case "dockedMode":
                case "ignoreMissingServices":
                case "hypervisor":
                case "asyncShaders":
                    return NativeSettings.Bool(value);
                case "resScale":
                    double scale = Math.Clamp(NativeSettings.Number(value), 0.1, 8);
                    return Math.Abs(scale - Math.Round(scale)) < 0.001 && scale >= 1 ? JsonValue.Create((int)Math.Round(scale)) : JsonValue.Create((float)scale);
                case "maxAnisotropy":
                    return (float)NativeSettings.Number(value);
                case "customRefreshRate":
                    return Math.Clamp((int)NativeSettings.Number(value), 1, 999);
                case "scalingFilterLevel":
                    return Math.Clamp((int)NativeSettings.Number(value), 0, 100);
                case "aspectRatio":
                    return NativeSettings.EnumValue<AspectRatio>(value).ToString();
                case "scalingFilter":
                    return NativeSettings.EnumValue<ScalingFilter>(value).ToString();
                case "vsyncMode":
                    return NativeSettings.EnumValue<VSyncMode>(value).ToString();
                case "language":
                    return NativeSettings.EnumValue<Language>(value).ToString();
                case "region":
                    return NativeSettings.EnumValue<Region>(value).ToString();
                case "memoryManagerMode":
                    return NativeSettings.EnumValue<MemoryManagerMode>(value).ToString();
                case "dramSize":
                    return NativeSettings.EnumValue<MemoryConfiguration>(value).ToString();
                default:
                    throw new ArgumentException($"A game can't have its own '{key}' setting.");
            }
        }

        private static void Clear()
        {
            _titleId = 0;
            _overrides = null;
            _globalValues = null;
            _globalFile = null;
        }

        private static string FilePath(ulong titleId) => Path.Combine(AppDataManager.GamesDirPath, titleId.ToString("x16"), FileName);

        private static JsonObject Read(ulong titleId)
        {
            string path = FilePath(titleId);

            try
            {
                if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject saved)
                {
                    JsonObject known = new();

                    foreach ((string key, JsonNode value) in saved)
                    {
                        if (_overridable.ContainsKey(key) && value != null)
                        {
                            known[key] = value.DeepClone();
                        }
                    }

                    return known;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Logger.Warning?.Print(LogClass.Application, $"Could not read {path}: {ex.Message}");
            }

            return new JsonObject();
        }

        private static void Write(ulong titleId, JsonObject settings)
        {
            string path = FilePath(titleId);

            try
            {
                if (settings.Count == 0)
                {
                    File.Delete(path);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Error?.Print(LogClass.Application, $"Could not save {path}: {ex.Message}");
            }
        }

        private static bool TryParseTitle(string id, out ulong titleId, out JsonObject error)
        {
            error = null;

            if (ulong.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out titleId) && titleId != 0)
            {
                return true;
            }

            error = NativeBridge.Error("Unknown title.");
            return false;
        }
    }
}
