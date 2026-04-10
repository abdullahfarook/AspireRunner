param(
    [switch]$Resume,
    [switch]$KeepHostRunning,
    [switch]$RunAllAndShowList,
    [int]$DashboardPort = 19088,
    [int]$LpcPort = 38472,
    [int]$NodePort = 39000,
    [int]$CSharpPort = 39001
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$testsDir = Split-Path -Parent $PSCommandPath
$repoRoot = (Resolve-Path (Join-Path $testsDir "..")).Path
$statePath = Join-Path $testsDir ".processmanager.state.json"
$buildStamp = [DateTime]::UtcNow.ToString("yyyyMMddHHmmss")
$toolOutputDir = Join-Path $repoRoot ".tmp-build/tool-net8-tests-$buildStamp"
$toolDll = Join-Path $toolOutputDir "AspireRunner.Tool.dll"
$toolExe = Join-Path $toolOutputDir "AspireRunner.Tool.exe"

function Write-Step {
    param([string]$Message)
    Write-Host "`n[STEP] $Message" -ForegroundColor Cyan
}

function Write-Pass {
    param([string]$Message)
    Write-Host "[PASS] $Message" -ForegroundColor Green
}

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }

    Write-Pass -Message $Message
}

function Test-PortListening {
    param([int]$Port)

    $connections = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    return $connections.Count -gt 0
}

function Wait-PortState {
    param(
        [int]$Port,
        [bool]$ShouldBeListening,
        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        $isListening = Test-PortListening -Port $Port
        if ($isListening -eq $ShouldBeListening) {
            return $true
        }

        Start-Sleep -Milliseconds 400
    }

    return $false
}

function Ensure-ToolBuild {
    Write-Step "Building AspireRunner.Tool for the test harness"
    Push-Location $repoRoot
    try {
        dotnet build src/AspireRunner.Tool/AspireRunner.Tool.csproj -c Debug -f net8.0 -o $toolOutputDir
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }

    Assert-True -Condition (Test-Path $toolDll) -Message "Tool DLL exists at $toolDll"
}

