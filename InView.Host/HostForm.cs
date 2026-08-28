using System.Text.Json;
using InView.Shared;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace InView.Host;

internal sealed class HostForm : Form
{
    private readonly WebView2 webView = new();
    private readonly FrameBuffer frames;
    private readonly CaptureEngine capture;
    private readonly PipeServer pipe;
    private readonly System.Windows.Forms.Timer captureTimer = new();
    private readonly string profileDir;
    private bool windowVisible;
    private bool hideChrome = true;
    private float volume = 1f;
    private string currentUrl = IpcConstants.DefaultUrl;
    private bool loaded;
    private string? lastError;
    private bool closing;
    private string captureSource = "webview";
    private IntPtr captureHwnd;
    private string capturedTitle = "";

    public HostForm(string[] args)
    {
        profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InView",
            "WebView2");
        Directory.CreateDirectory(profileDir);

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--url" or "-u")
                currentUrl = args[i + 1];
        }

        frames = FrameBuffer.Create();
        capture = new CaptureEngine(frames);
        pipe = new PipeServer(this);

        Text = "InView — WatchTogether";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(IpcConstants.CaptureWidth, IpcConstants.CaptureHeight);
        MinimumSize = new Size(640, 360);
        BackColor = Color.Black;
        ShowInTaskbar = false;

        webView.Dock = DockStyle.Fill;
        webView.DefaultBackgroundColor = Color.Black;
        Controls.Add(webView);

        captureTimer.Interval = IpcConstants.CaptureIntervalMs;
        captureTimer.Tick += (_, _) => CaptureTick();

        Load += async (_, _) => await StartAsync();
        FormClosing += (_, e) =>
        {
            if (closing)
                return;
            e.Cancel = true;
            SetWindowVisible(false);
        };
    }

    private async Task StartAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, profileDir);
            await webView.EnsureCoreWebView2Async(env);
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.PermissionRequested += OnPermissionRequested;
            webView.CoreWebView2.NavigationCompleted += async (_, args) =>
            {
                loaded = args.IsSuccess;
                lastError = args.IsSuccess ? null : $"Navigation failed ({args.WebErrorStatus})";
                currentUrl = webView.Source?.ToString() ?? currentUrl;
                await InjectAsync();
            };
            webView.CoreWebView2.DOMContentLoaded += async (_, _) => await InjectAsync();
            webView.CoreWebView2.Navigate(currentUrl);
            pipe.Start();
            captureTimer.Start();
            SetWindowVisible(false);
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
        }
    }

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.Handled = true;
        e.State = CoreWebView2PermissionState.Allow;
    }

    public void ApplyCommand(HostCommand command)
    {
        switch (command.Op)
        {
            case "navigate" when !string.IsNullOrWhiteSpace(command.Url):
                currentUrl = command.Url.Trim();
                loaded = false;
                if (webView.CoreWebView2 != null)
                    webView.CoreWebView2.Navigate(currentUrl);
                break;
            case "visible":
                SetWindowVisible(command.Visible ?? true);
                break;
            case "volume":
                volume = Math.Clamp(command.Volume ?? 1f, 0f, 1f);
                _ = InjectAsync();
                break;
            case "chrome":
                hideChrome = command.HideChrome ?? true;
                _ = InjectAsync();
                break;
            case "source":
                captureSource = command.Source == "window" ? "window" : "webview";
                captureHwnd = command.Hwnd is > 0 ? new IntPtr(command.Hwnd.Value) : IntPtr.Zero;
                if (captureSource != "window")
                    captureHwnd = IntPtr.Zero;
                capturedTitle = captureSource == "window" ? BrowserWindows.GetTitle(captureHwnd) : "";
                lastError = null;
                _ = InjectAsync();
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
            Loaded = loaded,
            Url = currentUrl,
            Width = header.Width,
            Height = header.Height,
            Fps = header.Fps,
            Generation = header.Generation,
            Capture = capture.Mode,
            Source = captureSource,
            WindowVisible = windowVisible,
            Error = lastError ?? capture.LastError,
            SharedHandle = capture.SharedHandle,
            CapturedTitle = capturedTitle,
            Windows = BrowserWindows.Enumerate(),
        };
    }

    private async Task InjectAsync()
    {
        if (webView.CoreWebView2 == null)
            return;
        try
        {
            var injectVolume = captureSource == "window" ? 0f : volume;
            await webView.CoreWebView2.ExecuteScriptAsync($"window.__inviewVolume = {injectVolume.ToString(System.Globalization.CultureInfo.InvariantCulture)};");
            await webView.CoreWebView2.ExecuteScriptAsync($"window.__inviewHideChrome = {(hideChrome ? "true" : "false")};");
            await webView.CoreWebView2.ExecuteScriptAsync(ChromeScript.HideAndVolume);
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
        }
    }

    private void CaptureTick()
    {
        if (!IsHandleCreated || capture.IsBusy)
            return;

        IntPtr hwnd;
        int width;
        int height;
        if (captureSource == "window")
        {
            if (!BrowserWindows.TryGetBounds(captureHwnd, out width, out height))
            {
                if (captureHwnd != IntPtr.Zero)
                {
                    lastError = "Browser window closed";
                    capturedTitle = "";
                    captureHwnd = IntPtr.Zero;
                }

                return;
            }

            hwnd = captureHwnd;
            capturedTitle = BrowserWindows.GetTitle(hwnd);
        }
        else
        {
            hwnd = Handle;
            width = Math.Max(2, ClientSize.Width);
            height = Math.Max(2, ClientSize.Height);
        }

        capture.TryTick(hwnd, width, height);
    }

    public void SetWindowVisible(bool visible)
    {
        windowVisible = visible;
        ShowInTaskbar = visible;
        if (visible)
        {
            Location = new Point(80, 80);
            WindowState = FormWindowState.Normal;
            Show();
            Activate();
        }
        else
        {
            Show();
            Location = new Point(-32000, -32000);
        }
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
            webView.Dispose();
        }

        base.Dispose(disposing);
    }
}
