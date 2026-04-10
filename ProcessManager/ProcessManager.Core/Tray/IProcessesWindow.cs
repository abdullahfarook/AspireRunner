namespace ProcessManager.Core.Tray;

public interface IProcessesWindow
{
    bool IsDisposed { get; }
    bool IsHandleCreated { get; }
    event EventHandler? Closed;
    void RefreshList();
    void Show();
    void BringToFront();
    void Close();
    void Invoke(Action action);
}
