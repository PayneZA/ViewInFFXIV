using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Host;

/// <summary>Normalised (0..1) window rectangle to capture instead of the whole window.</summary>
internal sealed record CropRect(float Left, float Top, float Right, float Bottom);

/// <summary>
/// The watch-party browser: a dedicated Brave/Chrome/Edge instance on its own ViewInFFXIV profile, launched with
/// anti-throttling flags and parked off-screen so it keeps rendering while nobody can see it.
/// Minimising would stop rendering, so a minimised window is restored straight back off-screen.
/// </summary>
internal sealed class ManagedBrowser : IDisposable
{
    public const int WindowWidth = 1920;
    public const int WindowHeight = 1080;

    // Without these Chromium stops painting windows it considers hidden (verified: off-screen freezes without them).
    // Do not add --disable-frame-rate-limit / --disable-gpu-vsync: they cut Brave's painting to ~14-50 fps.
    private static readonly string[] RenderFlags =
    [
        "--disable-features=CalculateNativeWinOcclusion",
        "--disable-backgrounding-occluded-windows",
        "--disable-renderer-backgrounding",
        "--disable-background-timer-throttling",
    ];

    // Largest visible player-sized iframe/video; runs in the page and again inside the player's own frame.
    private const string LargestMediaScript = """
        (() => {
          let best = null, area = 0, src = '';
          for (const e of document.querySelectorAll('iframe,video')) {
            const r = e.getBoundingClientRect();
            if (r.width < 160 || r.height < 90 || getComputedStyle(e).visibility === 'hidden') continue;
            if (r.width * r.height > area) { area = r.width * r.height; best = r; src = e.tagName === 'IFRAME' ? e.src : ''; }
          }
          const x = best ? Math.max(0, best.left) : 0, y = best ? Math.max(0, best.top) : 0;
          const r = best ? Math.min(innerWidth, best.right) : innerWidth, b = best ? Math.min(innerHeight, best.bottom) : innerHeight;
          return { iw: innerWidth, ih: innerHeight, dpr: devicePixelRatio, x, y, w: r - x, h: b - y, found: !!best, src };
        })()
        """;

    private Process? process;
    private CdpPage? page;
    private int port;
    private int cropBusy;

    // Hidden-window size, grown so the video area is exactly the capture size (1920x1080).
    private int parkedWidth = WindowWidth;
    private int parkedHeight = WindowHeight;
    private int guardTicks;
    private bool taskbarHidden;

    public IntPtr Hwnd { get; private set; }

    /// <summary>The browser running (or last started).</summary>
    public BrowserInfo? Browser { get; private set; }

    private string BrowserName => Browser?.Name ?? "the browser";

    /// <summary>DevTools port of the running instance, 0 when not running.</summary>
    public int Port => port;

    public bool Shown { get; private set; }

    public CropRect? Crop { get; private set; }

    public bool IsRunning
    {
        get
        {
            // Read from the UI, pipe and timer threads while Stop() may be disposing the process.
            try
            {
                return Hwnd != IntPtr.Zero && process is { HasExited: false } && IsWindow(Hwnd);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                return false;
            }
        }
    }

    public CdpPage Page => page is { IsOpen: true }
        ? page
        : throw new InvalidOperationException($"Lost connection to {BrowserName}. Start or join the party again.");

