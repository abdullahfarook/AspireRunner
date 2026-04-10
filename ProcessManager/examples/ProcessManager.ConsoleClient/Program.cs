using ProcessManager.Client;

const int port = ProcessManagerClient.DefaultPort;
ProcessManagerClient client = new(port);
bool wasAlreadyRunning = client.IsManagerRunning();

if (wasAlreadyRunning)
{
    Console.WriteLine("Process Manager already running.");
}
else
{
    Console.WriteLine("Starting Process Manager...");
    var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
    var host = Path.Combine(baseDir.Parent!.Parent!.Parent!.Parent!.Parent!.Parent!.FullName,"ProcessManager", "src", "ProcessManager.Host");
    
    client.StartManagerIfNeeded(host);
    int retries = 5;
    while (retries > 0 && !client.IsManagerRunning())
    {
        Thread.Sleep(500);
        retries--;
    }

    if (!client.IsManagerRunning())
    {
        Console.Error.WriteLine("Failed to start Process Manager.");
        return 1;
    }

    Console.WriteLine("Process Manager started.");
}

// Node.js app configuration
string nodeExe = "node";
string nodeArgs = "index.js";
string workingDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "node-app");
workingDir = Path.GetFullPath(workingDir);
if (!Directory.Exists(workingDir))
{
    workingDir = Directory.GetCurrentDirectory();
}

int? nodePid = null;
bool nodeLiveOnly = true;
IpcResponse? nodeExistsResponse = client.SendRequest(
    new IpcRequest(Command: "IsAlreadyExist", Name: "Index.js", Exe: nodeExe, Args: nodeArgs));
if (nodeExistsResponse is { Ok: true, Exists: true })
{
    ListResponse? listResponse = client.List();
    ProcessInfo? match = listResponse?.Processes?.FirstOrDefault(p =>
        string.Equals(p.Exe, nodeExe, StringComparison.OrdinalIgnoreCase) &&
        p.Args == nodeArgs &&
        p.Running);
    if (match is null)
    {
        Console.WriteLine("Node.js app was running but no longer found in Process Manager.");
        return 1;
    }

    nodePid = match.ProcessId;
    nodeLiveOnly = true;
    Console.WriteLine($"Re-attaching to existing Node.js app (Process ID: {nodePid}). Showing new logs only.");
}
else
{
    IpcResponse? registerResponse = client.SendRequest(
        new IpcRequest(Command: "Register", Name: "Node.js app", Exe: nodeExe, Args: nodeArgs, WorkingDir: workingDir));

    if (registerResponse is null or { Ok: false })
    {
        Console.Error.WriteLine("Failed to register Node.js app: " + (registerResponse?.Error ?? "No response"));
        return 1;
    }

    nodePid = registerResponse.ProcessId;
    Console.WriteLine($"Node.js app registered. Process ID: {nodePid}, Manager port: {registerResponse.Port}");
}

// Aspire dashboard configuration
string aspireExe = "aspire-runner";
string aspireArgs = "run";
string aspireWorkingDir = Directory.GetCurrentDirectory();

int? aspirePid = null;
bool aspireLiveOnly = true;
IpcResponse? aspireExistsResponse = client.SendRequest(
    new IpcRequest(Command: "IsAlreadyExist", Name: "Aspire dashboard", Exe: aspireExe, Args: aspireArgs));
if (aspireExistsResponse is { Ok: true, Exists: true })
{
    ListResponse? listResponse = client.List();
    ProcessInfo? match = listResponse?.Processes?.FirstOrDefault(p =>
        string.Equals(p.Exe, aspireExe, StringComparison.OrdinalIgnoreCase) &&
        p.Args == aspireArgs &&
        p.Running);
    if (match is null)
    {
        Console.WriteLine("Aspire dashboard was running but no longer found in Process Manager.");
    }
    else
    {
        aspirePid = match.ProcessId;
        aspireLiveOnly = true;
        Console.WriteLine($"Re-attaching to existing Aspire dashboard (Process ID: {aspirePid}). Showing new logs only.");
    }
}
else
{
    IpcResponse? registerResponse = client.SendRequest(
        new IpcRequest(Command: "Register", Name: "Aspire dashboard", Exe: aspireExe, Args: aspireArgs, WorkingDir: aspireWorkingDir));

    if (registerResponse is null or { Ok: false })
    {
        Console.Error.WriteLine("Failed to register Aspire dashboard: " + (registerResponse?.Error ?? "No response"));
    }
    else
    {
        aspirePid = registerResponse.ProcessId;
        Console.WriteLine($"Aspire dashboard registered. Process ID: {aspirePid}, Manager port: {registerResponse.Port}");
    }
}

Console.WriteLine("Streaming logs (Ctrl+C to detach; processes keep running in background)...");
Console.WriteLine();

if (nodePid is not int nodePidValue && aspirePid is not int aspirePidValue)
{
    return 0;
}

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Environment.Exit(0);
};

Thread? nodeStdoutThread = null;
Thread? nodeStderrThread = null;
Thread? aspireStdoutThread = null;
Thread? aspireStderrThread = null;

if (nodePid is int nodeId)
{
    nodeStdoutThread = new Thread(() =>
    {
        try
        {
            client.StreamStdout(nodeId, line => Console.WriteLine(line), nodeLiveOnly);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine("Node stdout stream ended: " + ex.Message);
        }
    });

    nodeStderrThread = new Thread(() =>
    {
        try
        {
            client.StreamStderr(nodeId, line =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(line);
                Console.ResetColor();
            }, nodeLiveOnly);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    });

    nodeStdoutThread.Start();
    nodeStderrThread.Start();
}

if (aspirePid is int aspireId)
{
    aspireStdoutThread = new Thread(() =>
    {
        try
        {
            client.StreamStdout(aspireId, line => Console.WriteLine(line), aspireLiveOnly);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine("Aspire dashboard stdout stream ended: " + ex.Message);
        }
    });

    aspireStderrThread = new Thread(() =>
    {
        try
        {
            client.StreamStderr(aspireId, line =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine(line);
                Console.ResetColor();
            }, aspireLiveOnly);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    });

    aspireStdoutThread.Start();
    aspireStderrThread.Start();
}

nodeStdoutThread?.Join();
nodeStderrThread?.Join();
aspireStdoutThread?.Join();
aspireStderrThread?.Join();

return 0;
