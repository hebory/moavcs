$ErrorActionPreference = 'Stop'

# This test writes HKLM shell-extension registration and installs/uninstalls a machine-wide DLL.
if ($env:GITHUB_ACTIONS -cne 'true' -or [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP) -or
    -not (Test-Path -LiteralPath $env:RUNNER_TEMP -PathType Container)) {
    throw 'This installer smoke test may run only on an isolated GitHub Actions runner.'
}
if (-not [BitConverter]::IsLittleEndian -or [Environment]::Is64BitProcess -eq $false) {
    throw 'The native overlay installer smoke test requires a 64-bit little-endian process.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The native overlay installer smoke test requires an elevated GitHub Actions runner.'
}

$workspace = Split-Path -Parent $PSScriptRoot
$installer = Join-Path $workspace 'artifacts/publish/win-x64/shell/MoaVcsShellSetup.exe'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Missing native shell installer: $installer." }
[xml]$project = Get-Content -LiteralPath (Join-Path $workspace 'src/MoaVcs.App/MoaVcs.App.csproj') -Raw
$version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText

$overlayNames = @('MoaVCS1Conflict', 'MoaVCS2Modified', 'MoaVCS3Untracked')
$priorityOverlayNames = @('    MoaVCS1Conflict', '    MoaVCS2Modified', '    MoaVCS3Untracked')
$ownedOverlayNames = $overlayNames + $priorityOverlayNames
$classIds = @(
    '{A25E0B81-8C4E-4C9B-9D3A-DF43085C2B11}',
    '{B7DA0B46-1295-42B0-B3A7-3D55F6E83F22}',
    '{CF3E2A97-E680-49B8-A2C4-7A9F65C4D833}'
)
$overlayRoot = 'Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers'
$clsidRoot = 'Software\Classes\CLSID'
$approvedRoot = 'Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved'
$machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
$commonPrograms = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonProgramFiles)
$expectedDllPrefix = [IO.Path]::GetFullPath((Join-Path $commonPrograms 'MoaVCS/Shell')) + [IO.Path]::DirectorySeparatorChar
$expectedDll = [IO.Path]::GetFullPath((Join-Path $expectedDllPrefix "$version\MoaVcsShell.dll"))
$settingsPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'MoaVcs/settings.json'
$overlayDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'MoaVcs/overlays'
$cachePath = Join-Path $overlayDirectory 'status.bin'
$testRoot = Join-Path $env:RUNNER_TEMP ('moavcs-overlay-' + [Guid]::NewGuid().ToString('N'))
$installerStarted = $false
$cacheCreated = $false
$overlayDirectoryCreated = $false
$testRootCreated = $false

function Get-OtherOverlaySnapshot {
    $rows = [Collections.Generic.List[string]]::new()
    $root = $machine.OpenSubKey($overlayRoot)
    if ($null -eq $root) { return '' }
    try {
        foreach ($name in $root.GetSubKeyNames() | Where-Object { $_ -notin $ownedOverlayNames } | Sort-Object -CaseSensitive) {
            $key = $root.OpenSubKey($name)
            try {
                foreach ($valueName in $key.GetValueNames() | Sort-Object -CaseSensitive) {
                    $kind = $key.GetValueKind($valueName)
                    $value = $key.GetValue($valueName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                    $rendered = switch ($kind) {
                        String { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$value)) }
                        ExpandString { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$value)) }
                        MultiString { [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes([string]::Join([char]0, [string[]]$value))) }
                        Binary { [Convert]::ToBase64String([byte[]]$value) }
                        DWord { [Convert]::ToString([int]$value, 16) }
                        QWord { [Convert]::ToString([long]$value, 16) }
                        default { throw "Unsupported registry value type in existing overlay key '$name': $kind." }
                    }
                    $rows.Add("$name`t$valueName`t$kind`t$rendered")
                }
                if ($key.SubKeyCount -gt 0) { throw "Unexpected nested key under another overlay handler '$name'." }
            } finally { $key.Dispose() }
        }
    } finally { $root.Dispose() }
    return [string]::Join("`n", $rows)
}

function Assert-RegistryString {
    param([string]$Path, [string]$Name, [string]$Expected)
    $key = $machine.OpenSubKey($Path)
    try {
        if ($null -eq $key) { throw "Missing HKLM64:\$Path." }
        $actual = $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ($actual -isnot [string] -or $actual -cne $Expected -or
            $key.GetValueKind($Name) -ne [Microsoft.Win32.RegistryValueKind]::String) {
            throw "Unexpected HKLM64:\$Path '$Name': '$actual'; expected '$Expected'."
        }
    } finally { if ($null -ne $key) { $key.Dispose() } }
}

