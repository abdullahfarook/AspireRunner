param(
    [switch]$Resume,
    [switch]$KeepHostRunning,
    [switch]$Auto,
    [int]$DashboardPort = 19088,
    [int]$LpcPort = 38472,
    [int]$NodePort = 39000,
    [int]$CSharpPort = 39001
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$testsDir = Split-Path -Parent $PSCommandPath
$repoRoot = (Resolve-Path (Join-Path $testsDir "..")).Path
$statePath = Join-Path $testsDir ".test-interactive.state.json"

$buildStamp = [DateTime]::UtcNow.ToString("yyyyMMddHHmmss")
$toolOutputDir = Join-Path $repoRoot ".tmp-build/tool-net8-interactive-$buildStamp"
$toolDll = Join-Path $toolOutputDir "AspireRunner.Tool.dll"
$toolExe = Join-Path $toolOutputDir "AspireRunner.Tool.exe"

function Write-Step {
    param([string]$Message)

    Write-Host ""
    Write-Host "[STEP] $Message" -ForegroundColor Cyan
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

function Ask-YesNo {
    param([string]$Prompt)

    $answer = Read-Host "$Prompt (y/n)"
    return $answer -match '^(y|yes)$'
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

function Wait-HostExit {
    param(
        [int]$HostPid,
        [string]$ExpectedProcessName,
        [datetime]$ExpectedStartTimeUtc,
        [int]$TimeoutSeconds = 45
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $proc = Get-Process -Id $HostPid -ErrorAction SilentlyContinue
        if ($null -eq $proc) {
            return $true
        }

        if ($proc.ProcessName -ne $ExpectedProcessName) {
            return $true
        }

        try {
            $currentStartTimeUtc = $proc.StartTime.ToUniversalTime()
            if ($currentStartTimeUtc -ne $ExpectedStartTimeUtc) {
                return $true
            }
        }
        catch {
            # If process metadata cannot be read anymore, treat it as exited/replaced.
            return $true
        }

        Start-Sleep -Milliseconds 400
    }

    return $false
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

function Ensure-ToolBuild {
    Write-Step "Building AspireRunner.Tool for interactive harness"

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

function Invoke-ToolCommand {
    param(
        [string[]]$Arguments,
        [string]$Description = "AspireRunner.Tool command"
    )

    if (Test-Path $toolExe) {
        $output = & $toolExe @Arguments 2>&1
    }
    else {
        $output = & dotnet $toolDll @Arguments 2>&1
    }

    if ($LASTEXITCODE -ne 0) {
        $capturedOutput = [string](@($output) | Out-String)
        $capturedOutput = $capturedOutput.Trim()
        throw "$Description failed with exit code $LASTEXITCODE. Args: $($Arguments -join ' ')`n$capturedOutput"
    }

    return @($output)
}

function Assert-OutputContains {
    param(
        [string[]]$Output,
        [string]$Regex,
        [string]$Message
    )

    $matches = @($Output | Where-Object { $_ -match $Regex })
    Assert-True -Condition ($matches.Count -gt 0) -Message $Message
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

    return Invoke-ToolCommand -Arguments $arguments -Description "Auto simulated interactive action '$Action' for '$Select'"
}

function Invoke-LpcRequest {
    param(
        [hashtable]$Request,
        [switch]$AsStream,
        [int]$MaxLines = 8,
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
        args = $ArgumentText
        port = $ExpectedPort
        envs = $EnvironmentText
        workingDir = $WorkingDir
    }

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
            return [pscustomobject]@{
                Pid = [int]$state.hostPid
                ProcessName = $existingHost.ProcessName
                StartTimeUtc = $existingHost.StartTime.ToUniversalTime()
            }
        }
    }

    Write-Step "Starting AspireRunner host in a visible terminal window"
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

    $windowStyle = if ($Auto) { "Hidden" } else { "Normal" }
    $hostProcess = Start-Process -FilePath "dotnet" -ArgumentList $arguments -WorkingDirectory $repoRoot -PassThru -WindowStyle $windowStyle
    Save-State -HostPid $hostProcess.Id

    Assert-True -Condition (Wait-PortState -Port $LpcPort -ShouldBeListening $true -TimeoutSeconds 90) -Message "LPC endpoint is listening on $LpcPort"
    Assert-True -Condition (Wait-PortState -Port $DashboardPort -ShouldBeListening $true -TimeoutSeconds 120) -Message "Aspire dashboard is listening on $DashboardPort"

    return [pscustomobject]@{
        Pid = [int]$hostProcess.Id
        ProcessName = $hostProcess.ProcessName
        StartTimeUtc = $hostProcess.StartTime.ToUniversalTime()
    }
}

function Show-ManualSteps {
    param(
        [string]$Title,
        [string[]]$Steps
    )

    Write-Step $Title
    Write-Host "Complete these actions in the host terminal window:" -ForegroundColor Yellow
    foreach ($step in $Steps) {
        Write-Host "  - $step" -ForegroundColor Yellow
    }

    [void](Read-Host "Press Enter here after completing the actions")
    Write-Pass -Message "$Title completed"
}

Write-Step "Preparing interactive process-manager harness"
Ensure-ToolBuild
Ensure-NodeDependencies

$hostInfo = Start-OrAttachHost
$hostPid = [int]$hostInfo.Pid
Write-Pass -Message "Host process is active with PID $hostPid"
if ($Auto) {
    Write-Pass -Message "Auto simulation mode is enabled"
}

$nodeScriptPath = Join-Path $testsDir "app.js"
$csharpProjectPath = Join-Path $testsDir "app.csproj"

$null = Ensure-RegisteredProcess -Name "NodeExpressTestApp" -Exe "node" -ArgumentText ('"' + $nodeScriptPath + '"') -ExpectedPort $NodePort -WorkingDir $testsDir -EnvironmentText ("PORT={0}" -f $NodePort)
$null = Ensure-RegisteredProcess -Name "CSharpSingleFileTestApp" -Exe "dotnet" -ArgumentText ('run --project "' + $csharpProjectPath + '"') -ExpectedPort $CSharpPort -WorkingDir $repoRoot -EnvironmentText ("PORT={0}" -f $CSharpPort)

Assert-True -Condition (Wait-PortState -Port $NodePort -ShouldBeListening $true -TimeoutSeconds 10) -Message "Node app port $NodePort is open"
Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $true -TimeoutSeconds 10) -Message "C# app port $CSharpPort is open"

Write-Host ""
Write-Host "Session endpoints:" -ForegroundColor Yellow
[pscustomobject]@{
    HostPid = $hostPid
    LpcEndpoint = "127.0.0.1:$LpcPort"
    DashboardUrl = "http://127.0.0.1:$DashboardPort"
    NodeUrl = "http://127.0.0.1:$NodePort"
    CSharpUrl = "http://127.0.0.1:$CSharpPort"
    StateFile = $statePath
} | Format-List

if ($Auto) {
    Write-Step "Interactive exit (detach back to dashboard view) - auto simulation"
    $listOutput = Invoke-ToolCommand -Arguments @(
        "process",
        "list",
        "--lpc",
        "--lpc-port", $LpcPort,
        "--running-only"
    ) -Description "Auto simulated process actions open/close"

    Assert-OutputContains -Output $listOutput -Regex "Process inventory source" -Message "Process list output includes inventory source header"
    Assert-OutputContains -Output $listOutput -Regex "Attached host inventory" -Message "Process list output confirms LPC-attached inventory source"

    $nodeListed = Get-ProcessByName -Name "NodeExpressTestApp"
    $csharpListed = Get-ProcessByName -Name "CSharpSingleFileTestApp"
    Assert-True -Condition ($null -ne $nodeListed) -Message "LPC list contains Node app"
    Assert-True -Condition ($null -ne $csharpListed) -Message "LPC list contains C# app"
}
else {
    Show-ManualSteps -Title "Interactive exit (detach back to dashboard view)" -Steps @(
        "Focus the Aspire host terminal window.",
        "Press P to open the process actions view.",
        "Exit the process actions view and return to the main dashboard view."
    )
}

Assert-True -Condition (Wait-PortState -Port $LpcPort -ShouldBeListening $true -TimeoutSeconds 10) -Message "LPC endpoint stays active after returning from process actions"
Assert-True -Condition (Wait-PortState -Port $DashboardPort -ShouldBeListening $true -TimeoutSeconds 10) -Message "Dashboard stays active after returning from process actions"

if ($Auto) {
    Write-Step "Interactive viewLogs on selected process - auto simulation"
    $logsOutput = Invoke-ToolProcessAction -Select "NodeExpressTestApp" -Action "logs" -LogsMaxLines 8 -LogsTimeoutSeconds 12
    Assert-OutputContains -Output $logsOutput -Regex "\[stdout\]" -Message "Logs output includes stdout lines"
    Assert-OutputContains -Output $logsOutput -Regex "\[stderr\]" -Message "Logs output includes stderr lines"
}
else {
    Show-ManualSteps -Title "Interactive viewLogs on selected process" -Steps @(
        "Press P to open process actions.",
        "Select NodeExpressTestApp.",
        "Choose Logs and verify both stdout and stderr lines appear.",
        "Exit logs and return to the main dashboard view."
    )

    Assert-True -Condition (Ask-YesNo -Prompt "Did logs show both stdout and stderr lines") -Message "Interactive logs check confirmed"
}

if ($Auto) {
    Write-Step "Interactive selected-process stop (Node) - auto simulation"
    $stopNodeOutput = Invoke-ToolProcessAction -Select "NodeExpressTestApp" -Action "stop"
    Assert-OutputContains -Output $stopNodeOutput -Regex "Stopped process" -Message "Stop action output confirms node process was stopped"
}
else {
    Show-ManualSteps -Title "Interactive selected-process stop (Node)" -Steps @(
        "Press P.",
        "Select NodeExpressTestApp.",
        "Choose Stop."
    )
}

Assert-True -Condition (Wait-PortState -Port $NodePort -ShouldBeListening $false -TimeoutSeconds 35) -Message "Node app port $NodePort is released after interactive stop"
$nodeAfterStop = Get-ProcessByName -Name "NodeExpressTestApp"
Assert-True -Condition ($null -ne $nodeAfterStop -and -not $nodeAfterStop.running) -Message "Node process is registered and marked stopped"

if ($Auto) {
    Write-Step "Interactive selected-process restart (Node) - auto simulation"
    $restartNodeOutput = Invoke-ToolProcessAction -Select "NodeExpressTestApp" -Action "restart"
    Assert-OutputContains -Output $restartNodeOutput -Regex "Restarted process" -Message "Restart action output confirms node process restart"
}
else {
    Show-ManualSteps -Title "Interactive selected-process restart (Node)" -Steps @(
        "Press P.",
        "Select NodeExpressTestApp.",
        "Choose Restart."
    )
}

Assert-True -Condition (Wait-PortState -Port $NodePort -ShouldBeListening $true -TimeoutSeconds 45) -Message "Node app port $NodePort is listening after interactive restart"
$nodeAfterRestart = Get-ProcessByName -Name "NodeExpressTestApp"
Assert-True -Condition ($null -ne $nodeAfterRestart -and $nodeAfterRestart.running) -Message "Node process is running after interactive restart"

if ($Auto) {
    Write-Step "Interactive selected-process stop (CSharp) - auto simulation"
    $stopCSharpOutput = Invoke-ToolProcessAction -Select "CSharpSingleFileTestApp" -Action "stop"
    Assert-OutputContains -Output $stopCSharpOutput -Regex "Stopped process" -Message "Stop action output confirms C# process was stopped"
}
else {
    Show-ManualSteps -Title "Interactive selected-process stop (CSharp)" -Steps @(
        "Press P.",
        "Select CSharpSingleFileTestApp.",
        "Choose Stop."
    )
}

Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $false -TimeoutSeconds 35) -Message "C# app port $CSharpPort is released after interactive stop"

if ($Auto) {
    Write-Step "Interactive selected-process restart (CSharp) - auto simulation"
    $restartCSharpOutput = Invoke-ToolProcessAction -Select "CSharpSingleFileTestApp" -Action "restart"
    Assert-OutputContains -Output $restartCSharpOutput -Regex "Restarted process" -Message "Restart action output confirms C# process restart"
}
else {
    Show-ManualSteps -Title "Interactive selected-process restart (CSharp)" -Steps @(
        "Press P.",
        "Select CSharpSingleFileTestApp.",
        "Choose Restart."
    )
}

Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $true -TimeoutSeconds 45) -Message "C# app port $CSharpPort is listening after interactive restart"

