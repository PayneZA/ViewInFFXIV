using System.Text.Json;

namespace ViewInFFXIV.Host;

/// <summary>
/// Everything that knows about the w2g.tv page lives here, so a w2g redesign only breaks this file.
/// </summary>
internal static class W2g
{
    public const string Home = "https://w2g.tv/";

    // Shared page helpers prepended to every script.
    private const string Js = """
        const vis = e => !!e && e.getClientRects().length > 0 && getComputedStyle(e).visibility !== 'hidden';
        const btn = t => [...document.querySelectorAll('button,a,[role=button]')].find(b => vis(b) && b.innerText.trim().toLowerCase() === t.toLowerCase());
        const input = re => [...document.querySelectorAll('input')].find(i => vis(i) && re.test(i.placeholder));
        const roomKeys = () => Object.keys(localStorage).filter(k => k.endsWith('_history_new')).map(k => k.slice(0, -'_history_new'.length));
        // The Invite dialog's link is authoritative; otherwise w2g's per-room storage key that appeared since `known`.
        const invite = known => {
          const i = [...document.querySelectorAll('input')].find(i => /w2g\.tv\/\?r=/.test(i.value));
          if (i) return i.value;
          const k = roomKeys().find(k => !known.includes(k));
          return k ? 'https://w2g.tv/?r=' + k : '';
        };
        """;

    /// <summary>
    /// While Brave is hidden, stretch w2g's player over the whole page (hiding chat, sidebars and dialogs)
    /// so YouTube sees a full-size player and serves full resolution. Returns true while the player is stretched.
    /// </summary>
    public static string TheaterScript(bool on) => $$"""
        (on => {
          const video = document.getElementById('w2g-video');
          let style = document.getElementById('viewinffxiv-theater');
          if (!on || !video) { style?.remove(); return false; }
          if (!style) {
            style = document.createElement('style');
            style.id = 'viewinffxiv-theater';
            style.textContent = '#w2g-video { position: fixed !important; inset: 0 !important; width: 100vw !important; height: 100vh !important; z-index: 2147483646 !important; background: #000 !important; }';
            document.head.append(style);
          }
          return true;
        })({{(on ? "true" : "false")}})
        """;

    // Each evaluation gets its own scope so the helpers can be redeclared every call.
    private static string Wrap(string expression) => $"(() => {{\n{Js}\nreturn ({expression});\n}})()";

    public static bool IsW2gUrl(string? url, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out uri!)
            && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host.Equals("w2g.tv", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".w2g.tv", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Creates a new room and returns its invitation link.</summary>
    public static async Task<string> CreateRoomAsync(CdpPage page, CancellationToken ct)
    {
        await page.NavigateAsync(Home, ct).ConfigureAwait(false);
        await WaitAsync(page, "!!btn('Create your room')", TimeSpan.FromSeconds(25), "w2g.tv did not load.", ct).ConfigureAwait(false);
        var known = await page.EvalAsync(Wrap("JSON.stringify(roomKeys())"), ct).ConfigureAwait(false);
        await page.EvalAsync(Wrap("(btn('Create your room').click(), true)"), ct).ConfigureAwait(false);
        await WaitAsync(page, "location.pathname.includes('/room')", TimeSpan.FromSeconds(25), "w2g did not create a room.", ct).ConfigureAwait(false);
        var link = await ReadInviteAsync(page, known?.GetString() ?? "[]", ct).ConfigureAwait(false);
        await CloseDialogsAsync(page, ct).ConfigureAwait(false);
        return link;
    }

    /// <summary>
    /// Opens an invitation or room link and returns it. Guests get a "Pick a nickname / Join the Room"
    /// welcome screen first: the name goes in there, otherwise it is set afterwards.
    /// </summary>
    public static async Task<string> JoinAsync(CdpPage page, string url, string? name, CancellationToken ct)
    {
        if (!IsW2gUrl(url, out var uri))
            throw new InvalidOperationException("That is not a w2g.tv link.");

        await page.NavigateAsync(uri.AbsoluteUri, ct).ConfigureAwait(false);
        await WaitAsync(page, "location.pathname.includes('/room')", TimeSpan.FromSeconds(25), "Could not open that w2g room.", ct).ConfigureAwait(false);

        // Wait for either the guest welcome screen, or your own user tile (returning visitors join straight in).
        // The room's inputs are no signal: they render behind the welcome screen.
        const string ready = "vis(document.getElementById('intro-nickname')) || !!document.querySelector('.self')";
        await WaitAsync(page, ready, TimeSpan.FromSeconds(15), "The w2g room did not finish loading.", ct).ConfigureAwait(false);
        var welcome = await TryEvalAsync(page, "vis(document.getElementById('intro-nickname'))", ct).ConfigureAwait(false);
        if (welcome is { ValueKind: JsonValueKind.True })
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                await page.EvalAsync(Wrap("(() => { const i = document.getElementById('intro-nickname'); i.focus(); i.select(); return true; })()"), ct).ConfigureAwait(false);
                await page.InsertTextAsync(name.Trim(), ct).ConfigureAwait(false);
            }

            const string join = "[...document.querySelectorAll('.w2g-button,button')].find(b => vis(b) && b.textContent.trim() === 'Join the Room')";
            await WaitAsync(page, $"({join})?.click() ?? false, !vis(document.getElementById('intro-nickname'))", TimeSpan.FromSeconds(10), "Could not press Join the Room.", ct).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            await SetNameAsync(page, name, ct).ConfigureAwait(false);
        }

