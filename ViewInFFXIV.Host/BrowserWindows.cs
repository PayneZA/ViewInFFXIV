using System.Diagnostics;
using System.Runtime.InteropServices;
using InView.Shared;

namespace InView.Host;

internal static class BrowserWindows
{
    private const int GwOwner = 4;

    public static List<BrowserWindowInfo> Enumerate()
    {
        var list = new List<BrowserWindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
                return true;
            if (GetWindow(hwnd, GwOwner) != IntPtr.Zero)
                return true;
            var text = GetTitle(hwnd);
            if (string.IsNullOrEmpty(text))
                return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                var name = process.ProcessName;
                if (name.Equals("InView.Host", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!BrowserCatalog.Processes.Contains(name))
                    return true;

                list.Add(new BrowserWindowInfo
                {
                    Hwnd = hwnd.ToInt64(),
                    Process = name,
                    Title = text,
                });
            }
            catch
            {
                // process exited
            }

            return true;
        }, IntPtr.Zero);

        return list;
    }

    public static string GetTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return "";
        var title = new char[length + 1];
        GetWindowText(hwnd, title, title.Length);
        return new string(title).TrimEnd('\0').Trim();
    }

    public static bool IsAlive(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && IsWindow(hwnd);

    public static bool TryGetBounds(IntPtr hwnd, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!IsAlive(hwnd) || IsIconic(hwnd) || !GetWindowRect(hwnd, out var rect))
            return false;
        width = Math.Max(0, rect.Right - rect.Left);
        height = Math.Max(0, rect.Bottom - rect.Top);
        return width >= 2 && height >= 2;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
