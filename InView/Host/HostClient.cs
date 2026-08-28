using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using InView.Shared;

namespace InView.Host;

public sealed class HostClient : IDisposable
{
    private readonly IPluginLog log;
    private readonly ITextureProvider textures;
    private readonly string hostExe;
    private readonly ConcurrentQueue<HostCommand> outbound = new();
    private NamedPipeClientStream? pipe;
    private StreamWriter? writer;
    private StreamReader? reader;
    private Process? process;
    private FrameBuffer? frames;
    private IDalamudTextureWrap? wrap;
    private byte[] upload = [];
    private ulong lastGeneration;
    private DateTime lastLaunchUtc = DateTime.MinValue;
    private DateTime lastPingUtc = DateTime.MinValue;
    private DateTime lastPipeAttemptUtc = DateTime.MinValue;
    private bool disposed;

    public HostStatus Status { get; private set; } = new();

    public IDalamudTextureWrap? Texture => wrap;

    public bool HelperAlive => process is { HasExited: false };

    public HostClient(IPluginLog log, ITextureProvider textures, string assemblyDirectory)
    {
        this.log = log;
        this.textures = textures;
        var nested = Path.Combine(assemblyDirectory, "Host", "InView.Host.exe");
        var beside = Path.Combine(assemblyDirectory, "InView.Host.exe");
        hostExe = File.Exists(nested) ? nested : beside;
    }

    public void Tick()
    {
        if (disposed)
            return;

        EnsureProcess();
        EnsurePipe();
        PumpIpc();
        UploadFrame();
    }

    public void Send(HostCommand command)
    {
        outbound.Enqueue(command);
    }

    public void ShowHostWindow() => Send(HostCommand.SetVisible(true));

    public void HideHostWindow() => Send(HostCommand.SetVisible(false));

    private void EnsureProcess()
    {
        if (HelperAlive)
            return;
        if (!File.Exists(hostExe))
        {
            Status = new HostStatus { Error = $"InView.Host.exe not found at {hostExe}" };
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
            log.Information("Started InView.Host from {Path}", hostExe);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to start InView.Host");
            Status = new HostStatus { Error = ex.Message };
        }
    }

    private void EnsurePipe()
    {
        if (pipe is { IsConnected: true })
            return;
        if (!HelperAlive)
            return;
        if (DateTime.UtcNow - lastPipeAttemptUtc < TimeSpan.FromMilliseconds(250))
            return;

        lastPipeAttemptUtc = DateTime.UtcNow;
        try
        {
            DisposePipe();
            pipe = new NamedPipeClientStream(
                ".",
                IpcConstants.PipeName,
                PipeDirection.InOut,
                PipeOptions.None);
            pipe.Connect(80);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
            {
                AutoFlush = true,
            };
            reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            frames ??= FrameBuffer.TryOpen();
        }
        catch
        {
            DisposePipe();
        }
    }

    private void PumpIpc()
    {
        if (pipe is not { IsConnected: true } || writer == null || reader == null)
            return;

        try
        {
            HostCommand command;
            if (!outbound.TryDequeue(out command!))
            {
                if (DateTime.UtcNow - lastPingUtc < TimeSpan.FromMilliseconds(400))
                    return;
                command = HostCommand.Ping();
            }

            writer.WriteLine(JsonSerializer.Serialize(command));
            lastPingUtc = DateTime.UtcNow;
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
                return;
            var status = JsonSerializer.Deserialize<HostStatus>(line);
            if (status != null)
                Status = status;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "InView IPC pump failed");
            DisposePipe();
        }
    }

    private void UploadFrame()
    {
        frames ??= FrameBuffer.TryOpen();
        if (frames == null)
            return;

        if (!frames.TryCopyLatest(EnsureUpload(IpcConstants.MaxWidth * IpcConstants.MaxHeight * 4), out var width, out var height, out var generation, out _))
            return;
        if (generation == lastGeneration || width <= 0 || height <= 0)
            return;

        var packed = width * height * 4;
        try
        {
            var spec = RawImageSpecification.Bgra32(width, height);
            var next = textures.CreateFromRaw(spec, upload.AsSpan(0, packed), "InView.Video");
            wrap?.Dispose();
            wrap = next;
            lastGeneration = generation;
            Status.Width = width;
            Status.Height = height;
            Status.Generation = generation;
            Status.Alive = true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Failed to upload InView frame");
        }
    }

    private byte[] EnsureUpload(int size)
    {
        if (upload.Length < size)
            upload = new byte[size];
        return upload;
    }

    private void DisposePipe()
    {
        try { writer?.Dispose(); } catch { /* ignored */ }
        try { reader?.Dispose(); } catch { /* ignored */ }
        try { pipe?.Dispose(); } catch { /* ignored */ }
        writer = null;
        reader = null;
        pipe = null;
    }

    public void Dispose()
    {
        disposed = true;
        try
        {
            if (pipe is { IsConnected: true } && writer != null)
                writer.WriteLine(JsonSerializer.Serialize(HostCommand.Quit()));
        }
        catch
        {
            // ignored
        }

        DisposePipe();
        wrap?.Dispose();
        wrap = null;
        frames?.Dispose();
        frames = null;
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignored
        }

        process?.Dispose();
        process = null;
    }
}
