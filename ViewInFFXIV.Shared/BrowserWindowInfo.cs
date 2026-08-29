using System.Text.Json.Serialization;

namespace InView.Shared;

public sealed class BrowserWindowInfo
{
    [JsonPropertyName("hwnd")]
    public long Hwnd { get; set; }

    [JsonPropertyName("process")]
    public string Process { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";
}
