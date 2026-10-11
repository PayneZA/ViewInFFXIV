using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace ViewInFFXIV.Windows;

/// <summary>
/// The mini remote: the title, seek bar and transport in a small bar you drag wherever it doesn't get in the way.
/// It is only there during a party or Twitch, and it fades to faint while the mouse is elsewhere. Shown or hidden with the
/// remote button in the main window, /matinee remote, Settings, or its own X.
/// </summary>
internal sealed class RemoteWindow : Window
{
    private const float Width = 460f;
    private const float IdleAlpha = 0.35f;

    private readonly Plugin plugin;
    private readonly PlayerControls controls;
    private float height = 120f;
    private float alpha = 1f;
    private bool hovered;

    public RemoteWindow(Plugin plugin)
        : base("##ViewInFFXIVRemote", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoDocking)
    {
        this.plugin = plugin;
        controls = new PlayerControls(plugin);
        RespectCloseHotkey = false;
        ShowCloseButton = false;
        AllowPinning = false;
        AllowClickthrough = false;
        PositionCondition = ImGuiCond.FirstUseEver;
        SizeCondition = ImGuiCond.Always;
    }

    public override void PreDraw()
    {
        // First time: bottom centre, above the hotbars. After that it stays where you put it.
        var viewport = ImGuiHelpers.MainViewport;
        Position = viewport.WorkPos + new Vector2((viewport.WorkSize.X - (Width * ImGuiHelpers.GlobalScale)) / 2, viewport.WorkSize.Y - 320);
        Size = new Vector2(Width, height);

        var target = hovered || controls.Interacting ? 1f : IdleAlpha;
        alpha += (target - alpha) * MathF.Min(1f, ImGui.GetIO().DeltaTime * (target > alpha ? 14f : 4f));

        Ui.PushStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 10));
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, alpha);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(2);
        Ui.PopStyle();
    }

    public override void Draw()
    {
        var min = ImGui.GetWindowPos();
        ImGui.GetWindowDrawList().AddRectFilled(min, min + ImGui.GetWindowSize(), Ui.Col(Ui.Background with { W = 0.92f }), 12f);

        var inner = ImGui.GetContentRegionAvail().X;
        var left = ImGui.GetCursorPosX();
        var twitch = plugin.Twitch;
        var window = plugin.WindowShare;
        var playback = twitch.Active || window.Active ? null : plugin.HighPerformance.Playback;
        if (window.Active)
            ImGui.TextUnformatted(Ui.Fit($"Sharing · {window.DisplayName}", inner - 64));
        else if (twitch.Active)
            ImGui.TextUnformatted(Ui.Fit(twitch.Status.Offline ? $"{twitch.DisplayName} (offline)" : $"Twitch · {twitch.DisplayName}", inner - 64));
        else if (playback == null)
            Ui.Dim("Nothing playing yet");
        else
            ImGui.TextUnformatted(Ui.Fit(playback.Title, inner - 64));
        ImGui.SameLine(left + inner - 54);
        if (Ui.RoundIconButton("##window", FontAwesomeIcon.Tv, 24f, tooltip: "Open Matinee (/matinee)"))
            plugin.ToggleMainUi();
        ImGui.SameLine(left + inner - 24);
        if (Ui.RoundIconButton("##close", FontAwesomeIcon.Times, 24f, tooltip: "Hide the remote\n(show it again from /matinee or /matinee remote)"))
        {
            plugin.Configuration.RemoteShown = false;
            plugin.Configuration.Save();
        }

        if (window.Active)
        {
            MainWindow.DrawWindowControls(plugin, 0.85f);
        }
        else if (twitch.Active)
        {
            if (twitch.Status.Title.Length > 0 && !twitch.Status.Offline)
                Ui.Dim(Ui.Fit(twitch.Status.Title, inner));
            MainWindow.DrawTwitchButtons(plugin, controls, 0.85f);
        }
        else if (playback != null)
        {
            controls.DrawSeek(playback, inner);
            controls.DrawTransport(playback, 0.85f);
        }

        // Next frame's height fits what was drawn, whatever the font size.
        height = (ImGui.GetCursorPosY() + 4) / ImGuiHelpers.GlobalScale;
        hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
    }
}
