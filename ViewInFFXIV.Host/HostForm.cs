using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Host;

internal sealed class HostForm : Form
{
    private readonly FrameBuffer frames;
    private readonly CaptureEngine capture;
    private readonly PipeServer pipe;
    private readonly System.Windows.Forms.Timer captureTimer = new();
    private readonly ManagedBrowser managed = new();
    private readonly YouTubeControl youtube = new();
    private readonly SemaphoreSlim managedLock = new(1, 1);
    private readonly System.Threading.Timer cropTimer;
    private string? lastError;
    private bool closing;
    private IntPtr captureHwnd;
    private string capturedTitle = "";
    private string? profileDir;
    private volatile string managedState = "off";
    private volatile string managedActivity = "";
    private volatile string? managedError;
    private volatile string roomUrl = "";

    public HostForm(string[] args)
    {
        _ = args;

        frames = FrameBuffer.Create();
        capture = new CaptureEngine(frames);
        pipe = new PipeServer(this);
        cropTimer = new System.Threading.Timer(_ =>
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

        captureTimer.Interval = IpcConstants.CaptureIntervalMs;
        captureTimer.Tick += (_, _) => CaptureTick();

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
            captureTimer.Start();
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
        }
    }

    public void ApplyCommand(HostCommand command)
    {
        if (ManagedOps.IsManaged(command.Op))
        {
            RunManaged(command);
            return;
        }

        switch (command.Op)
        {
            case "source":
                captureHwnd = command.Hwnd is > 0 ? new IntPtr(command.Hwnd.Value) : IntPtr.Zero;
                capturedTitle = captureHwnd != IntPtr.Zero ? BrowserWindows.GetTitle(captureHwnd) : "";
                lastError = null;
                break;
            case "listWindows":
                break;
            case "quit":
                closing = true;
                Close();
                break;
        }
    }

    public HostStatus BuildStatus()
    {
        var header = frames.ReadHeader();
        var state = managedState;
        var error = managedError;
        if (state is "ready" or "busy" && !managed.IsRunning)
        {
            state = "error";
            error = "Brave was closed. Use /viewinff start.";
        }

        return new HostStatus
        {
            Alive = true,
            Width = header.Width,
            Height = header.Height,
            Fps = header.Fps,
            Generation = header.Generation,
            Capture = capture.Mode,
            Source = captureHwnd != IntPtr.Zero ? "window" : "none",
            Error = lastError ?? capture.LastError,
            SharedHandle = capture.SharedHandle,
            CapturedTitle = capturedTitle,
            Windows = BrowserWindows.Enumerate(),
            Managed = new ManagedBrowserStatus
            {
                State = state,
                Activity = managedActivity,
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

        _ = Task.Run(async () =>
        {
            await managedLock.WaitAsync().ConfigureAwait(false);
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
                managedLock.Release();
            }
        });
    }

    private async Task ExecuteManagedAsync(HostCommand command)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = cts.Token;
        switch (command.Op)
        {
            case ManagedOps.Start:
                profileDir = string.IsNullOrWhiteSpace(command.Arg) ? profileDir : command.Arg;
                if (managed.IsRunning)
                    return;
                if (profileDir == null)
                    throw new InvalidOperationException("No browser profile folder was given.");

                managedState = "starting";
                managedActivity = "Starting Brave";
                await managed.StartAsync(profileDir, a => managedActivity = a, ct).ConfigureAwait(false);
                var hwnd = managed.Hwnd;
                BeginInvoke(() =>
                {
                    captureHwnd = hwnd;
                    capturedTitle = "Brave (High Performance)";
                    lastError = null;
                });
                return;

            case ManagedOps.Stop:
                var old = managed.Hwnd;
                youtube.Reset();
                managed.Stop();
                managedState = "off";
                roomUrl = "";
                BeginInvoke(() =>
                {
                    if (captureHwnd == old)
                    {
                        captureHwnd = IntPtr.Zero;
                        capturedTitle = "";
                    }
                });
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

    private void RequireRunning(string activity)
    {
        if (!managed.IsRunning)
            throw new InvalidOperationException("Brave is not running. Use /viewinff start.");
        managedState = "busy";
        managedActivity = activity;
    }

    private void CaptureTick()
    {
        // Keep the High Performance window off-screen and un-minimised so it never stops rendering.
        managed.Guard();

        if (!IsHandleCreated || capture.IsBusy || captureHwnd == IntPtr.Zero)
            return;

        if (!BrowserWindows.IsAlive(captureHwnd))
        {
            lastError = "Capture window closed";
            capturedTitle = "";
            captureHwnd = IntPtr.Zero;
            return;
        }

        // Minimised windows have nothing to capture; keep the binding and resume when restored.
        if (!BrowserWindows.TryGetBounds(captureHwnd, out var width, out var height))
            return;

        var managedCapture = captureHwnd == managed.Hwnd;
        if (!managedCapture)
            capturedTitle = BrowserWindows.GetTitle(captureHwnd);
        capture.TryTick(captureHwnd, width, height, managedCapture ? managed.Crop : null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            captureTimer.Stop();
            captureTimer.Dispose();
            cropTimer.Dispose();
            youtube.Dispose();
            managed.Dispose();
            pipe.Dispose();
            capture.Dispose();
            frames.Dispose();
        }

        base.Dispose(disposing);
    }
}
