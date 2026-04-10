using AspireRunner.Core.Abstractions;
using AspireRunner.Core.Models;
using AspireRunner.Tool.ProcessManager;
using AspireRunner.Tool.ProcessManager.Lpc;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace AspireRunner.Tool.Commands;

public class ProcessRunCommand : AsyncCommand<ProcessRunCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<exe>")]
        [Description("Path or command name of the executable to run")]
        public required string Executable { get; set; }

        [CommandOption("--args")]
        [Description("Arguments string passed to the executable (use --args=\"...\" when values start with '-')")]
        public string? Arguments { get; set; }

        [CommandOption("--name")]
        [Description("Display name for the managed process")]
        public string? Name { get; set; }

        [CommandOption("--id")]
        [Description("Process id in the session inventory")]
        public string? ProcessId { get; set; }

        [CommandOption("--working-dir")]
        [Description("Working directory for the process")]
        public string? WorkingDirectory { get; set; }

        [CommandOption("--env")]
        [Description("Environment variable in KEY=VALUE format. Accepts semicolon-separated entries and can be passed multiple times")]
        public string[] EnvironmentEntries { get; set; } = [];

        [CommandOption("--envs")]
        [Description("Environment variables in KEY=VALUE;KEY2=VALUE2 format")]
        public string? EnvironmentVariables { get; set; }

        [CommandOption("--port")]
        [Description("Exposed port for the process. Pass multiple times for multiple ports")]
        public int[] ExposedPorts { get; set; } = [];

        [CommandOption("--detach")]
        [Description("Start the process and return immediately")]
        public bool Detach { get; set; }

        [CommandOption("--restart-on-failure")]
        [Description("Automatically restart the process when it exits unexpectedly")]
        public bool RestartOnFailure { get; set; }

        [DefaultValue(2)]
        [CommandOption("--restart-delay")]
        [Description("Delay in seconds before restarting after an unexpected exit")]
        public int RestartDelaySeconds { get; set; }

        [DefaultValue(true)]
        [CommandOption("--pipe-output")]
        [Description("Write process output directly to the terminal")]
        public bool PipeOutput { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        _ = context;

        Widgets.Write([Widgets.Header(), Widgets.RunnerVersion]);
        Widgets.WriteLines(2);

        var parseResult = ParseEnvironmentVariables(settings.EnvironmentEntries, settings.EnvironmentVariables);
        if (parseResult.InvalidEntries.Length > 0)
        {
            Widgets.Write(Widgets.Error($"Invalid environment entries (expected KEY=VALUE): {string.Join(", ", parseResult.InvalidEntries)}"));
            return 2;
        }

        var displayName = string.IsNullOrWhiteSpace(settings.Name)
            ? Path.GetFileNameWithoutExtension(settings.Executable)
            : settings.Name;

        var processOptions = new ExecutableProcessOptions
        {
            ExecutablePath = settings.Executable,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Managed Process" : displayName,
            Arguments = ParseArguments(settings.Arguments),
            WorkingDirectory = string.IsNullOrWhiteSpace(settings.WorkingDirectory) ? null : settings.WorkingDirectory,
            EnvironmentVariables = parseResult.EnvironmentVariables,
            PipeOutput = settings.PipeOutput,
            RestartOnFailure = settings.RestartOnFailure,
            RestartDelaySeconds = Math.Max(settings.RestartDelaySeconds, 1)
        };

        IProcessFactory processFactory = new ProcessFactory(
            Logger.DefaultFactory.CreateLogger<ProcessFactory>(),
            Logger.DefaultFactory);
        using var lpcServer = StartLpcServer(processFactory);
        if (lpcServer is not null)
        {
            Widgets.WriteInterpolated($"LPC endpoint [{Widgets.PrimaryColorText}]127.0.0.1:{lpcServer.Port}[/] is available for ProcessManager.Client", true);
        }

        var process = await processFactory.CreateProcessAsync(ProcessCreationRequest.Executable(processOptions));
        if (process is null)
        {
            Widgets.Write(Widgets.Error("Failed to create process manager instance"));
            return -1;
        }

        await process.StartAsync(cancellationToken);
        if (!process.IsRunning)
        {
            Widgets.Write(Widgets.Error($"Failed to start process '{process.DisplayName}'"));
            return -1;
        }

        var processCommand = BuildCommandPreview(settings.Executable, processOptions.Arguments);
        var resolvedPorts = ResolveExposedPorts(processOptions.Arguments, settings.ExposedPorts);
        var processEntry = InMemoryProcessManager.Instance.Register(
            process,
            ProcessProfile.ExecutableProcess,
            command: processCommand,
            details: BuildProcessDetails(process, processOptions, resolvedPorts),
            preferredId: settings.ProcessId,
            executable: settings.Executable,
            arguments: settings.Arguments ?? string.Empty,
            environmentVariables: BuildEnvironmentString(parseResult.EnvironmentVariables),
            workingDirectory: processOptions.WorkingDirectory,
            exposedPorts: resolvedPorts);

        Widgets.WriteInterpolated($"Started process [{Widgets.PrimaryColorText}]{process.DisplayName}[/] with PID [{Widgets.PrimaryColorText}]{process.Pid}[/] (id: [{Widgets.PrimaryColorText}]{processEntry.Id}[/])", true);

        if (settings.Detach)
        {
            Widgets.WriteInterpolated($"Process is running in detached mode", true);
            return 0;
        }

        Widgets.Write("Press Ctrl+C to stop the process", true);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            process.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            linkedCts.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Stopped by cancellation (Ctrl+C or host cancellation).
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            InMemoryProcessManager.Instance.UpdateMetadata(processEntry.Id, processCommand, BuildProcessDetails(process, processOptions, resolvedPorts), exposedPorts: resolvedPorts);
        }

        return 0;
    }

    private static string BuildProcessDetails(IManagedProcess process, ExecutableProcessOptions options, IReadOnlyList<int> ports)
    {
        var details = new List<string>();
        if (ports.Count > 0)
        {
            details.Add($"port={string.Join('/', ports)}");
        }

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            details.Add($"cwd={options.WorkingDirectory}");
        }

        if (options.Arguments.Count > 0)
        {
            details.Add($"args={string.Join(' ', options.Arguments)}");
        }

        details.Add(process.IsRunning ? "running" : "stopped");
        return string.Join(", ", details);
    }

    private static IReadOnlyList<int> ResolveExposedPorts(IReadOnlyList<string> arguments, IReadOnlyList<int> explicitPorts)
    {
        var ports = new HashSet<int>();

        foreach (var port in explicitPorts)
        {
            if (port is > ushort.MinValue and <= ushort.MaxValue)
            {
                ports.Add(port);
            }
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if ((arg.Equals("--port", StringComparison.OrdinalIgnoreCase) || arg.Equals("-p", StringComparison.OrdinalIgnoreCase)) && i + 1 < arguments.Count)
            {
                if (int.TryParse(arguments[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort)
                    && parsedPort is > ushort.MinValue and <= ushort.MaxValue)
                {
                    ports.Add(parsedPort);
                }

                continue;
            }

            if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg[7..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inlinePort)
                && inlinePort is > ushort.MinValue and <= ushort.MaxValue)
            {
                ports.Add(inlinePort);
            }
        }

        return [.. ports.OrderBy(p => p)];
    }

    private static string BuildCommandPreview(string executable, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return executable;
        }

        return $"{executable} {string.Join(' ', arguments)}";
    }

    private static string? BuildEnvironmentString(IReadOnlyDictionary<string, string?> environmentVariables)
    {
        if (environmentVariables.Count == 0)
        {
            return null;
        }

        return string.Join(";", environmentVariables.Select(e => $"{e.Key}={e.Value}"));
    }

    private static LpcServer? StartLpcServer(IProcessFactory processFactory)
    {
        var server = new LpcServer(
            InMemoryProcessManager.Instance,
            processFactory,
            Logger.DefaultFactory.CreateLogger<LpcServer>(),
            LpcServer.DefaultPort);

        if (server.Start())
        {
            return server;
        }

        server.Dispose();
        return null;
    }

    private static string[] ParseArguments(string? rawArguments)
    {
        if (string.IsNullOrWhiteSpace(rawArguments))
        {
            return [];
        }

        var inQuotes = false;
        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var character in rawArguments.Trim())
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return [..tokens];
    }

    private static (IReadOnlyDictionary<string, string?> EnvironmentVariables, string[] InvalidEntries) ParseEnvironmentVariables(
        IEnumerable<string> entries,
        string? semicolonEntries)
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var invalidEntries = new List<string>();

        ParseEnvironmentSource(semicolonEntries, variables, invalidEntries);

        foreach (var entry in entries)
        {
            ParseEnvironmentSource(entry, variables, invalidEntries);
        }

        return (variables, [..invalidEntries]);
    }

    private static void ParseEnvironmentSource(
        string? raw,
        IDictionary<string, string?> variables,
        ICollection<string> invalidEntries)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = entry.IndexOf('=');
            if (separatorIndex <= 0)
            {
                invalidEntries.Add(entry);
                continue;
            }

            var key = entry[..separatorIndex].Trim();
            var value = entry[(separatorIndex + 1)..];
            if (string.IsNullOrWhiteSpace(key))
            {
                invalidEntries.Add(entry);
                continue;
            }

            variables[key] = value;
        }
    }
}
