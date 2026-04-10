using AspireRunner.Core.Abstractions;
using AspireRunner.Core.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics;
using System.Threading;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AspireRunner.Tool.ProcessManager;

public sealed class InMemoryProcessManager
{
    private readonly ConcurrentDictionary<string, ManagedProcessEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId;

    public static InMemoryProcessManager Instance { get; } = new();

    private InMemoryProcessManager() { }

    public ManagedProcessEntry Register(
        IManagedProcess process,
        ProcessProfile profile,
        string? command = null,
        string? details = null,
        string? preferredId = null,
        string? executable = null,
        string? arguments = null,
        string? environmentVariables = null,
        string? workingDirectory = null,
        IReadOnlyList<int>? exposedPorts = null)
    {
        ArgumentNullException.ThrowIfNull(process);

        var id = ResolveId(preferredId);
        var now = DateTimeOffset.UtcNow;

        var entry = new ManagedProcessEntry
        {
            Id = id,
            Profile = profile,
            Process = process,
            Command = command,
            Details = details,
            Executable = executable,
            Arguments = arguments,
            EnvironmentVariables = environmentVariables,
            WorkingDirectory = workingDirectory,
            ExposedPorts = NormalizePorts(exposedPorts),
            LastKnownPid = process.Pid,
            CreatedAt = now,
            LastUpdatedAt = now
        };

        _entries[id] = entry;
        return entry;
    }

    public IReadOnlyList<ManagedProcessEntry> List()
    {
        var entries = _entries.Values
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var entry in entries)
        {
            UpdateProcessState(entry);
        }

