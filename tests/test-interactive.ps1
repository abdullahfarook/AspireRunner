param(
    [switch]$Resume,
    [switch]$KeepHostRunning,
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
        [int]$TimeoutSeconds = 45
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
            return [int]$state.hostPid
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

    $hostProcess = Start-Process -FilePath "dotnet" -ArgumentList $arguments -WorkingDirectory $repoRoot -PassThru -WindowStyle Normal
    Save-State -HostPid $hostProcess.Id

    Assert-True -Condition (Wait-PortState -Port $LpcPort -ShouldBeListening $true -TimeoutSeconds 90) -Message "LPC endpoint is listening on $LpcPort"
    Assert-True -Condition (Wait-PortState -Port $DashboardPort -ShouldBeListening $true -TimeoutSeconds 120) -Message "Aspire dashboard is listening on $DashboardPort"

    return [int]$hostProcess.Id
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

$hostPid = Start-OrAttachHost
Write-Pass -Message "Host process is active with PID $hostPid"

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

Show-ManualSteps -Title "Interactive exit (detach back to dashboard view)" -Steps @(
    "Focus the Aspire host terminal window.",
    "Press P to open the process actions view.",
    "Exit the process actions view and return to the main dashboard view."
)

Assert-True -Condition (Wait-PortState -Port $LpcPort -ShouldBeListening $true -TimeoutSeconds 10) -Message "LPC endpoint stays active after returning from process actions"
Assert-True -Condition (Wait-PortState -Port $DashboardPort -ShouldBeListening $true -TimeoutSeconds 10) -Message "Dashboard stays active after returning from process actions"

Show-ManualSteps -Title "Interactive viewLogs on selected process" -Steps @(
    "Press P to open process actions.",
    "Select NodeExpressTestApp.",
    "Choose Logs and verify both stdout and stderr lines appear.",
    "Exit logs and return to the main dashboard view."
)

Assert-True -Condition (Ask-YesNo -Prompt "Did logs show both stdout and stderr lines") -Message "Interactive logs check confirmed"

Show-ManualSteps -Title "Interactive selected-process stop (Node)" -Steps @(
    "Press P.",
    "Select NodeExpressTestApp.",
    "Choose Stop."
)

Assert-True -Condition (Wait-PortState -Port $NodePort -ShouldBeListening $false -TimeoutSeconds 35) -Message "Node app port $NodePort is released after interactive stop"
$nodeAfterStop = Get-ProcessByName -Name "NodeExpressTestApp"
Assert-True -Condition ($null -ne $nodeAfterStop -and -not $nodeAfterStop.running) -Message "Node process is registered and marked stopped"

Show-ManualSteps -Title "Interactive selected-process restart (Node)" -Steps @(
    "Press P.",
    "Select NodeExpressTestApp.",
    "Choose Restart."
)

Assert-True -Condition (Wait-PortState -Port $NodePort -ShouldBeListening $true -TimeoutSeconds 45) -Message "Node app port $NodePort is listening after interactive restart"
$nodeAfterRestart = Get-ProcessByName -Name "NodeExpressTestApp"
Assert-True -Condition ($null -ne $nodeAfterRestart -and $nodeAfterRestart.running) -Message "Node process is running after interactive restart"

Show-ManualSteps -Title "Interactive selected-process stop (CSharp)" -Steps @(
    "Press P.",
    "Select CSharpSingleFileTestApp.",
    "Choose Stop."
)

Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $false -TimeoutSeconds 35) -Message "C# app port $CSharpPort is released after interactive stop"

Show-ManualSteps -Title "Interactive selected-process restart (CSharp)" -Steps @(
    "Press P.",
    "Select CSharpSingleFileTestApp.",
    "Choose Restart."
)

Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $true -TimeoutSeconds 45) -Message "C# app port $CSharpPort is listening after interactive restart"

Show-ManualSteps -Title "Interactive selected-process delete (CSharp)" -Steps @(
    "Press P.",
    "Select CSharpSingleFileTestApp.",
    "Choose Delete."
)

Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $false -TimeoutSeconds 35) -Message "C# app port $CSharpPort is released after interactive delete"
$csharpAfterDelete = Get-ProcessByName -Name "CSharpSingleFileTestApp"
Assert-True -Condition ($null -eq $csharpAfterDelete) -Message "C# process entry is removed after interactive delete"

if ($KeepHostRunning) {
    Write-Host ""
    Write-Host "Host is intentionally left running. Re-run with -Resume to continue." -ForegroundColor Magenta
    Write-Host "State file: $statePath" -ForegroundColor Magenta
    exit 0
}

Show-ManualSteps -Title "Interactive stop host" -Steps @(
    "Focus the Aspire host terminal window.",
    "Press Esc to exit the host runner."
)

Assert-True -Condition (Wait-HostExit -HostPid $hostPid -TimeoutSeconds 50) -Message "Host process exited after interactive stop"
Assert-True -Condition (Wait-PortState -Port $DashboardPort -ShouldBeListening $false -TimeoutSeconds 45) -Message "Dashboard port $DashboardPort released"
Assert-True -Condition (Wait-PortState -Port $LpcPort -ShouldBeListening $false -TimeoutSeconds 45) -Message "LPC port $LpcPort released"
Assert-True -Condition (Wait-PortState -Port $NodePort -ShouldBeListening $false -TimeoutSeconds 45) -Message "Node app port $NodePort released"
Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $false -TimeoutSeconds 45) -Message "C# app port $CSharpPort released"

if (Test-Path $statePath) {
    Remove-Item -Path $statePath -Force
}

Write-Host ""
Write-Host "All interactive process-manager checks passed." -ForegroundColor Green