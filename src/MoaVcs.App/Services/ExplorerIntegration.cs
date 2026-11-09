using System.IO;
using Microsoft.Win32;
using System.Security;

namespace MoaVcs.App.Services;

/// <summary>Optional, per-user Windows Explorer integration. It is never installed automatically.</summary>
public static class ExplorerIntegration
{
    private const string Owner = "MoaVcs.ExplorerMenu.v1";
    private const string MenuName = "MoaVcs";
    private const string MenuLabel = "moavcs";
    private static readonly string[] Scopes = ["Directory", "Directory\\Background", "*"];
    private static readonly (string Id, string Label, string Action, bool BackgroundOnly)[] Verbs =
    [
        ("Open", "moavcs 열기", "open", false),
        ("Status", "변경 사항 확인", "status", false),
        ("Diff", "변경 내용 비교", "diff", false),
        ("Blame", "행별 이력 보기 (Blame)", "blame", false),
        ("Log", "변경 이력 보기", "log", false),
        ("Commit", "커밋", "commit", false)
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
        }
        foreach (var scope in Scopes)
        {
            var commands = CommandRoot(scope) + "\\Shell";
            values.Add(new(CommandRoot(scope), "MoaVcsOwner", Owner));
            foreach (var (id, label, action, _) in Verbs)
            {
                if (action == "blame" && scope != "*") continue;
                var verb = commands + "\\" + id;
                values.Add(new(verb, "MUIVerb", label));
                var commandPath = verb + "\\command";
                // %V supplies the directory for background clicks; %1 supplies selected files/folders.
                var target = scope == "Directory\\Background" ? "%V" : "%1";
                // A trailing backslash before a shell-inserted closing quote escapes that quote on Windows.
                // The sentinel uses a character forbidden in Windows file names, so it cannot collide.
                var shellPathOption = "--shell-path \"" + target + "|\"";
                var invocation = QuoteArgument(exe) + " " + shellPathOption + (action == "open" ? "" : " --action " + action);
                values.Add(new(commandPath, null, invocation));
            }
        }
        return values;
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
            foreach (var group in plan.GroupBy(p => p.KeyPath, StringComparer.OrdinalIgnoreCase))
            {
                using var key = Registry.CurrentUser.CreateSubKey(group.Key, writable: true)
                    ?? throw new SecurityException($"레지스트리 키를 만들 수 없습니다: {group.Key}");
                foreach (var value in group)
                    key.SetValue(value.ValueName ?? "", value.Value, RegistryValueKind.String);
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

public sealed record RegistryValuePlan(string KeyPath, string? ValueName, string Value);
