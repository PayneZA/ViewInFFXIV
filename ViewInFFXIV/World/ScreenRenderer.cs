using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Component.GUI;
using SharpDX.Direct3D11;
using KernelDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace ViewInFFXIV.World;

/// <summary>
/// Puts the wall screen in the world. In order of preference:
/// <list type="number">
/// <item>In-world (<see cref="FrameInjector"/>): inside the game's frame, under name plates and the UI, no lag.</item>
/// <item>Overlay: the same renderer at the end of the frame, over the game UI.</item>
/// <item>Basic: an ImGui quad, if the renderer could not start.</item>
/// </list>
/// Each tier falls back to the next on its own: a hook that cannot be created, an exception on the render thread, or
/// three frames in a row where the drawing spot was not found (after a patch changed the frame) all end in the
/// overlay. Maintainer guide: docs/renderer.md.
/// </summary>
public sealed unsafe class ScreenRenderer : IDisposable
{
    private const uint BezelColor = 0xFF101010;

    /// <summary>Debug (/matinee rdebug overlay): draw with the overlay instead of in-world.</summary>
    public static bool ForceOverlay;

    /// <summary>Missed in-world frames in a row before the overlay steps in.</summary>
    private const int MissesBeforeOverlay = 3;

    /// <summary>Frames a replaced video view is kept alive, in case the render thread still holds it.</summary>
    private const int ReleaseDelayFrames = 8;

    private readonly Configuration config;
    private readonly IClientState client;
    private readonly IGameGui gameGui;
    private readonly Queue<(long Frame, ShaderResourceView View)> retired = new();
    private Dalamud.Plugin.IDalamudPluginInterface? pluginInterface;
    private IPluginLog? log;
    private WallRenderer? wall;
    private WallRenderer.Pass? overlay;
    private FrameInjector? injector;
    private string? startError;
    private nint videoPointer;
    private ShaderResourceView? videoView;
    private long drawFrames;

    public ScreenRenderer(Configuration config, IClientState client, IGameGui gameGui)
    {
        this.config = config;
        this.client = client;
        this.gameGui = gameGui;
    }

    public enum Tier
    {
        None,
        InWorld,
        Overlay,
        Basic,
    }

    /// <summary>The screen is in game, switched on and placed in the current zone.</summary>
    public bool WillDraw => config.ScreenMode == ScreenMode.InGame && config.ScreenEnabled && config.ScreenTerritory == client.TerritoryType;

    /// <summary>How the screen was shown on the last frame.</summary>
    public Tier Active { get; private set; }

    /// <summary>One line for the diagnostics panel.</summary>
    public string Describe()
    {
        var tier = Active switch
        {
            Tier.InWorld => $"in-world (under the UI){(injector?.Occluding == false ? ", no depth" : "")}",
            Tier.Overlay => injector is { Faulted: true } ? "overlay (in-world stopped after an error)"
                : config.DrawBehindGameUi && injector != null ? "overlay (in-world spot not found)" : "overlay",
            Tier.Basic => "basic" + (startError != null ? $" ({startError})" : ""),
            _ => "idle",
        };
        var problem = injector?.LastProblem ?? overlay?.LastProblem;
        return problem != null ? $"{tier} · {problem}" : tier;
    }

    /// <summary>Counters for /matinee rstat.</summary>
    public string Stats() =>
        $"tier {Active} · {Describe()} · willDraw {WillDraw} · behindUi {config.DrawBehindGameUi} · " +
        (injector == null ? "no in-world hook" : $"GPU {injector.TakeGpuMilliseconds():0.000} ms/frame · rendered {injector.RenderFrames} · drawn {injector.Drawn} · stale {injector.Stale} · missed {injector.Missed} · failed {injector.Failed} · faulted {injector.Faulted} · framework ticks during render {FrameworkOverlaps}/{FrameworkTicks} · last miss: {injector.LastMiss}");

    public long FrameworkTicks;
    public long FrameworkOverlaps;

    public void Initialize(Dalamud.Plugin.IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        try
        {
            wall = new WallRenderer();
            overlay = wall.CreatePass();
        }
        catch (Exception ex)
        {
            startError = ex.Message;
            log.Warning(ex, "Wall screen renderer unavailable; using the basic one");
            return;
        }

        try
        {
            injector = new FrameInjector(wall);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Wall screen: cannot hook the game's frame; the screen will draw over the game UI");
        }
    }

    /// <param name="texture">Video frame, or null to show a blank screen.</param>
    /// <param name="uiHidden">The game UI is hidden (Scroll Lock, GPose, cutscene).</param>
    public void Draw(ImTextureID? texture, bool uiHidden)
    {
        drawFrames++;
        ReleaseRetired();

        if (!WillDraw || !ScreenPlacement.TryGetCorners(config, out var center, out var right, out var down))
        {
            Hide();
            return;
        }

        if (wall == null || overlay == null)
        {
            injector?.Publish(null);
            Active = Tier.Basic;
            DrawBasic(texture, center, right, down);
            return;
        }

        var frame = new WallFrame
        {
            Center = center,
            HalfRight = right * 0.5f,
            HalfDown = down * 0.5f,
            CoverDepth = Math.Clamp(config.ScreenCoverDepth, 0f, 1f),
            HideWorldUi = config.HideWorldUiOnScreen,
            Video = VideoView(texture),
            Stamp = injector?.RenderFrames ?? 0,
        };

        if (injector is { Faulted: false } && config.DrawBehindGameUi && !ForceOverlay)
        {
            injector.Publish(frame);
            if (injector.ConsecutiveMisses < MissesBeforeOverlay)
            {
                Active = Tier.InWorld;
                return;
            }
        }
        else
        {
            injector?.Publish(null);
        }

        Active = Tier.Overlay;
        DrawOverlay(frame);
    }

