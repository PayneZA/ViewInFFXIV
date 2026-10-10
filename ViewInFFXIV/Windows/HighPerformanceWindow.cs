using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ViewInFFXIV.Host;
using ViewInFFXIV.Shared;
using ViewInFFXIV.World;

namespace ViewInFFXIV.Windows;

public sealed class HighPerformanceWindow : Window
{
    private readonly Plugin plugin;
    private string joinInput = "";
    private string playInput = "";
    private bool seeking;
    private float seekTarget;
    private bool volumeDragging;
    private int volumeDrag;
    private int? pendingVolume;
    private DateTime pendingUntil;
    private bool? pendingSubtitles;
    private DateTime pendingSubtitlesUntil;

    public HighPerformanceWindow(Plugin plugin)
        : base("ViewInFFXIV##ViewInFFXIVHighPerformance")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(540, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var hp = plugin.HighPerformance;
        var status = hp.Status;
        var active = hp.Active;

        ImGui.TextUnformatted($"Brave: {(plugin.Host.HelperAlive ? status.State : "off")}");
        if (!string.IsNullOrEmpty(status.Activity))
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"— {status.Activity}…");
        }

        if (!string.IsNullOrEmpty(status.Error))
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), status.Error);
        if (!string.IsNullOrEmpty(plugin.Host.Status.Error))
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), plugin.Host.Status.Error);

        if (!active)
        {
            if (ImGui.Button("Start"))
                hp.Start();
        }
        else
        {
            if (ImGui.Button("Stop"))
                hp.Stop();
            ImGui.SameLine();
            if (status.Shown ? ImGui.Button("Hide browser") : ImGui.Button("Show browser"))
            {
                if (status.Shown)
                    hp.Hide();
                else
                    hp.Show();
            }
        }

        ImGui.TextDisabled("Runs its own Brave off-screen (audio plays from it). Minimising it is safe.");

        ImGui.Separator();
        ImGui.TextUnformatted("w2g room");
        if (status.RoomUrl.Length > 0)
        {
            ImGui.TextUnformatted(status.RoomUrl);
            ImGui.SameLine();
            if (ImGui.Button("Copy invite"))
                hp.CopyInvite();
        }
        else
        {
            ImGui.TextDisabled("Not in a room.");
        }

        if (ImGui.Button("Create room"))
            hp.CreateRoom();

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.72f);
        ImGui.InputTextWithHint("##join", "https://w2g.tv/?r=…", ref joinInput, 512);
        ImGui.SameLine();
        if (ImGui.Button("Join"))
            hp.Join(joinInput);

        if (!active)
            ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.72f);
        ImGui.InputTextWithHint("##play", "YouTube / video link", ref playInput, 512);
        ImGui.SameLine();
        if (ImGui.Button("Play"))
            hp.Play(playInput);
        if (!active)
            ImGui.EndDisabled();

        DrawPlayback(hp);

        ImGui.Separator();
        ImGui.TextUnformatted($"Name in w2g: {hp.ResolveName() ?? "(log in to a character)"}");
        ImGui.TextDisabled("Always your character name. Everyone with the room link sees it.");

        ImGui.Separator();
        if (ImGui.Button("Screen placement…"))
            plugin.TogglePlacementUi();
        ImGui.SameLine();
        var config = plugin.Configuration;
        var screen = config.ScreenEnabled;
        if (ImGui.Checkbox("Show screen", ref screen))
        {
            config.ScreenEnabled = screen;
            if (screen && !config.HasAnchor)
            {
                plugin.Placement.PlaceInFront(Plugin.ClientState, Plugin.ObjectTable, config);
            }
            else
            {
                // Store it in this zone's saved spot too, or re-entering the zone brings the old value back.
                if (Plugin.ClientState.TerritoryType != 0)
                    PlacementPresets.SaveActivePreset(config, Plugin.ClientState.TerritoryType);
                config.Save();
            }
        }

        DrawVideoStatus();
    }

    private void DrawVideoStatus()
    {
        var host = plugin.Host.Status;
        if (!plugin.Host.HelperAlive || host.SharedTexture.Length == 0)
            return;

        var video = plugin.Video;
        if (video.Error != null)
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), video.Error);
        ImGui.TextDisabled(
            $"Wall: {video.Fps:0} fps · Brave {host.Fps:0} fps · {video.Width}x{video.Height} · {(plugin.Renderer.UsingOverlay ? plugin.Renderer.DrawingBehindUi ? "Overlay, behind UI" : "Overlay" : "fallback draw")}");
        ImGui.TextDisabled(
            $"Game {ImGui.GetIO().Framerate:0} fps · captured {host.ArrivedFps:0} fps, dropped {host.SkippedFps:0}/s (game still held the last frame)");
        if (plugin.HighPerformance.Playback is { } pb)
        {
            var dropped = pb.DecodedFrames > 0 ? 100f * pb.DroppedFrames / pb.DecodedFrames : 0f;
            ImGui.TextDisabled(
                $"Decode: {(pb.Codec.Length > 0 ? pb.Codec : "unknown codec")} · dropped {pb.DroppedFrames} of {pb.DecodedFrames} ({dropped:0.#}%)");
        }
        if (video.Handle is { } image && video.Height > 0)
        {
            var w = MathF.Min(ImGui.GetContentRegionAvail().X, 320f);
            ImGui.Image(image, new Vector2(w, w * video.Height / video.Width));
        }
    }

    private void DrawPlayback(HighPerformance hp)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Playback");
        var pb = hp.Playback;
        if (pb == null)
        {
            ImGui.TextDisabled("Nothing playing. Paste a YouTube link above or pick one in w2g.");
            return;
        }

        ImGui.TextUnformatted(pb.Title.Length > 70 ? pb.Title[..70] + "…" : pb.Title);
        if (pb.VideoHeight > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{pb.VideoHeight}p");
        }

        // Seek bar: follow the video until grabbed, send the seek on release.
        var duration = (float)Math.Max(1, pb.Duration);
        var position = seeking ? seekTarget : (float)Math.Min(pb.Position, duration);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.SliderFloat("##seek", ref position, 0f, duration, $"{Clock(position)} / {Clock(duration)}"))
        {
            seeking = true;
            seekTarget = position;
        }

        if (ImGui.IsItemDeactivated() && seeking)
        {
            hp.Control(PlaybackCommands.Seek(seekTarget));
            seeking = false;
        }

        if (ImGui.Button("-10s"))
            hp.Control(PlaybackCommands.Skip(-10));
        ImGui.SameLine();
        if (ImGui.Button(pb.Paused ? "  Play  " : " Pause "))
            hp.Control(PlaybackCommands.Toggle);
        ImGui.SameLine();
        if (ImGui.Button("+10s"))
            hp.Control(PlaybackCommands.Skip(10));
        ImGui.SameLine();
        // Keep showing what you picked until the helper reports it back (status lags ~1s),
        // otherwise the slider snaps to the old value and a click sends that instead.
        if (pendingVolume is { } pv && (pb.Volume == pv || DateTime.UtcNow > pendingUntil))
            pendingVolume = null;
        var volume = volumeDragging ? volumeDrag : pendingVolume ?? pb.Volume;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 60f);
        if (ImGui.SliderInt("Volume", ref volume, 0, 100))
        {
            volumeDragging = true;
            volumeDrag = volume;
        }

        if (ImGui.IsItemDeactivated() && volumeDragging)
        {
            volumeDragging = false;
            pendingVolume = volumeDrag;
            pendingUntil = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            hp.Volume(volumeDrag);
        }

        if (pendingSubtitles is { } ps && (pb.Subtitles == ps || DateTime.UtcNow > pendingSubtitlesUntil))
            pendingSubtitles = null;
        var subtitles = pendingSubtitles ?? pb.Subtitles;
        // Turning subtitles off always stays possible; only "on" needs subtitles to exist.
        var canToggle = subtitles || pb.SubtitlesAvailable;
        if (!canToggle)
            ImGui.BeginDisabled();
        if (ImGui.Checkbox("Subtitles (English)", ref subtitles))
        {
            pendingSubtitles = subtitles;
            pendingSubtitlesUntil = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            hp.Subtitles(subtitles);
        }

        if (!canToggle)
        {
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled("none on this video");
        }

        ImGui.TextDisabled("Play, pause and seek sync to the whole room. Subtitles and volume are just for you.");
    }

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
