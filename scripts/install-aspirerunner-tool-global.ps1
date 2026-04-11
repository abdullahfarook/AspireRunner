[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$PrereleaseLabel = "iter"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$toolProjectPath = Join-Path $repoRoot "src\AspireRunner.Tool\AspireRunner.Tool.csproj"
$packageOutputPath = Join-Path $repoRoot "dist\nupkg"
$packageId = "AspireRunner.Tool"

if (-not (Test-Path $toolProjectPath)) {
    throw "Could not find tool project at '$toolProjectPath'."
}

[xml]$toolProjectXml = Get-Content -Raw -Path $toolProjectPath
$versionNode = $toolProjectXml.SelectSingleNode("//Project/PropertyGroup/Version[string-length(normalize-space(text())) > 0]")

if ($null -eq $versionNode) {
    throw "Could not find a base <Version> in '$toolProjectPath'."
}

$baseVersion = $versionNode.InnerText.Trim()
$timestamp = Get-Date -Format "yyyyMMddHHmmssfff"
$nonce = Get-Random -Minimum 100 -Maximum 999
$newVersion = "$baseVersion-$PrereleaseLabel.$timestamp.$PID.$nonce"

Write-Host "Base version : $baseVersion"
Write-Host "Build version: $newVersion"
Write-Host "Building '$packageId'..."

New-Item -ItemType Directory -Path $packageOutputPath -Force | Out-Null

$buildArgs = @(
    "build",
    $toolProjectPath,
    "-c", $Configuration,
    "/p:Version=$newVersion"
)

& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

Write-Host "Packing '$packageId'..."

$packArgs = @(
    "pack",
    $toolProjectPath,
    "-c", $Configuration,
    "--no-build",
    "/p:Version=$newVersion",
    "--output", $packageOutputPath
)

& dotnet @packArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet pack failed with exit code $LASTEXITCODE."
}

$packagePath = Join-Path $packageOutputPath "$packageId.$newVersion.nupkg"
if (-not (Test-Path $packagePath)) {
    throw "Expected package '$packagePath' was not produced."
}

$globalToolsOutput = & dotnet tool list --global
if ($LASTEXITCODE -ne 0) {
    throw "Failed to list global tools (exit code $LASTEXITCODE)."
}

$alreadyInstalled = $globalToolsOutput | Select-String -Pattern "^\s*$([regex]::Escape($packageId))\s+" -Quiet

if ($alreadyInstalled) {
    Write-Host "Updating global tool '$packageId' to version '$newVersion'..."
    $toolArgs = @(
        "tool", "update", "--global", $packageId,
        "--version", $newVersion,
        "--add-source", $packageOutputPath,
        "--ignore-failed-sources"
    )
}
else {
    Write-Host "Installing global tool '$packageId' version '$newVersion'..."
    $toolArgs = @(
        "tool", "install", "--global", $packageId,
        "--version", $newVersion,
        "--add-source", $packageOutputPath,
        "--ignore-failed-sources"
    )
}

& dotnet @toolArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet tool install/update failed with exit code $LASTEXITCODE."
}

Write-Host "Done."
Write-Host "Command: aspire-dashboard"
Write-Host "Version: $newVersion"
