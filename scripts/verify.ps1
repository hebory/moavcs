param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Push-Location $workspace
try {
    cargo fmt --all -- --check
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $profile = @()
    $profileDirectory = 'debug'
    if ($Configuration -eq 'Release') { $profile = @('--release'); $profileDirectory = 'release' }
    cargo test --workspace --locked @profile
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    cargo build --workspace --locked @profile
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $app = Join-Path $workspace "target/$profileDirectory/moavcs.exe"
    & $app --version | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $output = Join-Path $workspace 'artifacts/screenshots/rust'
    $completion = Join-Path $output 'complete'
    if (Test-Path -LiteralPath $completion) { Remove-Item -LiteralPath $completion -Force }
    & $app --ui-smoke $output | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Rust GUI smoke failed.' }
    if (-not (Test-Path -LiteralPath $completion)) { throw 'Rust GUI smoke did not finish all scenarios.' }
} finally { Pop-Location }
