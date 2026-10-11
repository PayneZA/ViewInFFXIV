using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace ViewInFFXIV.Windows;

/// <summary>
/// The screen "On UI": a 16:9 picture on your own HUD that goes wherever you go. Drag it to move, drag an edge to
/// resize; hovering it shows play/pause, the seek bar, the time, your volume, a lock and an X back to the wall.
/// </summary>
internal sealed class UiScreenWindow : Window
{
    private const ImGuiWindowFlags BaseFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoCollapse
        | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoDocking;

    private static readonly Vector2 DefaultSize = new(480, 270);

    private readonly Plugin plugin;
    private readonly PlayerControls controls;
    private bool resetRequested;
    private bool resetApplied;
    private float barAlpha;

    public UiScreenWindow(Plugin plugin)
        : base("##ViewInFFXIVUiScreen", BaseFlags)
    {
        this.plugin = plugin;
        controls = new PlayerControls(plugin);
        RespectCloseHotkey = false;
        ShowCloseButton = false;
        AllowPinning = false;
        AllowClickthrough = false;
        Size = DefaultSize;
        SizeCondition = ImGuiCond.FirstUseEver;
        PositionCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(240, 135),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    private Configuration Config => plugin.Configuration;

    /// <summary>Puts the screen back at its default size, bottom right, on the next frame.</summary>
    public void ResetPlacement() => resetRequested = true;

    public override void PreDraw()
    {
        Flags = Config.UiScreenLocked ? BaseFlags | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize : BaseFlags;

        // The first time it appears, and on Reset: bottom right, clear of the main window (top left by default).
        var viewport = ImGuiHelpers.MainViewport;
        Position = viewport.WorkPos + viewport.WorkSize - (DefaultSize * ImGuiHelpers.GlobalScale) - new Vector2(40, 120);
        if (resetRequested)
        {
            Size = DefaultSize;
            SizeCondition = ImGuiCond.Always;
            PositionCondition = ImGuiCond.Always;
            resetRequested = false;
            resetApplied = true;
        }
        else if (resetApplied)
        {
            SizeCondition = ImGuiCond.FirstUseEver;
            PositionCondition = ImGuiCond.FirstUseEver;
            resetApplied = false;
        }

        Ui.PushStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        Ui.PopStyle();
    }

    public override void Draw()
    {
        // The biggest 16:9 picture that fits, centred; the rest of the window stays see-through.
        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        var size = new Vector2(avail.X, avail.X * 9f / 16f);
        if (size.Y > avail.Y)
            size = new Vector2(avail.Y * 16f / 9f, avail.Y);
        var picture = origin + ((avail - size) / 2);
        const float rounding = 10f;

        ImGui.SetCursorScreenPos(picture);
        Ui.RoundedImage(plugin.Picture, size, rounding, FontAwesomeIcon.Tv);

        var dl = ImGui.GetWindowDrawList();
        // Twitch brings its own picture (or "offline" card) and has no player controls here.
        var playback = plugin.Twitch.Active ? null : plugin.HighPerformance.Playback;
        if (playback == null && !plugin.Twitch.Active)
        {
            var hint = Ui.Fit("Nothing playing yet", size.X - 16);
            var textSize = ImGui.CalcTextSize(hint);
            dl.AddText(picture + new Vector2((size.X - textSize.X) / 2, (size.Y * 0.5f) + MathF.Min(size.Y * 0.3f, 40f) / 2 + 8), Ui.Col(Ui.TextDim), hint);
        }

        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var target = hovered || controls.Interacting ? 1f : 0f;
        barAlpha = Math.Clamp(barAlpha + (MathF.Sign(target - barAlpha) * ImGui.GetIO().DeltaTime * 6f), 0f, 1f);
        if (barAlpha <= 0f)
            return;

        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, barAlpha);

        // Lock and "back to the wall" in the top-right corner.
        const float cornerSize = 24f;
        ImGui.SetCursorScreenPos(picture + new Vector2(size.X - cornerSize - 8, 8));
        if (Ui.RoundIconButton("##close", FontAwesomeIcon.Times, cornerSize, tooltip: "Back to the wall (In game)"))
            plugin.SetScreenMode(ScreenMode.InGame);

        ImGui.SetCursorScreenPos(picture + new Vector2(size.X - (2 * cornerSize) - 14, 8));
        if (Ui.RoundIconButton("##lock", Config.UiScreenLocked ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen, cornerSize,
                tooltip: Config.UiScreenLocked ? "Unlock (move and resize)" : "Lock in place"))
        {
            Config.UiScreenLocked = !Config.UiScreenLocked;
            Config.Save();
        }

        // Play/pause and the seek bar over the bottom of the picture.
        if (playback != null)
        {
            const float pad = 8f;
            const float barHeight = 28f + (2 * pad);
            var barTop = picture + new Vector2(0, size.Y - barHeight);
            dl.AddRectFilled(barTop, picture + size, Ui.Col(new Vector4(0, 0, 0, 0.55f)), rounding, ImDrawFlags.RoundCornersBottom);
            ImGui.SetCursorScreenPos(barTop + new Vector2(pad, pad));
            controls.DrawMini(playback, size.X - (2 * pad));
        }

        ImGui.PopStyleVar();
    }
}