function Assert-OurRegistrationAbsent {
    for ($index = 0; $index -lt $classIds.Count; $index++) {
        foreach ($path in @("$overlayRoot\$($overlayNames[$index])", "$overlayRoot\$($priorityOverlayNames[$index])", "$clsidRoot\$($classIds[$index])")) {
            $key = $machine.OpenSubKey($path)
            if ($null -ne $key) {
                $key.Dispose()
                throw "MoaVCS overlay registration already exists; refusing to overwrite it: HKLM64:\$path."
            }
        }
        $approved = $machine.OpenSubKey($approvedRoot)
        if ($null -ne $approved) {
            try {
                if ($approved.GetValueNames() -contains $classIds[$index]) {
                    throw "MoaVCS overlay approval entry already exists; refusing to overwrite it: $($classIds[$index])."
                }
            } finally { $approved.Dispose() }
        }
    }
}

function Assert-OverlayRegistration {
    param([string[]]$Names)
    for ($index = 0; $index -lt $classIds.Count; $index++) {
        $handlerPath = "$overlayRoot\$($Names[$index])"
        Assert-RegistryString $handlerPath '' $classIds[$index]
        Assert-RegistryString $handlerPath 'MoaVcsDll' $expectedDll
        $clsidPath = "$clsidRoot\$($classIds[$index])"
        Assert-RegistryString $clsidPath 'MoaVcsOwner' 'MoaVcs.Overlays.v1'
        $serverPath = "$clsidPath\InprocServer32"
        Assert-RegistryString $serverPath '' $expectedDll
        Assert-RegistryString $serverPath 'ThreadingModel' 'Apartment'
        $approved = $machine.OpenSubKey($approvedRoot)
        try {
            if ($null -eq $approved -or $approved.GetValue($classIds[$index]) -cne "MoaVCS $(@('Conflict Overlay', 'Modified Overlay', 'Untracked Overlay')[$index])") {
                throw "Missing approved shell extension registration for $($classIds[$index])."
            }
        } finally { if ($null -ne $approved) { $approved.Dispose() } }
    }
    if (-not $expectedDll.StartsWith($expectedDllPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $expectedDll -PathType Leaf)) {
        throw "Overlay DLL is not installed in the protected Common Files directory: $expectedDll."
    }
}

function Assert-OurRegistrationRemoved {
    foreach ($name in $ownedOverlayNames) {
        $key = $machine.OpenSubKey("$overlayRoot\$name")
        if ($null -ne $key) { $key.Dispose(); throw "Overlay handler key remains after uninstall: $name." }
    }
    foreach ($index in 0..2) {
        $key = $machine.OpenSubKey("$clsidRoot\$($classIds[$index])")
        if ($null -ne $key) { $key.Dispose(); throw "Overlay CLSID remains after uninstall: $($classIds[$index])." }
        $approved = $machine.OpenSubKey($approvedRoot)
        if ($null -ne $approved) {
            try { if ($approved.GetValueNames() -contains $classIds[$index]) { throw "Overlay approval remains after uninstall: $($classIds[$index])." } }
            finally { $approved.Dispose() }
        }
    }
}

function Write-StatusFixture {
    param([string]$Path, [object[]]$Entries)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::Unicode, $true)
        try {
            $writer.Write([uint32]0x4D4F4156)
            $writer.Write([uint32]1)
            $writer.Write([DateTime]::UtcNow.ToFileTimeUtc())
            $writer.Write([uint32]$Entries.Count)
            foreach ($entry in $Entries) {
                $encodedPath = [Text.Encoding]::Unicode.GetBytes([string]$entry.Path)
                $writer.Write([uint32]$entry.State)
                $writer.Write([uint32]([string]$entry.Path).Length)
                $writer.Write($encodedPath)
            }
            $writer.Flush()
        } finally { $writer.Dispose() }
        $stream.Flush($true)
    } finally { $stream.Dispose() }
}

