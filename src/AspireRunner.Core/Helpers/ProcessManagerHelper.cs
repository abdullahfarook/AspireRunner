using ProcessManager.Client;
using ProcessManager.Client.Core;
using System.Diagnostics;
using System.Text;

namespace AspireRunner.Core.Helpers;

public static class ProcessManagerHelper
{
    private static readonly ProcessManagerClient Client = new();

    public static async Task<(string Output, string Error)> GetAsync(
        string processName,
        string[] arguments,
        IDictionary<string, string?>? environment = null,
        string? workingDir = null)
    {
        EnsureManagerRunning();

        var args = string.Join(" ", arguments);
        var result = await Client.TryRegister(
            processName,
            args,
            environment.ToEnvString(),
            workingDir ?? string.Empty);

        if (result.IsFailure)
            return (string.Empty, result.Error);

        var pid = result.Value.ProcessId;

        var output = new StringBuilder();
        var error = new StringBuilder();

        await Task.WhenAll(
            Task.Run(() => Client.StreamStdout(pid, line => output.AppendLine(line))),
            Task.Run(() => Client.StreamStderr(pid, line => error.AppendLine(line)))
        );

        return (output.ToString(), error.ToString());
    }

    public static (string Output, string Error) Get(
        string processName,
        string[] arguments,
        IDictionary<string, string?>? environment = null,
        string? workingDir = null)
    {
        EnsureManagerRunning();

        var args = string.Join(" ", arguments);
        var result = Client.TryRegister(
            processName,
            args,
            environment.ToEnvString(),
            workingDir ?? string.Empty).GetAwaiter().GetResult();

        if (result.IsFailure)
            return (string.Empty, result.Error);

        var pid = result.Value.ProcessId;

        var output = new StringBuilder();
        var error = new StringBuilder();

        Task.WhenAll(
            Task.Run(() => Client.StreamStdout(pid, line => output.AppendLine(line))),
            Task.Run(() => Client.StreamStderr(pid, line => error.AppendLine(line)))
        ).Wait();

        return (output.ToString(), error.ToString());
    }

    public static Process? Run(
        string processName,
        string[] arguments,
        IDictionary<string, string?>? environment = null,
        string? workingDir = null,
        Action<string>? outputHandler = null,
        Action<string>? errorHandler = null,
        bool liveOnly = false,
        string? name = null)
    {
        EnsureManagerRunning();

        var args = string.Join(" ", arguments);
        var result = Client.TryRegister(
            processName,
            args,
            environment.ToEnvString(),
            workingDir ?? string.Empty,
            name).GetAwaiter().GetResult();

        if (result.IsFailure)
            return null;

        var pid = result.Value.ProcessId;

        if (outputHandler is not null)
            Task.Run(() => Client.StreamStdout(pid, outputHandler, liveOnly: liveOnly));

        if (errorHandler is not null)
            Task.Run(() => Client.StreamStderr(pid, errorHandler, liveOnly: liveOnly));

        return GetProcessOrDefault(pid);  // ← uses own method now
    }
    public static Process? Attach(
        int pid,
        string processName,
        string[] arguments,
        IDictionary<string, string?>? environment = null,
        string? workingDir = null,
        Action<string>? outputHandler = null,
        Action<string>? errorHandler = null,
        bool liveOnly = false,
        string? name = null)
    {
        EnsureManagerRunning();

        var args = string.Join(" ", arguments);
        var result = Client.TryRegister(
            processName,
            args,
            environment.ToEnvString(),
            workingDir ?? string.Empty,
            name,
            pid: pid).GetAwaiter().GetResult();

        if (result.IsFailure)
            return null;

        pid = result.Value.ProcessId;

        if (outputHandler is not null)
            Task.Run(() => Client.StreamStdout(pid, outputHandler, liveOnly: liveOnly));

        if (errorHandler is not null)
            Task.Run(() => Client.StreamStderr(pid, errorHandler, liveOnly: liveOnly));

        return GetProcessOrDefault(pid);  // ← uses own method now
    }

    // public static bool IsRunning(this Process? process)
    // {
    //     try
    //     {
    //         return process?.HasExited is false;
    //     }
    //     catch
    //     {
    //         return false;
    //     }
    // }

    public static Process? GetProcessOrDefault(int pid)
    {
        if (pid <= 0)
            return null;
        try
        {
            return Process.GetProcessById(pid);
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureManagerRunning()
    {
        if (!Client.IsManagerRunning())
            throw new InvalidOperationException("ProcessManager is not running.");
    }
}