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
    private sealed record MenuEntry(string Id, string Label, string? Action = null, bool Separator = false, MenuEntry[]? Children = null);
    private static readonly MenuEntry[] FolderMenu =
    [
        new("10Status", "변경 사항 확인", "status"),
        new("20Commit", "커밋…", "commit"),
        new("30Update", "업데이트 / Pull…", "update"),
        new("40Push", "푸시…", "push"),
        new("50Log", "저장소 이력 보기", "log", Separator: true),
        new("60Repository", "저장소", Separator: true, Children:
        [
            new("10Open", "MoaVCS에서 열기", "open"),
            new("20Clone", "저장소 복제 / 체크아웃…", "clone"),
            new("30Init", "여기에 저장소 만들기…", "init")
        ]),
        new("70Settings", "도구 및 탐색기 설정…", "settings", Separator: true)
    ];
    private static readonly MenuEntry[] FileMenu =
    [
        new("10Diff", "변경 내용 비교", "diff"),
        new("20Blame", "행별 이력 (Blame)", "blame"),
        new("30Log", "저장소 이력 보기", "log"),
        new("40Status", "변경 사항 확인", "status", Separator: true),
        new("50Commit", "커밋…", "commit"),
        new("60Open", "MoaVCS에서 열기", "open", Separator: true),
        new("70Settings", "도구 및 탐색기 설정…", "settings", Separator: true)
    ];

    public static bool IsInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(MenuPath("Directory"), writable: false);
        return string.Equals(key?.GetValue("MoaVcsOwner") as string, Owner, StringComparison.Ordinal);
    }

    public static IReadOnlyList<RegistryValuePlan> BuildRegistrationPlan(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("앱 경로가 필요합니다.", nameof(executablePath));
        var exe = Path.GetFullPath(executablePath);
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
            AddEntries(CommandRoot(scope) + "\\Shell", scope == "*" ? FileMenu : FolderMenu, scope);
        }
        return values;

        void AddEntries(string commands, IEnumerable<MenuEntry> entries, string scope)
        {
            foreach (var entry in entries)
            {
                // Stable ordinal keys keep the classic cascade in workflow order.
                var verb = commands + "\\" + entry.Id;
                values.Add(new(verb, "MUIVerb", entry.Label));
                values.Add(new(verb, "MultiSelectModel", "Single"));
                if (entry.Separator) values.Add(new(verb, "CommandFlags", "32", RegistryValueKind.DWord));
                if (entry.Children is { } children)
                {
                    var nestedRoot = verb + "\\Commands";
                    values.Add(new(verb, "ExtendedSubCommandsKey", nestedRoot["Software\\Classes\\".Length..]));
                    AddEntries(nestedRoot + "\\Shell", children, scope);
                    continue;
                }
                var commandPath = verb + "\\command";
                // %V supplies the directory for background clicks; %1 supplies selected files/folders.
                var target = scope == "Directory\\Background" ? "%V" : "%1";
                // A trailing backslash before a shell-inserted closing quote escapes that quote on Windows.
                // The sentinel uses a character forbidden in Windows file names, so it cannot collide.
                var shellPathOption = "--shell-path \"" + target + "|\"";
                var invocation = QuoteArgument(exe) + " " + shellPathOption + " --action " + entry.Action;
                values.Add(new(commandPath, null, invocation));
            }
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

    public static void Install(string executablePath)
    {
        var plan = BuildRegistrationPlan(executablePath);
        // Never overwrite a menu key created by a different app or user.
        foreach (var scope in Scopes)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(MenuPath(scope), writable: false);
            var owner = existing?.GetValue("MoaVcsOwner") as string;
            if (existing is not null && !string.Equals(owner, Owner, StringComparison.Ordinal))
                throw new InvalidOperationException($"탐색기 메뉴 위치가 이미 다른 앱에서 사용 중입니다: {scope}");
        }
        foreach (var scope in Scopes)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(CommandRoot(scope), writable: false);
            var owner = existing?.GetValue("MoaVcsOwner") as string;
            if (existing is not null && !string.Equals(owner, Owner, StringComparison.Ordinal))
                throw new InvalidOperationException("moavcs 탐색기 명령 이름이 이미 사용 중입니다.");
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
    private static string CommandKeyName(string scope) => scope switch
    {
        "Directory\\Background" => "Directory\\ContextMenus\\MoaVcs.BackgroundCommands",
        "*" => "*\\ContextMenus\\MoaVcs.FileCommands",
        _ => "Directory\\ContextMenus\\MoaVcs.Commands"
    };
    private static string CommandRoot(string scope) => "Software\\Classes\\" + CommandKeyName(scope);
}

public sealed record RegistryValuePlan(string KeyPath, string? ValueName, string Value, RegistryValueKind Kind = RegistryValueKind.String);
