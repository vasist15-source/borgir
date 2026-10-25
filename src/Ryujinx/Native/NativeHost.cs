using Ryujinx.Audio.Backends.Apple;
using Ryujinx.Audio.Backends.Dummy;
using Ryujinx.Audio.Backends.OpenAL;
using Ryujinx.Audio.Backends.SDL3;
using Ryujinx.Audio.Backends.SoundIo;
using Ryujinx.Audio.Integration;
using Ryujinx.Ava.Common;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Multiplayer;
using Ryujinx.Common.Logging;
using Ryujinx.Common.SystemInterop;
using Ryujinx.Common.Utilities;
using Ryujinx.Cpu;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.Metal;
using Ryujinx.HLE.FileSystem;
using Ryujinx.Input;
using Ryujinx.Input.HLE;
using SharpMetal.QuartzCore;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Key = Ryujinx.Input.Key;
using Switch = Ryujinx.HLE.Switch;
using VSyncMode = Ryujinx.Common.Configuration.VSyncMode;

namespace Ryujinx.Ava.Native
{
    /// <summary>
    /// One emulation session rendering into the native UI's CAMetalLayer. Starts and shuts down in the same
    /// order as upstream Ryujinx's AppHost.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal sealed class NativeHost
    {
        private const float MaxResolutionScale = 4.0f;
        private const float VolumeDelta = 0.05f;
        private static readonly TimeSpan _statusInterval = TimeSpan.FromMilliseconds(500);

        private readonly nint _layer;
        private readonly bool _isFirmwareTitle;
        private readonly Stopwatch _playTimer = new();
        private readonly Stopwatch _statusTimer = new();
        private readonly ManualResetEvent _gpuDoneEvent = new(false);
        private readonly CancellationTokenSource _gpuCancellation = new();
        private readonly Lock _screenshotLock = new();

        private Thread _hostThread;
        private Thread _renderThread;
        private IRenderer _renderer;
        private IKeyboard _keyboard;
        private KeyboardHotkeyState _prevHotkeyState;
        private volatile bool _isActive;
        private volatile bool _stopRequested;
        private volatile bool _inputReady;
        private bool _renderingStarted;
        private bool _layerOwned = true;
        private string _gpuName;
        private string _stopError;
        private float _volumeBeforeMute;

        private int _width;
        private int _height;

        private uint _displayCount;
        private uint _previousCount;
        private DateTime _lastShaderReset;

        public Switch Device { get; private set; }
        public NpadManager NpadManager { get; private set; }
        public TouchScreenManager TouchScreenManager { get; private set; }
        public string ApplicationPath { get; }
        public ulong ApplicationId { get; }
        public bool ScreenshotRequested { get; set; }
        public bool IsFocused { get; set; } = true;

        public NativeHost(string applicationPath, ulong applicationId, nint metalLayer, int width, int height)
        {
            ApplicationPath = applicationPath;
            ApplicationId = applicationId;
            _layer = metalLayer;
            _width = width;
            _height = height;

            if (ApplicationPath.StartsWith("@SystemContent"))
            {
                ApplicationPath = VirtualFileSystem.SwitchPathToSystemPath(ApplicationPath);
                _isFirmwareTitle = true;
            }
        }

        public void Start()
        {
            _hostThread = new Thread(HostThreadMain) { Name = "PX.Emulation" };
            _hostThread.Start();
        }

        /// <summary>
        /// Asks the session to end. The host thread tears it down and posts game.stopped.
        /// </summary>
        public void Stop()
        {
            _stopRequested = true;
            _isActive = false;
        }

        /// <summary>
        /// Stops the session and waits for it to shut down, for app quit.
        /// </summary>
        public void StopAndWait(TimeSpan timeout)
        {
            Stop();
            _hostThread?.Join(timeout);
        }

        public void Pause()
        {
            if (Device == null || Device.System.IsPaused)
            {
                return;
            }

            Device.System.TogglePauseEmulation(true);
            _playTimer.Stop();

            Logger.Info?.Print(LogClass.Emulation, "Emulation was paused.");

            GCSettings.LatencyMode = GCLatencyMode.Interactive;
            PostStatus();
        }

        public void Resume()
        {
            if (Device == null || !Device.System.IsPaused)
            {
                return;
            }

            Device.System.TogglePauseEmulation(false);
            _playTimer.Start();

            Logger.Info?.Print(LogClass.Emulation, "Emulation was resumed.");

            if (ConfigurationState.Instance.System.GCLowLatency)
            {
                GCSettings.LatencyMode = GCLatencyMode.LowLatency;
            }

            PostStatus();
        }

        public void Resize(int width, int height)
        {
            _width = width;
            _height = height;

            _renderer?.Window?.SetSize(width, height);
        }

        /// <summary>
        /// Applies changed controller settings to the running game.
        /// </summary>
        public void ReloadInput()
        {
            if (!_inputReady)
            {
                return;
            }

            ConfigurationState config = ConfigurationState.Instance;

            NpadManager?.ReloadConfiguration(config.Hid.InputConfig.Value, config.Hid.PlayerInputAssignments.Value, config.Hid.EnableKeyboard, config.Hid.EnableMouse);
        }

        public void RequestScreenshot()
        {
            ScreenshotRequested = true;
        }

        public void ToggleVSync()
        {
            if (Device == null)
            {
                return;
            }

            VSyncMode next = Device.VSyncMode.Next(ConfigurationState.Instance.Graphics.EnableCustomVSyncInterval.Value);

            // Toggled in game (the overlay, the menu or the hotkey), so it is this game's V-Sync.
            NativeGameSettings.Set(ApplicationId.ToString("x16"), "vsyncMode", next.ToString());
            NativeBridge.Post("settings.changed", new JsonObject());
        }

        private void HostThreadMain()
        {
            try
            {
                InitEmulatedSwitch();

                if (!LoadApplication(out string error))
                {
                    _stopError = error;
                    return;
                }

                ApplicationLibrary.LoadAndSaveMetaData(Device.Processes.ActiveApplication.ProgramIdText, metadata => metadata.UpdatePreGame());

                _playTimer.Start();

                if (ConfigurationState.Instance.System.GCLowLatency)
                {
                    GCSettings.LatencyMode = GCLatencyMode.LowLatency;
                    Logger.Info?.Print(LogClass.Application, "Garbage collector set to low latency mode.");
                }

                DisplaySleep.Prevent();

                NpadManager.Initialize(Device, ConfigurationState.Instance.Hid.InputConfig, ConfigurationState.Instance.Hid.PlayerInputAssignments, ConfigurationState.Instance.Hid.EnableKeyboard, ConfigurationState.Instance.Hid.EnableMouse);
                TouchScreenManager.Initialize(Device);
                _inputReady = true;

                SetProgressHandlers();
                SubscribeToConfiguration();

                _isActive = !_stopRequested;

                _renderThread = new Thread(RenderLoop) { Name = "PX.RenderThread" };
                _renderThread.Start();

                Rainbow.Enable();

                NativeBridge.Post("game.started", new JsonObject
                {
                    ["title"] = Device.Processes.ActiveApplication.Name,
                    ["titleId"] = Device.Processes.ActiveApplication.ProgramIdText,
                });

                MainLoop();
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Emulation failed: {ex}");
                _stopError = ex.Message;
            }
            finally
            {
                Shutdown();

                NativeBridge.Post("game.stopped", new JsonObject { ["error"] = _stopError });
                NativeApp.OnHostExited(this);
            }
        }

        private void InitEmulatedSwitch()
        {
            NativeApp.VirtualFileSystem.ReloadKeySet();

            GraphicsConfig.MetalBackendActive = true;
            NativeApp.UpdateGraphicsConfig();

            IRenderer renderer = new MetalRenderer(() => new CAMetalLayer(_layer));

            // The renderer's window releases the layer when it is disposed.
            _layerOwned = false;

            NpadManager = NativeApp.InputManager.CreateNpadManager();
            TouchScreenManager = NativeApp.InputManager.CreateTouchScreenManager();
            _keyboard = (IKeyboard)NativeApp.InputManager.KeyboardDriver.GetGamepad("0");

            Device = new Switch(ConfigurationState.Instance.CreateHleConfiguration()
                .Configure(
                    NativeApp.VirtualFileSystem,
                    NativeApp.LibHacHorizonManager,
                    NativeApp.ContentManager,
                    NativeApp.AccountManager,
                    NativeApp.UserChannelPersistence,
                    renderer.TryMakeThreaded(ConfigurationState.Instance.Graphics.BackendThreading),
                    InitializeAudio(),
                    new NativeHostUIHandler(this)
                )
            );
        }

        private bool LoadApplication(out string error)
        {
            error = null;

            SystemVersion firmwareVersion = NativeApp.ContentManager.GetCurrentFirmwareVersion();
            Logger.Notice.Print(LogClass.Application, $"Using Firmware Version: {firmwareVersion?.VersionString}");

            bool loaded;

            if (_isFirmwareTitle)
            {
                Logger.Info?.Print(LogClass.Application, "Loading as Firmware Title (NCA).");
                loaded = Device.LoadNca(ApplicationPath);
            }
            else if (Directory.Exists(ApplicationPath))
            {
                string[] romFsFiles = Directory.GetFiles(ApplicationPath, "*.istorage");

                if (romFsFiles.Length == 0)
                {
                    romFsFiles = Directory.GetFiles(ApplicationPath, "*.romfs");
                }

                Logger.Notice.Print(LogClass.Application, $"Loading unpacked content archive from '{ApplicationPath}'.");

                loaded = romFsFiles.Length > 0 ? Device.LoadCart(ApplicationPath, romFsFiles[0]) : Device.LoadCart(ApplicationPath);
            }
            else if (File.Exists(ApplicationPath))
            {
                Logger.Notice.Print(LogClass.Application, $"Loading content archive from '{ApplicationPath}'.");

                switch (Path.GetExtension(ApplicationPath).ToLowerInvariant())
                {
                    case ".xci":
                        loaded = Device.LoadXci(ApplicationPath, ApplicationId);
                        break;
                    case ".nca":
                        loaded = Device.LoadNca(ApplicationPath);
                        break;
                    case ".nsp":
                    case ".pfs0":
                        loaded = Device.LoadNsp(ApplicationPath, ApplicationId);
                        break;
                    default:
                        try
                        {
                            loaded = Device.LoadProgram(ApplicationPath);
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            loaded = false;
                        }

                        break;
                }
            }
            else
            {
                error = $"{ApplicationPath} no longer exists.";
                return false;
            }

            if (!loaded)
            {
                error = "The game could not be loaded. Check that your keys and firmware are up to date; the log has details.";
            }

            return loaded;
        }

        private static IHardwareDeviceDriver InitializeAudio()
        {
            List<AudioBackend> availableBackends =
            [
                AudioBackend.AudioToolbox,
                AudioBackend.SDL3,
                AudioBackend.SoundIo,
                AudioBackend.OpenAl,
                AudioBackend.Dummy
            ];

            AudioBackend preferredBackend = ConfigurationState.Instance.System.AudioBackend.Value;

            if (preferredBackend is AudioBackend.SDL2)
            {
                preferredBackend = AudioBackend.SDL3;
            }

            if (availableBackends.Remove(preferredBackend))
            {
                availableBackends.Insert(0, preferredBackend);
            }

            foreach (AudioBackend backend in availableBackends)
            {
                IHardwareDeviceDriver driver = backend switch
                {
                    AudioBackend.AudioToolbox => AppleHardwareDeviceDriver.IsSupported ? new AppleHardwareDeviceDriver() : null,
                    AudioBackend.SDL3 => SDL3HardwareDeviceDriver.IsSupported ? new SDL3HardwareDeviceDriver() : null,
                    AudioBackend.SoundIo => SoundIoHardwareDeviceDriver.IsSupported ? new SoundIoHardwareDeviceDriver() : null,
                    AudioBackend.OpenAl => OpenALHardwareDeviceDriver.IsSupported ? new OpenALHardwareDeviceDriver() : null,
                    _ => new DummyHardwareDeviceDriver(),
                };

                if (driver != null)
                {
                    if (backend == AudioBackend.Dummy)
                    {
                        Logger.Warning?.Print(LogClass.Audio, "Audio output is off (Dummy). Choose an output in Settings › System › Audio.");
                    }
                    else
                    {
                        Logger.Info?.Print(LogClass.Audio, $"Audio output: {backend}");
                    }

                    if (backend != ConfigurationState.Instance.System.AudioBackend.Value)
                    {
                        Logger.Warning?.Print(LogClass.Audio, $"{ConfigurationState.Instance.System.AudioBackend.Value} is not supported, using {backend}.");
                        ConfigurationState.Instance.System.AudioBackend.Value = backend;
                        NativeApp.SaveConfig();
                    }

                    return driver;
                }
            }

            return new DummyHardwareDeviceDriver();
        }

        private void RenderLoop()
        {
            _renderer = Device.Gpu.Renderer is ThreadedRenderer threaded ? threaded.BaseRenderer : Device.Gpu.Renderer;
            _renderer.ScreenCaptured += Renderer_ScreenCaptured;

            Device.Gpu.Renderer.Initialize(ConfigurationState.Instance.Logger.GraphicsDebugLevel);

            _renderer.Window?.SetAntiAliasing(ConfigurationState.Instance.Graphics.AntiAliasing);
            _renderer.Window?.SetScalingFilter(ConfigurationState.Instance.Graphics.ScalingFilter);
            _renderer.Window?.SetScalingFilterLevel(ConfigurationState.Instance.Graphics.ScalingFilterLevel);
            _renderer.Window?.SetColorSpacePassthrough(ConfigurationState.Instance.Graphics.EnableColorSpacePassthrough);
            _renderer.Window?.SetSize(_width, _height);

            _gpuName = _renderer.GetHardwareInfo().GpuModel;

            Device.Gpu.Renderer.RunLoop(() =>
            {
                try
                {
                    ThreadQos.MarkTimeCritical();
                    Device.Gpu.SetGpuThread();
                    Device.Gpu.InitializeShaderCache(_gpuCancellation.Token);

                    _renderer.Window.ChangeVSyncMode(Device.VSyncMode);

                    while (_isActive)
                    {
                        if (Device.WaitFifo())
                        {
                            Device.Statistics.RecordFifoStart();
                            Device.ProcessFrame();
                            Device.Statistics.RecordFifoEnd();
                        }

                        while (Device.ConsumeFrameAvailable())
                        {
                            if (!_renderingStarted)
                            {
                                _renderingStarted = true;
                                NativeBridge.Post("game.firstFrame");
                                ScheduleDiagnosticScreenshot();
                            }

                            // Metal presents from the renderer itself; there is no buffer swap to do.
                            Device.PresentFrame(static () => { });
                        }
                    }
                }
                finally
                {
                    // Make sure all commands in the run loop are fully executed before leaving the loop.
                    if (Device.Gpu.Renderer is ThreadedRenderer threadedRenderer)
                    {
                        threadedRenderer.FlushThreadedCommands();
                    }

                    _gpuDoneEvent.Set();
                }
            });
        }

        private void MainLoop()
        {
            _statusTimer.Start();

            while (_isActive)
            {
                UpdateFrame();

                if (_statusTimer.Elapsed >= _statusInterval)
                {
                    _statusTimer.Restart();
                    PostStatus();
                }

                // Polling becomes expensive if it's not slept.
                Thread.Sleep(1);
            }
        }

        private void UpdateFrame()
        {
            float aspectRatio = ConfigurationState.Instance.Graphics.AspectRatio.Value.ToFloat();

            if (!IsFocused && ConfigurationState.Instance.Hid.DisableInputWhenOutOfFocus)
            {
                NativeApp.InputManager.KeyboardDriver.Clear();
            }

            NpadManager.Update(aspectRatio);

            if (IsFocused)
            {
                HandleHotkeys();
            }

            // Screenshots also come from the menu and the overlay, which work without keyboard focus.
            if (ScreenshotRequested)
            {
                ScreenshotRequested = false;
                _renderer?.Screenshot();
            }

            bool hasTouch = false;

            if (IsFocused && !ConfigurationState.Instance.Hid.EnableMouse.Value)
            {
                hasTouch = TouchScreenManager.Update(true, NativeApp.Mouse.IsButtonPressed(MouseButton.Button1), aspectRatio);
            }

            if (!hasTouch)
            {
                Device.Hid.Touchscreen.Update();
            }

            Device.Hid.DebugPad.Update();
        }

        private void HandleHotkeys()
        {
            KeyboardHotkeyState currentHotkeyState = GetHotkeyState();

            if (currentHotkeyState == _prevHotkeyState)
            {
                return;
            }

            if (ConfigurationState.Instance.Hid.Hotkeys.Value.TurboModeWhileHeld &&
                _keyboard.IsPressed((Key)ConfigurationState.Instance.Hid.Hotkeys.Value.TurboMode) != Device.TurboMode)
            {
                Device.ToggleTurbo();
            }

            switch (currentHotkeyState)
            {
                case KeyboardHotkeyState.ToggleVSyncMode:
                    ToggleVSync();
                    break;
                case KeyboardHotkeyState.CustomVSyncIntervalDecrement:
                    ConfigurationState.Instance.Graphics.CustomVSyncInterval.Value = Device.DecrementCustomVSyncInterval();
                    break;
                case KeyboardHotkeyState.CustomVSyncIntervalIncrement:
                    ConfigurationState.Instance.Graphics.CustomVSyncInterval.Value = Device.IncrementCustomVSyncInterval();
                    break;
                case KeyboardHotkeyState.TurboMode:
                    if (!ConfigurationState.Instance.Hid.Hotkeys.Value.TurboModeWhileHeld)
                    {
                        Device.ToggleTurbo();
                    }

                    break;
                case KeyboardHotkeyState.Screenshot:
                    ScreenshotRequested = true;
                    break;
                case KeyboardHotkeyState.ShowUI:
                    NativeBridge.Post("overlay.toggle");
                    break;
                case KeyboardHotkeyState.Pause:
                    if (Device.System.IsPaused)
                    {
                        Resume();
                    }
                    else
                    {
                        Pause();
                    }

                    break;
                case KeyboardHotkeyState.ToggleMute:
                    if (Device.IsAudioMuted())
                    {
                        Device.SetVolume(_volumeBeforeMute);
                    }
                    else
                    {
                        _volumeBeforeMute = Device.GetVolume();
                        Device.SetVolume(0);
                    }

                    break;
                case KeyboardHotkeyState.ResScaleUp:
                    GraphicsConfig.ResScale = GraphicsConfig.ResScale % MaxResolutionScale + 1;
                    break;
                case KeyboardHotkeyState.ResScaleDown:
                    GraphicsConfig.ResScale = (MaxResolutionScale + GraphicsConfig.ResScale - 2) % MaxResolutionScale + 1;
                    break;
                case KeyboardHotkeyState.VolumeUp:
                    ConfigurationState.Instance.System.AudioVolume.Value = Math.Clamp(MathF.Round(Device.GetVolume() + VolumeDelta, 2), 0, 1);
                    break;
                case KeyboardHotkeyState.VolumeDown:
                    ConfigurationState.Instance.System.AudioVolume.Value = Math.Clamp(MathF.Round(Device.GetVolume() - VolumeDelta, 2), 0, 1);
                    break;
            }

            _prevHotkeyState = currentHotkeyState;
        }

        private KeyboardHotkeyState GetHotkeyState()
        {
            KeyboardHotkeys hotkeys = ConfigurationState.Instance.Hid.Hotkeys.Value;

            (Key Key, KeyboardHotkeyState State)[] bindings =
            [
                ((Key)hotkeys.ToggleVSyncMode, KeyboardHotkeyState.ToggleVSyncMode),
                ((Key)hotkeys.Screenshot, KeyboardHotkeyState.Screenshot),
                ((Key)hotkeys.ShowUI, KeyboardHotkeyState.ShowUI),
                ((Key)hotkeys.Pause, KeyboardHotkeyState.Pause),
                ((Key)hotkeys.ToggleMute, KeyboardHotkeyState.ToggleMute),
                ((Key)hotkeys.ResScaleUp, KeyboardHotkeyState.ResScaleUp),
                ((Key)hotkeys.ResScaleDown, KeyboardHotkeyState.ResScaleDown),
                ((Key)hotkeys.VolumeUp, KeyboardHotkeyState.VolumeUp),
                ((Key)hotkeys.VolumeDown, KeyboardHotkeyState.VolumeDown),
                ((Key)hotkeys.CustomVSyncIntervalIncrement, KeyboardHotkeyState.CustomVSyncIntervalIncrement),
                ((Key)hotkeys.CustomVSyncIntervalDecrement, KeyboardHotkeyState.CustomVSyncIntervalDecrement),
                ((Key)hotkeys.TurboMode, KeyboardHotkeyState.TurboMode),
            ];

            foreach ((Key key, KeyboardHotkeyState state) in bindings)
            {
                if (_keyboard.IsPressed(key))
                {
                    return state;
                }
            }

            return KeyboardHotkeyState.None;
        }

        public JsonObject Status()
        {
            if (Device == null)
            {
                return [];
            }

            UpdateShaderCount();

            return new JsonObject
            {
                // Frame time is infinite until the first frame; JSON has no infinity.
                ["fps"] = Finite(Device.Statistics.GetGameFrameRate()),
                ["frameTime"] = Finite(Device.Statistics.GetGameFrameTime()),
                ["fifo"] = Finite(Device.Statistics.GetFifoPercent()),
                ["vsync"] = Device.VSyncMode.ToString(),
                ["docked"] = ConfigurationState.Instance.System.EnableDockedMode.Value,
                ["volume"] = Device.GetVolume(),
                ["resScale"] = GraphicsConfig.ResScale,
                ["aspectRatio"] = ConfigurationState.Instance.Graphics.AspectRatio.Value.ToString(),
                ["shaders"] = _displayCount,
                ["paused"] = Device.System.IsPaused,
                ["gpu"] = _gpuName,
            };
        }

        /// <summary>
        /// PX_SCREENSHOT_AFTER=<seconds> takes one screenshot that long after the first frame, to check captures
        /// without a keyboard.
        /// </summary>
        private void ScheduleDiagnosticScreenshot()
        {
            if (!double.TryParse(Environment.GetEnvironmentVariable("PX_SCREENSHOT_AFTER"), System.Globalization.CultureInfo.InvariantCulture, out double seconds) || seconds < 0)
            {
                return;
            }

            Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ =>
            {
                Logger.Info?.Print(LogClass.Application, "Taking the PX_SCREENSHOT_AFTER screenshot.");
                ScreenshotRequested = true;
            });
        }

