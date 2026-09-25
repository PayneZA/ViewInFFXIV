using System.Text.Json.Serialization;

namespace ViewInFFXIV.Shared;

public sealed class HostStatus
{
    [JsonPropertyName("alive")]
    public bool Alive { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("fps")]
    public float Fps { get; set; }

    [JsonPropertyName("generation")]
    public ulong Generation { get; set; }

    /// <summary>GPU the helper captures on.</summary>
    [JsonPropertyName("capture")]
    public string Capture { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Name of the D3D11 shared texture with the video; empty while not capturing.</summary>
    [JsonPropertyName("texture")]
    public string SharedTexture { get; set; } = "";

    [JsonPropertyName("managed")]
    public ManagedBrowserStatus Managed { get; set; } = new();
}

public sealed class ManagedBrowserStatus
{
    /// <summary>off, starting, ready, busy, error.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "off";

    [JsonPropertyName("activity")]
    public string Activity { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("roomUrl")]
    public string RoomUrl { get; set; } = "";

    [JsonPropertyName("hwnd")]
    public long Hwnd { get; set; }

    [JsonPropertyName("shown")]
    public bool Shown { get; set; }

    [JsonPropertyName("playback")]
    public PlaybackStatus? Playback { get; set; }

    [JsonIgnore]
    public bool Active => State is "starting" or "ready" or "busy";
}

/// <summary>State of the YouTube player in the room (null when nothing is loaded).</summary>
public sealed class PlaybackStatus
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("position")]
    public double Position { get; set; }

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("paused")]
    public bool Paused { get; set; }

    [JsonPropertyName("volume")]
    public int Volume { get; set; }

    /// <summary>Resolution YouTube is actually decoding, e.g. 1080 for 1920x1080.</summary>
    [JsonPropertyName("videoHeight")]
    public int VideoHeight { get; set; }

    [JsonPropertyName("subs")]
    public bool Subtitles { get; set; }

    /// <summary>False when the video has no subtitles at all (nothing to show or translate).</summary>
    [JsonPropertyName("subsAvailable")]
    public bool SubtitlesAvailable { get; set; }
}
