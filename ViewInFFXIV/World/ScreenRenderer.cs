using System.Numerics;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Pictomancy;

namespace ViewInFFXIV.World;

public sealed class ScreenRenderer : IDisposable
{
    private readonly Configuration config;
    private readonly IClientState client;
    private readonly IGameGui gameGui;
    private PctContext? pictomancy;
    private bool pictomancyFailed;

    public bool UsingPictomancy => pictomancy != null;

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
            pictomancy = PctService.Initialize(pluginInterface, new PctOptions
            {
                EnableKtkOutput = false,
                EnableVfxRenderer = false,
            });
        }
        catch (Exception ex)
        {
            pictomancyFailed = true;
            log.Warning(ex, "Pictomancy failed to initialize; falling back to WorldToScreen quads");
        }
    }

    public void Draw(IDalamudTextureWrap? texture)
    {
        if (!config.ScreenEnabled || config.ScreenTerritory != client.TerritoryType)
            return;
        if (!ScreenPlacement.TryGetCorners(config, out var center, out var right, out var down))
            return;

        if (!pictomancyFailed && pictomancy != null && texture != null)
        {
            using var draw = PctService.Draw(hints: new PctDrawHints
            {
                DefaultParams = new PctDxParams
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
                draw.AddImage(texture, center, right, down);
                return;
            }
        }

        DrawImGuiFallback(texture, center, right, down);
    }

    private void DrawImGuiFallback(IDalamudTextureWrap? texture, Vector3 center, Vector3 right, Vector3 down)
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

        var dl = Dalamud.Bindings.ImGui.ImGui.GetBackgroundDrawList();
        if (texture != null)
            dl.AddImageQuad(texture.Handle, stl, str, sbr, sbl);
        else
            dl.AddQuadFilled(stl, str, sbr, sbl, 0xFF101010);
    }

    public void Dispose()
    {
        pictomancy?.Dispose();
        pictomancy = null;
    }
}
