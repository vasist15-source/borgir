using Gommon;
using Ryujinx.Ava.Native;
using Ryujinx.Ava.Systems;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Ava.Systems.Configuration.System;
using Ryujinx.Ava.Utilities;
using Ryujinx.Ava.Utilities.SystemInfo;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.SystemInterop;
using Ryujinx.Common.Utilities;
using Ryujinx.Headless;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ryujinx.Ava
{
    internal static class Program
    {
        public static string Version { get; private set; }
        public static string ConfigurationPath { get; private set; }
        public static bool UseExtraConfig { get; set; }

        [STAThread]
        public static int Main(string[] args)
        {
            Version = ReleaseInformation.Version;

            // Borgir renders with Metal and its interface is AppKit, so it only runs on macOS.
            if (!OperatingSystem.IsMacOS())
            {
                Console.Error.WriteLine("Borgir only runs on macOS.");
                return 1;
            }

            [DllImport("libc")]
            static extern uint geteuid();

            if (geteuid() == 0)
            {
                Logger.Error?.PrintMsg(LogClass.Application, "Borgir is not intended to be run as administrator. Exiting...");
                macOSNativeInterop.SimpleMessageBox($"Borgir {Version}", "Borgir is not intended to be run as administrator.", "OK");
                return 0;
            }

            string downloadFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            if (Environment.CurrentDirectory.StartsWithIgnoreCase(downloadFiles))
            {
                Logger.Error?.PrintMsg(LogClass.Application, "Borgir is not intended to be run from the Downloads folder. Exiting...");
                macOSNativeInterop.SimpleMessageBox($"Borgir {Version}", "Borgir is not intended to be run from the Downloads folder.", "OK");
                return 0;
            }

            string icloudFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Mobile Documents/com~apple~CloudDocs");

            if (Environment.CurrentDirectory.StartsWithIgnoreCase(icloudFiles))
            {
                Logger.Error?.PrintMsg(LogClass.Application, "Borgir is not intended to be run from the iCloud folder. Exiting...");
                macOSNativeInterop.SimpleMessageBox($"Borgir {Version}", "Borgir is not intended to be run from the iCloud folder.", "OK");
                return 0;
            }

            bool noGuiArg = ConsumeCommandLineArgument(ref args, "--no-gui") || ConsumeCommandLineArgument(ref args, "nogui");
            bool coreDumpArg = ConsumeCommandLineArgument(ref args, "--core-dumps");

            // Unclean exits (an unhandled exception, for example) would otherwise leave core dumps behind, unless asked for.
            if (!coreDumpArg)
            {
                OsUtils.SetCoreDumpable(false);
            }

            if (noGuiArg)
            {
                HeadlessRyujinx.Entrypoint(args);
                return 0;
            }

            return NativeApp.Run(args);
        }

        private static bool ConsumeCommandLineArgument(ref string[] args, string targetArgument)
        {
            List<string> argList = [.. args];
            bool found = argList.Remove(targetArgument);
            args = argList.ToArray();
            return found;
        }

        public static string GetDirGameUserConfig(string gameId, bool changeFolderForGame = false)
        {
            if (string.IsNullOrEmpty(gameId))
            {
                return "";
            }

            string gameDir = Path.Combine(AppDataManager.GamesDirPath, gameId, ReleaseInformation.ConfigName);

            if (changeFolderForGame)
            {
                ConfigurationPath = gameDir;
                UseExtraConfig = true;
            }

            return gameDir;
        }

        public static void ReloadConfig(bool isRunGameWithCustomConfig = false)
        {

            string localConfigurationPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ReleaseInformation.ConfigName);
            string appDataConfigurationPath = Path.Combine(AppDataManager.BaseDirPath, ReleaseInformation.ConfigName);

            SeedConfigFromSharedConfig(appDataConfigurationPath);

            if (!isRunGameWithCustomConfig) // To return settings from the game folder if the user configuration exists
            {
                // Now load the configuration as the other subsystems are now registered
                if (File.Exists(localConfigurationPath))
                {
                    ConfigurationPath = localConfigurationPath;
                }
                else if (File.Exists(appDataConfigurationPath))
                {
                    ConfigurationPath = appDataConfigurationPath;
                }
            }

            if (ConfigurationPath == null)
            {
                // No configuration, we load the default values and save it to disk
                ConfigurationPath = appDataConfigurationPath;
                Logger.Notice.Print(LogClass.Application, $"No configuration file found. Saving default configuration to: {ConfigurationPath}");

                ConfigurationState.Instance.LoadDefault();
                ConfigurationState.Instance.ToFileFormat().SaveConfig(ConfigurationPath);
            }
            else
            {
                Logger.Notice.Print(LogClass.Application, $"Loading configuration from: {ConfigurationPath}");

                if (ConfigurationFileFormat.TryLoad(ConfigurationPath, out ConfigurationFileFormat configurationFileFormat))
                {
                    ConfigurationState.Instance.Load(configurationFileFormat, ConfigurationPath);
                }
                else
                {
                    Logger.Warning?.PrintMsg(LogClass.Application, $"Failed to load config! Loading the default config instead.\nFailed config location: {ConfigurationPath}");

                    ConfigurationFileFormat.RenameInvalidConfigFile(ConfigurationPath);

                    ConfigurationState.Instance.LoadDefault();
                }
            }

            // Metal is the only graphics backend, so --graphics-backend is accepted but has nothing to choose from.
            if (CommandLineState.OverrideGraphicsBackend is not null && !CommandLineState.OverrideGraphicsBackend.Equals("metal", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warning?.Print(LogClass.Application, $"Graphics backend '{CommandLineState.OverrideGraphicsBackend}' is not available; using Metal.");
            }

            ConfigurationState.Instance.Graphics.GraphicsBackend.Value = GraphicsBackend.Metal;

            // Check if backend threading was overridden
            if (CommandLineState.OverrideBackendThreading is not null)
                ConfigurationState.Instance.Graphics.BackendThreading.Value = CommandLineState.OverrideBackendThreading.ToLower() switch
                {
                    "auto" => BackendThreading.Auto,
                    "off" => BackendThreading.Off,
                    "on" => BackendThreading.On,
                    _ => ConfigurationState.Instance.Graphics.BackendThreading
                };

            // Check if docked mode was overriden.
            if (CommandLineState.OverrideDockedMode.HasValue)
                ConfigurationState.Instance.System.EnableDockedMode.Value = CommandLineState.OverrideDockedMode.Value;

            // Check if HideCursor was overridden.
            if (CommandLineState.OverrideHideCursor is not null)
                ConfigurationState.Instance.HideCursor.Value = CommandLineState.OverrideHideCursor.ToLower() switch
                {
                    "never" => HideCursorMode.Never,
                    "onidle" => HideCursorMode.OnIdle,
                    "always" => HideCursorMode.Always,
                    _ => ConfigurationState.Instance.HideCursor
                };

            // Check if memoryManagerMode was overridden.
            if (CommandLineState.OverrideMemoryManagerMode is not null)
                if (Enum.TryParse(CommandLineState.OverrideMemoryManagerMode, true, out MemoryManagerMode result))
                {
                    ConfigurationState.Instance.System.MemoryManagerMode.Value = result;
                }

            // Check if PPTC was overridden.
            if (CommandLineState.OverridePPTC is not null)
                if (Enum.TryParse(CommandLineState.OverridePPTC, true, out bool result))
                {
                    ConfigurationState.Instance.System.EnablePtc.Value = result;
                }

            // Check if region was overridden.
            if (CommandLineState.OverrideSystemRegion is not null)
                if (Enum.TryParse(CommandLineState.OverrideSystemRegion, true, out Region result))
                {
                    ConfigurationState.Instance.System.Region.Value = result;
                }

            //Check if language was overridden.
            if (CommandLineState.OverrideSystemLanguage is not null)
                if (Enum.TryParse(CommandLineState.OverrideSystemLanguage, true, out Language result))
                {
                    ConfigurationState.Instance.System.Language.Value = result;
                }
        }

        /// <summary>
        /// On first launch, start from this app's settings from before it was renamed Borgir or, failing that, from
        /// the settings another Ryujinx build left in the shared data folder (game folders, input layouts, graphics
        /// options) instead of the defaults.
        /// </summary>
        private static void SeedConfigFromSharedConfig(string configurationPath)
        {
            if (File.Exists(configurationPath))
            {
                return;
            }

            string previousConfigurationPath = Path.Combine(AppDataManager.BaseDirPath, ReleaseInformation.PreviousConfigName);

            if (File.Exists(previousConfigurationPath) && configurationPath != previousConfigurationPath)
            {
                try
                {
                    File.Copy(previousConfigurationPath, configurationPath);
                    Logger.Notice.Print(LogClass.Application, $"Copied the settings in {previousConfigurationPath} to {configurationPath}.");

                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Application, $"Could not copy {previousConfigurationPath}: {ex.Message}");
                }
            }

            string sharedConfigurationPath = Path.Combine(AppDataManager.BaseDirPath, ReleaseInformation.SharedConfigName);

            if (!File.Exists(sharedConfigurationPath) || configurationPath == sharedConfigurationPath)
            {
                return;
            }

            try
            {
                File.Copy(sharedConfigurationPath, configurationPath);
                Logger.Notice.Print(LogClass.Application, $"Copied the settings in {sharedConfigurationPath} to {configurationPath}.");

                // Astris leaves the core's audio set to Dummy (it plays sound itself); here that would mean silence.
                JsonNode config = JsonNode.Parse(File.ReadAllText(configurationPath));

                if (config?["audio_backend"]?.GetValue<string>() == nameof(AudioBackend.Dummy))
                {
                    config["audio_backend"] = nameof(AudioBackend.AudioToolbox);
                    File.WriteAllText(configurationPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    Logger.Notice.Print(LogClass.Application, "Switched the copied audio output from Dummy to AudioToolbox.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Could not copy {sharedConfigurationPath}: {ex.Message}");
            }
        }

        internal static void PrintSystemInfo()
        {
            // Print the ryubing logo + joke splash
            SplashTextHelper.PrintSplash(); 
            
            Logger.Notice.Print(LogClass.Application, $"Borgir Version: {Version}");
            Logger.Notice.Print(LogClass.Application, $".NET Runtime: {RuntimeInformation.FrameworkDescription}");
            SystemInfo.Gather().Print();

            Logger.Notice.Print(LogClass.Application, $"Logs Enabled: {Logger.GetEnabledLevels()
                    .FormatCollection(
                        x => x.ToString(),
                        separator: ", ",
                        emptyCollectionFallback: "<None>")}");

            Logger.Notice.Print(LogClass.Application,
                AppDataManager.Mode == AppDataManager.LaunchMode.Custom
                    ? $"Launch Mode: Custom Path {AppDataManager.BaseDirPath}"
                    : $"Launch Mode: {AppDataManager.Mode}");
        }

        internal static void ProcessUnhandledException(object sender, Exception initialException, bool isTerminating)
        {
            Logger.Log log = Logger.Error ?? Logger.Notice;

            List<Exception> exceptions = [];

            if (initialException is AggregateException ae)
            {
                exceptions.AddRange(ae.InnerExceptions);
            }
            else
            {
                exceptions.Add(initialException);
            }

            foreach (Exception e in exceptions)
            {
                string message = $"Unhandled exception caught: {e}";
                // ReSharper disable once ConstantConditionalAccessQualifier
                if (sender?.GetType()?.AsPrettyString() is { } senderName)
                    log.Print(LogClass.Application, message, senderName);
                else
                    log.PrintMsg(LogClass.Application, message);
            }


            if (isTerminating)
            {
                Logger.Flush();
                Exit();
            }
        }

        internal static void Exit()
        {
            DiscordIntegrationModule.Exit();

            Logger.Shutdown();
        }
    }
}
