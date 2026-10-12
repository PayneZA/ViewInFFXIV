# Wall screen renderer: maintainer guide

How ViewInFFXIV puts the video on the wall, what it assumes about the game, and what to do when a game or Dalamud
patch breaks it. Read this before changing anything under `ViewInFFXIV/World/`.

---

## 1. The 30-second version

- The game renders on its **own render thread**, executing one big list of commands per frame
  (`ImmediateContext.ProcessCommands`).
- We hook that function and `ImmediateContext.DoSetTargetCommand`, whose addresses come from ClientStructs.
- When the game binds its **final image together with a depth buffer** for the first time in a frame, we draw the
  screen just before. That bind starts its name plate pass, so the screen lands after all post-processing and
  before any UI.
- The screen is **one quad, one draw call**, from a shader (`Wall.hlsl`) that draws the bezel and picture, hides the
  screen behind people and scenery by reading the depth buffer, and writes its own depth so name plates and housing
  markers can go behind it.
- Everything runs on a **deferred context** and is submitted with `restoreContextState: true`, so the game never
  notices.
- If any of this fails, the plugin falls back on its own to an **overlay** (the same draw at Present, over the UI),
  then to a **basic** ImGui quad. A patch that breaks the in-world spot therefore shows the screen over the UI
  instead of not at all.

---

## 2. Files

| File | Role |
|---|---|
| `World/ScreenRenderer.cs` | Entry point from `Plugin.Draw`. Builds the per-frame `WallFrame`, picks the tier (in-world / overlay / basic), handles fallbacks, owns the video view's lifetime. |
| `World/FrameInjector.cs` | **In-world tier.** The two hooks, the per-frame snapshot, the injection rule, the miss counter. Most patch fixes happen here. |
| `World/WallRenderer.cs` | The D3D11 side: shaders, states, and `Pass` (deferred context, constants, views onto game textures, depth copy and depth write, GPU timer). `WallFrame` is defined here too. |
| `World/Shaders/Wall.hlsl` | Vertex and pixel shader. Compiled to `Wall.vs.cso` / `Wall.ps.cso`; **both `.cso` files are committed**. |
| `Diagnostics/RenderTrace.cs` | `/matinee rtrace`: records a few frames of the game's render-target switches. The tool for patch day. |
| `Diagnostics/GpuStopwatch.cs` | GPU timing on the render thread (shown by `/matinee rstat`). |
| `Media/Playability.cs` | Not rendering: checks a YouTube video can play in w2g before it is sent (see P1). |

Settings that affect the renderer (`Configuration.cs`, shown under Settings → Screen):

| Setting | Effect |
|---|---|
| `DrawBehindGameUi` ("Keep chat bubbles, name plates and the HUD on top") | on = in-world tier; off = overlay tier |
| `HideWorldUiOnScreen` ("Hide name plates and item markers over the screen") | on = the screen writes the nearest depth, so no depth-tested UI shows over it |
| `ScreenCoverDepth` ("Covers furniture up to N cm") | scenery this close in front of the screen does not hide it (screens set into walls, over furniture) |
| `KeepScreenWhenUiHidden` | Dalamud keeps calling Draw while the UI is hidden; otherwise the published frame goes stale and the in-world draw stops on its own |

---

## 3. A frame, step by step

```
main thread (Dalamud Draw, inside Present)            render thread (game)
──────────────────────────────────────────            ────────────────────────────────────────────
Plugin.Draw
  SharedVideo.Update   (copy the helper's frame)
  ScreenRenderer.Draw
    builds WallFrame { corners, video view,
                       cover depth, hide UI, Stamp }
    FrameInjector.Publish(frame)  ──────────────────▶ ProcessCommands (hooked)
                                                        BeginFrame: snapshot camera, final targets,
                                                                    G-buffer 3, "is the frame fresh?"
                                                        … shadows, G-buffer, lighting, transparency,
                                                          bloom, tonemap → final image …
                                                        DoSetTargetCommand(final image + depth)  (hooked)
                                                          ► WallRenderer.Pass.Draw  ◄── our draw
                                                        … name plates (depth-tested), 2D UI, gamma copy
                                                        EndFrame: count a miss if we wanted to draw and didn't
Present
```

The frame published on the main thread is drawn by the **next** rendered frame. That is invisible because:
- the camera comes from the render thread's own snapshot, so placement never lags;
- only the video frame and the settings are one frame old.

`Stamp` lets the render thread ignore a frame older than 2 rendered frames. That is how the screen disappears when
Dalamud stops calling Draw (UI hidden with *Keep screen* off, GPose, cutscenes).

