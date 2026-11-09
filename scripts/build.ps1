param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent $PSScriptRoot
$env:MSBuildEnableWorkloadResolver = "false"
dotnet build (Join-Path $workspace "src/MoaVcs.App/MoaVcs.App.csproj") --configuration $Configuration -m:1 -p:UseSharedCompilation=false -p:RestoreIgnoreFailedSources=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
