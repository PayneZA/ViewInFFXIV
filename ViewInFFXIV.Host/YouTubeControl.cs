using System.Globalization;
using System.Text.Json;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Host;

/// <summary>
/// Drives the YouTube embed inside the w2g room through YouTube's own player API (#movie_player).
/// Play/pause/seek made this way sync to the whole w2g room; captions and volume stay local.
/// The embed runs in its own renderer process, so it is a separate DevTools target from the room page.
/// </summary>
internal sealed class YouTubeControl : IDisposable
{
    private const string StateScript = """
        (() => {
          const mp = document.getElementById('movie_player');
          if (!mp || !mp.getPlayerState) return null;
          const d = mp.getVideoData ? mp.getVideoData() : {};
          let tr = null, list = [];
          try {
            tr = mp.getOption('captions', 'track');
            list = mp.getOption('captions', 'tracklist') || [];
          } catch (e) { }
          const video = document.querySelector('video');
          const q = video && video.getVideoPlaybackQuality ? video.getVideoPlaybackQuality() : {};
          let codec = '';
          try { codec = (mp.getStatsForNerds && mp.getStatsForNerds().codecs) || ''; } catch (e) { }
          return {
            id: d.video_id || '', title: d.title || '', author: d.author || '',
            position: mp.getCurrentTime() || 0, duration: mp.getDuration() || 0, state: mp.getPlayerState(),
            volume: mp.isMuted() ? 0 : mp.getVolume(),
            videoHeight: (video || {}).videoHeight || 0,
            subs: !!(tr && tr.languageCode), subsAvailable: list.length > 0 || !!(tr && tr.languageCode),
            codec, decodedFrames: q.totalVideoFrames || 0, droppedFrames: q.droppedVideoFrames || 0,
          };
        })()
        """;

    private readonly SemaphoreSlim gate = new(1, 1);
    private CdpPage? player;
    private string videoId = "";
    private DateTime videoStartedUtc;

    // What the user picked, re-applied whenever the next video loads (null = leave YouTube's default).
    private bool? wantSubtitles;
    private int? wantVolume;

    // Video found to have no subtitle tracks at all, so the toggle can be greyed out.
    private string noSubtitlesVideo = "";

    // Video whose quality has been pinned already (levels are only known once it starts loading).
    private string qualityVideo = "";

    // Best quality up to 1080p: the wall texture is 1920x1080, anything higher is wasted decoding.
    private const string QualityScript = """
        const order = ['hd1080', 'hd720', 'large', 'medium', 'small', 'tiny'];
        const levels = mp.getAvailableQualityLevels() || [];
        const best = order.find(q => levels.includes(q));
        if (!best) return false;
        if (mp.getPlaybackQuality() !== best || !mp.setPlaybackQualityRange) {
          if (mp.setPlaybackQualityRange) mp.setPlaybackQualityRange(best, best); else mp.setPlaybackQuality(best);
        }
        return true;
        """;

    // The wall shows only the picture: YouTube's own title bar, controls, end-screen tiles, watermark and
    // play/pause flash are hidden. Errors ("Video unavailable") and subtitles stay visible.
    private const string HideChromeScript = """
        (() => {
          if (document.getElementById('viewinffxiv-clean-2')) return true;
          const style = document.createElement('style');
          style.id = 'viewinffxiv-clean-2';
          // #player-controls / #bottom-sheet-wrapper: the 2026 embed controls; .ytp-*: the classic player.
          style.textContent = `
            #player-controls, #bottom-sheet-wrapper, .ytp-unmute,
            .ytp-chrome-top, .ytp-chrome-bottom, .ytp-gradient-top, .ytp-gradient-bottom, .ytp-pause-overlay,
            .ytp-watermark, .ytp-youtube-button, .ytp-show-cards-title, .ytp-ce-element, .ytp-cards-teaser,
            .ytp-cards-button, .ytp-paid-content-overlay, .ytp-impression-link, .ytp-large-play-button,
            .ytp-bezel, .ytp-bezel-text-wrapper, .html5-endscreen, .ytp-endscreen-content, .ytp-autonav-endscreen,
            .ytp-tooltip, .ytp-contextmenu, .iv-branding, .annotation, .ytp-featured-product, .ytp-overflow-panel
            { display: none !important; }`;
          (document.head || document.documentElement).append(style);
          return true;
        })()
        """;

    // Spatial audio: the video's sound goes through a gain and a stereo panner (Web Audio) in the player page.
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

    private CdpPage? cleanedPlayer;
    private CdpPage? audioPlayer;
    private double wantGain = 1;
    private double wantPan;
    private volatile bool audioDirty;

    public PlaybackStatus? Status { get; private set; }

