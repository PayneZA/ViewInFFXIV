using System.Globalization;
using System.Text.Json;

namespace ViewInFFXIV.Host;

/// <summary>
/// Everything that knows about Twitch's player page lives here, so a Twitch redesign only breaks this file.
/// The TV only ever shows the video: while parked, the video is stretched over the page, and when there is no
/// picture (offline, loading, a content warning) our own black card covers Twitch's page instead.
/// </summary>
internal sealed class Twitch : IDisposable
{
    // Live, but no picture for this long: something on the page wants the user (pop out and look).
    private static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(12);

    /// <summary>
    /// The player-only page. "parent" names the site embedding the player; opened directly, Twitch's own domain works.
    /// </summary>
    public static string PlayerUrl(string channel) =>
        $"https://player.twitch.tv/?channel={Uri.EscapeDataString(channel)}&parent=twitch.tv&muted=false&autoplay=true";

    /// <summary>
    /// For <see cref="ManagedBrowser"/>: while parked, the video fills the page and, without a picture, a card says
    /// why; popped out, Twitch's page is back as it is. Returns true while stretched.
    /// </summary>
    public static string TheaterScript(bool on) => $$"""
        (on => {
          let style = document.getElementById('viewinffxiv-theater');
          let card = document.getElementById('viewinffxiv-card');
          if (!on) { style?.remove(); card?.remove(); return false; }
          if (!style) {
            style = document.createElement('style');
            style.id = 'viewinffxiv-theater';
            style.textContent = `
              video { position: fixed !important; inset: 0 !important; width: 100vw !important; height: 100vh !important;
                      z-index: 2147483646 !important; background: #000 !important; object-fit: contain !important; }
              #viewinffxiv-card { position: fixed; inset: 0; z-index: 2147483647; background: #000; color: #fff;
                      display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 1.4vh;
                      font-family: 'Segoe UI', sans-serif; text-align: center; }
              #viewinffxiv-card b { font-size: 6vh; font-weight: 600; }
              #viewinffxiv-card span { font-size: 3vh; opacity: .6; }
              html, body { cursor: none !important; }`;
            document.head.append(style);
          }
          const v = document.querySelector('video');
          const picture = !!v && v.readyState >= 2 && v.videoWidth > 0;
          if (picture) { card?.remove(); return true; }
          const name = document.querySelector('[data-a-target=player-info-title]')?.textContent.trim()
            || new URLSearchParams(location.search).get('channel') || 'The channel';
          const offline = /is offline/i.test(document.body.innerText);
          const [big, small] = offline
            ? [`${name} is offline`, 'The TV shows the stream as soon as it goes live.']
            : [`Waiting for ${name}`, 'The stream starts in a moment.'];
          if (!card) {
            card = document.createElement('div');
            card.id = 'viewinffxiv-card';
            card.append(document.createElement('b'), document.createElement('span'));
            document.body.append(card);
          }
          if (card.firstChild.textContent !== big) card.firstChild.textContent = big;
          if (card.lastChild.textContent !== small) card.lastChild.textContent = small;
          return true;
        })({{(on ? "true" : "false")}})
        """;

    /// <summary>
    /// Stretches the video now and returns true once the player page (not the start page) is loaded, so the TV
    /// never shows Twitch's page while the timer has yet to catch up.
    /// </summary>
    public static string ReadyScript => $"location.host === 'player.twitch.tv' && ({TheaterScript(true)})";

    // Reads the player and keeps it playing at full volume (our gain node sets the level). It never clicks anything
    // on the page: a content warning shows as "stuck", and the user pops out to answer it.
    private const string StateScript = """
        (() => {
          const v = document.querySelector('video');
          if (v) {
            if (v.muted) v.muted = false;
            if (v.volume !== 1) v.volume = 1;
          }
          const name = document.querySelector('[data-a-target=player-info-title]')?.textContent.trim() || '';
          const title = [...document.querySelectorAll('.stream-info-card__body p, .stream-info-card p')]
            .map(p => p.textContent.trim()).find(t => t && t !== name && !/^Playing /.test(t)) || '';
          return {
            name, title,
            // The name card is there but stays empty when the channel does not exist.
            named: !!document.querySelector('[data-a-target=player-info-title]')?.textContent.trim(),
            game: document.querySelector('[data-a-target=player-info-game-name]')?.textContent.trim() || '',
            playing: !!v && v.readyState >= 2 && v.videoWidth > 0 && !v.paused,
            offline: !(v && v.readyState >= 2 && v.videoWidth > 0) && /is offline/i.test(document.body.innerText),
            h: v ? v.videoHeight : 0,
          };
        })()
        """;

