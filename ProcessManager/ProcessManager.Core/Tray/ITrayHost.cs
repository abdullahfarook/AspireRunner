using ProcessManager.Core.ProcessRegistry;

namespace ProcessManager.Core.Tray;

public interface ITrayHost : IDisposable
{
    void Create(int serverPort, TrayCallbacks callbacks);
    void UpdateMenu(IReadOnlyList<ManagedProcess> processes);
}

public sealed class TrayCallbacks
{
    public required Action OpenClicked { get; init; }
    public required Action ExitClicked { get; init; }
    public required Action<int> ProcessSelected { get; init; }
}
