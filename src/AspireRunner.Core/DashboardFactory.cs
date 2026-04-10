using AspireRunner.Core.Models;
using Microsoft.Extensions.Logging;

namespace AspireRunner.Core;

#pragma warning disable CS0618
public partial class ProcessFactory : IProcessFactory, IDashboardFactory
#pragma warning restore CS0618
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ProcessFactory> _logger;

    public ProcessFactory(ILogger<ProcessFactory> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task<IManagedProcess?> CreateProcessAsync(ProcessCreationRequest request)
    {
        return request.Profile switch
        {
            ProcessProfile.AspireDashboard when request.DashboardOptions is not null => await CreateAspireDashboardAsync(request.DashboardOptions),
            ProcessProfile.AspireDashboard => throw new ArgumentException("DashboardOptions are required for the Aspire dashboard profile", nameof(request)),
            ProcessProfile.ExecutableProcess when request.ExecutableProcessOptions is not null => CreateExecutableProcess(request.ExecutableProcessOptions),
            ProcessProfile.ExecutableProcess => throw new ArgumentException("ExecutableProcessOptions are required for the executable process profile", nameof(request)),
            _ => throw new NotSupportedException($"Process profile '{request.Profile}' is not supported")
        };
    }

    public async Task<IDashboard?> CreateAspireDashboardAsync(DashboardOptions options)
    {
        var compatibleRuntimes = await Dashboard.GetCompatibleRuntimesAsync();
        LogCompatibleRuntimes(compatibleRuntimes);

        if (compatibleRuntimes.Length == 0)
        {
            throw new ApplicationException($"The dashboard requires version '{Dashboard.MinimumRuntimeVersion}' or newer of the '{Dashboard.RequiredRuntimeName}' runtime");
        }

        var installedDashboards = Dashboard.GetInstalledDashboardsInfo();
        if (installedDashboards.Length is 0)
        {
            return null;
        }

        if (VersionRange.TryParse(options.Runner.PreferredVersion, loose: true, out var preferredVersion))
        {
            var preferredDashboard = installedDashboards.FirstOrDefault(d => preferredVersion.IsSatisfied(d.Version));
            if (preferredDashboard is not null)
            {
                LogInstallationPath(preferredDashboard.Path);
                return new Dashboard(preferredDashboard.Version, preferredDashboard.Path, options, _loggerFactory.CreateLogger<Dashboard>());
            }

            WarnPreferredVersionNotFound(options.Runner.PreferredVersion);
        }

        var latestDashboard = installedDashboards.MaxBy(d => d.Version)!;
        LogInstallationPath(latestDashboard.Path);

        return new Dashboard(latestDashboard.Version, latestDashboard.Path, options, _loggerFactory.CreateLogger<Dashboard>());
    }

    async Task<IDashboard?> IDashboardFactory.CreateDashboardAsync(DashboardOptions options)
        => await CreateAspireDashboardAsync(options);

    private IManagedProcess CreateExecutableProcess(ExecutableProcessOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            throw new ArgumentException("ExecutablePath cannot be empty", nameof(options));
        }

        return new Dashboard(options, _loggerFactory.CreateLogger<Dashboard>());
    }
}