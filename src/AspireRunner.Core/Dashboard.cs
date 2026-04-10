using AspireRunner.Core.Extensions;
using AspireRunner.Core.Models;
using Medallion.Threading.FileSystem;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AspireRunner.Core;

public partial class Dashboard : IDashboard, IProcessOutputSource
{
    private enum RuntimeProfile
    {
        AspireDashboard = 0,
        ExecutableProcess = 1
    }

    private Process? _managedProcess;
    private bool _stopRequested;

    private readonly string _runnerPath;
    private readonly ILogger<Dashboard> _logger;
    private readonly FileDistributedLock _instanceLock;
    private readonly IDictionary<string, string?> _environmentVariables;
    private readonly RuntimeProfile _runtimeProfile;
    private readonly ExecutableProcessOptions? _executableProcessOptions;

    public Version Version { get; }

    public DashboardOptions Options { get; }

    public string InstallationPath { get; }

    public string? Url { get; private set; }

    public IReadOnlyList<(string Url, string Protocol)>? OtlpEndpoints { get; private set; }

    public string? McpEndpoint { get; private set; }

    public string DisplayName { get; }

    public bool HasErrors { get; private set; }

    public bool IsRunning => _managedProcess.IsRunning();

    public int? Pid => _managedProcess?.Id;

    public event Action<string>? DashboardStarted;

    public event Action<(string Url, string Protocol)>? OtlpEndpointReady;

    public event Action<string>? McpEndpointReady;

    internal Dashboard(Version version, string dllPath, DashboardOptions options, ILogger<Dashboard> logger)
    {
        Version = version;
        Options = options;
        InstallationPath = dllPath;
        DisplayName = "Aspire Dashboard";

        _logger = logger;
        _runnerPath = GetRunnerPath();
        _environmentVariables = options.ToEnvironmentVariables();
        _instanceLock = new FileDistributedLock(new DirectoryInfo(_runnerPath), InstanceLock);
        _runtimeProfile = RuntimeProfile.AspireDashboard;
    }

    internal Dashboard(ExecutableProcessOptions processOptions, ILogger<Dashboard> logger)
    {
        ArgumentNullException.ThrowIfNull(processOptions);

        Version = new Version(0, 0, 0);
        Options = CreateRuntimeOptions(processOptions);
        InstallationPath = !string.IsNullOrWhiteSpace(processOptions.WorkingDirectory)
            ? processOptions.WorkingDirectory!
            : Path.GetDirectoryName(processOptions.ExecutablePath) ?? Environment.CurrentDirectory;
        DisplayName = string.IsNullOrWhiteSpace(processOptions.DisplayName)
            ? Path.GetFileNameWithoutExtension(processOptions.ExecutablePath)
            : processOptions.DisplayName;

        _logger = logger;
        _runnerPath = GetRunnerPath();
        _environmentVariables = processOptions.EnvironmentVariables.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        _instanceLock = new FileDistributedLock(new DirectoryInfo(_runnerPath), InstanceLock);
        _runtimeProfile = RuntimeProfile.ExecutableProcess;
        _executableProcessOptions = processOptions;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var retryCount = 0;
        var retryDelay = TimeSpan.FromSeconds(Options.Runner.RunRetryDelay);

        do
        {
            if (retryCount > 0)
            {
                WarnFailedToStartDashboardWithRetry(Options.Runner.RunRetryDelay);
                await Task.Delay(retryDelay, cancellationToken);
            }

            if (await TryStartProcessAsync(cancellationToken))
            {
                return;
            }
        } while (retryCount++ < Options.Runner.RunRetryCount && !cancellationToken.IsCancellationRequested);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _stopRequested = true;
        if (_runtimeProfile is RuntimeProfile.AspireDashboard && Options.Runner.Mode is RunningMode.Standalone)
        {
            _managedProcess = null;
            LogStopIgnoredStandaloneMode();
            return;
        }

        try
        {
            await Task.Run(() => _managedProcess?.Kill(true), cancellationToken);
        }
        catch (InvalidOperationException)
        {
            WarnDashboardAlreadyStopped();
        }

        _managedProcess = null;
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning || cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        return _managedProcess!.WaitForExitAsync(cancellationToken);
    }

