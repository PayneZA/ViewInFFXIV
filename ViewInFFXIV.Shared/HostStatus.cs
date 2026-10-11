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

    /// <summary>The stream browser (Twitch). While it runs, the TV shows it instead of the party.</summary>
    [JsonPropertyName("stream")]
    public StreamStatus Stream { get; set; } = new();

    /// <summary>One of your windows on the TV. While it is shared, the TV shows it instead of Twitch or the party.</summary>
    [JsonPropertyName("window")]
    public WindowStatus Window { get; set; } = new();
}

/// <summary>A window of yours on the TV, fitted with its aspect ratio kept.</summary>
public sealed class WindowStatus
{
    /// <summary>off, ready, closed (the window went away), error.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "off";

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("hwnd")]
    public long Hwnd { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>The app's own name, e.g. "Brave Browser" (<see cref="AppName"/>).</summary>
    [JsonPropertyName("app")]
    public string App { get; set; } = "";

    /// <summary>Size of what goes on the TV (the window's inside), in pixels.</summary>
    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>Minimised: Windows draws nothing for it, so the TV holds the last picture.</summary>
    [JsonPropertyName("minimized")]
    public bool Minimized { get; set; }

    [JsonPropertyName("maximized")]
    public bool Maximized { get; set; }

    /// <summary>
    /// ViewInFFXIV may show, enlarge and move it. False for apps running as administrator: Windows doesn't allow it.
    /// </summary>
    [JsonPropertyName("controllable")]
    public bool Controllable { get; set; }

    /// <summary>It can be made to fill its monitor (it has a maximise button).</summary>
    [JsonPropertyName("canEnlarge")]
    public bool CanEnlarge { get; set; }

    /// <summary>Another window covers it completely.</summary>
    [JsonPropertyName("covered")]
    public bool Covered { get; set; }

    /// <summary>The window covering it is the game.</summary>
    [JsonPropertyName("coveredByGame")]
    public bool CoveredByGame { get; set; }

    /// <summary>
    /// Covered and drawing nothing: browser-type apps (Chrome, Edge, Discord, Spotify...) pause while hidden, and
    /// so does anything that was moving and froze when it got covered.
    /// </summary>
    [JsonPropertyName("paused")]
    public bool Paused { get; set; }

    /// <summary>It sits on the same monitor as the game.</summary>
    [JsonPropertyName("onGameMonitor")]
    public bool OnGameMonitor { get; set; }

    /// <summary>You have a monitor the game isn't on, to move it to.</summary>
    [JsonPropertyName("otherMonitor")]
    public bool OtherMonitor { get; set; }

    [JsonIgnore]
    public bool Active => State == "ready";
}

/// <summary>A live stream on the TV (Twitch), in its own browser and profile.</summary>
public sealed class StreamStatus
{
    /// <summary>off, starting, ready, error.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "off";

    [JsonPropertyName("activity")]
    public string Activity { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Twitch channel login, e.g. "monstercat".</summary>
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";

    /// <summary>The channel's display name, as the player shows it.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>Game or category.</summary>
    [JsonPropertyName("game")]
    public string Game { get; set; } = "";

    /// <summary>The player says the channel is offline.</summary>
    [JsonPropertyName("offline")]
    public bool Offline { get; set; }

    /// <summary>Frames are coming in (the video is on the TV).</summary>
    [JsonPropertyName("playing")]
    public bool Playing { get; set; }

    /// <summary>Live, but no picture for a while: something on the page (a content warning, a prompt) wants you.</summary>
    [JsonPropertyName("stuck")]
    public bool Stuck { get; set; }

    /// <summary>Twitch has no channel by that name.</summary>
    [JsonPropertyName("missing")]
    public bool Missing { get; set; }

    [JsonPropertyName("videoHeight")]
    public int VideoHeight { get; set; }

    [JsonPropertyName("hwnd")]
    public long Hwnd { get; set; }

    /// <summary>Popped out onto your monitor.</summary>
    [JsonPropertyName("shown")]
    public bool Shown { get; set; }

    [JsonIgnore]
    public bool Active => State is "starting" or "ready";
}

public sealed class ManagedBrowserStatus
{
    /// <summary>off, starting, ready, busy, error.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "off";

    [JsonPropertyName("activity")]
    public string Activity { get; set; } = "";

    /// <summary>Name of the browser the helper runs ("Brave", "Google Chrome", ...); empty while none runs.</summary>
    [JsonPropertyName("browser")]
    public string Browser { get; set; } = "";

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

    /// <summary>
    /// The room still holds w2g's sample video (<see cref="PlaybackStatus.IsW2gSample"/>). Playback is null then,
    /// and the picture stays blank: the paused sample still draws its player bar.
    /// </summary>
    [JsonPropertyName("sample")]
    public bool SampleVideo { get; set; }

    [JsonIgnore]
    public bool Active => State is "starting" or "ready" or "busy";
}

/// <summary>State of the YouTube player in the room (null when nothing is loaded).</summary>
public sealed class PlaybackStatus
{
    /// <summary>
    /// The sample video w2g puts in every new room ("Watch2Gether - Cats vs. Dogs"). Until someone plays something
    /// it counts as nothing playing, so it never shows as the room's video or lands in Recent.
    /// </summary>
    public static bool IsW2gSample(string videoId, string title, string channel) =>
        videoId == "p5wTH5wFEwg"
        || channel.Equals("Watch2Gether", StringComparison.OrdinalIgnoreCase)
        || title.StartsWith("Watch2Gether - ", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>What YouTube shows instead of the video ("Video unavailable", age check…), if anything.</summary>
    [JsonPropertyName("playerError")]
    public string PlayerError { get; set; } = "";

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
