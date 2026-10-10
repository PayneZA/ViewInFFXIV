using Microsoft.Win32;

namespace ViewInFFXIV.Shared;

/// <summary>Finds the installed Brave browser (the helper runs it; the plugin checks for it up front).</summary>
public static class BraveLocator
{
    public const string DownloadUrl = "https://brave.com/download/";

    public static string? Find()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\brave.exe");
            if (key?.GetValue(null) is string path && File.Exists(path))
                return path;
        }

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BraveSoftware\Brave-Browser\Application\brave.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
