using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Configuration.Hid.Controller.Motion;
using Ryujinx.Common.Configuration.Hid.Keyboard;
using Ryujinx.Common.Logging;
using Ryujinx.Input;
using Ryujinx.Input.Assigner;
using Ryujinx.Input.SDL3;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Threading;
using ConfigGamepadInputId = Ryujinx.Common.Configuration.Hid.Controller.GamepadInputId;
using ConfigStickInputId = Ryujinx.Common.Configuration.Hid.Controller.StickInputId;
using Key = Ryujinx.Input.Key;
using PlayerIndex = Ryujinx.Common.Configuration.Hid.PlayerIndex;

namespace Ryujinx.Ava.Native
{
    /// <summary>
    /// Settings › Input: which device each player uses, as which controller type, and its button layout. Layouts are
    /// Ryujinx's own input configs, so they carry over to and from other Ryujinx builds. Changes reach a running game
    /// at once.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal static class NativeInputSettings
    {
        private const string KeyboardId = "0";

        private static readonly PlayerIndex[] _players =
        [
            PlayerIndex.Player1, PlayerIndex.Player2, PlayerIndex.Player3, PlayerIndex.Player4,
            PlayerIndex.Player5, PlayerIndex.Player6, PlayerIndex.Player7, PlayerIndex.Player8, PlayerIndex.Handheld,
        ];

        private static readonly ControllerType[] _playerTypes = [ControllerType.ProController, ControllerType.JoyconPair, ControllerType.JoyconLeft, ControllerType.JoyconRight];

        /// <summary>The buttons every layout has, by the names the interface uses.</summary>
        private static readonly string[] _buttons =
        [
            "ButtonZl", "ButtonL", "ButtonMinus", "DpadUp", "DpadDown", "DpadLeft", "DpadRight", "LeftStickButton", "LeftSl", "LeftSr",
            "ButtonZr", "ButtonR", "ButtonPlus", "ButtonA", "ButtonB", "ButtonX", "ButtonY", "RightStickButton", "RightSl", "RightSr",
        ];

        /// <summary>A keyboard moves each stick with four keys.</summary>
        private static readonly string[] _keyboardStickKeys =
        [
            "LeftStickUp", "LeftStickDown", "LeftStickLeft", "LeftStickRight",
            "RightStickUp", "RightStickDown", "RightStickLeft", "RightStickRight",
        ];

        private static int _listenCancelled;

        private static List<InputConfig> Configs => ConfigurationState.Instance.Hid.InputConfig.Value;

        public static JsonObject Get()
        {
            Dictionary<string, string> connected = new() { [KeyboardId] = "Keyboard" };
            JsonArray devices = [new JsonObject { ["id"] = KeyboardId, ["name"] = "Keyboard", ["kind"] = "keyboard" }];

            foreach (string id in NativeApp.InputManager.GamepadDriver.GamepadsIds.ToArray())
            {
                using IGamepad gamepad = NativeApp.InputManager.GamepadDriver.GetGamepad(id);

                if (gamepad != null)
                {
                    connected[id] = gamepad.Name;
                    devices.Add((JsonNode)new JsonObject { ["id"] = id, ["name"] = gamepad.Name, ["kind"] = "controller" });
                }
            }

            JsonArray players = [];

            foreach (PlayerIndex player in _players)
            {
                InputConfig config = Find(player);
                JsonArray types = [];

                foreach (ControllerType type in player == PlayerIndex.Handheld ? new[] { ControllerType.Handheld } : _playerTypes)
                {
                    types.Add((JsonNode)type.ToString());
                }

                JsonObject entry = new()
                {
                    ["player"] = player.ToString(),
                    ["types"] = types,
                };

                if (config != null)
                {
                    entry["device"] = config.Id;
                    entry["name"] = connected.GetValueOrDefault(config.Id ?? string.Empty, config.Name);
                    entry["kind"] = config is StandardKeyboardInputConfig ? "keyboard" : "controller";
                    entry["connected"] = connected.ContainsKey(config.Id ?? string.Empty);
                    entry["type"] = config.ControllerType.ToString();
                    entry["bindings"] = Bindings(config);

                    if (config is StandardControllerInputConfig controller)
                    {
                        entry["options"] = new JsonObject
                        {
                            ["deadzoneLeft"] = controller.DeadzoneLeft,
                            ["deadzoneRight"] = controller.DeadzoneRight,
                            ["invertLeftY"] = controller.LeftJoyconStick?.InvertStickY ?? false,
                            ["invertRightY"] = controller.RightJoyconStick?.InvertStickY ?? false,
                            ["rumble"] = controller.Rumble?.EnableRumble ?? false,
                            ["motion"] = controller.Motion?.EnableMotion ?? false,
                        };
                    }
                }

                players.Add((JsonNode)entry);
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["devices"] = devices,
                ["players"] = players,
                ["enableKeyboard"] = ConfigurationState.Instance.Hid.EnableKeyboard.Value,
                ["enableMouse"] = ConfigurationState.Instance.Hid.EnableMouse.Value,
            };
        }

        /// <summary>
        /// Gives a player a device with its default layout, or (with an empty id) removes the player.
        /// </summary>
        public static JsonObject SetDevice(string playerName, string id)
        {
            if (!TryParsePlayer(playerName, out PlayerIndex player, out JsonObject error))
            {
                return error;
            }

            InputConfig current = Find(player);

            if (current != null && current.Id == id)
            {
                return NativeBridge.Ok();
            }

            List<InputConfig> configs = [.. Configs];
            configs.RemoveAll(config => config.PlayerIndex == player);

            if (!string.IsNullOrEmpty(id))
            {
                ControllerType type = player == PlayerIndex.Handheld
                    ? ControllerType.Handheld
                    : current != null && _playerTypes.Contains(current.ControllerType) ? current.ControllerType : ControllerType.ProController;

                if (id == KeyboardId)
                {
                    configs.Add(InputConfigDefaults.CreateDefaultKeyboardConfiguration(id, "Keyboard", type, player));
                }
                else
                {
                    using IGamepad gamepad = NativeApp.InputManager.GamepadDriver.GetGamepad(id);

                    if (gamepad == null)
                    {
                        return NativeBridge.Error("That controller is no longer connected.");
                    }

                    bool isNintendoStyle = gamepad is SDL3Gamepad sdlGamepad
                        ? sdlGamepad.VendorId == 0x057E
                        : gamepad.Name.Contains("Nintendo", StringComparison.OrdinalIgnoreCase);

                    configs.Add(InputConfigDefaults.CreateDefaultControllerConfiguration(id, gamepad.Name, type, player, isNintendoStyle));
                }
            }

            // A per-player device assignment would route input from the old device.
            List<PlayerInputAssignment> assignments = [.. ConfigurationState.Instance.Hid.PlayerInputAssignments.Value];

            if (assignments.RemoveAll(assignment => assignment.PlayerIndex == player) > 0)
            {
                ConfigurationState.Instance.Hid.PlayerInputAssignments.Value = assignments;
            }

            Save(configs.OrderBy(config => config.PlayerIndex).ToList());

            return NativeBridge.Ok();
        }

        public static JsonObject SetType(string playerName, string typeName)
        {
            if (!TryFindConfig(playerName, out InputConfig config, out JsonObject error))
            {
                return error;
            }

            if (!Enum.TryParse(typeName, out ControllerType type) ||
                (config.PlayerIndex == PlayerIndex.Handheld ? type != ControllerType.Handheld : !_playerTypes.Contains(type)))
            {
                return NativeBridge.Error($"Player {playerName} can't be a {typeName}.");
            }

            config.ControllerType = type;
            Save([.. Configs]);

            return NativeBridge.Ok();
        }

        /// <summary>
        /// Binds one button. Keyboards take a macOS key code (macKeyCode), controllers a button or stick name (value);
        /// an empty value unbinds.
        /// </summary>
        public static JsonObject SetBinding(string playerName, string button, JsonObject args)
        {
            if (!TryFindConfig(playerName, out InputConfig config, out JsonObject error))
            {
                return error;
            }

            string value = args["value"]?.GetValue<string>();

            try
            {
                switch (config)
                {
                    case StandardKeyboardInputConfig keyboard:
                        PhysicalKey key = PhysicalKey.Unbound;

                        if (args["macKeyCode"] is JsonNode keyCode)
                        {
                            Key pressed = NativeKeyMap.FromMacKeyCode((ushort)keyCode.GetValue<double>());

                            if (pressed == Key.Unknown)
                            {
                                return NativeBridge.Error("That key can't be used for a button.");
                            }

                            // Ryujinx.Input.Key and PhysicalKey list the same keys in the same order.
                            key = (PhysicalKey)(int)pressed;
                        }
                        else if (!string.IsNullOrEmpty(value))
                        {
                            key = Enum.Parse<PhysicalKey>(value);
                        }

                        SetKeyboardBinding(keyboard, button, key);
                        break;
                    case StandardControllerInputConfig controller:
                        SetControllerBinding(controller, button, value);
                        break;
                }
            }
            catch (ArgumentException ex)
            {
                return NativeBridge.Error(ex.Message);
            }

            Save([.. Configs]);

            return NativeBridge.Ok();
        }

        public static JsonObject SetOption(string playerName, string option, JsonNode value)
        {
            if (!TryFindConfig(playerName, out InputConfig config, out JsonObject error))
            {
                return error;
            }

            if (config is not StandardControllerInputConfig controller)
            {
                return NativeBridge.Error("Keyboards have no stick, rumble or motion settings.");
            }

            try
            {
                switch (option)
                {
                    case "deadzoneLeft":
                        controller.DeadzoneLeft = (float)Math.Clamp(NativeSettings.Number(value), 0, 0.9);
                        break;
                    case "deadzoneRight":
                        controller.DeadzoneRight = (float)Math.Clamp(NativeSettings.Number(value), 0, 0.9);
                        break;
                    case "invertLeftY":
                        controller.LeftJoyconStick ??= new JoyconConfigControllerStick<ConfigGamepadInputId, ConfigStickInputId>();
                        controller.LeftJoyconStick.InvertStickY = NativeSettings.Bool(value);
                        break;
                    case "invertRightY":
                        controller.RightJoyconStick ??= new JoyconConfigControllerStick<ConfigGamepadInputId, ConfigStickInputId>();
                        controller.RightJoyconStick.InvertStickY = NativeSettings.Bool(value);
                        break;
                    case "rumble":
                        controller.Rumble ??= new RumbleConfigController { StrongRumble = 1, WeakRumble = 1 };
                        controller.Rumble.EnableRumble = NativeSettings.Bool(value);
                        break;
                    case "motion":
                        controller.Motion ??= new StandardMotionConfigController { MotionBackend = MotionInputBackendType.GamepadDriver, Sensitivity = 100, GyroDeadzone = 1 };
                        controller.Motion.EnableMotion = NativeSettings.Bool(value);
                        break;
                    default:
                        return NativeBridge.Error($"Unknown controller setting '{option}'.");
                }
            }
            catch (ArgumentException ex)
            {
                return NativeBridge.Error(ex.Message);
            }

            Save([.. Configs]);

            return NativeBridge.Ok();
        }

        /// <summary>
        /// Puts back the device's default layout, keeping the player's device and controller type.
        /// </summary>
        public static JsonObject Reset(string playerName)
        {
            if (!TryFindConfig(playerName, out InputConfig config, out JsonObject error))
            {
                return error;
            }

            InputConfig layout;

            if (config is StandardKeyboardInputConfig)
            {
                layout = InputConfigDefaults.CreateDefaultKeyboardConfiguration(config.Id, config.Name, config.ControllerType, config.PlayerIndex);
            }
            else
            {
                using IGamepad gamepad = NativeApp.InputManager.GamepadDriver.GetGamepad(config.Id);

                bool isNintendoStyle = gamepad is SDL3Gamepad sdlGamepad
                    ? sdlGamepad.VendorId == 0x057E
                    : config.Name?.Contains("Nintendo", StringComparison.OrdinalIgnoreCase) == true;

                layout = InputConfigDefaults.CreateDefaultControllerConfiguration(config.Id, config.Name, config.ControllerType, config.PlayerIndex, isNintendoStyle);
            }

            List<InputConfig> configs = [.. Configs];
            configs[configs.IndexOf(config)] = layout;
            Save(configs);

            return NativeBridge.Ok();
        }

        /// <summary>
        /// Waits for a button on the player's controller and returns its name, for binding. Runs on the caller's
        /// thread, off the interface's main thread, which keeps pumping the controller's events meanwhile.
        /// </summary>
        public static JsonObject Listen(string playerName, double timeoutSeconds)
        {
            if (!TryFindConfig(playerName, out InputConfig config, out JsonObject error))
            {
                return error;
            }

            if (config is not StandardControllerInputConfig controller)
            {
                return NativeBridge.Error("Keyboard keys are read by the interface.");
            }

            using IGamepad gamepad = NativeApp.InputManager.GamepadDriver.GetGamepad(controller.Id);

            if (gamepad == null)
            {
                return NativeBridge.Error($"{controller.Name} is not connected.");
            }

            GamepadButtonAssigner assigner = new(gamepad, controller.TriggerThreshold > 0 ? controller.TriggerThreshold : 0.5f, forStick: false);
            assigner.Initialize();

            Interlocked.Exchange(ref _listenCancelled, 0);
            Stopwatch elapsed = Stopwatch.StartNew();

            // The press that binds a button shouldn't also reach a running game.
            NativeApp.WithRunningHost(host => host.NpadManager?.BlockInputUpdates());

            try
            {
                while (elapsed.Elapsed.TotalSeconds < timeoutSeconds && Volatile.Read(ref _listenCancelled) == 0)
                {
                    Thread.Sleep(10);
                    assigner.ReadInput();

                    if (assigner.ShouldCancel())
                    {
                        return NativeBridge.Error($"{controller.Name} was disconnected.");
                    }

                    if (assigner.IsAnyButtonPressed() && assigner.GetPressedButton() is { } button)
                    {
                        return new JsonObject { ["ok"] = true, ["value"] = button.AsHidType<ConfigGamepadInputId>().ToString() };
                    }
                }
            }
            finally
            {
                NativeApp.WithRunningHost(host => host.NpadManager?.UnblockInputUpdates());
            }

            return new JsonObject { ["ok"] = true, ["value"] = null };
        }

        public static JsonObject CancelListen()
        {
            Interlocked.Exchange(ref _listenCancelled, 1);

            return NativeBridge.Ok();
        }

        private static InputConfig Find(PlayerIndex player) => Configs.FirstOrDefault(config => config.PlayerIndex == player);

        private static void Save(List<InputConfig> configs)
        {
            ConfigurationState.Instance.Hid.InputConfig.Value = configs;
            NativeApp.SaveConfig();
            NativeApp.WithRunningHost(host => host.ReloadInput());
        }

        private static JsonObject Bindings(InputConfig config)
        {
            JsonObject bindings = new();

            switch (config)
            {
                case StandardKeyboardInputConfig keyboard:
                    foreach (string button in _buttons.Concat(_keyboardStickKeys))
                    {
                        bindings[button] = KeyboardBinding(keyboard, button).ToString();
                    }

                    break;
                case StandardControllerInputConfig controller:
                    foreach (string button in _buttons)
                    {
                        bindings[button] = ControllerButton(controller, button).ToString();
                    }

                    bindings["LeftStick"] = (controller.LeftJoyconStick?.Joystick ?? ConfigStickInputId.Unbound).ToString();
                    bindings["RightStick"] = (controller.RightJoyconStick?.Joystick ?? ConfigStickInputId.Unbound).ToString();
                    break;
            }

            return bindings;
        }

        private static PhysicalKey KeyboardBinding(StandardKeyboardInputConfig config, string button)
        {
            config.LeftJoyconStick ??= new JoyconConfigKeyboardStick<PhysicalKey>();
            config.RightJoyconStick ??= new JoyconConfigKeyboardStick<PhysicalKey>();

            return button switch
            {
                "LeftStickUp" => config.LeftJoyconStick.StickUp,
                "LeftStickDown" => config.LeftJoyconStick.StickDown,
                "LeftStickLeft" => config.LeftJoyconStick.StickLeft,
                "LeftStickRight" => config.LeftJoyconStick.StickRight,
                "LeftStickButton" => config.LeftJoyconStick.StickButton,
                "RightStickUp" => config.RightJoyconStick.StickUp,
                "RightStickDown" => config.RightJoyconStick.StickDown,
                "RightStickLeft" => config.RightJoyconStick.StickLeft,
                "RightStickRight" => config.RightJoyconStick.StickRight,
                "RightStickButton" => config.RightJoyconStick.StickButton,
                _ => CommonButton(config, button),
            };
        }

        private static void SetKeyboardBinding(StandardKeyboardInputConfig config, string button, PhysicalKey key)
        {
            config.LeftJoyconStick ??= new JoyconConfigKeyboardStick<PhysicalKey>();
            config.RightJoyconStick ??= new JoyconConfigKeyboardStick<PhysicalKey>();

            switch (button)
            {
                case "LeftStickUp": config.LeftJoyconStick.StickUp = key; break;
                case "LeftStickDown": config.LeftJoyconStick.StickDown = key; break;
                case "LeftStickLeft": config.LeftJoyconStick.StickLeft = key; break;
                case "LeftStickRight": config.LeftJoyconStick.StickRight = key; break;
                case "LeftStickButton": config.LeftJoyconStick.StickButton = key; break;
                case "RightStickUp": config.RightJoyconStick.StickUp = key; break;
                case "RightStickDown": config.RightJoyconStick.StickDown = key; break;
                case "RightStickLeft": config.RightJoyconStick.StickLeft = key; break;
                case "RightStickRight": config.RightJoyconStick.StickRight = key; break;
                case "RightStickButton": config.RightJoyconStick.StickButton = key; break;
                default: SetCommonButton(config, button, key); break;
            }
        }

        private static ConfigGamepadInputId ControllerButton(StandardControllerInputConfig config, string button)
        {
            return button switch
            {
                "LeftStickButton" => config.LeftJoyconStick?.StickButton ?? ConfigGamepadInputId.Unbound,
                "RightStickButton" => config.RightJoyconStick?.StickButton ?? ConfigGamepadInputId.Unbound,
                _ => CommonButton(config, button),
            };
        }

        private static void SetControllerBinding(StandardControllerInputConfig config, string button, string value)
        {
            config.LeftJoyconStick ??= new JoyconConfigControllerStick<ConfigGamepadInputId, ConfigStickInputId>();
            config.RightJoyconStick ??= new JoyconConfigControllerStick<ConfigGamepadInputId, ConfigStickInputId>();

            if (button is "LeftStick" or "RightStick")
            {
                ConfigStickInputId stick = string.IsNullOrEmpty(value) ? ConfigStickInputId.Unbound : Enum.Parse<ConfigStickInputId>(value);

                (button == "LeftStick" ? config.LeftJoyconStick : config.RightJoyconStick).Joystick = stick;
                return;
            }

            ConfigGamepadInputId id = string.IsNullOrEmpty(value) ? ConfigGamepadInputId.Unbound : Enum.Parse<ConfigGamepadInputId>(value);

            switch (button)
            {
                case "LeftStickButton": config.LeftJoyconStick.StickButton = id; break;
                case "RightStickButton": config.RightJoyconStick.StickButton = id; break;
                default: SetCommonButton(config, button, id); break;
            }
        }

        private static T CommonButton<T>(GenericInputConfigurationCommon<T> config, string button) where T : unmanaged
        {
            config.LeftJoycon ??= new LeftJoyconCommonConfig<T>();
            config.RightJoycon ??= new RightJoyconCommonConfig<T>();

            return button switch
            {
                "ButtonZl" => config.LeftJoycon.ButtonZl,
                "ButtonL" => config.LeftJoycon.ButtonL,
                "ButtonMinus" => config.LeftJoycon.ButtonMinus,
                "DpadUp" => config.LeftJoycon.DpadUp,
                "DpadDown" => config.LeftJoycon.DpadDown,
                "DpadLeft" => config.LeftJoycon.DpadLeft,
                "DpadRight" => config.LeftJoycon.DpadRight,
                "LeftSl" => config.LeftJoycon.ButtonSl,
                "LeftSr" => config.LeftJoycon.ButtonSr,
                "ButtonZr" => config.RightJoycon.ButtonZr,
                "ButtonR" => config.RightJoycon.ButtonR,
                "ButtonPlus" => config.RightJoycon.ButtonPlus,
                "ButtonA" => config.RightJoycon.ButtonA,
                "ButtonB" => config.RightJoycon.ButtonB,
                "ButtonX" => config.RightJoycon.ButtonX,
                "ButtonY" => config.RightJoycon.ButtonY,
                "RightSl" => config.RightJoycon.ButtonSl,
                "RightSr" => config.RightJoycon.ButtonSr,
                _ => throw new ArgumentException($"Unknown button '{button}'."),
            };
        }

        private static void SetCommonButton<T>(GenericInputConfigurationCommon<T> config, string button, T value) where T : unmanaged
        {
            config.LeftJoycon ??= new LeftJoyconCommonConfig<T>();
            config.RightJoycon ??= new RightJoyconCommonConfig<T>();

            switch (button)
            {
                case "ButtonZl": config.LeftJoycon.ButtonZl = value; break;
                case "ButtonL": config.LeftJoycon.ButtonL = value; break;
                case "ButtonMinus": config.LeftJoycon.ButtonMinus = value; break;
                case "DpadUp": config.LeftJoycon.DpadUp = value; break;
                case "DpadDown": config.LeftJoycon.DpadDown = value; break;
                case "DpadLeft": config.LeftJoycon.DpadLeft = value; break;
                case "DpadRight": config.LeftJoycon.DpadRight = value; break;
                case "LeftSl": config.LeftJoycon.ButtonSl = value; break;
                case "LeftSr": config.LeftJoycon.ButtonSr = value; break;
                case "ButtonZr": config.RightJoycon.ButtonZr = value; break;
                case "ButtonR": config.RightJoycon.ButtonR = value; break;
                case "ButtonPlus": config.RightJoycon.ButtonPlus = value; break;
                case "ButtonA": config.RightJoycon.ButtonA = value; break;
                case "ButtonB": config.RightJoycon.ButtonB = value; break;
                case "ButtonX": config.RightJoycon.ButtonX = value; break;
                case "ButtonY": config.RightJoycon.ButtonY = value; break;
                case "RightSl": config.RightJoycon.ButtonSl = value; break;
                case "RightSr": config.RightJoycon.ButtonSr = value; break;
                default: throw new ArgumentException($"Unknown button '{button}'.");
            }
        }

        private static bool TryParsePlayer(string name, out PlayerIndex player, out JsonObject error)
        {
            error = null;

            if (Enum.TryParse(name, out player) && _players.Contains(player))
            {
                return true;
            }

            error = NativeBridge.Error($"Unknown player '{name}'.");
            return false;
        }

        private static bool TryFindConfig(string playerName, out InputConfig config, out JsonObject error)
        {
            config = null;

            if (!TryParsePlayer(playerName, out PlayerIndex player, out error))
            {
                return false;
            }

            config = Find(player);

            if (config == null)
            {
                error = NativeBridge.Error($"{playerName} has no controller.");
                return false;
            }

            return true;
        }
    }
}
