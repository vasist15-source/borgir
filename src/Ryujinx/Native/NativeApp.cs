using LibHac;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Shim;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Ava.Utilities;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Logging;
using Ryujinx.Common.SystemInterop;
using Ryujinx.Common.UI;
using Ryujinx.Graphics.Gpu;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.Input;
using Ryujinx.Input.HLE;
using Ryujinx.Input.SDL3;
using Ryujinx.SDL3.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Path = System.IO.Path;
using TimeSpan = System.TimeSpan;
using UserId = Ryujinx.HLE.HOS.Services.Account.Acc.UserId;

namespace Ryujinx.Ava.Native
{
    /// <summary>
    /// Runs Borgir with its native macOS interface (macos/ProjectXUI): sets up the emulator services, hands
    /// the main thread to the UI and answers its commands.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal static class NativeApp
    {
        private static readonly Lock _hostLock = new();
        private static readonly Lock _libraryLock = new();
        private static readonly Lock _saveLock = new();

        private static NativeHost _host;
        private static int _quitting;
        private static Thread _libraryThread;
        private static string _cacheDirectory;
        private static string _launchPath;
        private static Timer _saveTimer;
        private static PosixSignalRegistration _terminateSignal;

        /// <summary>Whether a game is running or starting.</summary>
        internal static bool IsGameRunning
        {
            get
            {
                lock (_hostLock)
                {
                    return _host != null;
                }
            }
        }

        /// <summary>The title id of the game running or starting, or 0.</summary>
        internal static ulong RunningTitleId
        {
            get
            {
                lock (_hostLock)
                {
                    return _host?.ApplicationId ?? 0;
                }
            }
        }

        /// <summary>Where decoded images (game icons, profile pictures, avatars) are kept for the interface.</summary>
        internal static string CacheDirectory => _cacheDirectory;

        public static VirtualFileSystem VirtualFileSystem { get; private set; }
        public static LibHacHorizonManager LibHacHorizonManager { get; private set; }
        public static ContentManager ContentManager { get; private set; }
        public static AccountManager AccountManager { get; private set; }
        public static UserChannelPersistence UserChannelPersistence { get; private set; }
        public static ApplicationLibrary ApplicationLibrary { get; private set; }
        public static InputManager InputManager { get; private set; }
        public static NativeKeyboardDriver Keyboard { get; private set; }
        public static NativeMouseDriver Mouse { get; private set; }

        public static unsafe int Run(string[] args)
        {
            if (!NativeUI.TryLoad(out string error))
            {
                macOSNativeInterop.SimpleMessageBox($"Borgir {Program.Version}", $"The native interface could not start. {error}", "OK");
                return 1;
            }

            Initialize(args);

            Logger.Info?.Print(LogClass.Application, "Starting the native interface.");

            return NativeUI.Run(NativeBridge.Callbacks);
        }

        private static void Initialize(string[] args)
        {
            CommandLineState.ParseArguments(args);

            AppDomain.CurrentDomain.UnhandledException += (sender, e)
                => Program.ProcessUnhandledException(sender, e.ExceptionObject as Exception, e.IsTerminating);
            TaskScheduler.UnobservedTaskException += (sender, e)
                => Program.ProcessUnhandledException(sender, e.Exception, false);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Program.Exit();

            AppDataManager.Initialize(CommandLineState.BaseDirPathArg);
            ConfigurationState.Initialize();
            LoggerModule.Initialize();

            // SDL must pump its events on the main thread, which the native UI's run loop owns. It only handles
            // controllers here; its video subsystem would take over NSApplication and break quitting.
            SDL3Driver.MainThreadDispatcher = MainThread.Post;
            SDL3Driver.UseVideo = false;

            Program.ReloadConfig();
            Program.PrintSystemInfo();

            // A game given on the command line (open "Borgir.app" --args game.nsp) starts once the UI is up.
            _launchPath = CommandLineState.LaunchPathArg;

            // Quit cleanly on SIGTERM too. Through AppKit when the UI answers, so macOS sees an ordinary quit (a process
            // that just exits can leave its Dock icon behind); directly if it doesn't within a few seconds.
            _terminateSignal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;

                Logger.Info?.Print(LogClass.Application, "SIGTERM received, quitting.");
                NativeBridge.Post("app.terminate", new JsonObject());

                Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));

