using AspireRunner.Core.Abstractions;
using AspireRunner.Core.Models;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace AspireRunner.Tool.ProcessManager.Lpc;

internal sealed class LpcServer : IDisposable
{
    public const int DefaultPort = 38472;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly InMemoryProcessManager _manager;
    private readonly IProcessFactory _processFactory;
    private readonly ILogger<LpcServer> _logger;
    private readonly Func<Task>? _shutdownRequested;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _isRunning;
    private bool _disposed;

    public int Port { get; }

    public bool IsRunning => _isRunning && !_disposed;

    public LpcServer(
        InMemoryProcessManager manager,
        IProcessFactory processFactory,
        ILogger<LpcServer> logger,
        int port = DefaultPort,
        Func<Task>? shutdownRequested = null)
    {
        _manager = manager;
        _processFactory = processFactory;
        _logger = logger;
        _shutdownRequested = shutdownRequested;

        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public bool Start()
    {
        if (_disposed || _isRunning)
        {
            return false;
        }

        try
        {
            _listener.Start();
            _isRunning = true;

            _ = AcceptLoopAsync(_shutdownCts.Token);
            _logger.LogInformation("LPC server started on {Host}:{Port}", IPAddress.Loopback, Port);
            return true;
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "Failed to start LPC server on {Host}:{Port}", IPAddress.Loopback, Port);
            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(tcpClient, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LPC accept loop failure");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
        using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true })
        {
            string? requestLine;
            try
            {
                requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(requestLine))
            {
                return;
            }

            LpcRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<LpcRequest>(requestLine, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid LPC payload: {Payload}", requestLine);
                await WriteResponseAsync(writer, LpcResponse.Fail("Invalid request payload")).ConfigureAwait(false);
                return;
            }

            if (request is null)
            {
                await WriteResponseAsync(writer, LpcResponse.Fail("Invalid request payload")).ConfigureAwait(false);
                return;
            }

            await HandleRequestAsync(request, writer, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleRequestAsync(LpcRequest request, StreamWriter writer, CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case "List":
                await HandleListAsync(writer).ConfigureAwait(false);
                break;
            case "IsAlreadyExist":
                await HandleIsAlreadyExistAsync(request, writer).ConfigureAwait(false);
                break;
            case "Register":
                await HandleRegisterAsync(request, writer, cancellationToken).ConfigureAwait(false);
                break;
            case "Stop":
                await HandleStopAsync(request, writer, cancellationToken).ConfigureAwait(false);
                break;
            case "Restart":
                await HandleRestartAsync(request, writer, cancellationToken).ConfigureAwait(false);
                break;
            case "Remove":
            case "Delete":
                await HandleRemoveAsync(request, writer).ConfigureAwait(false);
                break;
            case "Shutdown":
                await HandleShutdownAsync(writer).ConfigureAwait(false);
                break;
            case "Stdin":
                await WriteResponseAsync(writer, LpcResponse.Fail("Stdin forwarding is not supported by AspireRunner LPC")).ConfigureAwait(false);
                break;
            case "Stdout":
                await HandleOutputStreamAsync(request, writer, cancellationToken, isStdout: true, liveOnly: false).ConfigureAwait(false);
                return;
            case "StdoutLiveOnly":
                await HandleOutputStreamAsync(request, writer, cancellationToken, isStdout: true, liveOnly: true).ConfigureAwait(false);
                return;
            case "Stderr":
                await HandleOutputStreamAsync(request, writer, cancellationToken, isStdout: false, liveOnly: false).ConfigureAwait(false);
                return;
            case "StderrLiveOnly":
                await HandleOutputStreamAsync(request, writer, cancellationToken, isStdout: false, liveOnly: true).ConfigureAwait(false);
                return;
            default:
                await WriteResponseAsync(writer, LpcResponse.Fail($"Unknown command: {request.Command}")).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleOutputStreamAsync(LpcRequest request, StreamWriter writer, CancellationToken cancellationToken, bool isStdout, bool liveOnly)
    {
        if (request.ProcessId is null)
        {
            await WriteResponseAsync(writer, LpcResponse.Fail("processId is required")).ConfigureAwait(false);
            return;
        }

        if (!_manager.TryGetByPid(request.ProcessId.Value, out var entry) || entry is null)
        {
            await WriteResponseAsync(writer, LpcResponse.Fail("Process not found")).ConfigureAwait(false);
            return;
        }

        if (entry.Process is not IProcessOutputSource outputSource)
        {
            await WriteResponseAsync(writer, LpcResponse.Fail("Process output streaming is not supported")).ConfigureAwait(false);
            return;
        }

        if (!liveOnly)
        {
            var snapshot = isStdout ? outputSource.GetStdoutSnapshot() : outputSource.GetStderrSnapshot();
            foreach (var line in snapshot)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
        }

        var streamClosed = false;
        void ForwardLine(string line)
        {
            if (streamClosed)
            {
                return;
            }

            try
            {
                writer.WriteLine(line);
                writer.Flush();
            }
            catch
            {
                streamClosed = true;
            }
        }

        using var subscription = isStdout
            ? outputSource.SubscribeStdout(ForwardLine)
            : outputSource.SubscribeStderr(ForwardLine);

        while (!cancellationToken.IsCancellationRequested && !streamClosed)
        {
            if (!entry.Process.IsRunning)
            {
                break;
            }

            try
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task HandleIsAlreadyExistAsync(LpcRequest request, StreamWriter writer)
    {
        if (string.IsNullOrWhiteSpace(request.Exe))
        {
            await WriteResponseAsync(writer, LpcResponse.Fail("exe is required")).ConfigureAwait(false);
            return;
        }

        var exists = _manager.IsAlreadyExist(request.Exe, request.Args ?? string.Empty);
        await WriteResponseAsync(writer, new LpcResponse(Ok: true, Exists: exists)).ConfigureAwait(false);
    }

    private async Task HandleRegisterAsync(LpcRequest request, StreamWriter writer, CancellationToken cancellationToken)
    {
        if (request.ProcessId is int existingPid)
        {
            if (!_manager.TryGetByPid(existingPid, out var existingEntry) || existingEntry is null)
            {
                await WriteResponseAsync(writer, LpcResponse.Fail("Attaching to external process IDs is not supported")).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(writer, new LpcResponse(Ok: true, ProcessId: existingEntry.LastKnownPid ?? existingPid, Port: Port)).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Exe))
        {
            await WriteResponseAsync(writer, LpcResponse.Fail("exe is required")).ConfigureAwait(false);
            return;
        }

        var environmentVariables = ParseEnvironmentVariables(request.Envs);
        var parsedArguments = ParseArguments(request.Args);
        var inferredPorts = InferPorts(parsedArguments, request.Envs, request.Port);
        var processOptions = new ExecutableProcessOptions
        {
            ExecutablePath = request.Exe,
            DisplayName = string.IsNullOrWhiteSpace(request.Name)
                ? Path.GetFileNameWithoutExtension(request.Exe)
                : request.Name,
            Arguments = parsedArguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDir) ? null : request.WorkingDir,
            EnvironmentVariables = environmentVariables,
            PipeOutput = true,
            RestartOnFailure = false,
            RestartDelaySeconds = 2
        };

        var managedProcess = await _processFactory
            .CreateProcessAsync(ProcessCreationRequest.Executable(processOptions))
            .ConfigureAwait(false);

        if (managedProcess is null)
        {
            await WriteResponseAsync(writer, LpcResponse.Fail("Failed to create process instance")).ConfigureAwait(false);
            return;
        }

        await managedProcess.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!managedProcess.IsRunning)
        {
            await WriteResponseAsync(writer, LpcResponse.Fail("Failed to start process")).ConfigureAwait(false);
            return;
        }

        var argsText = request.Args ?? string.Empty;
        var entry = _manager.Register(
            managedProcess,
            ProcessProfile.ExecutableProcess,
            command: BuildCommandPreview(request.Exe, parsedArguments),
            details: BuildDetails(processOptions, managedProcess, inferredPorts),
            executable: request.Exe,
            arguments: argsText,
            environmentVariables: request.Envs,
            workingDirectory: request.WorkingDir,
            exposedPorts: inferredPorts);

        await WriteResponseAsync(
                writer,
                new LpcResponse(Ok: true, ProcessId: entry.LastKnownPid ?? managedProcess.Pid, Port: Port))
            .ConfigureAwait(false);
    }

    private async Task HandleStopAsync(LpcRequest request, StreamWriter writer, CancellationToken cancellationToken)
    {
        if (request.ProcessId is null)
        {
            await WriteResponseAsync(writer, LpcResponse.Fail("processId is required")).ConfigureAwait(false);
            return;
        }

        if (!_manager.TryGetByPid(request.ProcessId.Value, out var entry) || entry is null)
        {
            await WriteResponseAsync(writer, LpcResponse.Fail("Process not found")).ConfigureAwait(false);
            return;
        }

        var stopped = await _manager.StopAsync(entry.Id, cancellationToken).ConfigureAwait(false);
        if (!stopped)
        {
            await WriteResponseAsync(writer, LpcResponse.Fail("Failed to stop process")).ConfigureAwait(false);
            return;
        }

        await WriteResponseAsync(writer, new LpcResponse(Ok: true, ProcessId: entry.LastKnownPid ?? request.ProcessId)).ConfigureAwait(false);
    }

    private async Task HandleRestartAsync(LpcRequest request, StreamWriter writer, CancellationToken cancellationToken)
    {
        if (request.ProcessId is null)
        {
            await WriteResponseAsync(writer, LpcResponse.Fail("processId is required")).ConfigureAwait(false);
            return;
        }

        if (!_manager.TryGetByPid(request.ProcessId.Value, out var entry) || entry is null)
        {
            await WriteResponseAsync(writer, LpcResponse.Fail("Process not found")).ConfigureAwait(false);
            return;
        }

        var restarted = await _manager.RestartAsync(entry.Id, cancellationToken).ConfigureAwait(false);
        if (!restarted)
        {
            await WriteResponseAsync(writer, LpcResponse.Fail("Failed to restart process")).ConfigureAwait(false);
            return;
        }

        await WriteResponseAsync(writer, new LpcResponse(Ok: true, ProcessId: entry.LastKnownPid ?? request.ProcessId)).ConfigureAwait(false);
    }

    private async Task HandleRemoveAsync(LpcRequest request, StreamWriter writer)
    {
        if (request.ProcessId is null)
        {
            await WriteResponseAsync(writer, LpcResponse.Fail("processId is required")).ConfigureAwait(false);
            return;
        }

        if (!_manager.TryGetByPid(request.ProcessId.Value, out var entry) || entry is null)
        {
            await WriteResponseAsync(writer, LpcResponse.Fail("Process not found")).ConfigureAwait(false);
            return;
        }

        var removed = await _manager.RemoveAsync(entry.Id, stopIfRunning: !request.KeepRunning, CancellationToken.None).ConfigureAwait(false);
        if (!removed)
        {
            await WriteResponseAsync(writer, LpcResponse.Fail("Failed to remove process")).ConfigureAwait(false);
            return;
        }

        await WriteResponseAsync(writer, new LpcResponse(Ok: true, ProcessId: entry.LastKnownPid ?? request.ProcessId)).ConfigureAwait(false);
    }

    private async Task HandleShutdownAsync(StreamWriter writer)
    {
        try
        {
            await _manager.StopAllAsync(CancellationToken.None).ConfigureAwait(false);
            await _manager.RemoveAllAsync(stopIfRunning: false, CancellationToken.None).ConfigureAwait(false);
            await WriteResponseAsync(writer, new LpcResponse(Ok: true, Port: Port)).ConfigureAwait(false);

            if (_shutdownRequested is not null)
            {
                await _shutdownRequested().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to shutdown managed processes through LPC shutdown command");
            await WriteResponseAsync(writer, LpcResponse.Fail("Failed to shutdown host")).ConfigureAwait(false);
        }
    }

    private async Task HandleListAsync(StreamWriter writer)
    {
        var entries = _manager.List();
        var processes = entries
            .Where(e => e.LastKnownPid is not null)
            .Select(e => new LpcProcessInfo(
                ProcessId: e.LastKnownPid!.Value,
                Name: e.Process.DisplayName,
                Exe: e.Executable ?? string.Empty,
                Args: e.Arguments ?? string.Empty,
                WorkingDir: e.WorkingDirectory ?? string.Empty,
                Running: e.Process.IsRunning,
                Ports: e.ExposedPorts,
                Message: e.Details))
            .ToList();

        await writer.WriteLineAsync(JsonSerializer.Serialize(new LpcListResponse(Ok: true, Processes: processes))).ConfigureAwait(false);
    }

    private static async Task WriteResponseAsync(StreamWriter writer, LpcResponse response)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
    }

    private static Dictionary<string, string?> ParseEnvironmentVariables(string? raw)
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return variables;
        }

        foreach (var item in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = item.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = item[..separatorIndex].Trim();
            var value = item[(separatorIndex + 1)..];
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            variables[key] = value;
        }

        return variables;
    }

    private static string[] ParseArguments(string? rawArguments)
    {
        if (string.IsNullOrWhiteSpace(rawArguments))
        {
            return [];
        }

        var inQuotes = false;
        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var character in rawArguments.Trim())
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return [.. tokens];
    }

    private static string BuildCommandPreview(string executable, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return executable;
        }

        return $"{executable} {string.Join(' ', arguments)}";
    }

