[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$version = '0.13.0'
$expectedHash = '26b84459fd6ab2faa10d5f8eb8393848286935414210d7070dafffd97e4c1343'
$workspace = Split-Path -Parent $PSScriptRoot
$folder = Join-Path $workspace ".local\test-tools\lore-$version"
$archive = Join-Path $folder "lore-v$version-x86_64-pc-windows-msvc.zip"
New-Item -ItemType Directory -Path $folder -Force | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri "https://github.com/EpicGames/lore/releases/download/v$version/lore-v$version-x86_64-pc-windows-msvc.zip" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) {
    throw "Lore test archive checksum mismatch: $archive"
}
# Extract only the CLI used by the tests; do not install it or change PATH/user settings.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $entry = $zip.GetEntry('lore.exe')
    if ($null -eq $entry) { throw 'The pinned Lore archive does not contain lore.exe.' }
    $executable = Join-Path $folder 'lore.exe'
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $executable, $true)
} finally { $zip.Dispose() }
Write-Output $executable
