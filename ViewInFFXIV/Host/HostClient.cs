using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Host;

public sealed class HostClient : IDisposable
{
    private readonly IPluginLog log;
    private readonly ITextureProvider textures;
    private readonly string hostExe;
    private readonly ConcurrentQueue<HostCommand> outbound = new();
    private readonly SemaphoreSlim wake = new(0);
    private readonly CancellationTokenSource ipcCts = new();
    private readonly Task ipcLoop;
    private volatile Process? process;
    private FrameBuffer? frames;
    private IDalamudTextureWrap? wrap;
    private byte[] upload = [];
    private ulong lastGeneration;
    private DateTime lastLaunchUtc = DateTime.MinValue;
    private bool disposed;

    /// <summary>Latest status from the helper. Replaced (never mutated) by the IPC thread.</summary>
    public HostStatus Status { get; private set; } = new();

    public IDalamudTextureWrap? Texture => wrap;

    public bool HelperAlive
    {
        get
        {
            try
            {
                return process is { HasExited: false };
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public bool HostEnabled { get; private set; }

    public HostClient(IPluginLog log, ITextureProvider textures, string assemblyDirectory, bool hostEnabled)
    {
        this.log = log;
        this.textures = textures;
        HostEnabled = hostEnabled;
        var nested = Path.Combine(assemblyDirectory, "Host", "ViewInFFXIV.Host.exe");
        var beside = Path.Combine(assemblyDirectory, "ViewInFFXIV.Host.exe");
        hostExe = File.Exists(nested) ? nested : beside;
        // Pipe connect/read/write never runs on the game thread.
        ipcLoop = Task.Run(IpcLoopAsync);
    }

    public void Tick()
    {
        if (disposed)
            return;

        EnsureProcess();
        if (!HelperAlive)
            return;

        UploadFrame();
    }

    public void Send(HostCommand command)
    {
        outbound.Enqueue(command);
        wake.Release();
    }

    public void StartHost()
    {
        HostEnabled = true;
        EnsureProcess();
    }

    public void StopHost()
    {
        HostEnabled = false;
        var p = process;
        if (p != null && HelperAlive)
        {
            // Ask politely so the helper closes its Brave cleanly, then make sure it is gone.
            outbound.Clear();
            Send(HostCommand.Quit());
            try
            {
                if (!p.WaitForExit(1500))
                    p.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored
            }
        }

        process = null;
        p?.Dispose();
        wrap?.Dispose();
        wrap = null;
        frames?.Dispose();
        frames = null;
        lastGeneration = 0;
        Status = new HostStatus();
    }

    private void EnsureProcess()
    {
        if (!HostEnabled)
            return;
        if (HelperAlive)
            return;
        if (!File.Exists(hostExe))
        {
            Status = new HostStatus { Error = $"ViewInFFXIV.Host.exe not found at {hostExe}" };
            return;
        }

        if (DateTime.UtcNow - lastLaunchUtc < TimeSpan.FromSeconds(3))
            return;

        try
        {
            process?.Dispose();
            process = Process.Start(new ProcessStartInfo
            {
                FileName = hostExe,
                WorkingDirectory = Path.GetDirectoryName(hostExe)!,
                UseShellExecute = false,
            });
            lastLaunchUtc = DateTime.UtcNow;
            log.Information("Started ViewInFFXIV.Host from {Path}", hostExe);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to start ViewInFFXIV.Host");
            Status = new HostStatus { Error = ex.Message };
        }
    }

    private async Task IpcLoopAsync()
    {
        var ct = ipcCts.Token;
        NamedPipeClientStream? pipe = null;
        StreamWriter? writer = null;
        StreamReader? reader = null;

        void Drop()
        {
            try { writer?.Dispose(); } catch { /* ignored */ }
            try { reader?.Dispose(); } catch { /* ignored */ }
            try { pipe?.Dispose(); } catch { /* ignored */ }
            writer = null;
            reader = null;
            pipe = null;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!HelperAlive)
                {
                    Drop();
                    await Task.Delay(250, ct).ConfigureAwait(false);
                    continue;
                }

                if (pipe is not { IsConnected: true })
                {
                    Drop();
                    pipe = new NamedPipeClientStream(".", IpcConstants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(1000, ct).ConfigureAwait(false);
                    writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
                    reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                }

                // Commands go out immediately; otherwise poll the status a few times a second.
                if (!outbound.TryDequeue(out var command))
                {
                    await wake.WaitAsync(400, ct).ConfigureAwait(false);
                    if (!outbound.TryDequeue(out command))
                        command = HostCommand.Ping();
                }

                await writer!.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), ct).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var line = await reader!.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line == null)
                {
                    Drop();
                    continue;
                }

                if (JsonSerializer.Deserialize<HostStatus>(line) is { } status && HelperAlive)
                    Status = status;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Debug(ex, "ViewInFFXIV IPC failed; reconnecting");
                Drop();
                try
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Drop();
    }

    private void UploadFrame()
    {
        frames ??= FrameBuffer.TryOpen();
        if (frames == null)
            return;

        // Only copy the 8 MB frame when the helper actually published a new one.
        var published = frames.PeekGeneration();
        if (published == 0 || published == lastGeneration)
            return;

        if (!frames.TryCopyLatest(EnsureUpload(IpcConstants.MaxWidth * IpcConstants.MaxHeight * 4), out var width, out var height, out var generation, out _))
            return;
        if (generation == lastGeneration || width <= 0 || height <= 0)
            return;

        var packed = width * height * 4;
        try
        {
            var spec = RawImageSpecification.Bgra32(width, height);
            var next = textures.CreateFromRaw(spec, upload.AsSpan(0, packed), "ViewInFFXIV.Video");
            wrap?.Dispose();
            wrap = next;
            lastGeneration = generation;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Failed to upload ViewInFFXIV frame");
        }
    }

    private byte[] EnsureUpload(int size)
    {
        if (upload.Length < size)
            upload = new byte[size];
        return upload;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        StopHost();
        ipcCts.Cancel();
        try
        {
            ipcLoop.Wait(1000);
        }
        catch
        {
            // ignored
        }

        ipcCts.Dispose();
        wake.Dispose();
    }
}
