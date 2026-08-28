using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using InView.Shared;

namespace InView.Host;

internal sealed class PipeServer : IDisposable
{
    private readonly HostForm form;
    private readonly CancellationTokenSource cts = new();
    private Task? loop;

    public PipeServer(HostForm form)
    {
        this.form = form;
    }

    public void Start()
    {
        loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    IpcConstants.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);
                await HandleClientAsync(pipe).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(250, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                if (pipe != null)
                    await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
        {
            AutoFlush = true,
        };

        while (pipe.IsConnected && !cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            if (line == null)
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            HostCommand? command = null;
            try
            {
                command = JsonSerializer.Deserialize<HostCommand>(line);
            }
            catch
            {
                // ignore malformed lines
            }

            if (command != null)
                form.BeginInvoke(() => form.ApplyCommand(command));

            var status = form.BuildStatus();
            await writer.WriteLineAsync(JsonSerializer.Serialize(status)).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        try
        {
            loop?.Wait(500);
        }
        catch
        {
            // ignored
        }

        cts.Dispose();
    }
}