function Assert-ComOverlayBehavior {
    param([string[]]$FixturePaths)
    $expectedByHandler = @(
        @{ Id = $classIds[0]; IconIndex = 0; Priority = 0; State = 1 },
        @{ Id = $classIds[1]; IconIndex = 1; Priority = 1; State = 2 },
        @{ Id = $classIds[2]; IconIndex = 2; Priority = 2; State = 3 }
    )
    foreach ($handler in $expectedByHandler) {
        foreach ($path in $FixturePaths) {
            $query = [MoaVcs.OverlayInstallerSmoke]::Query($handler.Id, $path)
            if ($query.IconIndex -ne $handler.IconIndex -or $query.Priority -ne $handler.Priority -or
                -not (Test-Path -LiteralPath $query.IconPath -PathType Leaf)) {
                throw "COM metadata validation failed for overlay $($handler.Id): icon='$($query.IconPath)', index=$($query.IconIndex), priority=$($query.Priority)."
            }
            $expectedMembership = if ($path -in @($FixturePaths[0], $FixturePaths[1]) -and $handler.State -eq 1 -or
                $path -in @($FixturePaths[2], $FixturePaths[3]) -and $handler.State -eq 2 -or
                $path -in @($FixturePaths[4], $FixturePaths[5]) -and $handler.State -eq 3) { 0 } else { 1 }
            if ($query.Membership -ne $expectedMembership) {
                throw "COM IsMemberOf returned $($query.Membership) for state $($handler.State) and path '$path'; expected $expectedMembership."
            }
        }
    }
    [MoaVcs.OverlayInstallerSmoke]::FreeUnusedLibraries()
}

