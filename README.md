# ViewInFFXIV

Dalamud plugin for watch parties inside FFXIV housing: it runs a w2g (Watch2Gether) room in its own hidden Brave
browser and puts the video on a 16:9 screen on your wall, with the audio playing from Brave. No central server:
everyone joins the same w2g room and sees it on their own wall.

## Requirements

- **Windows 10 (2004+) or Windows 11** — the video is captured with Windows.Graphics.Capture on the GPU.
- **[Brave](https://brave.com/)** — its built-in ad blocking keeps YouTube ads out; the plugin runs a separate
  ViewInFFXIV Brave profile, so your normal browser is untouched.
- Dalamud / XIVLauncher. No separate .NET install: the helper runs on XIVLauncher's runtime.

Not supported: Linux / Steam Deck / Mac (Wine has no Windows.Graphics.Capture), other browsers.

## In game

`/viewin` opens the window. Everything is also a chat command:

| Command | |
|---|---|
| `/viewin create` | Start Brave, create a w2g room, copy the invite link |
| `/viewin join <w2g link>` | Join a room (no link: your last room). Chat lines with a w2g link also get a clickable **[Join watch party]** |
| `/viewin play <link>` | Play a video in the room |
| `/viewin pause` / `resume` / `seek 1:23` / `seek +10` | Playback — syncs to everyone in the room (YouTube) |
| `/viewin cc [on\|off]` / `vol 0-100` | English subtitles and volume — just for you, remembered |
| `/viewin invite` / `show` / `hide` | Copy the invite / show or hide the Brave window (e.g. to pick a video in w2g) |
| `/viewin place` / `apply <code>` | Put the screen in front of you / apply a placement share code |
| `/viewin stop` | Close Brave and the helper |

Your w2g name is always your character name. Placement (distance, height, size, saved spots per zone, share codes)
is under **Screen placement…**. Optional: **Keep screen when UI hidden** keeps the wall TV during Scroll Lock,
cutscenes and GPose.

## How it works

```
FFXIV (plugin) ──named pipe──▶ ViewInFFXIV.Host.exe ──DevTools──▶ Brave (off-screen, ViewInFFXIV profile) ▶ w2g
      ▲                              │ Windows.Graphics.Capture
      └──── D3D11 shared texture ◀───┘ (1920×1080, keyed mutex, same GPU as the game)
```

- The helper starts Brave with anti-throttling flags and parks it off-screen (minimising would stop it rendering,
  so a minimised window is put straight back). It drives w2g through the DevTools protocol: create/join rooms,
  your name, playback and subtitles.
- While hidden, the w2g player is stretched over the whole page and the window is sized so the video area is
  exactly 1920×1080; YouTube is pinned to the best quality up to 1080p.
- Each captured frame is copied on the GPU into a texture shared with the game; the plugin copies it into its own
  texture and the overlay library draws it on the wall, occluded by walls. No pixels go through the CPU.

## Solution layout

| Project | Role |
|---------|------|
| **[ViewInFFXIV](ViewInFFXIV/)** | Dalamud plugin. Window, commands, placement, receives the shared texture and draws it. |
| **[ViewInFFXIV.Host](ViewInFFXIV.Host/)** | Helper process (`ViewInFFXIV.Host.exe`): owns Brave, GPU capture, w2g/YouTube automation. Exits with the game. |
| **[ViewInFFXIV.Shared](ViewInFFXIV.Shared/)** | Pipe messages shared by both. |

## Build

Prerequisites: .NET 10 SDK, XIVLauncher/Dalamud dev hooks (`%AppData%\XIVLauncher\addon\Hooks\dev` or
`DALAMUD_HOME`), and the overlay library
checked out next to this repo (`..\overlay-lib`, or pass `-p:OverlayCsproj=...`).

```powershell
dotnet build ViewInFFXIV\ViewInFFXIV.csproj -c Debug -p:Platform=x64
```

The plugin build also builds the helper and copies it into `bin\x64\Debug\Host\`. A **Release** build creates
`dist/release.zip` (dev-plugin folder). For development, set `VIEWINFFXIV_INSTANCE=<name>` to run a second,
separate helper next to the game's.

## Install from the plugin repo

`/xlsettings` → **Experimental** → **Custom Plugin Repositories** → add
`https://raw.githubusercontent.com/PayneZA/ViewInFFXIV/main/repo.json` → `/xlplugins` → install **ViewInFFXIV**.

## Credits

| Component | Used for | License / link |
|-----------|----------|----------------|
| [Dalamud](https://github.com/goatcorp/Dalamud) | Plugin host | [AGPL-3.0](https://github.com/goatcorp/Dalamud/blob/master/LICENSE) |
| [SharpDX](https://github.com/sharpdx/SharpDX) | Direct3D 11 interop | MIT |
| [Watch2Gether](https://w2g.tv/) | Watch-party rooms | Their terms |
| [Brave](https://brave.com/) | Browser | Their terms |

ViewInFFXIV is not affiliated with Square Enix, Watch2Gether, Brave or the Dalamud team.