    public async Task RefreshAsync(int port)
    {
        if (port == 0)
        {
            Reset();
            return;
        }

        // A command is running; it refreshes when done.
        if (!await gate.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await RefreshLockedAsync(port, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            Status = null;
            DropPlayer();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CommandAsync(int port, string command)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ct = cts.Token;
            await EnsurePlayerAsync(port, ct).ConfigureAwait(false);
            if (player == null)
                throw new InvalidOperationException("No YouTube video is loaded in the room.");

            var colon = command.IndexOf(':');
            var verb = colon < 0 ? command : command[..colon];
            var value = colon < 0 ? "" : command[(colon + 1)..];
            var script = verb switch
            {
                "toggle" => "mp.getPlayerState() === 1 ? mp.pauseVideo() : mp.playVideo()",
                "play" => "mp.playVideo()",
                "pause" => "mp.pauseVideo()",
                "seek" => $"mp.seekTo({Js(Parse(value))}, true)",
                "skip" => $"mp.seekTo(Math.max(0, mp.getCurrentTime() + ({Js(Parse(value))})), true)",
                "volume" => SetVolume((int)Parse(value)),
                "cc" => SetSubtitles(value == "on"),
                _ => throw new InvalidOperationException($"Unknown playback command '{verb}'."),
            };

            var ok = await RunAsync(script, ct).ConfigureAwait(false);
            if (verb == "cc" && value == "on" && !ok)
            {
                noSubtitlesVideo = videoId;
                throw new InvalidOperationException("This video has no subtitles.");
            }

            await RefreshLockedAsync(port, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Remembers the spatial audio values; <see cref="ApplyAudioAsync"/> or the next refresh applies them.</summary>
    public void SetAudio(string? gain, string? pan)
    {
        if (double.TryParse(gain, NumberStyles.Float, CultureInfo.InvariantCulture, out var g) && double.IsFinite(g))
            wantGain = Math.Clamp(g, 0, 1);
        if (double.TryParse(pan, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && double.IsFinite(p))
            wantPan = Math.Clamp(p, -1, 1);
        audioDirty = true;
    }

    public async Task ApplyAudioAsync(int port)
    {
        if (port == 0 || !await gate.WaitAsync(0).ConfigureAwait(false))
            return; // the refresh running now applies it

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await EnsurePlayerAsync(port, cts.Token).ConfigureAwait(false);
            await ApplyAudioLockedAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // the next refresh tries again
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ApplyAudioLockedAsync(CancellationToken ct)
    {
        if (player == null || (!audioDirty && audioPlayer == player))
            return;
        // Untouched pages play at full volume, centred: only build the audio graph once spatial audio is used.
        if (audioPlayer != player && wantGain >= 0.999 && Math.Abs(wantPan) < 0.001)
        {
            audioDirty = false;
            return;
        }

        audioDirty = false;
        var script = AudioScript.Replace("GAIN", Js(wantGain)).Replace("PAN", Js(wantPan));
        if (await player.EvalAsync(script, ct).ConfigureAwait(false) is { ValueKind: JsonValueKind.True })
            audioPlayer = player;
        else
            audioDirty = true;
    }

    public void Reset()
    {
        Status = null;
        videoId = "";
        DropPlayer();
    }

    public void Dispose() => Reset();

    private async Task RefreshLockedAsync(int port, CancellationToken ct)
    {
        await EnsurePlayerAsync(port, ct).ConfigureAwait(false);
        if (player == null)
        {
            Status = null;
            return;
        }

        if (cleanedPlayer != player)
        {
            await player.EvalAsync(HideChromeScript, ct).ConfigureAwait(false);
            cleanedPlayer = player;
        }

        if (audioDirty || audioPlayer != player)
        {
            try
            {
                await ApplyAudioLockedAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                audioDirty = true;
            }
        }

        var value = await player.EvalAsync(StateScript, ct).ConfigureAwait(false);
        if (value is not { ValueKind: JsonValueKind.Object } v)
        {
            Status = null;
            return;
        }

        var id = v.GetProperty("id").GetString() ?? "";
        if (id.Length > 0 && id != videoId)
        {
            // New video: YouTube forgets caption choices per video, so put them back.
            videoId = id;
            videoStartedUtc = DateTime.UtcNow;
            if (await ReapplyAsync().ConfigureAwait(false))
            {
                // Re-applying may have used up this poll's time budget; read the new state on a fresh one.
                using var fresh = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                v = await player.EvalAsync(StateScript, fresh.Token).ConfigureAwait(false) ?? v;
            }
        }

        if (id.Length > 0 && id != qualityVideo)
        {
            try
            {
                using var fresh = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (await RunAsync(QualityScript, fresh.Token).ConfigureAwait(false))
                    qualityVideo = id;
            }
            catch
            {
                // try again on the next poll
            }
        }

        var status = v.Deserialize<PlaybackStatus>() ?? new PlaybackStatus();

        // YouTube can switch its own default captions on a few seconds into a video, after the choice above
        // was applied. Keep insisting on the user's choice for a little while.
        if (wantSubtitles is { } want && status.Subtitles != want && !(want && id == noSubtitlesVideo)
            && DateTime.UtcNow - videoStartedUtc < TimeSpan.FromSeconds(15))
        {
            try
            {
                using var fresh = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                if (!await RunAsync(SubtitlesScript(want), fresh.Token).ConfigureAwait(false) && want)
                    noSubtitlesVideo = id;
                status.Subtitles = want && id != noSubtitlesVideo;
            }
            catch
            {
                // try again on the next poll
            }
        }
        // 1 = playing, 3 = buffering; everything else (paused, ended, cued, unstarted) shows as paused.
        var state = v.GetProperty("state").GetInt32();
        status.Paused = state is not (1 or 3);
        status.Ended = state == 0;
        // The track list only fills once YouTube's caption module loads, so assume subtitles exist
        // until turning them on proved otherwise.
        status.SubtitlesAvailable |= id != noSubtitlesVideo;
        Status = status;
    }

    /// <summary>Attaches to the YouTube embed, replacing a stale connection when w2g swapped the iframe.</summary>
    private async Task EnsurePlayerAsync(int port, CancellationToken ct)
    {
        if (player is { IsOpen: true })
        {
            var urls = await CdpPage.ListTargetUrlsAsync(port, "iframe", ct).ConfigureAwait(false);
            if (urls.Contains(player.Url))
                return;
        }

        DropPlayer();
        player = await CdpPage.ConnectToTargetAsync(port, (type, url) => type == "iframe" && IsYouTubeEmbed(url), ct).ConfigureAwait(false);
        videoId = "";
    }

    /// <summary>Re-applies the subtitle/volume choices to a newly loaded video. Never fails the status refresh.</summary>
    private async Task<bool> ReapplyAsync()
    {
        var applied = false;
        try
        {
            // Own timeout: finding tracks can take a few seconds, longer than a status poll.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            if (wantVolume is { } volume)
            {
                await RunAsync(VolumeScript(volume), cts.Token).ConfigureAwait(false);
                applied = true;
            }

            if (wantSubtitles is { } on && !(on && videoId == noSubtitlesVideo))
            {
                if (!await RunAsync(SubtitlesScript(on), cts.Token).ConfigureAwait(false) && on)
                    noSubtitlesVideo = videoId;
                applied = true;
            }
        }
        catch
        {
            // leave it; the next command or video tries again
        }

        return applied;
    }

    private string SetVolume(int volume)
    {
        wantVolume = Math.Clamp(volume, 0, 100);
        return VolumeScript(wantVolume.Value);
    }

    private static string VolumeScript(int volume) => $"(mp.unMute(), mp.setVolume({volume}))";

    private string SetSubtitles(bool on)
    {
        wantSubtitles = on;
        return SubtitlesScript(on);
    }

    /// <summary>
    /// English subtitles: an English track, else YouTube's auto-generated English, else the first track
    /// auto-translated to English.
    /// </summary>
    private static string SubtitlesScript(bool on) => on
        ? """
          return (async () => {
            const sleep = ms => new Promise(r => setTimeout(r, ms));
            const active = () => { const t = mp.getOption('captions', 'track'); return !!(t && t.languageCode); };
            let list = mp.getOption('captions', 'tracklist') || [];
            if (!list.length) {
              // The track list arrives with YouTube's caption module (and stays empty for auto-generated-only videos).
              mp.loadModule('captions');
              for (let i = 0; i < 15 && !list.length; i++) { await sleep(100); list = mp.getOption('captions', 'tracklist') || []; }
            }
            const en = list.find(t => t.languageCode.startsWith('en') && !t.kind) || list.find(t => t.languageCode.startsWith('en'));
            let track;
            if (en) { track = { languageCode: en.languageCode }; if (en.kind) track.kind = en.kind; }
            else if (list[0]) { track = { languageCode: list[0].languageCode, translationLanguage: { languageCode: 'en' } }; if (list[0].kind) track.kind = list[0].kind; }
            else track = { languageCode: 'en' }; // YouTube resolves this to auto-generated English when that is all there is
            mp.setOption('captions', 'track', track);
            for (let i = 0; i < 20 && !active(); i++) await sleep(100);
            return active();
          })();
          """
        : "mp.setOption('captions', 'track', {})";

    /// <summary>Runs statements with `mp` bound to the player; a script may `return` a (promised) false to signal failure.</summary>
    private async Task<bool> RunAsync(string body, CancellationToken ct)
    {
        var result = await player!.EvalAsync(
            $"(async mp => {{ const r = await (async () => {{ {body}; }})(); return r !== false; }})(document.getElementById('movie_player'))",
            ct).ConfigureAwait(false);
        return result is not { ValueKind: JsonValueKind.False };
    }

    private void DropPlayer()
    {
        player?.Dispose();
        player = null;
    }

    private static bool IsYouTubeEmbed(string url) =>
        url.Contains("youtube.com/embed/", StringComparison.OrdinalIgnoreCase)
        || url.Contains("youtube-nocookie.com/embed/", StringComparison.OrdinalIgnoreCase);

    private static double Parse(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)
            ? n
            : throw new InvalidOperationException($"'{value}' is not a number.");

    private static string Js(double value) => value.ToString(CultureInfo.InvariantCulture);
}
