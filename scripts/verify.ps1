param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$env:MSBuildEnableWorkloadResolver = 'false'
Push-Location $workspace
try {
    dotnet build MoaVcs.sln --configuration $Configuration -m:1 -p:UseSharedCompilation=false -p:RestoreIgnoreFailedSources=true --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet run --project tests/MoaVcs.Tests --configuration $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet run --project tests/MoaVcs.UiSmoke --configuration $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally { Pop-Location }
