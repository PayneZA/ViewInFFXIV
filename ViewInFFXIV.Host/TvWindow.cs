using System.Runtime.InteropServices;
using System.Text;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Host;

/// <summary>
/// One of your windows on the TV. Only its inside goes on the TV (no title bar or borders), scaled to fit. Pop off
/// moves it off every monitor at exactly the TV's size, where Windows keeps drawing it, and puts it back where it
/// was afterwards. Clicking its taskbar button or Alt-Tabbing to it puts it back too, so it can never get lost.
/// UI thread only; <see cref="Status"/> is a snapshot for the pipe thread.
/// </summary>
internal sealed class TvWindow(int gamePid)
{
    // Measure-and-correct rounds for the TV size: an app with its own size limits is left at its closest size.
    private const int MaxSizeTries = 20;

    private SavedPlacement? original;
    private bool resizable;
    private int sizeTries;
    private DateTime poppedAt;
    private bool movingBeforePop;
    // It was the foreground window on the last tick; becoming it (not staying it) is what puts it back.
    private bool foreground;
    private bool stalled;
    private bool minimized;
    private int ticks;
    private string state = "off";
    private string? error;
    private string title = "";
    private string process = "";
    private bool canPopOff;
    private int width;
    private int height;

    public IntPtr Hwnd { get; private set; }

    /// <summary>On the TV (the window exists and is shared).</summary>
    public bool Active => Hwnd != IntPtr.Zero && state == "ready";

    public bool Popped => original != null;

    /// <summary>The window's inside, as a part of what Windows.Graphics.Capture delivers.</summary>
    public CropRect? Crop { get; private set; }

    public WindowStatus Status { get; private set; } = new();

    public void Start(IntPtr hwnd, bool popOff, float arrivedFps)
    {
        Stop();
        if (!IsWindow(hwnd))
            throw new InvalidOperationException("That window is gone. Pick it again from the list.");
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == gamePid || pid == Environment.ProcessId)
            throw new InvalidOperationException("The game can't show its own window on the TV.");

