using System.Diagnostics;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Host;

/// <summary>
/// Hidden helper window: owns the ViewInFFXIV Brave, captures it on the GPU and answers the plugin's pipe.
/// </summary>
internal sealed class HostForm : Form
{
    private static readonly string ExePath = Environment.ProcessPath ?? "";
    private static readonly long ExeStamp = File.GetLastWriteTimeUtc(ExePath).Ticks;

    private readonly PipeServer pipe;
    private readonly ManagedBrowser managed = new();
    private readonly YouTubeControl youtube = new();
    // Browser commands run one after another in the order they arrived (start before join, and so on).
    private Task managedChain = Task.CompletedTask;
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

    public HostForm(string[] args)
    {
        // The plugin passes the game's process id; the helper (and its Brave) go away with the game.
        parentPid = args.Length >= 2 && args[0] == "--parent" && int.TryParse(args[1], out var pid) ? pid : 0;

        pipe = new PipeServer(this);
        pageTimer = new System.Threading.Timer(_ =>
        {
            _ = managed.RefreshCropAsync();
            _ = youtube.RefreshAsync(managed.IsRunning ? managed.Port : 0);
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
                Playback = managed.IsRunning ? youtube.Status : null,
            },
        };
    }

    private void RunManaged(HostCommand command)
    {
        // Window moves are instant and must not wait behind a slow page script.
        switch (command.Op)
        {
            case ManagedOps.Show:
                managed.Show();
                return;
            case ManagedOps.Hide:
                managed.Park();
                return;
            case ManagedOps.Audio:
                youtube.SetAudio(command.Arg, command.Arg2);
                _ = youtube.ApplyAudioAsync(managed.Port);
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

                Invoke(StartCapture);
                return;

            case ManagedOps.Stop:
                youtube.Reset();
                Invoke(() => capture?.Stop());
                managed.Stop();
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

    /// <summary>Starts (or retargets) the GPU capture on the managed Brave window. UI thread.</summary>
    private void StartCapture()
    {
        if (!managed.IsRunning)
            return;

        try
        {
            capture ??= new GpuCapture(adapterLuid);
            if (capture.Window != managed.Hwnd)
                capture.Start(managed.Hwnd);
            lastError = capture.AdapterMatched
                ? null
                : $"Could not find the game's graphics card, so the screen may stay black. Check that FFXIV and {managed.Browser?.Name ?? "the browser"} use the same GPU.";
        }
        catch (Exception ex)
        {
            lastError = $"GPU capture failed: {ex.Message}";
        }
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
        // Keep Brave off-screen and un-minimised so it never stops rendering.
        managed.Guard();

        if (capture is { } gpu)
        {
            gpu.Crop = managed.Crop;
            if (gpu.Window != IntPtr.Zero && !managed.IsRunning)
                gpu.Stop();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            guardTimer.Stop();
            guardTimer.Dispose();
            pageTimer.Dispose();
            parent?.Dispose();
            youtube.Dispose();
            capture?.Dispose();
            managed.Dispose();
            pipe.Dispose();
        }

        base.Dispose(disposing);
    }
}
