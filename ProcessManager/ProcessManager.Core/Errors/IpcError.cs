namespace ProcessManager.Core.Errors;

public class IpcError(int processId, string message, Exception? exception = null) : Exception(message,exception)
{
    public int ProcessId { get; } = processId;
}