    private async Task<bool> TryStartProcessAsync(CancellationToken cancellationToken = default)
    {
        if (_runtimeProfile is RuntimeProfile.ExecutableProcess)
        {
            return TryStartExecutableProcess();
        }

        try
        {
            await using var lockHandle = await _instanceLock.AcquireAsync(timeout: TimeSpan.FromSeconds(InstanceLockTimeout), cancellationToken: cancellationToken);
            var instance = TryGetRunningInstance();

            if (instance.Dashboard.IsRunning())
            {
                if (Options.Runner.Mode is RunningMode.Standalone)
                {
                    // Reuse the existing dashboard process instead of restarting
                    _managedProcess = instance.Dashboard;
                    LogReusingRunningDashboard(_managedProcess.Id);

                    if (Options.Runner.RestartOnFailure)
                    {
                        RegisterProcessExitHandler();
                    }

                    return true;
                }

                if (!instance.Runner.IsRunning() || Options.Runner.SingleInstanceHandling is SingleInstanceHandling.ReplaceExisting)
                {
                    instance.Dashboard.Kill(true);
                }
                else if (Options.Runner.SingleInstanceHandling is SingleInstanceHandling.WarnAndExit)
                {
                    WarnExistingInstance(instance.Dashboard.Id);
                    return true;
                }
            }

            ClearInstanceState();
            _managedProcess = ProcessHelper.Run(DotnetCli.Executable, ["exec", Path.Combine(InstallationPath, DllName)], _environmentVariables, InstallationPath, OnStandardOutput, OnStandardError);
            if (_managedProcess is null)
            {
                LogFailedToStartDashboardProcess();
                return false;
            }

            if (Options.Runner.RestartOnFailure)
            {
                DashboardStarted += RegisterProcessExitHandler;
            }

            PersistInstance();
            return true;
        }
        catch (Exception ex)
        {
            LogFailedToStartDashboard(ex);
            return false;
        }
    }

    private bool TryStartExecutableProcess()
    {
        ArgumentNullException.ThrowIfNull(_executableProcessOptions);

        ClearInstanceState();
        _managedProcess = ProcessHelper.Run(
            _executableProcessOptions.ExecutablePath,
            [.._executableProcessOptions.Arguments],
            _environmentVariables,
            _executableProcessOptions.WorkingDirectory,
            OnStandardOutput,
            OnStandardError);

        if (_managedProcess is null)
        {
            _logger.LogError("Failed to start process {DisplayName} ({ExecutablePath})", DisplayName, _executableProcessOptions.ExecutablePath);
            return false;
        }

        if (Options.Runner.RestartOnFailure)
        {
            RegisterProcessExitHandlerImmediately();
        }

        return true;
    }

    private void RegisterProcessExitHandler(string? _ = null)
    {
        DashboardStarted -= RegisterProcessExitHandler;
        RegisterProcessExitHandlerCore();
    }

    private void RegisterProcessExitHandlerImmediately()
    {
        RegisterProcessExitHandlerCore();
    }

    private void RegisterProcessExitHandlerCore()
    {
        if (!_managedProcess.IsRunning())
        {
            return;
        }

        _managedProcess.EnableRaisingEvents = true;
        _managedProcess.Exited += async (_, _) =>
        {
            if (_stopRequested)
            {
                return;
            }

            if (_runtimeProfile is RuntimeProfile.AspireDashboard)
            {
                WarnDashboardExitedUnexpectedly();
            }
            else
            {
                _logger.LogWarning("Process {DisplayName} exited unexpectedly, attempting restart...", DisplayName);
            }

            await StartAsync();
        };
    }

