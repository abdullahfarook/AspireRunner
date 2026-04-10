using System.Text.Json.Serialization;

namespace ProcessManager.Core.Models;

public sealed record IpcRequest(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("processId")] int? ProcessId = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("exe")] string? Exe = null,
    [property: JsonPropertyName("args")] string? Args = null,
    [property: JsonPropertyName("envs")] string? Envs = null,
    [property: JsonPropertyName("workingDir")] string? WorkingDir = null,
    [property: JsonPropertyName("liveOnly")] bool LiveOnly = false,
    [property: JsonPropertyName("burstDelayOutput")] int? BurstDelayOutput = null);

public sealed record IpcResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("processId")] int? ProcessId = null,
    [property: JsonPropertyName("port")] int? Port = null,
    [property: JsonPropertyName("exists")] bool? Exists = null,
    [property: JsonPropertyName("error")] string? Error = null)
{
    public static IpcResponse Fail(string message) => new(Ok: false, Error: message);
    public static IpcResponse Success() => new(Ok: true);
}

public sealed record ProcessInfo(
    [property: JsonPropertyName("processId")] int ProcessId,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("exe")] string Exe = "",
    [property: JsonPropertyName("args")] string Args = "",
    [property: JsonPropertyName("workingDir")] string WorkingDir = "",
    [property: JsonPropertyName("running")] bool Running = false);

public sealed record ListResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("processes")] List<ProcessInfo>? Processes = null,
    [property: JsonPropertyName("error")] string? Error = null);
