using AspireRunner.Tool.ProcessManager;
using System.ComponentModel;

namespace AspireRunner.Tool.Commands;

public class ProcessRemoveCommand : AsyncCommand<ProcessRemoveCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<id>")]
        [Description("The managed process id")]
        public required string Id { get; set; }

        [CommandOption("--keep-running")]
        [Description("Remove from inventory without stopping the process")]
        public bool KeepRunning { get; set; }
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

        var removed = await InMemoryProcessManager.Instance.RemoveAsync(settings.Id, stopIfRunning: !settings.KeepRunning, cancellationToken);
        if (!removed)
        {
            Widgets.Write(Widgets.Error($"Failed to remove process '{entry.Process.DisplayName}'"));
            return -1;
        }

        Widgets.WriteInterpolated($"Removed process [{Widgets.PrimaryColorText}]{entry.Process.DisplayName}[/] ([{Widgets.PrimaryColorText}]{entry.Id}[/]) from session inventory", true);
        return 0;
    }
}