---

## 4. Game assumptions

Every one of these is tagged in the code as `GAME ASSUMPTION A<n>`. Run `grep -rn "ASSUMPTION" ViewInFFXIV` to see
all the places. Verified on 2026-10-10, game 7.x, 2560×1440, gamma 0.86, DX11.

### A1. Hook addresses (`FrameInjector` constructor)
- **Assumes:** `ImmediateContext.Addresses.DoSetTargetCommand` and `.ProcessCommands` from ClientStructs are right.
  `ProcessCommands` runs once per frame on the render thread and runs the whole frame. `DoSetTargetCommand` runs for
  every render-target switch. The `RenderCommandSetTarget` layout (`RenderTargets[0..4]`, `DepthBuffer`) is right.
- **If broken:**
  - The hook creation throws: the log says "cannot hook the game's frame" and the screen draws over the UI.
  - Or the hooks never fire: `/matinee rstat` shows `rendered` not increasing.
  - Or SetTarget is now inlined for some commands: the trace shows `[TARGET CHANGED OUTSIDE SetTarget]` near the end
    of the frame.
- **Fix:** update Dalamud (ClientStructs ships with it). If ClientStructs itself is behind, wait for it, or check
  its repo for the new signatures. Never ship a signature of our own if ClientStructs has one.

### A2. The injection rule (`FrameInjector.TryDraw`)
- **Assumes:** each frame ends with, in order:
  1. the final image bound **without** depth (tonemap and other post-processing);
  2. the final image bound **with** the scene depth (name plates and other depth-tested UI);
  3. the final image bound without depth (HUD, chat, windows);
  4. a copy to the swap chain.

  We draw at the first bind of type 2.
- **If broken:** `/matinee rstat` shows `missed` climbing, and after 3 misses in a row the screen draws over the UI.
  Or the screen draws but post-processing covers it (it looks hazy or bloomed), or the UI ends up under it.
- **Check:** `/matinee rtrace`, then read the end of a frame (section 6). Find where the frame switches from 1-draw
  post-processing blocks to UI blocks, and adjust the rule. Keep the rule tied to *which texture and which depth*,
  never to counting binds: a count differs between settings and machines and would need per-user tuning.

### A3. The final image (`FrameInjector.BeginFrame`)
- **Assumes:** the final image is `RenderTargetManager.ToneAdjustSource` when gamma or a colour filter is set,
  otherwise `Device.SwapChain->BackBuffer`. Both pointers stay put for the whole frame.
- **Never use `RenderTargetManager.SwapChainBackBuffer` here.** The game thread switches it between those two
  values *while the render thread is drawing*. Reading it lost ~30% of frames, which showed up as flicker only when
  the game was focused.
- **If broken:** missed frames. The trace shows the final binds landing on a texture we don't compare against.
- **Untested so far:** default gamma with no colour filter (the back-buffer branch). Run `rtrace` with that setting
  when convenient.

### A4. Threads (`FrameInjector.BeginFrame`)
- **Assumes:** the render thread runs a frame while the game thread is already updating the next one. `rstat` shows
  "framework ticks during render" at nearly 100%.
- **Rule:** read game-thread state (camera, `RenderTargetManager` fields, `Control`) only at the start of
  `ProcessCommands`; never mid-frame. Things published from the main thread to the render thread go through the
  `volatile` `WallFrame` (immutable). The video view it carries has its own COM reference and is released 8 frames
  after it is replaced (`ScreenRenderer.VideoView`).
- **If broken:** intermittent flicker or misses that get worse at high frame rates. Compare focused vs unfocused.

### A5. The camera (`FrameInjector.BeginFrame`)
- **Assumes:** `Render.Manager.MainCamera` at the start of `ProcessCommands` is the camera of the frame being
  rendered. Its view × projection matches `Control.ViewProjectionMatrix` (the trace prints both as `ctrlVP` and
  `mainVP`). `Origin` is the camera position and `NearPlane` the near plane.
- **If broken:** the screen swims or jitters against the wall while the camera moves, or its occlusion edges lag
  behind people.

### A6. Depth (`Wall.hlsl`, `WallRenderer.DepthReadFormat/DepthWriteFormat`)
- **Assumes:**
  - Reverse-Z with an infinite far plane: view distance = `near / depth`, and the sky is 0.
  - The depth bound in the SetTarget of A2 is the scene depth; it was `DepthStencil`, `D24_UNORM_S8_UINT_3`, R24G8
    typeless, readable by shaders.
  - Its size can differ from the target's (upscaling), so the shader uses `ActualWidth/Height` scale factors.
