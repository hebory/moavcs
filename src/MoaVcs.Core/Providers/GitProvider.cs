using System.Text;

namespace MoaVcs.Core.Providers;

/// <summary>Git adapter built on the official git executable.</summary>
public sealed class GitProvider(ICommandRunner runner, string executable = "git") : IVcsProvider
{
    public VcsKind Kind => VcsKind.Git;
    public string Name => "Git";
    public string Executable => executable;
    public VcsCapabilities Capabilities => VcsCapabilities.Diff | VcsCapabilities.History | VcsCapabilities.Stage |
        VcsCapabilities.Unstage | VcsCapabilities.Commit | VcsCapabilities.Update | VcsCapabilities.Push |
        VcsCapabilities.Initialize | VcsCapabilities.Clone;

    public async Task<RepositoryInfo> GetRepositoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new VcsException($"폴더를 찾을 수 없습니다: {fullPath}");
        var rootResult = await Run(fullPath, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);
        if (!rootResult.Success)
            throw new VcsException("이 폴더 또는 상위 폴더에서 Git 저장소를 찾을 수 없습니다. 기존 저장소를 열거나 새 저장소를 초기화하세요.");
        var root = Path.GetFullPath(rootResult.StandardOutput.Trim());
        var branchResult = await Run(root, ["symbolic-ref", "--short", "-q", "HEAD"], cancellationToken).ConfigureAwait(false);
        var branch = branchResult.Success && !string.IsNullOrWhiteSpace(branchResult.StandardOutput)
            ? branchResult.StandardOutput.Trim() : await GetShortHeadAsync(root, cancellationToken).ConfigureAwait(false);
        return new RepositoryInfo(Kind, root, string.IsNullOrEmpty(branch) ? "(no commits yet)" : branch);
    }

    public async Task<IReadOnlyList<FileChange>> GetStatusAsync(string root, CancellationToken cancellationToken = default)
    {
        root = NormalizeRoot(root);
        var result = (await Run(root, ["-c", "core.quotepath=false", "status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken)
            .ConfigureAwait(false)).EnsureSuccess();
        var fields = result.StandardOutput.Split('\0');
        var changes = new List<FileChange>();
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (field.Length < 4 || field[2] != ' ') continue;
            var x = field[0];
            var y = field[1];
            var path = field[3..];
            string? original = null;
            if ((x is 'R' or 'C' || y is 'R' or 'C') && i + 1 < fields.Length)
                original = fields[++i];
            ValidateRelativePath(root, path);
            var status = StatusLabel(x, y);
            changes.Add(new FileChange(path, status, x.ToString(), y.ToString(), original));
        }
        return changes;
    }

    public async Task<string> GetDiffAsync(string root, FileChange? file, bool staged = false, CancellationToken cancellationToken = default)
    {
        root = NormalizeRoot(root);
        if (file is null)
        {
            if (staged)
                return (await Run(root, ["--literal-pathspecs", "--no-pager", "diff", "--cached", "--no-ext-diff", "--no-textconv", "--no-color", "--"], cancellationToken)
                    .ConfigureAwait(false)).EnsureSuccess().StandardOutput;
            var status = await GetStatusAsync(root, cancellationToken).ConfigureAwait(false);
            var untracked = status.Where(c => c.IndexStatus == "?" && c.WorkTreeStatus == "?").ToArray();
            var trackedDiff = (await Run(root, ["--literal-pathspecs", "--no-pager", "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--"], cancellationToken)
                .ConfigureAwait(false)).EnsureSuccess().StandardOutput;
            if (untracked.Length == 0) return trackedDiff;
            var builder = new StringBuilder(trackedDiff);
            foreach (var item in untracked.Take(30))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var preview = await GetUntrackedDiffAsync(root, item.Path, cancellationToken).ConfigureAwait(false);
                if (builder.Length + preview.Length > ProcessCommandRunner.MaxOutputCharacters)
                    return builder + Environment.NewLine + "[출력 크기 제한에 도달했습니다.]";
                builder.Append(preview);
            }
            return builder.ToString();
        }

        var safePath = ValidateRelativePath(root, file.Path);
        if (!staged && file.IndexStatus == "?" && file.WorkTreeStatus == "?")
            return await GetUntrackedDiffAsync(root, safePath, cancellationToken).ConfigureAwait(false);
        var args = new List<string> { "--literal-pathspecs", "--no-pager", "diff" };
        if (staged) args.Add("--cached");
        args.AddRange(["--no-ext-diff", "--no-textconv", "--no-color", "--", safePath]);
        return (await Run(root, args, cancellationToken).ConfigureAwait(false)).EnsureSuccess().StandardOutput;
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(string root, CancellationToken cancellationToken = default)
    {
        root = NormalizeRoot(root);
        var head = await Run(root, ["rev-parse", "--verify", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (!head.Success)
        {
            // An initialized repository may have no first commit yet.
            await GetRepositoryAsync(root, cancellationToken).ConfigureAwait(false);
            return [];
        }
        const string format = "--pretty=format:%H%x00%an%x00%aI%x00%s%x00";
        var output = (await Run(root, ["log", "-n", "100", format], cancellationToken).ConfigureAwait(false)).EnsureSuccess().StandardOutput;
        var fields = output.Split('\0');
        var entries = new List<HistoryEntry>();
        for (var i = 0; i + 3 < fields.Length; i += 4)
        {
            if (fields[i].Length == 0) continue;
            entries.Add(new HistoryEntry(fields[i].TrimStart('\r', '\n'), fields[i + 1], fields[i + 2], fields[i + 3]));
        }
        return entries;
    }

    public async Task<CommandResult> ExecuteAsync(VcsRequest request, CancellationToken cancellationToken = default)
    {
        var working = Path.GetFullPath(request.WorkingDirectory);
        switch (request.Operation)
        {
            case VcsOperation.Initialize:
                Directory.CreateDirectory(working);
                return (await Run(working, ["init"], cancellationToken).ConfigureAwait(false)).EnsureSuccess();
            case VcsOperation.Clone:
                if (string.IsNullOrWhiteSpace(request.Remote) || string.IsNullOrWhiteSpace(request.Destination))
                    throw new VcsException("복제할 저장소 주소와 대상 폴더를 입력하세요.");
                var dest = Path.GetFullPath(request.Destination);
                if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
                    throw new VcsException("복제 대상 폴더가 비어 있어야 합니다.");
                var cloneParent = Directory.Exists(working) ? working : Path.GetDirectoryName(dest)!;
                Directory.CreateDirectory(cloneParent);
                return (await Run(cloneParent,
                    ["clone", "--", request.Remote, dest], cancellationToken).ConfigureAwait(false)).EnsureSuccess();
            case VcsOperation.Stage:
            case VcsOperation.Unstage:
                var stageRoot = await RequireRepositoryRootAsync(working, cancellationToken).ConfigureAwait(false);
                var paths = ValidateRequestPaths(stageRoot, request.Paths);
                if (paths.Count == 0) throw new VcsException("먼저 파일을 하나 이상 선택하세요.");
                var stageArgs = request.Operation == VcsOperation.Stage
                    ? new List<string> { "--literal-pathspecs", "add", "--" }
                    : new List<string> { "--literal-pathspecs", "restore", "--staged", "--" };
                stageArgs.AddRange(paths);
                var stage = await Run(stageRoot, stageArgs, cancellationToken).ConfigureAwait(false);
                if (request.Operation == VcsOperation.Unstage && !stage.Success)
                {
                    var head = await Run(stageRoot, ["rev-parse", "--verify", "HEAD"], cancellationToken).ConfigureAwait(false);
                    if (!head.Success)
                        return (await Run(stageRoot, ["--literal-pathspecs", "rm", "--cached", "-f", "-r", "--ignore-unmatch", "--", .. paths], cancellationToken)
                            .ConfigureAwait(false)).EnsureSuccess();
                }
                return stage.EnsureSuccess();
            case VcsOperation.Commit:
                var commitRoot = await RequireRepositoryRootAsync(working, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(request.Message)) throw new VcsException("커밋 메시지를 입력하세요.");
                var staged = (await GetStatusAsync(commitRoot, cancellationToken).ConfigureAwait(false)).Any(c => c.IsStaged);
                if (!staged) throw new VcsException("커밋할 스테이징된 파일이 없습니다. 파일을 선택하고 먼저 스테이지하세요.");
                return (await Run(commitRoot, ["commit", "--file=-"], cancellationToken, request.Message + "\n").ConfigureAwait(false)).EnsureSuccess();
            case VcsOperation.Update:
                var updateRoot = await RequireRepositoryRootAsync(working, cancellationToken).ConfigureAwait(false);
                return (await Run(updateRoot, ["pull", "--ff-only"], cancellationToken).ConfigureAwait(false)).EnsureSuccess();
            case VcsOperation.Push:
                var pushRoot = await RequireRepositoryRootAsync(working, cancellationToken).ConfigureAwait(false);
                return (await Run(pushRoot, ["push"], cancellationToken).ConfigureAwait(false)).EnsureSuccess();
            default: throw new VcsException("지원하지 않는 Git 작업입니다.");
        }
    }

    private async Task<string> GetUntrackedDiffAsync(string root, string path, CancellationToken cancellationToken)
    {
        var safePath = ValidateRelativePath(root, path);
        var fullPath = Path.Combine(root, safePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath)) return $"새 파일을 찾을 수 없습니다: {safePath}{Environment.NewLine}";
        var info = new FileInfo(fullPath);
        if (info.Length > 1024 * 1024) return $"--- {safePath}{Environment.NewLine}새 파일 미리보기는 1 MiB까지 지원합니다.{Environment.NewLine}";
        var result = await Run(root, ["--literal-pathspecs", "--no-pager", "diff", "--no-index", "--no-ext-diff", "--no-textconv", "--no-color", "--", "/dev/null", "./" + safePath], cancellationToken)
            .ConfigureAwait(false);
        // --no-index uses exit code 1 to indicate that the two paths differ.
        if (result.ExitCode is not (0 or 1)) result.EnsureSuccess();
        if (!File.Exists(fullPath)) return result.StandardOutput;
        return result.StandardOutput.Replace("a/./" + safePath, "a/" + safePath, StringComparison.Ordinal)
            .Replace("b/./" + safePath, "b/" + safePath, StringComparison.Ordinal);
    }

    private async Task<string> GetShortHeadAsync(string root, CancellationToken cancellationToken)
    {
        var head = await Run(root, ["rev-parse", "--short", "HEAD"], cancellationToken).ConfigureAwait(false);
        return head.Success ? $"(detached at {head.StandardOutput.Trim()})" : "";
    }

    private async Task<string> RequireRepositoryRootAsync(string path, CancellationToken cancellationToken)
    {
        var info = await GetRepositoryAsync(path, cancellationToken).ConfigureAwait(false);
        return info.RootPath;
    }

    private async Task<CommandResult> Run(string root, IReadOnlyList<string> args, CancellationToken cancellationToken, string? stdin = null)
    {
        return await runner.RunAsync(new CommandSpec(executable, args, root, stdin, TimeSpan.FromMinutes(5)), cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full)) throw new VcsException($"저장소 폴더를 찾을 수 없습니다: {full}");
        return full;
    }

    private static List<string> ValidateRequestPaths(string root, IReadOnlyList<string>? paths)
    {
        if (paths is null) return [];
        return paths.Select(path => ValidateRelativePath(root, path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ValidateRelativePath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) throw new VcsException("저장소 내부의 상대 경로를 지정해야 합니다.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, path));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new VcsException("저장소 밖의 파일을 지정할 수 없습니다.");
        return Path.GetRelativePath(fullRoot, full).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string StatusLabel(char x, char y)
    {
        if (x == '?' && y == '?') return "Untracked";
        if (x == '!' && y == '!') return "Ignored";
        if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D')) return "Conflict";
        if (x == 'R' || y == 'R') return "Renamed";
        if (x == 'C' || y == 'C') return "Copied";
        if (x == 'A' || y == 'A') return "Added";
        if (x == 'D' || y == 'D') return "Deleted";
        if (x == 'M' || y == 'M') return "Modified";
        return "Changed";
    }
}
