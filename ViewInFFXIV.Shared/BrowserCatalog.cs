namespace InView.Shared;

public static class BrowserCatalog
{
    public static readonly HashSet<string> Processes = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "brave", "msedge", "firefox", "opera", "vivaldi", "arc",
        "librewolf", "floorp", "chromium", "waterfox", "thorium", "iridium",
    };

    public static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Chrome",
        ["brave"] = "Brave",
        ["msedge"] = "Edge",
        ["firefox"] = "Firefox",
        ["opera"] = "Opera",
        ["vivaldi"] = "Vivaldi",
        ["arc"] = "Arc",
        ["librewolf"] = "LibreWolf",
        ["floorp"] = "Floorp",
        ["chromium"] = "Chromium",
        ["waterfox"] = "Waterfox",
        ["thorium"] = "Thorium",
        ["iridium"] = "Iridium",
    };

    public static string Friendly(string process) =>
        FriendlyNames.TryGetValue(process, out var name) ? name : process;

    public static string Label(string process, string title, int maxTitle = 40)
    {
        var trimmed = title.Trim();
        if (trimmed.Length > maxTitle)
            trimmed = trimmed[..maxTitle] + "…";
        return $"{Friendly(process)} — {trimmed}";
    }
}