    // Spatial audio: the video's sound goes through a gain and a stereo panner (Web Audio), as for YouTube.
    private const string AudioScript = """
        (() => {
          const v = document.querySelector('video');
          if (!v) return false;
          let a = window.__viewinffxivAudio;
          if (!a || a.video !== v) {
            const ctx = a ? a.ctx : new AudioContext();
            const source = ctx.createMediaElementSource(v);
            const gain = ctx.createGain();
            const pan = ctx.createStereoPanner();
            source.connect(gain).connect(pan).connect(ctx.destination);
            a = window.__viewinffxivAudio = { ctx, video: v, gain, pan };
          }
          if (a.ctx.state === 'suspended') a.ctx.resume();
          const t = a.ctx.currentTime;
          a.gain.gain.setTargetAtTime(GAIN, t, 0.15);
          a.pan.pan.setTargetAtTime(PAN, t, 0.15);
          return true;
        })()
        """;

    private readonly SemaphoreSlim gate = new(1, 1);
    private double wantGain = 1;
    private double wantPan;
    private volatile bool audioDirty = true;
    private DateTime pictureSeenUtc = DateTime.UtcNow;

    /// <summary>The channel on the TV ("" while none).</summary>
    public string Channel { get; private set; } = "";

    public string Name { get; private set; } = "";

    public string Title { get; private set; } = "";

    public string Game { get; private set; } = "";

    public bool Offline { get; private set; }

    public bool Playing { get; private set; }

    public bool Stuck { get; private set; }

    /// <summary>No picture and no channel name for a while: there is no such channel.</summary>
    public bool Missing { get; private set; }

    public int VideoHeight { get; private set; }

    /// <summary>Opens the channel's player page.</summary>
    public async Task OpenAsync(CdpPage page, string channel, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Reset();
            Channel = channel;
            pictureSeenUtc = DateTime.UtcNow;
            await page.NavigateAsync(PlayerUrl(channel), ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Called every second while the stream browser runs.</summary>
    public async Task RefreshAsync(CdpPage? page)
    {
        if (page is not { IsOpen: true } || Channel.Length == 0)
            return;
        if (!await gate.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (await page.EvalAsync(StateScript, cts.Token).ConfigureAwait(false) is { ValueKind: JsonValueKind.Object } s)
            {
                Name = s.GetProperty("name").GetString() ?? "";
                Title = s.GetProperty("title").GetString() ?? "";
                Game = s.GetProperty("game").GetString() ?? "";
                Offline = s.GetProperty("offline").GetBoolean();
                Playing = s.GetProperty("playing").GetBoolean();
                VideoHeight = s.GetProperty("h").GetInt32();
                var now = DateTime.UtcNow;
                if (Playing || Offline)
                    pictureSeenUtc = now;
                var waited = now - pictureSeenUtc > StuckAfter;
                var named = s.GetProperty("named").GetBoolean();
                Stuck = waited && named;
                Missing = waited && !named;
            }

            await ApplyAudioLockedAsync(page, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // page is navigating or busy; try again next round
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Remembers the spatial audio values (gain already includes your volume); the next refresh applies them.</summary>
    public void SetAudio(double gain, double pan)
    {
        wantGain = Math.Clamp(gain, 0, 1);
        wantPan = Math.Clamp(pan, -1, 1);
        audioDirty = true;
    }

    private async Task ApplyAudioLockedAsync(CdpPage page, CancellationToken ct)
    {
        // Twitch may swap its video element (ads, reconnects); the script rebuilds the graph for a new one.
        var script = AudioScript.Replace("GAIN", Js(wantGain)).Replace("PAN", Js(wantPan));
        if (!audioDirty && Playing)
        {
            // Cheap re-check that the graph still belongs to the current video.
            if (await page.EvalAsync("(() => { const a = window.__viewinffxivAudio; return !!a && a.video === document.querySelector('video'); })()", ct).ConfigureAwait(false) is { ValueKind: JsonValueKind.True })
                return;
        }

        audioDirty = await page.EvalAsync(script, ct).ConfigureAwait(false) is not { ValueKind: JsonValueKind.True };
    }

    public void Reset()
    {
        Channel = "";
        Name = "";
        Title = "";
        Game = "";
        Offline = false;
        Playing = false;
        Stuck = false;
        Missing = false;
        VideoHeight = 0;
        audioDirty = true;
    }

    public void Dispose()
    {
        Reset();
        gate.Dispose();
    }

    private static string Js(double value) => value.ToString(CultureInfo.InvariantCulture);
}
