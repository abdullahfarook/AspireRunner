using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Text;

namespace ProcessManager.Core.ProcessRegistry;

public sealed class ManagedProcess : IDisposable
{
    private const int MaxBufferedLines = 2000;
    private readonly ConcurrentQueue<string> _stdoutLines = [];
    private readonly ConcurrentQueue<string> _stderrLines = [];
    private readonly object _processLock = new();
    private Process? _process;
    private bool _disposed;

    public int ProcessId { get; private set; }
    public string? Name { get; }
    public string Exe { get; }
    public string Args { get; }
    public Dictionary<string, string>? Envs { get; }
    public string WorkingDir { get; }
    public int? AssignedPort { get; }
    public event Action<ManagedProcess, string, bool>? LineReceived;

    public ManagedProcess(string exe, string args, string envs, string workingDir, int? assignedPort = null, string? name = null)
    {
        Exe = exe;
        Args = args;
        Envs = ToStringDictionary(envs);
        WorkingDir = workingDir;
        AssignedPort = assignedPort;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private Dictionary<string, string>? ToStringDictionary(string envs)
    {
        if(string.IsNullOrWhiteSpace(envs))
        {
            return null;
        }
        return envs.Split(';')
            .Select(s => s.Split('='))
            .Where(s => s.Length == 2)
            .ToDictionary(s => s[0], s => s[1]);
    }

    public bool IsRunning
    {
        get
        {
            lock (_processLock)
            {
                return _process is { HasExited: false };
            }
        }
    }
    
    public int? AttachExisting(int pid)
    {
        lock (_processLock)
        {
            _process = Process.GetProcessById(pid);
            if (_process is null)
            {
                throw new InvalidOperationException($"Process with PID {pid} not found");
            }
            var isExternal = IsExternalProcess(_process);
            if (isExternal)
            {
               return Restart();
            }
            return _process.Id;
        }
    }

    private static bool IsExternalProcess(Process process)
    {
        try
        {
            return process.StartInfo.FileName == string.Empty;
        }
        catch
        {
            return true;
        }
    }

    public int? Start()
    {
        lock (_processLock)
        {
            if (_process is { HasExited: false })
            {
                return null;
            }

            ProcessStartInfo startInfo = new()
            {
                FileName = Exe,
                Arguments = Args,
                WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDir) ? Environment.CurrentDirectory : WorkingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (Envs is not null)
            {
                foreach (var env in Envs)
                {
                    startInfo.Environment[env.Key] = env.Value;
                }
            }

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnOutputDataReceived;
            _process.ErrorDataReceived += OnErrorDataReceived;
            _process.Exited += (_, _) => LineReceived?.Invoke(this, $"[Process exited with code {_process.ExitCode}]", false);
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            ProcessId = _process.Id;
            return _process.Id;
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data))
        {
            return;
        }

        EnqueueLine(_stdoutLines, e.Data);
        LineReceived?.Invoke(this, e.Data, true);
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data))
        {
            return;
        }

        EnqueueLine(_stderrLines, e.Data);
        LineReceived?.Invoke(this, e.Data, false);
    }

    private static void EnqueueLine(ConcurrentQueue<string> queue, string line)
    {
        queue.Enqueue(line);
        while (queue.Count > MaxBufferedLines && queue.TryDequeue(out _)) { }
    }

    public void Stop()
    {
        lock (_processLock)
        {
            if (_process is null)
            {
                return;
            }

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException) { }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }
    }

    public int? Restart()
    {
        Stop();
        return Start();
    }

    public void WriteStdin(string? input)
    {
        lock (_processLock)
        {
            if (_process is { HasExited: false } && input != null)
            {
                try
                {
                    _process.StandardInput.WriteLine(input);
                    _process.StandardInput.Flush();
                }
                catch (InvalidOperationException) { }
            }
        }
    }

    public IReadOnlyList<string> GetStdoutSnapshot()
    {
        return _stdoutLines.ToArray();
    }

    public IReadOnlyList<string> GetStderrSnapshot()
    {
        return _stderrLines.ToArray();
    }

    public void SubscribeLines(Action<string, bool> onLine)
    {
        LineReceived += (_, line, isStdout) => onLine(line, isStdout);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
