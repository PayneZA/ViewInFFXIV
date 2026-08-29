using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Host;

internal sealed class HostForm : Form
{
    private readonly FrameBuffer frames;
    private readonly CaptureEngine capture;
    private readonly PipeServer pipe;
    private readonly System.Windows.Forms.Timer captureTimer = new();
    private string? lastError;
    private bool closing;
    private IntPtr captureHwnd;
    private string capturedTitle = "";

    public HostForm(string[] args)
    {
        _ = args;

        frames = FrameBuffer.Create();
        capture = new CaptureEngine(frames);
        pipe = new PipeServer(this);

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
            if (!closing)
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
        };
    }

    private void CaptureTick()
    {
        if (!IsHandleCreated || capture.IsBusy || captureHwnd == IntPtr.Zero)
            return;

        if (!BrowserWindows.TryGetBounds(captureHwnd, out var width, out var height))
        {
            lastError = "Capture window closed";
            capturedTitle = "";
            captureHwnd = IntPtr.Zero;
            return;
        }

        capturedTitle = BrowserWindows.GetTitle(captureHwnd);
        capture.TryTick(captureHwnd, width, height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            captureTimer.Stop();
            captureTimer.Dispose();
            pipe.Dispose();
            capture.Dispose();
            frames.Dispose();
        }

        base.Dispose(disposing);
    }
}