- **If broken:**
  - Wrong format: the log says "scene depth … cannot be read" and the screen shows through everything.
  - Wrong depth model: the screen hides behind everything, or behind nothing. `/matinee rdebug` (ignore depth) shows
    whether the screen itself still draws.
- **Untested so far:** DLSS/FSR and dynamic resolution. Watch the screen's edges for shimmer while the camera moves.

### A7. Telling people from scenery (`Wall.hlsl toleranceAt`, `FrameInjector.BeginFrame`)
- **Assumes:** `RenderTargetManager.GBuffers[3]` is black (all channels < 0.05) on characters and sky, and coloured
  on scenery. Characters get a strict 5 cm; scenery gets the *Covers furniture* distance.
- **Check:** `/matinee rdebug info` paints the screen red where it sees a character and green where it sees scenery.
- **If broken:** people stop hiding the screen (they read as scenery), or walls cut holes in it (they read as
  characters).

### A8. Which UI is depth-tested (`Wall.hlsl` end of `ps`)
- **Assumes:** name plates and housing item markers are drawn after our point *with* depth testing (reverse-Z,
  nearer wins), so the depth the screen writes can hide them. Chat bubbles, the HUD and windows are drawn without
  depth, so they always stay on top.
- **Check:** in a trace, the depth-bound block after our point has draws with the UI shown and none in GPose.
- **If broken:** name plates show over the screen even with the setting on, or chat bubbles disappear behind it.

### A9. The device (`WallRenderer` constructor)
- **Assumes:** `Kernel.Device.D3D11Forwarder` is the game's `ID3D11Device`, and its immediate context is the one the
  render thread uses. The game tolerates `ExecuteCommandList(…, true)` between its commands.

### A10. The overlay tier (`ScreenRenderer.DrawOverlay`)
- **Assumes:**
  - At Present the swap chain back buffer holds the finished frame.
  - `RenderTargetManager.DepthStencil` still holds that frame's depth.
  - `Control.ViewProjectionMatrix` is the camera it was drawn with.
  - The loading fades are the addons `FadeMiddle` and `FadeBlack`.
- **Check:** `/matinee rdebug overlay` forces this tier.

### P1. YouTube embedding check (`Media/Playability.cs`)
- **Assumes:** `https://www.youtube.com/oembed?format=json&url=…` answers 200 when a video can be embedded, 401/403
  when its owner turned embedding off, and 404/400 when it is private, removed or invalid.
- **If broken:** any other answer counts as unknown and the video is sent to the room as before, so the worst case is
  the old behaviour. Videos showing "Can't play in a watch party" that do play on YouTube embeds would mean the
  status codes changed.

---

## 5. Patch day checklist

After a game or Dalamud update, with a screen placed in your house:

1. **Does it load?** `plugin_log` / dalamud.log, filtered by `ViewInFFXIV`. Look for "renderer unavailable",
   "cannot hook", "in-world drawing failed".
2. **Which tier is running?** Run `/matinee rstat`. Healthy:
   `tier InWorld … rendered N · drawn N · stale 0 · missed 0 · failed 0 · faulted False`, with `drawn` keeping up
   with `rendered`.
3. **If it is on the overlay, or `missed` is climbing:** run `/matinee rtrace` and check A2/A3 against the report.
   Also compare A1's `ProcessCommands begin/end` pairs (one per frame).
4. **If it draws but looks wrong:**
   - screen hidden: `/matinee rdebug` (ignore depth); still hidden means A2/A5, shows means A6/A7;
   - people or walls misjudged: `/matinee rdebug info` (A7);
   - swims during camera moves: A5;
   - names or markers on top: A8.
5. **Check GPU cost:** `rstat` shows `GPU x ms/frame`. It was 0.071 ms at 1440p, including the depth copy.
6. Fix the code at the tagged spot, rebuild Debug (it hot-reloads), and repeat from step 2.
7. Update this file: the verified date, and anything about the frame that changed.

Two things that cost an hour each the first time:
- **Commands typed right after a rebuild are lost** while the plugin reloads. Wait for "ViewInFFXIV loaded" in the
  log.
- **The game must be focused** for frame-timing problems (A4) to show up. Unfocused, it slows down and the threads
  stop overlapping.

---

## 6. Reading a render trace

