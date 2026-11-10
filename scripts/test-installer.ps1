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
$installDirectory = Join-Path $tempRoot ("moavcs installer smoke-" + [Guid]::NewGuid().ToString("N"))
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
$menus = @(
    @{
        Name = "directory"
        Menu = $menuPaths[0]
        Commands = $commandRoots[0]
        Target = "%1"
        Verbs = @(
            @{ Id = "10Status"; Action = "status" }
            @{ Id = "20Commit"; Action = "commit" }
            @{ Id = "30Update"; Action = "update" }
            @{ Id = "40Push"; Action = "push" }
            @{ Id = "50Log"; Action = "log"; Separator = $true }
            @{ Id = "60Repository"; Action = $null; Separator = $true }
            @{ Id = "70Settings"; Action = "settings"; Separator = $true }
        )
    }
    @{
        Name = "background"
        Menu = $menuPaths[1]
        Commands = $commandRoots[1]
        Target = "%V"
        Verbs = @(
            @{ Id = "10Status"; Action = "status" }
            @{ Id = "20Commit"; Action = "commit" }
            @{ Id = "30Update"; Action = "update" }
            @{ Id = "40Push"; Action = "push" }
            @{ Id = "50Log"; Action = "log"; Separator = $true }
            @{ Id = "60Repository"; Action = $null; Separator = $true }
            @{ Id = "70Settings"; Action = "settings"; Separator = $true }
        )
    }
    @{
        Name = "file"
        Menu = $menuPaths[2]
        Commands = $commandRoots[2]
        Target = "%1"
        Verbs = @(
            @{ Id = "10Diff"; Action = "diff" }
            @{ Id = "20Blame"; Action = "blame" }
            @{ Id = "30Log"; Action = "log" }
            @{ Id = "40Status"; Action = "status"; Separator = $true }
            @{ Id = "50Commit"; Action = "commit" }
            @{ Id = "60Open"; Action = "open"; Separator = $true }
            @{ Id = "70Settings"; Action = "settings"; Separator = $true }
        )
    }
)

function Assert-RegistryValue {
    param([string]$KeyPath, [string]$Name, [object]$Expected, [Microsoft.Win32.RegistryValueKind]$Kind)
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($KeyPath)
    try {
        if ($null -eq $key) { throw "Missing registry key HKCU:\$KeyPath." }
        $actual = $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $actualKind = $key.GetValueKind($Name)
        if ($actual -cne $Expected -or $actualKind -ne $Kind) {
            throw "Unexpected HKCU:\$KeyPath value '$Name': '$actual' ($actualKind), expected '$Expected' ($Kind)."
        }
    } finally {
        if ($null -ne $key) { $key.Dispose() }
    }
}

