using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Host;

public sealed class HostClient : IDisposable
{
    private readonly IPluginLog log;
    private readonly string hostExe;
    private readonly ConcurrentQueue<HostCommand> outbound = new();
    private readonly SemaphoreSlim wake = new(0);
    private readonly CancellationTokenSource ipcCts = new();
    private readonly Task ipcLoop;
    private volatile Process? process;
    private volatile bool connected;
    private DateTime lastLaunchUtc = DateTime.MinValue;
    private bool disposed;

    /// <summary>Latest status from the helper. Replaced (never mutated) by the IPC thread.</summary>
    public HostStatus Status { get; private set; } = new();

    /// <summary>
    /// Our helper process is running, or we are talking to one. A helper that was already running for this user
    /// (the new one exits at once, it allows one per user) is used instead of relaunching every few seconds.
    /// </summary>
    public bool HelperAlive => connected || ProcessAlive;

    private bool ProcessAlive
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

    public HostClient(IPluginLog log, string assemblyDirectory)
    {
        this.log = log;
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

    /// <summary>Closes the helper (and its Brave). Does not block the game; waits only when the plugin unloads.</summary>
    public void StopHost() => StopHost(wait: false);

    private void StopHost(bool wait)
    {
        HostEnabled = false;
        var p = process;
        process = null;
        if (p != null || connected)
        {
            // Ask politely so the helper closes its Brave cleanly; the IPC thread delivers it.
            outbound.Clear();
            Send(HostCommand.Quit());
        }

        Status = new HostStatus();
        if (p == null)
            return;

        void Finish()
        {
            try
            {
                if (!p.WaitForExit(3000))
                    p.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }

            p.Dispose();
        }

        if (wait)
            Finish();
        else
            _ = Task.Run(Finish);
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
            var start = new ProcessStartInfo
            {
                FileName = hostExe,
                WorkingDirectory = Path.GetDirectoryName(hostExe)!,
                UseShellExecute = false,
            };

            // The helper closes itself (and its Brave) when the game exits.
            start.ArgumentList.Add("--parent");
            start.ArgumentList.Add(Environment.ProcessId.ToString());

            // Run the helper on the .NET that XIVLauncher already ships, so nobody has to install .NET.
            if (DalamudDotnetRoot() is { } root)
                start.Environment["DOTNET_ROOT"] = root;

            process = Process.Start(start);
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
                // Talk to a helper while one is wanted (or ours is running); after Stop, only until the quit is out.
                var wanted = HostEnabled || ProcessAlive;
                if (pipe is not { IsConnected: true })
                {
                    connected = false;
                    Drop();
                    if (!wanted)
                    {
                        await Task.Delay(250, ct).ConfigureAwait(false);
                        continue;
                    }

                    pipe = new NamedPipeClientStream(".", IpcConstants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(1000, ct).ConfigureAwait(false);
                    writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
                    reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                    connected = true;
                }
                else if (!wanted && outbound.IsEmpty)
                {
                    connected = false;
                    Drop();
                    continue;
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

                if (JsonSerializer.Deserialize<HostStatus>(line) is { } status && HostEnabled)
                    Status = status;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Debug(ex, "ViewInFFXIV IPC failed; reconnecting");
                connected = false;
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

        connected = false;
        Drop();
    }

    /// <summary>
    /// Root of the .NET runtime the game is running Dalamud on (e.g. %AppData%/XIVLauncher/runtime), if it
    /// also has the Windows Desktop runtime the helper needs.
    /// </summary>
    private static string? DalamudDotnetRoot()
    {
        try
        {
            // The runtime directory is <root>/shared/Microsoft.NETCore.App/<version>/.
            var root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            return Directory.Exists(Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App")) ? root : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        StopHost(wait: true);

        // Give the IPC thread a moment to deliver the quit to a helper we did not start ourselves.
        for (var i = 0; i < 10 && connected && !outbound.IsEmpty; i++)
            Thread.Sleep(50);

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
