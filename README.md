<img src="ViewInFFXIV/images/icon.png" alt="" width="96" align="right">

# Matinee

Watch together on a TV in your FFXIV house. Start a party in one click, send the invite, and everyone sees the same
YouTube video, in sync, on a screen on their wall — with the sound coming from the screen. Or put a Twitch channel on
the TV, or show any app from your PC. No server of ours: the room is a [Watch2Gether](https://w2g.tv/) room, and each
player's plugin shows it on their own wall.

Open it with `/matinee`, or just `/tv`. *Matinee was called ViewInFFXIV until October 2026; `/viewin` still works,
and updating keeps all your settings.*

Matinee is developed hand in hand with [Claude](https://claude.ai) (Opus 5.5), Anthropic's AI, who co-authors the
commits: designed, written and tested in the game together, one feature at a time.

## Requirements

- **Windows 10 (2004+) or Windows 11** — the video is captured with Windows.Graphics.Capture on the GPU.
- **[Brave](https://brave.com/)** — Matinee is made for Brave. The plugin runs its own hidden copy with a separate
  profile, so your normal browser is untouched, and Brave's built-in ad blocking keeps ads off your wall.
  *Google Chrome and Microsoft Edge work on a best-effort basis only: the experience is not guaranteed and they are not
  supported* (you will also see ads). **Automatic** (Settings → Browser) picks Brave, else Chrome, else Edge. Firefox
  does not work: it no longer speaks the Chrome DevTools protocol.
- Dalamud / XIVLauncher. No separate .NET install: the helper runs on XIVLauncher's runtime.

Not supported: Linux / Steam Deck / Mac (Wine has no Windows.Graphics.Capture), Firefox.

## Watching together

1. `/matinee` → **Start a watch party**. The screen goes up in front of you (if this house has none yet), the room is
   created and the invite is copied.
2. Paste the invite to your friends. They paste it into the **Join** box (or `/matinee join <invite>`). The invite
   also carries where your screen is, so their TV appears in the same spot when they are in your house.
3. Search YouTube or paste a link in the window. Play, pause and seek are for everyone; volume and subtitles are
   just for you.

**In the window**

- **Watch** — the live picture, controls, *Search YouTube or paste a link*, results with thumbnails, **Up next**
  (the next one starts when a video ends), **Recent** and **Favourites** (star a video to keep it there).
- **Screen** — choose where the TV is: **In game** (on a wall, to watch with the people in the room) or **On UI**
  (a picture on your own screen that goes wherever you go; drag to move, hover for play/pause, the seek bar, the time
  and your volume).
  In game: place the TV, fine-tune distance / height / size / turn / tilt, saved spots (lounge, bedroom, …) and
  spot codes. Spots belong to the house they were made in, and each house brings back the spot you last used there.
- **Settings** — subtitles, Up next, sound, screen behaviour, the browser, diagnostics.
- **Mini remote** — the remote button at the top shows a small bar with the title, seek bar and transport.
  Drag it wherever it doesn't get in the way; it only shows while something is on the TV and fades while you're not using it.
- **Twitch on the TV** — type a channel (or paste a twitch.tv link) and it plays on your screen, video only, no
  account needed. It runs in its own browser profile; during a party, the party carries on muted for you and one
  button takes you back. Offline channels show a plain "offline" card until they go live. Pop out shows Twitch's
  page on your monitor if it asks something (a content warning, for example); pop in puts it back on the TV.
  Twitch's own ads do show.
- **Share an app on the TV** — pick any app (a video player, a browser, a second game client) and it shows on the
  screen just as it looks, aspect ratio kept, without its title bar. It stays where it is on your screen. If it gets
  paused, the plugin says why in plain words with the one button that fixes it: **Show it again** when it's minimised,
  **Move it to my other monitor** when the game covers a browser or Discord (they pause when hidden), and **Make it
  bigger** for a sharper picture from a small window. Sharing YouTube or Twitch in a browser suggests the watch party
  or Twitch on the TV instead. The app's own sound plays as usual; the yellow outline Windows draws just means it's
  being shared.

**Sound from the screen** — it gets quieter as you walk away, pans left/right with the camera and goes silent when you
leave the screen's zone (Settings → Sound). With the screen on your UI it plays at your own volume.

## Commands

Everything is in the window; these are for macros and chat.

| Command | |
|---|---|
| `/matinee` or `/tv` | Open the window (every command below works with `/tv` too) |
| `/matinee party` | Start a watch party (same as the big button) |
| `/matinee join <invite>` | Join a party (no invite: your last one) |
| `/matinee search <words>` / `play <link>` | Search YouTube in the window / play a link for everyone |
| `/matinee pause` / `resume` / `seek 1:23` / `seek +10` / `next` | Playback for everyone |
| `/matinee cc [on\|off]` / `vol 0-100` | Subtitles and volume — just for you |
| `/matinee invite` / `show` / `hide` | Copy the invite / show or hide the browser (w2g's own playlist and chat) |
| `/matinee place` / `screen` / `apply <code>` | Put the screen in front of you / open the Screen tab / use a spot code |
| `/matinee mode [game\|ui]` | Put the TV in game or on your UI (no argument: switch) |
| `/matinee remote` | Show or hide the mini remote |
| `/matinee twitch <channel>` / `twitch out\|in\|stop` | Put a Twitch channel on the TV / pop its page out or in / stop it |
| `/matinee window <app>` / `window show\|bigger\|move\|stop` | Share an app on the TV (no name lists them) / show it again, make it bigger, move it to your other monitor / stop |
| `/matinee browser auto\|brave\|chrome\|edge` | Pick the browser (switches a running party over) |
| `/matinee stop` | Leave the party, stop Twitch or sharing, and close the browsers |
| `/matinee bench` | 25-second performance check, results in chat |

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
  (`/matinee bench`). How it works and how to fix it after a game patch: [docs/renderer.md](docs/renderer.md).

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

The plugin build also builds the helper and copies it into `bin\x64\Debug\Host\` (run `/matinee stop` first: a
running helper locks its files). A new plugin closes a helper from another build on its own. A **Release** build
creates `dist/release.zip` (dev-plugin folder). Set `VIEWINFFXIV_INSTANCE=<name>` to run a second, separate helper.

## Install from the plugin repo

`/xlsettings` → **Experimental** → **Custom Plugin Repositories** → add
`https://raw.githubusercontent.com/PayneZA/ViewInFFXIV/main/repo.json` → `/xlplugins` → install **Matinee**.

## Credits

| Component | Used for | License / link |
|-----------|----------|----------------|
| [Dalamud](https://github.com/goatcorp/Dalamud) | Plugin host | [AGPL-3.0](https://github.com/goatcorp/Dalamud/blob/master/LICENSE) |
| [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) | Game render structures and hook addresses | MIT |
| [SharpDX](https://github.com/sharpdx/SharpDX) | Direct3D 11 interop | MIT |
| [Watch2Gether](https://w2g.tv/) | Watch-party rooms | Their terms |
| [Brave](https://brave.com/), Chrome, Edge | Browser | Their terms |

Matinee is not affiliated with Square Enix, Watch2Gether, YouTube, Brave, Google, Microsoft or the Dalamud team.
