using System.Diagnostics;
using System.IO;
using MoaVcs.Core;

namespace MoaVcs.App.Services;

public sealed record VcsTool(VcsKind Kind, string Executable, Uri DownloadUrl, string DownloadHint);

public static class VcsTools
{
    private static readonly IReadOnlyDictionary<VcsKind, VcsTool> Tools = new Dictionary<VcsKind, VcsTool>
    {
        [VcsKind.Git] = new(VcsKind.Git, "git", new("https://git-scm.com/install/windows"), "Git for Windows의 설치 파일을 다운로드하세요."),
        [VcsKind.Svn] = new(VcsKind.Svn, "svn", new("https://sliksvn.com/download/"), "Windows SVN 명령줄 도구를 제공하는 SlikSVN의 다운로드 페이지입니다."),
        [VcsKind.Perforce] = new(VcsKind.Perforce, "p4", new("https://www.perforce.com/downloads/helix-command-line-client-p4"), "Perforce P4 CLI에서 Windows 설치 파일을 선택하세요."),
        [VcsKind.Lore] = new(VcsKind.Lore, "lore", new("https://github.com/EpicGames/lore/releases/latest"), "Epic Games Lore의 최신 릴리스에서 Windows 설치 파일을 선택하세요.")
    };

    public static VcsTool Get(VcsKind kind) => Tools.TryGetValue(kind, out var tool)
        ? tool : throw new ArgumentOutOfRangeException(nameof(kind));

    public static void OpenDownload(Uri uri)
    {
        // Only application-owned download URLs can be launched, never a repository URL or command.
        if (!Tools.Values.Any(tool => tool.DownloadUrl == uri)) throw new ArgumentException("지원하는 공식 다운로드 주소가 아닙니다.", nameof(uri));
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public static string ResolveExecutable(VcsKind kind, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        var tool = Get(kind);
        // An installer may update PATH while MoaVCS is open. Read the persisted PATH
        // again without overwriting this process's environment or the user's settings.
        var paths = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
        };
        foreach (var directory in paths.Where(path => path is not null)
                     .SelectMany(path => path!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var expanded = Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(expanded)) continue;
            var candidate = Path.Combine(expanded, tool.Executable + ".exe");
            if (File.Exists(candidate)) return candidate;
        }
        return tool.Executable;
    }
}
