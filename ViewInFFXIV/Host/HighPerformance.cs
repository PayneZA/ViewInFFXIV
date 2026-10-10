using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ViewInFFXIV.Shared;
using ViewInFFXIV.World;

namespace ViewInFFXIV.Host;

/// <summary>
/// The watch party: the helper runs its own off-screen Brave (ViewInFFXIV profile) and drives w2g for us.
/// </summary>
public sealed partial class HighPerformance
{
    private const string ChatPrefix = "ViewInFFXIV";

    private readonly Plugin plugin;
    private string lastRoomUrl = "";
    private string? lastError;
    private bool bringToFront;
    private bool preferencesApplied;
    private bool creatingRoom;
    private DateTime connectingUntil = DateTime.MinValue;
    private DateTime foregroundLockUntil = DateTime.MinValue;

    public HighPerformance(Plugin plugin)
    {
        this.plugin = plugin;
    }

    /// <summary>Text the next Draw should put on the clipboard (ImGui clipboard only works inside a frame).</summary>
    public string? PendingClipboard { get; set; }

    public ManagedBrowserStatus Status => plugin.Host.Status.Managed;

    public bool Active => plugin.Host.HelperAlive && Status.Active;

    /// <summary>A party was started or joined and the room is not there yet (helper and Brave starting up).</summary>
    public bool Connecting =>
        DateTime.UtcNow < connectingUntil && Status.RoomUrl.Length == 0 && string.IsNullOrEmpty(Status.Error) && Status.State != "error";

    /// <summary>What is happening right now, for the "getting ready" screen.</summary>
    public string ConnectingActivity =>
        Status.Activity.Length > 0 ? Status.Activity : plugin.Host.HelperAlive && Status.Active ? "Opening the room" : "Starting up";

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
        // The helper must capture on the game's GPU for the texture to be shareable.
        plugin.Host.Send(HostCommand.Managed(ManagedOps.Start, ProfileDirectory, plugin.Video.AdapterLuid.ToString()));
    }

    public void CreateRoom()
    {
        Start();
        creatingRoom = true;
        connectingUntil = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        plugin.Host.Send(HostCommand.Managed(ManagedOps.Create));
        SendName();
    }

    /// <summary>One click: put the screen up (in front of you, if this zone has none yet) and create a room.</summary>
    public void StartParty()
    {
        var config = plugin.Configuration;
        if (!plugin.Renderer.WillDraw)
        {
            if (config.ScreenTerritory == Plugin.ClientState.TerritoryType && config.HasAnchor)
            {
                config.ScreenEnabled = true;
                PlacementPresets.SaveActivePreset(config, Plugin.ClientState.TerritoryType);
                config.Save();
            }
            else
            {
                plugin.Placement.PlaceInFront(Plugin.ClientState, Plugin.ObjectTable, config);
            }
        }

        CreateRoom();
    }

    /// <summary>Joins the w2g room in an invite; a screen spot in the same invite is saved and used too.</summary>
    public bool Join(string text)
    {
        var link = FindW2gLink(text);
        if (link == null)
        {
            Plugin.ChatGui.PrintError($"{ChatPrefix}: paste a w2g.tv invite link, e.g. /viewin join https://w2g.tv/?r=…");
            return false;
        }

        if (text.Contains(ScreenShareCode.Prefix, StringComparison.OrdinalIgnoreCase))
            plugin.ApplyShareCode(text);

        Start();
        creatingRoom = false;
        connectingUntil = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        // The name travels with the join so it goes straight into w2g's guest welcome screen.
        plugin.Host.Send(HostCommand.Managed(ManagedOps.Join, link, ResolveName()));
        return true;
    }

    /// <summary>
    /// What friends paste to join: the room link, plus your screen spot when the screen is up in this zone
    /// (their plugin puts their screen in the same place). The link comes first so it also works in a browser.
    /// </summary>
    public string BuildInvite()
    {
        var config = plugin.Configuration;
        var link = Status.RoomUrl;
        if (link.Length == 0 || !plugin.Renderer.WillDraw)
            return link;
        ScreenPlacement.EnsureAnchor(config);
        return $"{link} {ScreenShareCode.Export(config)}";
    }

    public void Play(string text)
    {
        var url = UrlRegex().Match(text).Value;
        if (url.Length == 0)
        {
            Plugin.ChatGui.PrintError($"{ChatPrefix}: /viewin play <video link>");
            return;
        }

        if (!Active)
        {
            Plugin.ChatGui.PrintError($"{ChatPrefix}: create or join a room first (/viewin create).");
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
                Plugin.ChatGui.PrintError($"{ChatPrefix}: /viewin seek 1:23 | 83 | +10 | -10");
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

    /// <summary>Closes Brave and the helper.</summary>
    public void Stop()
    {
        plugin.Host.StopHost();
        bringToFront = false;
        connectingUntil = DateTime.MinValue;
    }

    public void CopyInvite()
    {
        if (Status.RoomUrl.Length == 0)
        {
            Plugin.ChatGui.PrintError($"{ChatPrefix}: no room yet (/viewin create).");
            return;
        }

        PendingClipboard = BuildInvite();
        Plugin.ChatGui.Print($"Invite copied — friends paste it into /viewin join: {Status.RoomUrl}", ChatPrefix);
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
            if (creatingRoom)
            {
                // Your own new room: the invite goes straight to the clipboard. Joining keeps your clipboard.
                creatingRoom = false;
                PendingClipboard = BuildInvite();
                Plugin.ChatGui.Print($"Watch party ready — invite copied, paste it to your friends: {status.RoomUrl}", ChatPrefix);
            }
            else
            {
                Plugin.ChatGui.Print($"Joined the watch party: {status.RoomUrl}", ChatPrefix);
            }
        }
        else if (status.RoomUrl.Length == 0)
        {
            lastRoomUrl = "";
        }

        // First video of a Brave session: hand the helper your saved subtitle/volume choice. Always send it, even
        // when YouTube currently agrees: YouTube may switch its own default captions on a moment later, and the
        // helper only defends choices it has been given (it then re-applies them to every following video).
        if (!Active)
        {
            preferencesApplied = false;
        }
        else if (!preferencesApplied && Playback != null)
        {
            preferencesApplied = true;
            var config = plugin.Configuration;
            plugin.Host.Send(HostCommand.Managed(ManagedOps.Control, PlaybackCommands.Subtitles(config.SubtitlesOn)));
            plugin.Host.Send(HostCommand.Managed(ManagedOps.Control, PlaybackCommands.Volume(config.Volume)));
        }

        if (status.Error != lastError)
        {
            lastError = status.Error;
            if (!string.IsNullOrEmpty(status.Error))
                Plugin.ChatGui.PrintError($"{ChatPrefix}: {status.Error}");
        }
    }

    /// <summary>Finds a w2g link in text, with or without "https://" (chat often drops it).</summary>
    public static string? FindW2gLink(string text)
    {
        var match = W2gLinkRegex().Match(text);
        if (!match.Success)
            return null;
        return match.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? match.Value : "https://" + match.Value;
    }

    [GeneratedRegex(@"(?:https?://)?(?:www\.)?w2g\.tv/\S+", RegexOptions.IgnoreCase)]
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
