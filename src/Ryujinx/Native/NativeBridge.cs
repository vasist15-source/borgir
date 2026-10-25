using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ryujinx.Ava.Native
{
    /// <summary>
    /// The managed end of the bridge: the functions the native UI calls, and helpers to send it events and
    /// questions. Messages are JSON objects; see README.md in this folder.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal static unsafe class NativeBridge
    {
        private static CoreCallbacks* _callbacks;

        public static CoreCallbacks* Callbacks
        {
            get
            {
                if (_callbacks == null)
                {
                    _callbacks = (CoreCallbacks*)NativeMemory.AllocZeroed((nuint)sizeof(CoreCallbacks));
                    _callbacks->Version = CoreCallbacks.CurrentVersion;
                    _callbacks->Command = &Command;
                    _callbacks->FreeString = &FreeString;
                    _callbacks->KeyEvent = &KeyEvent;
                    _callbacks->MouseEvent = &MouseEvent;
                    _callbacks->ViewResized = &ViewResized;
                    _callbacks->FocusChanged = &FocusChanged;
                }

                return _callbacks;
            }
        }

        public static JsonObject Ok() => new() { ["ok"] = true };

        public static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

        /// <summary>
        /// Sends an event to the UI. Safe to call from any thread.
        /// </summary>
        public static void Post(string name, JsonObject payload = null)
        {
            payload ??= [];
            payload["event"] = name;

            string json;

            try
            {
                json = payload.ToJsonString();
            }
            catch (ArgumentException ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Could not encode the {name} event: {ex.Message}");
                return;
            }

            NativeUI.PostEvent(json);
        }

        /// <summary>
        /// Shows the player a dialog and waits for the answer. Call from an emulation thread, never the main thread.
        /// </summary>
        public static JsonObject Request(JsonObject request)
        {
            try
            {
                string reply = NativeUI.Request(request.ToJsonString());

                return reply == null ? null : JsonNode.Parse(reply) as JsonObject;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Native UI request failed: {ex}");

                return null;
            }
        }

        [UnmanagedCallersOnly]
        private static byte* Command(byte* json)
        {
            JsonObject reply;

            try
            {
                string text = Marshal.PtrToStringUTF8((nint)json) ?? "{}";

                if (JsonNode.Parse(text) is not JsonObject request)
                {
                    reply = Error("Malformed command.");
                }
                else
                {
                    string name = request["cmd"]?.GetValue<string>() ?? string.Empty;
                    long start = Stopwatch.GetTimestamp();

                    reply = NativeApp.HandleCommand(name, request) ?? Ok();

                    // The interface mostly calls in from its main thread, where a slow command shows up as a stutter.
                    TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

                    if (elapsed.TotalMilliseconds > 16)
                    {
                        Logger.Info?.Print(LogClass.Application, $"Native UI command {name} took {elapsed.TotalMilliseconds:F0} ms");
                    }
                }
            }
            catch (JsonException ex)
            {
                reply = Error($"Malformed command: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Native UI command failed: {ex}");

                reply = Error(ex.Message);
            }

            return NativeUI.AllocUtf8(reply.ToJsonString());
        }

        [UnmanagedCallersOnly]
        private static void FreeString(byte* text)
        {
            NativeMemory.Free(text);
        }

        [UnmanagedCallersOnly]
        private static void KeyEvent(ushort keyCode, byte pressed)
        {
            try
            {
                NativeApp.Keyboard?.OnKey(keyCode, pressed != 0);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Keyboard event failed: {ex}");
            }
        }

        [UnmanagedCallersOnly]
        private static void MouseEvent(int kind, double x, double y, int button)
        {
            try
            {
                NativeApp.Mouse?.OnMouse((NativeMouseEvent)kind, x, y, button);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Mouse event failed: {ex}");
            }
        }

        [UnmanagedCallersOnly]
        private static void ViewResized(double width, double height, double scale)
        {
            try
            {
                NativeApp.OnViewResized(width, height, scale);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Resize failed: {ex}");
            }
        }

        [UnmanagedCallersOnly]
        private static void FocusChanged(byte focused)
        {
            try
            {
                NativeApp.OnFocusChanged(focused != 0);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Focus change failed: {ex}");
            }
        }
    }
}
