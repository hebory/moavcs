using System.Globalization;

namespace MoaVcs.Core.Providers;

public sealed partial class GitProvider
{
    private const int HistorySnapshotLimit = 200;
    private const int ReachabilityWalkLimit = 50_000;
    private const string SnapshotFormat =
        "--format=%H%x00%P%x00%an%x00%ae%x00%cn%x00%ce%x00%aI%x00%cI%x00%s%x00";

    public async Task<HistorySnapshot> GetHistorySnapshotAsync(string root, HistoryScope scope = HistoryScope.CurrentBranch,
        CancellationToken cancellationToken = default)
    {
        root = NormalizeRoot(root);
        var context = await ReadGitHistoryContextAsync(root, cancellationToken).ConfigureAwait(false);
        var (refs, referencedCommits) = await ReadHistoryReferencesAsync(root, cancellationToken).ConfigureAwait(false);
        var headExists = !context.IsUnborn;

        if (!headExists && scope == HistoryScope.CurrentBranch)
            return new HistorySnapshot([], "첫 커밋 전 · 현재 브랜치 이력", context, false);

        var args = new List<string> { "log", "--topo-order", $"--max-count={HistorySnapshotLimit + 1}", SnapshotFormat };
        var historyTips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (scope == HistoryScope.AllBranches)
        {
            if (headExists) historyTips.Add(context.HeadId);
            foreach (var referenceId in referencedCommits)
                historyTips.Add(referenceId);
        }
        else
        {
            if (headExists) historyTips.Add(context.HeadId);
            if (context.UpstreamId.Length > 0) historyTips.Add(context.UpstreamId);
        }
        if (historyTips.Count == 0)
            return new HistorySnapshot([], "모든 로컬·원격 추적 브랜치의 이력", context, false);
        args.Add("--stdin");
        var revisionInput = string.Join(Environment.NewLine, historyTips) + Environment.NewLine;

        var log = (await Run(root, args, cancellationToken, revisionInput).ConfigureAwait(false)).EnsureSuccess().StandardOutput;
        var entries = ParseSnapshotEntries(log);
        if (context.IsShallow)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                if (entries[index].ParentIds.Count > 0) continue;
                var parents = await ReadRawCommitParentsAsync(root, entries[index].Id, cancellationToken).ConfigureAwait(false);
                if (parents.Count > 0) entries[index] = entries[index] with { ParentIds = parents };
            }
        }
        var isTruncated = entries.Count > HistorySnapshotLimit;
        if (isTruncated) entries.RemoveRange(HistorySnapshotLimit, entries.Count - HistorySnapshotLimit);

        var localReachable = headExists
            ? await ReadReachableCommitsAsync(root, context.HeadId, cancellationToken).ConfigureAwait(false)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? upstreamReachable = context.UpstreamId.Length > 0
            ? await ReadReachableCommitsAsync(root, context.UpstreamId, cancellationToken).ConfigureAwait(false)
            : null;

        var decorated = new List<HistoryEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var relation = await GetHistoryRelationAsync(root, entry.Id, localReachable, upstreamReachable,
                context.HeadId, context.UpstreamId, context.IsShallow, cancellationToken).ConfigureAwait(false);
            decorated.Add(entry with { References = GetReferences(entry.Id, refs, context.HeadId), Relation = relation });
        }

        var scopeLabel = scope == HistoryScope.AllBranches
            ? "모든 로컬·원격 추적 브랜치와 태그에서 도달 가능한 이력"
            : context.Upstream.Length > 0 && context.UpstreamId.Length > 0
                ? $"현재 브랜치와 추적 브랜치 {context.Upstream}의 이력"
                : "현재 브랜치 이력";
        if (isTruncated) scopeLabel += $" · 최근 {HistorySnapshotLimit}개 표시";
        if (context.IsShallow) scopeLabel += " · 얕은 저장소에 내려받은 범위";
        return new HistorySnapshot(decorated, scopeLabel, context, isTruncated);
    }

    private async Task<GitHistoryContext> ReadGitHistoryContextAsync(string root, CancellationToken cancellationToken)
    {
        var shallowResult = await Run(root, ["rev-parse", "--is-shallow-repository"], cancellationToken).ConfigureAwait(false);
        var isShallow = shallowResult.Success && shallowResult.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        var branchResult = await Run(root, ["symbolic-ref", "--quiet", "HEAD"], cancellationToken).ConfigureAwait(false);
        var isDetached = !branchResult.Success;
        var branch = branchResult.Success ? branchResult.StandardOutput.Trim() : "";
        if (branch.StartsWith("refs/heads/", StringComparison.Ordinal)) branch = branch["refs/heads/".Length..];
        var headResult = await Run(root, ["rev-parse", "--verify", "HEAD^{commit}"], cancellationToken).ConfigureAwait(false);
        var headId = headResult.Success && FullObjectIdPattern.IsMatch(headResult.StandardOutput.Trim())
            ? headResult.StandardOutput.Trim() : "";
        var isUnborn = headId.Length == 0;
        if (isUnborn) isDetached = false;

        var upstream = "";
        var upstreamId = "";
        var pushTarget = "";
        var pushTargetId = "";
        int? ahead = null;
        int? behind = null;
        var status = isUnborn ? "첫 커밋 전" : isDetached ? "분리된 HEAD" : "추적 정보 없음";

        if (!isUnborn && !isDetached)
        {
            (upstream, upstreamId, pushTarget, pushTargetId) =
                await ResolveBranchTargetsAsync(root, branch, cancellationToken).ConfigureAwait(false);
            if (upstream.Length == 0)
            {
                status = "추적 브랜치 없음";
            }
            else if (upstreamId.Length == 0)
            {
                status = "추적 브랜치를 로컬에서 찾을 수 없음 · 가져오기 필요";
            }
            else if (isShallow)
            {
                status = "얕은 저장소 · 내려받은 범위 밖의 차이는 계산하지 않음";
            }
            else
            {
                var counts = await Run(root, ["rev-list", "--left-right", "--count", $"{upstreamId}...{headId}"], cancellationToken)
                    .ConfigureAwait(false);
                if (counts.Success)
                {
                    var fields = counts.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length == 2 && int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var behindCount) &&
                        int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var aheadCount))
                    {
                        behind = behindCount;
                        ahead = aheadCount;
                        status = ahead == 0 && behind == 0 ? "추적 브랜치와 동기화됨" :
                            ahead > 0 && behind > 0 ? "로컬·추적 브랜치가 서로 다름" :
                            ahead > 0 ? "추적 브랜치에 없는 로컬 커밋 있음" : "현재 브랜치에 없는 추적 커밋 있음";
                    }
                }
            }
        }

        return new GitHistoryContext
        {
            CurrentBranch = branch,
            HeadId = headId,
            IsDetached = isDetached,
            IsUnborn = isUnborn,
            Upstream = upstream,
            UpstreamId = upstreamId,
            PushTarget = pushTarget,
            PushTargetId = pushTargetId,
            Ahead = ahead,
            Behind = behind,
            IsShallow = isShallow,
            Status = status
        };
    }

    private async Task<(string Upstream, string UpstreamId, string PushTarget, string PushTargetId)> ResolveBranchTargetsAsync(
        string root, string branch, CancellationToken cancellationToken)
    {
        var fullBranchRef = $"refs/heads/{branch}";
        var result = await Run(root,
            ["for-each-ref", "--format=%(upstream)%00%(upstream:short)%00%(push)%00%(push:short)%00", fullBranchRef],
            cancellationToken).ConfigureAwait(false);
        if (!result.Success) return ("", "", "", "");
        var fields = result.StandardOutput.TrimEnd('\r', '\n').Split('\0');
        if (fields.Length < 4) return ("", "", "", "");
        var upstreamRef = fields[0].StartsWith("refs/", StringComparison.Ordinal) ? fields[0] : "";
        var pushRef = fields[2].StartsWith("refs/", StringComparison.Ordinal) ? fields[2] : "";
        var upstreamId = await ResolveReferenceCommitIdAsync(root, upstreamRef, cancellationToken).ConfigureAwait(false);
        var pushId = await ResolveReferenceCommitIdAsync(root, pushRef, cancellationToken).ConfigureAwait(false);
        return (fields[1], upstreamId, fields[3], pushId);
    }

    private async Task<string> ResolveReferenceCommitIdAsync(string root, string reference, CancellationToken cancellationToken)
    {
        if (reference.Length == 0) return "";
        var idResult = await Run(root, ["rev-parse", "--verify", $"{reference}^{{commit}}"], cancellationToken).ConfigureAwait(false);
        var id = idResult.Success ? idResult.StandardOutput.Trim() : "";
        return FullObjectIdPattern.IsMatch(id) ? id : "";
    }

    private async Task<(Dictionary<string, List<HistoryReference>> References, IReadOnlyList<string> Commits)> ReadHistoryReferencesAsync(string root,
        CancellationToken cancellationToken)
    {
        var result = await Run(root,
            ["for-each-ref", "--format=%(refname)%00%(objectname)%00%(objecttype)%00%(*objectname)%00%(*objecttype)%00"],
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess();
        var references = new Dictionary<string, List<HistoryReference>>(StringComparer.OrdinalIgnoreCase);
        var commits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\0');
            if (fields.Length < 5) continue;
            var reference = fields[0];
            var id = fields[2] == "commit" ? fields[1] : fields[4] == "commit" ? fields[3] : "";
            if (!FullObjectIdPattern.IsMatch(id)) continue;
            var (kind, name) = reference.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? (HistoryReferenceKind.LocalBranch, reference["refs/heads/".Length..])
                : reference.StartsWith("refs/remotes/", StringComparison.Ordinal)
                    ? (HistoryReferenceKind.RemoteBranch, reference["refs/remotes/".Length..])
                    : reference.StartsWith("refs/tags/", StringComparison.Ordinal)
                        ? (HistoryReferenceKind.Tag, reference["refs/tags/".Length..])
                        : ((HistoryReferenceKind?)null, "");
            if (kind is null) continue;
            commits.Add(id);
            AddReference(references, id, new HistoryReference(name, kind.Value));
        }
        return (references, commits.ToArray());
    }

    private async Task<HashSet<string>> ReadReachableCommitsAsync(string root, string revision,
        CancellationToken cancellationToken)
    {
        var result = await Run(root, ["rev-list", $"--max-count={ReachabilityWalkLimit}", revision], cancellationToken).ConfigureAwait(false);
        if (!result.Success) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(id => FullObjectIdPattern.IsMatch(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<string>> ReadRawCommitParentsAsync(string root, string commit,
        CancellationToken cancellationToken)
    {
        var result = await Run(root, ["cat-file", "-p", commit], cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess();
        var parents = new List<string>();
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var header = line.TrimEnd('\r');
            if (header.Length == 0) break;
            if (header.StartsWith("parent ", StringComparison.Ordinal) && FullObjectIdPattern.IsMatch(header[7..]))
                parents.Add(header[7..]);
        }
        return parents;
    }

    private static List<HistoryEntry> ParseSnapshotEntries(string output)
    {
        var fields = output.Split('\0');
        var entries = new List<HistoryEntry>();
        for (var index = 0; index + 8 < fields.Length; index += 9)
        {
            var id = fields[index].TrimStart('\r', '\n');
            if (id.Length == 0 || !FullObjectIdPattern.IsMatch(id)) continue;
            var parents = fields[index + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            entries.Add(new HistoryEntry(id, fields[index + 2], fields[index + 6], fields[index + 8].TrimEnd('\r', '\n'))
            {
                ParentIds = parents,
                AuthorEmail = fields[index + 3],
                Committer = fields[index + 4],
                CommitterEmail = fields[index + 5],
                CommitDate = fields[index + 7]
            });
        }
        return entries;
    }

    private static IReadOnlyList<HistoryReference> GetReferences(string id,
        IReadOnlyDictionary<string, List<HistoryReference>> references, string headId)
    {
        var result = new List<HistoryReference>();
        if (id.Equals(headId, StringComparison.OrdinalIgnoreCase)) result.Add(new HistoryReference("HEAD", HistoryReferenceKind.Head));
        if (references.TryGetValue(id, out var found)) result.AddRange(found.OrderBy(reference => reference.Name, StringComparer.Ordinal));
        return result;
    }

    private async Task<HistoryRelation> GetHistoryRelationAsync(string root, string id, IReadOnlySet<string> local,
        IReadOnlySet<string>? upstream, string headId, string upstreamId, bool shallow, CancellationToken cancellationToken)
    {
        if (shallow || upstream is null) return HistoryRelation.Unknown;
        var inLocal = local.Contains(id);
        if (!inLocal && headId.Length > 0)
            inLocal = await IsAncestorOfAsync(root, id, headId, cancellationToken).ConfigureAwait(false);
        var inUpstream = upstream?.Contains(id) == true;
        if (upstream is not null && !inUpstream && upstreamId.Length > 0)
            inUpstream = await IsAncestorOfAsync(root, id, upstreamId, cancellationToken).ConfigureAwait(false);
        return (inLocal, inUpstream) switch
        {
            (true, true) => HistoryRelation.Shared,
            (true, false) => HistoryRelation.LocalOnly,
            (false, true) => HistoryRelation.UpstreamOnly,
            _ => HistoryRelation.Other
        };
    }

    private async Task<bool> IsAncestorOfAsync(string root, string candidateId, string tipId,
        CancellationToken cancellationToken)
    {
        var result = await Run(root, ["merge-base", "--is-ancestor", candidateId, tipId], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0) return true;
        if (result.ExitCode == 1) return false;
        result.EnsureSuccess();
        return false;
    }

    private static void AddReference(Dictionary<string, List<HistoryReference>> references, string id, HistoryReference reference)
    {
        if (!references.TryGetValue(id, out var list)) references[id] = list = [];
        list.Add(reference);
    }
}
