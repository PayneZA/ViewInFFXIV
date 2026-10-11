using System.Diagnostics;
using System.Globalization;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Host;

/// <summary>
/// Hidden helper window: owns the ViewInFFXIV browsers (the watch party's and the stream's), captures what is on
/// the TV (a window you share, else the stream, else the party) on the GPU and answers the plugin's pipe.
/// </summary>
internal sealed class HostForm : Form
{
    private static readonly string ExePath = Environment.ProcessPath ?? "";
    private static readonly long ExeStamp = File.GetLastWriteTimeUtc(ExePath).Ticks;

    private readonly PipeServer pipe;
    private readonly ManagedBrowser managed = new(W2g.TheaterScript, waitForAdBlock: true);
    private readonly YouTubeControl youtube = new();
    // The stream browser (Twitch): its own profile. While it runs, the TV shows it and the party is muted here.
    private readonly ManagedBrowser stream = new(Twitch.TheaterScript, waitForAdBlock: false);
    private readonly Twitch twitch = new();
    // A window of yours on the TV; while it is shared it beats the stream and the party.
    private readonly TvWindow window;
    // Browser commands run one after another in the order they arrived (start before join, and so on).
    private Task managedChain = Task.CompletedTask;
    private Task streamChain = Task.CompletedTask;
    private string? startError;
    private readonly System.Windows.Forms.Timer guardTimer = new();
    private readonly System.Threading.Timer pageTimer;
    private readonly int parentPid;
    private Process? parent;
    private GpuCapture? capture;
    private long adapterLuid;
    private string? lastError;
    private bool closing;
    private string? profileDir;
    private volatile string managedState = "off";
    private volatile string managedActivity = "";
    private volatile string? managedError;
    private volatile string roomUrl = "";
    private volatile string streamState = "off";
    private volatile string streamActivity = "";
    private volatile string? streamError;
    // The stream page has the video stretched (and the crop measured) since it was last opened or popped back in.
    // Until then the TV shows nothing from it, so Twitch's own page never lands on the TV.
    private volatile bool streamClean;
    private IntPtr failedCaptureWindow;
    // Last spatial audio from the plugin, kept to give the party its sound back when the stream stops.
    private string? audioGain;
    private string? audioPan;

    public HostForm(string[] args)
    {
        // The plugin passes the game's process id; the helper (and its Brave) go away with the game.
        parentPid = args.Length >= 2 && args[0] == "--parent" && int.TryParse(args[1], out var pid) ? pid : 0;
        window = new TvWindow(parentPid);

        pipe = new PipeServer(this);
        pageTimer = new System.Threading.Timer(_ =>
        {
            _ = managed.RefreshCropAsync();
            _ = youtube.RefreshAsync(managed.IsRunning ? managed.Port : 0);
            _ = stream.RefreshCropAsync();
            _ = twitch.RefreshAsync(stream.IsRunning ? stream.OpenPage : null);
        }, null, 1000, 1000);

        Text = "ViewInFFXIV Host";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(1, 1);
        ShowInTaskbar = false;
        Location = new Point(-32000, -32000);

        guardTimer.Interval = 100;
        guardTimer.Tick += (_, _) => GuardTick();

        Load += (_, _) => Start();
        FormClosing += (_, e) =>
        {
            if (!closing && e.CloseReason == CloseReason.UserClosing)
                e.Cancel = true;
        };
    }

    private void Start()
    {
        try
        {
            pipe.Start();
            guardTimer.Start();
            WatchParent();
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
        }
    }

    private void WatchParent()
    {
        if (parentPid == 0)
            return;

        try
        {
            parent = Process.GetProcessById(parentPid);
            parent.Exited += (_, _) => BeginInvoke(Quit);
            parent.EnableRaisingEvents = true;
        }
        catch (ArgumentException)
        {
            // the game is already gone
            Quit();
        }
        catch (Exception)
        {
            // Not allowed to watch the game process (e.g. it runs elevated); the plugin still stops us on unload.
        }
    }

    private void Quit()
    {
        closing = true;
        Close();
    }

    public void ApplyCommand(HostCommand command)
    {
        if (ManagedOps.IsManaged(command.Op))
        {
            RunManaged(command);
            return;
        }

        if (command.Op == "quit")
            Quit();
    }

