# AspireRunner.Tool Architecture

## Scope
AspireRunner.Tool is a CLI for downloading and running the Aspire Dashboard for local development. It focuses on:
- Installing and managing dashboard versions
- Starting and monitoring the dashboard process
- Presenting a TUI (text UI) for status, logs, and actions

Non-goals:
- Hosting the dashboard for production use
- Replacing the Aspire Dashboard itself

## System context
The CLI sits on top of the AspireRunner.Core runtime and the AspireRunner.Installer package.
- AspireRunner.Core: starts and manages the dashboard process, builds environment variables, and parses output
- AspireRunner.Installer: downloads dashboard packages from NuGet and manages local installs

External dependencies:
- Spectre.Console + Spectre.Console.Cli for the TUI and command system
- Microsoft.Extensions.Logging for log flow and filtering

## High-level structure
- Program.cs
  - Creates the Spectre Console command app
  - Registers root command branches:
    - `aspire`: run, install, uninstall, cleanup
    - `process`: run, list, stop, restart, remove
  - Sets global exception handler to render a friendly error
- Commands/
  - RunCommand: main runtime loop and UI rendering
  - InstallCommand: install a dashboard version
  - UninstallCommand: remove dashboard versions
  - CleanupCommand: remove old installs and stale instance state
  - ProcessRunCommand: starts and manages a generic executable process
  - ProcessListCommand / ProcessLogsCommand / ProcessStopCommand / ProcessRestartCommand / ProcessRemoveCommand: session inventory operations for managed processes
- ProcessManager/
  - InMemoryProcessManager: session-scoped process registry for both Aspire and generic process profiles
  - ManagedProcessEntry: tracked process metadata
  - Lpc/: TCP process-control compatibility layer for ProcessManager.Client contracts
- Widgets.cs + Widgets.operations.cs
  - Shared rendering helpers, styles, and layout utilities
- Logging/
  - In-memory logger provider and channel-based log storage
- RunnerInfo.cs
  - Reads assembly metadata (version, command name, project URL)

## Runtime flow
### Run command
1) Validate that the dotnet CLI is available.
2) Build DashboardOptions from CLI args (ports, auth, HTTPS, CORS, MCP, runner settings).
3) If auto-update is enabled, run DashboardInstaller.EnsureLatestAsync().
4) Create a Dashboard instance via DashboardFactory.
   - If no dashboard is installed, invoke InstallCommand (latest), then retry.
5) Start the dashboard process via Dashboard.StartAsync().
6) Render the TUI layout and show a live startup spinner until endpoints are ready.
7) Enter a UI loop that:
  - refreshes endpoint status table, process inventory table, and log panel
   - reacts to key commands (R = restart, S = stop, B = open browser, H = help, Esc = exit)

8) Keep a local process-control endpoint active for the command lifetime.
  - Default bind: 127.0.0.1:38472
  - Contract-compatible with ProcessManager.Client request/response payloads for List/Register/Stop/Restart/IsAlreadyExist

### Process manager commands
- `process run`: create/start a generic executable process and register it in session inventory
- `process list`: show tracked managed processes
- `process stop`: stop a tracked process by id
- `process restart`: restart a tracked process by id
- `process remove`: remove tracked process metadata (optionally keep process running)

### LPC compatibility layer
- Implemented in `ProcessManager/Lpc/*`.
- Exposes a localhost TCP protocol compatible with ProcessManager.Client payload shapes.
- Backed by the same in-memory inventory used by UI and process commands.
- Supports buffered and live output forwarding via `Stdout` / `StdoutLiveOnly` and `Stderr` / `StderrLiveOnly`.

Note: inventory is currently in-memory and only available while the runner process is alive.

### Install command
- Reads compatible runtimes from Core and queries NuGet versions via DashboardInstaller.
- Installs the selected version under the runner path.

### Uninstall command
- Ensures the dashboard is not running.
- Removes matching versions using DashboardInstaller.RemoveAsync().

### Cleanup command
- Ensures neither the dashboard nor runner is running.
- Deletes the instance file and removes older dashboard versions (keeps latest).

## Configuration mapping
CLI arguments are mapped into DashboardOptions, then translated to environment variables for the dashboard process.
Key details:
- DashboardOptions -> environment variables via OptionsExtensions.ToEnvironmentVariables()
- Additional env vars:
  - DOTNET_DASHBOARD_OTLP_ENDPOINT_URL
  - DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL
  - ASPIRE_DASHBOARD_MCP_ENDPOINT_URL
  - ASPNETCORE_URLS

Environment variables that affect the tool:
- ASPIRE_RUNNER_PATH: base folder for runner data
- ASPIRE_RUNNER_NUGET_REPO: override NuGet feed for dashboard packages
- DOTNET_HOST_PATH: override dotnet host discovery

## Process and instance management
- The dashboard is started with `dotnet exec` against the installed Aspire.Dashboard.dll.
- A file-based lock (aspire_dashboard) prevents concurrent start races.
- An instance file (aspire-dashboard.instance) stores dashboard and runner PIDs.
- Single instance behavior is controlled by RunnerOptions.SingleInstanceHandling:
  - WarnAndExit, Ignore, or ReplaceExisting
- Running mode (Embed vs Standalone) defines whether the dashboard stops with the runner.

## UI and logging
- Widgets provides colors, header rendering, layout helpers, and status glyphs.
- The Run command renders a layout with:
  - Header (large or small based on terminal size)
  - Live log panel
  - Endpoint status table for Dashboard, OTLP gRPC, OTLP HTTP, and MCP
  - Process inventory table sourced from InMemoryProcessManager
  - Key action bar
- Logging uses an in-memory provider. Core logs are read from the category
  "AspireRunner.Core.Dashboard" and displayed in the log panel.

## Data and file layout
- Runner base path: ~/.dotnet/.AspireRunner (configurable via ASPIRE_RUNNER_PATH)
- Downloads: {RunnerPath}/dashboard/{version}/tools
- Instance file: {RunnerPath}/aspire-dashboard.instance

## Extensibility notes
- Add a new command by implementing a Spectre Console Command class and registering it in Program.cs.
- Extend UI by adding new Widgets helpers or extending the layout composition in RunCommand.
- If new dashboard options are introduced, update the CLI settings and the DashboardOptions mapping.
