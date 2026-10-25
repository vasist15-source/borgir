using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Ava.Systems.Configuration.System;
using Ryujinx.Common.Configuration;
using Ryujinx.HLE;
using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ryujinx.Ava.Native
{
    /// <summary>
    /// The settings the native UI shows, under short flat keys, read from and written to ConfigurationState.
    /// Settings that a running game listens to (docked mode, volume, aspect ratio, filters, V-Sync) take effect
    /// at once; the rest apply the next time a game starts.
    /// </summary>
    internal static class NativeSettings
    {
        private static ConfigurationState Config => ConfigurationState.Instance;

        public static JsonObject Snapshot()
        {
            return new JsonObject
            {
                // Graphics
                ["resScale"] = Config.Graphics.ResScale.Value == -1 ? Config.Graphics.ResScaleCustom.Value : Config.Graphics.ResScale.Value,
                ["maxAnisotropy"] = Config.Graphics.MaxAnisotropy.Value,
                ["aspectRatio"] = Config.Graphics.AspectRatio.Value.ToString(),
                ["scalingFilter"] = Config.Graphics.ScalingFilter.Value.ToString(),
                ["scalingFilterLevel"] = Config.Graphics.ScalingFilterLevel.Value,
                ["antiAliasing"] = Config.Graphics.AntiAliasing.Value.ToString(),
                ["vsyncMode"] = Config.Graphics.VSyncMode.Value.ToString(),
                ["customRefreshRate"] = Config.Graphics.CustomVSyncInterval.Value,
                ["shaderCache"] = Config.Graphics.EnableShaderCache.Value,
                ["macroHLE"] = Config.Graphics.EnableMacroHLE.Value,
                ["colorSpacePassthrough"] = Config.Graphics.EnableColorSpacePassthrough.Value,
                ["backendThreading"] = Config.Graphics.BackendThreading.Value.ToString(),

                // System
                ["dockedMode"] = Config.System.EnableDockedMode.Value,
                ["language"] = Config.System.Language.Value.ToString(),
                ["region"] = Config.System.Region.Value.ToString(),
                ["audioBackend"] = Config.System.AudioBackend.Value.ToString(),
                ["volume"] = Config.System.AudioVolume.Value,
                ["internetAccess"] = Config.System.EnableInternetAccess.Value,
                ["ignoreMissingServices"] = Config.System.IgnoreMissingServices.Value,
                ["fsIntegrityChecks"] = Config.System.EnableFsIntegrityChecks.Value,

                // CPU and memory
                ["hypervisor"] = Config.System.UseHypervisor.Value,
                ["ptc"] = Config.System.EnablePtc.Value,
                ["lowPowerPtc"] = Config.System.EnableLowPowerPtc.Value,
                ["memoryManagerMode"] = Config.System.MemoryManagerMode.Value.ToString(),
                ["dramSize"] = Config.System.DramSize.Value.ToString(),
                ["gcLowLatency"] = Config.System.GCLowLatency.Value,

                // Interface and input
                ["startFullscreen"] = Config.UI.StartFullscreen.Value,
                ["confirmExit"] = Config.ShowConfirmExit.Value,
                ["hideCursor"] = Config.HideCursor.Value.ToString(),
                ["enableKeyboard"] = Config.Hid.EnableKeyboard.Value,
                ["enableMouse"] = Config.Hid.EnableMouse.Value,
            };
        }

        public static JsonObject Options()
        {
            return new JsonObject
            {
                ["aspectRatio"] = Names<AspectRatio>(),
                ["scalingFilter"] = new JsonArray("Bilinear", "Nearest", "Fsr"),
                ["vsyncMode"] = Names<VSyncMode>(),
                ["backendThreading"] = Names<BackendThreading>(),
                ["language"] = Names<Language>(),
                ["region"] = Names<Region>(),
                ["audioBackend"] = new JsonArray("AudioToolbox", "SDL3", "SoundIo", "OpenAl", "Dummy"),
                ["memoryManagerMode"] = Names<MemoryManagerMode>(),
                ["dramSize"] = Names<MemoryConfiguration>(),
                ["hideCursor"] = Names<HideCursorMode>(),
            };
        }

        /// <summary>
        /// Changes one setting. Throws ArgumentException for unknown keys or values of the wrong kind.
        /// </summary>
        public static void Set(string key, JsonNode value)
        {
            switch (key)
            {
                case "resScale":
                    float scale = (float)Number(value);

                    if (scale is >= 1 and <= 4 && MathF.Abs(scale - MathF.Round(scale)) < 0.001f)
                    {
                        Config.Graphics.ResScale.Value = (int)MathF.Round(scale);
                    }
                    else
                    {
                        Config.Graphics.ResScale.Value = -1;
                        Config.Graphics.ResScaleCustom.Value = Math.Clamp(scale, 0.1f, 8f);
                    }

                    break;
                case "maxAnisotropy":
                    Config.Graphics.MaxAnisotropy.Value = (float)Number(value);
                    break;
                case "aspectRatio":
                    Config.Graphics.AspectRatio.Value = EnumValue<AspectRatio>(value);
                    break;
                case "scalingFilter":
                    Config.Graphics.ScalingFilter.Value = EnumValue<ScalingFilter>(value);
                    break;
                case "scalingFilterLevel":
                    Config.Graphics.ScalingFilterLevel.Value = Math.Clamp((int)Number(value), 0, 100);
                    break;
                case "antiAliasing":
                    Config.Graphics.AntiAliasing.Value = EnumValue<AntiAliasing>(value);
                    break;
                case "vsyncMode":
                    VSyncMode mode = EnumValue<VSyncMode>(value);

                    if (mode == VSyncMode.Custom)
                    {
                        Config.Graphics.EnableCustomVSyncInterval.Value = true;
                    }

                    Config.Graphics.VSyncMode.Value = mode;
                    break;
                case "customRefreshRate":
                    Config.Graphics.CustomVSyncInterval.Value = Math.Clamp((int)Number(value), 1, 999);
                    break;
                case "shaderCache":
                    Config.Graphics.EnableShaderCache.Value = Bool(value);
                    break;
                case "macroHLE":
                    Config.Graphics.EnableMacroHLE.Value = Bool(value);
                    break;
                case "colorSpacePassthrough":
                    Config.Graphics.EnableColorSpacePassthrough.Value = Bool(value);
                    break;
                case "backendThreading":
                    Config.Graphics.BackendThreading.Value = EnumValue<BackendThreading>(value);
                    break;
                case "dockedMode":
                    Config.System.EnableDockedMode.Value = Bool(value);
                    break;
                case "language":
                    Config.System.Language.Value = EnumValue<Language>(value);
                    break;
                case "region":
                    Config.System.Region.Value = EnumValue<Region>(value);
                    break;
                case "audioBackend":
                    Config.System.AudioBackend.Value = EnumValue<AudioBackend>(value);
                    break;
                case "volume":
                    Config.System.AudioVolume.Value = Math.Clamp((float)Number(value), 0, 1);
                    break;
                case "internetAccess":
                    Config.System.EnableInternetAccess.Value = Bool(value);
                    break;
                case "ignoreMissingServices":
                    Config.System.IgnoreMissingServices.Value = Bool(value);
                    break;
                case "fsIntegrityChecks":
                    Config.System.EnableFsIntegrityChecks.Value = Bool(value);
                    break;
                case "hypervisor":
                    Config.System.UseHypervisor.Value = Bool(value);
                    break;
                case "ptc":
                    Config.System.EnablePtc.Value = Bool(value);
                    break;
                case "lowPowerPtc":
                    Config.System.EnableLowPowerPtc.Value = Bool(value);
                    break;
                case "memoryManagerMode":
                    Config.System.MemoryManagerMode.Value = EnumValue<MemoryManagerMode>(value);
                    break;
                case "dramSize":
                    Config.System.DramSize.Value = EnumValue<MemoryConfiguration>(value);
                    break;
                case "gcLowLatency":
                    Config.System.GCLowLatency.Value = Bool(value);
                    break;
                case "startFullscreen":
                    Config.UI.StartFullscreen.Value = Bool(value);
                    break;
                case "confirmExit":
                    Config.ShowConfirmExit.Value = Bool(value);
                    break;
                case "hideCursor":
                    Config.HideCursor.Value = EnumValue<HideCursorMode>(value);
                    break;
                case "enableKeyboard":
                    Config.Hid.EnableKeyboard.Value = Bool(value);
                    break;
                case "enableMouse":
                    Config.Hid.EnableMouse.Value = Bool(value);
                    break;
                default:
                    throw new ArgumentException($"Unknown setting '{key}'.");
            }
        }

        private static JsonArray Names<T>() where T : struct, Enum
        {
            JsonArray names = [];

            foreach (string name in Enum.GetNames<T>())
            {
                names.Add((JsonNode)name);
            }

            return names;
        }

        internal static bool Bool(JsonNode value) => value?.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetValue<double>() != 0,
            _ => throw new ArgumentException("Expected true or false."),
        };

        internal static double Number(JsonNode value) => value?.GetValueKind() == JsonValueKind.Number
            ? value.GetValue<double>()
            : throw new ArgumentException("Expected a number.");

        internal static T EnumValue<T>(JsonNode value) where T : struct, Enum
        {
            string name = value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

            if (name != null && System.Enum.TryParse(name, true, out T result) && System.Enum.GetNames<T>().Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return result;
            }

            throw new ArgumentException($"'{name}' is not one of {string.Join(", ", System.Enum.GetNames<T>())}.");
        }
    }
}
