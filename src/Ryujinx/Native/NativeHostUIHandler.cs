using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Applets;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService.ApplicationProxy.Types;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.UI;
using Ryujinx.Headless;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace Ryujinx.Ava.Native
{
    /// <summary>
    /// Shows the dialogs a running game asks for (the Switch's software keyboard, error and controller applets)
    /// as native alerts. The emulator calls these on its own threads and waits for the player.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal sealed class NativeHostUIHandler : IHostUIHandler
    {
        private readonly NativeHost _host;

        public NativeHostUIHandler(NativeHost host)
        {
            _host = host;
        }

        public IHostUITheme HostUITheme { get; } = new HeadlessHostUiTheme();

        public bool DisplayInputDialog(SoftwareKeyboardUIArgs args, out string userText)
        {
            _host.NpadManager?.BlockInputUpdates();

            JsonObject reply = NativeBridge.Request(new JsonObject
            {
                ["kind"] = "input",
                ["header"] = args.HeaderText ?? string.Empty,
                ["sub"] = args.SubtitleText ?? string.Empty,
                ["guide"] = args.GuideText ?? string.Empty,
                ["initial"] = args.InitialText ?? string.Empty,
                ["submit"] = string.IsNullOrEmpty(args.SubmitText) ? "OK" : args.SubmitText,
                ["minLength"] = args.StringLengthMin,
                ["maxLength"] = args.StringLengthMax,
            });

            _host.NpadManager?.UnblockInputUpdates();

            if (reply == null)
            {
                // Like upstream Ryujinx: an internal error returns true with no text.
                userText = null;
                return true;
            }

            bool ok = reply["ok"]?.GetValue<bool>() ?? false;

            userText = ok ? reply["text"]?.GetValue<string>() ?? string.Empty : args.InitialText ?? string.Empty;

            return ok;
        }

        public bool DisplayMessageDialog(string title, string message)
        {
            JsonObject reply = NativeBridge.Request(new JsonObject
            {
                ["kind"] = "message",
                ["title"] = title ?? string.Empty,
                ["message"] = message ?? string.Empty,
            });

            return reply?["ok"]?.GetValue<bool>() ?? false;
        }

        public bool DisplayMessageDialog(ControllerAppletUIArgs args)
        {
            if (ConfigurationState.Instance.System.IgnoreControllerApplet)
            {
                return false;
            }

            JsonArray styles = [];

            foreach (ControllerType style in new[] { ControllerType.ProController, ControllerType.JoyconPair, ControllerType.JoyconLeft, ControllerType.JoyconRight, ControllerType.Handheld })
            {
                if ((args.SupportedStyles & style) != 0)
                {
                    styles.Add((JsonNode)style.ToString());
                }
            }

            JsonArray players = [];

            foreach (PlayerIndex player in args.SupportedPlayers ?? [])
            {
                players.Add((JsonNode)player.ToString());
            }

            // The interface lets the player change controllers while this waits, then answers true to have the game
            // check them again, or false to go on as they are.
            JsonObject reply = NativeBridge.Request(new JsonObject
            {
                ["kind"] = "controllers",
                ["min"] = args.PlayerCountMin,
                ["max"] = args.PlayerCountMax,
                ["styles"] = styles,
                ["players"] = players,
                ["docked"] = args.IsDocked,
            });

            return reply?["ok"]?.GetValue<bool>() ?? false;
        }

        public bool DisplayCabinetDialog(out string userText)
        {
            JsonObject reply = NativeBridge.Request(new JsonObject
            {
                ["kind"] = "input",
                ["header"] = "Rename Amiibo",
                ["initial"] = "My Amiibo",
                ["minLength"] = 1,
                ["maxLength"] = 25,
            });

            bool ok = reply?["ok"]?.GetValue<bool>() ?? false;

            userText = ok ? reply["text"]?.GetValue<string>() ?? "My Amiibo" : "My Amiibo";

            return ok;
        }

        public void DisplayCabinetMessageDialog()
        {
            NativeBridge.Request(new JsonObject
            {
                ["kind"] = "message",
                ["title"] = "Scan Your Amiibo",
                ["message"] = "Scan your Amiibo now.",
            });
        }

        public void ExecuteProgram(Switch device, ProgramSpecifyKind kind, ulong value)
        {
            device.Configuration.UserChannelPersistence.ExecuteProgram(kind, value);

            _host.Stop();
        }

        public bool DisplayErrorAppletDialog(string title, string message, string[] buttonsText, (uint Module, uint Description)? errorCode = null)
        {
            JsonArray buttons = [];

            foreach (string button in buttonsText ?? [])
            {
                buttons.Add((JsonNode)button);
            }

            JsonObject reply = NativeBridge.Request(new JsonObject
            {
                ["kind"] = "error",
                ["title"] = title ?? "Error",
                ["message"] = message ?? string.Empty,
                ["buttons"] = buttons,
            });

            int pressed = reply?["button"]?.GetValue<int>() ?? -1;

            // True asks the game to show its error details: any button but the last one.
            return buttonsText is { Length: > 1 } && pressed >= 0 && pressed != buttonsText.Length - 1;
        }

        public IDynamicTextInputHandler CreateDynamicTextInputHandler() => new HeadlessDynamicTextInputHandler();

        public UserProfile ShowPlayerSelectDialog() => AccountSaveDataManager.GetLastUsedUser();

        public void TakeScreenshot() => _host.RequestScreenshot();
    }
}