function Ensure-NodeDependencies {
    Write-Step "Ensuring Node.js dependencies are installed"

    $nodeCommand = Get-Command node -ErrorAction SilentlyContinue
    $npmCommand = Get-Command npm -ErrorAction SilentlyContinue

    if ($null -eq $nodeCommand -or $null -eq $npmCommand) {
        throw "Node.js and npm are required to run tests/app.js"
    }

    $expressPath = Join-Path $testsDir "node_modules/express"
    if (Test-Path $expressPath) {
        Write-Pass -Message "Express dependency already installed"
        return
    }

    Push-Location $testsDir
    try {
        npm install --silent
        if ($LASTEXITCODE -ne 0) {
            throw "npm install failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }

    Assert-True -Condition (Test-Path $expressPath) -Message "Express dependency installed"
}

function Save-State {
    param([int]$HostPid)

    $state = [ordered]@{
        hostPid = $HostPid
        lpcPort = $LpcPort
        dashboardPort = $DashboardPort
        nodePort = $NodePort
        csharpPort = $CSharpPort
        updatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    }

    $state | ConvertTo-Json -Depth 5 | Set-Content -Path $statePath -Encoding UTF8
}

function Get-State {
    if (-not (Test-Path $statePath)) {
        return $null
    }

    $raw = Get-Content -Raw -Path $statePath
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return $null
    }

    return $raw | ConvertFrom-Json
}

function Invoke-LpcRequest {
    param(
        [hashtable]$Request,
        [switch]$AsStream,
        [int]$MaxLines = 4,
        [int]$TimeoutSeconds = 8
    )

    $client = [System.Net.Sockets.TcpClient]::new()

    try {
        $client.Connect("127.0.0.1", $LpcPort)
        $networkStream = $client.GetStream()
        $networkStream.ReadTimeout = [Math]::Max(1000, $TimeoutSeconds * 1000)
        $networkStream.WriteTimeout = 5000

        $writer = [System.IO.StreamWriter]::new($networkStream, [System.Text.Encoding]::UTF8, 1024, $true)
        $reader = [System.IO.StreamReader]::new($networkStream, [System.Text.Encoding]::UTF8, $true, 1024, $true)

        try {
            $json = $Request | ConvertTo-Json -Compress -Depth 10
            $writer.WriteLine($json)
            $writer.Flush()

            if ($AsStream) {
                $lines = [System.Collections.Generic.List[string]]::new()
                $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

                while ($lines.Count -lt $MaxLines -and (Get-Date) -lt $deadline) {
                    try {
                        $line = $reader.ReadLine()
                    }
                    catch [System.IO.IOException] {
                        break
                    }

                    if ([string]::IsNullOrWhiteSpace($line)) {
                        continue
                    }

                    [void]$lines.Add($line)
                }

                return $lines.ToArray()
            }

            $responseLine = $reader.ReadLine()
            if ([string]::IsNullOrWhiteSpace($responseLine)) {
                throw "No response received for command '$($Request.command)'"
            }

            return $responseLine | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
            $writer.Dispose()
        }
    }
    finally {
        $client.Dispose()
    }
}

function Get-LpcProcesses {
    $response = Invoke-LpcRequest -Request @{ command = "List" }
    if (-not $response.ok) {
        throw "LPC List failed: $($response.error)"
    }

    if ($null -eq $response.processes) {
        return @()
    }

    return @($response.processes)
}

function Resolve-PortFromProcess {
    param($ProcessInfo)

    if ($ProcessInfo.name -eq "NodeExpressTestApp") {
        return $NodePort
    }

    if ($ProcessInfo.name -eq "CSharpSingleFileTestApp") {
        return $CSharpPort
    }

    if ($ProcessInfo.name -eq "Aspire Dashboard") {
        return $DashboardPort
    }

    $signature = "$($ProcessInfo.name) $($ProcessInfo.exe) $($ProcessInfo.'args')"

    if ($signature -match "app\\.js") {
        return $NodePort
    }

    if ($signature -match "app\\.cs") {
        return $CSharpPort
    }

    if ($signature -match "app\\.csproj") {
        return $CSharpPort
    }

    if ($signature -match "Aspire" -or $signature -match "Dashboard") {
        return $DashboardPort
    }

    return $null
}

function Show-InventoryTable {
    $processes = Get-LpcProcesses
    $rows = foreach ($process in $processes) {
        [pscustomobject]@{
            Name = $process.name
            ProcessId = $process.processId
            Running = $process.running
            Port = Resolve-PortFromProcess -ProcessInfo $process
            Exe = $process.exe
            Arguments = $process.'args'
        }
    }

    Write-Host "`nManaged process inventory:" -ForegroundColor Yellow
    if ($rows.Count -eq 0) {
        Write-Host "(no managed processes found)"
        return
    }

    $rows | Sort-Object -Property Name | Format-Table -AutoSize
}

function Invoke-ToolProcessList {
    param([switch]$UseLpc)

    $arguments = @("process", "list", "--running-only")
    if ($UseLpc) {
        $arguments += @("--lpc", "--lpc-port", $LpcPort)
    }

    Invoke-ToolCommand -Arguments $arguments | Out-Null
}

function Invoke-ToolCommand {
    param([string[]]$Arguments)

    if (Test-Path $toolExe) {
        $output = & $toolExe @Arguments 2>&1
    }
    else {
        $output = & dotnet $toolDll @Arguments 2>&1
    }

    if ($LASTEXITCODE -ne 0) {
        throw "AspireRunner.Tool command failed with exit code $LASTEXITCODE. Args: $($Arguments -join ' ')"
    }

    return @($output)
}

function Invoke-ToolProcessAction {
    param(
        [string]$Select,
        [ValidateSet("stop", "restart", "delete", "logs")]
        [string]$Action,
        [int]$LogsMaxLines = 0,
        [int]$LogsTimeoutSeconds = 0
    )

    $arguments = @(
        "process",
        "list",
        "--lpc",
        "--lpc-port", $LpcPort,
        "--select", $Select,
        "--action", $Action
    )

    if ($Action -eq "logs") {
        $arguments += @("--logs-live", "--logs-stdout", "--logs-stderr")

        if ($LogsMaxLines -gt 0) {
            $arguments += @("--logs-max-lines", $LogsMaxLines)
        }

        if ($LogsTimeoutSeconds -gt 0) {
            $arguments += @("--logs-timeout-seconds", $LogsTimeoutSeconds)
        }
    }

    return Invoke-ToolCommand -Arguments $arguments
}

function Get-ProcessByName {
    param([string]$Name)

    $processes = Get-LpcProcesses
    return $processes | Where-Object { $_.name -eq $Name } | Select-Object -First 1
}

function Ensure-RegisteredProcess {
    param(
        [string]$Name,
        [string]$Exe,
        [string]$ArgumentText,
        [int]$ExpectedPort,
        [string]$WorkingDir,
        [string]$EnvironmentText = ""
    )

    $existing = Get-ProcessByName -Name $Name
    if ($null -ne $existing) {
        if ($existing.running -and (Wait-PortState -Port $ExpectedPort -ShouldBeListening $true -TimeoutSeconds 3)) {
            Write-Pass -Message "$Name already registered with PID $($existing.processId)"
            return [int]$existing.processId
        }

        Write-Step "Removing stale registration for $Name"
        $removeResponse = Invoke-LpcRequest -Request @{ command = "Delete"; processId = [int]$existing.processId }
        Assert-True -Condition $removeResponse.ok -Message "Stale registration removed for $Name"
    }

    Write-Step "Registering managed process: $Name"
    $registerRequest = @{
        command = "Register"
        name = $Name
        exe = $Exe
        port = $ExpectedPort
        envs = $EnvironmentText
        workingDir = $WorkingDir
    }

    $registerRequest["args"] = $ArgumentText
    $registerResponse = Invoke-LpcRequest -Request $registerRequest

    Assert-True -Condition $registerResponse.ok -Message "Register command succeeded for $Name"
    Assert-True -Condition (Wait-PortState -Port $ExpectedPort -ShouldBeListening $true -TimeoutSeconds 40) -Message "$Name is listening on port $ExpectedPort"

    return [int]$registerResponse.processId
}

function Start-OrAttachHost {
    $state = Get-State

    if ($Resume -and $null -ne $state) {
        $existingHost = Get-Process -Id $state.hostPid -ErrorAction SilentlyContinue
        if ($null -ne $existingHost -and (Wait-PortState -Port $LpcPort -ShouldBeListening $true -TimeoutSeconds 3)) {
            Write-Pass -Message "Attached to existing AspireRunner host PID $($state.hostPid)"
            return [int]$state.hostPid
        }
    }

    Write-Step "Starting AspireRunner host in background (detached mode)"
    $arguments = @(
        $toolDll,
        "aspire",
        "run",
        "--port", $DashboardPort,
        "--https", "false",
        "--mcp-port", "0",
        "--otlp-port", "0",
        "--auto-update", "false",
        "--multiple"
    )

    $hostProcess = Start-Process -FilePath "dotnet" -ArgumentList $arguments -WorkingDirectory $repoRoot -PassThru -WindowStyle Hidden
    Save-State -HostPid $hostProcess.Id

    Assert-True -Condition (Wait-PortState -Port $LpcPort -ShouldBeListening $true -TimeoutSeconds 90) -Message "LPC endpoint is listening on $LpcPort"
    Assert-True -Condition (Wait-PortState -Port $DashboardPort -ShouldBeListening $true -TimeoutSeconds 120) -Message "Aspire dashboard is listening on $DashboardPort"

    return [int]$hostProcess.Id
}

function Wait-HostExit {
    param(
        [int]$HostPid,
        [int]$TimeoutSeconds = 40
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $proc = Get-Process -Id $HostPid -ErrorAction SilentlyContinue
        if ($null -eq $proc) {
            return $true
        }

        Start-Sleep -Milliseconds 400
    }

    return $false
}

Write-Step "Preparing test harness"
Ensure-ToolBuild
Ensure-NodeDependencies

$hostPid = Start-OrAttachHost
Write-Pass -Message "Detached host process is active with PID $hostPid"

$nodeScriptPath = Join-Path $testsDir "app.js"
$csharpScriptPath = Join-Path $testsDir "app.cs"
$csharpProjectPath = Join-Path $testsDir "app.csproj"

$nodePid = Ensure-RegisteredProcess -Name "NodeExpressTestApp" -Exe "node" -ArgumentText ('"' + $nodeScriptPath + '"') -ExpectedPort $NodePort -WorkingDir $testsDir -EnvironmentText ("PORT={0}" -f $NodePort)
$csharpPid = Ensure-RegisteredProcess -Name "CSharpSingleFileTestApp" -Exe "dotnet" -ArgumentText ('run --project "' + $csharpProjectPath + '"') -ExpectedPort $CSharpPort -WorkingDir $repoRoot -EnvironmentText ("PORT={0}" -f $CSharpPort)

Assert-True -Condition (Wait-PortState -Port $DashboardPort -ShouldBeListening $true -TimeoutSeconds 10) -Message "Dashboard port $DashboardPort is open"
Assert-True -Condition (Wait-PortState -Port $NodePort -ShouldBeListening $true -TimeoutSeconds 10) -Message "Node app port $NodePort is open"
Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $true -TimeoutSeconds 10) -Message "C# app port $CSharpPort is open"

