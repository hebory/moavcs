param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath
)

$ErrorActionPreference = "Stop"
if ($env:GITHUB_ACTIONS -ne "true" -or [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw "This smoke test installs software and edits the current-user Explorer registry. Run it only on an isolated GitHub Actions Windows runner."
}

$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$tempRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP)
$installDirectory = Join-Path $tempRoot ("moavcs-installer-smoke-" + [Guid]::NewGuid().ToString("N"))
if (-not $installDirectory.StartsWith($tempRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to install outside RUNNER_TEMP."
}

$menuPaths = @(
    "Software\Classes\Directory\shell\MoaVcs",
    "Software\Classes\Directory\Background\shell\MoaVcs",
    "Software\Classes\*\shell\MoaVcs"
)
$commandRoots = @(
    "Software\Classes\Directory\ContextMenus\MoaVcs.Commands",
    "Software\Classes\Directory\ContextMenus\MoaVcs.BackgroundCommands",
    "Software\Classes\*\ContextMenus\MoaVcs.FileCommands"
)
$setupAttempted = $false
try {
    $quotedInstallDirectory = [char]34 + $installDirectory + [char]34
    $arguments = @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
        "/DIR=$quotedInstallDirectory", "/TASKS=explorermenu"
    )
    $setupAttempted = $true
    $setup = Start-Process -FilePath $installer -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($setup.ExitCode -ne 0) { throw "Silent installer returned exit code $($setup.ExitCode)." }

    $app = Join-Path $installDirectory "moavcs.exe"
    if (-not (Test-Path -LiteralPath $app)) { throw "The installer did not install moavcs.exe." }
    foreach ($path in $menuPaths) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path)
        try {
            if ($null -eq $key -or $key.GetValue("MoaVcsOwner") -ne "MoaVcs.ExplorerMenu.v1") {
                throw "Explorer menu was not registered under HKCU:\$path."
            }
        } finally {
            if ($null -ne $key) { $key.Dispose() }
        }
    }
    foreach ($path in $commandRoots) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$path\Shell\Open\command")
        try {
            $command = if ($null -ne $key) { $key.GetValue("") } else { $null }
            if ($command -notlike "*--shell-path*") { throw "Explorer command is missing for HKCU:\$path." }
        } finally {
            if ($null -ne $key) { $key.Dispose() }
        }
    }
    $uninstaller = Join-Path $installDirectory "unins000.exe"
    if (-not (Test-Path -LiteralPath $uninstaller)) { throw "The per-user uninstaller was not created." }
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") -Wait -PassThru -WindowStyle Hidden
    if ($uninstall.ExitCode -ne 0) { throw "Silent uninstaller returned exit code $($uninstall.ExitCode)." }
    foreach ($path in $menuPaths + $commandRoots) {
        if (Test-Path -LiteralPath "Registry::HKEY_CURRENT_USER\$path") {
            throw "Uninstall left an Explorer menu key behind: HKCU:\$path."
        }
    }
} finally {
    if ($setupAttempted -and (Test-Path -LiteralPath $installDirectory)) {
        $uninstaller = Join-Path $installDirectory "unins000.exe"
        if (Test-Path -LiteralPath $uninstaller) {
            $cleanup = Start-Process -FilePath $uninstaller -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") -Wait -PassThru -WindowStyle Hidden
            if ($cleanup.ExitCode -ne 0) { Write-Warning "Cleanup uninstaller exited with code $($cleanup.ExitCode)." }
        }
    }
    if (Test-Path -LiteralPath $installDirectory) {
        Remove-Item -LiteralPath $installDirectory -Recurse -Force
    }
}

Write-Host "Installer smoke test passed."
