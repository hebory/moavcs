using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace MoaVcs.App.Services;

/// <summary>Opt-in, per-user cache worker; the separately installed native handler is machine-wide.</summary>
public static class ExplorerOverlays
{
    public const string RegistryRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "MoaVcsStatusCache";
    public static readonly string[] ClassIds =
    ["{A25E0B81-8C4E-4C9B-9D3A-DF43085C2B11}", "{B7DA0B46-1295-42B0-B3A7-3D55F6E83F22}", "{CF3E2A97-E680-49B8-A2C4-7A9F65C4D833}"];
    public static readonly string[] HandlerNames = ["MoaVCS1Conflict", "MoaVCS2Modified", "MoaVCS3Untracked"];
    private const string PriorityPrefix = "    ";
    public static string CacheDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoaVcs", "overlays");
    public static string CachePath => Path.Combine(CacheDirectory, "status.bin");
    internal static string ObjectName(string suffix) => @"Local\MoaVcs.Overlays." + WindowsIdentity.GetCurrent().User!.Value + "." + suffix;

    public static string StartupCommand(string executable) => ExplorerIntegration.QuoteArgument(Path.GetFullPath(executable)) + " --overlay-worker";

    public static bool IsStartupOwner(string executable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(RunName) as string, StartupCommand(executable), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInstalled()
    {
        return ReadInstalledPolicy() is not null;
    }

    public static bool IsPriorityInstalled() => ReadInstalledPolicy() is true;

    private static bool? ReadInstalledPolicy()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            var normal = IsPolicyInstalled(machine, PriorityNames(false));
            var priority = IsPolicyInstalled(machine, PriorityNames(true));
            if (normal == priority) return null; // Both absent or mixed/duplicate registrations are invalid.
            return priority;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return null; }
    }

    private static string[] PriorityNames(bool preferPriority) => HandlerNames
        .Select(name => preferPriority ? PriorityPrefix + name : name).ToArray();

    private static bool IsPolicyInstalled(RegistryKey machine, IReadOnlyList<string> names)
    {
        for (var i = 0; i < ClassIds.Length; i++)
        {
            using var handler = machine.OpenSubKey(RegistryRoot + "\\" + names[i]);
            using var classKey = machine.OpenSubKey(@"Software\Classes\CLSID\" + ClassIds[i]);
            using var server = machine.OpenSubKey(@"Software\Classes\CLSID\" + ClassIds[i] + @"\InprocServer32");
            var dll = server?.GetValue("") as string;
            if (!string.Equals(handler?.GetValue("") as string, ClassIds[i], StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(handler?.GetValue("MoaVcsDll") as string, dll, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(classKey?.GetValue("MoaVcsOwner") as string, "MoaVcs.Overlays.v1", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(dll) || !File.Exists(dll)) return false;
        }
        return true;
    }

    public static string GetStatusText(bool enabled)
    {
        if (!enabled) return "사용 안 함 · 상태 배지를 켜면 최근 저장소를 백그라운드에서 확인합니다.";
        if (!IsInstalled()) return "상태 배지 확장이 설치되어 있지 않습니다. 상태 배지를 다시 켜서 설치하세요.";
        if (!File.Exists(CachePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) > TimeSpan.FromSeconds(90))
            return "사용 중 · 첫 상태를 확인하는 중입니다. 저장소를 MoaVCS에서 한 번 열어 주세요.";
        var priority = IsPriorityInstalled() ? "우선 표시 사용 중 · " : "";
        return priority + "최근 저장소의 상태를 자동 갱신합니다. 처음 켠 뒤 배지가 보이지 않으면 로그아웃 후 다시 로그인하세요.";
    }

    public static async Task EnableAsync(string executable, SettingsStore store, bool preferPriority = false)
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new NotSupportedException("탐색기 상태 배지는 현재 Windows x64에서 지원합니다.");
        // Read settings before elevation. An unreadable file must never be replaced by defaults.
        _ = store.Load();
        if (store.LoadWarning is { } warning) throw new IOException(warning);
        EnsureNoOtherOwner(executable);
        if (!IsInstalled() || IsPriorityInstalled() != preferPriority)
        {
            var setup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "shell", "MoaVcsShellSetup.exe");
            if (!File.Exists(setup)) throw new FileNotFoundException("상태 배지 설치 파일이 없습니다. 최신 설치 파일 또는 포터블 ZIP을 사용하세요.", setup);
            using var process = Process.Start(new ProcessStartInfo(setup)
            {
                UseShellExecute = true, Verb = "runas",
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PRIORITY=" + (preferPriority ? "1" : "0"),
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new IOException("상태 배지 설치를 시작하지 못했습니다.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || !IsInstalled() || IsPriorityInstalled() != preferPriority)
                throw new IOException("상태 배지 설치가 완료되지 않았습니다. 관리자 승인과 설치 결과를 확인하세요.");
        }
        // Wait for a previous worker during a quick off/on before creating the next one.
        Signal("stop");
        await WaitForStoppedAsync();
        store.SaveExplorerOverlays(true, preferPriority);
        try { Resume(executable, store); }
        catch
        {
            store.SaveExplorerOverlays(false);
            RemoveStartup(executable);
            throw;
        }
        NotifyAssociations();
    }

    public static void Resume(string executable, SettingsStore store)
    {
        var settings = store.Load();
        if (store.LoadWarning is not null || !settings.ExplorerOverlaysEnabled || !IsInstalled()) return;
        EnsureNoOtherOwner(executable);
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
            key.SetValue(RunName, StartupCommand(executable));
        StartWorker(executable);
    }

    public static void StartWorker(string executable)
    {
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--overlay-worker" }
        });
    }

    public static void Disable(string executable, SettingsStore store)
    {
        EnsureNoOtherOwner(executable);
        store.SaveExplorerOverlays(false);
        RemoveStartup(executable);
        Signal("stop");
        ClearCache();
        NotifyAssociations();
    }

    public static async Task DisableAsync(string executable, SettingsStore store)
    {
        Disable(executable, store);
        await WaitForStoppedAsync();
    }

    private static void EnsureNoOtherOwner(string executable)
    {
        if (IsStartupOwner(executable)) return;
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(RunName) is not null)
            throw new InvalidOperationException("다른 설치본이 상태 배지를 관리하고 있습니다. 해당 설치본의 설정에서 먼저 끄세요.");
    }

    public static async Task PauseAsync(string executable)
    {
        if (!IsStartupOwner(executable)) return;
        Signal("stop");
        await WaitForStoppedAsync();
    }

    private static async Task WaitForStoppedAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            if (!Mutex.TryOpenExisting(ObjectName("worker"), out var running)) return;
            running.Dispose();
            await Task.Delay(100);
        }
        throw new IOException("상태 확인 프로세스가 아직 종료 중입니다. 잠시 후 다시 시도하세요.");
    }

    private static void RemoveStartup(string executable)
    {
        if (!IsStartupOwner(executable)) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(RunName, throwOnMissingValue: false);
    }

    public static void Wake() => Signal("wake");
    private static void Signal(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(ObjectName(name), out var handle)) return;
        using (handle) handle.Set();
    }

    internal static void ClearCache()
    {
        try { File.Delete(CachePath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void NotifyPath(string path) => SHChangeNotify(0x00002000, 0x0005 | 0x2000, path, IntPtr.Zero);
    private static void NotifyAssociations() => SHChangeNotify(0x08000000, 0, null, IntPtr.Zero);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string? path, IntPtr unused);
}
