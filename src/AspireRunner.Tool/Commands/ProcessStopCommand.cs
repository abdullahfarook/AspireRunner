using AspireRunner.Tool.ProcessManager;
using System.ComponentModel;

namespace AspireRunner.Tool.Commands;

public class ProcessStopCommand : AsyncCommand<ProcessStopCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<id>")]
        [Description("The managed process id")]
        public required string Id { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        _ = context;

        Widgets.Write([Widgets.Header(), Widgets.RunnerVersion]);
        Widgets.WriteLines(2);

        if (!InMemoryProcessManager.Instance.TryGet(settings.Id, out var entry) || entry is null)
        {
            Widgets.Write(Widgets.Error($"Managed process '{settings.Id}' was not found in the current session"));
            return 2;
        }

        await InMemoryProcessManager.Instance.StopAsync(settings.Id, cancellationToken);
        Widgets.WriteInterpolated($"Stopped process [{Widgets.PrimaryColorText}]{entry.Process.DisplayName}[/] ([{Widgets.PrimaryColorText}]{entry.Id}[/])", true);

        return 0;
    }
}