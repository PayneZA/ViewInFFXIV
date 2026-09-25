using System.Text.Json.Serialization;

namespace ViewInFFXIV.Shared;

public sealed class HostCommand
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = "";

    [JsonPropertyName("arg")]
    public string? Arg { get; set; }

    [JsonPropertyName("arg2")]
    public string? Arg2 { get; set; }

    public static HostCommand Ping() => new() { Op = "ping" };

    public static HostCommand Quit() => new() { Op = "quit" };

    public static HostCommand Managed(string op, string? arg = null, string? arg2 = null) => new() { Op = op, Arg = arg, Arg2 = arg2 };
}

/// <summary>High Performance mode (/viewinff): the helper launches and drives its own off-screen Brave.</summary>
public static class ManagedOps
{
    /// <summary>Arg = browser profile folder, Arg2 = the game's GPU adapter LUID (decimal).</summary>
    public const string Start = "ffStart";
    public const string Stop = "ffStop";
    public const string Create = "ffCreate";
    /// <summary>Arg = w2g link, Arg2 = optional nickname (typed into w2g's guest welcome screen).</summary>
    public const string Join = "ffJoin";
    public const string Name = "ffName";
    public const string Play = "ffPlay";
    public const string Show = "ffShow";
    public const string Hide = "ffHide";

    /// <summary>Playback control; see <see cref="PlaybackCommands"/> for the argument format.</summary>
    public const string Control = "ffControl";

    public static bool IsManaged(string op) => op.StartsWith("ff", StringComparison.Ordinal);
}

/// <summary>Arguments for <see cref="ManagedOps.Control"/>.</summary>
public static class PlaybackCommands
{
    public const string Toggle = "toggle";

    public static string Seek(double seconds) => $"seek:{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public static string Skip(double seconds) => $"skip:{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public static string Volume(int percent) => $"volume:{percent}";

    /// <summary>English subtitles on (English track, else auto-generated English, else auto-translated) or off.</summary>
    public static string Subtitles(bool on) => on ? "cc:on" : "cc:off";
}
