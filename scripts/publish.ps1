param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent $PSScriptRoot
$env:MSBuildEnableWorkloadResolver = "false"
$output = Join-Path $workspace "artifacts/$Runtime"
$arguments = @("publish", (Join-Path $workspace "src/MoaVcs.App/MoaVcs.App.csproj"), "--configuration", $Configuration, "--runtime", $Runtime, "--output", $output, "-m:1", "-p:UseSharedCompilation=false", "-p:RestoreIgnoreFailedSources=true")
if ($SelfContained) { $arguments += @("--self-contained", "true") }
dotnet @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "moavcs output: $output"
