using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ViewInFFXIV.Media;

/// <summary>
/// Whether a YouTube video can play in a watch party. Watch2Gether plays YouTube through the embedded player, so a
/// video whose owner turned embedding off (or that is private or removed) shows w2g's error page on everyone's wall
/// instead. YouTube's oEmbed endpoint tells us first: 200 with the title, 401/403 when embedding is off, 404 when the
/// video is gone. Results are cached for the session.
/// </summary>
/// <remarks>
/// EXTERNAL ASSUMPTION P1 (docs/renderer.md): the oEmbed status codes above. If YouTube changes them, the worst case
/// is the old behaviour: any other answer counts as "unknown" and the video is sent to the room as before.
/// </remarks>
public sealed class Playability
{
    private const string Endpoint = "https://www.youtube.com/oembed?format=json&url=https://www.youtube.com/watch?v=";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    private readonly ConcurrentDictionary<string, Task<Result>> checks = new();
    private readonly SemaphoreSlim parallel = new(4);

    public enum State
    {
        /// <summary>Not known (yet, or the check failed): treat as playable.</summary>
        Unknown,
        Playable,

        /// <summary>The owner does not allow the video on other sites.</summary>
        Blocked,

        /// <summary>Private, removed, or not a real video.</summary>
        Gone,
    }

    public sealed record Result(State State, string Title = "", string Channel = "");

    /// <summary>The cached answer, starting a check in the background if there is none. Never blocks.</summary>
    public State Peek(string videoId)
    {
        var task = Check(videoId);
        return task.IsCompletedSuccessfully ? task.Result.State : State.Unknown;
    }

    public Task<Result> Check(string videoId) => checks.GetOrAdd(videoId, id => Task.Run(() => Fetch(id)));

    /// <summary>Why a video cannot play, for notifications and row labels.</summary>
    public static string Reason(State state) => state switch
    {
        State.Blocked => "Can't play in a watch party: its owner turned off playing it on other sites.",
        State.Gone => "Can't play: it's private, removed, or the link is wrong.",
        _ => "",
    };

    private async Task<Result> Fetch(string videoId)
    {
        await parallel.WaitAsync().ConfigureAwait(false);
        try
        {
            using var response = await Http.GetAsync(Endpoint + Uri.EscapeDataString(videoId)).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return new Result(State.Blocked);
                case HttpStatusCode.NotFound or HttpStatusCode.BadRequest:
                    return new Result(State.Gone);
                case HttpStatusCode.OK:
                    await using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
                        var root = doc.RootElement;
                        return new Result(State.Playable, Str(root, "title"), Str(root, "author_name"));
                    }

                default:
                    checks.TryRemove(videoId, out _); // try again next time
                    return new Result(State.Unknown);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Playability check failed for {Video}", videoId);
            checks.TryRemove(videoId, out _);
            return new Result(State.Unknown);
        }
        finally
        {
            parallel.Release();
        }
    }

    private static string Str(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
