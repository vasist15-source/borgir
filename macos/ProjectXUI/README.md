# ProjectXUI

The native macOS interface for Borgir: SwiftUI windows for the library, settings and the in-game overlay. It builds
into `libProjectXUI.dylib`, which the emulator loads at startup (see `src/Ryujinx/Native/README.md` for the bridge).

```bash
swift build -c release --package-path macos/ProjectXUI
```

`distribution/macos/build_borgir.sh` builds it along with the emulator and puts it in the app's
`Contents/Frameworks`.

| File | Contents |
|---|---|
| `Bridge.swift` | The exported `px_ui_*` functions, and `Core`, which calls the emulator. |
| `AppModel.swift` | App state, the running game session, settings bindings and core events. |
| `LibraryView.swift` | The game library. |
| `GameDetailView.swift` | The library's detail pane: Play, and the game's settings, update, DLC, mods and cheats. |
| `GameSettingsView.swift` | A game's own settings, and the note in Settings while a running game has some. |
| `MainWindow.swift` | Quitting when the main window closes, and the `PX_TEST` interface script. |
| `ExitHelpers.swift` | Ends the helper processes macOS started for the app as it exits, so the Dock lets go of it. |
| `GameScreen.swift` | The game's Metal view, its input forwarding and the loading screen. |
| `OverlayView.swift` | The in-game overlay. |
| `SettingsView.swift` | The Settings window. |
| `InputViews.swift` | Settings › Input: players, devices and button layouts; the prompt when a game asks for controllers. |
| `ProfileViews.swift` | Settings › Profiles, the firmware avatar picker and profile pictures (also in the toolbar). |
| `Dialogs.swift` | Dialogs a running game asks for (software keyboard, error and controller applets). |
| `Compat.swift` | Lets `@State` build without Xcode's macro plugins. |

It targets macOS 26 for the Liquid Glass APIs and builds with only the Command Line Tools.