        private static double Finite(double value) => double.IsFinite(value) ? value : 0;

        private void PostStatus()
        {
            if (Device == null || !_isActive)
            {
                return;
            }

            try
            {
                NativeBridge.Post("game.status", Status());
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Could not report the game status: {ex.Message}");
            }
        }

        private void UpdateShaderCount()
        {
            if (_renderer == null || (_displayCount is 0 && _renderer.ProgramCount is 0))
            {
                return;
            }

            // A change in the number of compiled programs means new shaders were built since the last update.
            if (_renderer.ProgramCount != _previousCount)
            {
                _displayCount += _renderer.ProgramCount - _previousCount;
                _lastShaderReset = DateTime.Now;
                _previousCount = _renderer.ProgramCount;
            }
            else if (_lastShaderReset.AddSeconds(5) <= DateTime.Now)
            {
                _displayCount = 0;
            }
        }

        private void SetProgressHandlers()
        {
            if (Device.Processes.ActiveApplication.DiskCacheLoadState != null)
            {
                Device.Processes.ActiveApplication.DiskCacheLoadState.StateChanged += ProgressHandler;
            }

            Device.Gpu.ShaderCacheStateChanged += ProgressHandler;
        }

        private void ProgressHandler<T>(T state, int current, int total) where T : Enum
        {
            string stage = state switch
            {
                LoadState.Loading => "Loading the translation cache",
                LoadState.Loaded => "Translation cache loaded",
                ShaderCacheState.Start or ShaderCacheState.Loading => "Loading shaders",
                ShaderCacheState.Packaging => "Packaging shaders",
                ShaderCacheState.Loaded => "Shaders loaded",
                _ => "Loading",
            };

            NativeBridge.Post("game.loading", new JsonObject
            {
                ["stage"] = stage,
                ["current"] = current,
                ["total"] = total,
            });
        }

