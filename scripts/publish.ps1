param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Version,
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent $PSScriptRoot
$project = Join-Path $workspace "src/MoaVcs.App/MoaVcs.App.csproj"
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    $Version = $projectXml.SelectSingleNode("/Project/PropertyGroup/Version").InnerText
}
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw "Version must be a stable numeric version such as 1.2.3."
}

$artifactRoot = [IO.Path]::GetFullPath((Join-Path $workspace "artifacts"))
$publishRoot = [IO.Path]::GetFullPath((Join-Path $artifactRoot "publish"))
$output = [IO.Path]::GetFullPath((Join-Path $publishRoot $Runtime))
$separator = [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($artifactRoot.TrimEnd($separator) + $separator, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to publish outside the artifacts directory: $output"
}

foreach ($candidate in @($artifactRoot, $publishRoot, $output)) {
    if (Test-Path -LiteralPath $candidate) {
        $item = Get-Item -LiteralPath $candidate -Force
        if ($item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to clean a redirected artifact directory: $candidate"
        }
    }
}
if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
New-Item -ItemType Directory -Path $output -Force | Out-Null

$env:MSBuildEnableWorkloadResolver = "false"
$arguments = @(
    "publish", $project,
    "--configuration", $Configuration,
    "--runtime", $Runtime,
    "--output", $output,
    "-m:1",
    "-p:UseSharedCompilation=false",
    "-p:RestoreIgnoreFailedSources=true",
    "-p:Version=$Version",
    "-p:ContinuousIntegrationBuild=true"
)
if ($SelfContained) { $arguments += @("--self-contained", "true") }

Push-Location $workspace
try {
    dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}

if ($Runtime -eq 'win-x64') {
    & (Join-Path $PSScriptRoot 'build-shell.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Native Explorer overlay build failed.' }
}

Write-Host "Published $Runtime ($Version) to $output"