if ($RunAllAndShowList) {
    Write-Step "AspireRunner tool process list (LPC attached)"
    Invoke-ToolProcessList -UseLpc

    Write-Host "`nSession endpoints:" -ForegroundColor Yellow
    [pscustomobject]@{
        HostPid = $hostPid
        LpcEndpoint = "127.0.0.1:$LpcPort"
        DashboardUrl = "http://127.0.0.1:$DashboardPort"
        NodeUrl = "http://127.0.0.1:$NodePort"
        CSharpUrl = "http://127.0.0.1:$CSharpPort"
        StateFile = $statePath
    } | Format-List

    Write-Host "Host is intentionally left running. Re-run with -Resume -RunAllAndShowList to refresh list." -ForegroundColor Magenta
    exit 0
}

Write-Step "Rendering single inventory table for dashboard and managed apps"
Show-InventoryTable

Write-Step "Validating tool-native realtime logs action"
$logsOutput = Invoke-ToolProcessAction -Select "NodeExpressTestApp" -Action "logs" -LogsMaxLines 6 -LogsTimeoutSeconds 12
Assert-True -Condition ($logsOutput.Count -gt 0) -Message "Tool-native logs action returns output lines"

$stdoutLines = @($logsOutput | Where-Object { $_ -match "\[stdout\]" })
$stderrLines = @($logsOutput | Where-Object { $_ -match "\[stderr\]" })