        await CloseDialogsAsync(page, ct).ConfigureAwait(false);
        return uri.AbsoluteUri;
    }

    public static async Task SetNameAsync(CdpPage page, string name, CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length == 0)
            return;

        // Your own tile in the user list opens the nickname editor. Every tile carries the same classes;
        // only yours sits inside a `.self` wrapper.
        const string open = """
            (() => {
              if (input(/nickname/i)) return true;
              const self = [...document.querySelectorAll('.self [class*="[.self_&]:cursor-pointer"]')].find(vis);
              if (self) { self.click(); return true; }
              return false;
            })()
            """;
        await WaitAsync(page, open, TimeSpan.FromSeconds(15), "Could not find the w2g name editor.", ct).ConfigureAwait(false);
        await WaitAsync(page, "!!input(/nickname/i)", TimeSpan.FromSeconds(5), "The w2g name editor did not open.", ct).ConfigureAwait(false);
        await page.EvalAsync(Wrap("(() => { const i = input(/nickname/i); i.focus(); i.select(); return true; })()"), ct).ConfigureAwait(false);
        await page.InsertTextAsync(name, ct).ConfigureAwait(false);
        await page.PressKeyAsync("Enter", 13, "\r", ct).ConfigureAwait(false);
    }

    public static async Task PlayAsync(CdpPage page, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("Play needs a video link (https://…).");

        const string focus = """
            (() => {
              const i = input(/quick play/i) || input(/paste a video link/i);
              if (!i) return false;
              i.focus(); i.select();
              return true;
            })()
            """;
        await WaitAsync(page, focus, TimeSpan.FromSeconds(10), "Join or create a room first.", ct).ConfigureAwait(false);
        await page.InsertTextAsync(uri.AbsoluteUri, ct).ConfigureAwait(false);
        await page.PressKeyAsync("Enter", 13, "\r", ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadInviteAsync(CdpPage page, string knownKeysJson, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        var nextInviteClick = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            var value = await TryEvalAsync(page, $"invite({knownKeysJson})", ct).ConfigureAwait(false);
            if (value is { ValueKind: JsonValueKind.String } s && s.GetString() is { Length: > 0 } link)
                return link;

            // The invite dialog only opens by itself in your very first room; open it ourselves.
            if (DateTime.UtcNow > nextInviteClick)
            {
                await TryEvalAsync(page, "(btn('Invite')?.click(), true)", ct).ConfigureAwait(false);
                nextInviteClick = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return "";
    }

    private static async Task CloseDialogsAsync(CdpPage page, CancellationToken ct)
    {
        const string close = """
            (() => {
              let n = 0;
              // w2g's dialog buttons are styled divs (.w2g-button), not <button>s.
              for (const b of document.querySelectorAll('button,.w2g-button')) {
                if (vis(b) && b.textContent.trim() === 'Close') { b.click(); n++; }
              }
              return n;
            })()
            """;
        await TryEvalAsync(page, close, ct).ConfigureAwait(false);
        await page.PressKeyAsync("Escape", 27, null, ct).ConfigureAwait(false);
    }

    private static async Task WaitAsync(CdpPage page, string condition, TimeSpan timeout, string failure, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = await TryEvalAsync(page, $"!!({condition})", ct).ConfigureAwait(false);
            if (value is { ValueKind: JsonValueKind.True })
                return;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        throw new TimeoutException(failure);
    }

    private static async Task<JsonElement?> TryEvalAsync(CdpPage page, string script, CancellationToken ct)
    {
        try
        {
            return await page.EvalAsync(Wrap(script), ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // the page is mid-navigation; the caller polls again
            return null;
        }
    }
}
