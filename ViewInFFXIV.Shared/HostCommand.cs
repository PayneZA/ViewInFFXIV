using System.Text.Json.Serialization;

namespace ViewInFFXIV.Shared;

public sealed class HostCommand
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = "";

    [JsonPropertyName("hwnd")]
    public long? Hwnd { get; set; }

    public static HostCommand SetWindow(long hwnd) => new() { Op = "source", Hwnd = hwnd };

    public static HostCommand ClearWindow() => new() { Op = "source", Hwnd = 0 };

    public static HostCommand ListWindows() => new() { Op = "listWindows" };

    public static HostCommand Ping() => new() { Op = "ping" };

    public static HostCommand Quit() => new() { Op = "quit" };
}