`/matinee rtrace` writes `%AppData%\XIVLauncher\pluginConfigs\ViewInFFXIV\rtrace-<time>.txt`. Each `SetTarget` line
shows:
- **`draws`**: how many D3D draws ran on that target before the next switch;
- **`RT0..2` and `DS`**: the textures, named after their `RenderTargetManager` field (sizes and formats included);
- **`ctrlVP` / `mainVP`**: the last row of the two cameras' view-projection matrices (A5: they should match).

The end of a healthy frame (UI shown, gamma set, so the final image is `ToneAdjustSource`):

```
SetTarget draws=   0  RT0=Unk690                         ← HDR post chain ends
SetTarget draws=   1  RT0=ToneAdjustSource  DS=-         ← tonemap into the final image   (A2 step 1)
SetTarget draws=   1  RT0=Unk280            DS=-
SetTarget draws=   1  RT0=ToneAdjustSource  DS=-
SetTarget draws=  18  RT0=ToneAdjustSource  DS=DepthStencil   ← ★ we draw just before this: name plates (A2 step 2)
SetTarget draws=   1  RT0=ToneAdjustSource  DS=DepthStencil
SetTarget draws=   1  RT0=0x…(256x1)        DS=0x…(256x1)
SetTarget draws=   0  RT0=ToneAdjustSource  DS=-
SetTarget draws=  28  RT0=ToneAdjustSource  DS=-         ← HUD, chat, windows            (A2 step 3)
SetTarget draws=   1  RT0=SwapChain.BackBuffer           ← gamma copy                    (A2 step 4)
ProcessCommands end
```

The same trace in GPose: the ★ block has 0 draws (no name plates) and the UI block has 175 (the GPose windows). The
order of blocks stays the same. That is how the UI blocks were identified, and how to identify them again: trace
once with the UI shown and once hidden, and see which blocks' draw counts change.

The summary at the bottom lists the threads, the first final-image bind per frame, and "target changes outside
SetTarget". That last one was 1 per frame (after SSAO, far from our point); a jump near the end of the frame means
A1 is breaking.

---

## 7. Debug commands

None of these change saved settings; everything resets when the plugin reloads.

| Command | What it does |
|---|---|
| `/matinee rstat` | Tier, GPU time, and the counters: `rendered` (frames the game rendered), `drawn` (frames with the screen), `stale` (frames whose published state was too old: UI hidden), `missed` (wanted to draw, spot not found), `failed` (our draw returned an error), `faulted` (an exception stopped in-world drawing until reload), framework/render overlap (A4), last miss |
| `/matinee rtrace` | Render trace of 3 frames (section 6) |
| `/matinee rdebug` | Toggle: ignore scene depth (is it the depth test or the draw?) |
| `/matinee rdebug info` | Toggle: paint what the depth test thinks is behind the screen (A7) |
| `/matinee rdebug overlay` | Toggle: force the overlay tier (A10) |
| `/matinee rdebug over` | Toggle: draw over everything (no depth read, depth still written) |
| `/matinee rdebug ladder` | Toggle: run the depth self-test on any card, as on AMD and Intel (starts it over) |
| `/matinee rdebug mode shader\|hardware\|none` | Force the depth mode on any card; `mode` alone goes back to automatic |
| `/matinee rdebug fail shader\|hardware\|shader hardware` | Make the self-test report those variants hidden (starts it over); `fail` alone clears it |

### Graphics card gating and the depth self-test (`ScreenRenderer.DepthTestSupported`, `World/DepthLadder.cs`)

The shader depth read (people and furniture hiding the screen) is verified on NVIDIA only. On an AMD Radeon RX 9070 XT
it hid the whole screen in the world (drawn every frame, nothing visible; ignoring depth showed it). So on AMD (vendor
0x1002) and Intel (0x8086) cards the depth settings (cover distance, name-plate hiding, the "over everything" box) are
hidden, and the machine finds out by itself how the screen can hide behind things:

- The screen starts over everything (`DepthMode.None`).
- Once a second while it is in view, the render thread draws the real screen five times, invisibly, each inside an
  occlusion query, before our own draw writes any depth: with nothing hiding it, then for each variant in
  `DepthLadder.Variants`: the shader read, then the **hardware** depth test (`DepthMode.Hardware`, the GPU's own
  `GreaterEqual` test against the scene depth with the cover-shifted depth from the shader; no shader depth read at
  all), each with and without G-buffer 3.
- A variant that shows at least 75% of the screen in 3 tests of at least 2000 pixels is kept for the session, best
  first. It never demotes (a wall can hide the screen for real). Any error turns the test off and leaves the screen
  over everything; it never faults the in-world tier, whose fallback would cover chat bubbles.
