using System.Text;

namespace MoaVcs.Core.Providers;

/// <summary>Git adapter built on the official git executable.</summary>
public sealed partial class GitProvider(ICommandRunner runner, string executable = "git") : IVcsProvider
{
    private const string ExternalOriginConfigReadOnlyReason = "origin URL이 전역, include 또는 worktree 설정에서 오므로 안전하게 보존할 수 없어 읽기 전용입니다.";
    public const long MaxTextSnapshotBytes = TextSnapshot.MaxBytes;
    public const int MaxBlameLines = 50_000;

    public VcsKind Kind => VcsKind.Git;
    public string Name => "Git";
    public string Executable => executable;
    public VcsCapabilities Capabilities => VcsCapabilities.Diff | VcsCapabilities.History | VcsCapabilities.Stage |
        VcsCapabilities.Unstage | VcsCapabilities.Commit | VcsCapabilities.Update | VcsCapabilities.Push |
        VcsCapabilities.Initialize | VcsCapabilities.Clone | VcsCapabilities.FileComparison | VcsCapabilities.Blame |
        VcsCapabilities.RemoteUrl | VcsCapabilities.ConflictResolution;

    public async Task<RemoteInfo> GetRemoteAsync(string root, CancellationToken cancellationToken = default)
    {
        root = await RequireRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        var fetchEntries = await ReadRemoteConfigEntriesAsync(root, "remote.origin.url", cancellationToken).ConfigureAwait(false);
        var pushEntries = await ReadRemoteConfigEntriesAsync(root, "remote.origin.pushurl", cancellationToken).ConfigureAwait(false);
        var sectionKeys = await Run(root,
            ["config", "--null", "--name-only", "--get-regexp", "^remote\\.origin\\."], cancellationToken).ConfigureAwait(false);
        if (sectionKeys.ExitCode is not (0 or 1)) sectionKeys.EnsureSuccess();
        var remoteList = (await Run(root, ["remote"], cancellationToken).ConfigureAwait(false)).EnsureSuccess();
        var hasOriginConfig = sectionKeys.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Any(key => key.StartsWith("remote.origin.", StringComparison.OrdinalIgnoreCase));
        var exists = fetchEntries.Count > 0 || pushEntries.Count > 0 || hasOriginConfig ||
            remoteList.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Contains("origin", StringComparer.Ordinal);

        if (!exists)
            return new RemoteInfo("origin", false, [], [], [], []);

        var readOnlyReason = await FindReadOnlyReasonAsync(root, fetchEntries.Concat(pushEntries), cancellationToken)
            .ConfigureAwait(false);
        var configuredFetch = fetchEntries.Select(entry => entry.Value).ToArray();
        var configuredPush = pushEntries.Select(entry => entry.Value).ToArray();
        if (configuredFetch.Length == 0)
            readOnlyReason ??= "origin 항목은 있지만 fetch 주소가 없어 안전하게 변경할 수 없습니다.";
        else if (configuredFetch.Count(url => string.Equals(url, configuredFetch[0], StringComparison.Ordinal)) > 1)
            readOnlyReason ??= "origin의 첫 fetch 주소가 중복되어 하나만 안전하게 변경할 수 없습니다.";

        var unsafeControlCharacters = configuredFetch.Any(HasControlCharacter) || configuredPush.Any(HasControlCharacter);
        if (unsafeControlCharacters)
            readOnlyReason ??= "origin 설정에 줄바꿈 또는 제어 문자가 포함되어 안전하게 편집할 수 없습니다.";

        IReadOnlyList<string> effectiveFetch = [];
        IReadOnlyList<string> effectivePush = [];
        if (configuredFetch.Length > 0 && !unsafeControlCharacters &&
            !string.Equals(readOnlyReason, ExternalOriginConfigReadOnlyReason, StringComparison.Ordinal))
        {
            effectiveFetch = await ReadEffectiveUrlsAsync(root, ["remote", "get-url", "--all", "origin"], cancellationToken)
                .ConfigureAwait(false);
            effectivePush = await ReadEffectiveUrlsAsync(root, ["remote", "get-url", "--push", "--all", "origin"], cancellationToken)
                .ConfigureAwait(false);
        }

        return new RemoteInfo("origin", true, configuredFetch, configuredPush, effectiveFetch, effectivePush, readOnlyReason);
    }

