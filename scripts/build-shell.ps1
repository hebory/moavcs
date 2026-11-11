param([switch]$Test)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$shellProject = Join-Path $workspace 'src/MoaVcs.Shell/MoaVcsShell.vcxproj'
$shellTestProject = Join-Path $workspace 'src/MoaVcs.Shell/MoaVcsShellTest.vcxproj'
$smokeProject = Join-Path $workspace 'src/MoaVcs.Shell/MoaVcsShellSmoke.vcxproj'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts'))
$output = [IO.Path]::GetFullPath((Join-Path $artifactRoot 'shell/win-x64'))
$separator = [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($artifactRoot.TrimEnd($separator) + $separator, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to build outside artifacts: $output"
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw "Visual Studio locator not found: $vswhere" }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installation)) {
    throw 'Visual Studio C++ x64 build tools are required to build the Explorer overlay.'
}
$msbuild = Join-Path $installation 'MSBuild/Current/Bin/MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { throw "MSBuild was not found: $msbuild" }
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/Include'
if (-not (Test-Path -LiteralPath $sdkRoot)) { throw "Windows 10 SDK was not found: $sdkRoot" }
if (-not (Test-Path -LiteralPath (Join-Path $workspace 'assets/explorer-overlays/conflict.ico'))) {
    throw 'Explorer overlay ICO resources have not been generated yet.'
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
$intOutput = Join-Path $artifactRoot 'shell/obj'
foreach ($project in @($shellProject, $shellTestProject, $smokeProject)) {
    $projectObjectDirectory = Join-Path $intOutput ([IO.Path]::GetFileNameWithoutExtension($project))
    New-Item -ItemType Directory -Path $projectObjectDirectory -Force | Out-Null
    & $msbuild $project '/nologo' '/m:1' '/p:Configuration=Release' '/p:Platform=x64' `
        "/p:OutDir=$output\" "/p:IntDir=$projectObjectDirectory\" '/p:UseMultiToolTask=false'
    if ($LASTEXITCODE -ne 0) { throw "Native build failed for $project with exit code $LASTEXITCODE." }
}

$dll = Join-Path $output 'MoaVcsShell.dll'
$testDll = Join-Path $output 'MoaVcsShellTest.dll'
$smoke = Join-Path $output 'MoaVcsShellSmoke.exe'
if (-not (Test-Path -LiteralPath $dll)) { throw "Shell overlay DLL was not produced: $dll" }
if (-not (Test-Path -LiteralPath $testDll)) { throw "Shell overlay test DLL was not produced: $testDll" }
if (-not (Test-Path -LiteralPath $smoke)) { throw "Shell overlay smoke executable was not produced: $smoke" }
if ($Test) {
    & $smoke $testDll
    if ($LASTEXITCODE -ne 0) { throw "Shell overlay smoke tests failed with exit code $LASTEXITCODE." }
}
Write-Host "Built Windows x64 shell overlay: $dll"
