namespace ProcessManager.Core.Tray;

public interface ILogWindow
{
    bool IsDisposed { get; }
    event EventHandler? Closed;
    void Show();
    void BringToFront();
    void Close();
}
