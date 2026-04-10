namespace ProcessManagerRunner.Example.WebApi;

public sealed class NodeAppOptions
{
    public const string SectionName = "NodeApp";

    public string Exe { get; set; } = "node";

    public string Args { get; set; } = "index.js";

    public string WorkingDir { get; set; } = "";

    public string Name { get; set; } = "Node.js app";
}
