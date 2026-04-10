using AspireRunner.Core.Models;

namespace AspireRunner.Core.Abstractions;

public interface IProcessFactory
{
    Task<IManagedProcess?> CreateProcessAsync(ProcessCreationRequest request);

    Task<IDashboard?> CreateAspireDashboardAsync(DashboardOptions options);
}