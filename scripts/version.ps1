$ErrorActionPreference = 'Stop'
$manifest = Join-Path (Split-Path -Parent $PSScriptRoot) 'Cargo.toml'
$match = [regex]::Match((Get-Content -LiteralPath $manifest -Raw), '(?m)^version\s*=\s*"(?<version>\d+\.\d+\.\d+)"')
if (-not $match.Success) { throw 'Cargo workspace version was not found.' }
$match.Groups['version'].Value
