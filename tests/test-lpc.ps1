param(
    [switch]$Resume,
    [switch]$KeepHostRunning,
    [int]$DashboardPort = 19088,
    [int]$LpcPort = 38472,
    [int]$NodePort = 39000,
    [int]$CSharpPort = 39001,
    [int]$KeepRunningPort = 39002
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$testsDir = Split-Path -Parent $PSCommandPath
$repoRoot = (Resolve-Path (Join-Path $testsDir "..")).Path
$statePath = Join-Path $testsDir ".test-lpc.state.json"

$buildStamp = [DateTime]::UtcNow.ToString("yyyyMMddHHmmss")
$toolOutputDir = Join-Path $repoRoot ".tmp-build/tool-net8-lpc-$buildStamp"
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

function Assert-OutputContains {
    param(
        [string[]]$Output,
        [string]$Regex,
        [string]$Message
    )

    $matchingLines = @($Output | Where-Object { $_ -match $Regex })
    Assert-True -Condition ($matchingLines.Count -gt 0) -Message $Message
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

        Start-Sleep -Milliseconds 350
    }

    return $false
}

function Wait-HostExit {
    param(
        [int]$HostPid,
        [string]$ExpectedProcessName,
        [datetime]$ExpectedStartTimeUtc,
        [int]$TimeoutSeconds = 60
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $processInfo = Get-Process -Id $HostPid -ErrorAction SilentlyContinue
        if ($null -eq $processInfo) {
            return $true
        }

        if ($processInfo.ProcessName -ne $ExpectedProcessName) {
            return $true
        }

        try {
            $currentStartTimeUtc = $processInfo.StartTime.ToUniversalTime()
            if ($currentStartTimeUtc -ne $ExpectedStartTimeUtc) {
                return $true
            }
        }
        catch {
            return $true
        }

        Start-Sleep -Milliseconds 350
    }

    return $false
}

function Get-PortOwnerPids {
    param([int]$Port)

    return @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
}

function Stop-PortOwnerPids {
    param([int]$Port)

    $ownerPids = Get-PortOwnerPids -Port $Port
    foreach ($ownerPid in $ownerPids) {
        if ($ownerPid -le 0) {
            continue
        }

        try {
            Stop-Process -Id $ownerPid -Force -ErrorAction Stop
        }
        catch {
            # Best effort cleanup.
        }
    }
}

