namespace AspireRunner.Core.Models;

public sealed record ExecutableProcessOptions
{
    public required string ExecutablePath { get; init; }

    public string DisplayName { get; init; } = "Managed Process";

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    public bool PipeOutput { get; init; } = true;

    public bool RestartOnFailure { get; init; }

    public int RestartDelaySeconds { get; init; } = 2;
}