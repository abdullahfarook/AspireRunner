using AspireRunner.Tool;
using AspireRunner.Tool.Commands;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

static void TryShowCursor()
{
    if (Console.IsOutputRedirected)
    {
        return;
    }

    try
    {
        AnsiConsole.Cursor.Show();
    }
    catch
    {
        // Ignore cursor visibility failures in non-interactive contexts.
    }
}

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName(RunnerInfo.CommandName);
    config.SetApplicationVersion(RunnerInfo.Version.ToString());
    config.SetExceptionHandler((ex, _) =>
    {
        TryShowCursor();
        AnsiConsole.Write(Widgets.Error(ex.Message));

#if DEBUG
        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths);
#endif

        return -99;
    });

    config.AddBranch("aspire", aspire =>
    {
        aspire.AddCommand<RunCommand>("run");
        aspire.AddCommand<InstallCommand>("install");
        aspire.AddCommand<UninstallCommand>("uninstall");
        aspire.AddCommand<CleanupCommand>("cleanup")
            .WithDescription("Remove old versions of the dashboard and other temporary files");
        aspire.SetDefaultCommand<RunCommand>();
    });

    config.AddBranch("process", process =>
    {
        process.AddCommand<ProcessRunCommand>("run")
            .WithDescription("Run and manage a generic executable process");
        process.AddCommand<ProcessListCommand>("list")
            .WithDescription("List managed processes in the current session inventory");
        process.AddCommand<ProcessLogsCommand>("logs")
            .WithDescription("Stream logs for a managed process by PID");
        process.AddCommand<ProcessStopCommand>("stop")
            .WithDescription("Stop a managed process by id");
        process.AddCommand<ProcessRestartCommand>("restart")
            .WithDescription("Restart a managed process by id");
        process.AddCommand<ProcessRemoveCommand>("remove")
            .WithDescription("Remove a managed process from inventory");
        process.SetDefaultCommand<ProcessRunCommand>();
    });
});

var exitCode = await app.RunAsync(args);
TryShowCursor();
return exitCode;