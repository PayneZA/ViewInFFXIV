using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using ViewInFFXIV.Host;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Windows;

/// <summary>The Watch tab while Twitch is on the TV, and the channel picker used to put it there.</summary>
public sealed partial class MainWindow
{
    private string twitchInput = "";

    private TwitchStream Twitch => plugin.Twitch;

    /// <summary>Twitch's picture, what is on, pop out/in, your volume, and the way back.</summary>
    private void DrawTwitch()
    {
        var status = Twitch.Status;
        var avail = ImGui.GetContentRegionAvail().X;
        DrawPicture(avail);
        if (Twitch.Starting)
        {
            ImGui.Spacing();
            Ui.Centered((status.Activity.Length > 0 ? status.Activity : "Opening Twitch") + "…", Ui.TextDim);
        }
        else
        {
            using (titleFont.Push())
                ImGui.TextUnformatted(Ui.Fit(Twitch.DisplayName, avail));
            var meta = new List<string>();
            if (status.Offline)
                meta.Add("offline");
            else if (status.Title.Length > 0)
                meta.Add(status.Title);
            if (status.Game.Length > 0 && !status.Offline)
                meta.Add(status.Game);
            if (status.VideoHeight > 0 && status.Playing)
                meta.Add($"{status.VideoHeight}p");
            Ui.Dim(Ui.Fit(meta.Count > 0 ? string.Join("  ·  ", meta) : "Twitch", avail));
        }

        if (status.Stuck)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Ui.Warn);
            ImGui.TextWrapped("Live, but no picture. Twitch may be asking something on its page: pop out, answer it, then pop back in.");
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        DrawTwitchButtons(1f);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Another channel");
        DrawTwitchPicker(ImGui.GetContentRegionAvail().X, showRecent: true);
    }

    /// <summary>Pop out/in, your Twitch volume, and stop (or back to the party). Shared with the mini remote.</summary>
    internal static void DrawTwitchButtons(Plugin plugin, PlayerControls controls, float scale)
    {
        var twitch = plugin.Twitch;
        var status = twitch.Status;
        var size = 34f * scale;
        var shown = status.Shown;
        if (Ui.RoundIconButton("##pop", shown ? FontAwesomeIcon.CompressAlt : FontAwesomeIcon.ExternalLinkAlt, size, accent: shown || status.Stuck,
                tooltip: shown ? "Pop in: back off your screen, only the video on the TV" : "Pop out: show Twitch's page on your monitor\n(for anything on the page that wants a click)"))
            twitch.TogglePop();

        ImGui.SameLine();
        controls.DrawStreamVolume(size, 80f * scale);

        var party = plugin.HighPerformance.Active && plugin.HighPerformance.Status.RoomUrl.Length > 0;
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - size));
        if (Ui.RoundIconButton("##twitchstop", party ? FontAwesomeIcon.Users : FontAwesomeIcon.Stop, size,
                tooltip: party ? "Back to the watch party" : "Stop Twitch"))
            twitch.Stop();
    }

    private void DrawTwitchButtons(float scale) => DrawTwitchButtons(plugin, controls, scale);

    /// <summary>The TV picture, as large as fits.</summary>
    private void DrawPicture(float avail)
    {
        var maxHeight = MathF.Max(160, ImGui.GetWindowHeight() * 0.42f);
        var size = new Vector2(avail, avail * 9f / 16f);
        if (size.Y > maxHeight)
            size = new Vector2(maxHeight * 16f / 9f, maxHeight);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((avail - size.X) / 2));
        Ui.RoundedImage(plugin.Picture, size, 10f, FontAwesomeIcon.Tv);
    }

    /// <summary>Channel name box with Watch, and your recent channels as one-click buttons.</summary>
    /// <returns>True when a channel was put on the TV.</returns>
    private bool DrawTwitchPicker(float width, bool showRecent)
    {
        var started = false;
        ImGui.SetNextItemWidth(width - 80);
        var enter = ImGui.InputTextWithHint("##twitch", "Twitch channel or twitch.tv link", ref twitchInput, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Watch", new Vector2(72, 0)) || enter) && twitchInput.Trim().Length > 0 && Twitch.Watch(twitchInput))
        {
            twitchInput = "";
            started = true;
        }

        var recent = plugin.Configuration.TwitchRecent;
        if (!showRecent || recent.Count == 0)
            return started;

        var current = Twitch.Active ? Twitch.Status.Channel : "";
        var right = ImGui.GetCursorPosX() + width;
        var first = true;
        foreach (var channel in recent.ToList())
        {
            if (channel == current)
                continue;
            var buttonWidth = ImGui.CalcTextSize(channel).X + (ImGui.GetStyle().FramePadding.X * 2);
            if (!first)
            {
                ImGui.SameLine();
                if (ImGui.GetCursorPosX() + buttonWidth > right)
                    ImGui.NewLine();
            }

            first = false;
            if (ImGui.Button($"{channel}##recent"))
                started |= Twitch.Watch(channel);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Put {channel} on the TV");
        }

        return started;
    }

    /// <summary>Under the welcome card: put a Twitch channel on the TV without a party.</summary>
    private void DrawTwitchWelcome()
    {
        Ui.Card("twitch", width =>
        {
            ImGui.TextUnformatted("Twitch on the TV");
            ImGui.PushStyleColor(ImGuiCol.Text, Ui.TextDim);
            ImGui.TextWrapped("Type a channel and it plays on your screen, video only. No account needed.");
            ImGui.PopStyleColor();
            ImGui.Spacing();
            DrawTwitchPicker(width, showRecent: true);
        });
    }

    /// <summary>In a party: a button by the search box that opens the channel picker.</summary>
    private void DrawTwitchPopupButton()
    {
        if (ImGuiComponents.IconButton("##twitchopen", FontAwesomeIcon.BroadcastTower))
            ImGui.OpenPopup("twitchpick");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Put a Twitch channel on the TV\n(the party carries on; you come back to it with one click)");

        using var popup = Dalamud.Interface.Utility.Raii.ImRaii.Popup("twitchpick");
        if (!popup.Success)
            return;
        ImGui.TextUnformatted("Twitch on the TV");
        Ui.Dim("Just for you: friends keep watching the party.");
        if (DrawTwitchPicker(320, showRecent: true))
            ImGui.CloseCurrentPopup();
    }
}