    public HostStatus BuildStatus()
    {
        var state = managedState;
        var error = managedError;
        if (state is "ready" or "busy" && !managed.IsRunning)
        {
            state = "error";
            error = $"{managed.Browser?.Name ?? "The browser"} was closed. Start or join the party again.";
        }

        var watching = stream.IsRunning;
        var twitchState = streamState;
        var twitchError = streamError;
        if (twitchState == "ready" && !watching)
        {
            twitchState = "error";
            twitchError = $"{stream.Browser?.Name ?? "The browser"} was closed. Start the channel again.";
        }

        var playback = managed.IsRunning ? youtube.Status : null;
        var sample = playback != null && PlaybackStatus.IsW2gSample(playback.VideoId, playback.Title, playback.Author);
        var gpu = capture;
        var capturing = gpu != null && gpu.Window != IntPtr.Zero;
        return new HostStatus
        {
            Alive = true,
            Exe = ExePath,
            ExeStamp = ExeStamp,
            Width = capturing ? IpcConstants.MaxWidth : 0,
            Height = capturing ? IpcConstants.MaxHeight : 0,
            Fps = capturing ? gpu!.Fps : 0,
            ArrivedFps = capturing ? gpu!.ArrivedFps : 0,
            SkippedFps = capturing ? gpu!.SkippedFps : 0,
            Generation = gpu?.Frames ?? 0,
            Capture = gpu == null ? "" : $"{gpu.AdapterName} (in {gpu.Arrived}, skipped {gpu.Skipped})",
            SharedTexture = capturing ? gpu!.SharedName : "",
            Error = lastError ?? gpu?.LastError,
            Managed = new ManagedBrowserStatus
            {
                State = state,
                Activity = managedActivity,
                Browser = managed.IsRunning ? managed.Browser?.Name ?? "" : "",
                Error = error,
                RoomUrl = roomUrl,
                Hwnd = managed.Hwnd.ToInt64(),
                Shown = managed.Shown,
                Playback = sample ? null : playback,
                SampleVideo = sample,
            },
            Window = window.Status,
            Stream = new StreamStatus
            {
                State = twitchState,
                Activity = streamActivity,
                Error = twitchError,
                Channel = twitch.Channel,
                Name = twitch.Name,
                Title = twitch.Title,
                Game = twitch.Game,
                Offline = watching && twitch.Offline,
                Playing = watching && twitch.Playing,
                Stuck = watching && twitch.Stuck && !stream.Shown,
                Missing = watching && twitch.Missing,
                VideoHeight = twitch.VideoHeight,
                Hwnd = stream.Hwnd.ToInt64(),
                Shown = stream.Shown,
            },
        };
    }