    public async Task StartAsync(string profileDir, BrowserInfo browser, Action<string> progress, CancellationToken ct)
    {
        if (IsRunning && page is { IsOpen: true } && Browser == browser)
            return;

        Stop();
        Browser = browser;
        var exe = Browsers.Find(browser)
            ?? throw new InvalidOperationException($"{browser.Name} is not installed. Pick another browser under Settings.");

        var firstRun = !Directory.Exists(Path.Combine(profileDir, "Default"));
        Directory.CreateDirectory(profileDir);
        if (browser == Browsers.Brave)
            SeedLocalState(profileDir);
        var startPage = WriteStartPage(profileDir);

        // A Brave left running on this profile (e.g. after a crash) would swallow our launch and ignore the flags.
        var portFile = Path.Combine(profileDir, "DevToolsActivePort");
        if (TryReadPort(portFile, out var stalePort) && await CdpPage.TryCloseBrowserAsync(stalePort, ct).ConfigureAwait(false))
        {
            var closeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
            while (DateTime.UtcNow < closeDeadline && await CdpPage.IsAliveAsync(stalePort, ct).ConfigureAwait(false))
                await Task.Delay(250, ct).ConfigureAwait(false);
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        TryDelete(portFile);

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add($"--user-data-dir={profileDir}");
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");
        psi.ArgumentList.Add("--hide-crash-restore-bubble");
        psi.ArgumentList.Add("--remote-debugging-port=0");
        psi.ArgumentList.Add("--force-device-scale-factor=1");
        psi.ArgumentList.Add("--autoplay-policy=no-user-gesture-required");
        psi.ArgumentList.Add($"--window-size={WindowWidth},{WindowHeight}");
        psi.ArgumentList.Add($"--window-position={OffscreenX(WindowWidth)},0");
        foreach (var flag in RenderFlags)
            psi.ArgumentList.Add(flag);
        foreach (var flag in browser.ExtraFlags)
            psi.ArgumentList.Add(flag);
        // App mode (no tabs or address bar) needs a real page; about:blank silently opens a normal window.
        psi.ArgumentList.Add($"--app={new Uri(startPage).AbsoluteUri}");
        process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {browser.Name}.");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (Hwnd == IntPtr.Zero || port == 0)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"{browser.Name} closed during startup.");
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{browser.Name} did not open a window in time.");

            if (Hwnd == IntPtr.Zero)
            {
                Hwnd = FindWindow(process.Id);
                if (Hwnd != IntPtr.Zero)
                    Park();
            }

            if (port == 0 && TryReadPort(portFile, out var livePort))
                port = livePort;

            await Task.Delay(30, ct).ConfigureAwait(false);
        }

        // Edge reports its DevTools port a moment before its first page exists.
        var pageDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            try
            {
                page = await CdpPage.ConnectAsync(port, ct).ConfigureAwait(false);
                break;
            }
            catch (InvalidOperationException) when (DateTime.UtcNow < pageDeadline && !process.HasExited)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }

        // A brand-new Brave profile downloads its ad-block lists after launch; pages opened before they
        // arrive show ads. Give the lists a moment on first run.
        if (firstRun && browser == Browsers.Brave)
        {
            progress("Downloading Brave's ad-block lists (first run only)");
            var listDir = Path.Combine(profileDir, AdBlockComponentId);
            var listDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < listDeadline && !(Directory.Exists(listDir) && Directory.EnumerateDirectories(listDir).Any()))
                await Task.Delay(500, ct).ConfigureAwait(false);
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Moves the window off every monitor without activating it. Restores it first if minimised.</summary>
    public void Park()
    {
        if (Hwnd == IntPtr.Zero)
            return;

        // Un-minimise / un-maximise first. SW_SHOWNOACTIVATE leaves a maximised window maximised, hence SW_RESTORE.
        if (IsIconic(Hwnd) || IsZoomed(Hwnd))
        {
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            GetWindowPlacement(Hwnd, ref placement);
            placement.ShowCmd = IsZoomed(Hwnd) ? SwRestore : SwShowNoActivate;
            placement.Flags = 0;
            SetWindowPlacement(Hwnd, ref placement);
        }

        // SetWindowPlacement clamps fully off-screen rectangles back onto a monitor; SetWindowPos does not.
        SetWindowPos(Hwnd, IntPtr.Zero, OffscreenX(parkedWidth), 0, parkedWidth, parkedHeight, SwpNoZOrder | SwpNoActivate);
        Shown = false;
    }

