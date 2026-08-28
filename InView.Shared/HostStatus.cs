using System.Text.Json.Serialization;

namespace InView.Shared;

public sealed class HostStatus
{
    [JsonPropertyName("alive")]
    public bool Alive { get; set; }

    [JsonPropertyName("loaded")]
    public bool Loaded { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("fps")]
    public float Fps { get; set; }

    [JsonPropertyName("generation")]
    public ulong Generation { get; set; }

    [JsonPropertyName("capture")]
    public string Capture { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "webview";

    [JsonPropertyName("windowVisible")]
    public bool WindowVisible { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("sharedHandle")]
    public ulong SharedHandle { get; set; }

    [JsonPropertyName("capturedTitle")]
    public string CapturedTitle { get; set; } = "";

    [JsonPropertyName("windows")]
    public List<BrowserWindowInfo> Windows { get; set; } = [];
}
