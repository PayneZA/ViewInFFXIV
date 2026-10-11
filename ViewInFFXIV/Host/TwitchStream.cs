using System.Runtime.InteropServices;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Host;

/// <summary>
/// A Twitch channel on the TV. The helper opens it in a second browser with its own profile and shows only the
/// video; while it runs, the TV shows it instead of the party (which keeps going, muted for you). No login: type a
/// channel and it plays. Pop out shows Twitch's page on your monitor when something there needs a click.
/// </summary>
public sealed class TwitchStream
{
    public const int RecentKept = 6;

    private readonly Plugin plugin;
    private string? lastError;
    // The channel last asked for, and the one whose "on the TV" notice was shown.
    private string wanted = "";
    private string announced = "";
    private bool stuckReported;
    private bool bringToFront;
    private DateTime foregroundLockUntil = DateTime.MinValue;

    public TwitchStream(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public StreamStatus Status => plugin.Host.Status.Stream;

    public bool Active => plugin.Host.HelperAlive && Status.Active;

    /// <summary>Twitch was on the TV as of the last tick (to word a helper crash).</summary>
    public bool WasActive { get; private set; }

    /// <summary>Starting up or opening the channel.</summary>
    public bool Starting => Active && Status.State == "starting";

    /// <summary>The channel's display name, else what was typed.</summary>
    public string DisplayName => Status.Name.Length > 0 ? Status.Name : Status.Channel;

    /// <summary>Each browser keeps its own Twitch profile, apart from the party's.</summary>
    private static string ProfileDirectory(BrowserInfo browser) =>
        Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "Browser", "Twitch-" + (browser == Browsers.Brave ? "Brave" : browser == Browsers.Chrome ? "Chrome" : "Edge"));

    /// <summary>Puts a channel ("name" or a twitch.tv link) on the TV. Returns false if the text is not one.</summary>
    public bool Watch(string text)
    {
        var channel = TwitchChannel.Parse(text);
        if (channel == null)
        {
            Notify.Error("That isn't a Twitch channel", "Type the channel's name, or paste a twitch.tv link.");
            return false;
        }

        var browser = Browsers.Resolve(plugin.Configuration.Browser);
        if (browser == null)
        {
            Notify.Error("No browser found", "ViewInFFXIV is made for Brave: install it from brave.com.");
            return false;
        }

        plugin.Host.StartHost();
        plugin.HighPerformance.PutScreenUp();
        if (!Active)
        {
            // The browser's first window would otherwise pull focus away from the game.
            LockSetForegroundWindow(LsfwLock);
            foregroundLockUntil = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        }

        wanted = channel;
        announced = "";
        stuckReported = false;
        Remember(channel);
        plugin.Host.Send(HostCommand.Managed(ManagedOps.Twitch, channel, ProfileDirectory(browser), browser.Id, plugin.Video.AdapterLuid.ToString()));
        return true;
    }

    /// <summary>Takes Twitch off the TV: back to the party if you are in one, else everything closes.</summary>
    public void Stop()
    {
        wanted = "";
        bringToFront = false;
        if (plugin.HighPerformance.Active)
            plugin.Host.Send(HostCommand.Managed(ManagedOps.StreamStop));
        else
            plugin.Host.StopHost();
    }

    /// <summary>Shows Twitch's page on your monitor (for a prompt on the page); the TV is blank until you pop back in.</summary>
    public void PopOut()
    {
        if (!Active)
            return;
        plugin.Host.Send(HostCommand.Managed(ManagedOps.Show, ManagedOps.StreamTarget));
        bringToFront = true;
    }

    /// <summary>Back off-screen: only the video on the TV again.</summary>
    public void PopIn() => plugin.Host.Send(HostCommand.Managed(ManagedOps.Hide, ManagedOps.StreamTarget));

    public void TogglePop()
    {
        if (Status.Shown)
            PopIn();
        else
            PopOut();
    }

    /// <summary>Your Twitch volume (just you), 0-100.</summary>
    public void SetVolume(int volume)
    {
        plugin.Configuration.TwitchVolume = Math.Clamp(volume, 0, 100);
        plugin.Configuration.Save();
    }

    private void Remember(string channel)
    {
        var recent = plugin.Configuration.TwitchRecent;
        recent.RemoveAll(c => c.Equals(channel, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, channel);
        if (recent.Count > RecentKept)
            recent.RemoveRange(RecentKept, recent.Count - RecentKept);
        plugin.Configuration.Save();
    }

    /// <summary>Runs every framework tick: focus handling and the notices.</summary>
    public void Tick()
    {
        var status = Status;
        WasActive = Active;

        if (foregroundLockUntil != DateTime.MinValue
            && (status.State is "ready" or "error" || DateTime.UtcNow > foregroundLockUntil || bringToFront))
        {
            LockSetForegroundWindow(LsfwUnlock);
            foregroundLockUntil = DateTime.MinValue;
        }

        // The game owns the foreground, so it is allowed to hand it to the popped-out window.
        if (bringToFront && status.Shown && status.Hwnd != 0)
        {
            SetForegroundWindow((nint)status.Hwnd);
            bringToFront = false;
        }

        if (Active && status.State == "ready" && status.Channel == wanted && wanted != announced)
        {
            announced = wanted;
            Notify.Success($"Twitch on the TV: {DisplayName}");
        }

        if (Active && status.Missing && status.Channel == wanted && wanted.Length > 0)
        {
            Notify.Error($"No Twitch channel called {wanted}", "Check the spelling, or paste the channel's twitch.tv link.");
            plugin.Configuration.TwitchRecent.Remove(wanted);
            plugin.Configuration.Save();
            Stop();
            return;
        }

        if (status.Stuck && !stuckReported)
        {
            stuckReported = true;
            Notify.Warning("Twitch shows no picture", "The channel is live but nothing plays. Twitch may be asking something on its page: press Pop out to see it, then Pop in.");
        }
        else if (!status.Stuck)
        {
            stuckReported = false;
        }

        if (status.Error != lastError)
        {
            lastError = status.Error;
            if (!string.IsNullOrEmpty(status.Error))
                Notify.Error("Twitch problem", status.Error);
        }
    }

    private const uint LsfwLock = 1;
    private const uint LsfwUnlock = 2;

    [DllImport("user32.dll")]
    private static extern bool LockSetForegroundWindow(uint uLockCode);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
}