function Save-State {
    param([int]$HostPid)

    $state = [ordered]@{
        hostPid = $HostPid
        lpcPort = $LpcPort
        dashboardPort = $DashboardPort
        nodePort = $NodePort
        csharpPort = $CSharpPort
        keepRunningPort = $KeepRunningPort
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
    Write-Step "Building AspireRunner.Tool for LPC harness"

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

function Get-LpcProcessByName {
    param([string]$Name)

    return @(Get-LpcProcesses | Where-Object { $_.name -eq $Name } | Select-Object -First 1)
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

    $existing = Get-LpcProcessByName -Name $Name
    if ($null -ne $existing) {
        if ($existing.running -and (Wait-PortState -Port $ExpectedPort -ShouldBeListening $true -TimeoutSeconds 3)) {
            Write-Pass -Message "$Name already registered with PID $($existing.processId)"
            return [int]$existing.processId
        }

        Write-Step "Removing stale registration for $Name"
        $deleteResponse = Invoke-LpcRequest -Request @{ command = "Delete"; processId = [int]$existing.processId }
        Assert-True -Condition $deleteResponse.ok -Message "Stale registration removed for $Name"
    }

    Write-Step "Registering managed process: $Name"
    $registerResponse = Invoke-LpcRequest -Request @{
        command = "Register"
        name = $Name
        exe = $Exe
        args = $ArgumentText
        port = $ExpectedPort
        envs = $EnvironmentText
        workingDir = $WorkingDir
    }

    Assert-True -Condition $registerResponse.ok -Message "Register command succeeded for $Name"
    Assert-True -Condition (Wait-PortState -Port $ExpectedPort -ShouldBeListening $true -TimeoutSeconds 40) -Message "$Name is listening on port $ExpectedPort"

    return [int]$registerResponse.processId
}

function Assert-LpcFailure {
    param(
        [hashtable]$Request,
        [string]$Message,
        [string]$ErrorRegex = ".+"
    )

    $response = Invoke-LpcRequest -Request $Request
    Assert-True -Condition (-not $response.ok) -Message $Message
    Assert-True -Condition ([string]$response.error -match $ErrorRegex) -Message "$Message (error message)"
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

    Write-Step "Starting AspireRunner host in background"
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

    return [pscustomobject]@{
        Pid = [int]$hostProcess.Id
        ProcessName = $hostProcess.ProcessName
        StartTimeUtc = $hostProcess.StartTime.ToUniversalTime()
    }
}

Write-Step "Preparing LPC protocol harness"
Ensure-ToolBuild
Ensure-NodeDependencies

$hostInfo = Start-OrAttachHost
$hostPid = [int]$hostInfo.Pid
Write-Pass -Message "Host process is active with PID $hostPid"

$nodeScriptPath = Join-Path $testsDir "app.js"
$csharpProjectPath = Join-Path $testsDir "app.csproj"

$nodeArgs = ('"' + $nodeScriptPath + '"')
$csharpArgs = ('run --project "' + $csharpProjectPath + '"')
$keepArgs = $nodeArgs

$nodePid = Ensure-RegisteredProcess -Name "NodeExpressTestApp" -Exe "node" -ArgumentText $nodeArgs -ExpectedPort $NodePort -WorkingDir $testsDir -EnvironmentText ("PORT={0}" -f $NodePort)
$csharpPid = Ensure-RegisteredProcess -Name "CSharpSingleFileTestApp" -Exe "dotnet" -ArgumentText $csharpArgs -ExpectedPort $CSharpPort -WorkingDir $repoRoot -EnvironmentText ("PORT={0}" -f $CSharpPort)

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

Write-Step "LPC List command"
$listResponse = Invoke-LpcRequest -Request @{ command = "List" }
Assert-True -Condition $listResponse.ok -Message "List command succeeded"
$processNames = @($listResponse.processes | ForEach-Object { $_.name })
Assert-True -Condition ($processNames -contains "NodeExpressTestApp") -Message "List contains NodeExpressTestApp"
Assert-True -Condition ($processNames -contains "CSharpSingleFileTestApp") -Message "List contains CSharpSingleFileTestApp"
Assert-True -Condition ($processNames -contains "Aspire Dashboard") -Message "List contains Aspire Dashboard"

Write-Step "LPC IsAlreadyExist command"
$existsNode = Invoke-LpcRequest -Request @{ command = "IsAlreadyExist"; exe = "node"; args = $nodeArgs }
Assert-True -Condition $existsNode.ok -Message "IsAlreadyExist succeeded for node"
Assert-True -Condition ([bool]$existsNode.exists) -Message "IsAlreadyExist returns true for NodeExpressTestApp"

$existsMissing = Invoke-LpcRequest -Request @{ command = "IsAlreadyExist"; exe = "dotnet"; args = "run --project missing.csproj" }
Assert-True -Condition $existsMissing.ok -Message "IsAlreadyExist succeeded for missing process"
Assert-True -Condition (-not [bool]$existsMissing.exists) -Message "IsAlreadyExist returns false for unknown process"

Write-Step "LPC error handling"
Assert-LpcFailure -Request @{ command = "UnknownCommand" } -Message "Unknown command returns failure" -ErrorRegex "Unknown command"
Assert-LpcFailure -Request @{ command = "IsAlreadyExist" } -Message "IsAlreadyExist validates exe" -ErrorRegex "exe is required"
Assert-LpcFailure -Request @{ command = "Register"; name = "Bad" } -Message "Register validates exe" -ErrorRegex "exe is required"
Assert-LpcFailure -Request @{ command = "Stop" } -Message "Stop validates processId" -ErrorRegex "processId is required"
Assert-LpcFailure -Request @{ command = "Restart" } -Message "Restart validates processId" -ErrorRegex "processId is required"
Assert-LpcFailure -Request @{ command = "Delete" } -Message "Delete validates processId" -ErrorRegex "processId is required"
Assert-LpcFailure -Request @{ command = "Remove" } -Message "Remove validates processId" -ErrorRegex "processId is required"
Assert-LpcFailure -Request @{ command = "Stdin"; processId = $nodePid } -Message "Stdin forwarding is unsupported" -ErrorRegex "not supported"

Write-Step "LPC output streaming"
$nodeStdout = Invoke-LpcRequest -Request @{ command = "Stdout"; processId = $nodePid } -AsStream -MaxLines 10 -TimeoutSeconds 12
Assert-OutputContains -Output $nodeStdout -Regex "Node Express app listening|node-heartbeat" -Message "Stdout stream includes node output"

$nodeStderr = Invoke-LpcRequest -Request @{ command = "Stderr"; processId = $nodePid } -AsStream -MaxLines 6 -TimeoutSeconds 15
Assert-OutputContains -Output $nodeStderr -Regex "node-stderr-heartbeat" -Message "Stderr stream includes node stderr output"

$nodeStdoutLive = Invoke-LpcRequest -Request @{ command = "StdoutLiveOnly"; processId = $nodePid } -AsStream -MaxLines 4 -TimeoutSeconds 10
Assert-OutputContains -Output $nodeStdoutLive -Regex "node-heartbeat" -Message "StdoutLiveOnly includes live node output"

$csharpStderrLive = Invoke-LpcRequest -Request @{ command = "StderrLiveOnly"; processId = $csharpPid } -AsStream -MaxLines 4 -TimeoutSeconds 18
Assert-OutputContains -Output $csharpStderrLive -Regex "csharp-stderr-heartbeat" -Message "StderrLiveOnly includes live C# stderr output"

Write-Step "LPC stop/restart lifecycle for node app"
$stopNode = Invoke-LpcRequest -Request @{ command = "Stop"; processId = $nodePid }
Assert-True -Condition $stopNode.ok -Message "Stop succeeded for NodeExpressTestApp"
Assert-True -Condition (Wait-PortState -Port $NodePort -ShouldBeListening $false -TimeoutSeconds 40) -Message "Node port $NodePort released after stop"

$restartNode = Invoke-LpcRequest -Request @{ command = "Restart"; processId = $nodePid }
Assert-True -Condition $restartNode.ok -Message "Restart succeeded for NodeExpressTestApp"
if ($restartNode.processId) {
    $nodePid = [int]$restartNode.processId
}
Assert-True -Condition (Wait-PortState -Port $NodePort -ShouldBeListening $true -TimeoutSeconds 45) -Message "Node port $NodePort listening after restart"

Write-Step "LPC stop/restart lifecycle for C# app"
$stopCSharp = Invoke-LpcRequest -Request @{ command = "Stop"; processId = $csharpPid }
Assert-True -Condition $stopCSharp.ok -Message "Stop succeeded for CSharpSingleFileTestApp"
Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $false -TimeoutSeconds 45) -Message "C# port $CSharpPort released after stop"

$restartCSharp = Invoke-LpcRequest -Request @{ command = "Restart"; processId = $csharpPid }
Assert-True -Condition $restartCSharp.ok -Message "Restart succeeded for CSharpSingleFileTestApp"
if ($restartCSharp.processId) {
    $csharpPid = [int]$restartCSharp.processId
}
Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $true -TimeoutSeconds 45) -Message "C# port $CSharpPort listening after restart"