$csharp = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MoaVcs {
    [ComImport]
    [Guid("0C6C4200-C589-11D0-999A-00C04FD655E1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellIconOverlayIdentifier {
        [PreserveSig] int IsMemberOf([MarshalAs(UnmanagedType.LPWStr)] string path, uint attributes);
        [PreserveSig] int GetOverlayInfo([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconFile,
            int cchMax, out int index, out uint flags);
        [PreserveSig] int GetPriority(out int priority);
    }

    public sealed class OverlayQueryResult {
        public string IconPath { get; set; }
        public int IconIndex { get; set; }
        public uint Flags { get; set; }
        public int Priority { get; set; }
        public int Membership { get; set; }
    }

    public static class OverlayInstallerSmoke {
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoFreeUnusedLibraries();

        public static OverlayQueryResult Query(string classId, string path) {
            object instance = null;
            try {
                var type = Type.GetTypeFromCLSID(new Guid(classId), true);
                instance = Activator.CreateInstance(type);
                var handler = (IShellIconOverlayIdentifier)instance;
                var icon = new StringBuilder(32768);
                var hr = handler.GetOverlayInfo(icon, icon.Capacity, out var index, out var flags);
                CheckHr(hr, "GetOverlayInfo");
                hr = handler.GetPriority(out var priority);
                CheckHr(hr, "GetPriority");
                var membership = handler.IsMemberOf(path, 0);
                if (membership != 0 && membership != 1) CheckHr(membership, "IsMemberOf");
                return new OverlayQueryResult { IconPath = icon.ToString(), IconIndex = index,
                    Flags = flags, Priority = priority, Membership = membership };
            } finally {
                if (instance != null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
            }
        }

        public static void FreeUnusedLibraries() { GC.Collect(); GC.WaitForPendingFinalizers(); CoFreeUnusedLibraries(); }
        private static void CheckHr(int hr, string method) {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr, new IntPtr(-1));
            if (hr != 0) throw new InvalidOperationException(method + " returned unexpected HRESULT 0x" + hr.ToString("X8"));
        }
    }
}
'@

try {
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) { throw "Existing user settings detected; refusing to run overlay installer test: $settingsPath." }
    if (Test-Path -LiteralPath $overlayDirectory) { throw "Existing overlay cache detected; refusing to overwrite it: $overlayDirectory." }
    if (Test-Path -LiteralPath $testRoot) { throw "Unexpected overlay test fixture path collision: $testRoot." }
    Assert-OurRegistrationAbsent
    $otherOverlaysBefore = Get-OtherOverlaySnapshot

    $setup = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru -WindowStyle Hidden
    $installerStarted = $true
    if ($setup.ExitCode -ne 0) { throw "Native overlay setup exited with code $($setup.ExitCode)." }

    Assert-OverlayRegistration $overlayNames
    foreach ($name in $priorityOverlayNames) {
        $key = $machine.OpenSubKey("$overlayRoot\$name")
        if ($null -ne $key) { $key.Dispose(); throw "Plain install unexpectedly registered the priority handler name '$name'." }
    }

    [void][IO.Directory]::CreateDirectory($testRoot)
    $testRootCreated = $true
    $paths = @(
        (Join-Path $testRoot 'conflict.txt'), (Join-Path $testRoot 'conflict-folder'),
        (Join-Path $testRoot 'modified.txt'), (Join-Path $testRoot 'modified-folder'),
        (Join-Path $testRoot 'untracked.txt'), (Join-Path $testRoot 'untracked-folder'),
        (Join-Path $testRoot 'clean.txt')
    )
    foreach ($folder in @($paths[1], $paths[3], $paths[5])) { [void][IO.Directory]::CreateDirectory($folder) }
    foreach ($file in @($paths[0], $paths[2], $paths[4], $paths[6])) { [IO.File]::WriteAllText($file, 'overlay smoke fixture') }
    $overlayDirectoryCreated = $true
    $cacheCreated = $true
    Write-StatusFixture $cachePath @(
        @{ State = 1; Path = $paths[0] }, @{ State = 1; Path = $paths[1] },
        @{ State = 2; Path = $paths[2] }, @{ State = 2; Path = $paths[3] },
        @{ State = 3; Path = $paths[4] }, @{ State = 3; Path = $paths[5] }
    )
    if (-not ('MoaVcs.OverlayInstallerSmoke' -as [type])) { Add-Type -TypeDefinition $csharp -Language CSharp }
    Assert-ComOverlayBehavior $paths

    $upgrade = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/PRIORITY=1') -Wait -PassThru -WindowStyle Hidden
    if ($upgrade.ExitCode -ne 0) { throw "Native priority overlay setup exited with code $($upgrade.ExitCode)." }
    Assert-OverlayRegistration $priorityOverlayNames
    foreach ($name in $overlayNames) {
        $key = $machine.OpenSubKey("$overlayRoot\$name")
        if ($null -ne $key) { $key.Dispose(); throw "Priority upgrade left the plain overlay handler '$name' registered." }
    }
    if ((Get-OtherOverlaySnapshot) -cne $otherOverlaysBefore) { throw "Priority upgrade modified another application's overlay registration." }
    Assert-ComOverlayBehavior $paths

    $uninstaller = Join-Path $commonPrograms 'MoaVCS/Shell/uninstall/unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw "Native overlay uninstaller is missing: $uninstaller." }
    $remove = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru -WindowStyle Hidden
    if ($remove.ExitCode -ne 0) { throw "Native overlay uninstaller exited with code $($remove.ExitCode)." }
    Assert-OurRegistrationRemoved
    if ((Get-OtherOverlaySnapshot) -cne $otherOverlaysBefore) { throw "Native overlay install/uninstall modified another application's overlay registration." }
    $installerStarted = $false
    Write-Host 'Native overlay installer smoke test passed: HKLM64 registration, Apartment COM activation, cache states, and owned uninstall.'
} finally {
    if ('MoaVcs.OverlayInstallerSmoke' -as [type]) {
        try { [MoaVcs.OverlayInstallerSmoke]::FreeUnusedLibraries() } catch { Write-Warning "Could not release the native overlay test module: $($_.Exception.Message)" }
    }
    if ($installerStarted) {
        $uninstaller = Join-Path $commonPrograms 'MoaVCS/Shell/uninstall/unins000.exe'
        if (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
            $cleanup = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru -WindowStyle Hidden
            if ($cleanup.ExitCode -ne 0) { Write-Warning "Native overlay cleanup uninstaller exited with code $($cleanup.ExitCode)." }
        }
    }
    try {
        if ($cacheCreated -and (Test-Path -LiteralPath $cachePath -PathType Leaf)) { Remove-Item -LiteralPath $cachePath -Force }
        if ($overlayDirectoryCreated -and (Test-Path -LiteralPath $overlayDirectory -PathType Container) -and
            @(Get-ChildItem -LiteralPath $overlayDirectory -Force).Count -eq 0) { Remove-Item -LiteralPath $overlayDirectory -Force }
    } catch { Write-Warning "Could not clean the synthetic overlay cache: $($_.Exception.Message)" }
    try {
        if ($testRootCreated -and (Test-Path -LiteralPath $testRoot -PathType Container)) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
    } catch { Write-Warning "Could not clean the synthetic overlay fixture: $($_.Exception.Message)" }
    $machine.Dispose()
}
