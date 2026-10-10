using System.Text.Json.Serialization;

namespace ViewInFFXIV.Shared;

public sealed class HostStatus
{
    [JsonPropertyName("alive")]
    public bool Alive { get; set; }

    /// <summary>Path of the running helper exe; with <see cref="ExeStamp"/> it tells a stale helper from a current one.</summary>
    [JsonPropertyName("exe")]
    public string Exe { get; set; } = "";

    /// <summary>Last-write time (UTC ticks) of the helper exe when it started.</summary>
    [JsonPropertyName("exeStamp")]
    public long ExeStamp { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("fps")]
    public float Fps { get; set; }

    /// <summary>Frames per second Windows delivers from the Brave window.</summary>
    [JsonPropertyName("arrivedFps")]
    public float ArrivedFps { get; set; }

    /// <summary>Of those, dropped per second because the game had not taken the previous frame yet.</summary>
    [JsonPropertyName("skippedFps")]
    public float SkippedFps { get; set; }

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
    /// <summary>YouTube video id.</summary>
    [JsonPropertyName("id")]
    public string VideoId { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>Channel name.</summary>
    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("position")]
    public double Position { get; set; }

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("paused")]
    public bool Paused { get; set; }

    /// <summary>The video played to the end.</summary>
    [JsonPropertyName("ended")]
    public bool Ended { get; set; }

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

    /// <summary>Codecs YouTube is playing, e.g. "vp09.00.51.08 (303) / opus (251)".</summary>
    [JsonPropertyName("codec")]
    public string Codec { get; set; } = "";

    /// <summary>Frames the browser decoded / dropped for this video.</summary>
    [JsonPropertyName("decodedFrames")]
    public int DecodedFrames { get; set; }

    [JsonPropertyName("droppedFrames")]
    public int DroppedFrames { get; set; }
}