    private void RunManaged(HostCommand command)
    {
        // Window moves are instant and must not wait behind a slow page script.
        switch (command.Op)
        {
            case ManagedOps.Show when command.Arg == ManagedOps.StreamTarget:
                streamClean = false;
                stream.Show();
                SyncCapture();
                return;
            case ManagedOps.Hide when command.Arg == ManagedOps.StreamTarget:
                stream.Park();
                _ = Task.Run(CleanStreamAsync);
                return;
            case ManagedOps.Show:
                managed.Show();
                return;
            case ManagedOps.Hide:
                managed.Park();
                return;
            case ManagedOps.Audio:
                audioGain = command.Arg;
                audioPan = command.Arg2;
                ApplyAudio();
                return;
            case ManagedOps.Twitch or ManagedOps.StreamStop:
                // Twitch takes the TV from a shared window.
                if (command.Op == ManagedOps.Twitch)
                    StopWindow();
                RunStream(command);
                return;
            case ManagedOps.Window:
                ShareWindow(command);
                return;
            case ManagedOps.WindowStop:
                StopWindow();
                return;
            case ManagedOps.WindowPop:
                if (command.Arg == "1")
                    window.PopOff(capture?.ArrivedFps ?? 0);
                else
                    window.PutBack();
                return;
            case ManagedOps.Control:
                // Playback must feel instant, so it does not queue behind room/name scripts.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await youtube.CommandAsync(managed.Port, command.Arg ?? "").ConfigureAwait(false);
                        managedError = null;
                    }
                    catch (Exception ex)
                    {
                        managedError = ex.Message;
                    }
                });
                return;
        }

        // Called on the UI thread in arrival order, so chaining keeps that order.
        managedChain = managedChain.ContinueWith(async _ =>
        {
            try
            {
                managedError = null;
                await ExecuteManagedAsync(command).ConfigureAwait(false);
                if (managedState != "off")
                    managedState = managed.IsRunning ? "ready" : "error";
            }
            catch (Exception ex)
            {
                managedError = ex.Message;
                managedState = managed.IsRunning ? "ready" : "error";
            }
            finally
            {
                managedActivity = "";
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private async Task ExecuteManagedAsync(HostCommand command)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = cts.Token;
        switch (command.Op)
        {
            case ManagedOps.Start:
                profileDir = string.IsNullOrWhiteSpace(command.Arg) ? profileDir : command.Arg;
                if (long.TryParse(command.Arg2, out var luid))
                    adapterLuid = luid;
                var browser = Browsers.ById(command.Arg3) ?? Browsers.Brave;
                if (!managed.IsRunning || managed.Browser != browser)
                {
                    if (profileDir == null)
                        throw new InvalidOperationException("No browser profile folder was given.");

                    managedState = "starting";
                    managedActivity = $"Starting {browser.Name}";
                    youtube.Reset();
                    try
                    {
                        await managed.StartAsync(profileDir, browser, a => managedActivity = a, ct).ConfigureAwait(false);
                        startError = null;
                    }
                    catch (Exception ex)
                    {
                        // Shown instead of the "not running" of the commands queued behind this start.
                        startError = ex.Message;
                        throw;
                    }
                }

                Invoke(SyncCapture);
                return;

            case ManagedOps.Stop:
                youtube.Reset();
                managed.Stop();
                Invoke(SyncCapture);
                managedState = "off";
                roomUrl = "";
                return;

            case ManagedOps.Create:
                RequireRunning("Creating a w2g room");
                roomUrl = await W2g.CreateRoomAsync(managed.Page, ct).ConfigureAwait(false);
                return;

            case ManagedOps.Join:
                RequireRunning("Joining the w2g room");
                roomUrl = await W2g.JoinAsync(managed.Page, command.Arg ?? "", command.Arg2, ct).ConfigureAwait(false);
                return;

            case ManagedOps.Name:
                RequireRunning("Setting your w2g name");
                await W2g.SetNameAsync(managed.Page, command.Arg ?? "", ct).ConfigureAwait(false);
                return;

            case ManagedOps.Play:
                RequireRunning("Starting the video");
                await W2g.PlayAsync(managed.Page, command.Arg ?? "", ct).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// What is on the TV: a shared window, else the stream, else the party. Zero while the TV is blank (nothing on,
    /// or the stream popped out or not stretched yet, which would show Twitch's page). The crop is what to show of
    /// it; fit scales it (a window of any size) instead of copying it 1:1 (a browser sized for the TV).
    /// </summary>
    private IntPtr OnTv(out CropRect? crop, out bool fit)
    {
        fit = false;
        crop = null;
        if (window.Active)
        {
            fit = true;
            crop = window.Crop;
            return window.Hwnd;
        }

        if (stream.IsRunning)
        {
            crop = stream.Crop;
            return stream.Shown || !streamClean ? IntPtr.Zero : stream.Hwnd;
        }

        crop = managed.Crop;
        return managed.IsRunning ? managed.Hwnd : IntPtr.Zero;
    }

    /// <summary>Points the GPU capture at what is on the TV (starts, retargets or stops it). UI thread.</summary>
    private void SyncCapture()
    {
        var source = OnTv(out _, out _);
        if (source == IntPtr.Zero)
        {
            if (capture is { } idle && idle.Window != IntPtr.Zero)
                idle.Stop();
            return;
        }

        // A failed start is not retried on every tick.
        if (capture?.Window == source || failedCaptureWindow == source)
            return;

        try
        {
            failedCaptureWindow = source;
            capture ??= new GpuCapture(adapterLuid);
            capture.Start(source);
            var app = source == window.Hwnd ? "the shared window" : (stream.IsRunning ? stream.Browser : managed.Browser)?.Name ?? "the browser";
            lastError = capture.AdapterMatched
                ? null
                : $"Could not find the game's graphics card, so the screen may stay black. Check that FFXIV and {app} use the same GPU.";
            failedCaptureWindow = IntPtr.Zero;
        }
        catch (Exception ex)
        {
            lastError = $"GPU capture failed: {ex.Message}";
        }
    }

    /// <summary>Puts one of your windows on the TV (Twitch closes; the party carries on, muted).</summary>
    private void ShareWindow(HostCommand command)
    {
        if (long.TryParse(command.Arg4, out var luid))
            adapterLuid = luid;
        if (stream.IsRunning)
            RunStream(HostCommand.Managed(ManagedOps.StreamStop));

        try
        {
            window.Start(long.TryParse(command.Arg, out var hwnd) ? (IntPtr)hwnd : IntPtr.Zero, command.Arg2 == "1", capture?.ArrivedFps ?? 0);
        }
        catch (Exception ex)
        {
            window.Stop();
            window.Fail(ex.Message);
        }

        ApplyAudio();
        SyncCapture();
    }

    /// <summary>Takes the window off the TV and puts it back where it was; the TV goes back to the party.</summary>
    private void StopWindow()
    {
        if (window.Hwnd == IntPtr.Zero && window.Status.State == "off")
            return;
        window.Stop();
        ApplyAudio();
        SyncCapture();
    }

    /// <summary>The plugin's spatial audio goes to what is on the TV; the party stays silent under a stream or a window.</summary>
    private void ApplyAudio()
    {
        var gain = audioGain ?? "1";
        var pan = audioPan ?? "0";
        youtube.SetAudio(stream.IsRunning || window.Active ? "0" : gain, pan);
        _ = youtube.ApplyAudioAsync(managed.Port);
        if (double.TryParse(gain, NumberStyles.Float, CultureInfo.InvariantCulture, out var g) && double.IsFinite(g)
            && double.TryParse(pan, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && double.IsFinite(p))
            twitch.SetAudio(g, p);
    }

    private void RunStream(HostCommand command)
    {
        // Its own queue: a stream never waits behind party scripts, nor the other way round.
        streamChain = streamChain.ContinueWith(async _ =>
        {
            try
            {
                streamError = null;
                await ExecuteStreamAsync(command).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                streamError = ex.Message;
                streamState = stream.IsRunning ? "ready" : "error";
            }
            finally
            {
                streamActivity = "";
                ApplyAudio();
                BeginInvoke(SyncCapture);
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private async Task ExecuteStreamAsync(HostCommand command)
    {
        if (command.Op == ManagedOps.StreamStop)
        {
            twitch.Reset();
            stream.Stop();
            streamState = "off";
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;
        var channel = TwitchChannel.Parse(command.Arg) ?? throw new InvalidOperationException($"\"{command.Arg}\" is not a Twitch channel.");
        if (string.IsNullOrWhiteSpace(command.Arg2))
            throw new InvalidOperationException("No browser profile folder was given.");
        if (long.TryParse(command.Arg4, out var luid))
            adapterLuid = luid;

        var browser = Browsers.ById(command.Arg3) ?? Browsers.Brave;
        streamState = "starting";
        if (!stream.IsRunning || stream.Browser != browser)
        {
            streamActivity = $"Starting {browser.Name}";
            twitch.Reset();
            await stream.StartAsync(command.Arg2, browser, a => streamActivity = a, ct).ConfigureAwait(false);
        }

        streamActivity = $"Opening {channel} on Twitch";
        streamClean = false;
        await twitch.OpenAsync(stream.Page, channel, ct).ConfigureAwait(false);
        await CleanStreamAsync().ConfigureAwait(false);
        streamState = "ready";
    }

    /// <summary>
    /// Stretches the stream's video and measures the crop right away (instead of on the next timer round), then
    /// lets the TV show the stream again.
    /// </summary>
    private async Task CleanStreamAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && stream.IsRunning && !stream.Shown)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                if (stream.OpenPage is { } page
                    && await page.EvalAsync(Twitch.ReadyScript, cts.Token).ConfigureAwait(false) is { ValueKind: System.Text.Json.JsonValueKind.True })
                {
                    await stream.RefreshCropAsync().ConfigureAwait(false);
                    break;
                }
            }
            catch
            {
                // navigating; try again
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        // On a timeout the timer's theater pass takes over within a second.
        if (!stream.Shown)
            streamClean = true;
    }

    private void RequireRunning(string activity)
    {
        if (!managed.IsRunning)
            throw new InvalidOperationException(startError ?? $"{managed.Browser?.Name ?? "The browser"} is not running. Start or join the party again.");
        managedState = "busy";
        managedActivity = activity;
    }

    private void GuardTick()
    {
        // Keep the browsers off-screen and un-minimised so they never stop rendering.
        managed.Guard();
        stream.Guard();
        var shared = window.Active;
        window.Guard(capture);
        if (shared && !window.Active)
            ApplyAudio();

        // Follow the TV: a window that closes, or a stream that ends or whose browser is closed, hands it back.
        SyncCapture();
        if (capture is { } gpu)
        {
            OnTv(out var crop, out var fit);
            gpu.Crop = crop;
            gpu.Fit = fit;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            guardTimer.Stop();
            guardTimer.Dispose();
            pageTimer.Dispose();
            // A popped-off window goes back on your monitor before anything else closes.
            window.Stop();
            parent?.Dispose();
            youtube.Dispose();
            capture?.Dispose();
            twitch.Dispose();
            stream.Dispose();
            managed.Dispose();
            pipe.Dispose();
        }

        base.Dispose(disposing);
    }
}
