namespace AspireRunner.Core.Models;

public enum ProcessProfile
{
    AspireDashboard = 0,
    ExecutableProcess = 1
}

public sealed record ProcessCreationRequest
{
    public required ProcessProfile Profile { get; init; }

    public DashboardOptions? DashboardOptions { get; init; }

    public ExecutableProcessOptions? ExecutableProcessOptions { get; init; }

    public static ProcessCreationRequest AspireDashboard(DashboardOptions options)
        => new()
        {
            Profile = ProcessProfile.AspireDashboard,
            DashboardOptions = options
        };

    public static ProcessCreationRequest Executable(ExecutableProcessOptions options)
        => new()
        {
            Profile = ProcessProfile.ExecutableProcess,
            ExecutableProcessOptions = options
        };
}