Write-Step "LPC Remove command"
$removeNode = Invoke-LpcRequest -Request @{ command = "Remove"; processId = $nodePid }
Assert-True -Condition $removeNode.ok -Message "Remove succeeded for NodeExpressTestApp"
Assert-True -Condition (Wait-PortState -Port $NodePort -ShouldBeListening $false -TimeoutSeconds 45) -Message "Node port $NodePort released after remove"

$processesAfterNodeRemove = @(Get-LpcProcesses)
Assert-True -Condition (-not ($processesAfterNodeRemove | Where-Object { $_.name -eq "NodeExpressTestApp" })) -Message "Node process removed from LPC list"

Write-Step "LPC Delete command"
$deleteCSharp = Invoke-LpcRequest -Request @{ command = "Delete"; processId = $csharpPid }
Assert-True -Condition $deleteCSharp.ok -Message "Delete succeeded for CSharpSingleFileTestApp"
Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $false -TimeoutSeconds 45) -Message "C# port $CSharpPort released after delete"

$processesAfterCSharpDelete = @(Get-LpcProcesses)
Assert-True -Condition (-not ($processesAfterCSharpDelete | Where-Object { $_.name -eq "CSharpSingleFileTestApp" })) -Message "C# process removed from LPC list"
Assert-LpcFailure -Request @{ command = "Stdout"; processId = $csharpPid } -Message "Streaming removed process fails" -ErrorRegex "Process not found"

