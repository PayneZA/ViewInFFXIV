using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using ViewInFFXIV.Media;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Windows;

/// <summary>Seek bar and transport buttons, shared by the main window and the controls on the TV.</summary>
internal sealed class PlayerControls
{
    private readonly Plugin plugin;
    private float? seekDrag;
    private int? volumeDrag;
    private int? pendingVolume;
    private DateTime pendingVolumeUntil;
    private bool? pendingSubtitles;
    private DateTime pendingSubtitlesUntil;
    private int lastVolume = 100;

    public PlayerControls(Plugin plugin)
    {
        this.plugin = plugin;
    }

    /// <summary>True while the user is dragging the seek bar or the volume.</summary>
    public bool Interacting => seekDrag != null || volumeDrag != null;

    public void DrawSeek(PlaybackStatus playback, float width)
    {
        var duration = Math.Max(1, playback.Duration);
        var fraction = (float)(playback.Position / duration);
        if (Ui.SeekBar("##seek", fraction, width, ref seekDrag, out var hover))
        {
            plugin.HighPerformance.Control(PlaybackCommands.Seek(seekDrag!.Value * duration));
            seekDrag = null;
        }

        if (hover is { } h)
            ImGui.SetTooltip(WatchQueue.Clock(h * duration));

        var shown = seekDrag is { } d ? d * duration : playback.Position;
        var start = ImGui.GetCursorPosX();
        Ui.Dim(WatchQueue.Clock(shown));
        var total = playback.Duration > 0 ? WatchQueue.Clock(duration) : "live";
        ImGui.SameLine(start + width - ImGui.CalcTextSize(total).X);
        Ui.Dim(total);
    }

    /// <param name="scale">1 for the main window, smaller for the TV overlay.</param>
    public void DrawTransport(PlaybackStatus playback, float scale = 1f)
    {
        var small = 34f * scale;
        var big = 48f * scale;
        var party = plugin.HighPerformance;
        var queue = plugin.Queue;

        var y = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(y + ((big - small) / 2));
        if (Ui.RoundIconButton("##back", FontAwesomeIcon.UndoAlt, small, tooltip: "Back 10 s"))
            party.Control(PlaybackCommands.Skip(-10));
        ImGui.SameLine();
        ImGui.SetCursorPosY(y);
        // Send what the button shows, not a toggle: a friend may have paused a moment ago.
        if (Ui.RoundIconButton("##play", playback.Paused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause, big, accent: true, tooltip: playback.Paused ? "Play for everyone" : "Pause for everyone"))
            party.Control(playback.Paused ? "play" : "pause");
        ImGui.SameLine();
        ImGui.SetCursorPosY(y + ((big - small) / 2));
        if (Ui.RoundIconButton("##fwd", FontAwesomeIcon.RedoAlt, small, tooltip: "Forward 10 s"))
            party.Control(PlaybackCommands.Skip(10));
        ImGui.SameLine();
        ImGui.SetCursorPosY(y + ((big - small) / 2));
        var next = queue.UpNext.Count > 0 ? queue.UpNext[0] : null;
        if (Ui.RoundIconButton("##next", FontAwesomeIcon.StepForward, small, tooltip: next != null ? $"Next: {next.Title}" : "Up next is empty", enabled: next != null))
            queue.Next();

        // Volume and subtitles are yours only: on the right.
        var volumeWidth = 110f * scale;
        ImGui.SameLine();
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - volumeWidth - (2 * small) - 16;
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), right));
        ImGui.SetCursorPosY(y + ((big - small) / 2));

        if (pendingVolume is { } pv && (playback.Volume == pv || DateTime.UtcNow > pendingVolumeUntil))
            pendingVolume = null;
        var volume = volumeDrag ?? pendingVolume ?? playback.Volume;
        if (volume > 0)
            lastVolume = volume;
        var volumeIcon = volume == 0 ? FontAwesomeIcon.VolumeMute : volume < 50 ? FontAwesomeIcon.VolumeDown : FontAwesomeIcon.VolumeUp;
        if (Ui.RoundIconButton("##mute", volumeIcon, small, tooltip: volume == 0 ? "Unmute" : "Mute (just you)"))
            SetVolume(volume == 0 ? Math.Max(10, lastVolume) : 0);
        ImGui.SameLine();
        ImGui.SetCursorPosY(y + ((big - ImGui.GetFrameHeight() + 6) / 2));
        ImGui.SetNextItemWidth(volumeWidth);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6, 3));
        if (ImGui.SliderInt("##vol", ref volume, 0, 100, "%d%%"))
            volumeDrag = volume;
        ImGui.PopStyleVar();
        if (ImGui.IsItemDeactivated() && volumeDrag is { } dragged)
        {
            volumeDrag = null;
            SetVolume(dragged);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Your volume (not synced)");

        ImGui.SameLine();
        ImGui.SetCursorPosY(y + ((big - small) / 2));
        if (pendingSubtitles is { } ps && (playback.Subtitles == ps || DateTime.UtcNow > pendingSubtitlesUntil))
            pendingSubtitles = null;
        var subtitles = pendingSubtitles ?? playback.Subtitles;
        var canToggle = subtitles || playback.SubtitlesAvailable;
        var ccTip = canToggle ? subtitles ? "Subtitles off" : "English subtitles (just you)" : "No subtitles on this video";
        if (Ui.RoundIconButton("##cc", FontAwesomeIcon.ClosedCaptioning, small, accent: subtitles, tooltip: ccTip, enabled: canToggle))
        {
            pendingSubtitles = !subtitles;
            pendingSubtitlesUntil = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            party.Subtitles(!subtitles);
        }

        ImGui.SetCursorPosY(y + big + ImGui.GetStyle().ItemSpacing.Y);
    }

    private void SetVolume(int volume)
    {
        pendingVolume = volume;
        pendingVolumeUntil = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        plugin.HighPerformance.Volume(volume);
    }
}
