using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using ViewInFFXIV.Media;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Windows;

/// <summary>Seek bar and transport buttons, shared by the main window and the screen on your UI.</summary>
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
        var duration = SeekBar(playback, width);
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
        var barWidth = 80f * scale;
        var labelWidth = ImGui.CalcTextSize("100%").X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SameLine();
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - (2 * small) - barWidth - labelWidth - (3 * spacing);
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), right));
        ImGui.SetCursorPosY(y + ((big - small) / 2));

        var volume = MuteButton(playback, small);
        ImGui.SameLine();
        ImGui.SetCursorPosY(y + ((big - Ui.SeekBarHeight) / 2));
        VolumeBar(volume, barWidth);
        ImGui.SameLine();
        var labelX = ImGui.GetCursorPosX();
        ImGui.SetCursorPosY(y + ((big - ImGui.GetTextLineHeight()) / 2));
        Ui.Dim($"{volume}%");

        ImGui.SameLine(labelX + labelWidth + spacing);
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

    /// <summary>
    /// Play/pause, the seek bar, the time and your volume on one line, for the screen on your UI. The volume bar
    /// only fits a wide picture; the mute button is always there.
    /// </summary>
    public void DrawMini(PlaybackStatus playback, float width)
    {
        const float button = 28f;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var y = ImGui.GetCursorPosY();
        if (Ui.RoundIconButton("##miniplay", playback.Paused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause, button, accent: true, tooltip: playback.Paused ? "Play for everyone" : "Pause for everyone"))
            plugin.HighPerformance.Control(playback.Paused ? "play" : "pause");

        var duration = Math.Max(1, playback.Duration);
        var position = seekDrag is { } d ? d * duration : playback.Position;
        var time = playback.Duration > 0 ? $"{WatchQueue.Clock(position)} / {WatchQueue.Clock(duration)}" : "live";
        var timeWidth = ImGui.CalcTextSize(time).X;
        var barWidth = width >= 420 ? 64f : 0f;
        var rightWidth = timeWidth + spacing + button + (barWidth > 0 ? spacing + barWidth : 0);

        ImGui.SameLine();
        ImGui.SetCursorPosY(y + ((button - Ui.SeekBarHeight) / 2));
        SeekBar(playback, MathF.Max(20, width - button - rightWidth - (2 * spacing)));
        ImGui.SameLine();
        ImGui.SetCursorPosY(y + ((button - ImGui.GetTextLineHeight()) / 2));
        ImGui.TextUnformatted(time);
        ImGui.SameLine();
        ImGui.SetCursorPosY(y);
        var volume = MuteButton(playback, button);
        if (barWidth > 0)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(y + ((button - Ui.SeekBarHeight) / 2));
            VolumeBar(volume, barWidth);
        }
    }

    /// <summary>Mute/unmute (just you). Returns the volume to show: the one being dragged or sent, else the player's.</summary>
    private int MuteButton(PlaybackStatus playback, float size)
    {
        if (pendingVolume is { } pv && (playback.Volume == pv || DateTime.UtcNow > pendingVolumeUntil))
            pendingVolume = null;
        var volume = volumeDrag ?? pendingVolume ?? playback.Volume;
        if (volume > 0)
            lastVolume = volume;
        var icon = volume == 0 ? FontAwesomeIcon.VolumeMute : volume < 50 ? FontAwesomeIcon.VolumeDown : FontAwesomeIcon.VolumeUp;
        if (Ui.RoundIconButton("##mute", icon, size, tooltip: volume == 0 ? "Unmute" : "Mute (just you)"))
            SetVolume(volume == 0 ? Math.Max(10, lastVolume) : 0);
        return volume;
    }

    /// <summary>Your Twitch volume (just you): mute button, bar and percentage on one line; set when you let go.</summary>
    public void DrawStreamVolume(float size, float barWidth)
    {
        var twitch = plugin.Twitch;
        var volume = volumeDrag ?? plugin.Configuration.TwitchVolume;
        if (volume > 0)
            lastVolume = volume;
        var y = ImGui.GetCursorPosY();
        var icon = volume == 0 ? FontAwesomeIcon.VolumeMute : volume < 50 ? FontAwesomeIcon.VolumeDown : FontAwesomeIcon.VolumeUp;
        if (Ui.RoundIconButton("##tmute", icon, size, tooltip: volume == 0 ? "Unmute" : "Mute (just you)"))
            twitch.SetVolume(volume == 0 ? Math.Max(10, lastVolume) : 0);

        ImGui.SameLine();
        ImGui.SetCursorPosY(y + ((size - Ui.SeekBarHeight) / 2));
        float? drag = volumeDrag / 100f;
        var released = Ui.SeekBar("##tvol", volume / 100f, barWidth, ref drag, out var hover);
        volumeDrag = drag is { } d ? (int)MathF.Round(d * 100) : null;
        if (released && volumeDrag is { } dragged)
        {
            volumeDrag = null;
            twitch.SetVolume(dragged);
        }

        if (hover is { } h)
            ImGui.SetTooltip($"Your Twitch volume (just you): {(int)MathF.Round(h * 100)}%");
        ImGui.SameLine();
        ImGui.SetCursorPosY(y + ((size - ImGui.GetTextLineHeight()) / 2));
        Ui.Dim($"{volume}%");
    }

    /// <summary>Your volume, drawn like the seek bar; it is set when you let go.</summary>
    private void VolumeBar(int volume, float width)
    {
        float? drag = volumeDrag / 100f;
        var released = Ui.SeekBar("##vol", volume / 100f, width, ref drag, out var hover);
        volumeDrag = drag is { } d ? (int)MathF.Round(d * 100) : null;
        if (released && volumeDrag is { } dragged)
        {
            volumeDrag = null;
            SetVolume(dragged);
        }

        if (hover is { } h)
            ImGui.SetTooltip($"Your volume (just you): {(int)MathF.Round(h * 100)}%");
    }

    /// <summary>The seek bar with its time tooltip; seeks for everyone on release. Returns the duration used.</summary>
    private double SeekBar(PlaybackStatus playback, float width)
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
        return duration;
    }

    private void SetVolume(int volume)
    {
        pendingVolume = volume;
        pendingVolumeUntil = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        plugin.HighPerformance.Volume(volume);
    }
}
