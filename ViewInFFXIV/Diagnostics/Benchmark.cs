using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;

namespace ViewInFFXIV.Diagnostics;

/// <summary>
/// /viewin bench: runs the game for a few seconds in each mode (everything on, no video copy, no screen draw,
/// neither) and prints the game's frame times plus the CPU and GPU cost of our own per-frame work.
/// Driven from the Draw callback; costs nothing while not running.
/// </summary>
public sealed class Benchmark : IDisposable
{
    private const double WarmupSeconds = 1;
    private const double ModeSeconds = 5;

    private static readonly Mode[] Modes =
    [
        new("normal", SkipVideo: false, SkipRender: false),
        new("no video copy", SkipVideo: true, SkipRender: false),
        new("no screen draw", SkipVideo: false, SkipRender: true),
        new("neither", SkipVideo: true, SkipRender: true),
    ];

    private readonly Plugin plugin;
    private readonly Stopwatch clock = new();
    private readonly Stopwatch section = new();
    private GpuTimer? gpu;
    private int modeIndex = -1;
    private double modeStart;
    private double lastFrame;
    private Sample sample = new();

    public Benchmark(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public bool Running => modeIndex >= 0;

    public bool SkipVideo => Running && Modes[modeIndex].SkipVideo;

    public bool SkipRender => Running && Modes[modeIndex].SkipRender;

    public void Start(Device device)
    {
        if (Running)
            return;

        gpu ??= CreateTimer(device);
        modeIndex = 0;
        clock.Restart();
        BeginMode();
        Plugin.ChatGui.Print(
            $"Benchmark: {Modes.Length} modes × {ModeSeconds:0} s. Keep the game focused and the screen in view.",
            "ViewInFFXIV");
    }

    /// <summary>Start of our per-frame work (top of Draw).</summary>
    public void BeginFrame()
    {
        if (!Running)
            return;

        var now = clock.Elapsed.TotalSeconds;
        if (now - modeStart >= WarmupSeconds)
        {
            sample.FrameTimes.Add((now - lastFrame) * 1000);
            if (!GameFocused())
                sample.Unfocused++;
            var host = plugin.Host.Status;
            sample.Arrived += host.ArrivedFps;
            sample.Skipped += host.SkippedFps;
            sample.HostSamples++;
            if (sample.WallFramesStart < 0)
                sample.WallFramesStart = (long)plugin.Video.Frames;
        }

        lastFrame = now;
        if (now - modeStart >= WarmupSeconds + ModeSeconds)
        {
            Report();
            if (++modeIndex >= Modes.Length)
            {
                modeIndex = -1;
                Plugin.ChatGui.Print("Benchmark done.", "ViewInFFXIV");
                return;
            }

            BeginMode();
        }

        gpu?.BeginFrame();
        gpu?.Mark(0);
        section.Restart();
    }

    /// <summary>Between the video copy and the screen draw.</summary>
    public void VideoDone()
    {
        if (!Running)
            return;
        sample.CpuVideo += section.Elapsed.TotalMilliseconds;
        gpu?.Mark(1);
        section.Restart();
    }

    /// <summary>End of our per-frame work.</summary>
    public void EndFrame()
    {
        if (!Running)
            return;
        sample.CpuRender += section.Elapsed.TotalMilliseconds;
        sample.CpuFrames++;
        gpu?.Mark(2);
        gpu?.EndFrame();
    }

    public void Dispose() => gpu?.Dispose();

    private GpuTimer? CreateTimer(Device device)
    {
        try
        {
            var timer = new GpuTimer(device, 3);
            timer.Measured += ms =>
            {
                // Only count frames of the mode they ran in (results arrive a few frames late).
                if (clock.Elapsed.TotalSeconds - modeStart < WarmupSeconds)
                    return;
                sample.GpuVideo += ms[0];
                sample.GpuRender += ms[1];
                sample.GpuFrames++;
            };
            return timer;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "GPU timing unavailable");
            return null;
        }
    }

    private void BeginMode()
    {
        modeStart = clock.Elapsed.TotalSeconds;
        lastFrame = modeStart;
        sample = new Sample();
    }

    private void Report()
    {
        var mode = Modes[modeIndex];
        var times = sample.FrameTimes;
        if (times.Count == 0)
            return;

        times.Sort();
        var fps = times.Count / times.Sum() * 1000;
        var p50 = times[times.Count / 2];
        var p99 = times[Math.Min(times.Count - 1, (int)(times.Count * 0.99))];
        var wall = sample.WallFramesStart < 0 ? 0 : ((long)plugin.Video.Frames - sample.WallFramesStart) / ModeSeconds;
        var host = Math.Max(1, sample.HostSamples);
        var cpuFrames = Math.Max(1, sample.CpuFrames);
        var gpuFrames = Math.Max(1, sample.GpuFrames);
        var unfocused = 100.0 * sample.Unfocused / times.Count;

        Plugin.ChatGui.Print(
            $"bench {mode.Name}: {fps:0.0} fps (p50 {p50:0.0} / p99 {p99:0.0} ms) · wall {wall:0.0}/s · " +
            $"captured {sample.Arrived / host:0}/s dropped {sample.Skipped / host:0}/s · " +
            $"CPU video {sample.CpuVideo / cpuFrames:0.00} draw {sample.CpuRender / cpuFrames:0.00} ms · " +
            $"GPU video {sample.GpuVideo / gpuFrames:0.00} draw {sample.GpuRender / gpuFrames:0.00} ms ({sample.GpuFrames} frames)" +
            (unfocused > 0 ? $" · UNFOCUSED {unfocused:0}%" : ""),
            "ViewInFFXIV");
    }

    private static bool GameFocused()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid == Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out int processId);

    private sealed record Mode(string Name, bool SkipVideo, bool SkipRender);

    private sealed class Sample
    {
        public List<double> FrameTimes { get; } = new(1024);

        public int Unfocused { get; set; }

        public long WallFramesStart { get; set; } = -1;

        public double Arrived { get; set; }

        public double Skipped { get; set; }

        public int HostSamples { get; set; }

        public double CpuVideo { get; set; }

        public double CpuRender { get; set; }

        public int CpuFrames { get; set; }

        public double GpuVideo { get; set; }

        public double GpuRender { get; set; }

        public int GpuFrames { get; set; }
    }
}