function Assert-Registration {
    param([string]$ApplicationPath)
    $quotedApplicationPath = [char]34 + $ApplicationPath + [char]34
    foreach ($menu in $menus) {
        Assert-RegistryValue $menu.Menu "MoaVcsOwner" "MoaVcs.ExplorerMenu.v1" ([Microsoft.Win32.RegistryValueKind]::String)
        Assert-RegistryValue $menu.Menu "Icon" ($quotedApplicationPath + ",0") ([Microsoft.Win32.RegistryValueKind]::String)
        Assert-RegistryValue $menu.Menu "MultiSelectModel" "Single" ([Microsoft.Win32.RegistryValueKind]::String)

        Assert-RegistryValue $menu.Commands "MoaVcsOwner" "MoaVcs.ExplorerMenu.v1" ([Microsoft.Win32.RegistryValueKind]::String)
        Assert-RegistryValue $menu.Commands "MoaVcsExecutable" $ApplicationPath ([Microsoft.Win32.RegistryValueKind]::String)
        $shellPath = "$($menu.Commands)\Shell"
        $shell = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($shellPath)
        try {
            if ($null -eq $shell) { throw "Missing command list at HKCU:\$shellPath." }
            $actualIds = @($shell.GetSubKeyNames() | Sort-Object -CaseSensitive)
            $expectedIds = @($menu.Verbs | ForEach-Object Id | Sort-Object -CaseSensitive)
            if (Compare-Object -ReferenceObject $expectedIds -DifferenceObject $actualIds) {
                throw "Unexpected $($menu.Name) menu verbs: $($actualIds -join ', ')."
            }
        } finally {
            if ($null -ne $shell) { $shell.Dispose() }
        }

        foreach ($verb in $menu.Verbs) {
            $verbPath = "$shellPath\$($verb.Id)"
            $labelKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($verbPath)
            try {
                if ($null -eq $labelKey -or [string]::IsNullOrWhiteSpace([string]$labelKey.GetValue("MUIVerb"))) {
                    throw "Missing display label for $($menu.Name) verb $($verb.Id)."
                }
                if ($labelKey.GetValue("MultiSelectModel") -cne "Single" -or
                    $labelKey.GetValueKind("MultiSelectModel") -ne [Microsoft.Win32.RegistryValueKind]::String) {
                    throw "Expected Single selection on $($menu.Name) verb $($verb.Id)."
                }
                if ($verb.Separator) {
                    $flags = $labelKey.GetValue("CommandFlags")
                    if ($labelKey.GetValueKind("CommandFlags") -ne [Microsoft.Win32.RegistryValueKind]::DWord -or $flags -ne 32) {
                        throw "Expected a DWORD separator flag on $($menu.Name) verb $($verb.Id)."
                    }
                } elseif ($labelKey.GetValueNames() -contains "CommandFlags") {
                    throw "Unexpected separator flag on $($menu.Name) verb $($verb.Id)."
                }
            } finally {
                if ($null -ne $labelKey) { $labelKey.Dispose() }
            }

            if ($verb.Id -eq "60Repository") {
                $relativeCommands = "$($menu.Commands.Substring('Software\Classes\'.Length))\Shell\60Repository\Commands"
                Assert-RegistryValue $verbPath "ExtendedSubCommandsKey" $relativeCommands ([Microsoft.Win32.RegistryValueKind]::String)
                $nestedShellPath = "$shellPath\60Repository\Commands\Shell"
                $nestedShell = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($nestedShellPath)
                try {
                    if ($null -eq $nestedShell) { throw "Missing nested repository menu at HKCU:\$nestedShellPath." }
                    $nestedIds = @($nestedShell.GetSubKeyNames() | Sort-Object -CaseSensitive)
                    if (Compare-Object -ReferenceObject @("10Open", "20Clone", "30Init") -DifferenceObject $nestedIds) {
                        throw "Unexpected nested repository actions for $($menu.Name): $($nestedIds -join ', ')."
                    }
                } finally {
                    if ($null -ne $nestedShell) { $nestedShell.Dispose() }
                }
                foreach ($nestedVerb in @(
                    @{ Id = "10Open"; Action = "open" }
                    @{ Id = "20Clone"; Action = "clone" }
                    @{ Id = "30Init"; Action = "init" }
                )) {
                    $nestedLabelPath = "$nestedShellPath\$($nestedVerb.Id)"
                    $nestedLabelKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($nestedLabelPath)
                    try {
                        if ($null -eq $nestedLabelKey -or [string]::IsNullOrWhiteSpace([string]$nestedLabelKey.GetValue("MUIVerb"))) {
                            throw "Missing label for nested action $($nestedVerb.Action) in $($menu.Name)."
                        }
                        if ($nestedLabelKey.GetValue("MultiSelectModel") -cne "Single" -or
                            $nestedLabelKey.GetValueKind("MultiSelectModel") -ne [Microsoft.Win32.RegistryValueKind]::String) {
                            throw "Expected Single selection on nested action $($nestedVerb.Action) in $($menu.Name)."
                        }
                    } finally {
                        if ($null -ne $nestedLabelKey) { $nestedLabelKey.Dispose() }
                    }
                    $nestedCommandPath = "$nestedShellPath\$($nestedVerb.Id)\command"
                    $nestedCommandKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($nestedCommandPath)
                    try {
                        $nestedCommand = if ($null -ne $nestedCommandKey) { [string]$nestedCommandKey.GetValue("") } else { $null }
                        if ($nestedCommand -notlike "$quotedApplicationPath --shell-path `"$($menu.Target)|`" --action $($nestedVerb.Action)*") {
                            throw "Unexpected nested command for $($menu.Name) action $($nestedVerb.Action): $nestedCommand"
                        }
                    } finally {
                        if ($null -ne $nestedCommandKey) { $nestedCommandKey.Dispose() }
                    }
                }
                continue
            }
            $commandKeyPath = "$verbPath\command"
            $commandKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($commandKeyPath)
            try {
                $command = if ($null -ne $commandKey) { [string]$commandKey.GetValue("") } else { $null }
                if ($command -notlike "$quotedApplicationPath --shell-path `"$($menu.Target)|`"*") {
                    throw "Unexpected shell command for $($menu.Name) verb $($verb.Id): $command"
                }
                if ($command -notlike "*--action $($verb.Action)*") {
                    throw "Wrong action for $($menu.Name) verb $($verb.Id): $command"
                }
            } finally {
                if ($null -ne $commandKey) { $commandKey.Dispose() }
            }
        }
    }
}

function Assert-LegacyVerbsRemoved {
    foreach ($root in $commandRoots) {
        foreach ($id in @("Open", "Diff")) {
            $path = "Registry::HKEY_CURRENT_USER\$root\Shell\$id"
            if (Test-Path -LiteralPath $path) { throw "Upgrade left stale legacy verb HKCU:\$root\Shell\$id." }
        }
    }
}

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
    Assert-Registration $app

    # Simulate keys left by the prior unnumbered menu schema, then run the app's
    # owned-key registration path as an in-place upgrade would.
    foreach ($root in $commandRoots) {
        foreach ($id in @("Open", "Diff")) {
            $legacy = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$root\Shell\$id\command")
            try { $legacy.SetValue("", "legacy command", [Microsoft.Win32.RegistryValueKind]::String) }
            finally { $legacy.Dispose() }
        }
    }
    $register = Start-Process -FilePath $app -ArgumentList @("--install-context-menu", "--headless") -Wait -PassThru -WindowStyle Hidden
    if ($register.ExitCode -ne 0) { throw "Menu upgrade registration returned exit code $($register.ExitCode)." }
    Assert-Registration $app
    Assert-LegacyVerbsRemoved

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
