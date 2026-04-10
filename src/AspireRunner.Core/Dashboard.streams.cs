using System.Collections.Concurrent;
using System.Threading;

namespace AspireRunner.Core;

public partial class Dashboard
{
    private const int MaxBufferedOutputLines = 2000;

    private readonly ConcurrentQueue<string> _stdoutLines = [];
    private readonly ConcurrentQueue<string> _stderrLines = [];
    private readonly object _stdoutSubscribersSync = new();
    private readonly object _stderrSubscribersSync = new();
    private readonly List<Action<string>> _stdoutSubscribers = [];
    private readonly List<Action<string>> _stderrSubscribers = [];

    public IReadOnlyList<string> GetStdoutSnapshot() => _stdoutLines.ToArray();

    public IReadOnlyList<string> GetStderrSnapshot() => _stderrLines.ToArray();

    public IDisposable SubscribeStdout(Action<string> onLine)
    {
        ArgumentNullException.ThrowIfNull(onLine);

        lock (_stdoutSubscribersSync)
        {
            _stdoutSubscribers.Add(onLine);
        }

        return new Subscription(() =>
        {
            lock (_stdoutSubscribersSync)
            {
                _stdoutSubscribers.Remove(onLine);
            }
        });
    }

    public IDisposable SubscribeStderr(Action<string> onLine)
    {
        ArgumentNullException.ThrowIfNull(onLine);

        lock (_stderrSubscribersSync)
        {
            _stderrSubscribers.Add(onLine);
        }

        return new Subscription(() =>
        {
            lock (_stderrSubscribersSync)
            {
                _stderrSubscribers.Remove(onLine);
            }
        });
    }

    private void CaptureStdoutLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        AddLine(_stdoutLines, line);
        PublishLine(_stdoutSubscribersSync, _stdoutSubscribers, line);
    }

    private void CaptureStderrLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        AddLine(_stderrLines, line);
        PublishLine(_stderrSubscribersSync, _stderrSubscribers, line);
    }

    private static void AddLine(ConcurrentQueue<string> lines, string line)
    {
        lines.Enqueue(line);
        while (lines.Count > MaxBufferedOutputLines && lines.TryDequeue(out _))
        {
            // Keep only the newest bounded snapshot of lines.
        }
    }

    private static void PublishLine(object sync, List<Action<string>> subscribers, string line)
    {
        Action<string>[] listeners;
        lock (sync)
        {
            listeners = [..subscribers];
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener(line);
            }
            catch
            {
                // Ignore subscriber exceptions to keep stream forwarding resilient.
            }
        }
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        private readonly Action _onDispose = onDispose;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _onDispose();
        }
    }
}