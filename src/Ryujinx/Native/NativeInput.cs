using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Keyboard;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.Input;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using System.Threading;
using Key = Ryujinx.Input.Key;

namespace Ryujinx.Ava.Native
{
    /// <summary>
    /// Keyboard input from the native game view. macOS key codes name physical key positions, which is what
    /// Ryujinx's physical keyboard mode expects; semantic mode sees the same keys as a US layout.
    /// </summary>
    internal sealed class NativeKeyboardDriver : IKeyboardModeDriver
    {
        private static readonly string[] _keyboardIds = ["0"];

        private readonly bool[] _pressed = new bool[(int)Key.Count];
        private readonly Queue<Key> _pressedQueue = new();
        private readonly Lock _lock = new();

        public string DriverName => "NativeKeyboardDriver";
        public ReadOnlySpan<string> GamepadsIds => _keyboardIds;

        public event Action<string> OnGamepadConnected
        {
            add { }
            remove { }
        }

        public event Action<string> OnGamepadDisconnected
        {
            add { }
            remove { }
        }

        public void OnKey(ushort macKeyCode, bool pressed)
        {
            Key key = NativeKeyMap.FromMacKeyCode(macKeyCode);

            if (key == Key.Unknown)
            {
                return;
            }

            lock (_lock)
            {
                if (pressed && !_pressed[(int)key])
                {
                    _pressedQueue.Enqueue(key);
                }

                _pressed[(int)key] = pressed;
            }
        }

        public bool IsPressed(Key key)
        {
            if (key is Key.Unknown or Key.Unbound || (uint)key >= (uint)Key.Count)
            {
                return false;
            }

            lock (_lock)
            {
                return _pressed[(int)key];
            }
        }

        public bool TryConsumePressedKey(out Key key)
        {
            lock (_lock)
            {
                return _pressedQueue.TryDequeue(out key);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_pressed);
                _pressedQueue.Clear();
            }
        }

        public IGamepad GetGamepad(string id) => GetKeyboard(id, KeyboardInputMode.Semantic);

        public IKeyboard GetKeyboard(string id, KeyboardInputMode mode)
        {
            return _keyboardIds[0].Equals(id) ? new NativeKeyboard(this, id) : null;
        }

        public IEnumerable<IGamepad> GetGamepads() => [GetGamepad("0")];

