using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using ViewInFFXIV.Host;

namespace ViewInFFXIV.Windows;

/// <summary>The Watch tab while one of your windows is on the TV, and the window list used to put it there.</summary>
public sealed partial class MainWindow
{
    private List<WindowChoice> windowList = [];
    private DateTime windowsListedUtc = DateTime.MinValue;
    // The window list is a side option: folded away until you open it (this session only).
    private bool windowListOpen;

    private WindowShare WindowShare => plugin.WindowShare;

    private const string WindowNotes =
        "Windows draws a yellow border around a window while it is shared; that's normal.\n" +
        "Protected video (some streaming sites) shows black.\n" +
        "The app's own sound plays as usual.";

    /// <summary>The window's picture, what it is, pop off / put back, and the way back.</summary>
    private void DrawWindowShare()
    {
        var status = WindowShare.Status;
        var avail = ImGui.GetContentRegionAvail().X;
        DrawPicture(avail);
        using (titleFont.Push())
            ImGui.TextUnformatted(Ui.Fit(WindowShare.DisplayName, avail));
        var meta = new List<string>();
        if (status.Process.Length > 0)
            meta.Add(status.Process);
        if (status.Width > 0 && status.Height > 0)
            meta.Add($"{status.Width}x{status.Height}");
        meta.Add(status.Popped ? "off screen" : "on your monitor");
        Ui.Dim(Ui.Fit(string.Join("  ·  ", meta), avail));

        if (status.Stalled || status.Minimized)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Ui.Warn);
            ImGui.TextWrapped(status.Stalled
                ? "It stopped drawing off screen: this app pauses while it can't be seen. Put it back to keep it on your monitor."
                : "It's minimised, and Windows doesn't draw minimised windows. Restore it, or pop it off.");
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        DrawWindowButtons(plugin, 1f);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var width = ImGui.GetContentRegionAvail().X;
        if (WindowListHeader("Another window", width))
            DrawWindowPicker(width, 150);
    }

    /// <summary>A title row that folds the window list away; returns true while it is open.</summary>
    private bool WindowListHeader(string label, float width)
    {
        var pos = ImGui.GetCursorScreenPos();
        var height = ImGui.GetTextLineHeight() + 4;
        if (ImGui.InvisibleButton($"##{label}", new Vector2(width, height)))
            windowListOpen = !windowListOpen;
        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetTooltip(windowListOpen ? "Hide your windows" : "Show your windows");

        var dl = ImGui.GetWindowDrawList();
        dl.AddText(pos + new Vector2(0, 2), Ui.Col(hovered ? Ui.Text : Ui.Text with { W = 0.9f }), label);
        Ui.DrawIcon(dl, windowListOpen ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown,
            pos + new Vector2(width - (height / 2), height / 2), height * 0.55f, hovered ? Ui.Text : Ui.TextDim);
        return windowListOpen;
    }

    /// <summary>Pop off / put back, and stop (or back to the party). Shared with the mini remote.</summary>
    internal static void DrawWindowButtons(Plugin plugin, float scale)
    {
        var share = plugin.WindowShare;
        var status = share.Status;
        var size = 34f * scale;
        var popped = status.Popped;
        if (Ui.RoundIconButton("##winpop", popped ? FontAwesomeIcon.WindowRestore : FontAwesomeIcon.SignOutAlt, size, accent: popped || status.Stalled,
                tooltip: !status.CanPopOff ? "FFXIV windows stay where they are"
                    : popped ? "Put back: on your monitor again, where it was"
                    : "Pop off: off your screen at the TV's size\n(it keeps drawing; click its taskbar button to get it back)",
                enabled: status.CanPopOff))
            share.TogglePopOff();

        var party = plugin.HighPerformance.Active && plugin.HighPerformance.Status.RoomUrl.Length > 0;
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - size));
        if (Ui.RoundIconButton("##winstop", party ? FontAwesomeIcon.Users : FontAwesomeIcon.Stop, size,
                tooltip: party ? "Back to the watch party" : "Stop showing the window"))
            share.Stop();
    }

    /// <summary>"Pop it off screen", and your windows; one click puts one on the TV.</summary>
    /// <returns>True when a window was put on the TV.</returns>
    private bool DrawWindowPicker(float width, float listHeight)
    {
        if (DateTime.UtcNow - windowsListedUtc > TimeSpan.FromSeconds(2))
        {
            windowList = WindowShare.ListWindows();
            windowsListedUtc = DateTime.UtcNow;
        }

        var popOff = plugin.Configuration.WindowPopOff;
        if (ImGui.Checkbox("Pop it off screen", ref popOff))
        {
            plugin.Configuration.WindowPopOff = popOff;
            plugin.Configuration.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Moves the window off your monitors at exactly the TV's size, so you don't have to size or hide it.\nIt goes back where it was when you stop. FFXIV windows always stay put.");
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - 24));
        ImGuiComponents.HelpMarker(WindowNotes);

        var started = false;
        using (var child = ImRaii.Child("windows", new Vector2(width, listHeight), true))
        {
            if (child.Success)
            {
                if (windowList.Count == 0)
                    Ui.Dim("No windows to show. Open the app you want on the TV.");

                var current = WindowShare.Active ? WindowShare.Status.Hwnd : 0;
                var inner = ImGui.GetContentRegionAvail().X;
                foreach (var choice in windowList)
                {
                    var app = choice.Process.Length > 0 ? choice.Process : "?";
                    var appWidth = ImGui.CalcTextSize(app).X;
                    var start = ImGui.GetCursorPosX();
                    if (ImGui.Selectable($"{Ui.Fit(choice.Title, inner - appWidth - 16)}##w{choice.Hwnd}", choice.Hwnd == current) && choice.Hwnd != current)
                    {
                        WindowShare.Share(choice);
                        started = true;
                    }

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(choice.CanPopOff ? $"Put {choice.Title} on the TV" : $"Put {choice.Title} on the TV\n(FFXIV windows are never popped off screen)");
                    ImGui.SameLine(start + inner - appWidth);
                    Ui.Dim(app);
                }
            }
        }

        return started;
    }

    /// <summary>Under the Twitch card, folded by default: put one of your windows on the TV without a party.</summary>
    private void DrawWindowWelcome()
    {
        Ui.Card("window", width =>
        {
            if (!WindowListHeader("A window on the TV", width))
                return;
            ImGui.PushStyleColor(ImGuiCol.Text, Ui.TextDim);
            ImGui.TextWrapped("Pick any of your windows (a video player, a browser, another game) and it fills the screen, aspect ratio kept.");
            ImGui.PopStyleColor();
            ImGui.Spacing();
            DrawWindowPicker(width, 150);
        });
    }

    /// <summary>In a party: a button by the search box that opens the window list.</summary>
    private void DrawWindowPopupButton()
    {
        if (ImGuiComponents.IconButton("##windowopen", FontAwesomeIcon.Desktop))
        {
            windowsListedUtc = DateTime.MinValue;
            ImGui.OpenPopup("windowpick");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Put one of your windows on the TV\n(the party carries on; you come back to it with one click)");

        using var popup = ImRaii.Popup("windowpick");
        if (!popup.Success)
            return;
        ImGui.TextUnformatted("A window on the TV");
        Ui.Dim("Just for you: friends keep watching the party.");
        if (DrawWindowPicker(360, 200))
            ImGui.CloseCurrentPopup();
    }
}
