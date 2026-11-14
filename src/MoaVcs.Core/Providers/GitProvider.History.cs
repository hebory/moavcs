using System.Text.RegularExpressions;

namespace MoaVcs.Core.Providers;

public sealed partial class GitProvider
{
    private static readonly Regex FullObjectIdPattern = new("^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant);

    public async Task<HistoryDetails> GetHistoryDetailsAsync(string root, string revision,
        CancellationToken cancellationToken = default)
    {
        root = NormalizeRoot(root);
        var commit = await ResolveHistoryCommitAsync(root, revision, cancellationToken).ConfigureAwait(false);
        var relationship = await ReadHistoryRelationshipAsync(root, commit, cancellationToken).ConfigureAwait(false);

        const string format = "--format=%H%x00%an%x00%aI%x00%B%x00%ae%x00%cn%x00%ce%x00%cI%x00%P%x00";
        var metadata = (await Run(root, ["show", "-s", format, commit], cancellationToken).ConfigureAwait(false))
            .EnsureSuccess().StandardOutput;
        var fields = metadata.Split('\0');
        if (fields.Length < 9 || !fields[0].TrimStart('\r', '\n').Equals(commit, StringComparison.OrdinalIgnoreCase))
            throw new VcsException("Git 커밋 세부 정보를 읽을 수 없습니다.");

        var message = fields[3].TrimEnd('\r', '\n');
        var entry = new HistoryEntry(commit, fields[1], fields[2], message)
        {
            AuthorEmail = fields[4].TrimEnd('\r', '\n'),
            Committer = fields[5].TrimEnd('\r', '\n'),
            CommitterEmail = fields[6].TrimEnd('\r', '\n'),
            CommitDate = fields[7].TrimEnd('\r', '\n'),
            ParentIds = fields[8].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        };
        var files = await ReadHistoryFilesAsync(root, relationship, commit, cancellationToken).ConfigureAwait(false);
        return new HistoryDetails(entry, files, relationship.Label);
    }

    public async Task<string> GetHistoryDiffAsync(string root, string revision, FileChange? file = null,
        CancellationToken cancellationToken = default)
    {
        root = NormalizeRoot(root);
        var commit = await ResolveHistoryCommitAsync(root, revision, cancellationToken).ConfigureAwait(false);
        var relationship = await ReadHistoryRelationshipAsync(root, commit, cancellationToken).ConfigureAwait(false);

        var args = new List<string>
        {
            "-c", "core.quotepath=false", "--literal-pathspecs", "--no-pager", "diff-tree", "--patch", "--no-commit-id",
            "-r", "-M", "--no-ext-diff", "--no-textconv", "--no-color"
        };
        if (relationship.IsRoot) args.Add("--root");
        else args.Add(relationship.Parent!);
        args.Add(commit);

        if (file is not null)
        {
            var safePath = ValidateRelativePath(root, file.Path);
            var recorded = await ReadHistoryFilesAsync(root, relationship, commit, cancellationToken).ConfigureAwait(false);
            var matching = recorded.FirstOrDefault(change =>
                change.Path.Equals(safePath, StringComparison.Ordinal) ||
                change.OriginalPath?.Equals(safePath, StringComparison.Ordinal) == true);
            if (matching is null)
                throw new VcsException($"'{safePath}'은 선택한 커밋에서 변경된 파일이 아닙니다.");

            args.Add("--");
            if (matching.OriginalPath is not null) args.Add(matching.OriginalPath);
            args.Add(matching.Path);
        }

        return (await Run(root, args, cancellationToken).ConfigureAwait(false)).EnsureSuccess().StandardOutput;
    }

    private async Task<string> ResolveHistoryCommitAsync(string root, string revision, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(revision) || !FullObjectIdPattern.IsMatch(revision))
            throw new VcsException("Git 커밋 ID가 올바르지 않습니다.");

        var result = await Run(root, ["rev-parse", "--verify", $"{revision}^{{commit}}"], cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
            throw new VcsException($"Git 커밋 '{revision}'을 찾을 수 없습니다.");

        var resolved = result.StandardOutput.Trim();
        if (!FullObjectIdPattern.IsMatch(resolved) || !resolved.Equals(revision, StringComparison.OrdinalIgnoreCase))
            throw new VcsException("Git 커밋 ID를 안전하게 확인하지 못했습니다.");
        return resolved;
    }

    private async Task<HistoryRelationship> ReadHistoryRelationshipAsync(string root, string commit,
        CancellationToken cancellationToken)
    {
        // Read the commit object itself: rev-list hides parents beyond a shallow boundary,
        // which would otherwise make an older commit look like a true root commit.
        var result = (await Run(root, ["cat-file", "-p", commit], cancellationToken).ConfigureAwait(false)).EnsureSuccess();
        var parents = new List<string>();
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var header = line.TrimEnd('\r');
            if (header.Length == 0) break;
            if (header.StartsWith("parent ", StringComparison.Ordinal)) parents.Add(header[7..]);
        }
        if (parents.Any(parent => !FullObjectIdPattern.IsMatch(parent)))
            throw new VcsException("Git 커밋의 부모 이력을 읽을 수 없습니다.");

        if (parents.Count == 0)
            return new HistoryRelationship(null, true, "첫 커밋 · 빈 저장소 → " + commit[..Math.Min(12, commit.Length)]);

        var parent = parents[0];
        var parentExists = await Run(root, ["cat-file", "-e", $"{parent}^{{commit}}"], cancellationToken).ConfigureAwait(false);
        if (!parentExists.Success)
            throw new VcsException("이 커밋의 부모가 로컬 저장소에 없어 변경 내역을 비교할 수 없습니다. 저장소 이력을 더 가져온 뒤 다시 시도하세요.");

        var isMerge = parents.Count > 1;
        var label = isMerge
            ? $"첫 번째 부모 기준 · {parent[..Math.Min(12, parent.Length)]} → {commit[..Math.Min(12, commit.Length)]}"
            : $"{parent[..Math.Min(12, parent.Length)]} → {commit[..Math.Min(12, commit.Length)]}";
        return new HistoryRelationship(parent, false, label);
    }