Assert-True -Condition ($stdoutLines.Count -gt 0) -Message "Tool-native logs include stdout lines"
Assert-True -Condition ($stderrLines.Count -gt 0) -Message "Tool-native logs include stderr lines"

Write-Host "Recent tool-native stdout lines:" -ForegroundColor Yellow
$stdoutLines | Select-Object -First 3 | ForEach-Object { Write-Host "  $_" }
Write-Host "Recent tool-native stderr lines:" -ForegroundColor Yellow
$stderrLines | Select-Object -First 3 | ForEach-Object { Write-Host "  $_" }

Write-Step "Validating stop + restart on C# app"
Invoke-ToolProcessAction -Select "CSharpSingleFileTestApp" -Action "stop" | Out-Null
Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $false -TimeoutSeconds 30) -Message "C# app port $CSharpPort is released after stop"

Invoke-ToolProcessAction -Select "CSharpSingleFileTestApp" -Action "restart" | Out-Null
Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $true -TimeoutSeconds 40) -Message "C# app port $CSharpPort is listening after restart"

$updatedCSharp = Get-ProcessByName -Name "CSharpSingleFileTestApp"
Assert-True -Condition ($null -ne $updatedCSharp -and $updatedCSharp.running) -Message "C# app is running after restart"
$csharpPid = [int]$updatedCSharp.processId

Write-Step "Validating delete operation and port release on Node app"
Invoke-ToolProcessAction -Select "NodeExpressTestApp" -Action "delete" | Out-Null
Assert-True -Condition (Wait-PortState -Port $NodePort -ShouldBeListening $false -TimeoutSeconds 30) -Message "Node app port $NodePort is released after delete"

$nodeAfterDelete = Get-ProcessByName -Name "NodeExpressTestApp"
Assert-True -Condition ($null -eq $nodeAfterDelete) -Message "Node app entry removed from manager inventory"

$nodePid = Ensure-RegisteredProcess -Name "NodeExpressTestApp" -Exe "node" -ArgumentText ('"' + $nodeScriptPath + '"') -ExpectedPort $NodePort -WorkingDir $testsDir -EnvironmentText ("PORT={0}" -f $NodePort)

Write-Step "Inventory after stop/restart/delete coverage"
Show-InventoryTable

if ($KeepHostRunning) {
    Write-Host "`nHost is intentionally left running. Re-run with -Resume to continue from current state." -ForegroundColor Magenta
    Write-Host "State file: $statePath"
    exit 0
}

Write-Step "Stopping AspireRunner host and validating full cleanup"
$shutdownResponse = Invoke-LpcRequest -Request @{ command = "Shutdown" }
Assert-True -Condition $shutdownResponse.ok -Message "Host shutdown command acknowledged"
Assert-True -Condition (Wait-HostExit -HostPid $hostPid -TimeoutSeconds 45) -Message "AspireRunner host process exited"

Assert-True -Condition (Wait-PortState -Port $DashboardPort -ShouldBeListening $false -TimeoutSeconds 40) -Message "Dashboard port $DashboardPort released"
Assert-True -Condition (Wait-PortState -Port $NodePort -ShouldBeListening $false -TimeoutSeconds 40) -Message "Node app port $NodePort released"
Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $false -TimeoutSeconds 40) -Message "C# app port $CSharpPort released"

if (Test-Path $statePath) {
    Remove-Item -Path $statePath -Force
}

Write-Host "`nAll process-manager integration checks passed." -ForegroundColor Green
