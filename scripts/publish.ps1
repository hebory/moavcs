param([ValidateSet('win-x64')][string]$Runtime = 'win-x64', [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [string]$Version, [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$declaredVersion = & (Join-Path $PSScriptRoot 'version.ps1')
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $declaredVersion }
if ($Version -ne $declaredVersion) { throw "Package version $Version must match Cargo workspace version $declaredVersion." }
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts'))
$output = [IO.Path]::GetFullPath((Join-Path $artifactRoot "publish/$Runtime"))
if (-not $output.StartsWith($artifactRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid publish path.' }
foreach ($candidate in @($artifactRoot, (Join-Path $artifactRoot 'publish'), $output)) {
    if (Test-Path -LiteralPath $candidate) {
        if ((Get-Item -LiteralPath $candidate -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw "Refusing redirected publish path: $candidate" }
    }
}
Push-Location $workspace
try {
    $profile = @()
    $profileDirectory = 'debug'
    if ($Configuration -eq 'Release') { $profile = @('--release'); $profileDirectory = 'release' }
    cargo build --locked --workspace @profile
    if ($LASTEXITCODE -ne 0) { throw "Rust $Configuration build failed." }
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    New-Item -ItemType Directory -Path (Join-Path $output 'shell') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $output 'icons/explorer') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $workspace "target/$profileDirectory/moavcs.exe") -Destination $output
    Copy-Item -LiteralPath (Join-Path $workspace "target/$profileDirectory/MoaVcsShell.dll") -Destination (Join-Path $output 'shell')
    Get-ChildItem -LiteralPath (Join-Path $workspace 'assets/explorer-icons') -Filter '*.ico' | Copy-Item -Destination (Join-Path $output 'icons/explorer')
    Copy-Item -LiteralPath (Join-Path $workspace 'icon.png'), (Join-Path $workspace 'cover.png') -Destination $output
    Write-Host "Published Rust Windows x64 app ($Version) to $output"
} finally { Pop-Location }
