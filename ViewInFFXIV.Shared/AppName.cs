using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ViewInFFXIV.Shared;

/// <summary>An app's own name as Windows shows it ("Brave Browser", not "brave"), for people rather than programs.</summary>
public static class AppName
{
    /// <summary>
    /// The description in the app's exe (works for apps running as administrator too), else its exe name. Store apps
    /// all run in one host process, so for them the window title is the better name.
    /// </summary>
    public static string Of(int pid, string title)
    {
        var path = ExePath(pid);
        if (path == null)
            return "";

        var exe = Path.GetFileNameWithoutExtension(path);
        if (exe.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            return title;

        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description) && !description.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return description;
        }
        catch
        {
            // no version information
        }

        // Store apps often carry no description; their window title ("WhatsApp") reads better than "WhatsApp.Root".
        if (path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) && title.Length > 0)
            return title;
        return exe;
    }

    /// <summary>The exe file name, e.g. "ffxiv_dx11".</summary>
    public static string Exe(int pid) => ExePath(pid) is { } path ? Path.GetFileNameWithoutExtension(path) : "";

    private static string? ExePath(int pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
