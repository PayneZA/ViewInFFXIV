using System.Numerics;
using Dalamud.Bindings.ImGui;
using ViewInFFXIV.World;

namespace ViewInFFXIV.Windows;

/// <summary>
/// Controls that fade in on the TV itself when you point at it: title, seek bar and transport, no window needed.
/// </summary>
internal sealed class ScreenOverlay
{
    private const float LingerSeconds = 1.2f;
    private const ImGuiWindowFlags Flags =
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking;

    private readonly Plugin plugin;
    private readonly PlayerControls controls;
    private float alpha;
    private double lastWanted;
    private bool overlayHovered;

    public ScreenOverlay(Plugin plugin)
    {
        this.plugin = plugin;
        controls = new PlayerControls(plugin);
    }

    public void Draw()
    {
        var config = plugin.Configuration;
        var playback = plugin.HighPerformance.Playback;
        if (!config.ScreenControls || playback == null || !plugin.Renderer.WillDraw
            || !ScreenPlacement.TryGetCorners(config, out var center, out var right, out var down)
            || !Project(center - (right / 2) - (down / 2), out var tl) || !Project(center + (right / 2) - (down / 2), out var tr)
            || !Project(center + (right / 2) + (down / 2), out var br) || !Project(center - (right / 2) + (down / 2), out var bl))
        {
            alpha = 0;
            overlayHovered = false;
            return;
        }

        // Not through walls from the other end of the house.
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || System.Numerics.Vector3.Distance(player.Position, center) > 6f + config.ScreenWidth + config.HearingRange)
        {
            alpha = 0;
            overlayHovered = false;
            return;
        }

        var io = ImGui.GetIO();
        var now = ImGui.GetTime();
        // Not while you hold a mouse button out in the world (turning the camera across the screen).
        var dragging = !overlayHovered && (ImGui.IsMouseDown(ImGuiMouseButton.Left) || ImGui.IsMouseDown(ImGuiMouseButton.Right));
        if ((InQuad(io.MousePos, tl, tr, br, bl) && !dragging) || overlayHovered || controls.Interacting)
            lastWanted = now;

        var target = now - lastWanted < LingerSeconds ? 1f : 0f;
        alpha += (target - alpha) * MathF.Min(1f, io.DeltaTime * (target > alpha ? 12f : 5f));
        if (alpha < 0.02f)
        {
            overlayHovered = false;
            return;
        }

        // A bar along the bottom of the picture; below it when the screen is too small on screen to fit inside.
        var bottomLength = Vector2.Distance(bl, br);
        var width = Math.Clamp(bottomLength * 0.92f, 330f, 720f);
        const float height = 150f;
        var mid = (bl + br) / 2;
        var screenHeight = MathF.Min(Vector2.Distance(tl, bl), Vector2.Distance(tr, br));
        var pos = screenHeight > height * 1.6f
            ? mid - new Vector2(width / 2, height + 12)
            : mid + new Vector2(-width / 2, 10);

        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(new Vector2(width, height));
        Ui.PushStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, alpha);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 10));
        if (ImGui.Begin("##ViewInFFXIVScreenControls", Flags))
        {
            var dl = ImGui.GetWindowDrawList();
            var min = ImGui.GetWindowPos();
            dl.AddRectFilled(min, min + new Vector2(width, height), Ui.Col(new Vector4(0.05f, 0.05f, 0.07f, 0.72f)), 12f);

            var inner = width - 28;
            ImGui.TextUnformatted(Ui.Fit(playback.Title, inner));
            controls.DrawSeek(playback, inner);
            controls.DrawTransport(playback, 0.78f);
            overlayHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
        Ui.PopStyle();
    }

    private static bool Project(Vector3 world, out Vector2 screen) => Plugin.GameGui.WorldToScreen(world, out screen);

    /// <summary>Point in a convex quad (corners in order).</summary>
    private static bool InQuad(Vector2 p, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        var s1 = Cross(b - a, p - a);
        var s2 = Cross(c - b, p - b);
        var s3 = Cross(d - c, p - c);
        var s4 = Cross(a - d, p - d);
        return (s1 >= 0 && s2 >= 0 && s3 >= 0 && s4 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0 && s4 <= 0);
    }

    private static float Cross(Vector2 a, Vector2 b) => (a.X * b.Y) - (a.Y * b.X);
}
