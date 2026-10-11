using System.Globalization;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.World;

/// <summary>
/// Makes the sound come from the TV: quieter as you walk away, panned left/right with the camera, and silent
/// once you leave the zone the screen is in. Applied on top of your own volume, inside the player page.
/// </summary>
public sealed class SpatialAudio
{
    private const float MinGain = 0.04f;
    private const float MaxPan = 0.7f;

    private readonly Plugin plugin;
    private float sentGain = -1;
    private float sentPan;
    private bool sentTwitch;
    private DateTime nextSend;

    public SpatialAudio(Plugin plugin)
    {
        this.plugin = plugin;
    }

    /// <summary>Volume factor (0..1) currently applied, for the UI.</summary>
    public float Gain { get; private set; } = 1;

    public void Tick()
    {
        var party = plugin.HighPerformance;
        var twitch = plugin.Twitch.Active;
        if (!twitch && (!party.Active || party.Playback == null))
        {
            // A new video page starts at full volume; send again once it plays.
            sentGain = -1;
            return;
        }

        // Twitch has no player volume of ours: your Twitch volume rides on the same gain. Switching sends afresh.
        if (twitch != sentTwitch)
        {
            sentTwitch = twitch;
            sentGain = -1;
        }

        var (gain, pan) = Compute();
        Gain = gain;
        if (twitch)
            gain *= plugin.Configuration.TwitchVolume / 100f;
        var now = DateTime.UtcNow;
        if (now < nextSend || (MathF.Abs(gain - sentGain) < 0.02f && MathF.Abs(pan - sentPan) < 0.03f))
            return;

        nextSend = now + TimeSpan.FromMilliseconds(120);
        sentGain = gain;
        sentPan = pan;
        plugin.Host.Send(HostCommand.Managed(ManagedOps.Audio, Format(gain), Format(pan)));
    }

    private unsafe (float Gain, float Pan) Compute()
    {
        var config = plugin.Configuration;
        // On your UI the screen has no place in the world: your own volume, centred.
        if (!config.SpatialAudio || config.ScreenMode == ScreenMode.OnUi)
            return (1, 0);

        if (!plugin.Renderer.WillDraw)
        {
            // Out of the screen's zone: silent, unless you are watching in the window instead.
            var away = config.ScreenEnabled && config.HasAnchor && config.ScreenTerritory != Plugin.ClientState.TerritoryType;
            return (away && !plugin.MainWindowOpen ? 0 : 1, 0);
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return (1, 0);

        var center = config.ScreenPosition;
        var distance = Vector3.Distance(player.Position, center);
        var near = 3f + (config.ScreenWidth * 0.8f);
        var far = near + config.HearingRange;
        var t = Math.Clamp((distance - near) / MathF.Max(0.1f, far - near), 0f, 1f);
        t = t * t * (3 - (2 * t));
        var gain = 1f + ((MinGain - 1f) * t);

        // Left/right from the camera: the screen's x in clip space (sign is right even when it is behind you).
        var control = Control.Instance();
        var pan = 0f;
        if (control != null)
        {
            var viewProj = *(Matrix4x4*)&control->ViewProjectionMatrix;
            var clip = Vector4.Transform(new Vector4(center, 1), viewProj);
            pan = Math.Clamp(clip.X / MathF.Max(MathF.Abs(clip.W), 0.5f), -1f, 1f) * MaxPan;
        }

        return (gain, pan);
    }

    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
