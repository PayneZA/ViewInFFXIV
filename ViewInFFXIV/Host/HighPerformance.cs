using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ViewInFFXIV.Media;
using ViewInFFXIV.Shared;
using ViewInFFXIV.World;

namespace ViewInFFXIV.Host;

/// <summary>
/// The watch party: the helper runs its own off-screen Brave (ViewInFFXIV profile) and drives w2g for us.
/// </summary>
public sealed partial class HighPerformance
{
    private const string ChatPrefix = "Matinee";

    private readonly Plugin plugin;
    private string lastRoomUrl = "";
    private string? lastError;
    private bool bringToFront;
    private bool preferencesApplied;
    private bool creatingRoom;
    private DateTime connectingUntil = DateTime.MinValue;
    private DateTime foregroundLockUntil = DateTime.MinValue;

    // The video "Watch again" started this party with; played once the room it created is ready.
    private VideoInfo? startWith;

    public HighPerformance(Plugin plugin)
    {
        this.plugin = plugin;
    }

    /// <summary>Text the next Draw should put on the clipboard (ImGui clipboard only works inside a frame).</summary>
    public string? PendingClipboard { get; set; }

    public ManagedBrowserStatus Status => plugin.Host.Status.Managed;

    public bool Active => plugin.Host.HelperAlive && Status.Active;

    /// <summary>
    /// The room is open and holds something to watch, so the browser shows it. Before that it shows w2g's own pages
    /// or its paused sample video, which never go on the wall, the UI picture or the preview.
    /// </summary>
    public bool ShowsVideo => Active && Status.RoomUrl.Length > 0 && !Status.SampleVideo;

    /// <summary>Your last party ended because the helper stopped unexpectedly; cleared by starting or joining one.</summary>
    public bool Dropped { get; private set; }

    /// <summary>A party was started or joined and the room is not there yet (helper and Brave starting up).</summary>
    public bool Connecting =>
        DateTime.UtcNow < connectingUntil && Status.RoomUrl.Length == 0 && string.IsNullOrEmpty(Status.Error) && Status.State != "error";

    /// <summary>What is happening right now, for the "getting ready" screen.</summary>
    public string ConnectingActivity =>
        Status.Activity.Length > 0 ? Status.Activity : plugin.Host.HelperAlive && Status.Active ? "Opening the room" : "Starting up";

    /// <summary>The browser the next start uses (null: none of the supported ones is installed).</summary>
    public BrowserInfo? Browser => Browsers.Resolve(plugin.Configuration.Browser);

