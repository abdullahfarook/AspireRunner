using AspireRunner.Core.Models;
using AspireRunner.Tool.ProcessManager;
using AspireRunner.Tool.ProcessManager.Lpc;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AspireRunner.Tool.Commands;

public class ProcessListCommand : AsyncCommand<ProcessListCommand.Settings>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public class Settings : CommandSettings
    {
        [CommandOption("--running-only")]
        [Description("Show only running managed processes")]
        public bool RunningOnly { get; set; }

        [DefaultValue(true)]
        [CommandOption("--auto-attach")]
        [Description("When local inventory is empty, query the running AspireRunner host via LPC")]
        public bool AutoAttach { get; set; } = true;

        [CommandOption("--lpc")]
        [Description("Query process list directly from the running AspireRunner host via LPC")]
        public bool UseLpc { get; set; }

        [DefaultValue(LpcServer.DefaultPort)]
        [CommandOption("--lpc-port")]
        [Description("LPC port to use with --lpc or automatic attach")]
        public int LpcPort { get; set; } = LpcServer.DefaultPort;

        [DefaultValue(false)]
        [CommandOption("--interactive")]
        [Description("Open process action selector after listing")]
        public bool Interactive { get; set; }

        [CommandOption("--select")]
        [Description("Select process by id or name and run action")]
        public string? Select { get; set; }

        [CommandOption("--action")]
        [Description("Action for selected process: stop, restart, delete, logs")]
        public string? Action { get; set; }

        [DefaultValue(true)]
        [CommandOption("--logs-live")]
        [Description("With action=logs, stream logs in realtime")]
        public bool LogsLive { get; set; } = true;

        [DefaultValue(true)]
        [CommandOption("--logs-stdout")]
        [Description("With action=logs, include stdout stream")]
        public bool LogsStdout { get; set; } = true;

        [DefaultValue(true)]
        [CommandOption("--logs-stderr")]
        [Description("With action=logs, include stderr stream")]
        public bool LogsStderr { get; set; } = true;

        [DefaultValue(0)]
        [CommandOption("--logs-max-lines")]
        [Description("With action=logs, stop after this many lines (0 = unlimited)")]
        public int LogsMaxLines { get; set; }

        [DefaultValue(0)]
        [CommandOption("--logs-timeout-seconds")]
        [Description("With action=logs, stop streaming after timeout seconds (0 = unlimited)")]
        public int LogsTimeoutSeconds { get; set; }
    }
    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        return ExecutePublicAsync(context, settings, cancellationToken);
    }
    public async Task<int> ExecutePublicAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        _ = context;

        Widgets.Write([Widgets.Header(), Widgets.RunnerVersion]);
        Widgets.WriteLines(2);

        var source = settings.UseLpc || settings.AutoAttach
            ? ResolveSource(settings)
            : ProcessSource.LocalOnly;

        if (!TryLoadProcesses(source, settings, out var processes, out var sourceText, out var error))
        {
            Widgets.Write(Widgets.Error(error ?? "Failed to load process list"));
            return 2;
        }

        var filtered = processes
            .Where(p => !settings.RunningOnly || p.Running)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ProcessId)
            .ToArray();

        if (filtered.Length == 0)
        {
            Widgets.Write(Widgets.Warn("No managed processes are available in the selected source"), true);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            Widgets.WriteInterpolated($"Process inventory source: [{Widgets.PrimaryColorText}]{sourceText}[/]", true);
        }

        WriteTable(filtered);

        var actionInput = settings.Action?.Trim().ToLowerInvariant();
        if (settings.Interactive || !string.IsNullOrWhiteSpace(settings.Select) || !string.IsNullOrWhiteSpace(actionInput))
        {
            var actionContext = BuildActionContext(source, settings.LpcPort);
            var executeResult = await ExecuteActionFlowAsync(filtered, settings, actionContext, cancellationToken).ConfigureAwait(false);
            return executeResult;
        }

        return 0;
    }

    private static ProcessSource ResolveSource(Settings settings)
    {
        if (settings.UseLpc)
        {
            return ProcessSource.LpcOnly;
        }

        return ProcessSource.LocalThenLpc;
    }

    private static ActionContext BuildActionContext(ProcessSource source, int lpcPort)
    {
        return source switch
        {
            ProcessSource.LocalOnly => new ActionContext(LocalOnly: true, LpcPort: null),
            _ => new ActionContext(LocalOnly: false, LpcPort: lpcPort)
        };
    }

    private static bool TryLoadProcesses(
        ProcessSource source,
        Settings settings,
        out ProcessView[] processes,
        out string? sourceText,
        out string? error)
    {
        processes = [];
        sourceText = null;
        error = null;

        if (source is ProcessSource.LocalOnly)
        {
            processes = LoadLocalProcesses();
            sourceText = "Local session inventory";
            return true;
        }

        if (source is ProcessSource.LpcOnly)
        {
            if (!TryGetLpcProcesses(settings.LpcPort, out var lpcProcesses, out var lpcError))
            {
                error = $"Failed to query LPC process list on 127.0.0.1:{settings.LpcPort}. {lpcError}";
                return false;
            }

            processes = lpcProcesses.Select(ToProcessView).ToArray();
            sourceText = $"Attached host inventory from 127.0.0.1:{settings.LpcPort}";
            return true;
        }

        var localProcesses = LoadLocalProcesses();
        if (localProcesses.Length > 0)
        {
            processes = localProcesses;
            sourceText = "Local session inventory";
            return true;
        }

        if (!TryGetLpcProcesses(settings.LpcPort, out var attachedProcesses, out var attachedError))
        {
            error = attachedError;
            return false;
        }

        processes = attachedProcesses.Select(ToProcessView).ToArray();
        sourceText = $"Attached host inventory from 127.0.0.1:{settings.LpcPort}";
        return true;
    }

    private static ProcessView[] LoadLocalProcesses()
    {
        return InMemoryProcessManager.Instance
            .List()
            .Select(entry => new ProcessView(
                entry.Id,
                entry.LastKnownPid ?? entry.Process.Pid ?? 0,
                entry.Process.DisplayName,
                entry.Profile,
                entry.Process.IsRunning,
                entry.Executable ?? string.Empty,
                entry.Arguments ?? string.Empty,
                entry.Details,
                entry.ExposedPorts))
            .Where(view => view.ProcessId > 0)
            .ToArray();
    }

    private static ProcessView ToProcessView(LpcProcessInfo process)
    {
        var ports = process.Ports?.ToArray() ?? ParsePortsFromMessage(process.Message);
        return new ProcessView(
            Id: process.Name ?? process.ProcessId.ToString(CultureInfo.InvariantCulture),
            ProcessId: process.ProcessId,
            Name: process.Name ?? string.Empty,
            Profile: ResolveProfile(process),
            Running: process.Running,
            Exe: process.Exe,
            Args: process.Args,
            Message: process.Message,
            Ports: ports);
    }

    private static ProcessProfile ResolveProfile(LpcProcessInfo process)
    {
        var name = process.Name ?? string.Empty;
        if (name.Contains("Aspire", StringComparison.OrdinalIgnoreCase) || name.Contains("Dashboard", StringComparison.OrdinalIgnoreCase))
        {
            return ProcessProfile.AspireDashboard;
        }

        return ProcessProfile.ExecutableProcess;
    }

    private static int[] ParsePortsFromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return [];
        }

        var ports = new HashSet<int>();
        var segmentStart = message.IndexOf("port=", StringComparison.OrdinalIgnoreCase);
        if (segmentStart >= 0)
        {
            var start = segmentStart + 5;
            var end = message.IndexOf(',', start);
            var raw = end >= 0 ? message[start..end] : message[start..];
            foreach (var part in raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort)
                    && parsedPort is > ushort.MinValue and <= ushort.MaxValue)
                {
                    ports.Add(parsedPort);
                }
            }
        }

        var urlToken = "url=";
        var urlIndex = message.IndexOf(urlToken, StringComparison.OrdinalIgnoreCase);
        if (urlIndex >= 0)
        {
            var start = urlIndex + urlToken.Length;
            var end = message.IndexOf(',', start);
            var rawUrl = end >= 0 ? message[start..end].Trim() : message[start..].Trim();
            if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) && uri.Port is > ushort.MinValue and <= ushort.MaxValue)
            {
                ports.Add(uri.Port);
            }
        }

        return [.. ports.OrderBy(p => p)];
    }

    private static void WriteTable(IEnumerable<ProcessView> processes)
    {
        var table = new Table()
            .BorderStyle(new Style(Widgets.PrimaryColor, decoration: Decoration.Dim))
            .AddColumn("Id", c => c.NoWrap())
            .AddColumn("Name", c => c.NoWrap())
            .AddColumn("Profile", c => c.NoWrap())
            .AddColumn("Status", c => c.Centered().NoWrap())
            .AddColumn("PID", c => c.Centered().NoWrap())
            .AddColumn("Ports", c => c.Centered().NoWrap())
            .AddColumn("Details", c => c.NoWrap());

        foreach (var process in processes)
        {
            table.AddRow(
                process.Id.Truncate(18).Widget(),
                process.Name.Truncate(24).Widget(),
                ProcessProfileLabel(process.Profile).Widget(),
                Widgets.TableColumn([Widgets.StatusSymbol(process.Running)], HorizontalAlignment.Center),
                process.ProcessId.ToString(CultureInfo.InvariantCulture).Widget(),
                (process.Ports.Count == 0 ? string.Empty : string.Join(",", process.Ports)).Widget(),
                (process.Message ?? process.Args ?? string.Empty).Truncate(48).Widget());
        }

        AnsiConsole.Write(table);
    }

    private static async Task<int> ExecuteActionFlowAsync(ProcessView[] processes, Settings settings, ActionContext context, CancellationToken cancellationToken)
    {
        var selected = SelectProcess(processes, settings.Select, settings.Interactive);
        if (selected is null)
        {
            if (!string.IsNullOrWhiteSpace(settings.Select))
            {
                Widgets.Write(Widgets.Error($"Process '{settings.Select}' was not found in the list"));
                return 2;
            }

            return 0;
        }

        var action = ResolveAction(settings.Action, settings.Interactive);
        if (action is null)
        {
            Widgets.Write(Widgets.Warn("No action selected. Use --action stop|restart|delete|logs or --interactive."), true);
            return 0;
        }

        return action switch
        {
            ProcessAction.Stop => await StopProcessAsync(selected.Value, context, cancellationToken).ConfigureAwait(false),
            ProcessAction.Restart => await RestartProcessAsync(selected.Value, context, cancellationToken).ConfigureAwait(false),
            ProcessAction.Delete => await DeleteProcessAsync(selected.Value, context, cancellationToken).ConfigureAwait(false),
            ProcessAction.Logs => await ShowLogsAsync(selected.Value, context, settings, cancellationToken).ConfigureAwait(false),
            _ => 0
        };
    }

    private static ProcessView? SelectProcess(ProcessView[] processes, string? selector, bool interactive)
    {
        if (!string.IsNullOrWhiteSpace(selector))
        {
            return processes.FirstOrDefault(p => p.Id.Equals(selector, StringComparison.OrdinalIgnoreCase)
                                              || p.Name.Equals(selector, StringComparison.OrdinalIgnoreCase)
                                              || p.ProcessId.ToString(CultureInfo.InvariantCulture).Equals(selector, StringComparison.OrdinalIgnoreCase));
        }

        if (!interactive)
        {
            return null;
        }

        var prompt = new SelectionPrompt<ProcessView>()
            .Title("Select a managed process")
            .PageSize(10)
            .UseConverter(p => $"{p.Name} ({p.Id}, pid={p.ProcessId}, status={(p.Running ? "running" : "stopped")})")
            .AddChoices(processes);

        return AnsiConsole.Prompt(prompt);
    }

    private static ProcessAction? ResolveAction(string? action, bool interactive)
    {
        if (!string.IsNullOrWhiteSpace(action))
        {
            return action.Trim().ToLowerInvariant() switch
            {
                "stop" => ProcessAction.Stop,
                "restart" => ProcessAction.Restart,
                "delete" => ProcessAction.Delete,
                "remove" => ProcessAction.Delete,
                "logs" => ProcessAction.Logs,
                _ => null
            };
        }

        if (!interactive)
        {
            return null;
        }

        var prompt = new SelectionPrompt<ProcessAction>()
            .Title("Select an action")
            .AddChoices(ProcessAction.Stop, ProcessAction.Restart, ProcessAction.Delete, ProcessAction.Logs)
            .UseConverter(actionValue => actionValue switch
            {
                ProcessAction.Stop => "Stop",
                ProcessAction.Restart => "Restart",
                ProcessAction.Delete => "Delete",
                ProcessAction.Logs => "Logs (realtime)",
                _ => actionValue.ToString()
            });

        return AnsiConsole.Prompt(prompt);
    }

    private static async Task<int> StopProcessAsync(ProcessView process, ActionContext context, CancellationToken cancellationToken)
    {
        if (context.LocalOnly)
        {
            if (!InMemoryProcessManager.Instance.TryGet(process.Id, out var entry) || entry is null)
            {
                Widgets.Write(Widgets.Error($"Managed process '{process.Id}' was not found in local session"));
                return 2;
            }

            var stopped = await InMemoryProcessManager.Instance.StopAsync(process.Id, cancellationToken).ConfigureAwait(false);
            if (!stopped)
            {
                Widgets.Write(Widgets.Error($"Failed to stop process '{process.Name}'"));
                return -1;
            }

            Widgets.WriteInterpolated($"Stopped process [{Widgets.PrimaryColorText}]{process.Name}[/]", true);
            return 0;
        }

        var response = SendLpcRequest(context.LpcPort!.Value, new LpcRequest(Command: "Stop", ProcessId: process.ProcessId), out var error);
        if (response is null || !response.Ok)
        {
            Widgets.Write(Widgets.Error($"Failed to stop process '{process.Name}'. {error ?? response?.Error}"));
            return -1;
        }

        Widgets.WriteInterpolated($"Stopped process [{Widgets.PrimaryColorText}]{process.Name}[/]", true);
        return 0;
    }

    private static async Task<int> RestartProcessAsync(ProcessView process, ActionContext context, CancellationToken cancellationToken)
    {
        if (context.LocalOnly)
        {
            var restarted = await InMemoryProcessManager.Instance.RestartAsync(process.Id, cancellationToken).ConfigureAwait(false);
            if (!restarted)
            {
                Widgets.Write(Widgets.Error($"Failed to restart process '{process.Name}'"));
                return -1;
            }

            Widgets.WriteInterpolated($"Restarted process [{Widgets.PrimaryColorText}]{process.Name}[/]", true);
            return 0;
        }

        var response = SendLpcRequest(context.LpcPort!.Value, new LpcRequest(Command: "Restart", ProcessId: process.ProcessId), out var error);
        if (response is null || !response.Ok)
        {
            Widgets.Write(Widgets.Error($"Failed to restart process '{process.Name}'. {error ?? response?.Error}"));
            return -1;
        }

        Widgets.WriteInterpolated($"Restarted process [{Widgets.PrimaryColorText}]{process.Name}[/]", true);
        return 0;
    }

    private static async Task<int> DeleteProcessAsync(ProcessView process, ActionContext context, CancellationToken cancellationToken)
    {
        if (context.LocalOnly)
        {
            var removed = await InMemoryProcessManager.Instance.RemoveAsync(process.Id, stopIfRunning: true, cancellationToken).ConfigureAwait(false);
            if (!removed)
            {
                Widgets.Write(Widgets.Error($"Failed to delete process '{process.Name}'"));
                return -1;
            }

            Widgets.WriteInterpolated($"Deleted process [{Widgets.PrimaryColorText}]{process.Name}[/]", true);
            return 0;
        }

        var response = SendLpcRequest(context.LpcPort!.Value, new LpcRequest(Command: "Delete", ProcessId: process.ProcessId), out var error);
        if (response is null || !response.Ok)
        {
            Widgets.Write(Widgets.Error($"Failed to delete process '{process.Name}'. {error ?? response?.Error}"));
            return -1;
        }

        Widgets.WriteInterpolated($"Deleted process [{Widgets.PrimaryColorText}]{process.Name}[/]", true);
        return 0;
    }

    private static async Task<int> ShowLogsAsync(ProcessView process, ActionContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (context.LocalOnly)
        {
            Widgets.Write(Widgets.Warn("Logs action requires attached host LPC source. Re-run with --lpc or --auto-attach."), true);
            return 2;
        }

        if (!settings.LogsStdout && !settings.LogsStderr)
        {
            Widgets.Write(Widgets.Error("At least one of --logs-stdout or --logs-stderr must be enabled"));
            return 2;
        }

        return await ProcessLogsCommand.StreamLogsAsync(
            new ProcessLogsCommand.Settings
            {
                ProcessId = process.ProcessId,
                LpcPort = context.LpcPort!.Value,
                Live = settings.LogsLive,
                IncludeStdout = settings.LogsStdout,
                IncludeStderr = settings.LogsStderr,
                MaxLines = settings.LogsMaxLines,
                TimeoutSeconds = settings.LogsTimeoutSeconds
            },
            cancellationToken,
            writeIntro: true).ConfigureAwait(false);
    }

    private static LpcResponse? SendLpcRequest(int port, LpcRequest request, out string? error)
    {
        error = null;

        try
        {
            using var client = new TcpClient();
            client.ReceiveTimeout = 30000;
            client.SendTimeout = 10000;
            client.Connect(IPAddress.Loopback, port);

            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true)
            {
                AutoFlush = true
            };

            writer.WriteLine(JsonSerializer.Serialize(request));
            var responseLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                error = "No response received from LPC host.";
                return null;
            }

            return JsonSerializer.Deserialize<LpcResponse>(responseLine, JsonOptions);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static bool TryGetLpcProcesses(int port, out LpcProcessInfo[] processes, out string? error)
    {
        processes = [];
        error = null;

        try
        {
            using var client = new TcpClient();
            client.ReceiveTimeout = 3000;
            client.SendTimeout = 3000;
            client.Connect(IPAddress.Loopback, port);

            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true)
            {
                AutoFlush = true
            };

            var payload = JsonSerializer.Serialize(new LpcRequest(Command: "List"));
            writer.WriteLine(payload);

            var responseLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                error = "No response received from LPC host.";
                return false;
            }

            var response = JsonSerializer.Deserialize<LpcListResponse>(responseLine, JsonOptions);
            if (response is null)
            {
                error = "Invalid LPC response payload.";
                return false;
            }

            if (!response.Ok)
            {
                error = response.Error ?? "Unknown LPC list error.";
                return false;
            }

            processes = response.Processes?.ToArray() ?? [];
            return true;
        }
        catch (SocketException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ProcessProfileLabel(ProcessProfile profile) => profile switch
    {
        ProcessProfile.AspireDashboard => "Aspire",
        ProcessProfile.ExecutableProcess => "Executable",
        _ => profile.ToString()
    };



    private enum ProcessSource
    {
        LocalOnly = 0,
        LpcOnly = 1,
        LocalThenLpc = 2
    }

    private enum ProcessAction
    {
        Stop,
        Restart,
        Delete,
        Logs
    }

    private readonly record struct ActionContext(bool LocalOnly, int? LpcPort);

    private readonly record struct ProcessView(
        string Id,
        int ProcessId,
        string Name,
        ProcessProfile Profile,
        bool Running,
        string Exe,
        string Args,
        string? Message,
        IReadOnlyList<int> Ports);
}