if ($Auto) {
    Write-Step "Interactive selected-process delete (CSharp) - auto simulation"
    $deleteCSharpOutput = Invoke-ToolProcessAction -Select "CSharpSingleFileTestApp" -Action "delete"
    Assert-OutputContains -Output $deleteCSharpOutput -Regex "Deleted process" -Message "Delete action output confirms C# process removal"
}
else {
    Show-ManualSteps -Title "Interactive selected-process delete (CSharp)" -Steps @(
        "Press P.",
        "Select CSharpSingleFileTestApp.",
        "Choose Delete."
    )
}

Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $false -TimeoutSeconds 35) -Message "C# app port $CSharpPort is released after interactive delete"
$csharpAfterDelete = Get-ProcessByName -Name "CSharpSingleFileTestApp"
Assert-True -Condition ($null -eq $csharpAfterDelete) -Message "C# process entry is removed after interactive delete"

if ($KeepHostRunning) {
    Write-Host ""
    Write-Host "Host is intentionally left running. Re-run with -Resume to continue." -ForegroundColor Magenta
    Write-Host "State file: $statePath" -ForegroundColor Magenta
    exit 0
}

if ($Auto) {
    Write-Step "Interactive stop host - auto simulation via LPC shutdown"
    $shutdownResponse = Invoke-LpcRequest -Request @{ command = "Shutdown" }
    Assert-True -Condition $shutdownResponse.ok -Message "Host shutdown command acknowledged in auto simulation"
}
else {
    Show-ManualSteps -Title "Interactive stop host" -Steps @(
        "Focus the Aspire host terminal window.",
        "Press Esc to exit the host runner."
    )
}

Assert-True -Condition (Wait-HostExit -HostPid $hostPid -ExpectedProcessName $hostInfo.ProcessName -ExpectedStartTimeUtc $hostInfo.StartTimeUtc -TimeoutSeconds 50) -Message "Host process exited after interactive stop"
Assert-True -Condition (Wait-PortState -Port $DashboardPort -ShouldBeListening $false -TimeoutSeconds 45) -Message "Dashboard port $DashboardPort released"
Assert-True -Condition (Wait-PortState -Port $LpcPort -ShouldBeListening $false -TimeoutSeconds 45) -Message "LPC port $LpcPort released"
Assert-True -Condition (Wait-PortState -Port $NodePort -ShouldBeListening $false -TimeoutSeconds 45) -Message "Node app port $NodePort released"
Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $false -TimeoutSeconds 45) -Message "C# app port $CSharpPort released"

if (Test-Path $statePath) {
    Remove-Item -Path $statePath -Force
}

Write-Host ""
Write-Host "All interactive process-manager checks passed." -ForegroundColor Green