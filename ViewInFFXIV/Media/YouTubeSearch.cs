using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ViewInFFXIV.Media;

/// <summary>A YouTube video as shown in search results, the queue and the history.</summary>
[Serializable]
public sealed class VideoInfo
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string Channel { get; set; } = "";

    /// <summary>"12:34"; empty for live streams.</summary>
    public string Duration { get; set; } = "";

    public string Views { get; set; } = "";

    public string Age { get; set; } = "";

    public string Url => $"https://www.youtube.com/watch?v={Id}";

    public bool IsLive => Duration.Length == 0;
}

/// <summary>
/// YouTube search through the endpoint youtube.com's own search box uses (no API key). Everything that knows
/// about YouTube's response format lives here; if it changes, search fails and links still work.
/// </summary>
public static partial class YouTubeSearch
{
    private const string Endpoint = "https://www.youtube.com/youtubei/v1/search?prettyPrint=false";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<List<VideoInfo>> SearchAsync(string query, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            context = new { client = new { clientName = "WEB", clientVersion = "2.20250101.00.00", hl = "en", gl = "US" } },
            query,
        });
        using var response = await Http.PostAsync(Endpoint, new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var results = new List<VideoInfo>();
        var seen = new HashSet<string>();
        Collect(doc.RootElement, results, seen);
        return results;
    }

    /// <summary>The video id in a YouTube link (watch, youtu.be, shorts, embed, live), or null.</summary>
    public static string? FindVideoId(string text)
    {
        var match = VideoIdRegex().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static void Collect(JsonElement node, List<VideoInfo> results, HashSet<string> seen)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (property.NameEquals("videoRenderer") && Parse(property.Value) is { } video && seen.Add(video.Id))
                        results.Add(video);
                    else
                        Collect(property.Value, results, seen);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                    Collect(item, results, seen);
                break;
        }
    }

    private static VideoInfo? Parse(JsonElement v)
    {
        var id = Str(v, "videoId");
        if (id.Length == 0)
            return null;

        return new VideoInfo
        {
            Id = id,
            Title = Text(v, "title"),
            Channel = Text(v, "ownerText") is { Length: > 0 } owner ? owner : Text(v, "longBylineText"),
            Duration = Text(v, "lengthText"),
            Views = Text(v, "shortViewCountText"),
            Age = Text(v, "publishedTimeText"),
        };
    }

    /// <summary>YouTube text objects are either { simpleText } or { runs: [{ text }] }.</summary>
    private static string Text(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var t) || t.ValueKind != JsonValueKind.Object)
            return "";
        if (t.TryGetProperty("simpleText", out var simple))
            return simple.GetString() ?? "";
        if (!t.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            return "";

        var sb = new StringBuilder();
        foreach (var run in runs.EnumerateArray())
            sb.Append(Str(run, "text"));
        return sb.ToString();
    }

    private static string Str(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    [GeneratedRegex(@"(?:youtube\.com/(?:watch\?(?:\S*&)?v=|shorts/|embed/|live/)|youtu\.be/)([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase)]
    private static partial Regex VideoIdRegex();
}
