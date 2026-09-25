using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Host;

/// <summary>
/// /viewinff: the helper runs its own off-screen Brave (ViewInFFXIV profile) and drives w2g for us.
/// </summary>
public sealed partial class HighPerformance
{
    private const string ChatPrefix = "ViewInFFXIV";

    private readonly Plugin plugin;
    private string lastRoomUrl = "";
    private string? lastError;
    private bool bringToFront;
    private bool preferencesApplied;
    private DateTime foregroundLockUntil = DateTime.MinValue;

    public HighPerformance(Plugin plugin)
    {
        this.plugin = plugin;
    }

    /// <summary>Text the next Draw should put on the clipboard (ImGui clipboard only works inside a frame).</summary>
    public string? PendingClipboard { get; set; }

    public ManagedBrowserStatus Status => plugin.Host.Status.Managed;

    public bool Active => plugin.Host.HelperAlive && Status.Active;

    public string ProfileDirectory =>
        Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "Browser", "Brave");

    public void Start()
    {
        plugin.Host.StartHost();
        if (Active)
            return;

        // Brave's first window would otherwise pull focus away from the game.
        LockSetForegroundWindow(LsfwLock);
        foregroundLockUntil = DateTime.UtcNow + TimeSpan.FromSeconds(25);
        plugin.Host.Send(HostCommand.Managed(ManagedOps.Start, ProfileDirectory));
    }

    public void CreateRoom()
    {
        Start();
        plugin.Host.Send(HostCommand.Managed(ManagedOps.Create));
        SendName();
    }

    public void Join(string text)
    {
        var link = FindW2gLink(text);
        if (link == null)
        {
            Plugin.ChatGui.PrintError($"{ChatPrefix}: paste a w2g.tv invite link, e.g. /viewinff join https://w2g.tv/?r=…");
            return;
        }

        Start();
        // The name travels with the join so it goes straight into w2g's guest welcome screen.
        plugin.Host.Send(HostCommand.Managed(ManagedOps.Join, link, ResolveName()));
    }

    public void Play(string text)
    {
        var url = UrlRegex().Match(text).Value;
        if (url.Length == 0)
        {
            Plugin.ChatGui.PrintError($"{ChatPrefix}: /viewinff play <video link>");
            return;
        }

        if (!Active)
        {
            Plugin.ChatGui.PrintError($"{ChatPrefix}: create or join a room first (/viewinff create).");
            return;
        }

        plugin.Host.Send(HostCommand.Managed(ManagedOps.Play, url));
    }

    public PlaybackStatus? Playback => Active ? Status.Playback : null;

    /// <summary>Sends a <see cref="PlaybackCommands"/> argument to the YouTube player in the room.</summary>
    public void Control(string command)
    {
        if (Playback == null)
        {
            Plugin.ChatGui.PrintError($"{ChatPrefix}: no YouTube video is playing in the room.");
            return;
        }

        plugin.Host.Send(HostCommand.Managed(ManagedOps.Control, command));
    }

    /// <summary>"1:23", "83", "+10", "-30" → seek or skip.</summary>
    public void SeekText(string text)
    {
        text = text.Trim();
        if (text.StartsWith('+') || text.StartsWith('-'))
        {
            if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var delta))
                Control(PlaybackCommands.Skip(delta));
            return;
        }

        var seconds = 0.0;
        foreach (var part in text.Split(':'))
        {
            if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                Plugin.ChatGui.PrintError($"{ChatPrefix}: /viewinff seek 1:23 | 83 | +10 | -10");
                return;
            }

            seconds = (seconds * 60) + n;
        }

        Control(PlaybackCommands.Seek(seconds));
    }

    /// <summary>English subtitles on/off (null toggles). Remembered and applied to every following video.</summary>
    public void Subtitles(bool? on)
    {
        var want = on ?? Playback is not { Subtitles: true };
        plugin.Configuration.SubtitlesOn = want;
        plugin.Configuration.Save();
        Control(PlaybackCommands.Subtitles(want));
    }

    /// <summary>Your own YouTube volume (not synced to the room). Remembered across sessions.</summary>
    public void Volume(int volume)
    {
        plugin.Configuration.Volume = Math.Clamp(volume, 0, 100);
        plugin.Configuration.Save();
        Control(PlaybackCommands.Volume(plugin.Configuration.Volume));
    }

    public void SendName()
    {
        if (ResolveName() is { Length: > 0 } name)
            plugin.Host.Send(HostCommand.Managed(ManagedOps.Name, name));
    }

    public void Show()
    {
        if (!Active)
            return;
        plugin.Host.Send(HostCommand.Managed(ManagedOps.Show));
        bringToFront = true;
    }

    public void Hide() => plugin.Host.Send(HostCommand.Managed(ManagedOps.Hide));

    public void Stop()
    {
        plugin.Host.Send(HostCommand.Managed(ManagedOps.Stop));
        bringToFront = false;
    }

    public void CopyInvite()
    {
        if (Status.RoomUrl.Length == 0)
        {
            Plugin.ChatGui.PrintError($"{ChatPrefix}: no room yet (/viewinff create).");
            return;
        }

        PendingClipboard = Status.RoomUrl;
        Plugin.ChatGui.Print($"Invite link copied: {Status.RoomUrl}", ChatPrefix);
    }

    /// <summary>Your w2g name is always your character name.</summary>
    public string? ResolveName() => Plugin.ObjectTable.LocalPlayer?.Name.TextValue;

    /// <summary>Runs every framework tick: reports rooms/errors in chat and finishes Show/startup focus handling.</summary>
    public void Tick()
    {
        var status = Status;

        if (foregroundLockUntil != DateTime.MinValue
            && (status.State is "ready" or "error" || DateTime.UtcNow > foregroundLockUntil || bringToFront))
        {
            LockSetForegroundWindow(LsfwUnlock);
            foregroundLockUntil = DateTime.MinValue;
        }

        // The game owns the foreground, so it is allowed to hand it to the Brave window.
        if (bringToFront && status.Shown && status.Hwnd != 0)
        {
            SetForegroundWindow((nint)status.Hwnd);
            bringToFront = false;
        }

        if (status.RoomUrl.Length > 0 && status.RoomUrl != lastRoomUrl)
        {
            lastRoomUrl = status.RoomUrl;
            plugin.Configuration.LastRoomUrl = status.RoomUrl;
            plugin.Configuration.Save();
            PendingClipboard = status.RoomUrl;
            Plugin.ChatGui.Print($"w2g room ready — invite link copied to clipboard: {status.RoomUrl}", ChatPrefix);
        }
        else if (status.RoomUrl.Length == 0)
        {
            lastRoomUrl = "";
        }

        // First video of a Brave session: replace YouTube's defaults with your saved subtitle/volume choice.
        // The helper then carries them over to each following video.
        if (!Active)
        {
            preferencesApplied = false;
        }
        else if (!preferencesApplied && Playback is { } playback)
        {
            preferencesApplied = true;
            var config = plugin.Configuration;
            if (playback.Subtitles != config.SubtitlesOn)
                plugin.Host.Send(HostCommand.Managed(ManagedOps.Control, PlaybackCommands.Subtitles(config.SubtitlesOn)));
            if (playback.Volume != config.Volume)
                plugin.Host.Send(HostCommand.Managed(ManagedOps.Control, PlaybackCommands.Volume(config.Volume)));
        }

        if (status.Error != lastError)
        {
            lastError = status.Error;
            if (!string.IsNullOrEmpty(status.Error))
                Plugin.ChatGui.PrintError($"{ChatPrefix}: {status.Error}");
        }
    }

    public static string? FindW2gLink(string text)
    {
        var match = W2gLinkRegex().Match(text);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"https://(?:www\.)?w2g\.tv/\S+", RegexOptions.IgnoreCase)]
    private static partial Regex W2gLinkRegex();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    private const uint LsfwLock = 1;
    private const uint LsfwUnlock = 2;

    [DllImport("user32.dll")]
    private static extern bool LockSetForegroundWindow(uint uLockCode);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
}