        Hwnd = hwnd;
        process = ProcessName((int)pid);
        canPopOff = !process.Equals("ffxiv_dx11", StringComparison.OrdinalIgnoreCase);
        title = WindowTitle(hwnd);
        state = "ready";
        error = null;
        Measure();
        if (popOff)
            PopOff(arrivedFps);
        Publish();
    }

    /// <summary>Takes the window off the TV and puts it back where it was.</summary>
    public void Stop()
    {
        PutBack();
        Hwnd = IntPtr.Zero;
        Crop = null;
        state = "off";
        error = null;
        title = "";
        process = "";
        width = height = 0;
        minimized = false;
        Publish();
    }

    /// <summary>Sharing could not start; the plugin shows why.</summary>
    public void Fail(string message)
    {
        state = "error";
        error = message;
        Publish();
    }

    /// <summary>Off every monitor, sized so its inside is exactly the TV's size (when the app allows resizing).</summary>
    public void PopOff(float arrivedFps)
    {
        if (!Active || Popped)
            return;
        if (!canPopOff)
        {
            error = "FFXIV windows always stay where they are.";
            Publish();
            return;
        }

        var saved = SavedPlacement.Capture(Hwnd);
        if (saved == null)
            return;

        // Minimised or maximised windows can't simply be moved: show them restored first, without taking focus.
        if (IsIconic(Hwnd) || IsZoomed(Hwnd))
        {
            ShowWindow(Hwnd, SwShowNoActivate);
            if (IsZoomed(Hwnd))
                ShowWindow(Hwnd, SwRestore);
        }

        resizable = (GetWindowLong(Hwnd, GwlStyle) & WsThickFrame) != 0;
        original = saved;
        poppedAt = DateTime.UtcNow;
        movingBeforePop = arrivedFps > 5;
        stalled = false;
        sizeTries = 0;
        error = null;
        foreground = GetForegroundWindow() == Hwnd;
        if (!MoveOff())
        {
            original = null;
            error = "Windows won't let ViewInFFXIV move this window (it may run as administrator). It stays on the TV where it is.";
        }

        Publish();
    }

    /// <summary>Back where it was before Pop off; it stays on the TV.</summary>
    public void PutBack()
    {
        var saved = original;
        original = null;
        stalled = false;
        saved?.Restore();
        Publish();
    }

    /// <summary>Every guard tick (100 ms): follows the window, keeps a popped-off one off screen and at size.</summary>
    public void Guard(GpuCapture? capture)
    {
        if (Hwnd == IntPtr.Zero)
            return;

        if (!IsWindow(Hwnd))
        {
            original = null;
            Hwnd = IntPtr.Zero;
            Crop = null;
            state = "closed";
            Publish();
            return;
        }

        var now = DateTime.UtcNow;
        if (Popped)
        {
            minimized = false;
            var wasForeground = foreground;
            foreground = GetForegroundWindow() == Hwnd;
            if (foreground && !wasForeground)
            {
                // Taskbar click or Alt-Tab: you want it, so it comes back to your monitor.
                PutBack();
            }
            else if (IsIconic(Hwnd))
            {
                // Minimised windows are not drawn: back off screen, restored.
                ShowWindow(Hwnd, SwShowNoActivate);
                MoveOff();
            }
            else if (GetWindowRect(Hwnd, out var r) && SystemInformation.VirtualScreen.IntersectsWith(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)))
            {
                MoveOff();
            }
            else if (resizable && sizeTries < MaxSizeTries && ClientSize(out var w, out var h)
                     && (w != IpcConstants.MaxWidth || h != IpcConstants.MaxHeight))
            {
                // Its DPI can change once it is off a monitor: measure and correct until the inside fits.
                sizeTries++;
                MoveOff();
            }

            // It was moving before Pop off and has drawn nothing since: the app pauses while it is off screen.
            var arrived = capture?.ArrivedFps ?? 0;
            if (arrived > 0)
                stalled = false;
            else if (Popped && movingBeforePop && now - poppedAt > TimeSpan.FromSeconds(3))
                stalled = true;
        }
        else
        {
            stalled = false;
            minimized = IsIconic(Hwnd);
        }

        if (++ticks % 10 == 0)
            title = WindowTitle(Hwnd);
        Measure();
        Publish();
    }

    /// <summary>Moves it off every monitor (at the back of the stack, without activating it). False if refused.</summary>
    private bool MoveOff()
    {
        if (!GetWindowRect(Hwnd, out var outer))
            return false;

        int w = outer.Right - outer.Left, h = outer.Bottom - outer.Top;
        var flags = SwpNoActivate;
        if (resizable && ClientSize(out var cw, out var ch))
        {
            w = Math.Clamp(w + (IpcConstants.MaxWidth - cw), 200, 8192);
            h = Math.Clamp(h + (IpcConstants.MaxHeight - ch), 150, 8192);
        }
        else
        {
            flags |= SwpNoSize;
        }

        var x = SystemInformation.VirtualScreen.Left - w - 400;
        return SetWindowPos(Hwnd, HwndBottom, x, SystemInformation.VirtualScreen.Top, w, h, flags);
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

    private bool ClientSize(out int w, out int h)
    {
        w = h = 0;
        if (!GetClientRect(Hwnd, out var client))
            return false;
        w = client.Right - client.Left;
        h = client.Bottom - client.Top;
        return true;
    }

    private void Publish()
    {
        Status = new WindowStatus
        {
            State = state,
            Error = error,
            Hwnd = Hwnd.ToInt64(),
            Title = title,
            Process = process,
            Width = width,
            Height = height,
            Popped = Popped,
            CanPopOff = canPopOff,
            Minimized = minimized,
            Stalled = stalled,
            Original = original,
        };
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        // Another process's window: GetWindowText reads the stored caption and never waits on a hung app.
        var text = new StringBuilder(256);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private static bool GetFrameBounds(IntPtr hwnd, out NativeRect bounds) =>
        DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out bounds, Marshal.SizeOf<NativeRect>()) == 0 || GetWindowRect(hwnd, out bounds);

    private const int SwShowNoActivate = 4;
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const int GwlStyle = -16;
    private const int WsThickFrame = 0x00040000;
    private const int DwmwaExtendedFrameBounds = 9;
    private static readonly IntPtr HwndBottom = new(1);

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

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);
}
