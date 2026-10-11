using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using ViewInFFXIV.Host;

namespace ViewInFFXIV.Windows;

/// <summary>The Watch tab while an app is shared on the TV, and the app list used to share one.</summary>
public sealed partial class MainWindow
{
    private List<WindowChoice> windowList = [];
    private DateTime windowsListedUtc = DateTime.MinValue;
    // The app list is a side option: folded away until you open it (this session only).
    private bool windowListOpen;

    private WindowShare WindowShare => plugin.WindowShare;

    private const string WindowNotes =
        "Your app stays where it is and shows on the TV just as it looks.\n" +
        "Keep it open: a minimised app doesn't show.\n" +
        "Browsers, Discord and similar apps pause while the game covers them; a second monitor helps.\n" +
        "The yellow outline around it just means it's being shared.\n" +
        "Some protected videos show black.\n" +
        "Its sound plays from the app, as usual.";

    /// <summary>The picture, which app it is, how it's doing (with the fix when it's paused), and the way back.</summary>
    private void DrawWindowShare()
    {
        var status = WindowShare.Status;
        var avail = ImGui.GetContentRegionAvail().X;
        DrawPicture(avail);
        using (titleFont.Push())
            ImGui.TextUnformatted(Ui.Fit(WindowShare.DisplayName, avail));
        if (status.Title.Length > 0 && status.Title != WindowShare.DisplayName)
            Ui.Dim(Ui.Fit(status.Title, avail));

        ImGui.Spacing();
        DrawWindowControls(plugin, 1f);
        DrawWatchingTip();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var width = ImGui.GetContentRegionAvail().X;
        if (WindowListHeader("Share another app", width))
            DrawWindowPicker(width, 180);
    }

    /// <summary>
    /// How the shared app is doing in one plain line, the one fix that helps when it's paused, and Stop (or back to
    /// the party). Shared with the mini remote.
    /// </summary>
    internal static void DrawWindowControls(Plugin plugin, float scale)
    {
        var share = plugin.WindowShare;
        var status = share.Status;
        var size = 34f * scale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var party = plugin.HighPerformance.Active && plugin.HighPerformance.Status.RoomUrl.Length > 0;
        var lineStart = ImGui.GetCursorPosX();
        var textWidth = ImGui.GetContentRegionAvail().X - size - spacing;

        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(lineStart + textWidth);
        if (status.Minimized)
        {
            StatusLine(Ui.Warn, "Paused: it's minimised.");
            if (status.Controllable)
            {
                if (Ui.AccentButton("Show it again"))
                    share.ShowAgain();
            }
            else
            {
                Ui.Dim("Click it on your taskbar to show it again.");
            }
        }
        else if (status.Paused)
        {
            StatusLine(Ui.Warn, status.CoveredByGame ? "Paused: the game is covering it." : "Paused: another window is covering it.");
            if (status.OtherMonitor && status.Controllable && (status.OnGameMonitor || !status.CoveredByGame))
            {
                if (Ui.AccentButton("Move it to my other monitor"))
                    share.MoveToOtherMonitor();
            }
            else if (status.OtherMonitor)
            {
                Ui.Dim("Drag it onto your other monitor, or keep part of it in view.");
            }
            else
            {
                Ui.Dim(status.CoveredByGame
                    ? "This kind of app pauses when it's hidden. Keep part of it in view, or play the game in a window."
                    : "This kind of app pauses when it's hidden. Keep part of it in view.");
            }
        }
        else
        {
            StatusLine(Ui.Good, "Showing on the TV.");
            // A small window makes a soft picture: one click makes it fill its screen (behind the game, out of your way).
            if (status.CanEnlarge && !status.Maximized && (status.Width < 1600 || status.Height < 900))
            {
                if (ImGui.SmallButton("Make it bigger for a sharper TV"))
                    share.MakeBigger();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(status.OnGameMonitor ? "It fills its screen, behind the game, out of your way." : "It fills its screen.");
            }
        }

        ImGui.PopTextWrapPos();
        ImGui.EndGroup();

        ImGui.SameLine(lineStart + textWidth + spacing);
        if (Ui.RoundIconButton("##winstop", party ? FontAwesomeIcon.Users : FontAwesomeIcon.Stop, size,
                tooltip: party ? "Back to the watch party" : "Stop sharing"))
            share.Stop();
    }

    private static void StatusLine(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted("●");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextWrapped(text);
    }

