using System.Text.Json.Serialization;

namespace AspireRunner.Tool.ProcessManager.Lpc;

internal sealed record LpcRequest(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("processId")] int? ProcessId = null,
    [property: JsonPropertyName("port")] int? Port = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("exe")] string? Exe = null,
    [property: JsonPropertyName("args")] string? Args = null,
    [property: JsonPropertyName("envs")] string? Envs = null,
    [property: JsonPropertyName("workingDir")] string? WorkingDir = null,
    [property: JsonPropertyName("liveOnly")] bool LiveOnly = false,
    [property: JsonPropertyName("keepRunning")] bool KeepRunning = false,
    [property: JsonPropertyName("burstDelayOutput")] int? BurstDelayOutput = null);

internal sealed record LpcResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("processId")] int? ProcessId = null,
    [property: JsonPropertyName("port")] int? Port = null,
    [property: JsonPropertyName("exists")] bool? Exists = null,
    [property: JsonPropertyName("error")] string? Error = null)
{
    public static LpcResponse Fail(string message) => new(Ok: false, Error: message);
}

internal sealed record LpcProcessInfo(
    [property: JsonPropertyName("processId")] int ProcessId,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("exe")] string Exe = "",
    [property: JsonPropertyName("args")] string Args = "",
    [property: JsonPropertyName("workingDir")] string WorkingDir = "",
    [property: JsonPropertyName("running")] bool Running = false,
    [property: JsonPropertyName("ports")] IReadOnlyList<int>? Ports = null,
    [property: JsonPropertyName("message")] string? Message = null);

internal sealed record LpcListResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("processes")] List<LpcProcessInfo>? Processes = null,
    [property: JsonPropertyName("error")] string? Error = null);