# ProcessManagerRunner.Example.WebApi

ASP.NET Core example that:

1. **Starts Process Manager** in the background (via `AddProcessManagerRunner()`).
2. **Starts or attaches to the node-app** (same process across restarts):
   - On first run: registers `node index.js` with Process Manager (from `NodeApp` config).
   - On later runs: if the same exe/args are already running, attaches to that process (same PID).
3. **Streams node-app logs** into the ASP.NET Core log pipeline:
   - stdout → `logger.LogInformation("[node stdout] ...")`
   - stderr → `logger.LogError("[node stderr] ...")`

## Prerequisites

- **ProcessManager.Host** – auto-installed when you call `AddProcessManagerRunnerInstaller()`: the installer runs `dotnet tool install -g ProcessManager.Runner.Tool` (if needed) and copies the Host from the installed tool package. Ensure the **ProcessManager.Runner.Tool** package is on a NuGet feed (e.g. publish it or use a local source).
- **Node.js** and a **node-app** (e.g. a folder with `index.js`). Set `NodeApp:WorkingDir` in `appsettings.json` to that folder, or leave empty to use `../../../../node-app` relative to the example output (create a `node-app` with an `index.js` that logs to stdout/stderr for testing).

## Configuration

- **ProcessManagerRunner:Port** – IPC port (default 38472).
- **ProcessManagerRunner:InstallSourcePath** – optional fallback path to a built ProcessManager.Host; used only if install via `dotnet tool` fails.
- **NodeApp:Exe** – executable (default `node`).
- **NodeApp:Args** – arguments (default `index.js`).
- **NodeApp:WorkingDir** – working directory for the process (default: auto-resolved or current directory).
- **NodeApp:Name** – display name in Process Manager (default `Node.js app`).

## Run

From repo root (with Process Manager and node-app available):

```bash
dotnet run --project ProcessManagerRunner/examples/ProcessManagerRunner.Example.WebApi
```

Then open http://localhost:5000. Node-app stdout/stderr appear in the console (and any configured log sinks). Restart the app: it will attach to the same node process and continue streaming logs.