    /// <summary>Brings the window onto the primary monitor so the user can click around in w2g.</summary>
    public void Show()
    {
        if (Hwnd == IntPtr.Zero)
            return;

        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, WindowWidth, WindowHeight);
        var w = Math.Min(WindowWidth, work.Width);
        var h = Math.Min(WindowHeight, work.Height);
        var x = (work.Width - w) / 2;
        var y = (work.Height - h) / 2;
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        GetWindowPlacement(Hwnd, ref placement);
        placement.NormalPosition = new Rect { Left = x, Top = y, Right = x + w, Bottom = y + h };
        placement.ShowCmd = SwShowNormal;
        placement.Flags = 0;
        SetWindowPlacement(Hwnd, ref placement);
        Shown = true;
    }

    /// <summary>Called on every capture tick. Minimising means "hide": put it back off-screen so it keeps rendering.</summary>
    public void Guard()
    {
        if (Hwnd == IntPtr.Zero)
            return;
        if (!IsWindow(Hwnd))
        {
            Hwnd = IntPtr.Zero;
            Shown = false;
            return;
        }

        if (IsIconic(Hwnd))
        {
            Park();
            return;
        }

        if (!Shown && GetWindowRect(Hwnd, out var r) && SystemInformation.VirtualScreen.IntersectsWith(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)))
            Park();

        // Keep the hidden window off the taskbar so it cannot be found and dragged back by accident.
        // Brave may re-add the button, so re-check every couple of seconds.
        if (Shown == taskbarHidden || ++guardTicks % 60 == 0)
            SyncTaskbarButton();
    }

    private void SyncTaskbarButton()
    {
        try
        {
            taskbar ??= CreateTaskbarList();
            if (Shown)
                taskbar.AddTab(Hwnd);
            else
                taskbar.DeleteTab(Hwnd);
            taskbarHidden = !Shown;
        }
        catch
        {
            // shell not available (e.g. explorer restarting); try again later
            taskbar = null;
        }
    }

    private static ITaskbarList CreateTaskbarList()
    {
        var list = (ITaskbarList)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"))!)!;
        list.HrInit();
        return list;
    }

    /// <summary>Measures where the video player sits in the window so only the video goes on the wall.</summary>
    public async Task RefreshCropAsync()
    {
        if (!IsRunning || page is not { IsOpen: true } || Interlocked.Exchange(ref cropBusy, 1) != 0)
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var shown = Shown;
            var theater = await page.EvalAsync(W2g.TheaterScript(!shown), cts.Token).ConfigureAwait(false) is { ValueKind: JsonValueKind.True };
            var value = await page.EvalAsync(LargestMediaScript, cts.Token).ConfigureAwait(false);
            // Windows.Graphics.Capture frames cover the visible frame (DWM bounds), which can be smaller than
            // GetWindowRect by invisible resize borders, so measure against those.
            if (value is not { ValueKind: JsonValueKind.Object } v || !GetWindowRect(Hwnd, out var rect) || !GetFrameBounds(Hwnd, out var frame))
                return;

            var x = v.GetProperty("x").GetDouble();
            var y = v.GetProperty("y").GetDouble();
            var w = v.GetProperty("w").GetDouble();
            var h = v.GetProperty("h").GetDouble();

            // Sites like w2g wrap the real player in their own frame together with their control bar:
            // look inside that frame and narrow the crop to the video itself.
            if (v.GetProperty("src").GetString() is { Length: > 0 } src && Uri.TryCreate(src, UriKind.Absolute, out var srcUri))
            {
                var inner = await page.EvalInFrameAsync(srcUri.GetLeftPart(UriPartial.Path), LargestMediaScript, cts.Token).ConfigureAwait(false);
                if (inner is { ValueKind: JsonValueKind.Object } i && i.GetProperty("found").GetBoolean())
                {
                    var ix = i.GetProperty("x").GetDouble();
                    var iy = i.GetProperty("y").GetDouble();
                    x += ix;
                    y += iy;
                    w = Math.Min(i.GetProperty("w").GetDouble(), w - ix);
                    h = Math.Min(i.GetProperty("h").GetDouble(), h - iy);
                }
            }

            float W = frame.Right - frame.Left, H = frame.Bottom - frame.Top;
            var dpr = (float)v.GetProperty("dpr").GetDouble();
            var viewW = (float)v.GetProperty("iw").GetDouble() * dpr;
            var viewH = (float)v.GetProperty("ih").GetDouble() * dpr;
            if (W < 2 || H < 2 || viewW < 2 || viewH < 2)
                return;

            // The page viewport sits at the bottom of the window, between equal side/bottom borders.
            var border = MathF.Max(0, (W - viewW) / 2f);
            var top = MathF.Max(0, H - viewH - border);
            var left = border + ((float)x * dpr);
            var upper = top + ((float)y * dpr);
            var width = (float)w * dpr;
            var height = (float)h * dpr;

            // With the player stretched, grow/shrink the hidden window until the video area is exactly
            // the capture size: YouTube then picks 1080p and the capture copies it 1:1 without scaling.
            if (theater && !shown && (MathF.Abs(width - IpcConstants.MaxWidth) > 2 || MathF.Abs(height - IpcConstants.MaxHeight) > 2))
            {
                parkedWidth = Math.Clamp((int)MathF.Round((rect.Right - rect.Left) + (IpcConstants.MaxWidth - width)), 640, 4096);
                parkedHeight = Math.Clamp((int)MathF.Round((rect.Bottom - rect.Top) + (IpcConstants.MaxHeight - height)), 360, 4096);
                SetWindowPos(Hwnd, IntPtr.Zero, OffscreenX(parkedWidth), 0, parkedWidth, parkedHeight, SwpNoZOrder | SwpNoActivate);
                return; // measure again next round, after the page has re-laid out
            }

            Crop = new CropRect(
                Math.Clamp(left / W, 0f, 1f),
                Math.Clamp(upper / H, 0f, 1f),
                Math.Clamp((left + width) / W, 0f, 1f),
                Math.Clamp((upper + height) / H, 0f, 1f));
        }
        catch
        {
            // page is navigating or busy; try again next round
        }
        finally
        {
            Volatile.Write(ref cropBusy, 0);
        }
    }

    public void Stop()
    {
        var p = process;
        process = null;
        Hwnd = IntPtr.Zero;
        Shown = false;
        Crop = null;
        page?.Dispose();
        page = null;

        if (p != null)
        {
            try
            {
                // Close politely so the profile is not marked as crashed, then make sure.
                if (!p.HasExited && port != 0)
                    Task.Run(() => CdpPage.TryCloseBrowserAsync(port, CancellationToken.None)).Wait(1500);
                if (!p.WaitForExit(2000))
                    p.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }

            p.Dispose();
        }

        port = 0;
    }

    public void Dispose() => Stop();

    private ITaskbarList? taskbar;

    private static string WriteStartPage(string profileDir)
    {
        var path = Path.Combine(profileDir, "viewinffxiv-start.html");
        File.WriteAllText(path, "<!doctype html><title>ViewInFFXIV</title><body style=\"background:#000\"></body>", new UTF8Encoding(false));
        return path;
    }

    private static int OffscreenX(int width) => SystemInformation.VirtualScreen.Left - width - 400;

    /// <summary>Pre-acknowledge Brave's first-run analytics notice so it never sits on top of the page.</summary>
    private static void SeedLocalState(string profileDir)
    {
        var path = Path.Combine(profileDir, "Local State");
        if (File.Exists(path))
            return;
        File.WriteAllText(path, """{"brave":{"p3a":{"enabled":false,"notice_acknowledged":true}}}""", new UTF8Encoding(false));
    }

    private static bool TryReadPort(string portFile, out int port)
    {
        port = 0;
        try
        {
            if (!File.Exists(portFile))
                return false;
            using var stream = new FileStream(portFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return int.TryParse(reader.ReadLine(), out port) && port > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignored */ }
    }

    private static IntPtr FindWindow(int pid)
    {
        var found = IntPtr.Zero;
        var className = new StringBuilder(64);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid || !IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero)
                return true;
            className.Clear();
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() != "Chrome_WidgetWin_1")
                return true;
            if (!GetWindowRect(hwnd, out var r) || r.Right - r.Left < 400 || r.Bottom - r.Top < 300)
                return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private const int SwShowNormal = 1;
    private const int SwShowNoActivate = 4;
    private const int SwRestore = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    // Brave's default ad-block filter list component folder inside the profile.
    private const string AdBlockComponentId = "iodkpdagapdfkphljnddpjlldadblomo";

    [ComImport]
    [Guid("56FDF342-FD6D-11d0-958A-006097C9A090")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        void HrInit();

        void AddTab(IntPtr hwnd);

        void DeleteTab(IntPtr hwnd);

        void ActivateTab(IntPtr hwnd);

        void SetActiveAlt(IntPtr hwnd);
    }
    private const int GwOwner = 4;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    private static bool GetFrameBounds(IntPtr hwnd, out Rect bounds) =>
        DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out bounds, Marshal.SizeOf<Rect>()) == 0 || GetWindowRect(hwnd, out bounds);

    private const int DwmwaExtendedFrameBounds = 9;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);
}
