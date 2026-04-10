namespace AspireRunner.Core.Abstractions;

[Obsolete("Use IProcessFactory instead.")]
public interface IDashboardFactory
{
    Task<IDashboard?> CreateDashboardAsync(DashboardOptions options);
}