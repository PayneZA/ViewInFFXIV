using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace ViewInFFXIV.Shared;

/// <summary>
/// Where a shared window was before Pop off moved it off every monitor. The helper puts it back when the share ends;
/// the plugin keeps a copy and puts it back itself if the helper died first.
/// </summary>
public sealed class SavedPlacement
{
    [JsonPropertyName("hwnd")]
    public long Hwnd { get; set; }

    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    /// <summary>Normal (restored) position, in workspace coordinates as GetWindowPlacement reports them.</summary>
    [JsonPropertyName("left")]
    public int Left { get; set; }

    [JsonPropertyName("top")]
    public int Top { get; set; }

    [JsonPropertyName("right")]
    public int Right { get; set; }

    [JsonPropertyName("bottom")]
    public int Bottom { get; set; }

    /// <summary>SW_SHOWNORMAL or SW_SHOWMAXIMIZED.</summary>
    [JsonPropertyName("show")]
    public int ShowCmd { get; set; }

    /// <summary>Reads where the window is now. Null if it is gone.</summary>
    public static SavedPlacement? Capture(IntPtr hwnd)
    {
        using var _ = new PerMonitorDpi();
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!IsWindow(hwnd) || !GetWindowPlacement(hwnd, ref placement))
            return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        return new SavedPlacement
        {
            Hwnd = hwnd.ToInt64(),
            Pid = (int)pid,
            Left = placement.NormalPosition.Left,
            Top = placement.NormalPosition.Top,
            Right = placement.NormalPosition.Right,
            Bottom = placement.NormalPosition.Bottom,
            // A minimised window comes back restored: minimised windows do not render.
            ShowCmd = placement.ShowCmd == SwShowMaximized || (placement.ShowCmd == SwShowMinimized && (placement.Flags & WpfRestoreToMaximized) != 0)
                ? SwShowMaximized
                : SwShowNormal,
        };
    }

    /// <summary>The same window (same handle and process) still exists.</summary>
    public bool Exists()
    {
        var hwnd = (IntPtr)Hwnd;
        if (!IsWindow(hwnd))
            return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Pid;
    }

    /// <summary>The window is on no monitor at all (still popped off).</summary>
    public bool IsOffScreen() => Exists() && MonitorFromWindow((IntPtr)Hwnd, MonitorDefaultToNull) == IntPtr.Zero;

    /// <summary>Puts the window back where it was, without activating it. False if it is gone.</summary>
    public bool Restore()
    {
        if (!Exists())
            return false;

        using var _ = new PerMonitorDpi();
        var hwnd = (IntPtr)Hwnd;
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        GetWindowPlacement(hwnd, ref placement);
        placement.Flags = 0;
        placement.NormalPosition = new Rect { Left = Left, Top = Top, Right = Right, Bottom = Bottom };
        placement.ShowCmd = ShowCmd == SwShowMaximized ? SwShowMaximized : SwShowNoActivate;
        return SetWindowPlacement(hwnd, ref placement);
    }

    /// <summary>Physical pixels for this thread while it is alive, whatever the process's own DPI awareness.</summary>
    private readonly struct PerMonitorDpi : IDisposable
    {
        private readonly IntPtr previous;

        public PerMonitorDpi()
        {
            try
            {
                previous = SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
            }
            catch (EntryPointNotFoundException)
            {
                previous = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            if (previous != IntPtr.Zero)
                SetThreadDpiAwarenessContext(previous);
        }
    }

    private const int SwShowNormal = 1;
    private const int SwShowMinimized = 2;
    private const int SwShowMaximized = 3;
    private const int SwShowNoActivate = 4;
    private const int WpfRestoreToMaximized = 2;
    private const uint MonitorDefaultToNull = 0;
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
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
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