        return entries;
    }

    public bool TryGet(string id, out ManagedProcessEntry? entry)
    {
        return _entries.TryGetValue(id.Trim(), out entry);
    }

    public bool TryGetByPid(int processId, out ManagedProcessEntry? entry)
    {
        entry = _entries.Values.FirstOrDefault(e =>
            e.Process.Pid == processId ||
            e.LastKnownPid == processId);

        if (entry is null)
        {
            return false;
        }

        UpdateProcessState(entry);
        return true;
    }

    public bool IsAlreadyExist(string executable, string arguments)
    {
        return _entries.Values.Any(e =>
            e.Process.IsRunning &&
            string.Equals(e.Executable, executable, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Arguments ?? string.Empty, arguments, StringComparison.Ordinal));
    }

    public void UpdateMetadata(
        string id,
        string? command = null,
        string? details = null,
        string? executable = null,
        string? arguments = null,
        string? environmentVariables = null,
        string? workingDirectory = null,
        IReadOnlyList<int>? exposedPorts = null)
    {
        if (!TryGet(id, out var entry) || entry is null)
        {
            return;
        }

        if (command is not null)
        {
            entry.Command = command;
        }

        if (details is not null)
        {
            entry.Details = details;
        }

        if (executable is not null)
        {
            entry.Executable = executable;
        }

        if (arguments is not null)
        {
            entry.Arguments = arguments;
        }

        if (environmentVariables is not null)
        {
            entry.EnvironmentVariables = environmentVariables;
        }

        if (workingDirectory is not null)
        {
            entry.WorkingDirectory = workingDirectory;
        }

        if (exposedPorts is not null)
        {
            entry.ExposedPorts = NormalizePorts(exposedPorts);
        }

        UpdateProcessState(entry);
        entry.LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task<bool> StopAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!TryGet(id, out var entry) || entry is null)
        {
            return false;
        }

        var pid = entry.Process.Pid ?? entry.LastKnownPid;
        var ports = entry.ExposedPorts;

        if (!entry.Process.IsRunning)
        {
            await EnsurePortsReleasedAsync(ports, pid, cancellationToken).ConfigureAwait(false);
            UpdateProcessState(entry);
            entry.LastUpdatedAt = DateTimeOffset.UtcNow;
            return true;
        }

        try
        {
            await entry.Process.StopAsync(cancellationToken);
        }
        catch
        {
            TryKillByPid(pid);
        }

        if (entry.Process.IsRunning)
        {
            TryKillByPid(pid);
        }

        await WaitForProcessExitAsync(pid, cancellationToken).ConfigureAwait(false);
        await EnsurePortsReleasedAsync(ports, pid, cancellationToken).ConfigureAwait(false);

        UpdateProcessState(entry);
        entry.LastUpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    public async Task<bool> RestartAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!TryGet(id, out var entry) || entry is null)
        {
            return false;
        }

        if (entry.Process.IsRunning)
        {
            await StopAsync(id, cancellationToken).ConfigureAwait(false);
        }

        await entry.Process.StartAsync(cancellationToken);
        UpdateProcessState(entry);
        entry.LastUpdatedAt = DateTimeOffset.UtcNow;
        return entry.Process.IsRunning;
    }

    public async Task<bool> RemoveAsync(string id, bool stopIfRunning = true, CancellationToken cancellationToken = default)
    {
        if (!TryGet(id, out var entry) || entry is null)
        {
            return false;
        }

        if (stopIfRunning && entry.Process.IsRunning)
        {
            await StopAsync(id, cancellationToken);
        }

        UpdateProcessState(entry);

        return _entries.TryRemove(id, out _);
    }

    public async Task<int> StopAllAsync(CancellationToken cancellationToken = default)
    {
        var entries = _entries.Values.ToArray();
        var stoppedCount = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (entry.Process.IsRunning)
                {
                    var pid = entry.Process.Pid ?? entry.LastKnownPid;
                    var ports = entry.ExposedPorts;

                    try
                    {
                        await entry.Process.StopAsync(cancellationToken);
                    }
                    catch
                    {
                        TryKillByPid(pid);
                    }

                    if (entry.Process.IsRunning)
                    {
                        TryKillByPid(pid);
                    }

                    await WaitForProcessExitAsync(pid, cancellationToken).ConfigureAwait(false);
                    await EnsurePortsReleasedAsync(ports, pid, cancellationToken).ConfigureAwait(false);

                    stoppedCount++;
                }
            }
            catch
            {
                // Best-effort shutdown for all processes; continue on failures.
            }
            finally
            {
                UpdateProcessState(entry);
                entry.LastUpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        return stoppedCount;
    }

    public async Task<int> RemoveAllAsync(bool stopIfRunning = true, CancellationToken cancellationToken = default)
    {
        var entries = _entries.Values.ToArray();
        var removedCount = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await RemoveAsync(entry.Id, stopIfRunning, cancellationToken))
            {
                removedCount++;
            }
        }

        return removedCount;
    }

    private static void UpdateProcessState(ManagedProcessEntry entry)
    {
        if (entry.Process.Pid is int pid)
        {
            entry.LastKnownPid = pid;
        }
    }

    private string ResolveId(string? preferredId)
    {
        var trimmedPreferredId = preferredId?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedPreferredId) && !_entries.ContainsKey(trimmedPreferredId))
        {
            return trimmedPreferredId;
        }

        string generated;
        do
        {
            generated = $"p{Interlocked.Increment(ref _nextId).ToString("D3", CultureInfo.InvariantCulture)}";
        } while (_entries.ContainsKey(generated));

        return generated;
    }

    private static void TryKillByPid(int? pid)
    {
        if (pid is null)
        {
            return;
        }

        try
        {
            var process = Process.GetProcessById(pid.Value);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch
        {
            // Best-effort fallback when graceful stop does not terminate the process.
        }
    }

    private static IReadOnlyList<int> NormalizePorts(IReadOnlyList<int>? ports)
    {
        if (ports is null || ports.Count == 0)
        {
            return [];
        }

        return [.. ports
            .Where(p => p is > ushort.MinValue and <= ushort.MaxValue)
            .Distinct()
            .OrderBy(p => p)];
    }

    private static async Task WaitForProcessExitAsync(int? pid, CancellationToken cancellationToken)
    {
        if (pid is null)
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var process = Process.GetProcessById(pid.Value);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsurePortsReleasedAsync(IReadOnlyList<int> ports, int? primaryPid, CancellationToken cancellationToken)
    {
        if (ports.Count == 0)
        {
            return;
        }

        if (await WaitForPortsReleasedAsync(ports, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // Some launch flows can leave a detached child process that still owns the bound port.
        // Force-kill the known process and any current listener owners for managed ports.
        TryKillByPid(primaryPid);

        foreach (var ownerPid in GetListeningOwnerPidsByPorts(ports))
        {
            if (ownerPid == Environment.ProcessId)
            {
                continue;
            }

            TryKillByPid(ownerPid);
        }

        await WaitForPortsReleasedAsync(ports, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForPortsReleasedAsync(IReadOnlyList<int> ports, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (ports.Count == 0)
        {
            return true;
        }

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            var allReleased = ports.All(port => listeners.All(listener => listener.Port != port));
            if (allReleased)
            {
                return true;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static IReadOnlyList<int> GetListeningOwnerPidsByPorts(IReadOnlyList<int> ports)
    {
        if (ports.Count == 0 || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return [];
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano -p tcp",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return [];
            }

            var expectedPorts = new HashSet<int>(ports);
            var pids = new HashSet<int>();
            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = Regex.Split(line, "\\s+");
                if (parts.Length < 5)
                {
                    continue;
                }

                var localEndpoint = parts[1];
                var state = parts[3];
                var pidText = parts[4];

                if (!state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var separatorIndex = localEndpoint.LastIndexOf(':');
                if (separatorIndex < 0)
                {
                    continue;
                }

                if (!int.TryParse(localEndpoint[(separatorIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var localPort))
                {
                    continue;
                }

                if (!expectedPorts.Contains(localPort))
                {
                    continue;
                }

                if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
                {
                    continue;
                }

                pids.Add(pid);
            }

            return [.. pids];
        }
        catch
        {
            return [];
        }
    }
}