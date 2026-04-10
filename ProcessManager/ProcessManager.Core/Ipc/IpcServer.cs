using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ProcessManager.Core.Models;
using ProcessManager.Core.ProcessRegistry;

namespace ProcessManager.Core.Ipc;

public sealed class IpcServer : IDisposable
{
    private readonly Channel<Exception> _errors = Channel.CreateUnbounded<Exception>();
    public ChannelReader<Exception> Errors => _errors.Reader;

    private static readonly JsonSerializerOptions IpcJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ProcessRegistryService _registry;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, List<StreamWriter>> _stdoutSubscribers = new();
    private readonly ConcurrentDictionary<int, List<StreamWriter>> _stderrSubscribers = new();
    private readonly ConcurrentDictionary<int, bool> _stdoutSubscribed = new();
    private readonly ConcurrentDictionary<int, bool> _stderrSubscribed = new();
    private bool _disposed;

    public int Port { get; }

    public IpcServer(ProcessRegistryService registry, int port)
    {
        _registry = registry;
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                await _errors.Writer.WriteAsync(e, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (NetworkStream stream = client.GetStream())
        using (StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true))
        using (StreamWriter writer = new(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true })
        {
            try
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(line))
                    return;

                IpcRequest? request = JsonSerializer.Deserialize<IpcRequest>(line, IpcJsonOptions);
                if (request is null)
                {
                    await WriteResponseAsync(writer, IpcResponse.Fail("Invalid request")).ConfigureAwait(false);
                    return;
                }

                switch (request.Command)
                {
                    case "Register":
                        await HandleRegisterAsync(request, writer).ConfigureAwait(false);
                        break;
                    case "Stop":
                        await HandleStopAsync(request, writer).ConfigureAwait(false);
                        break;
                    case "Restart":
                        await HandleRestartAsync(request, writer).ConfigureAwait(false);
                        break;
                    case "Stdin":
                        await HandleStdinAsync(request, writer, reader, ct).ConfigureAwait(false);
                        break;
                    case "Stdout":
                        await HandleStdoutStreamAsync(request, writer, ct, liveOnly: false).ConfigureAwait(false);
                        return;
                    case "StdoutLiveOnly":
                        await HandleStdoutStreamAsync(request, writer, ct, liveOnly: true).ConfigureAwait(false);
                        return;
                    case "Stderr":
                        await HandleStderrStreamAsync(request, writer, ct, liveOnly: false).ConfigureAwait(false);
                        return;
                    case "StderrLiveOnly":
                        await HandleStderrStreamAsync(request, writer, ct, liveOnly: true).ConfigureAwait(false);
                        return;
                    case "IsAlreadyExist":
                        await HandleIsAlreadyExistAsync(request, writer).ConfigureAwait(false);
                        break;
                    case "List":
                        await HandleListAsync(writer).ConfigureAwait(false);
                        break;
                    default:
                        await WriteResponseAsync(writer, IpcResponse.Fail($"Unknown command: {request.Command}"))
                            .ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await WriteErrorResponseAsync(writer, ex, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteErrorResponseAsync(StreamWriter writer, Exception ex, CancellationToken ct)
    {
        await _errors.Writer.WriteAsync(ex, ct).ConfigureAwait(false);
        try
        {
            await WriteResponseAsync(writer, IpcResponse.Fail(ex.Message)).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task HandleRegisterAsync(IpcRequest request, StreamWriter writer)
    {
        if (string.IsNullOrEmpty(request.Exe))
        {
            await WriteResponseAsync(writer, IpcResponse.Fail("exe is required")).ConfigureAwait(false);
            return;
        }
        
        if (request.ProcessId.HasValue)
        {
            await RegisterExistingProcess(request, writer).ConfigureAwait(false);
            return;
        }

        // Register returns the kernel-assigned PID directly from the spawned Process
        (int kernelPid, int port) = _registry.Register(
            request.Exe,
            request.Args ?? string.Empty,
            request.Envs ?? string.Empty,
            request.WorkingDir ?? string.Empty,
            request.Name);

        await WriteResponseAsync(writer, new IpcResponse(Ok: true, ProcessId: kernelPid, Port: _registry.ServerPort))
            .ConfigureAwait(false);
    }

    private async Task RegisterExistingProcess(IpcRequest request, StreamWriter writer)
    {
        await Task.CompletedTask;
        var (kernelPid, _) = _registry.RegisterExisting(
            request.ProcessId!.Value, 
            request.Exe?? string.Empty, 
            request.Args?? string.Empty, 
            request.Envs ?? string.Empty, 
            request.WorkingDir ?? string.Empty, 
            request.Name);
        
        await WriteResponseAsync(writer, new IpcResponse(Ok: true, ProcessId: kernelPid, Port: _registry.ServerPort))
            .ConfigureAwait(false);
    }

    private async Task HandleStopAsync(IpcRequest request, StreamWriter writer)
    {
        if (request.ProcessId is null)
        {
            await WriteResponseAsync(writer, IpcResponse.Fail("processId is required")).ConfigureAwait(false);
            return;
        }

        bool ok = _registry.Stop(request.ProcessId.Value);
        await WriteResponseAsync(writer, new IpcResponse(Ok: ok)).ConfigureAwait(false);
    }

    private async Task HandleRestartAsync(IpcRequest request, StreamWriter writer)
    {
        if (request.ProcessId is null)
        {
            await WriteResponseAsync(writer, IpcResponse.Fail("processId is required")).ConfigureAwait(false);
            return;
        }

        bool ok = _registry.Restart(request.ProcessId.Value);
        await WriteResponseAsync(writer, new IpcResponse(Ok: ok)).ConfigureAwait(false);
    }

    private async Task HandleStdinAsync(IpcRequest request, StreamWriter writer, StreamReader reader,
        CancellationToken ct)
    {
        if (request.ProcessId is null)
        {
            await WriteResponseAsync(writer, IpcResponse.Fail("processId is required")).ConfigureAwait(false);
            return;
        }

        string? input = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        _registry.WriteStdin(request.ProcessId.Value, input);
        await WriteResponseAsync(writer, new IpcResponse(Ok: true)).ConfigureAwait(false);
    }

    private Task HandleStdoutStreamAsync(IpcRequest request, StreamWriter writer, CancellationToken ct, bool liveOnly)
        => HandleStreamAsync(request, writer, ct, liveOnly,
            isStdout: true,
            getSnapshot: m => m.GetStdoutSnapshot(),
            subscribers: _stdoutSubscribers,
            subscribed: _stdoutSubscribed);

    private Task HandleStderrStreamAsync(IpcRequest request, StreamWriter writer, CancellationToken ct, bool liveOnly)
        => HandleStreamAsync(request, writer, ct, liveOnly,
            isStdout: false,
            getSnapshot: m => m.GetStderrSnapshot(),
            subscribers: _stderrSubscribers,
            subscribed: _stderrSubscribed);

    private async Task HandleStreamAsync(
        IpcRequest request,
        StreamWriter writer,
        CancellationToken ct,
        bool liveOnly,
        bool isStdout,
        Func<ManagedProcess, IEnumerable<string>> getSnapshot,
        ConcurrentDictionary<int, List<StreamWriter>> subscribers,
        ConcurrentDictionary<int, bool> subscribed)
    {
        if (request.ProcessId is null)
        {
            await WriteResponseAsync(writer, IpcResponse.Fail("processId is required")).ConfigureAwait(false);
            return;
        }

        ManagedProcess? managed = _registry.Get(request.ProcessId.Value);
        if (managed is null)
        {
            await WriteResponseAsync(writer, IpcResponse.Fail("Process not found")).ConfigureAwait(false);
            return;
        }

        if (!liveOnly)
        {
            foreach (string bufferedLine in getSnapshot(managed))
            {
                if (ct.IsCancellationRequested) return;
                await writer.WriteLineAsync(bufferedLine).ConfigureAwait(false);
            }
        }

        List<StreamWriter> subs = subscribers.GetOrAdd(managed.ProcessId, _ => []);

        lock (subs)
        {
            subs.Add(writer);
        }

        if (subscribed.TryAdd(managed.ProcessId, true))
        {
            managed.SubscribeLines((line, lineIsStdout) =>
            {
                if (lineIsStdout != isStdout) return;
                lock (subs)
                {
                    foreach (StreamWriter w in subs)
                    {
                        try
                        {
                            w.WriteLine(line);
                            w.Flush();
                        }
                        catch { }
                    }
                }
            });
        }

        try
        {
            while (!ct.IsCancellationRequested && managed.IsRunning)
                await Task.Delay(500, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (subs)
            {
                subs.Remove(writer);
            }
        }
    }

    private async Task HandleIsAlreadyExistAsync(IpcRequest request, StreamWriter writer)
    {
        if (string.IsNullOrEmpty(request.Exe))
        {
            await WriteResponseAsync(writer, IpcResponse.Fail("exe is required")).ConfigureAwait(false);
            return;
        }

        bool exists = _registry.IsAlreadyExist(request.Exe, request.Args ?? string.Empty);
        await WriteResponseAsync(writer, new IpcResponse(Ok: true, Exists: exists)).ConfigureAwait(false);
    }

    private async Task HandleListAsync(StreamWriter writer)
    {
        IReadOnlyList<ManagedProcess> list = _registry.List();
        List<ProcessInfo> processes = list.Select(p => new ProcessInfo(
            p.ProcessId,
            p.Name,
            p.Exe,
            p.Args,
            p.WorkingDir,
            p.IsRunning)).ToList();
        string json = JsonSerializer.Serialize(new ListResponse(Ok: true, Processes: processes));
        await writer.WriteLineAsync(json).ConfigureAwait(false);
    }

    private static async Task WriteResponseAsync(StreamWriter writer, IpcResponse response)
    {
        string json = JsonSerializer.Serialize(response);
        await writer.WriteLineAsync(json).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _cts.Cancel();
        try { _listener.Stop(); }
        catch { }
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}