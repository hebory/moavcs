param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string[]]$AppArguments = @()
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent $PSScriptRoot
$env:MSBuildEnableWorkloadResolver = "false"
$project = Join-Path $workspace "src/MoaVcs.App/MoaVcs.App.csproj"
dotnet build $project --configuration $Configuration -m:1 -p:UseSharedCompilation=false -p:RestoreIgnoreFailedSources=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$app = Join-Path $workspace "src/MoaVcs.App/bin/$Configuration/net8.0-windows/moavcs.exe"
if (-not (Test-Path -LiteralPath $app)) { throw "앱 실행 파일을 찾을 수 없습니다: $app" }
& $app @AppArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
