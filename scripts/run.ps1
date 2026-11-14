param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug', [string[]]$AppArguments = @())
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Push-Location $workspace
try {
    $arguments = @('run', '--locked', '-p', 'moavcs-app')
    if ($Configuration -eq 'Release') { $arguments += '--release' }
    cargo @arguments -- @AppArguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally { Pop-Location }