        public void Dispose() { }
    }

    internal sealed class NativeKeyboard : IKeyboard
    {
        private readonly NativeKeyboardDriver _driver;
        private readonly List<KeyboardInputMappingHelper.KeyboardButtonMapping> _buttonsUserMapping = [];
        private readonly Lock _userMappingLock = new();
        private StandardKeyboardInputConfig _configuration;

        public string Id { get; }
        public string Name => "Keyboard";
        public bool IsConnected => true;
        public GamepadFeaturesFlag Features => GamepadFeaturesFlag.None;

        public NativeKeyboard(NativeKeyboardDriver driver, string id)
        {
            _driver = driver;
            Id = id;
        }

        public bool IsPressed(Key key) => _driver.IsPressed(key);

        public bool TryConsumePressedKey(out Key key) => _driver.TryConsumePressedKey(out key);

        public KeyboardStateSnapshot GetKeyboardStateSnapshot() => IKeyboard.GetStateSnapshot(this);

        public GamepadStateSnapshot GetMappedStateSnapshot()
        {
            KeyboardStateSnapshot rawState = GetKeyboardStateSnapshot();
            GamepadStateSnapshot result = default;

            lock (_userMappingLock)
            {
                if (_configuration == null)
                {
                    return result;
                }

                foreach (KeyboardInputMappingHelper.KeyboardButtonMapping entry in _buttonsUserMapping)
                {
                    if (!entry.IsValid || result.IsPressed(entry.To))
                    {
                        continue;
                    }

                    result.SetPressed(entry.To, rawState.IsPressed(entry.From));
                }

                (short leftStickX, short leftStickY) = KeyboardInputMappingHelper.GetStickValues(ref rawState, _configuration.LeftJoyconStick);
                (short rightStickX, short rightStickY) = KeyboardInputMappingHelper.GetStickValues(ref rawState, _configuration.RightJoyconStick);

                result.SetStick(StickInputId.Left, ConvertRawStickValue(leftStickX), ConvertRawStickValue(leftStickY));
                result.SetStick(StickInputId.Right, ConvertRawStickValue(rightStickX), ConvertRawStickValue(rightStickY));
            }

            return result;
        }

        public void SetConfiguration(InputConfig configuration)
        {
            lock (_userMappingLock)
            {
                _configuration = (StandardKeyboardInputConfig)configuration;

                _buttonsUserMapping.Clear();
                _buttonsUserMapping.AddRange(KeyboardInputMappingHelper.BuildButtonMappings(_configuration));
            }
        }

        public GamepadStateSnapshot GetStateSnapshot() => throw new NotSupportedException();

        public (float, float) GetStick(StickInputId inputId) => throw new NotSupportedException();

        public bool IsPressed(GamepadButtonInputId inputId) => throw new NotSupportedException();

        public void SetLed(uint packedRgb) { }

        public bool HDRumble(VibrationValue left, VibrationValue right) => false;

        public void SetTriggerThreshold(float triggerThreshold) { }

        public bool Rumble(float lowFrequency, float highFrequency, uint durationMs) => false;

        public Vector3 GetMotionData(MotionInputId inputId) => Vector3.Zero;

        public void Clear() => _driver.Clear();

        public void Dispose() { }

        private static float ConvertRawStickValue(short value)
        {
            const float ConvertRate = 1.0f / (short.MaxValue + 0.5f);

            return value * ConvertRate;
        }
    }

    /// <summary>
    /// Mouse event kinds; match PX_MOUSE_* in PXBridge.h.
    /// </summary>
    internal enum NativeMouseEvent
    {
        Move = 0,
        Down = 1,
        Up = 2,
        Scroll = 3,
    }

    /// <summary>
    /// Mouse input from the native game view, in points from its top-left corner. Drives the Switch touch
    /// screen, and the Switch mouse when that is enabled.
    /// </summary>
    internal sealed class NativeMouseDriver : IGamepadDriver
    {
        private const long ScrollResetTicks = TimeSpan.TicksPerMillisecond * 50;

        private Size _clientSize;
        private long _lastScroll;

        public bool[] PressedButtons { get; } = new bool[(int)MouseButton.Count];
        public Vector2 CurrentPosition { get; private set; }

        public Vector2 Scroll
        {
            get => DateTime.UtcNow.Ticks - _lastScroll > ScrollResetTicks ? Vector2.Zero : field;
            private set;
        }

        public string DriverName => "NativeMouseDriver";
        public ReadOnlySpan<string> GamepadsIds => new[] { "0" };

        public event Action<string> OnGamepadConnected
        {
            add { }
            remove { }
        }

        public event Action<string> OnGamepadDisconnected
        {
            add { }
            remove { }
        }

        public void OnMouse(NativeMouseEvent kind, double x, double y, int button)
        {
            switch (kind)
            {
                case NativeMouseEvent.Move:
                    CurrentPosition = new Vector2((float)x, (float)y);
                    break;
                case NativeMouseEvent.Down:
                case NativeMouseEvent.Up:
                    CurrentPosition = new Vector2((float)x, (float)y);

                    if ((uint)button < (uint)PressedButtons.Length)
                    {
                        PressedButtons[button] = kind == NativeMouseEvent.Down;
                    }

                    break;
                case NativeMouseEvent.Scroll:
                    Scroll = new Vector2((float)x, (float)y);
                    _lastScroll = DateTime.UtcNow.Ticks;
                    break;
            }
        }

        /// <summary>
        /// The game view's size in points, which mouse positions are relative to.
        /// </summary>
        public void SetClientSize(int width, int height)
        {
            _clientSize = new Size(width, height);
        }

        public Size GetClientSize() => _clientSize;

        public bool IsButtonPressed(MouseButton button)
        {
            return (uint)button < (uint)PressedButtons.Length && PressedButtons[(int)button];
        }

        public void Clear()
        {
            Array.Clear(PressedButtons);
        }

        public IGamepad GetGamepad(string id) => new NativeMouse(this);

        public IEnumerable<IGamepad> GetGamepads() => [GetGamepad("0")];

        public void Dispose() { }
    }

    internal sealed class NativeMouse : IMouse
    {
        private readonly NativeMouseDriver _driver;

        public NativeMouse(NativeMouseDriver driver)
        {
            _driver = driver;
        }

        public string Id => "0";
        public string Name => "Mouse";
        public bool IsConnected => true;
        public GamepadFeaturesFlag Features => GamepadFeaturesFlag.None;
        public bool[] Buttons => _driver.PressedButtons;
        public Size ClientSize => _driver.GetClientSize();

        public Vector2 GetPosition() => _driver.CurrentPosition;

        public Vector2 GetScroll() => _driver.Scroll;

        public bool IsButtonPressed(MouseButton button) => _driver.IsButtonPressed(button);

        public GamepadStateSnapshot GetMappedStateSnapshot() => throw new NotSupportedException();

        public GamepadStateSnapshot GetStateSnapshot() => throw new NotSupportedException();

        public Vector3 GetMotionData(MotionInputId inputId) => Vector3.Zero;

        public (float, float) GetStick(StickInputId inputId) => throw new NotSupportedException();

        public bool IsPressed(GamepadButtonInputId inputId) => throw new NotSupportedException();

        public bool HDRumble(VibrationValue left, VibrationValue right) => false;

        public bool Rumble(float lowFrequency, float highFrequency, uint durationMs) => false;

        public void SetConfiguration(InputConfig configuration) { }

        public void SetLed(uint packedRgb)
        {
            Logger.Debug?.Print(LogClass.UI, "SetLed called on the native mouse");
        }

        public void SetTriggerThreshold(float triggerThreshold) { }

        public void Dispose() { }
    }

    /// <summary>
    /// macOS virtual key codes (kVK_* in Carbon's Events.h) to Ryujinx keys, by physical position.
    /// </summary>
    internal static class NativeKeyMap
    {
        private static readonly Key[] _map = Build();

        public static Key FromMacKeyCode(ushort keyCode)
        {
            return keyCode < _map.Length ? _map[keyCode] : Key.Unknown;
        }

        private static Key[] Build()
        {
            Key[] map = new Key[0x80];

            void Set(int code, Key key) => map[code] = key;

            Set(0x00, Key.A);
            Set(0x01, Key.S);
            Set(0x02, Key.D);
            Set(0x03, Key.F);
            Set(0x04, Key.H);
            Set(0x05, Key.G);
            Set(0x06, Key.Z);
            Set(0x07, Key.X);
            Set(0x08, Key.C);
            Set(0x09, Key.V);
            Set(0x0A, Key.Grave); // ISO section key, next to left shift
            Set(0x0B, Key.B);
            Set(0x0C, Key.Q);
            Set(0x0D, Key.W);
            Set(0x0E, Key.E);
            Set(0x0F, Key.R);
            Set(0x10, Key.Y);
            Set(0x11, Key.T);
            Set(0x12, Key.Number1);
            Set(0x13, Key.Number2);
            Set(0x14, Key.Number3);
            Set(0x15, Key.Number4);
            Set(0x16, Key.Number6);
            Set(0x17, Key.Number5);
            Set(0x18, Key.Plus); // =
            Set(0x19, Key.Number9);
            Set(0x1A, Key.Number7);
            Set(0x1B, Key.Minus);
            Set(0x1C, Key.Number8);
            Set(0x1D, Key.Number0);
            Set(0x1E, Key.BracketRight);
            Set(0x1F, Key.O);
            Set(0x20, Key.U);
            Set(0x21, Key.BracketLeft);
            Set(0x22, Key.I);
            Set(0x23, Key.P);
            Set(0x24, Key.Enter);
            Set(0x25, Key.L);
            Set(0x26, Key.J);
            Set(0x27, Key.Quote);
            Set(0x28, Key.K);
            Set(0x29, Key.Semicolon);
            Set(0x2A, Key.BackSlash);
            Set(0x2B, Key.Comma);
            Set(0x2C, Key.Slash);
            Set(0x2D, Key.N);
            Set(0x2E, Key.M);
            Set(0x2F, Key.Period);
            Set(0x30, Key.Tab);
            Set(0x31, Key.Space);
            Set(0x32, Key.Tilde); // `
            Set(0x33, Key.BackSpace);
            Set(0x35, Key.Escape);
            Set(0x36, Key.WinRight);
            Set(0x37, Key.WinLeft);
            Set(0x38, Key.ShiftLeft);
            Set(0x39, Key.CapsLock);
            Set(0x3A, Key.AltLeft);
            Set(0x3B, Key.ControlLeft);
            Set(0x3C, Key.ShiftRight);
            Set(0x3D, Key.AltRight);
            Set(0x3E, Key.ControlRight);
            Set(0x40, Key.F17);
            Set(0x41, Key.KeypadDecimal);
            Set(0x43, Key.KeypadMultiply);
            Set(0x45, Key.KeypadAdd);
            Set(0x47, Key.NumLock); // Clear sits where Num Lock does
            Set(0x4B, Key.KeypadDivide);
            Set(0x4C, Key.KeypadEnter);
            Set(0x4E, Key.KeypadSubtract);
            Set(0x4F, Key.F18);
            Set(0x50, Key.F19);
            Set(0x52, Key.Keypad0);
            Set(0x53, Key.Keypad1);
            Set(0x54, Key.Keypad2);
            Set(0x55, Key.Keypad3);
            Set(0x56, Key.Keypad4);
            Set(0x57, Key.Keypad5);
            Set(0x58, Key.Keypad6);
            Set(0x59, Key.Keypad7);
            Set(0x5A, Key.F20);
            Set(0x5B, Key.Keypad8);
            Set(0x5C, Key.Keypad9);
            Set(0x60, Key.F5);
            Set(0x61, Key.F6);
            Set(0x62, Key.F7);
            Set(0x63, Key.F3);
            Set(0x64, Key.F8);
            Set(0x65, Key.F9);
            Set(0x67, Key.F11);
            Set(0x69, Key.F13);
            Set(0x6A, Key.F16);
            Set(0x6B, Key.F14);
            Set(0x6D, Key.F10);
            Set(0x6F, Key.F12);
            Set(0x71, Key.F15);
            Set(0x72, Key.Insert); // Help
            Set(0x73, Key.Home);
            Set(0x74, Key.PageUp);
            Set(0x75, Key.Delete);
            Set(0x76, Key.F4);
            Set(0x77, Key.End);
            Set(0x78, Key.F2);
            Set(0x79, Key.PageDown);
            Set(0x7A, Key.F1);
            Set(0x7B, Key.Left);
            Set(0x7C, Key.Right);
            Set(0x7D, Key.Down);
            Set(0x7E, Key.Up);

            return map;
        }
    }
}
