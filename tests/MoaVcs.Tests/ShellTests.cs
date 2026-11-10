using MoaVcs.App.Services;
using MoaVcs.Core;
using System.IO;
using System.Runtime.InteropServices;

namespace MoaVcs.Tests;

public static class ShellTests
{
    public static Task RunAsync()
    {
        Check(LaunchOptions.Parse(["--path", @"C:\Work Area\repo", "--vcs", "git"]).Path == @"C:\Work Area\repo", "CLI paths retain spaces");
        Check(LaunchOptions.Parse(["--shell-path", @"C:\Work Area\repo\|"]).Path == @"C:\Work Area\repo\", "shell path sentinel is removed");
        Check(LaunchOptions.Parse(["--vcs", "Perforce", "--action", "diff"]).Action == "diff", "CLI parses provider and action");
        Check(LaunchOptions.Parse(["--action", "blame"]).Action == "blame", "CLI accepts blame action");
        foreach (var action in new[] { "update", "push", "clone", "init", "settings" })
            Check(LaunchOptions.Parse(["--action", action.ToUpperInvariant()]).Action == action, "CLI accepts " + action);
        Throws<FormatException>(() => LaunchOptions.Parse(["--action", "delete-all"]), "unknown actions are rejected");
        Throws<FormatException>(() => LaunchOptions.Parse(["--vcs", "invented"]), "unknown providers are rejected");
        Throws<FormatException>(() => LaunchOptions.Parse(["--shell-path", @"C:\repo\"]), "shell path requires its sentinel");
        Throws<FormatException>(() => LaunchOptions.Parse(["--install-context-menu", "--uninstall-context-menu"]), "conflicting install flags are rejected");
        Check(LaunchOptions.Parse(["--install-context-menu", "--headless"]).Headless, "installer can register without opening a GUI");
        Check(LaunchOptions.Parse(["--uninstall-context-menu", "--headless"]).UninstallContextMenu, "uninstaller can unregister without opening a GUI");
        Throws<FormatException>(() => LaunchOptions.Parse(["--headless"]), "headless mode requires an explicit shell operation");
        Throws<FormatException>(() => LaunchOptions.Parse(["--install-context-menu", "--headless", "--path", @"C:\repo"]), "installer mode cannot open a repository");

        Check(ExplorerIntegration.QuoteArgument(@"C:\Program Files\moavcs\moavcs.exe") == @"""C:\Program Files\moavcs\moavcs.exe""", "quoted executable paths");
        Check(ExplorerIntegration.QuoteArgument(@"C:\moavcs\moavcs.exe") == @"C:\moavcs\moavcs.exe", "simple executable paths");
        var plan = ExplorerIntegration.BuildRegistrationPlan(@"C:\Program Files\Moa Vcs\moavcs.exe");
        Check(plan.Any(p => p.KeyPath.Contains(@"Directory\ContextMenus\MoaVcs.BackgroundCommands", StringComparison.Ordinal) && p.Value.Contains("%V", StringComparison.Ordinal)), "background commands use shell background path");
        Check(plan.Count(p => p.Value.Contains("--action diff", StringComparison.Ordinal)) == 1 &&
            plan.Single(p => p.Value.Contains("--action diff", StringComparison.Ordinal)).KeyPath.Contains(@"MoaVcs.FileCommands\Shell\10Diff\command", StringComparison.Ordinal), "diff is a file-only command");
        Check(plan.Any(p => p.Value.Contains("--action commit", StringComparison.Ordinal)), "commit command is registered");
        Check(plan.Count(p => p.Value.Contains("--action blame", StringComparison.Ordinal)) == 1 &&
            plan.Single(p => p.Value.Contains("--action blame", StringComparison.Ordinal)).KeyPath.Contains("MoaVcs.FileCommands", StringComparison.Ordinal), "blame is registered only for files");
        Check(plan.Where(p => p.Value.Contains("--shell-path", StringComparison.Ordinal)).All(p => p.Value.Contains("\"%1|\"", StringComparison.Ordinal) || p.Value.Contains("\"%V|\"", StringComparison.Ordinal)), "shell substitutions include a safe trailing sentinel");
        CheckMenuLayout(plan);
        var dangerous = ExplorerIntegration.BuildRegistrationPlan(@"C:\repo & echo pwned\moavcs.exe").Last(p => p.Value.Contains("--action commit", StringComparison.Ordinal));
        Check(dangerous.Value.StartsWith("\"C:\\repo & echo pwned\\moavcs.exe\"", StringComparison.Ordinal), "metacharacters stay inside quoted executable name");

        var open = plan.First(p => p.KeyPath.EndsWith(@"\10Open\command", StringComparison.Ordinal) && p.KeyPath.Contains(@"\Directory\ContextMenus\", StringComparison.Ordinal));
        var rootInvocation = open.Value.Replace("%1", @"C:\", StringComparison.Ordinal);
        var parsed = ParseWindowsCommandLine(rootInvocation);
        var launch = LaunchOptions.Parse(parsed.Skip(1).ToArray());
        Check(launch.Path == @"C:\", "real shell command round-trips a drive root ending in backslash");
        var nestedInvocation = open.Value.Replace("%1", @"C:\Work Area\repo with spaces\", StringComparison.Ordinal);
        parsed = ParseWindowsCommandLine(nestedInvocation);
        launch = LaunchOptions.Parse(parsed.Skip(1).ToArray());
        Check(launch.Path == @"C:\Work Area\repo with spaces\", "real shell command round-trips spaces and trailing backslash");
        CheckSettingsStore();
        return Task.CompletedTask;
    }

    private static void CheckMenuLayout(IReadOnlyList<RegistryValuePlan> plan)
    {
        const string classes = @"Software\Classes\";
        foreach (var (scope, commandKey) in new[]
        {
            ("Directory", @"Directory\ContextMenus\MoaVcs.Commands"),
            (@"Directory\Background", @"Directory\ContextMenus\MoaVcs.BackgroundCommands"),
            ("*", @"*\ContextMenus\MoaVcs.FileCommands")
        })
        {
            var menu = classes + scope + @"\shell\MoaVcs";
            var root = classes + commandKey;
            var shell = root + @"\Shell\";
            var isFile = scope == "*";
            var expected = isFile
                ? new[] { "10Diff", "20Blame", "30Log", "40Status", "50Commit", "60Open", "70Settings" }
                : new[] { "10Status", "20Commit", "30Update", "40Push", "50Log", "60Repository", "70Settings" };
            var top = plan.Where(p => p.ValueName == "MUIVerb" && p.KeyPath.StartsWith(shell, StringComparison.Ordinal) &&
                                     !p.KeyPath[shell.Length..].Contains('\\')).ToArray();
            Check(top.Select(p => p.KeyPath[shell.Length..]).SequenceEqual(expected), "workflow ordering for " + scope);
            Check(plan.Any(p => p.KeyPath == menu && p.ValueName == "MUIVerb" && p.Value == "MoaVCS"), "branded cascade label");
            Check(plan.Any(p => p.KeyPath == menu && p.ValueName == "Icon" && p.Value.EndsWith("moavcs.exe\",0", StringComparison.Ordinal)), "cascade icon uses application resource");
            Check(plan.Any(p => p.KeyPath == root && p.ValueName == "MoaVcsExecutable" && p.Value == @"C:\Program Files\Moa Vcs\moavcs.exe"), "uninstall target persists independently of menu ordering");
            Check(plan.Any(p => p.KeyPath == menu && p.ValueName == "MultiSelectModel" && p.Value == "Single"), "single-target cascade");
            var separators = isFile ? new[] { "40Status", "60Open", "70Settings" } : new[] { "50Log", "60Repository", "70Settings" };
            foreach (var separator in separators)
                Check(plan.Any(p => p.KeyPath == shell + separator && p.ValueName == "CommandFlags" &&
                                    p.Kind == Microsoft.Win32.RegistryValueKind.DWord && p.Value == "32"), "typed group separator");
            if (!isFile)
            {
                var nested = commandKey + @"\Shell\60Repository\Commands";
                Check(plan.Any(p => p.KeyPath == shell + "60Repository" && p.ValueName == "ExtendedSubCommandsKey" && p.Value == nested), "repository submenu resolves its own command tree");
                foreach (var action in new[] { "open", "clone", "init" })
                    Check(plan.Any(p => p.KeyPath.StartsWith(classes + nested + @"\Shell\", StringComparison.Ordinal) && p.Value.EndsWith("--action " + action, StringComparison.Ordinal)), "repository submenu action " + action);
            }
            var commands = plan.Where(p => p.KeyPath.StartsWith(shell, StringComparison.Ordinal) && p.KeyPath.EndsWith(@"\command", StringComparison.Ordinal)).ToArray();
            Check(commands.Length == (isFile ? 7 : 9), "exact action set for " + scope);
            foreach (var command in commands)
            {
                var verb = command.KeyPath[..^@"\command".Length];
                Check(plan.Any(p => p.KeyPath == verb && p.ValueName == "MultiSelectModel" && p.Value == "Single"), "single-target leaf verb");
                var parsed = ParseWindowsCommandLine(command.Value.Replace(isFile || scope == "Directory" ? "%1" : "%V", @"C:\Work & Notes\한글\", StringComparison.Ordinal));
                var options = LaunchOptions.Parse(parsed.Skip(1).ToArray());
                Check(options.Path == @"C:\Work & Notes\한글\", "every menu command preserves literal shell target");
            }
        }
    }

    private static void CheckSettingsStore()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".local", "settings-test-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, "settings.json");
        try
        {
            var store = new SettingsStore(file);
            store.Save(new AppSettings
            {
                RecentRepositories = [new RecentRepository(@"C:\Work Area\repo", VcsKind.Git)],
                ExecutablePaths = new Dictionary<VcsKind, string> { [VcsKind.Perforce] = @"C:\Tools\p4.exe" }
            });
            var roundTrip = store.Load();
            Check(roundTrip.RecentRepositories.Single().Path == @"C:\Work Area\repo", "settings persist recent paths");
            Check(roundTrip.ExecutablePaths[VcsKind.Perforce] == @"C:\Tools\p4.exe", "settings persist executable paths");

            const string invalid = "{ this is not valid JSON";
            File.WriteAllText(file, invalid);
            var recovered = store.Load();
            Check(recovered.RecentRepositories.Count == 0 && !string.IsNullOrWhiteSpace(store.LoadWarning), "bad settings recover with a warning");
            Check(File.ReadAllText(file) == invalid, "bad settings remain available for recovery");
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".local")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string[] ParseWindowsCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argc);
        if (argv == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new string[argc];
            for (var i = 0; i < argc; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return result;
        }
        finally { _ = LocalFree(argv); }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException($"Shell test failed: {message}");
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Shell test failed: {message}");
    }
}
