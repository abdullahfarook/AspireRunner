using AspireRunner.Core.Abstractions;
using AspireRunner.Core.Models;

namespace AspireRunner.Tool.ProcessManager;

public sealed class ManagedProcessEntry
{
    public required string Id { get; init; }

    public required ProcessProfile Profile { get; init; }

    public required IManagedProcess Process { get; init; }

    public string? Command { get; set; }

    public string? Details { get; set; }

    public string? Executable { get; set; }

    public string? Arguments { get; set; }

    public string? EnvironmentVariables { get; set; }

    public string? WorkingDirectory { get; set; }

    public int? LastKnownPid { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}