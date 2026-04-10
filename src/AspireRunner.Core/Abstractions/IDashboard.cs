namespace AspireRunner.Core.Abstractions;

public interface IDashboard : IManagedProcess
{
    string IManagedProcess.DisplayName => "Aspire Dashboard";

    Version Version { get; }

    DashboardOptions Options { get; }

    string InstallationPath { get; }

    bool HasErrors { get; }

    string? Url { get; }

    IReadOnlyList<(string Url, string Protocol)>? OtlpEndpoints { get; }

    string? McpEndpoint { get; }

    /// <summary>
    /// Triggered when the Aspire Dashboard has started and the UI is ready.
    /// <br/>
    /// The dashboard URL (including the browser token) is passed to the event handler.
    /// </summary>
    event Action<string>? DashboardStarted;

    /// <summary>
    /// Triggered when the OTLP endpoint is ready to receive telemetry data.
    /// <br/>
    /// The OTLP endpoint URL and protocol are passed to the event handler.
    /// </summary>
    event Action<(string Url, string Protocol)>? OtlpEndpointReady;

    /// <summary>
    /// Triggered when the MCP endpoint is ready to be used.
    /// </summary>
    event Action<string>? McpEndpointReady;

}