- With the hardware variant, the name plates of people in front of the screen show over it (it cannot write the
  nearest depth everywhere and still be hidden by them).
- Results go to the Dalamud log ("Wall screen depth self-test: …") and `/matinee rstat`. "Over everything" only skips the depth
*read*: the screen still writes its depth, so name plates and housing markers behind it stay hidden (writing depth was
seen working on the failing card). `/matinee rdebug over` previews this mode on any card. `/matinee experimental [on|off]` turns the
depth test and its settings back on, saved across restarts (`Configuration.ExperimentalDepth`). Unknown vendors count as
supported. `/matinee rprobe` reads the scene depth and our copy of it as plain numbers (same two lines in chat on every
card) and is the tool to find out what differs on the failing card; remove the gate once that is fixed and verified.

---

## 8. Pitfalls already paid for

- **MSBuild culture detection:** `Wall.ps.cso` looks like a Pashto (`ps`) resource and is moved into a satellite
  assembly. The `EmbeddedResource` has `WithCulture="false"` and an explicit `LogicalName`; keep both.
- **Commit the `.cso` files.** The embedded-resource wildcard is expanded before `CompileWallShaders` runs, so a
  checkout without them builds a plugin with no shaders. It falls back to the basic renderer without an error in the
  build.
- **Constant buffer layout:** `WallRenderer.Constants` (C#, sequential) and `cbuffer Wall` (HLSL) must match field
  for field in 16-byte rows. Matrices are `row_major` in HLSL and passed as `System.Numerics` matrices without
  transposing. Add fields at the end, on both sides.
- **COM references:** `WallRenderer.Borrow` takes a reference on a pointer we don't own, and its wrapper releases it.
  Never wrap a game pointer with `new X(ptr)` directly: disposing it would release the game's own reference.
- **The depth copy is required for depth writes.** A shader cannot read the depth buffer it is writing, so the scene
  depth is copied first (`Pass.DepthWriteViews`).
- **"In front of the screen" is measured along the screen's normal**, not along the view ray. The view-ray version
  made screens set into walls sink into them at steep angles.
- **The name plate setting means all of them.** With *Hide name plates and item markers over the screen* on, the
  screen writes the nearest depth, so a name plate only partly over the screen is cut at its edge. A distance-based
  version was tried and rejected: the setting's job is to hide them.
- **Line endings:** the repo stores LF. Python on Windows writes CRLF in text mode unless you pass `newline=''`.

---

## 9. Video frame rate on the wall: the capture ceiling

A 60 fps video reaches the wall at about **45–48 fps**. 24–30 fps videos, most of YouTube, reach it at their full
rate. The limit is Windows.Graphics.Capture, not this plugin. Measured on 2026-10-10 with a standalone capture
tool (do-nothing handler, 1–4 pool buffers) on the hidden browser window:

| Main monitor | Frames delivered | Spacing between frames |
|---|---|---|
| 144 Hz | 48.0 /s, steady | 20.83 ms (3 refresh ticks) |
| 60 Hz | 42–48 /s | 16.7 ms or 33.5 ms (a skipped tick ~1 in 4) |

- **The browser is not the limit:** it decodes and presents the full 60 fps (0 dropped) at both refresh rates.
- **This plugin is not the limit:** the helper passes on every captured frame (`/matinee bench`: dropped 0/s).
- **What doesn't change it:** the window's position or nearest monitor, GPU load (game focused or in the
  background), pool size, or content that changes on every refresh.

The capture appears to keep a minimum gap of about 1/60 s between frames, aligned to refresh ticks. The only API
that changes this (`GraphicsCaptureSession.MinUpdateInterval`) is Windows 11 24H2+, and the plugin keeps identical
features on Windows 10 and 11, so it is not used. Going beyond this would need a different way to get frames out of
the browser.

## 10. Testing status

| Case | Status |
|---|---|
| In-world, gamma set (ToneAdjustSource path) | verified live |
| All angles, flicker under camera motion, people in front, furniture cover | verified live |
| Name plates/markers hidden, chat bubbles on top | verified live |
| Overlay tier | verified live |
| Plugin reload while drawing (hooks active) | verified ~15 reloads |
| Default gamma, no colour filter (back-buffer path) | not yet |
| DLSS / FSR / dynamic resolution | not yet |
| Window resize, fullscreen toggle | not yet |
| ReShade | not yet |
| GPose with *Keep screen* off (should hide) / on (should stay) | not yet |
