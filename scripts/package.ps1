param(
    [string]$Version,
    [ValidateSet("Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent $PSScriptRoot
$project = Join-Path $workspace "src/MoaVcs.App/MoaVcs.App.csproj"
$installerScript = Join-Path $workspace "installer/MoaVcs.iss"
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $workspace "artifacts"))
$publishOutput = [IO.Path]::GetFullPath((Join-Path $artifactRoot "publish/win-x64"))
$releaseOutput = [IO.Path]::GetFullPath((Join-Path $artifactRoot "release"))

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    $Version = $projectXml.SelectSingleNode("/Project/PropertyGroup/Version").InnerText
}
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw "Version must be a stable numeric version such as 1.2.3."
}

$separator = [IO.Path]::DirectorySeparatorChar
if (Test-Path -LiteralPath $artifactRoot) {
    $rootItem = Get-Item -LiteralPath $artifactRoot -Force
    if ($rootItem.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to use a redirected artifacts directory: $artifactRoot"
    }
}
foreach ($candidate in @($publishOutput, $releaseOutput)) {
    if (-not $candidate.StartsWith($artifactRoot.TrimEnd($separator) + $separator, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to package outside the artifacts directory: $candidate"
    }
    if (Test-Path -LiteralPath $candidate) {
        $item = Get-Item -LiteralPath $candidate -Force
        if ($item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to use a redirected artifacts directory: $candidate"
        }
    }
}

$setupName = "moavcs-$Version-win-x64-setup.exe"
$portableName = "moavcs-$Version-win-x64-portable.zip"
$checksumsName = "SHA256SUMS.txt"
$setupPath = Join-Path $releaseOutput $setupName
$portablePath = Join-Path $releaseOutput $portableName
$checksumsPath = Join-Path $releaseOutput $checksumsName

New-Item -ItemType Directory -Path $releaseOutput -Force | Out-Null
foreach ($oldFile in @($setupPath, $portablePath, $checksumsPath)) {
    if (Test-Path -LiteralPath $oldFile) {
        $item = Get-Item -LiteralPath $oldFile -Force
        if ($item.PSIsContainer -or $item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to replace an unexpected release asset: $oldFile"
        }
        Remove-Item -LiteralPath $oldFile -Force
    }
}

$publishScript = Join-Path $PSScriptRoot "publish.ps1"
& $publishScript -Runtime "win-x64" -Configuration $Configuration -Version $Version -SelfContained
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Publishing the self-contained app failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath (Join-Path $publishOutput "moavcs.exe"))) {
    throw "The self-contained publish output is missing moavcs.exe: $publishOutput"
}

Compress-Archive -Path (Join-Path $publishOutput "*") -DestinationPath $portablePath -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $portablePath)) { throw "The portable archive was not created." }

$compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($null -eq $compiler) {
    $programFilesX86 = [Environment]::GetFolderPath("ProgramFilesX86")
    $programFiles = [Environment]::GetFolderPath("ProgramFiles")
    $candidatePaths = @(
        (Join-Path $programFilesX86 "Inno Setup 6/ISCC.exe"),
        (Join-Path $programFiles "Inno Setup 6/ISCC.exe"),
        (Join-Path $programFiles "Inno Setup 7/ISCC.exe")
    )
    $compilerPath = $candidatePaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($compilerPath)) {
        throw "Inno Setup Compiler (ISCC.exe) was not found. Install it with: choco install innosetup --yes --no-progress"
    }
} else {
    $compilerPath = $compiler.Source
}

Push-Location $workspace
try {
    & $compilerPath "/DAppVersion=$Version" "/Qp" $installerScript
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}
if (-not (Test-Path -LiteralPath $setupPath)) { throw "The installer was not created: $setupPath" }

$checksumLines = foreach ($assetPath in @($setupPath, $portablePath)) {
    $hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $name = Split-Path -Leaf $assetPath
    "$hash *$name"
}
Set-Content -LiteralPath $checksumsPath -Value $checksumLines -Encoding ascii

Write-Host "Package assets:"
Write-Host "  $setupPath"
Write-Host "  $portablePath"
Write-Host "  $checksumsPath"
