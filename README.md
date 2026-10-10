# ViewInFFXIV

Watch YouTube together on a TV in your FFXIV house. Start a party in one click, send the invite, and everyone sees
the same video, in sync, on a screen on their wall — with the sound coming from the screen. No server of ours:
the room is a [Watch2Gether](https://w2g.tv/) room, and each player's plugin shows it on their own wall.

## Requirements

- **Windows 10 (2004+) or Windows 11** — the video is captured with Windows.Graphics.Capture on the GPU.
- **[Brave](https://brave.com/)** — ViewInFFXIV is made for Brave. The plugin runs its own hidden copy with a separate
  profile, so your normal browser is untouched, and Brave's built-in ad blocking keeps ads off your wall.
  *Google Chrome and Microsoft Edge work on a best-effort basis only: the experience is not guaranteed and they are not
  supported* (you will also see ads). **Automatic** (Settings → Browser) picks Brave, else Chrome, else Edge. Firefox
  does not work: it no longer speaks the Chrome DevTools protocol.
- Dalamud / XIVLauncher. No separate .NET install: the helper runs on XIVLauncher's runtime.

Not supported: Linux / Steam Deck / Mac (Wine has no Windows.Graphics.Capture), Firefox.

## Watching together

1. `/viewin` → **Start a watch party**. The screen goes up in front of you (if this house has none yet), the room is
   created and the invite is copied.
2. Paste the invite to your friends. They paste it into the **Join** box (or `/viewin join <invite>`). The invite
   also carries where your screen is, so their TV appears in the same spot when they are in your house.
3. Search YouTube or paste a link in the window. Play, pause and seek are for everyone; volume and subtitles are
   just for you.

**In the window**

- **Watch** — the live picture, controls, *Search YouTube or paste a link*, results with thumbnails, **Up next**
  (the next one starts when a video ends) and **Recent**.
- **Screen** — place the TV, fine-tune distance / height / size / turn / tilt, saved spots (lounge, bedroom, …) and
  spot codes. Spots belong to the house they were made in, and each house brings back the spot you last used there.
- **Settings** — subtitles, Up next, sound, screen behaviour, the browser, diagnostics.

**Sound from the screen** — it gets quieter as you walk away, pans left/right with the camera and goes silent when you
leave the screen's zone (Settings → Sound).

## Commands

Everything is in the window; these are for macros and chat.

| Command | |
|---|---|
| `/viewin` | Open the window |
| `/viewin party` | Start a watch party (same as the big button) |
| `/viewin join <invite>` | Join a party (no invite: your last one) |
| `/viewin search <words>` / `play <link>` | Search YouTube in the window / play a link for everyone |
| `/viewin pause` / `resume` / `seek 1:23` / `seek +10` / `next` | Playback for everyone |
| `/viewin cc [on\|off]` / `vol 0-100` | Subtitles and volume — just for you |
| `/viewin invite` / `show` / `hide` | Copy the invite / show or hide the browser (w2g's own playlist and chat) |
| `/viewin place` / `screen` / `apply <code>` | Put the screen in front of you / open the Screen tab / use a spot code |
| `/viewin browser auto\|brave\|chrome\|edge` | Pick the browser (switches a running party over) |
| `/viewin stop` | Leave the party and close the browser |
| `/viewin bench` | 25-second performance check, results in chat |

Your name in the room is always your character name.

## How it works

```
FFXIV (plugin) ──named pipe──▶ ViewInFFXIV.Host.exe ──DevTools──▶ Brave/Chrome/Edge (hidden, own profile) ▶ w2g
      ▲                              │ Windows.Graphics.Capture
      └──── D3D11 shared texture ◀───┘ (1920×1080, keyed mutex, same GPU as the game)
```

- The helper starts the browser off-screen (minimising would stop it rendering) and drives w2g through the DevTools
  protocol: rooms, your name, playback, subtitles and the spatial-audio gain/pan (Web Audio inside the player page).
- While hidden, the w2g player fills the page and the window is sized so the video is exactly 1920×1080; YouTube is
  pinned to the best quality up to 1080p and its own controls are hidden.
- Each captured frame is copied on the GPU into a texture shared with the game; the plugin copies it into its own
  texture and draws it on the wall itself, inside the game's own frame: after the game's post-processing and before
  its UI, so name plates, chat bubbles and the HUD sit on top with no lag, and walls, furniture and people in front
  hide it. No pixels go through the CPU. A 60 fps video costs the game well under a millisecond per frame
  (`/viewin bench`). How it works and how to fix it after a game patch: [docs/renderer.md](docs/renderer.md).

## Solution layout

| Project | Role |
|---------|------|
| **[ViewInFFXIV](ViewInFFXIV/)** | Dalamud plugin: window, TV controls, search, queue, placement, spatial audio, draws the shared texture. |
| **[ViewInFFXIV.Host](ViewInFFXIV.Host/)** | Helper process (`ViewInFFXIV.Host.exe`): owns the browser, GPU capture, w2g/YouTube automation. Exits with the game. |
| **[ViewInFFXIV.Shared](ViewInFFXIV.Shared/)** | Pipe messages and the supported-browser list, shared by both. |

## Build

Prerequisites: .NET 10 SDK and XIVLauncher/Dalamud dev hooks (`%AppData%\XIVLauncher\addon\Hooks\dev` or
`DALAMUD_HOME`). The wall shader (`ViewInFFXIV/World/Shaders/Wall.hlsl`) is rebuilt with `fxc` from the Windows SDK
when it is installed; the compiled `.cso` files are committed for builds without it.

```powershell
dotnet build ViewInFFXIV\ViewInFFXIV.csproj -c Debug -p:Platform=x64
```

The plugin build also builds the helper and copies it into `bin\x64\Debug\Host\` (run `/viewin stop` first: a
running helper locks its files). A new plugin closes a helper from another build on its own. A **Release** build
creates `dist/release.zip` (dev-plugin folder). Set `VIEWINFFXIV_INSTANCE=<name>` to run a second, separate helper.

## Install from the plugin repo

`/xlsettings` → **Experimental** → **Custom Plugin Repositories** → add
`https://raw.githubusercontent.com/PayneZA/ViewInFFXIV/main/repo.json` → `/xlplugins` → install **ViewInFFXIV**.

## Credits

| Component | Used for | License / link |
|-----------|----------|----------------|
| [Dalamud](https://github.com/goatcorp/Dalamud) | Plugin host | [AGPL-3.0](https://github.com/goatcorp/Dalamud/blob/master/LICENSE) |
| [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) | Game render structures and hook addresses | MIT |
| [SharpDX](https://github.com/sharpdx/SharpDX) | Direct3D 11 interop | MIT |
| [Watch2Gether](https://w2g.tv/) | Watch-party rooms | Their terms |
| [Brave](https://brave.com/), Chrome, Edge | Browser | Their terms |

ViewInFFXIV is not affiliated with Square Enix, Watch2Gether, YouTube, Brave, Google, Microsoft or the Dalamud team.
