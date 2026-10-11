using System.Runtime.InteropServices;
using System.Text;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Host;

/// <summary>One of your windows, as the window list offers it.</summary>
public sealed record WindowChoice(nint Hwnd, string Title, string Process, bool CanPopOff);

/// <summary>
/// One of your windows on the TV, scaled to fit with its aspect ratio kept. Pop off moves it off every monitor at
/// exactly the TV's size, and puts it back where it was afterwards. While it is shared, the TV shows it instead of
/// Twitch or the party (which keeps going, muted for you). Its own sound plays as usual.
/// </summary>
public sealed class WindowShare : IDisposable
{
    private readonly Plugin plugin;
    private readonly Dictionary<int, string> processNames = [];
    private string? lastError;
    // The window last asked for, and the one whose "on the TV" notice was shown.
    private long wanted;
    private long announced;
    private bool stalledReported;
    private bool minimizedReported;
    private bool closedReported;

    public WindowShare(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public WindowStatus Status => plugin.Host.Status.Window;

    public bool Active => plugin.Host.HelperAlive && Status.Active;

    /// <summary>A window was on the TV as of the last tick (to word a helper crash).</summary>
    public bool WasActive { get; private set; }

    /// <summary>The window's title, else its app.</summary>
    public string DisplayName => Status.Title.Length > 0 ? Status.Title : Status.Process.Length > 0 ? Status.Process : "A window";

    /// <summary>Puts a window on the TV, popped off screen if you chose that (FFXIV windows never are).</summary>
    public void Share(WindowChoice choice)
    {
        if (!IsWindow(choice.Hwnd))
        {
            Notify.Error("That window is gone", "Pick it again from the list.");
            return;
        }

        plugin.Host.StartHost();
        plugin.HighPerformance.PutScreenUp();
        wanted = choice.Hwnd;
        announced = 0;
        stalledReported = minimizedReported = closedReported = false;
        var popOff = plugin.Configuration.WindowPopOff && choice.CanPopOff;
        plugin.Host.Send(HostCommand.Managed(ManagedOps.Window, choice.Hwnd.ToString(), popOff ? "1" : "0", null, plugin.Video.AdapterLuid.ToString()));
    }

    /// <summary>Takes the window off the TV (and puts it back): back to the party if you are in one, else everything closes.</summary>
    public void Stop()
    {
        wanted = 0;
        if (plugin.HighPerformance.Active)
            plugin.Host.Send(HostCommand.Managed(ManagedOps.WindowStop));
        else
            plugin.Host.StopHost();
    }

    /// <summary>Pop off (off every monitor, at the TV's size) or put it back on your monitor.</summary>
    public void TogglePopOff() => SetPopped(!Status.Popped);

    public void SetPopped(bool popped)
    {
        if (!Active)
            return;
        if (popped && !Status.CanPopOff)
        {
            Notify.Info("FFXIV windows stay where they are");
            return;
        }

        stalledReported = false;
        plugin.Host.Send(HostCommand.Managed(ManagedOps.WindowPop, popped ? "1" : "0"));
    }

    /// <summary>
    /// Your windows that can go on the TV, in the order Alt-Tab shows them. Leaves out the game itself and
    /// ViewInFFXIV's own browsers.
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

            var process = ProcessName((int)pid);
            found.Add(new WindowChoice(hwnd, text.ToString(), process, !process.Equals("ffxiv_dx11", StringComparison.OrdinalIgnoreCase)));
            return true;
        }, 0);
        return found;
    }

    /// <summary>First window whose title (or app) contains the text, for /viewin window.</summary>
    public WindowChoice? Find(string text) =>
        ListWindows().FirstOrDefault(w => w.Title.Contains(text, StringComparison.OrdinalIgnoreCase))
        ?? ListWindows().FirstOrDefault(w => w.Process.Contains(text, StringComparison.OrdinalIgnoreCase));

    private string ProcessName(int pid)
    {
        if (processNames.TryGetValue(pid, out var name))
            return name;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            name = p.ProcessName;
        }
        catch
        {
            name = "";
        }

        if (processNames.Count > 256)
            processNames.Clear();
        processNames[pid] = name;
        return name;
    }

    /// <summary>Runs every framework tick: the notices, and putting a popped-off window back if the helper could not.</summary>
    public void Tick()
    {
        var status = Status;
        WasActive = Active;
        KeepPlacement(status);

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
                Notify.Info("The shared window closed");
                Stop();
            }

            return;
        }

        closedReported = false;
        if (Active && status.Stalled && !stalledReported)
        {
            stalledReported = true;
            Notify.Warning("The window stopped drawing", "Some apps (browsers, Discord, games) pause while they're off screen. Press Put back to keep it on your monitor.");
        }

        if (Active && status.Minimized && !minimizedReported)
        {
            minimizedReported = true;
            Notify.Warning("The shared window is minimised", "Windows doesn't draw minimised windows, so the TV holds the last picture. Restore it, or press Pop off.");
        }
        else if (!status.Minimized)
        {
            minimizedReported = false;
        }

        var error = plugin.Host.HelperAlive ? status.Error : null;
        if (error != lastError)
        {
            lastError = error;
            if (!string.IsNullOrEmpty(error))
                Notify.Error("Window share problem", error);
        }
    }

    /// <summary>
    /// Keeps a copy of where a popped-off window was. If the helper is gone (or no longer has it popped off) while
    /// the window is still off every monitor, it is put back from here.
    /// </summary>
    private void KeepPlacement(WindowStatus status)
    {
        var config = plugin.Configuration;
        var helper = plugin.Host.HelperAlive;
        if (helper && status is { Popped: true, Original: { } original })
        {
            if (config.PoppedWindow is not { } kept || kept.Hwnd != original.Hwnd || kept.Left != original.Left || kept.Top != original.Top)
            {
                config.PoppedWindow = original;
                config.Save();
            }

            return;
        }

        if (config.PoppedWindow is { } saved)
        {
            RestoreSaved(saved);
        }
    }

    private void RestoreSaved(SavedPlacement saved)
    {
        try
        {
            if (saved.IsOffScreen() && saved.Restore())
                Plugin.Log.Information("Put the shared window back on your monitor.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not put the shared window back");
        }

        plugin.Configuration.PoppedWindow = null;
        plugin.Configuration.Save();
    }

    /// <summary>On unload, after the helper has closed: a window it left off screen comes back.</summary>
    public void Dispose()
    {
        if (plugin.Configuration.PoppedWindow is { } saved)
            RestoreSaved(saved);
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