                    try
                    {
                        Logger.Warning?.Print(LogClass.Application, "The interface did not quit after SIGTERM; exiting directly.");
                        Quit();
                    }
                    finally
                    {
                        Environment.Exit(0);
                    }
                });
            });

            _cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "Borgir");

            UserChannelPersistence = new UserChannelPersistence();
            VirtualFileSystem = VirtualFileSystem.CreateInstance();
            LibHacHorizonManager = new LibHacHorizonManager();
            ContentManager = new ContentManager(VirtualFileSystem);

            LibHacHorizonManager.InitializeFsServer(VirtualFileSystem);
            LibHacHorizonManager.InitializeArpServer();
            LibHacHorizonManager.InitializeBcatServer();
            LibHacHorizonManager.InitializeSystemClients();

            ApplicationLibrary = new ApplicationLibrary(VirtualFileSystem, ConfigurationState.Instance.System.IntegrityCheckLevel);
            ApplicationLibrary.ApplicationCountUpdated += (_, e) => NativeBridge.Post("library.progress", new JsonObject
            {
                ["loaded"] = e.NumAppsLoaded,
                ["found"] = e.NumAppsFound,
            });

            VirtualFileSystem.FixExtraData(LibHacHorizonManager.RyujinxClient);

            AccountManager = new AccountManager(LibHacHorizonManager.RyujinxClient, CommandLineState.Profile);

            VirtualFileSystem.ReloadKeySet();

            Keyboard = new NativeKeyboardDriver();
            Mouse = new NativeMouseDriver();
            InputManager = new InputManager(Keyboard, new SDL3GamepadDriver());
            InputManager.SetMouseDriver(Mouse);
        }

        public static void SaveConfig()
        {
            lock (_saveLock)
            {
                _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                ConfigurationFileFormat file = ConfigurationState.Instance.ToFileFormat();

                // A running game's own settings stay out of the global file.
                NativeGameSettings.RestoreGlobalValues(file);

                file.SaveConfig(Program.ConfigurationPath);
            }
        }

        /// <summary>
        /// Saves settings once changes stop for a moment, so dragging a slider doesn't rewrite the file on every step.
        /// </summary>
        private static void SaveConfigSoon()
        {
            _saveTimer ??= new Timer(_ =>
            {
                try
                {
                    SaveConfig();
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Application, $"Could not save settings: {ex.Message}");
                }
            });

            _saveTimer.Change(500, Timeout.Infinite);
        }

        public static void UpdateGraphicsConfig()
        {
            ConfigurationState config = ConfigurationState.Instance;

            GraphicsConfig.ResScale = config.Graphics.ResScale == -1 ? config.Graphics.ResScaleCustom : config.Graphics.ResScale;
            GraphicsConfig.MaxAnisotropy = config.Graphics.MaxAnisotropy;
            GraphicsConfig.ShadersDumpPath = config.Graphics.ShadersDumpPath;
            GraphicsConfig.EnableShaderCache = config.Graphics.EnableShaderCache;
            GraphicsConfig.EnableTextureRecompression = config.Graphics.EnableTextureRecompression;
            GraphicsConfig.EnableMacroHLE = config.Graphics.EnableMacroHLE;
        }

        public static void OnHostExited(NativeHost host)
        {
            bool relaunch;

            lock (_hostLock)
            {
                if (_host != host)
                {
                    return;
                }

                _host = null;

                // A game asked to start another program (ExecuteProgram); the UI starts it again on the same layer
                // the next time it launches, so only clear the request here.
                relaunch = UserChannelPersistence.PreviousIndex != -1 && UserChannelPersistence.ShouldRestart;
                UserChannelPersistence.ShouldRestart = false;
            }

            if (relaunch)
            {
                NativeBridge.Post("notice", new JsonObject
                {
                    ["title"] = "Restart the Game",
                    ["message"] = "The game asked to restart into another part of itself. Start it again from your library.",
                });
            }

            // Save what changed while the game ran, then drop its own settings: reloading puts the global ones back.
            if (NativeGameSettings.IsActive)
            {
                SaveConfig();
                NativeGameSettings.EndSession();
            }

            Program.ReloadConfig();
            UpdateGraphicsConfig();

            NativeBridge.Post("settings.changed", new JsonObject());
        }

        public static void OnViewResized(double width, double height, double scale)
        {
            Mouse?.SetClientSize((int)Math.Round(width / Math.Max(scale, 1)), (int)Math.Round(height / Math.Max(scale, 1)));

            lock (_hostLock)
            {
                _host?.Resize((int)width, (int)height);
            }
        }

        public static void OnFocusChanged(bool focused)
        {
            lock (_hostLock)
            {
                _host?.IsFocused = focused;
            }

            if (!focused)
            {
                Keyboard?.Clear();
                Mouse?.Clear();
            }
        }

        public static JsonObject HandleCommand(string name, JsonObject args)
        {
            switch (name)
            {
                case "app.info":
                    return Info();
                case "app.quit":
                    return Quit();
                case "app.revealLogs":
                    return Reveal(AppDataManager.LogsDirPath ?? AppDataManager.BaseDirPath);

                case "library.refresh":
                    RefreshLibrary();
                    return NativeBridge.Ok();
                case "library.addDir":
                    return AddDirectory(String(args, "path"), String(args, "kind"));
                case "library.removeDir":
                    return RemoveDirectory(String(args, "path"), String(args, "kind"));
                case "library.inspect":
                    return Inspect(String(args, "path"));

                case "game.launch":
                    return Launch(args);
                case "game.stop":
                    WithHost(host => host.Stop());
                    return NativeBridge.Ok();
                case "game.pause":
                    WithHost(host => host.Pause());
                    return NativeBridge.Ok();
                case "game.resume":
                    WithHost(host => host.Resume());
                    return NativeBridge.Ok();
                case "game.toggleVSync":
                    WithHost(host => host.ToggleVSync());
                    SaveConfig();
                    return NativeBridge.Ok();
                case "game.screenshot":
                    WithHost(host => host.RequestScreenshot());
                    return NativeBridge.Ok();
                case "game.revealFolder":
                    return RevealGameFolder(String(args, "id"), String(args, "folder"));
                case "game.settings.get":
                    return NativeGameSettings.Get(String(args, "id"));
                case "game.settings.set":
                    return NativeGameSettings.Set(String(args, "id"), String(args, "key"), args["value"]);

                case "content.get":
                    return NativeContent.Get(String(args, "id"));
                case "content.selectUpdate":
                    return NativeContent.SelectUpdate(String(args, "id"), String(args, "path"));
                case "content.setDlc":
                    return NativeContent.SetDlc(String(args, "id"), String(args, "path"), String(args, "contentPath"), args["enabled"]?.GetValue<bool>() ?? true);
                case "content.add":
                    return NativeContent.Add(String(args, "id"), String(args, "path"));
                case "content.remove":
                    return NativeContent.Remove(String(args, "id"), String(args, "kind"), String(args, "path"));
                case "content.setMod":
                    return NativeContent.SetMod(String(args, "id"), String(args, "path"), args["enabled"]?.GetValue<bool>() ?? true);
                case "content.importMod":
                    return NativeContent.ImportMod(String(args, "id"), String(args, "path"));
                case "content.setCheat":
                    return NativeContent.SetCheat(String(args, "id"), String(args, "key"), args["enabled"]?.GetValue<bool>() ?? true);

                case "settings.get":
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["settings"] = NativeSettings.Snapshot(),
                        ["options"] = NativeSettings.Options(),
                        ["ownSettings"] = NativeGameSettings.ActiveKeys(),
                    };
                case "settings.set":
                    return SetSetting(String(args, "key"), args["value"], args["game"]?.GetValue<bool>() ?? false);

                case "keys.install":
                    return InstallKeys(String(args, "path"));
                case "firmware.verify":
                    return VerifyFirmware(String(args, "path"));
                case "firmware.install":
                    return InstallFirmware(String(args, "path"));

                case "profiles.select":
                    return SelectProfile(String(args, "id"));
                case "profiles.add":
                    return NativeProfiles.Add(String(args, "name"));
                case "profiles.rename":
                    return NativeProfiles.Rename(String(args, "id"), String(args, "name"));
                case "profiles.setImage":
                    return NativeProfiles.SetImage(String(args, "id"), String(args, "image"));
                case "profiles.delete":
                    return NativeProfiles.Delete(String(args, "id"));
                case "profiles.avatars":
                    return NativeProfiles.Avatars();

                case "input.get":
                    return NativeInputSettings.Get();
                case "input.setDevice":
                    return NativeInputSettings.SetDevice(String(args, "player"), String(args, "id"));
                case "input.setType":
                    return NativeInputSettings.SetType(String(args, "player"), String(args, "type"));
                case "input.setBinding":
                    return NativeInputSettings.SetBinding(String(args, "player"), String(args, "button"), args);
                case "input.setOption":
                    return NativeInputSettings.SetOption(String(args, "player"), String(args, "option"), args["value"]);
                case "input.reset":
                    return NativeInputSettings.Reset(String(args, "player"));
                case "input.listen":
                    return NativeInputSettings.Listen(String(args, "player"), args["timeout"]?.GetValue<double>() ?? 8);
                case "input.cancelListen":
                    return NativeInputSettings.CancelListen();

                default:
                    return NativeBridge.Error($"Unknown command '{name}'.");
            }
        }

        private static string String(JsonObject args, string key) => args[key]?.GetValue<string>() ?? string.Empty;

        internal static void WithRunningHost(Action<NativeHost> action) => WithHost(action);

        private static void WithHost(Action<NativeHost> action)
        {
            NativeHost host;

            lock (_hostLock)
            {
                host = _host;
            }

            if (host != null)
            {
                action(host);
            }
        }

        // App

        private static JsonObject Info()
        {
            string firmware = null;

            try
            {
                firmware = ContentManager.GetCurrentFirmwareVersion()?.VersionString;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Could not read the firmware version: {ex.Message}");
            }

            JsonArray profiles = [];

            foreach (UserProfile profile in AccountManager.GetAllUsers().OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase))
            {
                profiles.Add((JsonNode)new JsonObject
                {
                    ["id"] = profile.UserId.ToString(),
                    ["name"] = profile.Name,
                    ["image"] = CacheImage("profiles", profile.UserId.ToString(), profile.Image),
                });
            }

            JsonArray gameDirs = [];
            JsonArray autoloadDirs = [];

            foreach (string directory in ConfigurationState.Instance.UI.GameDirs.Value)
            {
                gameDirs.Add((JsonNode)directory);
            }

            foreach (string directory in ConfigurationState.Instance.UI.AutoloadDirs.Value)
            {
                autoloadDirs.Add((JsonNode)directory);
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["version"] = Program.Version,
                ["dataDir"] = AppDataManager.BaseDirPath,
                ["keysInstalled"] = KeysInstalled(),
                ["firmwareVersion"] = firmware,
                ["profiles"] = profiles,
                ["activeProfile"] = AccountManager.LastOpenedUser?.UserId.ToString(),
                ["gameDirs"] = gameDirs,
                ["autoloadDirs"] = autoloadDirs,
                ["launchPath"] = Interlocked.Exchange(ref _launchPath, null),
            };
        }

        private static bool KeysInstalled()
        {
            return File.Exists(Path.Combine(AppDataManager.KeysDirPath, "prod.keys")) ||
                (AppDataManager.Mode == AppDataManager.LaunchMode.UserProfile && File.Exists(Path.Combine(AppDataManager.KeysDirPathUser, "prod.keys")));
        }

        private static JsonObject Quit()
        {
            // Quitting can be asked for more than once (AppKit, then SIGTERM); shut down only the first time.
            if (Interlocked.Exchange(ref _quitting, 1) != 0)
            {
                return NativeBridge.Ok();
            }

            NativeHost host;

            lock (_hostLock)
            {
                host = _host;
            }

            host?.StopAndWait(TimeSpan.FromSeconds(15));

            ApplicationLibrary.CancelLoading();
            SaveConfig();

            try
            {
                InputManager.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Input shutdown failed: {ex.Message}");
            }

            Program.Exit();

            return NativeBridge.Ok();
        }

        private static JsonObject Reveal(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.Exists(path))
            {
                return NativeBridge.Error($"{path} does not exist yet.");
            }

            Process.Start(new ProcessStartInfo("/usr/bin/open", ["-R", path]) { UseShellExecute = false });

            return NativeBridge.Ok();
        }

        // Library

        private static void RefreshLibrary()
        {
            lock (_libraryLock)
            {
                if (_libraryThread is { IsAlive: true })
                {
                    ApplicationLibrary.CancelLoading();
                    _libraryThread.Join();
                }

                _libraryThread = new Thread(ScanLibrary) { Name = "PX.LibraryScan", IsBackground = true };
                _libraryThread.Start();
            }
        }

        private static void ScanLibrary()
        {
            try
            {
                ApplicationLibrary.DesiredLanguage = ConfigurationState.Instance.System.Language;
                ApplicationLibrary.LoadApplications(ConfigurationState.Instance.UI.GameDirs);

                List<string> autoloadDirs = ConfigurationState.Instance.UI.AutoloadDirs.Value;

                if (autoloadDirs.Count > 0)
                {
                    ApplicationLibrary.AutoLoadTitleUpdates(autoloadDirs, out _);
                    ApplicationLibrary.AutoLoadDownloadableContents(autoloadDirs, out _);
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Library scan failed: {ex}");
            }
            finally
            {
                PostLibrary();
            }
        }

        /// <summary>
        /// Sends the UI the library as it is now, after a scan or a change to a game's update.
        /// </summary>
        public static void PostLibrary()
        {
            NativeBridge.Post("library.updated", new JsonObject
            {
                ["games"] = GamesToJson(ApplicationLibrary.Applications.Items),
                ["loading"] = false,
            });
        }

        private static JsonArray GamesToJson(IEnumerable<ApplicationData> applications)
        {
            JsonArray games = [];

            foreach (ApplicationData application in applications)
            {
                games.Add((JsonNode)new JsonObject
                {
                    ["id"] = application.IdString,
                    ["path"] = application.Path,
                    ["name"] = application.Name,
                    ["developer"] = application.Developer,
                    ["version"] = application.Version,
                    ["icon"] = CacheImage("icons", application.IdString, application.Icon),
                    ["fileSize"] = application.FileSize,
                    ["fileType"] = application.FileExtension,
                    ["timePlayed"] = application.TimePlayed.TotalSeconds,
                    ["lastPlayed"] = application.LastPlayed is { } lastPlayed ? new DateTimeOffset(lastPlayed.ToUniversalTime()).ToUnixTimeSeconds() : null,
                    ["favorite"] = application.Favorite,
                });
            }

            return games;
        }

        /// <summary>
        /// Writes an image the UI shows (title icon, profile avatar) to the cache folder and returns its path.
        /// </summary>
        private static string CacheImage(string kind, string id, byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return null;
            }

            try
            {
                string extension = data is [0x89, 0x50, 0x4E, 0x47, ..] ? "png" : "jpg";
                string hash = Convert.ToHexStringLower(SHA256.HashData(data))[..12];
                string directory = Path.Combine(_cacheDirectory, kind);
                string path = Path.Combine(directory, $"{id}-{hash}.{extension}");

                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllBytes(path, data);
                }

                return path;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Could not cache an image for {id}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The folders the library scans: games, or (kind "autoload") updates and DLC to apply to them.
        /// </summary>
        private static ReactiveObject<List<string>> Directories(string kind)
            => kind == "autoload" ? ConfigurationState.Instance.UI.AutoloadDirs : ConfigurationState.Instance.UI.GameDirs;

        private static JsonObject AddDirectory(string path, string kind)
        {
            if (!Directory.Exists(path))
            {
                return NativeBridge.Error($"{path} is not a folder.");
            }

            List<string> directories = [.. Directories(kind).Value];

            if (!directories.Contains(path))
            {
                directories.Add(path);
                Directories(kind).Value = directories;
                SaveConfig();
            }

            return NativeBridge.Ok();
        }

        private static JsonObject RemoveDirectory(string path, string kind)
        {
            List<string> directories = [.. Directories(kind).Value];

            if (directories.Remove(path))
            {
                Directories(kind).Value = directories;
                SaveConfig();
            }

            return NativeBridge.Ok();
        }

        private static JsonObject Inspect(string path)
        {
            if (!ApplicationLibrary.TryGetApplicationsFromFile(path, out List<ApplicationData> applications) || applications.Count == 0)
            {
                return NativeBridge.Error($"{Path.GetFileName(path)} is not a game Borgir can read.");
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["games"] = GamesToJson(applications),
                ["loading"] = false,
            };
        }

        // Emulation

        private static JsonObject Launch(JsonObject args)
        {
            string path = String(args, "path");
            string idText = String(args, "id");
            string layerText = String(args, "layer");

            if (!nint.TryParse(layerText, System.Globalization.NumberStyles.HexNumber, null, out nint layer) || layer == 0)
            {
                return NativeBridge.Error("The game view has no Metal layer.");
            }

            ulong.TryParse(idText, System.Globalization.NumberStyles.HexNumber, null, out ulong applicationId);

            int width = (int)(args["width"]?.GetValue<double>() ?? 1280);
            int height = (int)(args["height"]?.GetValue<double>() ?? 720);
            double scale = args["scale"]?.GetValue<double>() ?? 2;

            lock (_hostLock)
            {
                if (_host != null)
                {
                    ReleaseLayer(layer);
                    return NativeBridge.Error("A game is already running.");
                }

                if (!KeysInstalled())
                {
                    ReleaseLayer(layer);
                    return NativeBridge.Error("Production keys are missing. Install your prod.keys from Settings › General.");
                }

                if (!SetupValidator.CanStartApplication(ContentManager, path, out UserError userError))
                {
                    ReleaseLayer(layer);

                    return NativeBridge.Error(userError switch
                    {
                        UserError.NoFirmware => "Firmware is not installed. Install the firmware dumped from your Switch from Settings › General.",
                        UserError.ApplicationNotFound => $"{path} could not be found.",
                        UserError.NoKeys => "Production keys are missing. Install your prod.keys from Settings › General.",
                        _ => $"The game cannot start ({userError}).",
                    });
                }

                Logger.RestartTime();

                Mouse.SetClientSize((int)Math.Round(width / Math.Max(scale, 1)), (int)Math.Round(height / Math.Max(scale, 1)));

                NativeGameSettings.BeginSession(applicationId);

                _host = new NativeHost(path, applicationId, layer, width, height);
                _host.Start();
            }

            // The game's own settings may have replaced some global ones.
            NativeBridge.Post("settings.changed", new JsonObject());

            return NativeBridge.Ok();
        }

        private static void ReleaseLayer(nint layer)
        {
            // The UI hands over a retained layer; balance it when no renderer will take it.
            new SharpMetal.QuartzCore.CAMetalLayer(layer).Dispose();
        }

        /// <summary>
        /// Rereads the enabled cheats when <paramref name="applicationId"/> is the game running.
        /// </summary>
        internal static void EnableCheatsInRunningGame(ulong applicationId)
        {
            WithHost(host =>
            {
                try
                {
                    if (host.Device?.Processes?.ActiveApplication?.ProgramId == applicationId)
                    {
                        host.Device.EnableCheats();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Application, $"Could not update the running game's cheats: {ex.Message}");
                }
            });
        }

        private static JsonObject RevealGameFolder(string id, string folder)
        {
            if (!ulong.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out ulong titleId))
            {
                return NativeBridge.Error("Unknown title.");
            }

            string titleIdText = titleId.ToString("x16");

            string path = folder switch
            {
                "mods" => NativeContent.ModsDirectory(titleIdText),
                "cache" => Path.Combine(AppDataManager.GamesDirPath, titleIdText, "cache"),
                _ => SaveDirectory(titleId),
            };

            if (path == null)
            {
                return NativeBridge.Error("This game has no save data yet.");
            }

            Directory.CreateDirectory(path);

            // Open the folder itself, as Ryujinx does; revealing it would show the folder above with this one selected.
            Process.Start(new ProcessStartInfo("/usr/bin/open", [path]) { UseShellExecute = false });

            return NativeBridge.Ok();
        }

        private static string SaveDirectory(ulong titleId)
        {
            UserProfile user = AccountManager.LastOpenedUser;

            if (user == null)
            {
                return null;
            }

            // Save data lives under bis/user/save/<save data id>; ask the save data index which one belongs to this title:
            // the active profile's, or, for games that save per console, the device save.
            SaveDataFilter filter = SaveDataFilter.Make(titleId, SaveDataType.Account, user.UserId.ToLibHac(), saveDataId: default, index: default);
            Result result = LibHacHorizonManager.RyujinxClient.Fs.FindSaveDataWithFilter(out SaveDataInfo saveDataInfo, SaveDataSpaceId.User, in filter);

            if (result.IsFailure())
            {
                filter = SaveDataFilter.Make(titleId, SaveDataType.Device, userId: default, saveDataId: default, index: default);
                result = LibHacHorizonManager.RyujinxClient.Fs.FindSaveDataWithFilter(out saveDataInfo, SaveDataSpaceId.User, in filter);
            }

            if (result.IsFailure())
            {
                return null;
            }

            string saveRoot = Path.Combine(VirtualFileSystem.GetNandPath(), $"user/save/{saveDataInfo.SaveDataId:x16}");

            // "0" is the committed copy the game loads next; "1" is the working copy.
            foreach (string candidate in new[] { Path.Combine(saveRoot, "0"), Path.Combine(saveRoot, "1") })
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return saveRoot;
        }

        // Settings

        /// <summary>
        /// Changes a setting. With <paramref name="forGame"/> (the in-game overlay and menu) a setting a game can have
        /// its own value for becomes the running game's own, leaving the global one as it is.
        /// </summary>
        private static JsonObject SetSetting(string key, JsonNode value, bool forGame)
        {
            ulong running = RunningTitleId;

            if (forGame && running != 0 && NativeGameSettings.IsOverridable(key))
            {
                JsonObject reply = NativeGameSettings.Set(running.ToString("x16"), key, value);

                if (reply["ok"]?.GetValue<bool>() != true)
                {
                    return reply;
                }
            }
            else
            {
                try
                {
                    NativeSettings.Set(key, value);
                }
                catch (ArgumentException ex)
                {
                    return NativeBridge.Error(ex.Message);
                }

                UpdateGraphicsConfig();
                SaveConfigSoon();
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["settings"] = NativeSettings.Snapshot(),
                ["ownSettings"] = NativeGameSettings.ActiveKeys(),
            };
        }

        // Keys, firmware, profiles

        private static JsonObject InstallKeys(string path)
        {
            try
            {
                string keysDirectory = AppDataManager.Mode == AppDataManager.LaunchMode.UserProfile && Directory.Exists(AppDataManager.KeysDirPathUser)
                    ? AppDataManager.KeysDirPathUser
                    : AppDataManager.KeysDirPath;

                ContentManager.InstallKeys(path, keysDirectory);
                VirtualFileSystem.ReloadKeySet();

                Logger.Info?.Print(LogClass.Application, $"Installed keys from {path}");

                return NativeBridge.Ok();
            }
            catch (Exception ex)
            {
                return NativeBridge.Error(ex.Message);
            }
        }

        private static JsonObject VerifyFirmware(string path)
        {
            try
            {
                SystemVersion version = ContentManager.VerifyFirmwarePackage(path);

                return version == null
                    ? NativeBridge.Error($"No firmware was found in {Path.GetFileName(path)}.")
                    : new JsonObject { ["ok"] = true, ["version"] = version.VersionString };
            }
            catch (MissingKeyException)
            {
                return NativeBridge.Error("Install your production keys first; they are needed to read firmware.");
            }
            catch (Exception ex)
            {
                return NativeBridge.Error(ex.Message);
            }
        }

        private static JsonObject InstallFirmware(string path)
        {
            Thread installer = new(() =>
            {
                try
                {
                    ContentManager.InstallFirmware(path);

                    string version = ContentManager.GetCurrentFirmwareVersion()?.VersionString ?? "?";

                    // The Mii editor caches data from the old firmware.
                    DirectoryInfo miiEditorCache = new(Path.Combine(AppDataManager.GamesDirPath, "0100000000001009", "cache"));

                    if (miiEditorCache.Exists)
                    {
                        miiEditorCache.Delete(true);
                    }

                    Logger.Info?.Print(LogClass.Application, $"Installed firmware {version}");

                    NativeBridge.Post("firmware.installed", new JsonObject
                    {
                        ["title"] = "Firmware Installed",
                        ["message"] = $"Firmware {version} is ready.",
                    });
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Application, $"Firmware install failed: {ex}");

                    NativeBridge.Post("notice", new JsonObject
                    {
                        ["title"] = "Unable to Install Firmware",
                        ["message"] = ex.Message,
                    });
                }
            })
            {
                Name = "PX.FirmwareInstaller",
                IsBackground = true,
            };

            installer.Start();

            return NativeBridge.Ok();
        }

        private static JsonObject SelectProfile(string id)
        {
            try
            {
                AccountManager.OpenUser(new UserId(id));

                return NativeBridge.Ok();
            }
            catch (Exception ex)
            {
                return NativeBridge.Error(ex.Message);
            }
        }
    }
}
