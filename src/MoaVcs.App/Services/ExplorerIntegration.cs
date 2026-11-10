using System.IO;
using Microsoft.Win32;
using System.Security;

namespace MoaVcs.App.Services;

/// <summary>Optional, per-user Windows Explorer integration. It is never installed automatically.</summary>
public static class ExplorerIntegration
{
    private const string Owner = "MoaVcs.ExplorerMenu.v1";
    private const string MenuName = "MoaVcs";
    private const string MenuLabel = "MoaVCS";
    private static readonly string[] Scopes = ["Directory", "Directory\\Background", "*"];
    private sealed record MenuEntry(string Id, string Label, string Action, bool Separator = false);
    private static readonly MenuEntry[] FolderMenu =
    [
        new("10Update", "업데이트 / Pull…", "update"),
        new("20Push", "푸시…", "push"),
        new("30Commit", "커밋…", "commit"),
        new("40Log", "로그 보기", "log", Separator: true),
        new("50Status", "변경 사항 확인", "status"),
        new("60Open", "MoaVCS에서 열기", "open", Separator: true),
        new("70Clone", "복제 / 체크아웃…", "clone"),
        new("80Init", "여기에 저장소 만들기…", "init"),
        new("85Remote", "원격 주소…", "remote"),
        new("90Settings", "설정…", "settings", Separator: true)
    ];
    private static readonly MenuEntry[] FileMenu =
    [
        new("10Commit", "커밋…", "commit"),
        new("20Diff", "차이 비교 (Diff)", "diff", Separator: true),
        new("30Log", "로그 보기", "log", Separator: true),
        new("40Blame", "행별 이력 (Blame)", "blame"),
        new("50Status", "변경 사항 확인", "status"),
        new("60Open", "MoaVCS에서 열기", "open", Separator: true),
        new("85Remote", "원격 주소…", "remote"),
        new("90Settings", "설정…", "settings", Separator: true)
    ];

    public static bool IsInstalled(string? expectedExecutable = null)
    {
        using var key = Registry.CurrentUser.OpenSubKey(MenuPath("Directory"), writable: false);
        return string.Equals(key?.GetValue("MoaVcsOwner") as string, Owner, StringComparison.Ordinal) &&
            (expectedExecutable is null || CommandTargetsExecutable("Directory", expectedExecutable));
    }

    public static IReadOnlyList<RegistryValuePlan> BuildRegistrationPlan(string executablePath, ExplorerMenuSettings? settings = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("앱 경로가 필요합니다.", nameof(executablePath));
        var exe = Path.GetFullPath(executablePath);
        settings ??= new ExplorerMenuSettings();
        var values = new List<RegistryValuePlan>();
        foreach (var scope in Scopes)
        {
            var menuPath = MenuPath(scope);
            values.Add(new(menuPath, "MUIVerb", MenuLabel));
            values.Add(new(menuPath, "MoaVcsOwner", Owner));
            values.Add(new(menuPath, "ExtendedSubCommandsKey", CommandKeyName(scope)));
            values.Add(new(menuPath, "Icon", QuoteArgument(exe) + ",0"));
            values.Add(new(menuPath, "MultiSelectModel", "Single"));
        }
        foreach (var scope in Scopes)
        {
            values.Add(new(CommandRoot(scope), "MoaVcsOwner", Owner));
            values.Add(new(CommandRoot(scope), "MoaVcsExecutable", exe));
            var firstSubmenuEntry = true;
            foreach (var entry in scope == "*" ? FileMenu : FolderMenu)
            {
                var promoted = entry.Action == "commit" && settings.ShowCommitAtTopLevel ||
                    entry.Action == "update" && settings.ShowUpdateAtTopLevel;
                var verb = promoted ? QuickMenuPath(scope, entry.Action) : CommandRoot(scope) + "\\Shell\\" + entry.Id;
                values.Add(new(verb, "MUIVerb", promoted ? "MoaVCS " + entry.Label : entry.Label));
                values.Add(new(verb, "MultiSelectModel", "Single"));
                if (promoted)
                {
                    values.Add(new(verb, "MoaVcsOwner", Owner));
                    values.Add(new(verb, "MoaVcsExecutable", exe));
                    values.Add(new(verb, "Icon", QuoteArgument(exe) + ",0"));
                }
                else
                {
                    // Do not leave a leading separator when a command moves to the first level.
                    if (entry.Separator && !firstSubmenuEntry)
                        values.Add(new(verb, "CommandFlags", "32", RegistryValueKind.DWord));
                    firstSubmenuEntry = false;
                }
                AddCommand(verb, entry.Action, scope);
            }
        }
        return values;

        void AddCommand(string verb, string action, string scope)
        {
            // %V supplies the background directory; %1 supplies the selected file/folder.
            var target = scope == "Directory\\Background" ? "%V" : "%1";
            // The forbidden path character protects a trailing backslash before the closing quote.
            var shellPathOption = "--shell-path \"" + target + "|\"";
            values.Add(new(verb + "\\command", null, QuoteArgument(exe) + " " + shellPathOption + " --action " + action));
        }
    }