    private static string BuildDetails(ExecutableProcessOptions options, IManagedProcess process, IReadOnlyList<int> ports)
    {
        var details = new List<string>();
        if (ports.Count > 0)
        {
            details.Add($"port={string.Join('/', ports)}");
        }

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            details.Add($"cwd={options.WorkingDirectory}");
        }

        if (options.Arguments.Count > 0)
        {
            details.Add($"args={string.Join(' ', options.Arguments)}");
        }

        details.Add(process.IsRunning ? "running" : "stopped");
        return string.Join(", ", details);
    }

    private static IReadOnlyList<int> InferPorts(IReadOnlyList<string> args, string? envs, int? explicitPort)
    {
        var ports = new HashSet<int>();

        if (explicitPort is > ushort.MinValue and <= ushort.MaxValue)
        {
            ports.Add(explicitPort.Value);
        }

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if ((arg.Equals("--port", StringComparison.OrdinalIgnoreCase) || arg.Equals("-p", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Count)
            {
                if (int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort)
                    && parsedPort is > ushort.MinValue and <= ushort.MaxValue)
                {
                    ports.Add(parsedPort);
                }

                continue;
            }

            if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg[7..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inlinePort)
                && inlinePort is > ushort.MinValue and <= ushort.MaxValue)
            {
                ports.Add(inlinePort);
                continue;
            }

            if (Uri.TryCreate(arg, UriKind.Absolute, out var uri) && uri.Port is > ushort.MinValue and <= ushort.MaxValue)
            {
                ports.Add(uri.Port);
            }
        }

        if (!string.IsNullOrWhiteSpace(envs))
        {
            foreach (var pair in envs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!pair.StartsWith("PORT=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (int.TryParse(pair[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var envPort)
                    && envPort is > ushort.MinValue and <= ushort.MaxValue)
                {
                    ports.Add(envPort);
                }
            }
        }

        return [.. ports.OrderBy(p => p)];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _shutdownCts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // Ignore listener shutdown exceptions.
        }

        _shutdownCts.Dispose();
        _disposed = true;
    }
}