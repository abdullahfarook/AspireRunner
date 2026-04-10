using Microsoft.Extensions.Options;
using ProcessManager.Client;

namespace ProcessManagerRunner.Example.WebApi;

public sealed class NodeAppHostedService(
    IOptions<AspNetCore.ProcessManagerConfiguration> runnerOptions,
    IOptions<NodeAppOptions> nodeOptions,
    ILogger<NodeAppHostedService> logger) : IHostedService
{
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private CancellationTokenSource? _cts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                int port = runnerOptions.Value.Port;
                ProcessManagerClient client = new(port);
                await WaitForManagerAsync(client, cancellationToken);

                NodeAppOptions node = nodeOptions.Value;
                string workingDir = ResolveWorkingDir(node.WorkingDir);

                int? pid = await GetOrRegisterNodeAppAsync(client, node, workingDir);
                if (pid is not int pidValue)
                {
                    logger.LogWarning("Could not get or register Node app.");
                    return;
                }

                logger.LogInformation("Node app process ID: {Pid}. Streaming stdout/stderr to application logs.", pidValue);

                bool liveOnly = true;
                _stdoutTask = Task.Run(() =>
                {
                    try
                    {
                        client.StreamStdout(
                            pidValue,
                            line => logger.LogInformation("[node stdout] {Line}", line),
                            liveOnly);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Stdout stream ended.");
                    }
                }, _cts.Token);

                _stderrTask = Task.Run(() =>
                {
                    try
                    {
                        client.StreamStderr(
                            pidValue,
                            line => logger.LogError("[node stderr] {Line}", line),
                            liveOnly);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Stderr stream ended.");
                    }
                }, _cts.Token);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "NodeAppHostedService failed.");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _cts?.Cancel();
            if (_stdoutTask is not null)
            {
                await Task.WhenAny(_stdoutTask, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
            }

            if (_stderrTask is not null)
            {
                await Task.WhenAny(_stderrTask, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
            }
        }
        finally
        {
            _cts?.Dispose();
        }
    }

    private async Task WaitForManagerAsync(ProcessManagerClient client, CancellationToken cancellationToken)
    {
        const int maxRetries = 30;
        const int delayMs = 500;

        for (int i = 0; i < maxRetries; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (client.IsManagerRunning())
            {
                return;
            }

            await Task.Delay(delayMs, cancellationToken);
        }

        throw new InvalidOperationException($"Process Manager did not become available on port {client.Port} within {maxRetries * delayMs / 1000}s.");
    }

    private static string ResolveWorkingDir(string workingDir)
    {
        if (string.IsNullOrWhiteSpace(workingDir))
        {
            string baseDir = AppContext.BaseDirectory;
            string candidate = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "node-app"));
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            return Directory.GetCurrentDirectory();
        }

        string resolved = Path.GetFullPath(workingDir);
        return Directory.Exists(resolved) ? resolved : Directory.GetCurrentDirectory();
    }

    private async Task<int?> GetOrRegisterNodeAppAsync(ProcessManagerClient client, NodeAppOptions node, string workingDir)
    {
        var result = await client.TryRegister(node.Exe, node.Args, workingDir, node.Name);
        if (result.IsFailure)
        {
            logger.LogError(result.Error, "Failed to register Node app: {Error}", result.Error);
            return null;
        }
        var info = result.Value;
        logger.LogInformation(info.Message);
        return info.ProcessId;
    }
}