Write-Step "LPC Remove keepRunning=true semantics"
$keepPid = Ensure-RegisteredProcess -Name "NodeKeepRunningTestApp" -Exe "node" -ArgumentText $keepArgs -ExpectedPort $KeepRunningPort -WorkingDir $testsDir -EnvironmentText ("PORT={0}" -f $KeepRunningPort)
$removeKeepRunning = Invoke-LpcRequest -Request @{ command = "Remove"; processId = $keepPid; keepRunning = $true }
Assert-True -Condition $removeKeepRunning.ok -Message "Remove with keepRunning=true succeeded"

$processesAfterKeepRemove = @(Get-LpcProcesses)
Assert-True -Condition (-not ($processesAfterKeepRemove | Where-Object { $_.name -eq "NodeKeepRunningTestApp" })) -Message "KeepRunning process removed from LPC list"
Assert-True -Condition (Wait-PortState -Port $KeepRunningPort -ShouldBeListening $true -TimeoutSeconds 8) -Message "KeepRunning process still listens on port $KeepRunningPort"

Stop-PortOwnerPids -Port $KeepRunningPort
Assert-True -Condition (Wait-PortState -Port $KeepRunningPort -ShouldBeListening $false -TimeoutSeconds 25) -Message "KeepRunning process port cleaned up after manual stop"

if ($KeepHostRunning) {
    Write-Host ""
    Write-Host "Host is intentionally left running. Re-run with -Resume to continue." -ForegroundColor Magenta
    Write-Host "State file: $statePath" -ForegroundColor Magenta
    exit 0
}

Write-Step "LPC Shutdown command"
$shutdownResponse = Invoke-LpcRequest -Request @{ command = "Shutdown" }
Assert-True -Condition $shutdownResponse.ok -Message "Shutdown command acknowledged"

Assert-True -Condition (Wait-PortState -Port $LpcPort -ShouldBeListening $false -TimeoutSeconds 60) -Message "LPC port $LpcPort released after shutdown"
Assert-True -Condition (Wait-PortState -Port $DashboardPort -ShouldBeListening $false -TimeoutSeconds 60) -Message "Dashboard port $DashboardPort released after shutdown"
Assert-True -Condition (Wait-PortState -Port $NodePort -ShouldBeListening $false -TimeoutSeconds 45) -Message "Node port $NodePort released after shutdown"
Assert-True -Condition (Wait-PortState -Port $CSharpPort -ShouldBeListening $false -TimeoutSeconds 45) -Message "C# port $CSharpPort released after shutdown"
Assert-True -Condition (Wait-PortState -Port $KeepRunningPort -ShouldBeListening $false -TimeoutSeconds 20) -Message "KeepRunning test port $KeepRunningPort released after cleanup"

$hostExited = Wait-HostExit -HostPid $hostPid -ExpectedProcessName $hostInfo.ProcessName -ExpectedStartTimeUtc $hostInfo.StartTimeUtc -TimeoutSeconds 60
Assert-True -Condition ($hostExited -or -not (Test-PortListening -Port $LpcPort)) -Message "Host process exited or detached after shutdown"

if (Test-Path $statePath) {
    Remove-Item -Path $statePath -Force
}

Write-Host ""
Write-Host "All LPC protocol checks passed." -ForegroundColor Green
