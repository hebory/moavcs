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
$settingsDirectory = Join-Path ([System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::LocalApplicationData)) "MoaVcs"
$settingsPath = Join-Path $settingsDirectory "settings.json"
$settingsBackupPath = Join-Path $tempRoot ("moavcs settings backup-" + [Guid]::NewGuid().ToString("N") + ".json")
$settingsDirectoryExisted = Test-Path -LiteralPath $settingsDirectory -PathType Container
$settingsExisted = Test-Path -LiteralPath $settingsPath -PathType Leaf
if ($settingsExisted) { [IO.File]::Copy($settingsPath, $settingsBackupPath) }

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
$quickRoots = @(
    "Software\Classes\Directory\shell\MoaVcs.Commit",
    "Software\Classes\Directory\shell\MoaVcs.Update",
    "Software\Classes\Directory\Background\shell\MoaVcs.Commit",
    "Software\Classes\Directory\Background\shell\MoaVcs.Update",
    "Software\Classes\*\shell\MoaVcs.Commit"
)
$menus = @(
    @{
        Name = "directory"
        Menu = $menuPaths[0]
        Commands = $commandRoots[0]
        Target = "%1"
        Verbs = @(
            @{ Id = "20Push"; Action = "push" }
            @{ Id = "40Log"; Action = "log"; Separator = $true }
            @{ Id = "50Status"; Action = "status" }
            @{ Id = "60Open"; Action = "open"; Separator = $true }
            @{ Id = "70Clone"; Action = "clone" }
            @{ Id = "80Init"; Action = "init" }
            @{ Id = "85Remote"; Action = "remote"; Label = "원격 주소…" }
            @{ Id = "90Settings"; Action = "settings"; Separator = $true }
        )
    }
    @{
        Name = "background"
        Menu = $menuPaths[1]
        Commands = $commandRoots[1]
        Target = "%V"
        Verbs = @(
            @{ Id = "20Push"; Action = "push" }
            @{ Id = "40Log"; Action = "log"; Separator = $true }
            @{ Id = "50Status"; Action = "status" }
            @{ Id = "60Open"; Action = "open"; Separator = $true }
            @{ Id = "70Clone"; Action = "clone" }
            @{ Id = "80Init"; Action = "init" }
            @{ Id = "85Remote"; Action = "remote"; Label = "원격 주소…" }
            @{ Id = "90Settings"; Action = "settings"; Separator = $true }
        )
    }
    @{
        Name = "file"
        Menu = $menuPaths[2]
        Commands = $commandRoots[2]
        Target = "%1"
        Verbs = @(
            @{ Id = "20Diff"; Action = "diff" }
            @{ Id = "30Log"; Action = "log"; Separator = $true }
            @{ Id = "40Blame"; Action = "blame" }
            @{ Id = "50Status"; Action = "status" }
            @{ Id = "60Open"; Action = "open"; Separator = $true }
            @{ Id = "85Remote"; Action = "remote"; Label = "원격 주소…" }
            @{ Id = "90Settings"; Action = "settings"; Separator = $true }
        )
    }
)
$quickActions = @(
    @{ Root = $quickRoots[0]; Label = "MoaVCS 커밋…"; Action = "commit"; Target = "%1" }
    @{ Root = $quickRoots[1]; Label = "MoaVCS 업데이트 / Pull…"; Action = "update"; Target = "%1" }
    @{ Root = $quickRoots[2]; Label = "MoaVCS 커밋…"; Action = "commit"; Target = "%V" }
    @{ Root = $quickRoots[3]; Label = "MoaVCS 업데이트 / Pull…"; Action = "update"; Target = "%V" }
    @{ Root = $quickRoots[4]; Label = "MoaVCS 커밋…"; Action = "commit"; Target = "%1" }
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

function Assert-RegistryValueAbsent {
    param([string]$KeyPath, [string]$Name)
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($KeyPath)
    try {
        if ($null -ne $key -and $key.GetValueNames() -contains $Name) {
            throw "Unexpected HKCU:\$KeyPath value '$Name'; it must be absent."
        }
    } finally {
        if ($null -ne $key) { $key.Dispose() }
    }
}

function Assert-Registration {
    param([string]$ApplicationPath)
    $quotedApplicationPath = [char]34 + $ApplicationPath + [char]34
    foreach ($menu in $menus) {
        Assert-RegistryValue $menu.Menu "MUIVerb" "MoaVCS" ([Microsoft.Win32.RegistryValueKind]::String)
        Assert-RegistryValue $menu.Menu "MoaVcsOwner" "MoaVcs.ExplorerMenu.v1" ([Microsoft.Win32.RegistryValueKind]::String)
        Assert-RegistryValue $menu.Menu "Icon" ($quotedApplicationPath + ",0") ([Microsoft.Win32.RegistryValueKind]::String)
        if ($menu.Name -eq "background") {
            Assert-RegistryValueAbsent $menu.Menu "MultiSelectModel"
        } else {
            Assert-RegistryValue $menu.Menu "MultiSelectModel" "Single" ([Microsoft.Win32.RegistryValueKind]::String)
        }
        Assert-RegistryValue $menu.Menu "ExtendedSubCommandsKey" $menu.Commands.Substring('Software\Classes\'.Length) ([Microsoft.Win32.RegistryValueKind]::String)

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
                if ($verb.ContainsKey("Label") -and $labelKey.GetValue("MUIVerb") -cne $verb.Label) {
                    throw "Unexpected label for $($menu.Name) verb $($verb.Id)."
                }
                if ($menu.Name -eq "background") {
                    if ($labelKey.GetValueNames() -contains "MultiSelectModel") {
                        throw "Background verb $($verb.Id) must omit MultiSelectModel."
                    }
                } elseif ($labelKey.GetValue("MultiSelectModel") -cne "Single" -or
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

            $commandPath = "$verbPath\command"
            $expectedCommand = "$quotedApplicationPath --shell-path `"$($menu.Target)|`" --action $($verb.Action)"
            Assert-RegistryValue $commandPath "" $expectedCommand ([Microsoft.Win32.RegistryValueKind]::String)
        }
    }

    foreach ($quick in $quickActions) {
        Assert-RegistryValue $quick.Root "MUIVerb" $quick.Label ([Microsoft.Win32.RegistryValueKind]::String)
        Assert-RegistryValue $quick.Root "MoaVcsOwner" "MoaVcs.ExplorerMenu.v1" ([Microsoft.Win32.RegistryValueKind]::String)
        Assert-RegistryValue $quick.Root "MoaVcsExecutable" $ApplicationPath ([Microsoft.Win32.RegistryValueKind]::String)
        Assert-RegistryValue $quick.Root "Icon" ($quotedApplicationPath + ",0") ([Microsoft.Win32.RegistryValueKind]::String)
        if ($quick.Root.StartsWith("Software\Classes\Directory\Background\", [StringComparison]::OrdinalIgnoreCase)) {
            Assert-RegistryValueAbsent $quick.Root "MultiSelectModel"
        } else {
            Assert-RegistryValue $quick.Root "MultiSelectModel" "Single" ([Microsoft.Win32.RegistryValueKind]::String)
        }
        $expectedCommand = "$quotedApplicationPath --shell-path `"$($quick.Target)|`" --action $($quick.Action)"
        Assert-RegistryValue "$($quick.Root)\command" "" $expectedCommand ([Microsoft.Win32.RegistryValueKind]::String)
    }
}

function Assert-LegacyVerbsRemoved {
    foreach ($root in $commandRoots) {
        foreach ($id in @("Open", "Diff")) {
            $path = "Registry::HKEY_CURRENT_USER\$root\Shell\$id"
            if (Test-Path -LiteralPath $path) { throw "Upgrade left stale legacy verb HKCU:\$root\Shell\$id." }
        }
        $nestedPath = "Registry::HKEY_CURRENT_USER\$root\Shell\60Repository"
        if (Test-Path -LiteralPath $nestedPath) { throw "Upgrade left the old nested repository submenu at HKCU:\$root\Shell\60Repository." }
    }
}

function Set-TestExplorerMenuSettings {
    param([bool]$ShowCommitAtTopLevel, [bool]$ShowUpdateAtTopLevel)
    [IO.Directory]::CreateDirectory($settingsDirectory) | Out-Null
    $settings = @{
        ExplorerMenu = @{
            ShowCommitAtTopLevel = $ShowCommitAtTopLevel
            ShowUpdateAtTopLevel = $ShowUpdateAtTopLevel
        }
    } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText($settingsPath, $settings, [Text.UTF8Encoding]::new($false))
}

function Assert-QuickActionsDemoted {
    param([string]$ApplicationPath)
    foreach ($root in $quickRoots) {
        if (Test-Path -LiteralPath "Registry::HKEY_CURRENT_USER\$root") {
            throw "Quick action should be hidden when both top-level options are disabled: HKCU:\$root."
        }
    }
    $quotedApplicationPath = [char]34 + $ApplicationPath + [char]34
    foreach ($menu in $menus) {
        $shellPath = "$($menu.Commands)\Shell"
        $shell = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($shellPath)
        try {
            if ($null -eq $shell) { throw "Missing submenu at HKCU:\$shellPath." }
            $actualIds = @($shell.GetSubKeyNames() | Sort-Object -CaseSensitive)
            $expectedIds = if ($menu.Name -eq "file") {
                @("10Commit", "20Diff", "30Log", "40Blame", "50Status", "60Open", "85Remote", "90Settings")
            } else {
                @("10Update", "20Push", "30Commit", "40Log", "50Status", "60Open", "70Clone", "80Init", "85Remote", "90Settings")
            }
            $expectedIds = @($expectedIds | Sort-Object -CaseSensitive)
            if (Compare-Object -ReferenceObject $expectedIds -DifferenceObject $actualIds) {
                throw "Quick actions were not restored to the $($menu.Name) cascade: $($actualIds -join ', ')."
            }
        } finally {
            if ($null -ne $shell) { $shell.Dispose() }
        }
        $demoted = if ($menu.Name -eq "file") {
            @(@{ Id = "10Commit"; Action = "commit" })
        } else {
            @(@{ Id = "10Update"; Action = "update" }, @{ Id = "30Commit"; Action = "commit" })
        }
        foreach ($verb in $demoted) {
            $expectedCommand = "$quotedApplicationPath --shell-path `"$($menu.Target)|`" --action $($verb.Action)"
            Assert-RegistryValue "$($menu.Commands)\Shell\$($verb.Id)\command" "" $expectedCommand `
                ([Microsoft.Win32.RegistryValueKind]::String)
        }
        if ($menu.Name -eq "background") {
            Assert-RegistryValueAbsent $menu.Menu "MultiSelectModel"
            foreach ($verb in $demoted) {
                Assert-RegistryValueAbsent "$($menu.Commands)\Shell\$($verb.Id)" "MultiSelectModel"
            }
        } else {
            foreach ($verb in $demoted) {
                Assert-RegistryValue "$($menu.Commands)\Shell\$($verb.Id)" "MultiSelectModel" "Single" `
                    ([Microsoft.Win32.RegistryValueKind]::String)
            }
        }
    }
}

function Set-ExecutableMarker {
    param([string]$KeyPath, [string]$ExecutablePath)
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($KeyPath, $true)
    try {
        if ($null -eq $key) { throw "Missing owned menu key HKCU:\$KeyPath." }
        $oldValue = [string]$key.GetValue("MoaVcsExecutable")
        $key.SetValue("MoaVcsExecutable", $ExecutablePath, [Microsoft.Win32.RegistryValueKind]::String)
        return $oldValue
    } finally {
        if ($null -ne $key) { $key.Dispose() }
    }
}

function Assert-OtherExecutablePreserved {
    param([string]$ExpectedExecutable)
    foreach ($path in $menuPaths + $commandRoots + $quickRoots) {
        if (-not (Test-Path -LiteralPath "Registry::HKEY_CURRENT_USER\$path")) {
            throw "Uninstall removed a menu owned by another executable: HKCU:\$path."
        }
    }
    foreach ($path in $commandRoots + $quickRoots) {
        Assert-RegistryValue $path "MoaVcsExecutable" $ExpectedExecutable ([Microsoft.Win32.RegistryValueKind]::String)
    }
}

$setupAttempted = $false
try {
    # Start with the documented defaults so the existing registration assertion remains stable.
    Set-TestExplorerMenuSettings $true $true
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

    Set-TestExplorerMenuSettings $false $false
    $demote = Start-Process -FilePath $app -ArgumentList @("--install-context-menu", "--headless") -Wait -PassThru -WindowStyle Hidden
    if ($demote.ExitCode -ne 0) { throw "Demoted menu registration returned exit code $($demote.ExitCode)." }
    Assert-QuickActionsDemoted $app

    Set-TestExplorerMenuSettings $true $true
    $restorePromotions = Start-Process -FilePath $app -ArgumentList @("--install-context-menu", "--headless") -Wait -PassThru -WindowStyle Hidden
    if ($restorePromotions.ExitCode -ne 0) { throw "Restored menu registration returned exit code $($restorePromotions.ExitCode)." }
    Assert-Registration $app

    # Simulate keys left by the prior unnumbered menu schema, then run the app's
    # owned-key registration path as an in-place upgrade would.
    foreach ($root in $commandRoots) {
        foreach ($id in @("Open", "Diff")) {
            $legacy = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$root\Shell\$id\command")
            try { $legacy.SetValue("", "legacy command", [Microsoft.Win32.RegistryValueKind]::String) }
            finally { $legacy.Dispose() }
        }
        $nested = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$root\Shell\60Repository\Commands\Shell\10Open\command")
        try { $nested.SetValue("", "legacy nested action", [Microsoft.Win32.RegistryValueKind]::String) }
        finally { $nested.Dispose() }
    }
    # Reproduce the bug: prior registrations wrote Single to background verbs and quick entries.
    $staleBackgroundKeys = @(
        $menuPaths[1],
        "$($commandRoots[1])\Shell\10Update",
        "$($commandRoots[1])\Shell\20Push",
        "$($commandRoots[1])\Shell\30Commit",
        "$($commandRoots[1])\Shell\40Log",
        "$($commandRoots[1])\Shell\50Status",
        "$($commandRoots[1])\Shell\60Open",
        "$($commandRoots[1])\Shell\70Clone",
        "$($commandRoots[1])\Shell\80Init",
        "$($commandRoots[1])\Shell\85Remote",
        "$($commandRoots[1])\Shell\90Settings",
        $quickRoots[2],
        $quickRoots[3]
    )
    foreach ($path in $staleBackgroundKeys) {
        $stale = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($path)
        try { $stale.SetValue("MultiSelectModel", "Single", [Microsoft.Win32.RegistryValueKind]::String) }
        finally { $stale.Dispose() }
    }
    $register = Start-Process -FilePath $app -ArgumentList @("--install-context-menu", "--headless") -Wait -PassThru -WindowStyle Hidden
    if ($register.ExitCode -ne 0) { throw "Menu upgrade registration returned exit code $($register.ExitCode)." }
    Assert-Registration $app
    Assert-LegacyVerbsRemoved

    $otherExecutable = Join-Path $tempRoot ("moavcs newer executable fixture-" + [Guid]::NewGuid().ToString("N") + "\moavcs.exe")
    $ownedExecutableKeys = $commandRoots + $quickRoots
    $savedExecutableMarkers = @{}
    foreach ($path in $ownedExecutableKeys) { $savedExecutableMarkers[$path] = Set-ExecutableMarker $path $otherExecutable }
    try {
        $unregister = Start-Process -FilePath $app -ArgumentList @("--uninstall-context-menu", "--headless") -Wait -PassThru -WindowStyle Hidden
        if ($unregister.ExitCode -ne 0) { throw "Executable ownership check returned exit code $($unregister.ExitCode)." }
        Assert-OtherExecutablePreserved $otherExecutable
    } finally {
        foreach ($path in $ownedExecutableKeys) { [void](Set-ExecutableMarker $path $savedExecutableMarkers[$path]) }
    }
    Assert-Registration $app

    $uninstaller = Join-Path $installDirectory "unins000.exe"
    if (-not (Test-Path -LiteralPath $uninstaller)) { throw "The per-user uninstaller was not created." }
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") -Wait -PassThru -WindowStyle Hidden
    if ($uninstall.ExitCode -ne 0) { throw "Silent uninstaller returned exit code $($uninstall.ExitCode)." }
    foreach ($path in $menuPaths + $commandRoots + $quickRoots) {
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
    if ($settingsExisted) {
        [IO.File]::Copy($settingsBackupPath, $settingsPath, $true)
        Remove-Item -LiteralPath $settingsBackupPath -Force
    } else {
        if (Test-Path -LiteralPath $settingsPath -PathType Leaf) { Remove-Item -LiteralPath $settingsPath -Force }
        if (-not $settingsDirectoryExisted -and (Test-Path -LiteralPath $settingsDirectory -PathType Container) -and
            @(Get-ChildItem -LiteralPath $settingsDirectory -Force).Count -eq 0) {
            Remove-Item -LiteralPath $settingsDirectory -Force
        }
    }
}

Write-Host "Installer smoke test passed."