    public async Task SetRemoteUrlAsync(string root, string url, RemoteInfo expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var validatedUrl = RemoteUrl.Validate(url);
        root = await RequireRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var current = await GetRemoteAsync(root, cancellationToken).ConfigureAwait(false);
        if (!RemoteInfoMatches(current, expected))
            throw new VcsException("원격 저장소 설정이 다른 곳에서 바뀌었습니다. 화면을 새로 고친 뒤 다시 시도하세요.");
        if (current.ReadOnlyReason is not null)
            throw new VcsException(current.ReadOnlyReason);

        if (!current.Exists)
        {
            (await Run(root, ["remote", "add", "origin", validatedUrl], cancellationToken).ConfigureAwait(false)).EnsureSuccess();
            return;
        }

        if (current.FetchUrls.Count == 0)
            throw new VcsException("origin 설정은 있지만 fetch 주소가 없어 안전하게 변경할 수 없습니다.");

        // Git remote set-url rejects multivalued remote.origin.url. Replace the exact first value,
        // using fixed-value matching so URL characters are never treated as a regular expression.
        (await Run(root, ["config", "--local", "--fixed-value", "--replace-all", "--", "remote.origin.url",
            validatedUrl, current.FetchUrls[0]], cancellationToken).ConfigureAwait(false)).EnsureSuccess();
    }

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

    public async Task<FileComparison> GetComparisonAsync(string root, FileChange file, bool staged = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        root = NormalizeRoot(root);
        var path = ValidateRelativePath(root, file.Path);
        if (file.IsConflicted)
        {
            if (staged)
                throw new VcsException($"'{path}'은 충돌 상태라 일반 스테이지 비교를 할 수 없습니다. 충돌 비교를 여세요.");
            var conflict = await GetConflictAsync(root, file, cancellationToken).ConfigureAwait(false);
            return new FileComparison(path, conflict.LeftLabel, conflict.RightLabel,
                conflict.LeftText, conflict.RightText);
        }
        string? left;
        string? right;
        string leftLabel;
        string rightLabel;

        if (staged)
        {
            // For an index rename, HEAD still stores the old name while the index stores the new one.
            var headPath = file.IndexStatus == "R" && file.OriginalPath is not null
                ? ValidateRelativePath(root, file.OriginalPath)
                : path;
            left = await ReadHeadTextAsync(root, headPath, cancellationToken).ConfigureAwait(false);
            right = await ReadIndexTextAsync(root, path, cancellationToken).ConfigureAwait(false);
            leftLabel = left is null ? "HEAD (파일 없음)" : "HEAD";
            rightLabel = right is null ? "인덱스 (삭제됨)" : "인덱스";
        }
        else
        {
            // For an unstaged worktree rename, compare the old index entry with the new worktree path.
            var indexPath = file.WorkTreeStatus == "R" && file.OriginalPath is not null
                ? ValidateRelativePath(root, file.OriginalPath)
                : path;
            left = await ReadIndexTextAsync(root, indexPath, cancellationToken).ConfigureAwait(false);
            right = await ReadWorkTreeTextAsync(root, path, cancellationToken).ConfigureAwait(false);
            leftLabel = left is null ? "인덱스 (파일 없음)" : "인덱스";
            rightLabel = right is null ? "작업 트리 (삭제됨)" : "작업 트리";
        }

        return new FileComparison(file.Path, leftLabel, rightLabel, left ?? string.Empty, right ?? string.Empty);
    }

