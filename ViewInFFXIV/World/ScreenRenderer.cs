using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Overlay;

namespace ViewInFFXIV.World;

public sealed class ScreenRenderer : IDisposable
{
    private readonly Configuration config;
    private readonly IClientState client;
    private readonly IGameGui gameGui;
    private OverlayContext? overlay;
    private bool overlayFailed;

    public bool UsingOverlay => overlay != null;

    public ScreenRenderer(Configuration config, IClientState client, IGameGui gameGui)
    {
        this.config = config;
        this.client = client;
        this.gameGui = gameGui;
    }

    public void Initialize(Dalamud.Plugin.IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        try
        {
            overlay = OverlayService.Initialize(pluginInterface, new OverlayOptions
            {
                EnableNodeOutput = false,
                EnableVfxRenderer = false,
            });
        }
        catch (Exception ex)
        {
            overlayFailed = true;
            log.Warning(ex, "Overlay library failed to initialize; falling back to WorldToScreen quads");
        }
    }

    public void Draw(ImTextureID? texture)
    {
        if (!config.ScreenEnabled || config.ScreenTerritory != client.TerritoryType)
            return;
        if (!ScreenPlacement.TryGetCorners(config, out var center, out var right, out var down))
            return;

        if (!overlayFailed && overlay != null && texture is { } image)
        {
            using var draw = OverlayService.Draw(hints: new OverlayDrawHints
            {
                DefaultParams = new OverlayDxParams
                {
                    OccludedAlpha = 0f,
                    OcclusionTolerance = 0.15f,
                },
            });
            if (draw != null)
            {
                var bezel = 1.04f;
                draw.AddQuadFilled(
                    center - (right * 0.5f * bezel) - (down * 0.5f * bezel),
                    center + (right * 0.5f * bezel) - (down * 0.5f * bezel),
                    center + (right * 0.5f * bezel) + (down * 0.5f * bezel),
                    center - (right * 0.5f * bezel) + (down * 0.5f * bezel),
                    0xFF101010);
                draw.AddImage(image, center, right, down);
                return;
            }
        }

        DrawImGuiFallback(texture, center, right, down);
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
            dl.AddQuadFilled(stl, str, sbr, sbl, 0xFF101010);
    }

    public void Dispose()
    {
        overlay?.Dispose();
        overlay = null;
    }
}