    public static string QuoteArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"')) return argument;
        var result = new System.Text.StringBuilder(argument.Length + 2).Append('"');
        var slashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(ch);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    public static void Install(string executablePath, ExplorerMenuSettings? settings = null)
    {
        var plan = BuildRegistrationPlan(executablePath, settings);
        // Never overwrite a menu key created by a different app or user.
        foreach (var path in plan.Where(value => value.ValueName == "MoaVcsOwner").Select(value => value.KeyPath))
        {
            using var existing = Registry.CurrentUser.OpenSubKey(path, writable: false);
            var owner = existing?.GetValue("MoaVcsOwner") as string;
            if (existing is not null && !string.Equals(owner, Owner, StringComparison.Ordinal))
                throw new InvalidOperationException($"탐색기 메뉴 위치가 이미 다른 앱에서 사용 중입니다: {path}");
        }

        try
        {
            // Rebuild only the owner-checked MoaVCS trees so legacy verbs do not survive updates.
            UninstallOwnedKeys();
            foreach (var group in plan.GroupBy(p => p.KeyPath, StringComparer.OrdinalIgnoreCase))
            {
                using var key = Registry.CurrentUser.CreateSubKey(group.Key, writable: true)
                    ?? throw new SecurityException($"레지스트리 키를 만들 수 없습니다: {group.Key}");
                foreach (var value in group)
                    key.SetValue(value.ValueName ?? "", value.Kind == RegistryValueKind.DWord
                        ? int.Parse(value.Value, System.Globalization.CultureInfo.InvariantCulture) : value.Value, value.Kind);
            }
        }
        catch
        {
            // Roll back only keys carrying our ownership marker.
            UninstallOwnedKeys();
            throw;
        }
    }

    public static void Uninstall(string? expectedExecutable = null) => UninstallOwnedKeys(expectedExecutable);

    private static void UninstallOwnedKeys(string? expectedExecutable = null)
    {
        // Shortcuts carry their own target: an older install must not remove a newer install's verbs.
        foreach (var scope in Scopes)
        foreach (var action in scope == "*" ? new[] { "commit" } : new[] { "commit", "update" })
        {
            var path = QuickMenuPath(scope, action);
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            if (string.Equals(key?.GetValue("MoaVcsOwner") as string, Owner, StringComparison.Ordinal) &&
                (expectedExecutable is null || string.Equals(key?.GetValue("MoaVcsExecutable") as string,
                    Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase)))
                Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        var scopes = Scopes.Where(scope => expectedExecutable is null || CommandTargetsExecutable(scope, expectedExecutable)).ToArray();
        foreach (var scope in scopes)
        {
            var path = MenuPath(scope);
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            if (string.Equals(key?.GetValue("MoaVcsOwner") as string, Owner, StringComparison.Ordinal))
                Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        foreach (var scope in scopes)
        {
            var root = CommandRoot(scope);
            using var owned = Registry.CurrentUser.OpenSubKey(root, writable: false);
            if (string.Equals(owned?.GetValue("MoaVcsOwner") as string, Owner, StringComparison.Ordinal))
                Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    private static bool CommandTargetsExecutable(string scope, string executable)
    {
        using var root = Registry.CurrentUser.OpenSubKey(CommandRoot(scope));
        if (root?.GetValue("MoaVcsExecutable") is string target)
            return target.Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
        // v0.3 and earlier used an unnumbered Open verb without an executable marker.
        using var command = Registry.CurrentUser.OpenSubKey(CommandRoot(scope) + "\\Shell\\Open\\command");
        return command?.GetValue("") is string value && value.StartsWith(QuoteArgument(Path.GetFullPath(executable)) + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static string MenuPath(string scope) => $"Software\\Classes\\{scope}\\shell\\{MenuName}";
    private static string QuickMenuPath(string scope, string action) =>
        $"Software\\Classes\\{scope}\\shell\\{MenuName}." + (action == "commit" ? "Commit" : "Update");
    private static string CommandKeyName(string scope) => scope switch
    {
        "Directory\\Background" => "Directory\\ContextMenus\\MoaVcs.BackgroundCommands",
        "*" => "*\\ContextMenus\\MoaVcs.FileCommands",
        _ => "Directory\\ContextMenus\\MoaVcs.Commands"
    };
    private static string CommandRoot(string scope) => "Software\\Classes\\" + CommandKeyName(scope);
}

public sealed record RegistryValuePlan(string KeyPath, string? ValueName, string Value, RegistryValueKind Kind = RegistryValueKind.String);
