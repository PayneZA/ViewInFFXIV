using System.Runtime.InteropServices;
using System.Text;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Host;

/// <summary>
/// One of your windows mirrored on the TV, where it is: only its inside (no title bar or borders), scaled to fit.
/// This follows it (closed, minimised, covered and paused) and offers the fixes: show it again, make it fill its
/// monitor, or move it to a monitor the game isn't on.
/// UI thread only; <see cref="Status"/> is a snapshot for the pipe thread.
/// </summary>
internal sealed class TvWindow(int gamePid)
{
    // A window drawing at least this often counts as moving (a video, an animation), not a still page.
    private const float MovingFps = 5;

    // Chromium's window class: Chrome, Edge, Brave and every Electron app (Discord, Spotify, ChatGPT...). They all
    // stop drawing while another window covers them completely.
    private const string ChromiumClass = "Chrome_WidgetWin_1";

    private IntPtr game;
    private bool browserLike;
    private bool controllable;
    private bool canEnlarge;
    private bool moving;
    private bool covered;
    private bool coveredByGame;
    private bool paused;
    private bool minimized;
    private bool maximized;
    private bool onGameMonitor;
    private int ticks;
    private string state = "off";
    private string? error;
    private string title = "";
    private string app = "";
    private int width;
    private int height;

    public IntPtr Hwnd { get; private set; }

    /// <summary>On the TV (the window exists and is shared).</summary>
    public bool Active => Hwnd != IntPtr.Zero && state == "ready";

    /// <summary>The window's inside, as a part of what Windows.Graphics.Capture delivers.</summary>
    public CropRect? Crop { get; private set; }

    public WindowStatus Status { get; private set; } = new();

    /// <summary>Shares a window; a minimised one is shown again (you picked it to see it).</summary>
    public void Start(IntPtr hwnd, IntPtr gameWindow)
    {
        Stop();
        if (!IsWindow(hwnd))
            throw new InvalidOperationException("That app's window is gone. Pick it again from the list.");
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == gamePid || pid == Environment.ProcessId)
            throw new InvalidOperationException("The game can't show itself on the TV.");