    private void HandleDashboardOutput(string output)
    {
        if (_runtimeProfile is RuntimeProfile.ExecutableProcess)
        {
            return;
        }

        if (Options.Frontend.AuthMode is FrontendAuthMode.BrowserToken && output.Contains(DashboardStartedConsoleMessage, StringComparison.OrdinalIgnoreCase))
        {
            // Wait for the authentication token to be printed
            return;
        }

        if (DashboardLaunchUrlRegex().Match(output) is { Success: true } match)
        {
            Url = UrlHelper.ReplaceDefaultRoute(match.Groups["url"].Value);
            if (Options.Runner.LaunchBrowser)
            {
                _ = LaunchBrowserAsync(Url);
            }

            DashboardStarted?.Invoke(Url);
        }

        if (OtlpEndpointRegex().Match(output) is { Success: true } otlpMatch)
        {
            var endpoint = (UrlHelper.ReplaceDefaultRoute(otlpMatch.Groups["url"].Value), otlpMatch.Groups["protocol"].Value);
            var endpoints = (List<(string Url, string Protocol)>)(OtlpEndpoints ??= new List<(string Url, string Protocol)>());
            endpoints.Add(endpoint);

            OtlpEndpointReady?.Invoke(endpoint);
        }

        if (McpEndpointRegex().Match(output) is { Success: true } mcpMatch)
        {
            McpEndpoint = UrlHelper.ReplaceDefaultRoute(mcpMatch.Groups["url"].Value);
            McpEndpointReady?.Invoke(McpEndpoint);
        }
    }

    private Task LaunchBrowserAsync(string url)
    {
        try
        {
            var urlOpener = PlatformHelper.GetUrlOpener(url);
            if (urlOpener is null)
            {
                WarnFailedToFindUrlOpener();
                return Task.CompletedTask;
            }

            return ProcessHelper.Run(urlOpener.Value.Executable, urlOpener.Value.Arguments)?.WaitForExitAsync()
                ?? throw new ApplicationException("Failed to launch the browser");
        }
        catch
        {
            WarnFailedToLaunchBrowser();
        }

        return Task.CompletedTask;
    }

    private void ClearInstanceState()
    {
        Url = null;
        HasErrors = false;
        McpEndpoint = null;
        OtlpEndpoints = null;
        _lastOutput = null;
        _lastOutputTime = null;
    }

    private void PersistInstance()
    {
        if (!IsRunning || _runtimeProfile is not RuntimeProfile.AspireDashboard)
        {
            return;
        }

        var instanceFilePath = Path.Combine(_runnerPath, InstanceFile);
        File.WriteAllText(instanceFilePath, $"{_managedProcess!.Id}:{Environment.ProcessId}");
    }

    private static DashboardOptions CreateRuntimeOptions(ExecutableProcessOptions processOptions)
    {
        return new DashboardOptions
        {
            Runner = new RunnerOptions
            {
                PipeOutput = processOptions.PipeOutput,
                LaunchBrowser = false,
                SingleInstanceHandling = SingleInstanceHandling.Ignore,
                AutoUpdate = false,
                PreferredVersion = null,
                RestartOnFailure = processOptions.RestartOnFailure,
                RunRetryCount = 0,
                RunRetryDelay = Math.Max(processOptions.RestartDelaySeconds, 1),
                Mode = RunningMode.Embed
            },
            Frontend = new FrontendOptions
            {
                EndpointUrls = string.Empty,
                AuthMode = FrontendAuthMode.Unsecured,
                BrowserToken = null
            },
            Otlp = new OtlpOptions
            {
                EndpointUrl = null,
                HttpEndpointUrl = null,
                Cors = null,
                AuthMode = OtlpAuthMode.Unsecured,
                PrimaryApiKey = null,
                SecondaryApiKey = null
            },
            Mcp = new McpOptions
            {
                Disabled = true,
                AuthMode = McpAuthMode.Unsecured,
                PrimaryApiKey = null,
                SecondaryApiKey = null,
                EndpointUrl = null,
                PublicUrl = null,
                SuppressUnsecuredMessage = true
            }
        };
    }
}