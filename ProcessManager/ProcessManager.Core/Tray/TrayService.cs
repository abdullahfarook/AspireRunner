using ProcessManager.Core.ProcessRegistry;

namespace ProcessManager.Core.Tray;

public sealed class TrayService : IDisposable
{
    private readonly ProcessRegistryService _registry;
    private readonly ITrayHost _trayHost;
    private readonly ITrayUI _trayUI;
    private readonly Action<int> _onShowLogs;
    private readonly Dictionary<int, ILogWindow> _logWindows = [];
    private IProcessesWindow? _processesWindow;
    private bool _disposed;

    public TrayService(
        ProcessRegistryService registry,
        ITrayHost trayHost,
        ITrayUI trayUI,
        Action<int> onShowLogs)
    {
        _registry = registry;
        _trayHost = trayHost;
        _trayUI = trayUI;
        _onShowLogs = onShowLogs;
    }

    public void Create(int serverPort)
    {
        try
        {
            _trayHost.Create(serverPort, new TrayCallbacks
            {
                OpenClicked = ShowProcessesWindow,
                ExitClicked = () => Environment.Exit(0),
                ProcessSelected = _onShowLogs,
            });
            _registry.ProcessListChanged += (_, _) =>
            {
                try
                {
                    if (_processesWindow is { IsDisposed: false, IsHandleCreated: true } w)
                    {
                        w.Invoke(() => w.RefreshList());
                    }
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            };
        }
        catch (Exception)
        {
            // Tray not available (e.g. headless or unsupported platform)
        }
    }

    public void UpdateMenu()
    {
        try
        {
            _trayHost.UpdateMenu(_registry.List());
        }
        catch (ObjectDisposedException) { }
    }

    public void ShowLogWindow(int processId)
    {
        ManagedProcess? managed = _registry.Get(processId);
        if (managed is null)
        {
            return;
        }

        if (_logWindows.TryGetValue(processId, out ILogWindow? existing) && !existing.IsDisposed)
        {
            existing.BringToFront();
            existing.Show();
            return;
        }

        if (_trayUI.InvokeRequired)
        {
            _trayUI.RunOnUIThread(() => ShowLogWindowCore(processId));
            return;
        }
        ShowLogWindowCore(processId);
    }

    private void ShowLogWindowCore(int processId)
    {
        ManagedProcess? managed = _registry.Get(processId);
        if (managed is null)
        {
            return;
        }

        if (_logWindows.TryGetValue(processId, out ILogWindow? existing) && !existing.IsDisposed)
        {
            existing.BringToFront();
            existing.Show();
            return;
        }

        ILogWindow logWindow = _trayUI.CreateLogWindow(processId, managed);
        logWindow.Closed += (_, _) => _logWindows.Remove(processId);
        _logWindows[processId] = logWindow;
        logWindow.Show();
    }

    public void ShowProcessesWindow()
    {
        if (_processesWindow is null || _processesWindow.IsDisposed)
        {
            _processesWindow = _trayUI.CreateProcessesWindow(_registry, _onShowLogs);
            _processesWindow.Closed += (_, _) => _processesWindow = null;
        }
        _processesWindow.Show();
        _processesWindow.BringToFront();
        _processesWindow.RefreshList();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _processesWindow?.Close();
        _processesWindow = null;
        foreach (ILogWindow w in _logWindows.Values)
        {
            try
            {
                w.Close();
            }
            catch { }
        }
        _logWindows.Clear();
        _trayHost.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