        Hwnd = hwnd;
        game = gameWindow;
        title = WindowTitle(hwnd);
        app = AppName.Of((int)pid, title);
        browserLike = ClassName(hwnd) == ChromiumClass;
        // An app running as administrator can't be shown, enlarged or moved from here (Windows blocks it).
        controllable = CanControl(hwnd);
        canEnlarge = controllable && (GetWindowLong(hwnd, GwlStyle) & WsMaximizeBox) != 0;
        state = "ready";
        error = null;
        if (controllable && IsIconic(hwnd))
            ShowWindowAsync(hwnd, SwShowNoActivate);
        Measure();
        Publish();
    }

    /// <summary>Takes the window off the TV. The window itself is left as it is.</summary>
    public void Stop()
    {
        Hwnd = IntPtr.Zero;
        game = IntPtr.Zero;
        Crop = null;
        state = "off";
        error = null;
        title = app = "";
        width = height = 0;
        browserLike = controllable = canEnlarge = false;
        minimized = maximized = onGameMonitor = false;
        moving = covered = coveredByGame = paused = false;
        Publish();
    }

    /// <summary>Sharing could not start; the plugin shows why.</summary>
    public void Fail(string message)
    {
        state = "error";
        error = message;
        Publish();
    }

    /// <summary>One of the fixes (<see cref="ManagedOps.WindowShow"/> and the others).</summary>
    public void Act(string? action)
    {
        if (!Active || !controllable)
            return;

        switch (action)
        {
            case ManagedOps.WindowShow:
                if (IsIconic(Hwnd))
                    ShowWindowAsync(Hwnd, SwShowNoActivate);
                break;
            case ManagedOps.WindowBigger when canEnlarge:
                // Async: a hung app never stalls the helper.
                ShowWindowAsync(Hwnd, SwShowMaximized);
                break;
            case ManagedOps.WindowMove when OtherMonitor() is { } target:
                MoveTo(target);
                break;
        }
    }

    /// <summary>Puts it on another monitor: filling it when it can be maximised, else centred there.</summary>
    private void MoveTo(Screen target)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(Hwnd, ref placement))
            return;

        var area = target.WorkingArea;
        var normal = placement.NormalPosition;
        int w = Math.Min(normal.Right - normal.Left, area.Width), h = Math.Min(normal.Bottom - normal.Top, area.Height);
        var x = area.Left + ((area.Width - w) / 2);
        var y = area.Top + ((area.Height - h) / 2);
        placement.Flags = 0;
        placement.NormalPosition = new NativeRect { Left = x, Top = y, Right = x + w, Bottom = y + h };
        placement.ShowCmd = canEnlarge ? SwShowMaximized : SwShowNoActivate;
        SetWindowPlacement(Hwnd, ref placement);
    }

    /// <summary>The biggest monitor the game isn't on, if you have one.</summary>
    private Screen? OtherMonitor()
    {
        var gameScreen = game != IntPtr.Zero ? Screen.FromHandle(game) : Screen.PrimaryScreen;
        return Screen.AllScreens
            .Where(s => s.DeviceName != gameScreen?.DeviceName)
            .OrderByDescending(s => s.Bounds.Width * s.Bounds.Height)
            .FirstOrDefault();
    }

    /// <summary>Every guard tick (100 ms): follows the window and how it is drawn.</summary>
    public void Guard(GpuCapture? capture)
    {
        if (Hwnd == IntPtr.Zero)
            return;

        if (!IsWindow(Hwnd))
        {
            Hwnd = IntPtr.Zero;
            Crop = null;
            state = "closed";
            Publish();
            return;
        }

        minimized = IsIconic(Hwnd);
        maximized = !minimized && IsZoomed(Hwnd);
        var arrived = capture?.ArrivedFps ?? 0;
        if (++ticks % 5 == 0)
        {
            var cover = minimized ? IntPtr.Zero : CoveringWindow();
            covered = cover != IntPtr.Zero;
            coveredByGame = covered && GetWindowThreadProcessId(cover, out var coverPid) != 0 && coverPid == gamePid;
            onGameMonitor = game != IntPtr.Zero && MonitorFromWindow(Hwnd, MonitorDefaultToNearest) == MonitorFromWindow(game, MonitorDefaultToNearest);
        }

        // A still page delivers no frames either: only a browser-type app, or a window that was moving, counts as
        // paused when it is covered and nothing arrives.
        if (arrived >= MovingFps)
            moving = true;
        else if (!covered && arrived == 0)
            moving = false; // in plain view and still: paused by its user, not by Windows
        paused = covered && arrived == 0 && (browserLike || moving);

        if (ticks % 10 == 0)
            title = WindowTitle(Hwnd);
        Measure();
        Publish();
    }

    /// <summary>A single visible window above it that covers it completely (the game in full screen, typically).</summary>
    private IntPtr CoveringWindow()
    {
        if (!GetFrameBounds(Hwnd, out var me))
            return IntPtr.Zero;

        var n = 0;
        for (var w = GetWindow(Hwnd, GwHwndPrev); w != IntPtr.Zero && n++ < 1000; w = GetWindow(w, GwHwndPrev))
        {
            // Click-through overlays (game overlays, ACT's) are see-through: they hide nothing.
            if (!IsWindowVisible(w) || IsIconic(w) || (GetWindowLong(w, GwlExStyle) & (WsExLayered | WsExTransparent)) != 0)
                continue;
            if (DwmGetWindowAttribute(w, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                continue;
            if (GetFrameBounds(w, out var r) && r.Left <= me.Left && r.Top <= me.Top && r.Right >= me.Right && r.Bottom >= me.Bottom)
                return w;
        }

        return IntPtr.Zero;
    }

    /// <summary>The inside of the window, as a part of the visible frame (what the capture delivers).</summary>
    private void Measure()
    {
        if (IsIconic(Hwnd) || !GetClientRect(Hwnd, out var client) || !GetFrameBounds(Hwnd, out var frame))
            return;

        var origin = new NativePoint();
        if (!ClientToScreen(Hwnd, ref origin))
            return;

        float frameW = frame.Right - frame.Left, frameH = frame.Bottom - frame.Top;
        width = client.Right - client.Left;
        height = client.Bottom - client.Top;
        if (frameW < 2 || frameH < 2 || width < 2 || height < 2)
        {
            Crop = null;
            return;
        }

        Crop = new CropRect(
            Math.Clamp((origin.X - frame.Left) / frameW, 0f, 1f),
            Math.Clamp((origin.Y - frame.Top) / frameH, 0f, 1f),
            Math.Clamp((origin.X - frame.Left + width) / frameW, 0f, 1f),
            Math.Clamp((origin.Y - frame.Top + height) / frameH, 0f, 1f));
    }

    private void Publish()
    {
        Status = new WindowStatus
        {
            State = state,
            Error = error,
            Hwnd = Hwnd.ToInt64(),
            Title = title,
            App = app,
            Width = width,
            Height = height,
            Minimized = minimized,
            Maximized = maximized,
            Controllable = controllable,
            CanEnlarge = canEnlarge,
            Covered = covered,
            CoveredByGame = coveredByGame,
            Paused = paused,
            OnGameMonitor = onGameMonitor,
            OtherMonitor = Hwnd != IntPtr.Zero && Screen.AllScreens.Length > 1,
        };
    }

    /// <summary>
    /// Whether Windows lets us change it: a do-nothing SetWindowPos is refused (access denied) for a window of a
    /// process running with higher rights, such as an app started as administrator.
    /// </summary>
    private static bool CanControl(IntPtr hwnd) =>
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder)
        || Marshal.GetLastWin32Error() != ErrorAccessDenied;

    private static string WindowTitle(IntPtr hwnd)
    {
        // Another process's window: GetWindowText reads the stored caption and never waits on a hung app.
        var text = new StringBuilder(256);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    private static string ClassName(IntPtr hwnd)
    {
        var text = new StringBuilder(64);
        GetClassName(hwnd, text, text.Capacity);
        return text.ToString();
    }

    private static bool GetFrameBounds(IntPtr hwnd, out NativeRect bounds) =>
        DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out bounds, Marshal.SizeOf<NativeRect>()) == 0 || GetWindowRect(hwnd, out bounds);

    private const int SwShowMaximized = 3;
    private const int SwShowNoActivate = 4;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int ErrorAccessDenied = 5;
    private const int GwHwndPrev = 3;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsMaximizeBox = 0x00010000;
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
