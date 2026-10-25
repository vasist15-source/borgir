# Borgir

A personal Nintendo Switch emulator for Apple silicon Macs. It is a fork of
[Ryujinx_Optime](https://github.com/sunsssyc/Ryujinx_Optime), which is Ryubing (Ryujinx) with a native Metal renderer.
Borgir adds:

- **Metal only.** The Vulkan and OpenGL backends and MoltenVK are gone; games always render with the native Metal
  backend.
- **A native macOS interface.** SwiftUI windows for the game library, settings, keys and firmware, built from
  [`macos/ProjectXUI`](macos/ProjectXUI). The Avalonia interface is gone.
- **Game details.** Clicking a game opens a pane with a Play button, its update (pick one or add files), DLC, mods
  (toggle, import a mod folder or a ZIP, 7z or RAR download, move one to the Trash) and cheats (toggle, even while it runs; add cheat files).
- **Per-game settings.** A game can have its own resolution, docked mode, V-Sync, hypervisor, DRAM size and more;
  everything else follows the global settings.
- **Controllers.** Settings › Input sets up players 1 to 8 and Handheld: device, controller type, button remapping
  (press the key or button), stick dead zones, rumble and motion. When a game asks for controllers, a prompt shows who
  has what and opens these settings.
- **Profiles.** Settings › Profiles adds, renames and deletes user profiles and sets their pictures, from an image file
  or from the Switch avatars in the installed firmware. The toolbar's profile button shows the active profile's picture.
- **An in-game overlay.** Liquid Glass controls over the running game: pause, stop, docked mode, V-Sync, volume,
  resolution, aspect ratio, filtering, screenshot, full screen. It appears when the pointer moves.
- **Screenshots on Metal.** Saved to the data folder's `screenshots` folder.

Borgir needs macOS 26 or later on Apple silicon.

### Install

Download `Borgir-<version>.dmg` from [Releases](https://github.com/vasist15-source/borgir/releases), open it and drag
Borgir to Applications. Borgir is signed ad hoc, not with an Apple Developer ID, so macOS stops the first launch: choose
Done, then open System Settings › Privacy & Security and click Open Anyway next to the message about Borgir. If macOS
says Borgir is damaged instead, clear the download's quarantine flag and open it again:

```bash
xattr -dr com.apple.quarantine /Applications/Borgir.app
```

Games need keys and firmware dumped from your own Switch: install them from Settings › General. Borgir includes no
keys, firmware or games.

### Build

Needs the .NET SDK from [global.json](global.json) (10.0.301 or later) and the Xcode Command Line Tools
(`xcode-select --install`). Full Xcode is not required.

```bash
distribution/macos/build_borgir.sh
```

The script publishes the emulator, builds the SwiftUI library, assembles `artifacts/Borgir.app` and signs it ad hoc
with the hypervisor and JIT entitlements. To install it, quit Borgir and replace the copy in Applications (replace,
don't merge: leftover files would break the app's signature):

```bash
rm -rf "/Applications/Borgir.app" && ditto "artifacts/Borgir.app" "/Applications/Borgir.app"
```

### Data

Borgir reads the standard Ryujinx data folder, `~/Library/Application Support/Ryujinx`, so it shares keys, firmware,
saves, profiles, mods and shader caches with Ryujinx and Astris. Its settings live in `Borgir.json` in that folder.
On first launch they are copied from `ProjectX.json` (Borgir was called Project X) or, failing that, from the folder's
`Config.json`, which Borgir never writes to.

The icon, a burger whose buns are two controller halves, is drawn by
[`distribution/macos/make_icon.swift`](distribution/macos/make_icon.swift).

### Notes

- `Ryujinx.LibHac` pre-releases come from Ryubing's own package server. A verified copy is vendored in
  [nuget-local](nuget-local/README.md), so builds work when that server is down.
- The bridge between the emulator and the interface is described in [src/Ryujinx/Native/README.md](src/Ryujinx/Native/README.md).
- The Metal backend is the Optime fork's: tuned for Tears of the Kingdom, experimental elsewhere. Its notes follow.

---

# Ryujinx_Optime：带原生 Metal 后端的 Ryujinx

这是 [Ryubing 版 Ryujinx](https://git.ryujinx.app/ryubing/ryujinx) 1.3.3 的一个个人分支，加了一个**原生 Metal 渲染后端**，主要针对《塞尔达传说：王国之泪》1.4.2 在 Apple Silicon Mac 上调优，修掉了一批画面、稳定性和性能问题。Metal 后端仍是实验性的。

- **主分支**：`codex/native-metal-backend`
- **测试环境**：Apple M1 Max，macOS 26.5 32内存
- **本地使用指南**：[docs/METAL_FORK_GUIDE.md](docs/METAL_FORK_GUIDE.md)，包括编译、签名、启动、推荐设置、调试开关和问题反馈
- **修复说明**：[docs/METAL_FIXES.md](docs/METAL_FIXES.md)，每个修复的现象、原因、提交号，以及已知问题

### 快速开始

需要 .NET SDK 10.0.301 或更高版本。

```bash
dotnet publish src/Ryujinx/Ryujinx.csproj -c Release -r osx-arm64 --self-contained true \
  -p:Version=1.3.3+local-metal -p:AssemblyVersion=1.3.3.0 -p:FileVersion=1.3.3.0 \
  -p:InformationalVersion=1.3.3+local-metal \
  -o artifacts/terminal/Ryujinx-metal-local
codesign --entitlements distribution/macos/entitlements.xml -f -s - artifacts/terminal/Ryujinx-metal-local/Ryujinx
artifacts/terminal/Ryujinx-metal-local/Ryujinx --graphics-backend Metal
```

**首次运行前必须安装密钥和固件**，它们只能从你自己的 Switch 导出，步骤见使用指南第 3 节。所有修复默认开启，正常游玩不需要设置环境变量。王国之泪建议把 UltraCam Mod 的帧率上限设为 50，原因见使用指南。

---

*以下为上游 Ryubing README 原文。*

<table align="center">
    <tr>
        <td align="center" width="25%">
            <img src="https://raw.githubusercontent.com/Ryubing/Assets/refs/heads/main/RyujinxApp_1024.png" alt="Ryujinx" >
        </td>
        <td align="center" width="75%">
          
<h1 class="ryu-gradient-text">Ryujinx</h1>

[![Latest release](https://git.ryujinx.app/projects/Ryubing/badges/release.svg?label=stable&color=32cd32)](https://update.ryujinx.app/latest/stable)
[![Latest canary release](https://git.ryujinx.app/Ryubing/Canary/badges/release.svg?label=canary&color=FF4500)](https://update.ryujinx.app/latest/canary)
<br>
<a href="https://discord.gg/PEuzjrFXUA">
<img src="https://img.shields.io/discord/1294443224030511104?color=5865F2&label=Ryubing&logo=discord&logoColor=white" alt="Discord">
</a>
        </td>
    </tr>
</table>

<p align="center">
  Ryujinx is an open-source Nintendo Switch emulator, originally created by gdkchan, written in C#.
  This emulator aims at providing excellent accuracy and performance, a user-friendly interface and consistent builds.
  It was written from scratch and development on the project began in September 2017.
  Ryujinx is available on a self-managed <a class="forgejo-gradient-text" href="https://github.com/Ryubing/forgejo" target="_blank">modified Forgejo</a> instance under the <a href="https://git.ryujinx.app/projects/Ryubing/src/branch/master/LICENSE.txt" target="_blank">MIT license</a>.
  <br />
</p>
<p align="center">
  On October 1st 2024, Ryujinx was discontinued as the creator was forced to abandon the project.
  <br>
  This fork is intended to be a QoL uplift for existing Ryujinx users.
  <br>
  This is not a Ryujinx revival project. This is not a Phoenix project.
  <br>
  Guides and documentation can be found on the <a href="https://git.ryujinx.app/projects/Ryubing/wiki/Home">Wiki tab</a>.
</p>

<p align="center">
    <img src="https://git.ryujinx.app/projects/Ryubing/raw/branch/master/docs/shell.png" alt="Ryujinx example">
</p>

## Usage

To run this emulator, your PC must be equipped with at least:
- 8GiB of RAM 
- 6 cores
- A GPU released within the last 10 years.
- OpenGL 4.6 | Vulkan 1.4
- Windows 10 version 20H1 | macOS Big Sur (Apple Silicon)

Failing to meet these requirements may result in a poor gameplay experience or unexpected crashes.

## Latest build

Stable builds are made every so often, based on the `master` branch, that then gets put into the releases you know and love.
These stable builds exist so that the end user can get a more **enjoyable and stable experience**.
They are released every month or so, to ensure consistent updates, while not being an annoying amount of individual updates to download over the course of that month.

You can find the stable releases [here](https://git.ryujinx.app/projects/Ryubing/releases).

Canary builds are compiled automatically for each commit on the `master` branch.
While we strive to ensure optimal stability and performance prior to pushing an update, these builds **may be unstable or completely broken**.
These canary builds are only recommended for experienced users.

You can find the canary releases [here](https://git.ryujinx.app/Ryubing/Canary/releases).

## Documentation

If you are planning to contribute or just want to learn more about this project please read through our [documentation](https://git.ryujinx.app/projects/Ryubing/src/branch/master/docs/README.md).

## Features

- **Audio**

  Audio output is entirely supported, audio input (microphone) isn't supported.
  We use C# wrappers for [OpenAL](https://openal-soft.org/), and [SDL3](https://www.libsdl.org/) & [libsoundio](http://libsound.io/) as fallbacks.

- **CPU**

  The CPU emulator, ARMeilleure, emulates an ARMv8 CPU and currently has support for most 64-bit ARMv8 and some of the ARMv7 (and older) instructions, including partial 32-bit support.
  It translates the ARM code to a custom IR, performs a few optimizations, and turns that into x86 code.
  There are three memory manager options available depending on the user's preference, leveraging both software-based (slower) and host-mapped modes (much faster).
  The fastest option (host, unchecked) is set by default.
  Ryujinx also features an optional Profiled Persistent Translation Cache, which essentially caches translated functions so that they do not need to be translated every time the game loads.
  The net result is a significant reduction in load times (the amount of time between launching a game and arriving at the title screen) for nearly every game.
  NOTE: This feature is enabled by default in the Options menu > System tab.
  You must launch the game at least twice to the title screen or beyond before performance improvements are unlocked on the third launch!
  These improvements are permanent and do not require any extra launches going forward.

- **GPU**

  The GPU emulator emulates the Switch's Maxwell GPU using either the OpenGL (version 4.5 minimum), Vulkan, or Metal (via MoltenVK) APIs through a custom build of OpenTK or Silk.NET respectively.
  There are currently six graphics enhancements available to the end user in Ryujinx: Disk Shader Caching, Resolution Scaling, Anti-Aliasing, Scaling Filters (including FSR), Anisotropic Filtering and Aspect Ratio Adjustment.
  These enhancements can be adjusted or toggled as desired in the GUI.

- **Input**

  We currently have support for keyboard, mouse, touch input, Joy-Con input support, and nearly all controllers.
  Motion controls are natively supported in most cases; for dual-JoyCon motion support, DS4Windows or BetterJoy are currently required.
  In all scenarios, you can set up everything inside the input configuration menu.

- **DLC & Modifications**

  Ryujinx is able to manage add-on content/downloadable content through the GUI.
  Mods (romfs, exefs, and runtime mods such as cheats) are also supported;
  the GUI contains a shortcut to open the respective mods folder for a particular game.

- **Configuration**

  The emulator has settings for enabling or disabling some logging, remapping controllers, and more.
  You can configure all of them through the graphical interface or manually through the config file, `Config.json`, found in the Ryujinx data folder which can be accessed by clicking `Open Ryujinx Folder` under the File menu in the GUI.

## License

This software is licensed under the terms of the [MIT license](https://git.ryujinx.app/projects/Ryubing/src/branch/master/LICENSE.txt).
This project makes use of code authored by the libvpx project, licensed under BSD and the ffmpeg project, licensed under LGPLv3.
See [LICENSE.txt](https://git.ryujinx.app/projects/Ryubing/src/branch/master/LICENSE.txt) and [THIRDPARTY.md](https://git.ryujinx.app/projects/Ryubing/src/branch/master/distribution/legal/THIRDPARTY.md) for more details.

## Credits

- [LibHac](https://git.ryujinx.app/projects/LibHac) is used for our file-system.
- [AmiiboAPI](https://www.amiiboapi.com) is used in our Amiibo emulation.
- [ldn_mitm](https://github.com/spacemeowx2/ldn_mitm) is used for one of our available multiplayer modes.
- [ShellLink](https://github.com/securifybv/ShellLink) is used for Windows shortcut generation.
