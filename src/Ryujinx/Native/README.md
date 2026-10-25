# Native interface bridge

Borgir's macOS interface is a SwiftUI library, `macos/ProjectXUI` (built into `libProjectXUI.dylib`). The emulator
starts as usual. Then `Program.Main` calls `NativeApp.Run`, which loads the library and gives the main thread to it
through `px_ui_run`. `--no-gui` runs a game in the headless SDL window instead.

The two sides talk through a small C interface, declared in `macos/ProjectXUI/Sources/PXBridge/include/PXBridge.h`:

| Direction | Call | Use |
|---|---|---|
| UI → core | `callbacks->command(json)` | Everything the UI asks for. Returns a JSON reply. |
| UI → core | `key_event`, `mouse_event`, `view_resized`, `focus_changed` | Game view input, without JSON. |
| core → UI | `px_ui_post_event(json)` | Notifications, from any thread. |
| core → UI | `px_ui_request(json)` | A dialog the player must answer. Blocks, so never call it on the main thread. |

Every message is a JSON object.

## Commands

The UI sends `{"cmd": "<name>", ...}`. Replies carry `"ok"`, plus `"error"` when it is false.

| Command | Arguments | Reply |
|---|---|---|
| `app.info` | | `version`, `dataDir`, `keysInstalled`, `firmwareVersion`, `profiles[]`, `activeProfile`, `gameDirs[]`, `autoloadDirs[]`, `launchPath` |
| `app.quit` | | Stops any game and saves settings. |
| `app.revealLogs` | | Shows the logs folder in Finder. |
| `library.refresh` | | Starts a scan; `library.progress` and `library.updated` follow. |
| `library.addDir` / `library.removeDir` | `path`, `kind` (`"autoload"` for update and DLC folders) | |
| `library.inspect` | `path` | `games[]` for one file. |
| `game.launch` | `path`, `id`, `layer` (hex pointer, retained), `width`, `height` (pixels), `scale` | The core releases the layer when the renderer is done with it. |
| `game.stop`, `game.pause`, `game.resume`, `game.toggleVSync`, `game.screenshot` | | |
| `game.revealFolder` | `id`, `folder` (`save`, `mods`, `cache`) | |
| `game.settings.get` | `id` | `own{}` (the game's own values), `global{}` (the values it would otherwise use) |
| `game.settings.set` | `id`, `key`, `value` (`null` to follow the global setting) | As `game.settings.get`. Applies at once to that game when it runs. |
| `content.get` | `id` | `updates[]`, `dlc[]`, `mods[]`, `cheats[]`, `ownSettings[]`, `modsDir` |
| `content.selectUpdate` | `id`, `path` (`""` for the base game) | `library.updated` follows. |
| `content.setDlc` | `id`, `path`, `contentPath`, `enabled` | |
| `content.setMod` | `id`, `path`, `enabled` | Written to `games/<id>/mods.json`. |
| `content.importMod` | `id`, `path` (a folder, or an archive bsdtar reads) | `imported[]`, `skipped[]` (with reasons). Each folder inside with a romfs, exefs or cheats folder is one mod; ones under another game's title id are skipped. Installs into `sdcard/atmosphere/contents/<id>`, as Ryujinx does. |
| `content.setCheat` | `id`, `key`, `enabled` | Written to `mods/contents/<id>/cheats/enabled.txt`, as upstream does; a running game rereads it. |
| `content.add` | `id`, `path` (an NSP or XCI) | `updates`, `dlc` (how many were added) |
| `content.remove` | `id`, `kind` (`update` or `dlc`), `path` | Forgets the file; it stays on disk. |
| `settings.get` | | `settings{}`, `options{}` (the choices for list settings), `ownSettings[]` (keys the running game has its own value for) |
| `settings.set` | `key`, `value` | `settings{}` |
| `keys.install` | `path` | |
| `firmware.verify` | `path` | `version` |
| `firmware.install` | `path` | Installs in the background; `firmware.installed` or `notice` follows. |
| `profiles.select` | `id` | |
| `profiles.add` | `name` | `id`. Starts with Ryujinx's default picture. |
| `profiles.rename` | `id`, `name` | At most 32 bytes of UTF-8, as on the Switch. |
| `profiles.setImage` | `id`, `image` (base64 JPEG, up to 128 KiB) | |
| `profiles.delete` | `id` | Deletes the profile's save data too. The default profile can't be deleted. |
| `profiles.avatars` | | `avatars[]`: the firmware's avatar pictures as cached 256-pixel PNGs. |

| `input.get` | | `devices[]`, `players[]` (each: `player`, `types[]`, and when it has one `device`, `name`, `kind`, `connected`, `type`, `bindings{}`, `options{}`), `enableKeyboard`, `enableMouse` |
| `input.setDevice` | `player` (`Player1`…`Player8`, `Handheld`), `id` (`""` removes the player) | The device's default layout. |
| `input.setType` | `player`, `type` (`ProController`, `JoyconPair`, `JoyconLeft`, `JoyconRight`; `Handheld`) | |
| `input.setBinding` | `player`, `button`, `macKeyCode` (keyboards) or `value` (controllers; `""` unbinds) | |
| `input.setOption` | `player`, `option` (`deadzoneLeft`, `deadzoneRight`, `invertLeftY`, `invertRightY`, `rumble`, `motion`), `value` | Controllers only. |
| `input.reset` | `player` | The device's default layout again. |
| `input.listen` | `player`, `timeout` (seconds) | `value`: the next controller button pressed, or `null`. Blocks; call it off the main thread. |
| `input.cancelListen` | | Ends a waiting `input.listen`. |

Profile changes are refused while a game runs. Input changes reach a running game at once.

`NativeSettings.cs` lists the setting keys; `NativeGameSettings.cs` the ones a game can have its own value for, which
are kept in `games/<id>/borgir-settings.json`.

## Events

`{"event": "<name>", ...}`

| Event | Fields |
|---|---|
| `library.progress` | `loaded`, `found` |
| `library.updated` | `games[]`, `loading` |
| `firmware.installed`, `notice` | `title`, `message` |
| `game.started` | `title`, `titleId` |
| `game.loading` | `stage`, `current`, `total` |
| `game.firstFrame` | |
| `game.status` | `fps`, `frameTime`, `fifo`, `vsync`, `docked`, `volume`, `resScale`, `aspectRatio`, `shaders`, `paused`, `gpu` |
| `game.stopped` | `error` (when the game could not start or failed) |
| `overlay.toggle` | (the Show UI hotkey) |
| `app.terminate` | (SIGTERM; quit without asking) |
| `screenshot.saved` | `path` |

## Requests

`{"kind": "<kind>", ...}`, sent from emulation threads while the game waits for an answer.

| Kind | Fields | Reply |
|---|---|---|
| `message` | `title`, `message`, `cancellable` | `ok` |
| `input` (software keyboard) | `header`, `sub`, `guide`, `initial`, `submit`, `minLength`, `maxLength` | `ok`, `text` |
| `controllers` (controller applet) | `min`, `max`, `styles[]`, `players[]`, `docked` | `ok`: true to check the controllers again, false to go on |
| `error` (error applet) | `title`, `message`, `buttons[]` | `button` (index) |

## Diagnostics

- `PX_SCREENSHOT_AFTER=<seconds>` takes one screenshot that long after the first frame, for testing captures without
  a keyboard: `open -n --env PX_SCREENSHOT_AFTER=6 "Borgir.app" --args game.nsp`.
- Passing a game path as an argument starts it once the window opens.
- `PX_TEST` drives the interface for scripted checks; steps are separated by commas: `wait:<seconds>`,
  `select:<title id>`, `scrollPane` (the detail pane to its end), `settings:<tab>`, `close` (the window's close
  button), `closeWindow` (Cmd+W), `play:<title id>`, `stop` (the running game), `report` (game running, full screen),
  `command:<name>;key=value;…` (a core command and its reply; `#2` passes a number, `true`, `false` and `null` themselves), `avatarPicker:<profile name>`,
  `profilePicture:<profile name>/<avatar index>/<background hex>`, `gameSettings:<title id>` (that game's settings
  sheet), `playerEditor:<player>` (in Settings › Input) and `controllerRequest:<min>/<max>/<style+style>` (the controller
  prompt, as a game would show it). Steps that report write to the `PX_TEST_LOG` file.
  With it, `PX_FRAME_LOG=<file>` writes the main thread's time between display frames, one line per frame, plus a line
  per `select` step, for measuring stutter in the interface.
- A command from the interface that takes longer than a frame (16 ms) is logged as `Native UI command <name> took … ms`:
  the interface mostly calls in from its main thread, where that is a visible stall.
- SIGTERM quits like Cmd+Q, without asking: the core posts `app.terminate` and the UI terminates through AppKit
  (exiting directly after 10 seconds if it doesn't).
- SDL handles controllers only: the core starts it without its video subsystem, because on macOS that subsystem
  replaces `NSApplication` with a subclass whose `-terminate:` never quits.
