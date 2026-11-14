using MoaVcs.Core;
using MoaVcs.Core.Providers;

namespace MoaVcs.Tests;

public static class HistoryContextGitTests
{
    public static async Task RunAsync()
    {
        var scratchParent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local"));
        Directory.CreateDirectory(scratchParent);
        var scratch = Path.Combine(scratchParent, "test-git-history-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var root = Path.Combine(scratch, "repo");
        var runner = new ProcessCommandRunner();
        var fetchRunner = new FetchRecordingRunner(runner);
        var provider = new GitProvider(fetchRunner);
        var emptyGlobal = Path.Combine(scratch, "empty-global.config");
        await File.WriteAllTextAsync(emptyGlobal, string.Empty);
        var previousGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var previousSystem = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
        try
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", emptyGlobal);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");

            await Git(runner, scratch, "init", "--initial-branch=main", root);
            await Configure(runner, root);
            await File.WriteAllTextAsync(Path.Combine(root, "base.txt"), "base\n");
            await Git(runner, root, "add", "--", "base.txt");
            await Git(runner, root, "commit", "-m", "base commit");
            var baseId = (await Git(runner, root, "rev-parse", "HEAD")).StandardOutput.Trim();
            await Git(runner, root, "update-ref", "refs/remotes/origin/main", baseId);
            await Git(runner, root, "update-ref", "refs/remotes/review/main", baseId);
            await Git(runner, root, "config", "branch.main.remote", "origin");
            await Git(runner, root, "config", "branch.main.merge", "refs/heads/main");
            await Git(runner, root, "config", "branch.main.pushRemote", "review");
            await Git(runner, root, "config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*");
            await Git(runner, root, "config", "remote.review.fetch", "+refs/heads/*:refs/remotes/review/*");
            await Git(runner, root, "config", "remote.origin.url", Path.Combine(scratch, "unused-origin.git"));
            await Git(runner, root, "config", "remote.review.url", Path.Combine(scratch, "unused-review.git"));
            await Git(runner, root, "config", "push.default", "current");
            await Git(runner, root, "tag", "v1.0", baseId);

            var initial = await provider.GetHistorySnapshotAsync(root);
            Assert(initial.Git is { CurrentBranch: "main", IsDetached: false, IsUnborn: false, Ahead: 0, Behind: 0,
                Upstream: "origin/main", PushTarget: "review/main" },
                $"branch, upstream, and separate push target (actual: {initial.Git})");
            Assert(initial.Entries.Count == 1 && initial.Entries[0].ParentIds.Count == 0 &&
                initial.Entries[0].AuthorEmail == "history@example.invalid" && initial.Entries[0].Committer == "History Test" &&
                initial.Entries[0].CommitDate.Length > 0, "root metadata includes parent, author, committer, and both dates");
            Assert(initial.Entries[0].References.Any(reference => reference.Name == "origin/main" && reference.Kind == HistoryReferenceKind.RemoteBranch) &&
                initial.Entries[0].References.Any(reference => reference.Name == "v1.0" && reference.Kind == HistoryReferenceKind.Tag),
                "remote branch and tag references decorate their commit");
            await Git(runner, root, "config", "push.default", "nothing");
            var disabledPush = await provider.GetHistorySnapshotAsync(root);
            Assert(disabledPush.Git is { Upstream: "origin/main", PushTarget: "", Ahead: 0, Behind: 0 },
                "disabled/default-unresolvable push does not hide upstream information");
            await Git(runner, root, "config", "push.default", "current");
            await Git(runner, root, "update-ref", "refs/remotes/origin/stable", baseId);
            await Git(runner, root, "config", "branch.main.merge", "refs/heads/stable");
            var differentlyNamed = await provider.GetHistorySnapshotAsync(root);
            Assert(differentlyNamed.Git is { CurrentBranch: "main", Upstream: "origin/stable", PushTarget: "review/main", Ahead: 0, Behind: 0 },
                "upstream may use a different branch name while push follows its own settings");
            await Git(runner, root, "config", "branch.main.merge", "refs/heads/main");
            await Git(runner, root, "tag", "main", baseId);
            Assert((await provider.GetHistorySnapshotAsync(root)).Git?.CurrentBranch == "main" &&
                (await provider.GetRepositoryAsync(root)).Branch == "main",
                "a same-named tag does not change the current branch identity");
            await Git(runner, root, "tag", "-d", "main");

            await File.WriteAllTextAsync(Path.Combine(root, "local.txt"), "local\n");
            await Git(runner, root, "add", "--", "local.txt");
            await Git(runner, root, "commit", "-m", "local only");
            var localId = (await Git(runner, root, "rev-parse", "HEAD")).StandardOutput.Trim();
            var ahead = await provider.GetHistorySnapshotAsync(root);
            Assert(ahead.Git?.Ahead == 1 && ahead.Git.Behind == 0 && ahead.Entries[0].Relation == HistoryRelation.LocalOnly,
                "ahead count and local-only relation");
            var details = await provider.GetHistoryDetailsAsync(root, localId);
            Assert(details.Entry.ParentIds.SequenceEqual([baseId]) && details.Entry.AuthorEmail == "history@example.invalid" &&
                details.Entry.CommitterEmail == "history@example.invalid" && details.Entry.CommitDate.Length > 0,
                "commit detail exposes parent, author/committer, and commit date");

            var remoteTree = (await Git(runner, root, "rev-parse", $"{baseId}^{{tree}}")).StandardOutput.Trim();
            var remoteId = (await Git(runner, root, "commit-tree", remoteTree, "-p", baseId, "-m", "remote only")).StandardOutput.Trim();
            await Git(runner, root, "update-ref", "refs/remotes/origin/main", remoteId);
            var diverged = await provider.GetHistorySnapshotAsync(root);
            Assert(diverged.Git?.Ahead == 1 && diverged.Git.Behind == 1 &&
                diverged.Entries.Any(entry => entry.Relation == HistoryRelation.LocalOnly) &&
                diverged.Entries.Any(entry => entry.Relation == HistoryRelation.UpstreamOnly) &&
                diverged.Entries.Any(entry => entry.Relation == HistoryRelation.Shared),
                "ahead, behind, and shared/local-only/upstream-only relation after divergence");
            var capturedFetch = await provider.ExecuteAsync(new VcsRequest(VcsOperation.Fetch, root));
            Assert(capturedFetch.Success && fetchRunner.FetchAllCalls == 1,
                "explicit fetch runs git fetch --all without prune");

            var allBranches = await provider.GetHistorySnapshotAsync(root, HistoryScope.AllBranches);
            Assert(allBranches.ScopeLabel.Contains("모든", StringComparison.Ordinal) && allBranches.Entries.Count >= 3,
                "all branches scope includes ref-reachable history");
            await File.AppendAllTextAsync(Path.Combine(root, "base.txt"), "unfinished work\n");
            await Git(runner, root, "stash", "push", "-m", "unrelated stash");
            var stashId = (await Git(runner, root, "rev-parse", "refs/stash")).StandardOutput.Trim();
            Assert(!(await provider.GetHistorySnapshotAsync(root, HistoryScope.AllBranches)).Entries.Any(entry => entry.Id == stashId),
                "all-branch scope excludes stash and other non-branch/tag refs");

            await Git(runner, root, "checkout", "--detach");
            var detached = await provider.GetHistorySnapshotAsync(root);
            Assert(detached.Git is { IsDetached: true, CurrentBranch: "", Ahead: null, Behind: null } &&
                detached.Entries.All(entry => entry.Relation == HistoryRelation.Unknown),
                "detached HEAD has no branch-relative counts or classifications");

            var unborn = Path.Combine(scratch, "unborn");
            await Git(runner, scratch, "init", "--initial-branch=empty", unborn);
            var empty = await provider.GetHistorySnapshotAsync(unborn);
            Assert(empty.Git is { IsUnborn: true, IsDetached: false, Ahead: null, Behind: null } && empty.Entries.Count == 0,
                "unborn branch has explicit no-commit state");
            await Configure(runner, unborn);
            await File.WriteAllTextAsync(Path.Combine(unborn, "other.txt"), "other ref\n");
            await Git(runner, unborn, "add", "--", "other.txt");
            await Git(runner, unborn, "commit", "-m", "only branch commit");
            Assert((await provider.GetHistorySnapshotAsync(unborn, HistoryScope.AllBranches)).Entries.Count == 1,
                "all-branches history works from an unborn current branch");

            var noUpstream = Path.Combine(scratch, "no-upstream");
            await Git(runner, scratch, "init", "--initial-branch=main", noUpstream);
            await Configure(runner, noUpstream);
            await File.WriteAllTextAsync(Path.Combine(noUpstream, "file.txt"), "no upstream\n");
            await Git(runner, noUpstream, "add", "--", "file.txt");
            await Git(runner, noUpstream, "commit", "-m", "no upstream commit");
            var noTrack = await provider.GetHistorySnapshotAsync(noUpstream);
            Assert(noTrack.Git is { Upstream: "", Ahead: null, Behind: null } && noTrack.Git.Status == "추적 브랜치 없음" &&
                noTrack.Entries.All(entry => entry.Relation == HistoryRelation.Unknown),
                "no-upstream state does not invent zero counts or local-only labels");

            await Git(runner, root, "update-ref", "-d", "refs/remotes/origin/main");
            await Git(runner, root, "checkout", "main");
            var missingTracking = await provider.GetHistorySnapshotAsync(root);
            Assert(missingTracking.Git is { Upstream: "origin/main", UpstreamId: "", Ahead: null, Behind: null } &&
                missingTracking.Git.Status.Contains("가져오기 필요", StringComparison.Ordinal),
                "configured but missing tracking ref is distinct from no upstream");

            var shallow = Path.Combine(scratch, "shallow");
            await Git(runner, scratch, "init", "--initial-branch=main", shallow);
            await Configure(runner, shallow);
            await File.WriteAllTextAsync(Path.Combine(shallow, "shallow.txt"), "one\n");
            await Git(runner, shallow, "add", "--", "shallow.txt");
            await Git(runner, shallow, "commit", "-m", "shallow base");
            var shallowParent = (await Git(runner, shallow, "rev-parse", "HEAD")).StandardOutput.Trim();
            await File.AppendAllTextAsync(Path.Combine(shallow, "shallow.txt"), "two\n");
            await Git(runner, shallow, "commit", "-am", "shallow child");
            var shallowHead = (await Git(runner, shallow, "rev-parse", "HEAD")).StandardOutput.Trim();
            await Git(runner, shallow, "update-ref", "refs/remotes/origin/main", shallowParent);
            await Git(runner, shallow, "config", "branch.main.remote", "origin");
            await Git(runner, shallow, "config", "branch.main.merge", "refs/heads/main");
            var shallowFile = (await Git(runner, shallow, "rev-parse", "--git-path", "shallow")).StandardOutput.Trim();
            await File.WriteAllTextAsync(Path.IsPathRooted(shallowFile) ? shallowFile : Path.Combine(shallow, shallowFile), shallowHead + Environment.NewLine);
            var shallowSnapshot = await provider.GetHistorySnapshotAsync(shallow);
            Assert(shallowSnapshot.Git is { IsShallow: true, Ahead: null, Behind: null } &&
                shallowSnapshot.Entries[0].ParentIds.SequenceEqual([shallowParent]) &&
                shallowSnapshot.Entries.All(entry => entry.Relation == HistoryRelation.Unknown),
                "shallow boundary keeps raw parent and suppresses incomplete counts/classifications");

            var longHistory = Path.Combine(scratch, "long-history");
            await Git(runner, scratch, "init", "--initial-branch=main", longHistory);
            var import = new System.Text.StringBuilder();
            for (var index = 1; index <= 205; index++)
            {
                var message = $"bounded commit {index}";
                import.Append($"commit refs/heads/main\nmark :{index}\nauthor Original Author <author@example.invalid> {1700000000 + index} +0000\ncommitter Integrator <committer@example.invalid> {1700001000 + index} +0000\ndata {message.Length}\n{message}\n");
                if (index > 1) import.Append($"from :{index - 1}\n");
                import.Append('\n');
            }
            (await runner.RunAsync(new CommandSpec("git", ["fast-import", "--quiet"], longHistory, import.ToString()))).EnsureSuccess();
            var bounded = await provider.GetHistorySnapshotAsync(longHistory);
            Assert(bounded.IsTruncated && bounded.Entries.Count == 200 && bounded.Entries[^1].ParentIds.Count == 1,
                "history is bounded to 200 commits and retains the parent beyond the visible range");
            var tip = bounded.Entries[0];
            var tipDetails = (await provider.GetHistoryDetailsAsync(longHistory, tip.Id)).Entry;
            Assert(tip.Date != tip.CommitDate && tip.Author != tip.Committer &&
                tip.Date == tipDetails.Date && tip.CommitDate == tipDetails.CommitDate,
                "author and committer identities/dates remain distinct and agree with selected details");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previousGlobal);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", previousSystem);
            RemoveCreatedDirectory(scratchParent, scratch);
        }
    }

    private static async Task Configure(ProcessCommandRunner runner, string path)
    {
        await Git(runner, path, "config", "user.name", "History Test");
        await Git(runner, path, "config", "user.email", "history@example.invalid");
    }

    private static async Task<CommandResult> Git(ProcessCommandRunner runner, string path, params string[] args)
    {
        var result = await runner.RunAsync(new CommandSpec("git", args, path, Timeout: TimeSpan.FromSeconds(30)));
        if (!result.Success) throw new InvalidOperationException($"Git history context fixture failed: {string.Join(' ', args)}{Environment.NewLine}{result.Output}");
        return result;
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"Git history context test failed: {description}");
    }

    private static void RemoveCreatedDirectory(string parent, string target)
    {
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(target);
        if (fullTarget.Equals(Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase) ||
            !fullTarget.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete a Git history fixture outside its scratch folder.");
        if (!Directory.Exists(fullTarget)) return;
        foreach (var file in Directory.EnumerateFiles(fullTarget, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        foreach (var directory in Directory.EnumerateDirectories(fullTarget, "*", SearchOption.AllDirectories))
            File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
        Directory.Delete(fullTarget, recursive: true);
    }

    private sealed class FetchRecordingRunner(ICommandRunner inner) : ICommandRunner
    {
        public int FetchAllCalls { get; private set; }

        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            if (command.Arguments.SequenceEqual(["fetch", "--all"]))
            {
                FetchAllCalls++;
                return Task.FromResult(new CommandResult(0, "", ""));
            }
            return inner.RunAsync(command, cancellationToken);
        }
    }
}
