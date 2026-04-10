namespace AspireRunner.Core.Abstractions;

public interface IManagedProcess
{
    string DisplayName { get; }

    bool IsRunning { get; }

    int? Pid { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task WaitForExitAsync(CancellationToken cancellationToken = default);
}