    /// <summary>Sharing a browser with YouTube or Twitch in it: point at the feature made for that.</summary>
    private void DrawWatchingTip()
    {
        if (WindowShare.WatchingSite() is not { } site)
            return;
        var inParty = Party.Active && Party.Status.RoomUrl.Length > 0;
        if (site.YouTube && inParty)
            return;

        ImGui.Spacing();
        Ui.Card("watchtip", _ =>
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Ui.TextDim);
            if (site.TwitchChannel is { } channel)
            {
                ImGui.TextWrapped($"Watching {channel} on Twitch? Twitch on the TV shows just the video, sharper, with sound from the TV.");
                ImGui.PopStyleColor();
                if (ImGui.Button($"Put {channel} on the TV"))
                    plugin.Twitch.Watch(channel);
            }
            else
            {
                ImGui.TextWrapped("Watching YouTube? A watch party plays it right on the TV, smoother, with sound from the TV, and friends can watch along in sync.");
                ImGui.PopStyleColor();
                if (ImGui.Button("Start a watch party"))
                    Party.StartParty();
            }
        }, color: Ui.Accent with { W = 0.10f });
    }

    /// <summary>A title row that folds the app list away; returns true while it is open.</summary>
    private bool WindowListHeader(string label, float width)
    {
        var pos = ImGui.GetCursorScreenPos();
        var height = ImGui.GetTextLineHeight() + 4;
        if (ImGui.InvisibleButton($"##{label}", new Vector2(width, height)))
            windowListOpen = !windowListOpen;
        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetTooltip(windowListOpen ? "Hide your apps" : "Show your apps");

        var dl = ImGui.GetWindowDrawList();
        dl.AddText(pos + new Vector2(0, 2), Ui.Col(hovered ? Ui.Text : Ui.Text with { W = 0.9f }), label);
        Ui.DrawIcon(dl, windowListOpen ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown,
            pos + new Vector2(width - (height / 2), height / 2), height * 0.55f, hovered ? Ui.Text : Ui.TextDim);
        return windowListOpen;
    }

    /// <summary>Your apps, newest first: the app's name with its window title under it. One click shares it.</summary>
    /// <returns>True when an app was shared.</returns>
    private bool DrawWindowPicker(float width, float listHeight)
    {
        if (DateTime.UtcNow - windowsListedUtc > TimeSpan.FromSeconds(2))
        {
            windowList = WindowShare.ListWindows();
            windowsListedUtc = DateTime.UtcNow;
        }

        Ui.Dim("It stays where it is on your screen.");
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - 24));
        ImGuiComponents.HelpMarker(WindowNotes);

        var started = false;
        using (var child = ImRaii.Child("windows", new Vector2(width, listHeight), true))
        {
            if (child.Success)
            {
                if (windowList.Count == 0)
                    Ui.Dim("No apps open. Open the one you want on the TV.");

                var current = WindowShare.Active ? WindowShare.Status.Hwnd : 0;
                var inner = ImGui.GetContentRegionAvail().X;
                var line = ImGui.GetTextLineHeight();
                var dl = ImGui.GetWindowDrawList();
                foreach (var choice in windowList)
                {
                    var pos = ImGui.GetCursorScreenPos();
                    var name = choice.App.Length > 0 ? choice.App : choice.Title;
                    if (ImGui.Selectable($"##w{choice.Hwnd}", choice.Hwnd == current, ImGuiSelectableFlags.None, new Vector2(inner, (line * 2) + 2))
                        && choice.Hwnd != current)
                    {
                        WindowShare.Share(choice);
                        started = true;
                    }

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Show {name} on the TV");
                    dl.AddText(pos, Ui.Col(Ui.Text), Ui.Fit(name, inner - 8));
                    if (choice.Title != name)
                        dl.AddText(pos + new Vector2(0, line + 1), Ui.Col(Ui.TextDim), Ui.Fit(choice.Title, inner - 8));
                }
            }
        }

        return started;
    }

    /// <summary>Under the Twitch card, folded by default: share one of your apps on the TV without a party.</summary>
    private void DrawWindowWelcome()
    {
        Ui.Card("window", width =>
        {
            if (!WindowListHeader("Share an app on the TV", width))
                return;
            ImGui.PushStyleColor(ImGuiCol.Text, Ui.TextDim);
            ImGui.TextWrapped("Pick an app (a video player, a browser, a second game) and it shows on the TV.");
            ImGui.PopStyleColor();
            ImGui.Spacing();
            DrawWindowPicker(width, 180);
        });
    }

    /// <summary>In a party: a button by the search box that opens the app list.</summary>
    private void DrawWindowPopupButton()
    {
        if (ImGuiComponents.IconButton("##windowopen", FontAwesomeIcon.Desktop))
        {
            windowsListedUtc = DateTime.MinValue;
            ImGui.OpenPopup("windowpick");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Share an app on the TV\n(the party carries on; you come back to it with one click)");

        using var popup = ImRaii.Popup("windowpick");
        if (!popup.Success)
            return;
        ImGui.TextUnformatted("Share an app on the TV");
        Ui.Dim("Just for you: friends keep watching the party.");
        if (DrawWindowPicker(360, 220))
            ImGui.CloseCurrentPopup();
    }
}
