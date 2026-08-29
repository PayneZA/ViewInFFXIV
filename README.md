# ViewInFFXIV

Dalamud plugin that captures a **browser or Discord window** and draws it on a 16:9 screen in FFXIV housing. Use WatchTogether, Twitch, YouTube, or Discord in your own browser (with uBlock, etc.) and put that picture on the wall.

## Solution layout

| Project | Role |
|---------|------|
| **[ViewInFFXIV](ViewInFFXIV/)** | Dalamud plugin (`ViewInFFXIV.dll`). Runs inside `ffxiv_dx11.exe`, draws the video on a housing wall, talks to the helper over named pipes + shared memory. |
| **[ViewInFFXIV.Host](ViewInFFXIV.Host/)** | Out-of-process capture helper (`ViewInFFXIV.Host.exe`). WinForms daemon; captures a chosen window with `PrintWindow`, writes BGRA frames to a memory-mapped file. Never loaded into the game. |
| **[ViewInFFXIV.Shared](ViewInFFXIV.Shared/)** | IPC contracts shared by the plugin and helper (pipe messages, frame buffer layout, browser catalog). |

Build order: **ViewInFFXIV.Shared** → **ViewInFFXIV.Host** → **ViewInFFXIV** (the plugin project builds the host and copies it into `ViewInFFXIV/bin/.../Host/` automatically).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Visual Studio 2022](https://visualstudio.microsoft.com/) with **.NET desktop development** (optional; CLI works too)
- Local **XIVLauncher / Dalamud** dev hooks (`%AppData%\XIVLauncher\addon\Hooks\dev`, or set `DALAMUD_HOME`)
- A browser (Chrome, Brave, Edge, Firefox, etc.) or Discord desktop for the video source

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

Inside the zip: `ViewInFFXIV.dll`, dependencies, `ViewInFFXIV.json`, `Host/` (capture helper), and `INSTALL.txt`.

**To install:** extract anywhere → `/xlsettings` → **Experimental** → **Dev Plugin Locations** → add the extracted folder → `/xlplugins` → enable **ViewInFFXIV**.

Visual Studio **Release \| x64** build produces the zip automatically. No extra step.

## Install from Dalamud plugin installer

Friends can install like a normal third-party plugin — no dev-plugin folder setup.

1. In game: `/xlsettings` → **Experimental** → **Custom Plugin Repositories**
2. Add this URL:

   ```
   https://raw.githubusercontent.com/PayneZA/ViewInFFXIV/main/repo.json
   ```

3. `/xlplugins` → find **ViewInFFXIV** → **Install**
4. Enable the plugin, then `/viewin start` (or turn on **Start helper with FFXIV** in the menu)

The installer zip includes `ViewInFFXIV.dll` and the `Host/` capture helper together.

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

- `/viewin` — remote (helper start/stop, capture window picker, placement)
- `/viewin start` / `/viewin stop` — start or stop the capture helper
- `/viewin place` — put the screen on the wall in front of you
- `/viewin apply <code>` — apply a wall placement share code

Optional: enable **Keep screen when UI hidden** in `/viewin` to leave the wall TV visible during Scroll Lock, cutscenes, and GPose while the game HUD and plugin menu hide.

Both people need Dalamud + ViewInFFXIV. Open the same watch party in your browsers. A guest without the plugin sees a blank wall.

## Saved spots per zone

Each housing **territory** can store up to **12 named TV placements** (e.g. Lounge, Bedroom). Lounge and bedroom share the same territory ID — use **named spots** to switch between them.

In `/viewin` under **Saved spots**:

- **Spot** combo — switch between saved placements in the current zone
- **Save spot** — overwrite the active spot with current slider values
- **New spot** — save the current placement under a new name
- **Delete spot** — remove the active spot from this zone
- **Remove from zone** — same as delete spot (clears the active saved placement)

When you leave and re-enter a zone, the last active spot for that territory restores automatically.

## Capture sources

Pick a window in `/viewin` under **Capture window**:

- **Browsers** — Chrome, Brave, Edge, Firefox, Opera, Vivaldi, LibreWolf, and others listed in the helper
- **Discord** — Discord desktop (stable, PTB, or Canary). Pop out a stream or activity for best results

Audio stays in the browser or Discord app. ViewInFFXIV captures pixels only.

Capture uses **PrintWindow** (Win10/11). Large windows are scaled to fit 1920×1080 before upload. Fullscreen the video (F11) in the browser if the page has sidebars you do not want on the wall.

## Constraints

- **DRM / protected capture.** Netflix-style DRM can black-screen a share. YouTube and typical WatchTogether embeds are the reliable path.
- **Audio is 2D** from the browser or Discord process. Positional audio from the wall is not in v1.
- Client overlay only. It does not inject game packets or create real housing furniture.

## Credits & third-party

| Component | Used for | License / link |
|-----------|----------|----------------|
| [Dalamud](https://github.com/goatcorp/Dalamud) | Plugin host, UI, textures, hooks | [AGPL-3.0](https://github.com/goatcorp/Dalamud/blob/master/LICENSE) |
| [WatchTogether.watch](https://watchtogether.watch/) | Watch-party website (use in your own browser) | Their terms |
| [Pictomancy](https://github.com/Olde-School-RuneScape/Pictomancy) | World-space drawing when available | See package / repo |
| .NET / Windows Forms | Capture helper | Microsoft |

ViewInFFXIV is not affiliated with Square Enix, WatchTogether, or the Dalamud team.