    public async Task<BlameResult> GetBlameAsync(string root, FileChange file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        root = NormalizeRoot(root);
        var path = ValidateRelativePath(root, file.Path);
        // Blame is intentionally anchored to committed HEAD. A staged rename's old name is the committed path.
        if (file.IndexStatus == "R" && file.OriginalPath is not null)
            path = ValidateRelativePath(root, file.OriginalPath);

        var head = await Run(root, ["rev-parse", "--verify", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (!head.Success)
            throw new VcsException("작성자 이력을 보려면 먼저 저장소에 첫 커밋이 필요합니다.");
        var revision = head.StandardOutput.Trim();
        var content = await ReadCommitTextAsync(root, revision, path, cancellationToken).ConfigureAwait(false);
        if (content is null)
            throw new VcsException("선택한 파일은 현재 커밋에 없어 작성자 이력을 표시할 수 없습니다.");
        var expectedLines = CountTextLines(content);
        if (expectedLines > MaxBlameLines)
            throw new VcsException($"파일이 {MaxBlameLines:N0}줄을 초과하여 작성자 이력을 표시할 수 없습니다.");

        var blame = await Run(root,
            ["--literal-pathspecs", "--no-pager", "blame", "--line-porcelain", "--encoding=UTF-8", revision, "--", path],
            cancellationToken).ConfigureAwait(false);
        if (!blame.Success)
            throw new VcsException($"Git 작성자 이력을 읽지 못했습니다.{Environment.NewLine}{blame.Output}");

        var lines = ParseBlame(blame.StandardOutput);
        if (lines.Count != expectedLines || lines.Where((line, index) => line.LineNumber != index + 1).Any())
            throw new VcsException("Git 작성자 이력의 줄 수가 파일 내용과 일치하지 않습니다.");
        var shortRevision = await Run(root, ["rev-parse", "--short", revision], cancellationToken).ConfigureAwait(false);
        var label = shortRevision.Success ? $"HEAD ({shortRevision.StandardOutput.Trim()})" : "HEAD";
        return new BlameResult(file.Path, label, lines);
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
                return (await Run(pushRoot, ["-c", "push.autoSetupRemote=true", "push"], cancellationToken).ConfigureAwait(false)).EnsureSuccess();
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

    private async Task<string?> ReadWorkTreeTextAsync(string root, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safePath = ValidateRelativePath(root, path);
        var fullPath = Path.Combine(root, safePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            if (Directory.Exists(fullPath)) throw new VcsException("폴더 전체를 텍스트로 비교할 수 없습니다. 파일을 선택하세요.");
            return null;
        }
        var info = new FileInfo(fullPath);
        TextSnapshot.EnsureWithinLimit(info.Length, safePath);
        try { return await TextSnapshot.ReadFileAsync(fullPath, safePath, cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private async Task<string?> ReadIndexTextAsync(string root, string path, CancellationToken cancellationToken)
    {
        var safePath = ValidateRelativePath(root, path);
        var listing = await Run(root,
            ["--literal-pathspecs", "ls-files", "--stage", "-z", "--", safePath], cancellationToken).ConfigureAwait(false);
        listing.EnsureSuccess();
        var records = listing.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (records.Length == 0) return null;
        var fields = records[0].Split('\t', 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3 || fields[2] != "0" || records.Length != 1)
            throw new VcsException($"'{safePath}' 파일은 인덱스 충돌 상태여서 비교할 수 없습니다.");
        var objectId = fields[1];
        return await ReadBlobTextAsync(root, objectId, safePath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ReadHeadTextAsync(string root, string path, CancellationToken cancellationToken)
    {
        var head = await Run(root, ["rev-parse", "--verify", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (!head.Success) return null;
        return await ReadCommitTextAsync(root, head.StandardOutput.Trim(), path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ReadCommitTextAsync(string root, string revision, string path, CancellationToken cancellationToken)
    {
        var safePath = ValidateRelativePath(root, path);
        var objectSpec = $"{revision}:{safePath}";
        var exists = await Run(root, ["cat-file", "-e", objectSpec], cancellationToken).ConfigureAwait(false);
        if (!exists.Success) return null;
        return await ReadBlobTextAsync(root, objectSpec, safePath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadBlobTextAsync(string root, string objectSpec, string path, CancellationToken cancellationToken)
    {
        var sizeResult = await Run(root, ["cat-file", "-s", objectSpec], cancellationToken).ConfigureAwait(false);
        sizeResult.EnsureSuccess();
        if (!long.TryParse(sizeResult.StandardOutput.Trim(), out var size) || size < 0)
            throw new VcsException($"Git에서 '{path}' 파일 크기를 확인하지 못했습니다.");
        TextSnapshot.EnsureWithinLimit(size, path);

        var blob = (await Run(root, ["cat-file", "blob", objectSpec], cancellationToken).ConfigureAwait(false)).EnsureSuccess();
        return TextSnapshot.ValidateText(blob.StandardOutput, path);
    }

    private static IReadOnlyList<BlameLine> ParseBlame(string output)
    {
        var source = output.Split('\n');
        var result = new List<BlameLine>();
        for (var i = 0; i < source.Length;)
        {
            var header = source[i].TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (header.Length < 3 || header[0].Length < 7 ||
                !int.TryParse(header[1], out _) || !int.TryParse(header[2], out var finalLine))
            {
                i++;
                continue;
            }

            var revision = header[0];
            var author = string.Empty;
            var date = string.Empty;
            i++;
            while (i < source.Length && !source[i].StartsWith('\t'))
            {
                var metadata = source[i].TrimEnd('\r');
                if (metadata.StartsWith("author ", StringComparison.Ordinal)) author = metadata[7..];
                else if (metadata.StartsWith("author-time ", StringComparison.Ordinal) &&
                         long.TryParse(metadata[12..], out var unixTime))
                {
                    try { date = DateTimeOffset.FromUnixTimeSeconds(unixTime).ToString("O"); }
                    catch (ArgumentOutOfRangeException) { date = string.Empty; }
                }
                i++;
            }
            if (i < source.Length && source[i].StartsWith('\t'))
            {
                result.Add(new BlameLine(finalLine, revision, author, date, source[i][1..].TrimEnd('\r')));
                i++;
            }
        }
        return result;
    }

    private static int CountTextLines(string text)
    {
        if (text.Length == 0) return 0;
        var lines = text.Count(character => character == '\n');
        return text[^1] == '\n' ? lines : lines + 1;
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

    private async Task<List<RemoteConfigEntry>> ReadRemoteConfigEntriesAsync(string root, string key,
        CancellationToken cancellationToken)
    {
        var result = await Run(root, ["config", "--null", "--show-origin", "--show-scope", "--get-all", key], cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode == 1) return [];
        result.EnsureSuccess();
        var fields = result.StandardOutput.Split('\0');
        if (fields.Length % 3 != 1 || fields[^1].Length != 0)
            throw new VcsException("Git 원격 설정을 안전하게 읽지 못했습니다.");
        var entries = new List<RemoteConfigEntry>(fields.Length / 3);
        for (var i = 0; i + 2 < fields.Length; i += 3)
            entries.Add(new RemoteConfigEntry(fields[i], fields[i + 1], fields[i + 2]));
        return entries;
    }

    private async Task<string?> FindReadOnlyReasonAsync(string root, IEnumerable<RemoteConfigEntry> entries,
        CancellationToken cancellationToken)
    {
        var urlEntries = entries.ToArray();
        if (urlEntries.Length == 0) return null;
        var configPathResult = (await Run(root, ["rev-parse", "--git-path", "config"], cancellationToken)
            .ConfigureAwait(false)).EnsureSuccess();
        var configPath = configPathResult.StandardOutput.Trim();
        var expectedConfig = Path.GetFullPath(Path.IsPathRooted(configPath) ? configPath : Path.Combine(root, configPath));

        foreach (var entry in urlEntries)
        {
            if (!entry.Scope.Equals("local", StringComparison.OrdinalIgnoreCase) ||
                !TryResolveConfigOrigin(root, entry.Origin, out var sourcePath) ||
                !Path.GetFullPath(sourcePath).Equals(expectedConfig, StringComparison.OrdinalIgnoreCase))
            {
                return ExternalOriginConfigReadOnlyReason;
            }
        }
        return null;
    }

    private static bool TryResolveConfigOrigin(string root, string origin, out string path)
    {
        path = string.Empty;
        if (!origin.StartsWith("file:", StringComparison.Ordinal)) return false;
        var source = origin[5..];
        if (source.Length == 0) return false;
        path = Path.GetFullPath(Path.IsPathRooted(source) ? source : Path.Combine(root, source));
        return true;
    }

    private async Task<IReadOnlyList<string>> ReadEffectiveUrlsAsync(string root, IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var result = (await Run(root, args, cancellationToken).ConfigureAwait(false)).EnsureSuccess();
        return result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool RemoteInfoMatches(RemoteInfo current, RemoteInfo expected) =>
        string.Equals(current.Name, expected.Name, StringComparison.Ordinal) &&
        current.Exists == expected.Exists &&
        current.FetchUrls.SequenceEqual(expected.FetchUrls, StringComparer.Ordinal) &&
        current.PushUrls.SequenceEqual(expected.PushUrls, StringComparer.Ordinal) &&
        current.EffectiveFetchUrls.SequenceEqual(expected.EffectiveFetchUrls, StringComparer.Ordinal) &&
        current.EffectivePushUrls.SequenceEqual(expected.EffectivePushUrls, StringComparer.Ordinal) &&
        string.Equals(current.ReadOnlyReason, expected.ReadOnlyReason, StringComparison.Ordinal);

    private static bool HasControlCharacter(string value) => value.Any(char.IsControl);

    private sealed record RemoteConfigEntry(string Scope, string Origin, string Value);

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
