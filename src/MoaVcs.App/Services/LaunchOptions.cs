using MoaVcs.Core;

namespace MoaVcs.App.Services;

public sealed record LaunchOptions(string? Path, string Action = "open", VcsKind? Vcs = null,
    bool InstallContextMenu = false, bool UninstallContextMenu = false, bool Headless = false, string? OverlayOperation = null)
{
    private static readonly HashSet<string> AllowedActions = new(StringComparer.OrdinalIgnoreCase)
        { "open", "status", "diff", "blame", "log", "commit", "update", "push", "clone", "init", "remote", "settings" };

    public static LaunchOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? path = null;
        string action = "open";
        VcsKind? vcs = null;
        bool install = false, uninstall = false, headless = false;
        string? overlay = null;
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token is "--overlay-worker" or "--pause-overlays" or "--resume-overlays" or "--disable-overlays")
            {
                if (overlay is not null) throw new FormatException("상태 배지 작업을 동시에 실행할 수 없습니다.");
                overlay = token;
                continue;
            }
            if (token.Equals("--headless", StringComparison.OrdinalIgnoreCase)) { headless = true; continue; }
            if (token.Equals("--install-context-menu", StringComparison.OrdinalIgnoreCase)) { install = true; continue; }
            if (token.Equals("--uninstall-context-menu", StringComparison.OrdinalIgnoreCase)) { uninstall = true; continue; }
            if (token is not ("--path" or "--shell-path" or "--action" or "--vcs"))
                throw new FormatException($"알 수 없는 시작 옵션입니다: {token}");
            if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                throw new FormatException($"{token} 뒤에 값을 입력해야 합니다.");
            var value = args[i];
            switch (token)
            {
                case "--path": path = value; break;
                case "--shell-path":
                    if (!value.EndsWith('|'))
                        throw new FormatException("탐색기 경로의 끝 표시가 없습니다.");
                    path = value[..^1];
                    if (path.Length == 0) throw new FormatException("탐색기에서 전달된 경로가 비어 있습니다.");
                    break;
                case "--action":
                    if (!AllowedActions.Contains(value)) throw new FormatException($"지원하지 않는 작업입니다: {value}");
                    action = value.ToLowerInvariant();
                    break;
                case "--vcs":
                    if (!Enum.TryParse<VcsKind>(value, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
                        throw new FormatException($"지원하지 않는 버전 관리 시스템입니다: {value}");
                    vcs = kind;
                    break;
            }
        }
        if (install && uninstall) throw new FormatException("탐색기 메뉴 설치와 제거를 동시에 실행할 수 없습니다.");
        if (overlay is not null && (install || uninstall || path is not null || action != "open" || vcs is not null))
            throw new FormatException("상태 배지 작업은 다른 작업과 함께 실행할 수 없습니다.");
        if (headless && (overlay is null && !install && !uninstall || path is not null || action != "open" || vcs is not null))
            throw new FormatException("--headless는 탐색기 메뉴 등록 또는 제거 작업에만 사용할 수 있습니다.");
        return new LaunchOptions(path, action, vcs, install, uninstall, headless, overlay);
    }
}
