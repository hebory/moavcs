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
        foreach (var action in new[] { "update", "push", "clone", "init", "settings", "remote" })
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
        const string executable = @"C:\Program Files\Moa Vcs\moavcs.exe";
        var defaults = new ExplorerMenuSettings();
        Check(defaults.ShowCommitAtTopLevel && defaults.ShowUpdateAtTopLevel, "menu promotes Commit and Update by default");
        var plan = ExplorerIntegration.BuildRegistrationPlan(executable);
        CheckMenuLayout(plan, executable, defaults);
        Check(plan.Count(p => p.ValueName is null && p.KeyPath.EndsWith(@"\command", StringComparison.Ordinal) && p.Value.Contains("--action diff", StringComparison.Ordinal)) == 1,
            "Diff remains file-only");
        Check(plan.Count(p => p.ValueName is null && p.KeyPath.EndsWith(@"\command", StringComparison.Ordinal) && p.Value.Contains("--action blame", StringComparison.Ordinal)) == 1 &&
              plan.Single(p => p.ValueName is null && p.KeyPath.EndsWith(@"\command", StringComparison.Ordinal) && p.Value.Contains("--action blame", StringComparison.Ordinal)).KeyPath.Contains("MoaVcs.FileCommands", StringComparison.Ordinal),
            "Blame remains file-only");
        Check(plan.Where(p => p.Value.Contains("--shell-path", StringComparison.Ordinal)).All(p => p.Value.Contains("\"%1|\"", StringComparison.Ordinal) || p.Value.Contains("\"%V|\"", StringComparison.Ordinal)), "shell substitutions include a safe trailing sentinel");
        Check(plan.Count(p => p.ValueName is null && p.KeyPath.EndsWith(@"\command", StringComparison.Ordinal) && p.Value.Contains("--action remote", StringComparison.Ordinal)) == 3,
            "Remote address action is available in every context without VCS filtering");
        var noPromotions = new ExplorerMenuSettings { ShowCommitAtTopLevel = false, ShowUpdateAtTopLevel = false };
        CheckMenuLayout(ExplorerIntegration.BuildRegistrationPlan(executable, noPromotions), executable, noPromotions);
        var onlyUpdate = new ExplorerMenuSettings { ShowCommitAtTopLevel = false, ShowUpdateAtTopLevel = true };
        CheckMenuLayout(ExplorerIntegration.BuildRegistrationPlan(executable, onlyUpdate), executable, onlyUpdate);
        var onlyCommit = new ExplorerMenuSettings { ShowCommitAtTopLevel = true, ShowUpdateAtTopLevel = false };
        CheckMenuLayout(ExplorerIntegration.BuildRegistrationPlan(executable, onlyCommit), executable, onlyCommit);
        var dangerousPlan = ExplorerIntegration.BuildRegistrationPlan(@"C:\repo & echo pwned\moavcs.exe");
        var dangerous = dangerousPlan.Single(p => p.KeyPath == @"Software\Classes\Directory\shell\MoaVcs.Commit\command");
        Check(dangerous.Value.StartsWith("\"C:\\repo & echo pwned\\moavcs.exe\"", StringComparison.Ordinal), "quick command quotes executable metacharacters");

        var open = plan.First(p => p.ValueName is null && p.KeyPath.EndsWith(@"\60Open\command", StringComparison.Ordinal) && p.KeyPath.Contains(@"MoaVcs.Commands\", StringComparison.Ordinal));
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

    private static void CheckMenuLayout(IReadOnlyList<RegistryValuePlan> plan, string executable, ExplorerMenuSettings settings)
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
            var allEntries = isFile
                ? new[] { "10Commit", "20Diff", "30Log", "40Blame", "50Status", "60Open", "85Remote", "90Settings" }
                : new[] { "10Update", "20Push", "30Commit", "40Log", "50Status", "60Open", "70Clone", "80Init", "85Remote", "90Settings" };
            var expected = allEntries.Where(id =>
                !((id == "10Commit" || id == "30Commit") && settings.ShowCommitAtTopLevel) &&
                !(id == "10Update" && !isFile && settings.ShowUpdateAtTopLevel)).ToArray();
            var top = plan.Where(p => p.ValueName == "MUIVerb" && p.KeyPath.StartsWith(shell, StringComparison.Ordinal) &&
                                     !p.KeyPath[shell.Length..].Contains('\\')).ToArray();
            Check(top.Select(p => p.KeyPath[shell.Length..]).SequenceEqual(expected), "workflow ordering for " + scope);
            Check(plan.Any(p => p.KeyPath == menu && p.ValueName == "MUIVerb" && p.Value == "MoaVCS"), "branded cascade label");
            Check(plan.Any(p => p.KeyPath == menu && p.ValueName == "Icon" && p.Value.EndsWith("moavcs.exe\",0", StringComparison.Ordinal)), "cascade icon uses application resource");
            Check(plan.Any(p => p.KeyPath == root && p.ValueName == "MoaVcsExecutable" && p.Value == @"C:\Program Files\Moa Vcs\moavcs.exe"), "uninstall target persists independently of menu ordering");
            var selectionModelIsSingle = scope != @"Directory\Background";
            CheckSelectionModel(plan, menu, selectionModelIsSingle, "cascade " + scope);
            var separators = isFile ? new[] { "30Log", "60Open", "90Settings" } : new[] { "40Log", "90Settings" };
            foreach (var separator in separators)
                Check(plan.Any(p => p.KeyPath == shell + separator && p.ValueName == "CommandFlags" &&
                                    p.Kind == Microsoft.Win32.RegistryValueKind.DWord && p.Value == "32"), "typed group separator");
            Check(plan.Any(p => p.KeyPath == shell + "85Remote" && p.ValueName == "MUIVerb" && p.Value == "원격 주소…") &&
                  plan.Any(p => p.KeyPath == shell + "85Remote\\command" && p.ValueName is null && p.Value.EndsWith("--action remote", StringComparison.Ordinal)),
                "flat Remote action uses the expected label and command in " + scope);
            Check(!plan.Any(p => p.KeyPath == shell + "85Remote" && p.ValueName == "CommandFlags"), "Remote address action is not separated");
            Check(!plan.Any(p => p.KeyPath.Contains(@"\60Repository", StringComparison.Ordinal) ||
                                p.ValueName == "ExtendedSubCommandsKey" && p.Value.Contains("Repository", StringComparison.Ordinal)), "menu has no nested Repository submenu");
            Check(!plan.Any(p => p.KeyPath == shell + expected[0] && p.ValueName == "CommandFlags"), "first submenu item has no separator for " + scope);
            var commands = plan.Where(p => p.KeyPath.StartsWith(shell, StringComparison.Ordinal) && p.KeyPath.EndsWith(@"\command", StringComparison.Ordinal)).ToArray();
            Check(commands.Length == expected.Length, "exact nested action set for " + scope);
            foreach (var verb in plan.Where(p => p.ValueName == "MUIVerb" && p.KeyPath.StartsWith(shell, StringComparison.Ordinal) &&
                                                  !p.KeyPath[shell.Length..].Contains('\\')))
                CheckSelectionModel(plan, verb.KeyPath, selectionModelIsSingle, "submenu verb " + scope);

            if (settings.ShowCommitAtTopLevel)
                CheckQuickVerb(plan, classes + scope + @"\shell\MoaVcs.Commit", executable, "commit", scope == @"Directory\Background" ? "%V" : "%1", selectionModelIsSingle);
            else
                Check(!plan.Any(p => p.KeyPath.StartsWith(classes + scope + @"\shell\MoaVcs.Commit", StringComparison.Ordinal)), "commit remains nested when promotion is disabled");
            if (!isFile && settings.ShowUpdateAtTopLevel)
                CheckQuickVerb(plan, classes + scope + @"\shell\MoaVcs.Update", executable, "update", scope == @"Directory\Background" ? "%V" : "%1", selectionModelIsSingle);
            else
                Check(!plan.Any(p => p.KeyPath.StartsWith(classes + scope + @"\shell\MoaVcs.Update", StringComparison.Ordinal)), "update is folder-only and nested when promotion is disabled");
        }

        foreach (var command in plan.Where(p => p.ValueName is null && p.KeyPath.EndsWith(@"\command", StringComparison.Ordinal)))
        {
            var parsed = ParseWindowsCommandLine(command.Value.Replace("%1", @"C:\Work & Notes\한글\", StringComparison.Ordinal)
                .Replace("%V", @"C:\Work & Notes\한글\", StringComparison.Ordinal));
            var options = LaunchOptions.Parse(parsed.Skip(1).ToArray());
            Check(options.Path == @"C:\Work & Notes\한글\", "every menu command preserves literal shell target");
        }
    }

    private static void CheckQuickVerb(IReadOnlyList<RegistryValuePlan> plan, string key, string executable, string action, string target,
        bool selectionModelIsSingle)
    {
        Check(plan.Any(p => p.KeyPath == key && p.ValueName == "MoaVcsOwner" && !string.IsNullOrWhiteSpace(p.Value)), action + " quick verb has ownership marker");
        Check(plan.Any(p => p.KeyPath == key && p.ValueName == "MoaVcsExecutable" && p.Value == Path.GetFullPath(executable)), action + " quick verb stores executable");
        Check(plan.Any(p => p.KeyPath == key && p.ValueName == "Icon" && p.Value == ExplorerIntegration.QuoteArgument(Path.GetFullPath(executable)) + ",0"), action + " quick verb has app icon");
        CheckSelectionModel(plan, key, selectionModelIsSingle, action + " quick verb");
        Check(plan.Any(p => p.KeyPath == key && p.ValueName == "MUIVerb" && !string.IsNullOrWhiteSpace(p.Value)), action + " quick verb has label");
        Check(plan.Any(p => p.KeyPath == key + @"\command" && p.ValueName is null && p.Value.Contains("--action " + action, StringComparison.Ordinal) && p.Value.Contains(target, StringComparison.Ordinal)), action + " quick command gets the correct shell target");
    }

    private static void CheckSelectionModel(IReadOnlyList<RegistryValuePlan> plan, string key, bool shouldBeSingle, string description)
    {
        var values = plan.Where(p => p.KeyPath == key && p.ValueName == "MultiSelectModel").ToArray();
        Check(shouldBeSingle
                ? values.Length == 1 && values[0].Value == "Single" && values[0].Kind == Microsoft.Win32.RegistryValueKind.String
                : values.Length == 0,
            shouldBeSingle ? description + " requires Single selection" : description + " must omit MultiSelectModel for background menus");
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
                ExecutablePaths = new Dictionary<VcsKind, string> { [VcsKind.Perforce] = @"C:\Tools\p4.exe" },
                ExternalTools = new Dictionary<ExternalToolKind, ExternalToolSettings>
                {
                    [ExternalToolKind.Diff] = new() { UseExternal = true, ExecutablePath = @"C:\Tools\diff.exe", ArgumentTemplates = ["{left}", "{right}"] }
                },
                ExplorerMenu = new ExplorerMenuSettings { ShowCommitAtTopLevel = false, ShowUpdateAtTopLevel = true }
            });
            var roundTrip = store.Load();
            Check(roundTrip.RecentRepositories.Single().Path == @"C:\Work Area\repo", "settings persist recent paths");
            Check(roundTrip.ExecutablePaths[VcsKind.Perforce] == @"C:\Tools\p4.exe", "settings persist executable paths");
            Check(!roundTrip.ExplorerMenu.ShowCommitAtTopLevel && roundTrip.ExplorerMenu.ShowUpdateAtTopLevel, "explorer menu preferences round-trip");

            store.SaveExplorerMenu(new ExplorerMenuSettings { ShowCommitAtTopLevel = true, ShowUpdateAtTopLevel = false });
            var menuSaved = store.Load();
            Check(menuSaved.ExplorerMenu.ShowCommitAtTopLevel && !menuSaved.ExplorerMenu.ShowUpdateAtTopLevel, "saving explorer menu preferences updates both toggles");
            Check(menuSaved.RecentRepositories.Single().Path == @"C:\Work Area\repo" && menuSaved.ExecutablePaths[VcsKind.Perforce] == @"C:\Tools\p4.exe",
                "saving explorer menu preferences preserves recent repositories and executable paths");
            Check(menuSaved.ExternalTools[ExternalToolKind.Diff].UseExternal &&
                  menuSaved.ExternalTools[ExternalToolKind.Diff].ExecutablePath == @"C:\Tools\diff.exe" &&
                  menuSaved.ExternalTools[ExternalToolKind.Diff].ArgumentTemplates.SequenceEqual(["{left}", "{right}"]),
                "saving explorer menu preferences preserves external tool settings");

            const string invalid = "{ this is not valid JSON";
            File.WriteAllText(file, invalid);
            var recovered = store.Load();
            Check(recovered.RecentRepositories.Count == 0 && !string.IsNullOrWhiteSpace(store.LoadWarning), "bad settings recover with a warning");
            Check(File.ReadAllText(file) == invalid, "bad settings remain available for recovery");
            Throws<IOException>(() => store.SaveExplorerMenu(new ExplorerMenuSettings { ShowCommitAtTopLevel = false, ShowUpdateAtTopLevel = false }),
                "menu settings do not overwrite corrupt settings");
            Check(File.ReadAllText(file) == invalid, "failed menu-only save leaves corrupt settings intact");

            File.WriteAllText(file, "{\"RecentRepositories\":[],\"ExecutablePaths\":{},\"ExternalTools\":{}}");
            var legacy = store.Load();
            Check(store.LoadWarning is null && legacy.ExplorerMenu.ShowCommitAtTopLevel && legacy.ExplorerMenu.ShowUpdateAtTopLevel,
                "settings written before menu preferences receive new defaults");
            File.WriteAllText(file, "{\"ExplorerMenu\":null}");
            var nullMenu = store.Load();
            Check(store.LoadWarning is null && nullMenu.ExplorerMenu.ShowCommitAtTopLevel && nullMenu.ExplorerMenu.ShowUpdateAtTopLevel,
                "a null menu settings object recovers to defaults");
            File.WriteAllText(file, "{\"ExplorerMenu\":{}}");
            var partialMenu = store.Load();
            Check(store.LoadWarning is null && partialMenu.ExplorerMenu.ShowCommitAtTopLevel && partialMenu.ExplorerMenu.ShowUpdateAtTopLevel,
                "missing menu preference fields use their defaults");
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
