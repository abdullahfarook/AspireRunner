namespace ProcessManager.Client;

public interface IProcessOptions
{
    public ProcessConfiguration Configuration { get; }
}
public class ProcessOptions(ProcessConfiguration config):IProcessOptions
{
    public ProcessConfiguration Configuration { get; } = config;
}