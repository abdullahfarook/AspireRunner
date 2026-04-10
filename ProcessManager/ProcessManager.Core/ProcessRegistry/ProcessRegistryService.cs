using System.Diagnostics;
using ProcessManager.Core.Errors;

namespace ProcessManager.Core.ProcessRegistry;

public sealed class ProcessRegistryService
{
    private readonly object _lock = new();
    private int _nextPort = 1;
    private readonly List<ManagedProcess> _processes = [];
    private readonly Dictionary<int, ManagedProcess> _byPid = [];

    public int ServerPort { get; set; }

    public event EventHandler? ProcessListChanged;

    public (int pid, int port) Register(string exe, string args, string envs, string workingDir, string? name = null)
    {
        lock (_lock)
        {
            int port = _nextPort++;
            ManagedProcess managed = new(exe, args, envs, workingDir, port, name);

            try
            {
                var pid = managed.Start();
                if (pid is null)
                {
                    managed.Dispose();
                    throw new IpcError(0, "Failed to start process", null);
                }

                _processes.Add(managed);
                _byPid[pid.Value] = managed;

                ProcessListChanged?.Invoke(this, EventArgs.Empty);
                return (pid.Value, port);
            }
            catch (Exception e)
            {
                _nextPort--;
                managed.Dispose();
                throw new IpcError(0, e.Message, e);
            }
        }
    }

    public (int pid, int? port) RegisterExisting(int pid,string exe, string args, string envs, string workingDir,
        string? name = null)
    {
        lock (_lock)
        {
            if (_byPid.TryGetValue(pid, out ManagedProcess? managed))
            {
                return (pid, managed.AssignedPort);
            }
            managed = new ManagedProcess(exe, args, envs, workingDir, null, name);
            var newPid = managed.AttachExisting(pid);
            if (newPid is null)
            {
                managed.Dispose();
                throw new IpcError(0, "Failed to attach to existing process", null);
            }

            if (newPid != pid)
            {
                pid = newPid.Value;
            }
            _processes.Add(managed);
            _byPid[pid] = managed;
            ProcessListChanged?.Invoke(this, EventArgs.Empty);
            return (pid, managed.AssignedPort);
        }
    }

    public bool Stop(int pid)
    {
        lock (_lock)
        {
            if (!_byPid.TryGetValue(pid, out ManagedProcess? managed))
                return false;

            managed.Stop();
            ProcessListChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
    }

    public bool Restart(int pid)
    {
        lock (_lock)
        {
            if (!_byPid.TryGetValue(pid, out ManagedProcess? managed))
                return false;

            var oldPid = pid;
            var newPid = managed.Restart();

            if (newPid is not null && newPid != oldPid)
            {
                _byPid.Remove(oldPid);
                _byPid[newPid.Value] = managed;
            }

            return true;
        }
    }

    public void WriteStdin(int pid, string? input)
    {
        lock (_lock)
        {
            if (_byPid.TryGetValue(pid, out ManagedProcess? managed))
                managed.WriteStdin(input);
        }
    }

    public bool IsAlreadyExist(string exe, string args)
    {
        lock (_lock)
        {
            return _processes.Exists(p =>
                string.Equals(p.Exe, exe, StringComparison.OrdinalIgnoreCase) &&
                p.Args == args &&
                p.IsRunning);
        }
    }

    public ManagedProcess? Get(int pid)
    {
        lock (_lock)
        {
            return _byPid.TryGetValue(pid, out ManagedProcess? p) ? p : null;
        }
    }

    public IReadOnlyList<ManagedProcess> List()
    {
        lock (_lock)
        {
            return _processes.ToList();
        }
    }

    public bool Update(int pid, string exe, string args, string envs, string workingDir, string? name = null)
    {
        lock (_lock)
        {
            if (!_byPid.TryGetValue(pid, out ManagedProcess? existing))
                return false;

            int? port = existing.AssignedPort;

            existing.Dispose();
            _processes.Remove(existing);
            _byPid.Remove(pid);

            ManagedProcess managed = new(exe, args, envs, workingDir, port, name);
            var newPid = managed.Start();

            if (newPid is null)
            {
                managed.Dispose();
                return false;
            }

            _processes.Add(managed);
            _byPid[newPid.Value] = managed;

            ProcessListChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
    }

    public void Remove(int pid)
    {
        lock (_lock)
        {
            if (!_byPid.TryGetValue(pid, out ManagedProcess? managed))
                return;

            managed.Dispose();
            _processes.Remove(managed);
            _byPid.Remove(pid);
            ProcessListChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}