# ViewInFFXIV

Dalamud plugin that moves a [WatchTogether.watch](https://watchtogether.watch/) room off a second monitor and onto a 16:9 screen in FFXIV housing. WatchTogether still plays and syncs the video. ViewInFFXIV hosts that page (or a live browser/Discord window), scales it to fit, and draws it in the world. Talk in `/say`.

## Solution layout

| Project | Role |
|---------|------|
| **[ViewInFFXIV](ViewInFFXIV/)** | Dalamud plugin (`ViewInFFXIV.dll`). Runs inside `ffxiv_dx11.exe`, draws the video on a housing wall, talks to the helper over named pipes + shared memory. |
| **[ViewInFFXIV.Host](ViewInFFXIV.Host/)** | Out-of-process helper (`ViewInFFXIV.Host.exe`). WebView2 + WinForms; captures the page with `PrintWindow`, writes BGRA frames to a memory-mapped file. Never loaded into the game. |
| **[ViewInFFXIV.Shared](ViewInFFXIV.Shared/)** | IPC contracts shared by the plugin and helper (pipe messages, frame buffer layout, browser catalog). |

Build order: **ViewInFFXIV.Shared** → **ViewInFFXIV.Host** → **ViewInFFXIV** (the plugin project builds the host and copies it into `ViewInFFXIV/bin/.../Host/` automatically).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Visual Studio 2022](https://visualstudio.microsoft.com/) with **.NET desktop development** (optional; CLI works too)
- Local **XIVLauncher / Dalamud** dev hooks (`%AppData%\XIVLauncher\addon\Hooks\dev`, or set `DALAMUD_HOME`)
- **WebView2 Runtime** (usually already installed with Edge)

## Build

### Visual Studio

1. Open [`ViewInFFXIV.sln`](ViewInFFXIV.sln).
2. Set configuration to **Debug \| x64** or **Release \| x64** (required for the Dalamud plugin project).
3. Build the solution (**Build → Build Solution**).

The plugin only exposes **x64** platforms. Shared and Host map to **Any CPU** under the x64 solution configuration.

### Command line

```powershell
dotnet build ViewInFFXIV\ViewInFFXIV.csproj -c Debug
```

Release (also creates a dev-plugin zip — see below):

```powershell
dotnet build ViewInFFXIV\ViewInFFXIV.csproj -c Release -p:Platform=x64
```

### Dev plugin zip (`release.zip`)

Every **Release** build of the plugin project automatically creates:

| File | Contents |
|------|----------|
| `dist/release.zip` | Ready-to-extract dev plugin folder |
| `dist/ViewInFFXIV-{version}.zip` | Same archive, versioned copy |

Inside the zip: `ViewInFFXIV.dll`, dependencies, `ViewInFFXIV.json`, `Host/` (helper + WebView2), and `INSTALL.txt`.

**To install:** extract anywhere → `/xlsettings` → **Experimental** → **Dev Plugin Locations** → add the extracted folder → `/xlplugins` → enable **ViewInFFXIV**.

Visual Studio **Release \| x64** build produces the zip automatically. No extra step.

### Output folders

| Artifact | Path (Visual Studio, **Debug \| x64**) |
|----------|----------------------------------------|
| Plugin | `ViewInFFXIV/bin/x64/Debug/ViewInFFXIV.dll` |
| Helper | `ViewInFFXIV/bin/x64/Debug/Host/ViewInFFXIV.Host.exe` |

CLI `dotnet build ViewInFFXIV\ViewInFFXIV.csproj -c Debug` (no platform) writes to `ViewInFFXIV/bin/Debug/` instead.

Unload the plugin in Dalamud before rebuilding if the copy step reports that `ViewInFFXIV.Host.exe` is locked.

## Load as a dev plugin

1. Build (see above).
2. In game: `/xlsettings` → **Experimental** → add the folder containing `ViewInFFXIV.dll` as a **Dev Plugin Location** (typically `ViewInFFXIV/bin/x64/Debug/` when built from Visual Studio, or `ViewInFFXIV/bin/Debug/` from CLI).
3. `/xlplugins` → **Dev Tools** → **Installed Dev Plugins** → enable **ViewInFFXIV**.

The helper does **not** start automatically unless you enable **Start helper with FFXIV** in the menu (or run `/viewin start`).

## In-game

- `/viewin` — remote (helper start/stop, room URL, volume, capture source, placement)
- `/viewin start` / `/viewin stop` — start or stop the helper process
- `/viewin place` — put the screen on the wall in front of you
- `/viewin host` — show the helper window (login, screen-share picker)
- `/viewin hide` — park the helper off-screen again
- `/viewin apply <code>` — apply a wall placement share code

Both people need Dalamud + ViewInFFXIV. Join the **same WatchTogether room URL**. A guest without the plugin sees a blank wall.

## Capture sources

- **Built-in** — WebView2 inside `ViewInFFXIV.Host.exe` (default).
- **Browser window** — live capture of Chrome, Brave, Edge, Firefox, etc. Audio stays in the browser; ViewInFFXIV is pixels only.
- **Discord** — Discord desktop (stable, PTB, or Canary). Pop out a stream or activity for best results; audio stays in Discord.

Capture uses **PrintWindow** (Win10/11). Large windows are scaled to fit 1920×1080 before upload.

## Constraints

- **DRM / protected capture.** Netflix-style DRM can black-screen a share. Sync Video (YouTube URL) is the reliable path.
- **`getDisplayMedia` in WebView2** is weaker than Chrome. Screen share may need **Show host window**, or the host keeps Chrome for share while both still view in ViewInFFXIV.
- **Audio is 2D** from the helper or browser/Discord process. Positional audio from the wall is not in v1.
- Client overlay only. It does not inject game packets or create real housing furniture.

## Credits & third-party

| Component | Used for | License / link |
|-----------|----------|----------------|
| [Dalamud](https://github.com/goatcorp/Dalamud) | Plugin host, UI, textures, hooks | [AGPL-3.0](https://github.com/goatcorp/Dalamud/blob/master/LICENSE) |
| [WatchTogether.watch](https://watchtogether.watch/) | Watch-party sync and player (website only; not bundled) | Their terms |
| [Microsoft WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) | Built-in browser in `ViewInFFXIV.Host` | [Microsoft EULA](https://www.microsoft.com/legal/terms-of-use) |
| [Pictomancy](https://github.com/Olde-School-RuneScape/Pictomancy) | World-space drawing when available | See package / repo |
| .NET / Windows Forms | Helper UI and capture | Microsoft |

ViewInFFXIV is not affiliated with Square Enix, WatchTogether, or the Dalamud team.
