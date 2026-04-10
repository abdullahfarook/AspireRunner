using AspireRunner.Tool.ProcessManager.Lpc;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AspireRunner.Tool.Commands;

public class ProcessLogsCommand : AsyncCommand<ProcessLogsCommand.Settings>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<process-id>")]
        [Description("The managed process PID")]
        public required int ProcessId { get; set; }

        [DefaultValue(LpcServer.DefaultPort)]
        [CommandOption("--lpc-port")]
        [Description("LPC port to use when streaming process logs")]
        public int LpcPort { get; set; } = LpcServer.DefaultPort;

        [DefaultValue(true)]
        [CommandOption("--live")]
        [Description("Use live-only mode (skip buffered snapshot)")]
        public bool Live { get; set; } = true;

        [DefaultValue(true)]
        [CommandOption("--stdout")]
        [Description("Include stdout stream")]
        public bool IncludeStdout { get; set; } = true;

        [DefaultValue(true)]
        [CommandOption("--stderr")]
        [Description("Include stderr stream")]
        public bool IncludeStderr { get; set; } = true;

        [DefaultValue(0)]
        [CommandOption("--max-lines")]
        [Description("Stop after this many lines across selected streams (0 = unlimited)")]
        public int MaxLines { get; set; }

        [DefaultValue(0)]
        [CommandOption("--timeout-seconds")]
        [Description("Stop streaming after timeout seconds (0 = unlimited)")]
        public int TimeoutSeconds { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        _ = context;

        Widgets.Write([Widgets.Header(), Widgets.RunnerVersion]);
        Widgets.WriteLines(2);

        return await StreamLogsAsync(settings, cancellationToken, writeIntro: true).ConfigureAwait(false);
    }

    internal static async Task<int> StreamLogsAsync(Settings settings, CancellationToken cancellationToken, bool writeIntro)
    {
        if (!settings.IncludeStdout && !settings.IncludeStderr)
        {
            Widgets.Write(Widgets.Error("At least one of --stdout or --stderr must be enabled"));
            return 2;
        }

        if (settings.ProcessId <= 0)
        {
            Widgets.Write(Widgets.Error("process-id must be a positive integer"));
            return 2;
        }

        if (settings.MaxLines < 0)
        {
            Widgets.Write(Widgets.Error("--max-lines must be zero or a positive integer"));
            return 2;
        }

        if (settings.TimeoutSeconds < 0)
        {
            Widgets.Write(Widgets.Error("--timeout-seconds must be zero or a positive integer"));
            return 2;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (settings.TimeoutSeconds > 0)
        {
            linkedCts.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        }

        if (writeIntro)
        {
            Widgets.WriteInterpolated($"Streaming logs for PID [{Widgets.PrimaryColorText}]{settings.ProcessId}[/] from LPC [{Widgets.PrimaryColorText}]127.0.0.1:{settings.LpcPort}[/]", true);
            if (settings.MaxLines == 0 && settings.TimeoutSeconds == 0)
            {
                Widgets.Write("Press Ctrl+C to stop log streaming", true);
            }
        }

        var sync = new object();
        var lineCount = 0;
        var streamTasks = new List<Task<string?>>();

        if (settings.IncludeStdout)
        {
            streamTasks.Add(StreamChannelAsync(
                lpcPort: settings.LpcPort,
                processId: settings.ProcessId,
                isStdout: true,
                liveOnly: settings.Live,
                cancellationToken: linkedCts.Token,
                onLine: line =>
                {
                    if (ShouldEmitLine(settings.MaxLines, linkedCts, ref lineCount))
                    {
                        WriteStreamLine(isStdout: true, line, sync);
                    }
                }));
        }

        if (settings.IncludeStderr)
        {
            streamTasks.Add(StreamChannelAsync(
                lpcPort: settings.LpcPort,
                processId: settings.ProcessId,
                isStdout: false,
                liveOnly: settings.Live,
                cancellationToken: linkedCts.Token,
                onLine: line =>
                {
                    if (ShouldEmitLine(settings.MaxLines, linkedCts, ref lineCount))
                    {
                        WriteStreamLine(isStdout: false, line, sync);
                    }
                }));
        }

        var results = await Task.WhenAll(streamTasks).ConfigureAwait(false);
        var streamError = results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
        if (!string.IsNullOrWhiteSpace(streamError))
        {
            Widgets.Write(Widgets.Error(streamError));
            return -1;
        }

        return 0;
    }

    private static bool ShouldEmitLine(int maxLines, CancellationTokenSource cts, ref int lineCount)
    {
        var nextCount = Interlocked.Increment(ref lineCount);
        if (maxLines > 0 && nextCount > maxLines)
        {
            if (!cts.IsCancellationRequested)
            {
                cts.Cancel();
            }

            return false;
        }

        if (maxLines > 0 && nextCount == maxLines && !cts.IsCancellationRequested)
        {
            cts.Cancel();
        }

        return true;
    }

    private static async Task<string?> StreamChannelAsync(
        int lpcPort,
        int processId,
        bool isStdout,
        bool liveOnly,
        CancellationToken cancellationToken,
        Action<string> onLine)
    {
        var command = isStdout
            ? (liveOnly ? "StdoutLiveOnly" : "Stdout")
            : (liveOnly ? "StderrLiveOnly" : "Stderr");

        var channelLabel = isStdout ? "stdout" : "stderr";

        try
        {
            using var client = new TcpClient();
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;

            await client.ConnectAsync(IPAddress.Loopback, lpcPort, cancellationToken).ConfigureAwait(false);

            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

            var request = JsonSerializer.Serialize(new LpcRequest(Command: command, ProcessId: processId));
            await writer.WriteLineAsync(request).ConfigureAwait(false);

            var isFirstLine = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                if (line is null)
                {
                    return null;
                }

                if (isFirstLine && TryGetFailure(line, out var error))
                {
                    return error ?? $"Failed to stream {channelLabel}";
                }

                isFirstLine = false;

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                onLine(line);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            return $"{channelLabel} stream failed: {ex.Message}";
        }
    }

    private static void WriteStreamLine(bool isStdout, string line, object sync)
    {
        lock (sync)
        {
            var labelStyle = isStdout ? new Style(Widgets.PrimaryColor, decoration: Decoration.Bold) : new Style(Color.Orange1, decoration: Decoration.Bold);
            var label = isStdout ? "stdout" : "stderr";
            AnsiConsole.Write(new Text($"[{label}] ", labelStyle));
            AnsiConsole.Write(new Text(line));
            AnsiConsole.WriteLine();
        }
    }

    private static bool TryGetFailure(string line, out string? error)
    {
        error = null;

        if (!line.TrimStart().StartsWith('{'))
        {
            return false;
        }

        try
        {
            var response = JsonSerializer.Deserialize<LpcResponse>(line, JsonOptions);
            if (response is not null && !response.Ok)
            {
                error = response.Error ?? "LPC stream request failed";
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }
}
