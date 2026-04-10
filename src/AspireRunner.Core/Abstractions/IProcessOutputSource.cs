namespace AspireRunner.Core.Abstractions;

public interface IProcessOutputSource
{
    IReadOnlyList<string> GetStdoutSnapshot();

    IReadOnlyList<string> GetStderrSnapshot();

    IDisposable SubscribeStdout(Action<string> onLine);

    IDisposable SubscribeStderr(Action<string> onLine);
}