        private void Renderer_ScreenCaptured(object sender, ScreenCaptureImageInfo e)
        {
            if (e.Data.Length == 0 || e.Width <= 0 || e.Height <= 0)
            {
                Logger.Error?.Print(LogClass.Application, $"Screenshot is empty. Size : {e.Data.Length} bytes. Resolution : {e.Width}x{e.Height}", "Screenshot");
                return;
            }

            string applicationName = Device.Processes.ActiveApplication.Name;

            Task.Run(() =>
            {
                lock (_screenshotLock)
                {
                    string sanitizedApplicationName = FileSystemUtils.SanitizeFileName(applicationName);
                    DateTime now = DateTime.Now;
                    string directory = Path.Combine(AppDataManager.BaseDirPath, "screenshots");
                    string path = Path.Combine(directory, $"{sanitizedApplicationName}_{now:yyyy-MM-dd_HH-mm-ss}.png");

                    try
                    {
                        Directory.CreateDirectory(directory);

                        SKColorType colorType = e.IsBgra ? SKColorType.Bgra8888 : SKColorType.Rgba8888;
                        using SKBitmap bitmap = new(new SKImageInfo(e.Width, e.Height, colorType, SKAlphaType.Premul));

                        Marshal.Copy(e.Data, 0, bitmap.GetPixels(), e.Data.Length);

                        using SKBitmap bitmapToSave = new(bitmap.Width, bitmap.Height);
                        using SKCanvas canvas = new(bitmapToSave);

                        canvas.Clear(SKColors.Black);
                        canvas.SetMatrix(SKMatrix.CreateScale(e.FlipX ? -1 : 1, e.FlipY ? -1 : 1, bitmap.Width / 2f, bitmap.Height / 2f));
                        canvas.DrawBitmap(bitmap, SKPoint.Empty);

                        using SKData data = bitmapToSave.Encode(SKEncodedImageFormat.Png, 100);
                        using FileStream stream = File.OpenWrite(path);

                        data.SaveTo(stream);

                        Logger.Notice.Print(LogClass.Application, $"Screenshot saved to '{path}'.", "Screenshot");
                        NativeBridge.Post("screenshot.saved", new JsonObject { ["path"] = path });
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Application, $"Failed to save screenshot to {path}: {ex.Message}", "Screenshot");
                    }
                }
            });
        }

        private void SubscribeToConfiguration()
        {
            ConfigurationState.Instance.System.IgnoreMissingServices.Event += UpdateIgnoreMissingServicesState;
            ConfigurationState.Instance.Graphics.AspectRatio.Event += UpdateAspectRatioState;
            ConfigurationState.Instance.System.EnableDockedMode.Event += UpdateDockedModeState;
            ConfigurationState.Instance.System.AudioVolume.Event += UpdateAudioVolumeState;
            ConfigurationState.Instance.Graphics.AntiAliasing.Event += UpdateAntiAliasing;
            ConfigurationState.Instance.Graphics.ScalingFilter.Event += UpdateScalingFilter;
            ConfigurationState.Instance.Graphics.ScalingFilterLevel.Event += UpdateScalingFilterLevel;
            ConfigurationState.Instance.Graphics.EnableColorSpacePassthrough.Event += UpdateColorSpacePassthrough;
            ConfigurationState.Instance.Graphics.VSyncMode.Event += UpdateVSyncMode;
            ConfigurationState.Instance.Graphics.CustomVSyncInterval.Event += UpdateCustomVSyncIntervalValue;
            ConfigurationState.Instance.Graphics.EnableCustomVSyncInterval.Event += UpdateCustomVSyncIntervalEnabled;
            ConfigurationState.Instance.System.EnableInternetAccess.Event += UpdateEnableInternetAccessState;
            ConfigurationState.Instance.Multiplayer.LanInterfaceId.Event += UpdateLanInterfaceIdState;
            ConfigurationState.Instance.Multiplayer.Mode.Event += UpdateMultiplayerModeState;
        }

        private void UnsubscribeFromConfiguration()
        {
            ConfigurationState.Instance.System.IgnoreMissingServices.Event -= UpdateIgnoreMissingServicesState;
            ConfigurationState.Instance.Graphics.AspectRatio.Event -= UpdateAspectRatioState;
            ConfigurationState.Instance.System.EnableDockedMode.Event -= UpdateDockedModeState;
            ConfigurationState.Instance.System.AudioVolume.Event -= UpdateAudioVolumeState;
            ConfigurationState.Instance.Graphics.AntiAliasing.Event -= UpdateAntiAliasing;
            ConfigurationState.Instance.Graphics.ScalingFilter.Event -= UpdateScalingFilter;
            ConfigurationState.Instance.Graphics.ScalingFilterLevel.Event -= UpdateScalingFilterLevel;
            ConfigurationState.Instance.Graphics.EnableColorSpacePassthrough.Event -= UpdateColorSpacePassthrough;
            ConfigurationState.Instance.Graphics.VSyncMode.Event -= UpdateVSyncMode;
            ConfigurationState.Instance.Graphics.CustomVSyncInterval.Event -= UpdateCustomVSyncIntervalValue;
            ConfigurationState.Instance.Graphics.EnableCustomVSyncInterval.Event -= UpdateCustomVSyncIntervalEnabled;
            ConfigurationState.Instance.System.EnableInternetAccess.Event -= UpdateEnableInternetAccessState;
            ConfigurationState.Instance.Multiplayer.LanInterfaceId.Event -= UpdateLanInterfaceIdState;
            ConfigurationState.Instance.Multiplayer.Mode.Event -= UpdateMultiplayerModeState;
        }

        private void UpdateIgnoreMissingServicesState(object sender, ReactiveEventArgs<bool> e) => Device?.Configuration.IgnoreMissingServices = e.NewValue;

        private void UpdateAspectRatioState(object sender, ReactiveEventArgs<AspectRatio> e) => Device?.Configuration.AspectRatio = e.NewValue;

        private void UpdateDockedModeState(object sender, ReactiveEventArgs<bool> e) => Device?.System.ChangeDockedModeState(e.NewValue);

        private void UpdateAudioVolumeState(object sender, ReactiveEventArgs<float> e) => Device?.SetVolume(e.NewValue);

        private void UpdateAntiAliasing(object sender, ReactiveEventArgs<AntiAliasing> e) => _renderer?.Window?.SetAntiAliasing(e.NewValue);

        private void UpdateScalingFilter(object sender, ReactiveEventArgs<ScalingFilter> e)
        {
            _renderer?.Window?.SetScalingFilter(e.NewValue);
            _renderer?.Window?.SetScalingFilterLevel(ConfigurationState.Instance.Graphics.ScalingFilterLevel);
        }

        private void UpdateScalingFilterLevel(object sender, ReactiveEventArgs<int> e)
        {
            _renderer?.Window?.SetScalingFilter(ConfigurationState.Instance.Graphics.ScalingFilter);
            _renderer?.Window?.SetScalingFilterLevel(e.NewValue);
        }

        private void UpdateColorSpacePassthrough(object sender, ReactiveEventArgs<bool> e) => _renderer?.Window?.SetColorSpacePassthrough(e.NewValue);

        private void UpdateVSyncMode(object sender, ReactiveEventArgs<VSyncMode> e)
        {
            if (Device != null)
            {
                Device.VSyncMode = e.NewValue;
                Device.UpdateVSyncInterval();
            }

            _renderer?.Window?.ChangeVSyncMode(e.NewValue);
        }

        private void UpdateCustomVSyncIntervalValue(object sender, ReactiveEventArgs<int> e)
        {
            if (Device != null)
            {
                Device.TargetVSyncInterval = e.NewValue;
                Device.UpdateVSyncInterval();
            }
        }

        private void UpdateCustomVSyncIntervalEnabled(object sender, ReactiveEventArgs<bool> e)
        {
            if (Device != null)
            {
                Device.CustomVSyncIntervalEnabled = e.NewValue;
                Device.UpdateVSyncInterval();
            }
        }

        private void UpdateEnableInternetAccessState(object sender, ReactiveEventArgs<bool> e) => Device?.Configuration.EnableInternetAccess = e.NewValue;

        private void UpdateLanInterfaceIdState(object sender, ReactiveEventArgs<string> e) => Device?.Configuration.MultiplayerLanInterfaceId = e.NewValue;

        private void UpdateMultiplayerModeState(object sender, ReactiveEventArgs<MultiplayerMode> e) => Device?.Configuration.MultiplayerMode = e.NewValue;

        private void Shutdown()
        {
            _isActive = false;
            _inputReady = false;

            UnsubscribeFromConfiguration();

            _gpuCancellation.Cancel();
            _playTimer.Stop();

            GCSettings.LatencyMode = GCLatencyMode.Interactive;

            NativeApp.InputManager.KeyboardDriver.Clear();
            NativeApp.Mouse.Clear();

            Rainbow.Disable();
            Rainbow.Reset();

            DisplaySleep.Restore();

            if (Device?.Processes?.ActiveApplication != null)
            {
                ApplicationLibrary.LoadAndSaveMetaData(Device.Processes.ActiveApplication.ProgramIdText, metadata => metadata.UpdatePostGame(_playTimer.Elapsed));
            }

            NpadManager?.Dispose();
            TouchScreenManager?.Dispose();

            if (Device != null)
            {
                Device.Dispose();

                // The render loop stays alive until the renderer is disposed, as it may handle resource disposal.
                // Only wait for the commands submitted by the GPU loop.
                if (_renderThread != null)
                {
                    _gpuDoneEvent.WaitOne(TimeSpan.FromSeconds(10));
                }

                if (_renderingStarted)
                {
                    Device.Gpu.WaitUntilGpuReady();
                }

                Device.DisposeGpu();

                _renderThread?.Join(TimeSpan.FromSeconds(10));
            }

            if (_layerOwned)
            {
                // The renderer never took the layer; release the reference the UI handed over.
                new CAMetalLayer(_layer).Dispose();
                _layerOwned = false;
            }

            _gpuDoneEvent.Dispose();
            _gpuCancellation.Dispose();
        }
    }
}