    private async Task<IReadOnlyList<FileChange>> ReadHistoryFilesAsync(string root, HistoryRelationship relationship,
        string commit, CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "--literal-pathspecs", "--no-pager", "diff-tree", "--no-commit-id", "--name-status", "-r", "-M", "-z"
        };
        if (relationship.IsRoot) args.Add("--root");
        else args.Add(relationship.Parent!);
        args.Add(commit);

        var output = (await Run(root, args, cancellationToken).ConfigureAwait(false)).EnsureSuccess().StandardOutput;
        var fields = output.Split('\0');
        var result = new List<FileChange>();
        var index = 0;
        while (index < fields.Length)
        {
            var status = fields[index++];
            if (status.Length == 0) continue;
            if (index >= fields.Length) throw new VcsException("Git 커밋 파일 목록이 잘못된 형식입니다.");

            var originalPath = status[0] is 'R' or 'C' ? fields[index++] : null;
            if (index >= fields.Length) throw new VcsException("Git 커밋 파일 목록이 잘못된 형식입니다.");
            var path = fields[index++];
            if (path.Length == 0 || originalPath is not null && originalPath.Length == 0)
                throw new VcsException("Git 커밋 파일 목록에 빈 경로가 있습니다.");

            var safePath = ValidateRelativePath(root, path);
            var safeOriginalPath = originalPath is null ? null : ValidateRelativePath(root, originalPath);
            var changeStatus = status[0] switch
            {
                'A' => "Added",
                'C' => "Copied",
                'D' => "Deleted",
                'M' => "Modified",
                'R' => "Renamed",
                'T' => "Type changed",
                'U' => "Unmerged",
                _ => throw new VcsException($"Git 커밋 파일 상태 '{status}'을 해석할 수 없습니다.")
            };
            result.Add(new FileChange(safePath, changeStatus, OriginalPath: safeOriginalPath));
        }
        return result;
    }

    private sealed record HistoryRelationship(string? Parent, bool IsRoot, string Label);
}