    /// <summary>Each browser keeps its own ViewInFFXIV profile (Brave's is the original "Brave" folder).</summary>
    private static string ProfileDirectory(BrowserInfo browser) =>
        Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "Browser", browser == Browsers.Brave ? "Brave" : browser == Browsers.Chrome ? "Chrome" : "Edge");

    public void Start()
    {
        plugin.Host.StartHost();
        var browser = Browser;
        if (browser == null)
        {
            Notify.Error("No browser found", "Matinee is made for Brave: install it from brave.com.");
            return;
        }

        if (Active && Status.Browser == browser.Name)
            return;

        // The browser's first window would otherwise pull focus away from the game.
        LockSetForegroundWindow(LsfwLock);
        foregroundLockUntil = DateTime.UtcNow + TimeSpan.FromSeconds(25);
        // The helper must capture on the game's GPU for the texture to be shareable.
        plugin.Host.Send(HostCommand.Managed(ManagedOps.Start, ProfileDirectory(browser), plugin.Video.AdapterLuid.ToString(), browser.Id));
    }

    /// <summary>Switches to the browser now chosen in Settings, rejoining the party you are in.</summary>
    public void SwitchBrowser()
    {
        var room = Status.RoomUrl.Length > 0 ? Status.RoomUrl : plugin.Configuration.LastRoomUrl;
        if (!Active || room.Length == 0)
            return;
        Join(room);
    }

    public void CreateRoom()
    {
        EndTwitch();
        Start();
        creatingRoom = true;
        connectingUntil = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        plugin.Host.Send(HostCommand.Managed(ManagedOps.Create));
        SendName();
    }

    /// <summary>
    /// One click: put the screen up (in front of you, if this zone has none yet) and create a room. With a video, it
    /// plays for everyone as soon as the room is ready.
    /// </summary>
    public void StartParty(VideoInfo? video = null)
    {
        Dropped = false;
        startWith = video;
        PutScreenUp();
        CreateRoom();
    }

    /// <summary>The TV comes up: on your UI, at this zone's spot, or in front of you when the zone has none yet.</summary>
    public void PutScreenUp()
    {
        var config = plugin.Configuration;
        if (config.ScreenMode == ScreenMode.OnUi)
        {
            config.UiScreenShown = true;
            config.Save();
        }
        else if (!plugin.Renderer.WillDraw)
        {
            if (config.ScreenTerritory == Plugin.ClientState.TerritoryType && config.HasAnchor)
            {
                PlacementPresets.SetShown(config, Plugin.ClientState.TerritoryType, plugin.CurrentHouse, true);
                config.Save();
            }
            else
            {
                plugin.Placement.PlaceInFront(Plugin.ClientState, Plugin.ObjectTable, config, plugin.CurrentHouse);
            }
        }
    }

    /// <summary>Joins the w2g room in an invite; a screen spot in the same invite is saved and used too.</summary>
    public bool Join(string text)
    {
        var link = FindW2gLink(text);
        if (link == null)
        {
            Notify.Error("That isn't an invite", "Paste the invite your friend sent (it starts with a w2g.tv link).");
            return false;
        }

        if (text.Contains(ScreenShareCode.Prefix, StringComparison.OrdinalIgnoreCase))
            plugin.ApplyShareCode(text);

        Dropped = false;
        startWith = null;
        EndTwitch();
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
        return $"{link} {ScreenShareCode.Export(config, plugin.CurrentHouse)}";
    }

    public void Play(string text)
    {
        var url = UrlRegex().Match(text).Value;
        if (url.Length == 0)
        {
            Plugin.ChatGui.Print("Usage: /matinee play <video link>", ChatPrefix);
            return;
        }

        if (!Active)
        {
            Notify.Warning("Start or join a party first");
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
            Notify.Warning("Nothing is playing yet");
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
                Plugin.ChatGui.Print("Usage: /matinee seek 1:23 | 83 | +10 | -10", ChatPrefix);
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

    /// <summary>A party you start or join takes the TV back from Twitch or a shared window.</summary>
    private void EndTwitch()
    {
        if (plugin.Twitch.Active)
            plugin.Host.Send(HostCommand.Managed(ManagedOps.StreamStop));
        if (plugin.WindowShare.Active)
            plugin.Host.Send(HostCommand.Managed(ManagedOps.WindowStop));
    }

    /// <summary>
    /// Leaves the party: closes its browser, and the helper too unless Twitch or a window is on the TV. With
    /// <paramref name="everything"/>, those close as well.
    /// </summary>
    public void Stop(bool everything = false)
    {
        if ((plugin.Twitch.Active || plugin.WindowShare.Active) && !everything)
            plugin.Host.Send(HostCommand.Managed(ManagedOps.Stop));
        else
            plugin.Host.StopHost();
        bringToFront = false;
        connectingUntil = DateTime.MinValue;
        startWith = null;
    }

    public void CopyInvite()
    {
        if (Status.RoomUrl.Length == 0)
        {
            Notify.Warning("Start or join a party first");
            return;
        }

        PendingClipboard = BuildInvite();
        Notify.Chat(InviteCopiedText());
    }

    /// <summary>What was copied and how friends use it (for the chat box).</summary>
    private string InviteCopiedText() =>
        (plugin.Renderer.WillDraw ? "Invite copied: the room link and your screen spot. " : "Invite copied: the room link. ")
        + "Friends paste it into the Join box in /matinee, or type /matinee join followed by the invite.";

    /// <summary>Your w2g name is always your character name.</summary>
    public string? ResolveName() => Plugin.ObjectTable.LocalPlayer?.Name.TextValue;

    /// <summary>Runs every framework tick: reports rooms/errors in chat and finishes Show/startup focus handling.</summary>
    public void Tick()
    {
        // Before anything below forgets the room: lastRoomUrl still says whether there was one.
        if (plugin.Host.TakeCrash())
        {
            var hadRoom = lastRoomUrl.Length > 0;
            lastRoomUrl = "";
            lastError = null;
            creatingRoom = false;
            startWith = null;
            connectingUntil = DateTime.MinValue;
            Dropped = hadRoom;
            if (!hadRoom && plugin.WindowShare.WasActive)
            {
                Notify.Error("Sharing stopped unexpectedly");
                Notify.Chat("Sharing your app on the TV stopped unexpectedly. Pick it again from /matinee.");
            }
            else if (!hadRoom && plugin.Twitch.WasActive)
            {
                Notify.Error("Twitch stopped unexpectedly");
                Notify.Chat("Twitch stopped unexpectedly. Start the channel again from /matinee, or type /matinee twitch followed by its name.");
            }
            else if (hadRoom)
            {
                Notify.Error("The watch party stopped");
                Notify.Chat("The watch party stopped unexpectedly. Press \"Rejoin your last party\" in /matinee, or type /matinee join, to get back in.");
            }
            else
            {
                Notify.Error("The watch party couldn't start");
                Notify.Chat("The watch party stopped unexpectedly while it was starting. Start it again from /matinee.");
            }

            return;
        }

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
                Notify.Success("Watch party ready");
                Notify.Chat(InviteCopiedText());
                if (startWith is { } video)
                {
                    startWith = null;
                    plugin.Queue.PlayNow(video);
                }
            }
            else
            {
                Notify.Success("Joined the watch party");
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
            {
                startWith = null;
                Notify.Error("Watch party problem", status.Error);
            }
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
