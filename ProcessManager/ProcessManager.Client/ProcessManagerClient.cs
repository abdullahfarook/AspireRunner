using System.Diagnostics;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;

namespace ProcessManager.Client;

public sealed class ProcessManagerClient
{
    public const int DefaultPort = 38472;

    public int Port { get; }

    public ProcessManagerClient(int port = DefaultPort)
    {
        Port = port;
    }

    public bool IsManagerRunning()
    {
        try
        {
            using TcpClient client = new();
            client.Connect(IPAddress.Loopback, Port);
            return true;
        }
        catch
        {
            return false;
        }
    }
    public async Task WaitForManagerAsync(CancellationToken cancellationToken = default)
    {
        const int maxRetries = 30;
        const int delayMs = 500;

        for (int i = 0; i < maxRetries; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (IsManagerRunning())
            {
                return;
            }

            await Task.Delay(delayMs, cancellationToken);
        }

        throw new InvalidOperationException($"Process Manager did not become available on port {Port} within {maxRetries * delayMs / 1000}s.");
    }
    private readonly List<string[]> _candidates =
        [
            ["ProcessManager.Host.exe"],
            ["bin", "Debug", "net8.0", "ProcessManager.Host.exe"],
            ["bin", "Release", "net8.0", "ProcessManager.Host.exe"],
        ];
    public void StartManagerIfNeeded(string? baseDir = null)
    {
        baseDir??= AppContext.BaseDirectory;
        var candidates =  _candidates.Select(path => Path.Combine([baseDir,..path])).ToArray();
        
        string? exePath = null;
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                exePath = candidate;
                break;
            }
        }

        if (string.IsNullOrEmpty(exePath))
        {
            throw new FileNotFoundException("ProcessManager.exe not found. Build the ProcessManager project first.", string.Join("; ", candidates));
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = exePath,
            Arguments = Port.ToString(),
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(startInfo);
        Thread.Sleep(1500);
    }

    public async Task<Result<ProcessInfo>> TryRegister(string exe, string args, string? envs, string workingDir, string? name = null, int? pid = null)
    {
        await Task.CompletedTask;
        var existsResponse = SendRequest(
            new IpcRequest(Command: "IsAlreadyExist", Exe: exe, ProcessId: pid, Args: args, Envs: envs));

        if (existsResponse is { Ok: true, Exists: true })
        {
            var listResponse = List();
            var match = listResponse?.Processes?.FirstOrDefault(p =>
                string.Equals(p.Exe, exe, StringComparison.OrdinalIgnoreCase) 
                && p.ProcessId == pid
                || (p.Args == args && p.Running)
            );
 
            if (match is not null)
            {
                pid = match.ProcessId;
                return match with { Message = $"Re-attaching to existing App (PID {pid})." };
            }
        }

        var registerResponse = SendRequest(
            new IpcRequest(
                Command: "Register",
                Name: name,
                Exe: exe,
                ProcessId: pid,
                Args: args,
                Envs: envs,
                WorkingDir: workingDir));

        if (registerResponse is null or { Ok: false })
        {
            return registerResponse?.Error ?? "No response";
        }
        pid = registerResponse.ProcessId!.Value;
        return new ProcessInfo(pid.Value, name, exe, args, workingDir, true, Message: $"App registered (PID {pid}).");
    }

    public IpcResponse? SendRequest(IpcRequest request)
    {
        try
        {
            using TcpClient client = new();
            client.Connect(IPAddress.Loopback, Port);
            client.ReceiveTimeout = 500000;
            client.SendTimeout = 500000;
            using var stream = client.GetStream();
            using StreamReader reader = new(stream, Encoding.UTF8);
            using StreamWriter writer = new(stream, Encoding.UTF8) { AutoFlush = true };
            var json = JsonSerializer.Serialize(request);
            writer.WriteLine(json);
            var responseLine = reader.ReadLine();
            return string.IsNullOrEmpty(responseLine) ? null : JsonSerializer.Deserialize<IpcResponse>(responseLine);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public ListResponse? List()
    {
        try
        {
            using TcpClient client = new();
            client.Connect(IPAddress.Loopback, Port);
            using var stream = client.GetStream();
            using StreamReader reader = new(stream, Encoding.UTF8);
            using StreamWriter writer = new(stream, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(new IpcRequest(Command: "List")));
            var line = reader.ReadLine();
            return string.IsNullOrEmpty(line) ? null : JsonSerializer.Deserialize<ListResponse>(line);
        }
        catch
        {
            return null;
        }
    }

    public void StreamStdout(int processId, Action<string> onLine, bool liveOnly = false)
    {
        var command = liveOnly ? "StdoutLiveOnly" : "Stdout";
        using TcpClient client = new();
        client.Connect(IPAddress.Loopback, Port);
        client.ReceiveTimeout = 0;
        using var stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.UTF8);
        using StreamWriter writer = new(stream, Encoding.UTF8) { AutoFlush = true };
        writer.WriteLine(JsonSerializer.Serialize(new IpcRequest(Command: command, ProcessId: processId)));
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            onLine(line);
        }
    }

    public void StreamStderr(int processId, Action<string> onLine, bool liveOnly = false)
    {
        var command = liveOnly ? "StderrLiveOnly" : "Stderr";
        using TcpClient client = new();
        client.Connect(IPAddress.Loopback, Port);
        client.ReceiveTimeout = 0;
        using var stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.UTF8);
        using StreamWriter writer = new(stream, Encoding.UTF8) { AutoFlush = true };
        writer.WriteLine(JsonSerializer.Serialize(new IpcRequest(Command: command, ProcessId: processId)));
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            onLine(line);
        }
    }
}

public sealed record IpcRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("command")] string Command,
    [property: System.Text.Json.Serialization.JsonPropertyName("processId")] int? ProcessId = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("exe")] string? Exe = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("args")] string? Args = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("envs")] string? Envs = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("workingDir")] string? WorkingDir = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("liveOnly")] bool LiveOnly = false);

public sealed record IpcResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("ok")] bool Ok,
    [property: System.Text.Json.Serialization.JsonPropertyName("processId")] int? ProcessId = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("port")] int? Port = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("exists")] bool? Exists = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Error = null);

public sealed record ProcessInfo(
    [property: System.Text.Json.Serialization.JsonPropertyName("processId")] int ProcessId,
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("exe")] string Exe = "",
    [property: System.Text.Json.Serialization.JsonPropertyName("args")] string Args = "",
    [property: System.Text.Json.Serialization.JsonPropertyName("workingDir")] string WorkingDir = "",
    [property: System.Text.Json.Serialization.JsonPropertyName("running")] bool Running = false,
    [property: System.Text.Json.Serialization.JsonPropertyName("message")] string? Message = null);

public sealed record ListResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("ok")] bool Ok,
    [property: System.Text.Json.Serialization.JsonPropertyName("processes")] List<ProcessInfo>? Processes = null);