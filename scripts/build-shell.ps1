param([switch]$Test)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Push-Location $workspace
try {
    cargo build --locked --release -p moavcs-shell
    if ($LASTEXITCODE -ne 0) { throw 'Rust Explorer overlay build failed.' }
    if ($Test) {
        cargo test --locked -p moavcs-shell
        if ($LASTEXITCODE -ne 0) { throw 'Rust Explorer overlay COM tests failed.' }
    }
    $output = Join-Path $workspace 'artifacts/publish/win-x64/shell'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $workspace 'target/release/MoaVcsShell.dll') -Destination $output -Force
    Write-Host "Built Rust Windows x64 shell overlay: $output\MoaVcsShell.dll"
} finally { Pop-Location }
