using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ViewInFFXIV.Host;

/// <summary>
/// Minimal Chrome DevTools Protocol client bound to one page target of the managed Brave instance.
/// </summary>
internal sealed class CdpPage : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly ClientWebSocket socket;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly CancellationTokenSource cts = new();
    private readonly Dictionary<string, int> frameContexts = new();
    private int nextId;

    private CdpPage(ClientWebSocket socket)
    {
        this.socket = socket;
        _ = Task.Run(ReceiveLoopAsync);
    }

    public bool IsOpen => socket.State == WebSocketState.Open;

    /// <summary>URL of the target this client is attached to.</summary>
    public string Url { get; private init; } = "";

    public static async Task<CdpPage> ConnectAsync(int port, CancellationToken ct)
    {
        var cdp = await ConnectToTargetAsync(port, (type, _) => type == "page", ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Brave has no page to control.");
        await cdp.SendAsync("Page.enable", null, ct).ConfigureAwait(false);
        // The window lives off-screen and never has OS focus; let the page behave as if it does.
        await cdp.SendAsync("Emulation.setFocusEmulationEnabled", new { enabled = true }, ct).ConfigureAwait(false);
        return cdp;
    }

    /// <summary>
    /// Attaches to the first target (page, or cross-site iframe running in its own process) matching
    /// <paramref name="match"/>(type, url). Returns null when there is none.
    /// </summary>
    public static async Task<CdpPage?> ConnectToTargetAsync(int port, Func<string, string, bool> match, CancellationToken ct)
    {
        var json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct).ConfigureAwait(false);
        var target = JsonNode.Parse(json)!.AsArray()
            .FirstOrDefault(t => match((string?)t!["type"] ?? "", (string?)t!["url"] ?? ""));
        if (target == null)
            return null;

        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri((string)target["webSocketDebuggerUrl"]!), ct).ConfigureAwait(false);
        return new CdpPage(socket) { Url = (string?)target["url"] ?? "" };
    }

    /// <summary>Lists the URLs of all targets of the given type.</summary>
    public static async Task<List<string>> ListTargetUrlsAsync(int port, string type, CancellationToken ct)
    {
        var json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct).ConfigureAwait(false);
        return JsonNode.Parse(json)!.AsArray()
            .Where(t => (string?)t!["type"] == type)
            .Select(t => (string?)t!["url"] ?? "")
            .ToList();
    }

    /// <summary>Asks a Brave instance left over on this profile to close, so a fresh one can take the profile.</summary>
    public static async Task<bool> TryCloseBrowserAsync(int port, CancellationToken ct)
    {
        try
        {
            var json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct).ConfigureAwait(false);
            var url = (string?)JsonNode.Parse(json)?["webSocketDebuggerUrl"];
            if (url == null)
                return false;
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
            var msg = Encoding.UTF8.GetBytes("""{"id":1,"method":"Browser.close"}""");
            await socket.SendAsync(msg, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { } }, JsonOptions);
        await sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using (timeout.Token.Register(() => tcs.TrySetCanceled()))
        {
            var reply = await tcs.Task.ConfigureAwait(false);
            if (reply.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"{method}: {error.GetProperty("message").GetString()}");
            return reply.TryGetProperty("result", out var result) ? result : default;
        }
    }

    /// <summary>Evaluates JS in the page (awaiting promises) and returns the JSON value, or null.</summary>
    public async Task<JsonElement?> EvalAsync(string expression, CancellationToken ct)
    {
        var result = await SendAsync("Runtime.evaluate", new { expression, awaitPromise = true, returnByValue = true }, ct).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object)
            return null;
        if (result.TryGetProperty("exceptionDetails", out var ex))
            throw new InvalidOperationException($"Page script failed: {ex}");
        return result.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v) ? v : null;
    }

    /// <summary>
    /// Evaluates JS inside a child frame (e.g. w2g's player frame) whose URL starts with <paramref name="urlPrefix"/>.
    /// Returns null when no such frame exists.
    /// </summary>
    public async Task<JsonElement?> EvalInFrameAsync(string urlPrefix, string expression, CancellationToken ct)
    {
        var tree = await SendAsync("Page.getFrameTree", null, ct).ConfigureAwait(false);
        var frameId = FindFrame(tree.GetProperty("frameTree"), urlPrefix);
        if (frameId == null)
            return null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!frameContexts.TryGetValue(frameId, out var contextId))
            {
                var world = await SendAsync("Page.createIsolatedWorld", new { frameId, worldName = "ViewInFFXIV" }, ct).ConfigureAwait(false);
                contextId = world.GetProperty("executionContextId").GetInt32();
                frameContexts[frameId] = contextId;
            }

            try
            {
                var result = await SendAsync("Runtime.evaluate", new { expression, contextId, returnByValue = true }, ct).ConfigureAwait(false);
                return result.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v) ? v : null;
            }
            catch (InvalidOperationException)
            {
                // the frame navigated and took its context with it; make a fresh one
                frameContexts.Remove(frameId);
            }
        }

        return null;
    }

    private static string? FindFrame(JsonElement node, string urlPrefix)
    {
        var frame = node.GetProperty("frame");
        if (frame.TryGetProperty("url", out var url) && url.GetString()?.StartsWith(urlPrefix, StringComparison.Ordinal) == true
            && frame.TryGetProperty("parentId", out _))
            return frame.GetProperty("id").GetString();

        if (node.TryGetProperty("childFrames", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                if (FindFrame(child, urlPrefix) is { } id)
                    return id;
            }
        }

        return null;
    }

    public Task NavigateAsync(string url, CancellationToken ct) => SendAsync("Page.navigate", new { url }, ct);

    public Task InsertTextAsync(string text, CancellationToken ct) => SendAsync("Input.insertText", new { text }, ct);

    public async Task PressKeyAsync(string key, int virtualKey, string? text, CancellationToken ct)
    {
        await SendAsync("Input.dispatchKeyEvent", new { type = "keyDown", key, code = key, windowsVirtualKeyCode = virtualKey, text }, ct).ConfigureAwait(false);
        await SendAsync("Input.dispatchKeyEvent", new { type = "keyUp", key, code = key, windowsVirtualKeyCode = virtualKey }, ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                using var doc = JsonDocument.Parse(message.ToArray());
                message.SetLength(0);
                if (doc.RootElement.TryGetProperty("id", out var idProp)
                    && pending.TryRemove(idProp.GetInt32(), out var tcs))
                {
                    tcs.TrySetResult(doc.RootElement.Clone());
                }
            }
        }
        catch
        {
            // socket closed or browser exited
        }

        foreach (var tcs in pending.Values)
            tcs.TrySetException(new InvalidOperationException("Lost connection to Brave."));
        pending.Clear();
    }

    public void Dispose()
    {
        cts.Cancel();
        try { socket.Abort(); } catch { /* ignored */ }
        socket.Dispose();
        sendLock.Dispose();
    }
}
