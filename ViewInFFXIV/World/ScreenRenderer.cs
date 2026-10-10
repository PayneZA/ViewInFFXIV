using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Overlay;

namespace ViewInFFXIV.World;

public sealed class ScreenRenderer : IDisposable
{
    private const uint BezelColor = 0xFF101010;
    private const float BezelScale = 1.04f;

    private readonly Configuration config;
    private readonly IClientState client;
    private readonly IGameGui gameGui;
    private OverlayContext? overlay;
    private bool nativeAvailable;
    private bool nativeShown;

    public bool UsingOverlay => overlay != null;

    /// <summary>The screen is switched on and placed in the current zone.</summary>
    public bool WillDraw => config.ScreenEnabled && config.ScreenTerritory == client.TerritoryType;

    /// <summary>True when the last frame went through the game's own UI layer (chat bubbles etc. on top).</summary>
    public bool DrawingBehindUi => nativeShown;

    public ScreenRenderer(Configuration config, IClientState client, IGameGui gameGui)
    {
        this.config = config;
        this.client = client;
        this.gameGui = gameGui;
    }

    public void Initialize(Dalamud.Plugin.IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        // Native output (a game UI node on the background layer) lets chat bubbles, name plates and the HUD
        // draw over the screen. If that part fails on this game version, keep the ImGui overlay.
        try
        {
            overlay = OverlayService.Initialize(pluginInterface, new OverlayOptions { EnableNodeOutput = true, EnableVfxRenderer = false });
            nativeAvailable = true;
            return;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Native overlay unavailable; the screen will draw over the game UI");
            OverlayService.Dispose();
        }

        try
        {
            overlay = OverlayService.Initialize(pluginInterface, new OverlayOptions { EnableNodeOutput = false, EnableVfxRenderer = false });
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Overlay library failed to initialize; falling back to WorldToScreen quads");
        }
    }

    /// <param name="texture">Video frame, or null to show a blank screen.</param>
    /// <param name="uiHidden">The game UI is hidden (Scroll Lock, GPose, cutscene): native UI nodes are not shown then.</param>
    public void Draw(ImTextureID? texture, bool uiHidden)
    {
        if (!WillDraw
            || !ScreenPlacement.TryGetCorners(config, out var center, out var right, out var down))
        {
            HideNative();
            return;
        }

        if (overlay == null)
        {
            DrawImGuiFallback(texture, center, right, down);
            return;
        }

        var native = nativeAvailable && config.DrawBehindGameUi && !uiHidden;
        using var draw = OverlayService.Draw(hints: new OverlayDrawHints
        {
            AutoDraw = native ? AutoDraw.NativeOverlay : AutoDraw.ImGuiOverlay,
            DrawInCutscene = config.KeepScreenWhenUiHidden,
            DefaultParams = new OverlayDxParams
            {
                OccludedAlpha = 0f,
                OcclusionTolerance = 0.15f,
            },
        });

        // Null means the overlay library chose not to draw this frame (loading fade, cutscene): show nothing rather
        // than an unoccluded fallback quad floating over the fade.
        if (draw == null)
            return;

        nativeShown = native;
        draw.AddQuadFilled(
            center - (right * 0.5f * BezelScale) - (down * 0.5f * BezelScale),
            center + (right * 0.5f * BezelScale) - (down * 0.5f * BezelScale),
            center + (right * 0.5f * BezelScale) + (down * 0.5f * BezelScale),
            center - (right * 0.5f * BezelScale) + (down * 0.5f * BezelScale),
            BezelColor);
        if (texture is { } image)
            draw.AddImage(image, center, right, down);
    }

    /// <summary>Draws nothing this frame.</summary>
    public void Hide() => HideNative();

    /// <summary>
    /// The native node keeps showing its last frame until told otherwise. An empty ImGui-mode frame hides it.
    /// </summary>
    private void HideNative()
    {
        if (!nativeShown || overlay == null)
            return;
        using (OverlayService.Draw(hints: new OverlayDrawHints { AutoDraw = AutoDraw.ImGuiOverlay, DrawInCutscene = true, DrawWhenFaded = true }))
        {
        }

        nativeShown = false;
    }

    private void DrawImGuiFallback(ImTextureID? texture, Vector3 center, Vector3 right, Vector3 down)
    {
        var tl = center - (right * 0.5f) - (down * 0.5f);
        var tr = center + (right * 0.5f) - (down * 0.5f);
        var br = center + (right * 0.5f) + (down * 0.5f);
        var bl = center - (right * 0.5f) + (down * 0.5f);
        if (!gameGui.WorldToScreen(tl, out var stl)
            || !gameGui.WorldToScreen(tr, out var str)
            || !gameGui.WorldToScreen(br, out var sbr)
            || !gameGui.WorldToScreen(bl, out var sbl))
            return;

        var dl = ImGui.GetBackgroundDrawList();
        if (texture is { } image)
            dl.AddImageQuad(image, stl, str, sbr, sbl);
        else
            dl.AddQuadFilled(stl, str, sbr, sbl, BezelColor);
    }

    public void Dispose()
    {
        overlay?.Dispose();
        overlay = null;
    }
}
