using System.Text.Json.Serialization;

namespace InView.Shared;

public sealed class HostCommand
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = "";

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("volume")]
    public float? Volume { get; set; }

    [JsonPropertyName("visible")]
    public bool? Visible { get; set; }

    [JsonPropertyName("hideChrome")]
    public bool? HideChrome { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("hwnd")]
    public long? Hwnd { get; set; }

    public static HostCommand Navigate(string url) => new() { Op = "navigate", Url = url };

    public static HostCommand SetVisible(bool visible) => new() { Op = "visible", Visible = visible };

    public static HostCommand SetVolume(float volume) => new() { Op = "volume", Volume = volume };

    public static HostCommand SetHideChrome(bool hide) => new() { Op = "chrome", HideChrome = hide };

    public static HostCommand SetSource(string source, long? hwnd = null) => new()
    {
        Op = "source",
        Source = source,
        Hwnd = hwnd,
    };

    public static HostCommand ListWindows() => new() { Op = "listWindows" };

    public static HostCommand Ping() => new() { Op = "ping" };

    public static HostCommand Quit() => new() { Op = "quit" };
}
