using System.Runtime.InteropServices;
using System.Text;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Host;

/// <summary>One of your apps' windows, as the list offers it: the app's own name and the window's title.</summary>
public sealed record WindowChoice(nint Hwnd, string Title, string App);

/// <summary>
/// One of your apps mirrored on the TV, scaled to fit with its aspect ratio kept. The window stays where it is.
/// When something stops the picture (minimised, or hidden behind the game), this says so in plain words and offers
/// the one fix that helps. While it is shared, the TV shows it instead of Twitch or the party (which keeps going,
/// muted for you). The app's own sound plays as usual.
/// </summary>
public sealed class WindowShare
{
    private readonly Plugin plugin;
    private readonly Dictionary<int, string> appNames = [];
    private string? lastError;
    // The window last asked for, and the one whose "on the TV" notice was shown.
    private long wanted;
    private long announced;
    // Each notice once per share; the Watch tab and the remote always show the current state.
    private bool pausedReported;
    private bool closedReported;
    // Enlarging or moving brings the window to the front: the game takes the focus back right after.
    private DateTime refocusAt = DateTime.MinValue;

    public WindowShare(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public WindowStatus Status => plugin.Host.Status.Window;

    public bool Active => plugin.Host.HelperAlive && Status.Active;

    /// <summary>A window was on the TV as of the last tick (to word a helper crash).</summary>
    public bool WasActive { get; private set; }

    /// <summary>The app's name, else the window's title.</summary>
    public string DisplayName => Status.App.Length > 0 ? Status.App : Status.Title.Length > 0 ? Status.Title : "An app";

    /// <summary>The TV shows nothing new: the window is minimised, or hidden and paused.</summary>
    public bool Stuck => Active && (Status.Minimized || Status.Paused);

    /// <summary>Mirrors an app's window on the TV (a minimised one is shown again).</summary>
    public void Share(WindowChoice choice)
    {
        if (!IsWindow(choice.Hwnd))
        {
            Notify.Error("That app's window is gone", "Pick it again from the list.");
            return;
        }

        plugin.Host.StartHost();
        plugin.HighPerformance.PutScreenUp();
        wanted = choice.Hwnd;
        announced = 0;
        pausedReported = closedReported = false;
        plugin.Host.Send(HostCommand.Managed(ManagedOps.Window, choice.Hwnd.ToString(), null, GameWindow().ToString(), plugin.Video.AdapterLuid.ToString()));
    }

    /// <summary>Takes the app off the TV: back to the party if you are in one, else everything closes.</summary>
    public void Stop()
    {
        wanted = 0;
        if (plugin.HighPerformance.Active)
            plugin.Host.Send(HostCommand.Managed(ManagedOps.WindowStop));
        else
            plugin.Host.StopHost();
    }

    /// <summary>Shows a minimised window again, without taking the focus.</summary>
    public void ShowAgain() => Act(ManagedOps.WindowShow, refocus: false);

    /// <summary>Makes it fill its monitor (behind the game, if it shares the game's monitor).</summary>
    public void MakeBigger() => Act(ManagedOps.WindowBigger, refocus: true);

    /// <summary>Moves it to a monitor the game isn't on, so the game no longer covers it.</summary>
    public void MoveToOtherMonitor() => Act(ManagedOps.WindowMove, refocus: true);

    private void Act(string action, bool refocus)
    {
        if (!Active || !Status.Controllable)
            return;
        plugin.Host.Send(HostCommand.Managed(ManagedOps.WindowAct, action));
        if (refocus)
            refocusAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
    }

    /// <summary>
    /// Your apps' windows, in the order Alt-Tab shows them. Leaves out the game itself and ViewInFFXIV's own browsers.
    /// </summary>
    public List<WindowChoice> ListWindows()
    {
        var game = Environment.ProcessId;
        var skip = new HashSet<long> { plugin.Host.Status.Managed.Hwnd, plugin.Host.Status.Stream.Hwnd };
        var found = new List<WindowChoice>();
        var text = new StringBuilder(256);
        var className = new StringBuilder(64);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != 0 || skip.Contains(hwnd)
                || (GetWindowLong(hwnd, GwlExStyle) & WsExToolWindow) != 0)
                return true;
            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == game)
                return true;

            className.Clear();
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd")
                return true;

            text.Clear();
            GetWindowText(hwnd, text, text.Capacity);
            if (text.Length == 0)
                return true;

            var title = text.ToString();
            found.Add(new WindowChoice(hwnd, title, NameOf((int)pid, title)));
            return true;
        }, 0);
        return found;
    }

    /// <summary>First window whose title (or app) contains the text, for /viewin window.</summary>
    public WindowChoice? Find(string text)
    {
        var windows = ListWindows();
        return windows.FirstOrDefault(w => w.Title.Contains(text, StringComparison.OrdinalIgnoreCase))
            ?? windows.FirstOrDefault(w => w.App.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"Watching YouTube?" / "Watching Twitch?" from the shared window's title, else null.</summary>
    public (bool YouTube, string? TwitchChannel)? WatchingSite()
    {
        var title = Status.Title;
        if (title.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
            return (true, null);

        // Twitch titles read "channel - Twitch".
        var twitch = title.IndexOf(" - Twitch", StringComparison.OrdinalIgnoreCase);
        if (twitch > 0 && TwitchChannel.Parse(title[..twitch].Trim()) is { } channel)
            return (false, channel);
        return null;
    }

    private string NameOf(int pid, string title)
    {
        if (appNames.TryGetValue(pid, out var name))
            return name;
        name = AppName.Of(pid, title);
        if (appNames.Count > 256)
            appNames.Clear();
        // Store apps all run in one host: their name is their title, which changes, so it is not kept.
        if (name != title)
            appNames[pid] = name;
        return name;
    }

    private static unsafe nint GameWindow()
    {
        var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        return framework != null && framework->GameWindow != null ? framework->GameWindow->WindowHandle : 0;
    }

    /// <summary>Runs every framework tick: the notices, and taking the focus back after a fix.</summary>
    public void Tick()
    {
        var status = Status;
        WasActive = Active;

        if (refocusAt != DateTime.MinValue && DateTime.UtcNow >= refocusAt)
        {
            refocusAt = DateTime.MinValue;
            if (GameWindow() is var game and not 0)
                SetForegroundWindow(game);
        }

        if (Active && status.Hwnd == wanted && wanted != announced)
        {
            announced = wanted;
            Notify.Success($"On the TV: {DisplayName}");
        }

        if (plugin.Host.HelperAlive && status.State == "closed")
        {
            if (!closedReported)
            {
                closedReported = true;
                Notify.Info($"{(DisplayName == "An app" ? "The shared app" : DisplayName)} was closed");
                Stop();
            }

            return;
        }

        closedReported = false;

        // A short toast only when you're not looking at the window, which says the same with a button to fix it.
        if (Stuck && !pausedReported && !plugin.MainWindowOpen)
        {
            pausedReported = true;
            Notify.Warning("The app on the TV is paused", "Open /viewin to get it moving again.");
        }

        var error = plugin.Host.HelperAlive ? status.Error : null;
        if (error != lastError)
        {
            lastError = error;
            if (!string.IsNullOrEmpty(error))
                Notify.Error("Couldn't show that app", error);
        }
    }

    private const int GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const int DwmwaCloaked = 14;

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, int uCmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder className, int maxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}
