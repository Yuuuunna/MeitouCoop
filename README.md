<p align="center">
  <img src="assets/branding/header.png" alt="MeitouClient" width="100%">
</p>

[![Support me on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/martinvlcek)

C#/.NET 11 reimplementation of [Kenshi](https://lofigames.com/), aiming to run original content,
mods and saves unchanged. **No game assets are shipped; an installed legal copy is required.**
Architecture/research: [docs index](docs/README.md). Website: [meitou.kenshi.wiki](https://meitou.kenshi.wiki/).

[![Contributors](assets/branding/contributors.png?v=c86af348)](https://github.com/VlcekM/MeitouClient/graphs/contributors)

## Modding

MeitouClient should be fully compatible with Forgotten Construction Set Kenshi mods.

We also have a C# modding API, do check out our [Wiki](https://github.com/VlcekM/MeitouClient/wiki/Writing-code-mods) for documentation!

## Download and requirements

The easiest way to get the game is the [Meitou Launcher](https://github.com/VlcekM/MeitouClient/releases/latest): it installs and
updates the game and manages mods. Download the newest one for

[Windows](https://github.com/VlcekM/MeitouClient/releases/latest/download/MeitouLauncher-win-x64-Setup.exe) (Setup.exe),

[Linux](https://github.com/VlcekM/MeitouClient/releases/latest/download/MeitouLauncher-linux-x64.AppImage) (AppImage),

[macOS Apple silicon](https://github.com/VlcekM/MeitouClient/releases/latest/download/MeitouLauncher-osx-arm64-Setup.pkg) or

[macOS Intel](https://github.com/VlcekM/MeitouClient/releases/latest/download/MeitouLauncher-osx-x64-Setup.pkg) (unsigned, Gatekeeper warns).

For macOS 13 Ventura, use the .NET 10 legacy launcher:
[Intel](https://github.com/VlcekM/MeitouClient/releases/latest/download/MeitouLauncherLegacy-osx-x64-legacy-Setup.pkg) or
[Apple silicon](https://github.com/VlcekM/MeitouClient/releases/latest/download/MeitouLauncherLegacy-osx-arm64-legacy-Setup.pkg).
It installs the matching legacy game builds; standard builds use .NET 11 and require macOS 14 or later.

By hand: [releases](https://github.com/VlcekM/MeitouClient/releases) for Windows, Linux and macOS, unzip and run

Steam installs, and GOG installs made by GOG Galaxy, Heroic, Lutris or Bottles, are located automatically; otherwise select the Kenshi folder when prompted,
set KENSHI_PATH, or create git-ignored meitou.local.json in the repo/working/executable directory (on Linux and macOS also `~/.config/meitou/config.json`, the place the prompt saves to when the executable's folder is read-only):

```json
{ "kenshiPath": "<your Kenshi install directory>" }
```

## Build and run

Building requires the [.NET 11 SDK](https://dotnet.microsoft.com/download).

```text
dotnet build -c Release
dotnet test -c Release --filter "Category!=Slow&Category!=Bench"
dotnet run -c Release --project src/Meitou.Game
```

To export both Windows x64 multiplayer packages, run `pwsh -File ./export-packages.ps1` (PowerShell 7).
It publishes Release self-contained builds and writes client/server ZIPs, launch scripts, instructions and
SHA256SUMS into `artifacts/releases/multiplayer-<timestamp>`; use `-Output <folder>` for a new destination
(relative to the repository). Published staging files remain in `artifacts/multiplayer-stage/`.

For live UI iteration, run `dotnet watch --project src/Meitou.Game -c Debug -- --ui meitou --windowed`.
Saving supported C# edits updates the running game; Ctrl+R in the watch terminal restarts it.
Startup code, static initializers and already-baked UI textures need a restart to show changes.
DLSS also needs the vendor libraries (Windows: the Streamline DLLs; Linux: `libnvidia-ngx-dlss.so.*` from NVIDIA's
[DLSS SDK](https://github.com/NVIDIA/DLSS), `lib/Linux_x86_64/rel/`, and `libmeitou_ngx.so`, which `tools/scripts/build-ngx-shim.sh`
builds into `dlss-runtime/`; it needs curl and g++, on Fedora and RHEL also libstdc++-static) beside the Debug executable, or `MEITOU_DLSS_PATH` (formerly `MEITOU_STREAMLINE_PATH`, still read) pointing to their directory; see [third-party notices](THIRD_PARTY_NOTICES.md).

Run the full suite before committing. Slow tests require assets/GPU/long runs; Bench tests assert
timing and need a quiet machine. Game/viewer share the rendering and data libraries. Use
--help / viewer --world --help for options. Tab opens settings, F10 lists current keys, F11/F12
show statistics/profiler. Game F5 saves/F9 loads; F2..F4 select1/2/5 speed, Space pauses,
F6 cinematic camera, F7 hides UI. [Viewer guide](docs/viewer.md), [simulation](docs/simulation.md),
[UI compatibility](docs/faithful-ui-parity.md).

## Releases and license

GPL-3.0-or-later, with [license exception](LICENSE-EXCEPTION.md). Readers and game logic use
interoperability facts; proprietary SDKs/original content/decompiled code are not distributed.
Contribution rules: [AGENTS.md](AGENTS.md); current choices: [DECISIONS.md](DECISIONS.md).
