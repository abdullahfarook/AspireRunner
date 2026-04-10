namespace ProcessManager.Client;

public class ProcessConfiguration
{
    public string Exe { get; set; } = null!;
    public string Args { get; set; } = null!;
    public string WorkingDir { get; set; } = "";
    public string Name { get; set; } = null!;
    public bool LiveOnly { get; set; } = true;
    public bool Stdout { get; set; } = true;
    public bool Stderr { get; set; } = true;
    public int BurstDelayOutput { get; set; }
}