    /// <summary>Draws nothing this frame.</summary>
    public void Hide()
    {
        injector?.Publish(null);
        Active = Tier.None;
    }

    public void Dispose()
    {
        injector?.Dispose();
        injector = null;
        overlay?.Dispose();
        overlay = null;
        videoView?.Dispose();
        videoView = null;
        while (retired.Count > 0)
            retired.Dequeue().View.Dispose();
        wall?.Dispose();
        wall = null;
    }

    /// <remarks>
    /// GAME ASSUMPTION A10: at Present (where Dalamud calls Draw) the swap chain back buffer holds the finished
    /// frame, RenderTargetManager.DepthStencil still holds its depth, and Control.ViewProjectionMatrix is the camera
    /// it was drawn with. The fade addons are named FadeMiddle and FadeBlack.
    /// </remarks>
    private void DrawOverlay(WallFrame frame)
    {
        // Over everything: hide it while the game fades (loading screens), as the in-world screen is by the fade.
        if (IsFaded())
            return;

        var rtm = RenderTargetManager.Instance();
        var device = KernelDevice.Instance();
        var control = Control.Instance();
        if (rtm == null || device == null || device->SwapChain == null || control == null)
            return;

        var camera = control->CameraManager.GetActiveCamera();
        var renderCamera = camera != null ? camera->SceneCamera.RenderCamera : null;
        var nearPlane = renderCamera != null ? renderCamera->NearPlane : 0.1f;
        var cameraPos = renderCamera != null ? (Vector3)renderCamera->Origin : Vector3.Zero;
        Matrix4x4 viewProj = control->ViewProjectionMatrix;
        overlay!.Draw(frame, device->SwapChain->BackBuffer, rtm->DepthStencil, rtm->GBuffers[3].Value, viewProj, cameraPos, nearPlane, writeDepth: false);
    }

    /// <summary>
    /// Our own reference to the video view, so the render thread never sees it freed under it. The view is
    /// published on the Draw thread and used a frame later on the render thread; a replaced view is released only
    /// after <see cref="ReleaseDelayFrames"/> more frames.
    /// </summary>
    private ShaderResourceView? VideoView(ImTextureID? texture)
    {
        var pointer = texture is { } t ? (nint)t.Handle : 0;
        if (pointer == videoPointer)
            return videoView;

        if (videoView != null)
            retired.Enqueue((drawFrames, videoView));
        videoView = pointer != 0 ? WallRenderer.Borrow<ShaderResourceView>(pointer) : null;
        videoPointer = pointer;
        return videoView;
    }

    private void ReleaseRetired()
    {
        while (retired.Count > 0 && drawFrames - retired.Peek().Frame > ReleaseDelayFrames)
            retired.Dequeue().View.Dispose();
    }

    private bool IsFaded()
    {
        var middle = (AtkUnitBase*)gameGui.GetAddonByName("FadeMiddle").Address;
        var black = (AtkUnitBase*)gameGui.GetAddonByName("FadeBlack").Address;
        return (middle != null && middle->IsVisible) || (black != null && black->IsVisible);
    }

    // ───────────────────────── basic ─────────────────────────

    /// <summary>
    /// ImGui quads have no perspective correction, so the screen is split into a grid of small quads with the
    /// right texture coordinates at every corner; that keeps the picture straight at an angle.
    /// </summary>
    private void DrawBasic(ImTextureID? texture, Vector3 center, Vector3 right, Vector3 down)
    {
        const int cells = 8;
        var points = new Vector2[cells + 1, cells + 1];
        for (var y = 0; y <= cells; y++)
        {
            for (var x = 0; x <= cells; x++)
            {
                var world = center + (right * ((float)x / cells - 0.5f)) + (down * ((float)y / cells - 0.5f));
                if (!gameGui.WorldToScreen(world, out points[x, y]))
                    return;
            }
        }

        var dl = ImGui.GetBackgroundDrawList();
        for (var y = 0; y < cells; y++)
        {
            for (var x = 0; x < cells; x++)
            {
                var a = points[x, y];
                var b = points[x + 1, y];
                var c = points[x + 1, y + 1];
                var d = points[x, y + 1];
                if (texture is { } image)
                {
                    var uv0 = new Vector2((float)x / cells, (float)y / cells);
                    var uv1 = new Vector2((float)(x + 1) / cells, (float)(y + 1) / cells);
                    dl.AddImageQuad(image, a, b, c, d, uv0, new Vector2(uv1.X, uv0.Y), uv1, new Vector2(uv0.X, uv1.Y));
                }
                else
                {
                    dl.AddQuadFilled(a, b, c, d, BezelColor);
                }
            }
        }
    }
}
