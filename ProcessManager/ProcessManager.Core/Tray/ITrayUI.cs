using ProcessManager.Core.ProcessRegistry;

namespace ProcessManager.Core.Tray;

public interface ITrayUI
{
    IProcessesWindow CreateProcessesWindow(ProcessRegistryService registry, Action<int> onShowLogs);
    ILogWindow CreateLogWindow(int processId, ManagedProcess managed);
    void RunOnUIThread(Action action);
    bool InvokeRequired { get; }
}
