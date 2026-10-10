using Microsoft.Win32;

namespace ViewInFFXIV.Shared;

/// <summary>A browser the helper can drive: a Chromium browser that takes the DevTools launch flags as they are.</summary>
/// <param name="Id">Stored in the configuration and sent to the helper.</param>
/// <param name="BlocksAds">Built-in ad blocking keeps YouTube ads off the wall (Brave).</param>
/// <param name="ExtraFlags">Flags this browser needs on top of the common ones.</param>
public sealed record BrowserInfo(string Id, string Name, string ExeName, bool BlocksAds, string[] ExtraFlags, string[] InstallPaths);

/// <summary>
/// The supported browsers. Only Chromium browsers that accept the same DevTools launch line qualify; Firefox does
/// not (it dropped the Chrome DevTools protocol for WebDriver BiDi).
/// </summary>
public static class Browsers
{
    public const string Auto = "auto";

    public static readonly BrowserInfo Brave = new(
        "brave", "Brave", "brave.exe", BlocksAds: true, [],
        [@"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe", @"%ProgramFiles(x86)%\BraveSoftware\Brave-Browser\Application\brave.exe", @"%LocalAppData%\BraveSoftware\Brave-Browser\Application\brave.exe"]);

    public static readonly BrowserInfo Chrome = new(
        "chrome", "Google Chrome", "chrome.exe", BlocksAds: false, [],
        [@"%ProgramFiles%\Google\Chrome\Application\chrome.exe", @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe", @"%LocalAppData%\Google\Chrome\Application\chrome.exe"]);

    // Without these a fresh Edge profile opens a "turn on sync" dialog page, and Edge relaunches itself on start
    // (its compatibility layer), leaving the helper holding a process that has already exited.
    public static readonly BrowserInfo Edge = new(
        "edge", "Microsoft Edge", "msedge.exe", BlocksAds: false, ["--disable-sync", "--edge-skip-compat-layer-relaunch"],
        [@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe", @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"]);

    public const string BraveDownloadUrl = "https://brave.com/download/";

    /// <summary>In the order Auto prefers them: ad blocking first.</summary>
    public static readonly IReadOnlyList<BrowserInfo> All = [Brave, Chrome, Edge];

    public static BrowserInfo? ById(string? id) => All.FirstOrDefault(b => b.Id == id);

    private static readonly Dictionary<string, (string? Path, long At)> Found = new();

    /// <summary>The browser's exe, or null when it is not installed (looked up at most every few seconds).</summary>
    public static string? Find(BrowserInfo browser)
    {
        lock (Found)
        {
            if (Found.TryGetValue(browser.Id, out var hit) && Environment.TickCount64 - hit.At < 5000)
                return hit.Path;
            var path = Locate(browser);
            Found[browser.Id] = (path, Environment.TickCount64);
            return path;
        }
    }

    private static string? Locate(BrowserInfo browser)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{browser.ExeName}");
            if (key?.GetValue(null) is string path && File.Exists(path))
                return path;
        }

        return browser.InstallPaths.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(File.Exists);
    }

    /// <summary>The browser to use for a setting ("auto" or an id): that one if installed, else the first installed.</summary>
    public static BrowserInfo? Resolve(string? setting)
    {
        if (ById(setting) is { } chosen && Find(chosen) != null)
            return chosen;
        return All.FirstOrDefault(b => Find(b) != null);
    }
}
