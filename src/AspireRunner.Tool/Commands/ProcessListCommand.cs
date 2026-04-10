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

public class ProcessListCommand : Command<ProcessListCommand.Settings>
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
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        _ = context;
        _ = cancellationToken;

        Widgets.Write([Widgets.Header(), Widgets.RunnerVersion]);
        Widgets.WriteLines(2);

        if (settings.UseLpc)
        {
            return ShowLpcProcessList(settings.LpcPort, settings.RunningOnly);
        }

        var managedProcesses = InMemoryProcessManager.Instance
            .List()
            .Where(p => !settings.RunningOnly || p.Process.IsRunning)
            .ToArray();

        if (managedProcesses.Length > 0)
        {
            var table = new Table()
                .BorderStyle(new Style(Widgets.PrimaryColor, decoration: Decoration.Dim))
                .AddColumn("Id", c => c.NoWrap())
                .AddColumn("Name", c => c.NoWrap())
                .AddColumn("Profile", c => c.NoWrap())
                .AddColumn("Status", c => c.Centered().NoWrap())
                .AddColumn("PID", c => c.Centered().NoWrap())
                .AddColumn("Details", c => c.NoWrap());

            foreach (var process in managedProcesses)
            {
                var isRunning = process.Process.IsRunning;
                table.AddRow(
                    process.Id.Widget(),
                    process.Process.DisplayName.Widget(),
                    ProcessProfileLabel(process.Profile).Widget(),
                    Widgets.TableColumn([Widgets.StatusSymbol(isRunning)], HorizontalAlignment.Center),
                    (process.Process.Pid?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Widget(),
                    (process.Details ?? string.Empty).Truncate(48).Widget());
            }

            AnsiConsole.Write(table);
            Widgets.WriteLines(1);
            Widgets.Write(Widgets.Warn("The process inventory is in-memory and scoped to the current runner session."), true);

            return 0;
        }

        if (settings.AutoAttach && TryGetLpcProcesses(settings.LpcPort, out var lpcProcesses, out _))
        {
            var filteredProcesses = lpcProcesses
                .Where(p => !settings.RunningOnly || p.Running)
                .ToArray();

            if (filteredProcesses.Length > 0)
            {
                Widgets.WriteInterpolated($"No local processes found; showing attached host inventory from [{Widgets.PrimaryColorText}]127.0.0.1:{settings.LpcPort}[/]", true);
                WriteLpcTable(filteredProcesses);
                return 0;
            }
        }

        Widgets.Write(Widgets.Warn("No managed processes are available in the current session"), true);
        if (settings.AutoAttach)
        {
            Widgets.Write(Widgets.Warn($"Tip: use --lpc (or --lpc-port {settings.LpcPort}) to query an attached AspireRunner host"), true);
        }

        return 0;
    }

    private static int ShowLpcProcessList(int port, bool runningOnly)
    {
        if (!TryGetLpcProcesses(port, out var lpcProcesses, out var error))
        {
            Widgets.Write(Widgets.Error($"Failed to query LPC process list on 127.0.0.1:{port}. {error}"));
            return 2;
        }

        var filteredProcesses = lpcProcesses
            .Where(p => !runningOnly || p.Running)
            .ToArray();

        if (filteredProcesses.Length == 0)
        {
            Widgets.Write(Widgets.Warn("No managed processes were returned by the LPC host"), true);
            return 0;
        }

        Widgets.WriteInterpolated($"Attached host inventory from [{Widgets.PrimaryColorText}]127.0.0.1:{port}[/]", true);
        WriteLpcTable(filteredProcesses);
        return 0;
    }

    private static void WriteLpcTable(IEnumerable<LpcProcessInfo> processes)
    {
        var table = new Table()
            .BorderStyle(new Style(Widgets.PrimaryColor, decoration: Decoration.Dim))
            .AddColumn("Name", c => c.NoWrap())
            .AddColumn("Status", c => c.Centered().NoWrap())
            .AddColumn("PID", c => c.Centered().NoWrap())
            .AddColumn("Port", c => c.Centered().NoWrap())
            .AddColumn("Exe", c => c.NoWrap())
            .AddColumn("Details", c => c.NoWrap());

        foreach (var process in processes)
        {
            table.AddRow(
                (process.Name ?? string.Empty).Truncate(30).Widget(),
                Widgets.TableColumn([Widgets.StatusSymbol(process.Running)], HorizontalAlignment.Center),
                process.ProcessId.ToString(CultureInfo.InvariantCulture).Widget(),
                ResolvePortText(process).Widget(),
                process.Exe.Truncate(30).Widget(),
                (process.Message ?? process.Args ?? string.Empty).Truncate(48).Widget());
        }

        AnsiConsole.Write(table);
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

    private static string ResolvePortText(LpcProcessInfo process)
    {
        if (string.IsNullOrWhiteSpace(process.Message))
        {
            return string.Empty;
        }

        var message = process.Message;
        var urlToken = "url=";
        var urlIndex = message.IndexOf(urlToken, StringComparison.OrdinalIgnoreCase);
        if (urlIndex >= 0)
        {
            var start = urlIndex + urlToken.Length;
            var end = message.IndexOf(',', start);
            var rawUrl = end >= 0 ? message[start..end].Trim() : message[start..].Trim();

            if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port.ToString(CultureInfo.InvariantCulture);
            }
        }

        return string.Empty;
    }

    private static string ProcessProfileLabel(ProcessProfile profile) => profile switch
    {
        ProcessProfile.AspireDashboard => "Aspire",
        ProcessProfile.ExecutableProcess => "Executable",
        _ => profile.ToString()
    };
}