using MoaVcs.Core;
using MoaVcs.Core.Providers;

namespace MoaVcs.Tests;

public static class RemoteTests
{
    public static async Task RunAsync()
    {
        var runner = new ProcessCommandRunner();
        var provider = new GitProvider(runner);
        var scratch = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local"));
        Directory.CreateDirectory(scratch);
        var sandbox = Path.Combine(scratch, "test-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        var globalConfig = Path.Combine(sandbox, "empty-global.config");
        await File.WriteAllTextAsync(globalConfig, string.Empty);
        var previousGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var previousSystem = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
        Exception? testFailure = null;

        try
        {
            // Keep machine/user Git settings out of these isolated fixtures. No user's config is read or modified.
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", globalConfig);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");

            ValidateUrlInput();

            var globalRepo = await CreateRepositoryAsync(sandbox, "global", runner);
            var originalGlobalBytes = await File.ReadAllBytesAsync(globalConfig);
            try
            {
                await File.WriteAllTextAsync(globalConfig, "[remote \"origin\"]\n\turl = https://global.invalid/repo.git\n");
                var globalInfo = await provider.GetRemoteAsync(globalRepo);
                Assert(globalInfo.Exists && globalInfo.FetchUrls.SequenceEqual(["https://global.invalid/repo.git"]) &&
                       globalInfo.ReadOnlyReason is not null, "global origin URL is surfaced as read-only");
                var currentGlobalBytes = await File.ReadAllBytesAsync(globalConfig);
                await AssertThrowsVcsAsync(() => provider.SetRemoteUrlAsync(globalRepo, "https://blocked.invalid/repo.git", globalInfo),
                    "global origin URL cannot be overwritten");
                Assert((await File.ReadAllBytesAsync(globalConfig)).SequenceEqual(currentGlobalBytes),
                    "global config bytes are unchanged by attempted origin update");
            }
            finally
            {
                await File.WriteAllBytesAsync(globalConfig, originalGlobalBytes);
            }

            var queryFailureRepo = await CreateRepositoryAsync(sandbox, "query-failure", runner);
            var failingQueryProvider = new GitProvider(new FailingRemoteQueryRunner(queryFailureRepo));
            await AssertThrowsVcsAsync(() => failingQueryProvider.GetRemoteAsync(queryFailureRepo),
                "failure to resolve effective origin URLs is surfaced");

            var absent = await CreateRepositoryAsync(sandbox, "absent", runner);
            var missing = await provider.GetRemoteAsync(absent);
            Assert(!missing.Exists && missing.Name == "origin" && missing.FetchUrls.Count == 0 &&
                   missing.PushUrls.Count == 0 && missing.EffectiveFetchUrls.Count == 0 &&
                   missing.EffectivePushUrls.Count == 0, "origin lookup when no remote exists");
            await provider.SetRemoteUrlAsync(absent, "  https://example.invalid/new.git  ", missing);
            var added = await provider.GetRemoteAsync(absent);
            Assert(added.Exists && added.FetchUrls.SequenceEqual(["https://example.invalid/new.git"]) &&
                   added.PushUrls.Count == 0, "missing origin is added without fetching");

            var multiple = await CreateRepositoryAsync(sandbox, "multiple", runner);
            await Run(runner, multiple, "remote", "add", "origin", "https://fetch-one.invalid/[repo].git");
            await Run(runner, multiple, "remote", "set-url", "--add", "origin", "ssh://fetch-two.invalid/team/repo.git");
            await Run(runner, multiple, "config", "--add", "remote.origin.pushurl", "ssh://push-one.invalid/team/repo.git");
            await Run(runner, multiple, "config", "--add", "remote.origin.pushurl", "https://push-two.invalid/team/repo.git");
            await Run(runner, multiple, "remote", "add", "upstream", "https://upstream.invalid/repo.git");
            await Run(runner, multiple, "config", "--add", "remote.upstream.pushurl", "ssh://upstream-push.invalid/repo.git");
            var before = await provider.GetRemoteAsync(multiple);
            Assert(before.FetchUrls.SequenceEqual(["https://fetch-one.invalid/[repo].git", "ssh://fetch-two.invalid/team/repo.git"]),
                "configured multi-fetch URLs are returned in order");
            Assert(before.PushUrls.SequenceEqual(["ssh://push-one.invalid/team/repo.git", "https://push-two.invalid/team/repo.git"]),
                "configured multi-push URLs are returned separately");
            Assert(before.EffectiveFetchUrls.SequenceEqual(before.FetchUrls) &&
                   before.EffectivePushUrls.SequenceEqual(before.PushUrls), "effective URLs match without rewrites");
            var upstreamBefore = (await Run(runner, multiple, "config", "--local", "--get-regexp", "^remote\\.upstream\\.")).StandardOutput;

            await provider.SetRemoteUrlAsync(multiple, "https://fetch-replacement.invalid/repo.git", before);
            var after = await provider.GetRemoteAsync(multiple);
            Assert(after.FetchUrls.SequenceEqual(["https://fetch-replacement.invalid/repo.git", "ssh://fetch-two.invalid/team/repo.git"]),
                "changing origin replaces only its first fetch URL");
            Assert(after.PushUrls.SequenceEqual(before.PushUrls), "changing fetch URL preserves every configured push URL");
            Assert((await Run(runner, multiple, "config", "--local", "--get-regexp", "^remote\\.upstream\\.")).StandardOutput == upstreamBefore,
                "changing origin leaves another remote untouched");

            var duplicate = await CreateRepositoryAsync(sandbox, "duplicate", runner);
            const string duplicateUrl = "https://duplicate.invalid/[repo].git";
            await Run(runner, duplicate, "remote", "add", "origin", duplicateUrl);
            await Run(runner, duplicate, "config", "--local", "--add", "remote.origin.url", duplicateUrl);
            var duplicateInfo = await provider.GetRemoteAsync(duplicate);
            Assert(duplicateInfo.ReadOnlyReason is not null, "duplicate first fetch URL is marked read-only");
            await AssertThrowsVcsAsync(() => provider.SetRemoteUrlAsync(duplicate, "https://replacement.invalid/repo.git", duplicateInfo),
                "duplicate first fetch URL cannot be replaced ambiguously");
            Assert((await provider.GetRemoteAsync(duplicate)).FetchUrls.SequenceEqual([duplicateUrl, duplicateUrl]),
                "ambiguous duplicate URL configuration remains unchanged");

            var fallback = await CreateRepositoryAsync(sandbox, "fallback", runner);
            await Run(runner, fallback, "remote", "add", "origin", "https://fallback-one.invalid/repo.git");
            await Run(runner, fallback, "remote", "set-url", "--add", "origin", "https://fallback-two.invalid/repo.git");
            var fallbackInfo = await provider.GetRemoteAsync(fallback);
            Assert(fallbackInfo.PushUrls.Count == 0 && fallbackInfo.EffectivePushUrls.SequenceEqual(fallbackInfo.FetchUrls),
                "push URL fallback is distinguished from explicit pushurl values");

            var rewrite = await CreateRepositoryAsync(sandbox, "rewrite", runner);
            await Run(runner, rewrite, "remote", "add", "origin", "corp:team/repo.git");
            await Run(runner, rewrite, "config", "url.https://rewrite.invalid/base/.insteadOf", "corp:");
            await Run(runner, rewrite, "config", "url.ssh://push-rewrite.invalid/base/.pushInsteadOf", "corp:");
            var rewritten = await provider.GetRemoteAsync(rewrite);
            Assert(rewritten.FetchUrls.SequenceEqual(["corp:team/repo.git"]) &&
                   rewritten.EffectiveFetchUrls.SequenceEqual(["https://rewrite.invalid/base/team/repo.git"]) &&
                   rewritten.EffectivePushUrls.SequenceEqual(["ssh://push-rewrite.invalid/base/team/repo.git"]),
                "raw URL, insteadOf fetch URL and pushInsteadOf push URL are distinguished");

            var staleRepo = await CreateRepositoryAsync(sandbox, "stale", runner);
            await Run(runner, staleRepo, "remote", "add", "origin", "https://initial.invalid/repo.git");
            var staleSnapshot = await provider.GetRemoteAsync(staleRepo);
            await Run(runner, staleRepo, "config", "--local", "--replace-all", "remote.origin.url", "https://changed-elsewhere.invalid/repo.git");
            await AssertThrowsVcsAsync(() => provider.SetRemoteUrlAsync(staleRepo, "https://must-not-write.invalid/repo.git", staleSnapshot),
                "stale expected origin snapshot is rejected");
            Assert((await provider.GetRemoteAsync(staleRepo)).FetchUrls.SequenceEqual(["https://changed-elsewhere.invalid/repo.git"]),
                "stale snapshot rejection preserves the newer value");

            var included = await CreateRepositoryAsync(sandbox, "included", runner);
            var includeFile = Path.Combine(included, "origin-include.cfg");
            await File.WriteAllTextAsync(includeFile, "[remote \"origin\"]\n\turl = https://included.invalid/repo.git\n");
            await Run(runner, included, "config", "--local", "include.path", includeFile);
            var includedInfo = await provider.GetRemoteAsync(included);
            Assert(includedInfo.Exists && includedInfo.FetchUrls.SequenceEqual(["https://included.invalid/repo.git"]) &&
                   includedInfo.ReadOnlyReason is not null, "included origin URL is surfaced as read-only");
            await AssertThrowsVcsAsync(() => provider.SetRemoteUrlAsync(included, "https://blocked.invalid/repo.git", includedInfo),
                "included origin URL cannot be overwritten");
            Assert((await provider.GetRemoteAsync(included)).FetchUrls.SequenceEqual(["https://included.invalid/repo.git"]),
                "read-only origin remains unchanged");

            var worktree = await CreateRepositoryAsync(sandbox, "worktree", runner);
            await Run(runner, worktree, "config", "--local", "extensions.worktreeConfig", "true");
            await Run(runner, worktree, "config", "--worktree", "remote.origin.url", "https://worktree.invalid/repo.git");
            var worktreeInfo = await provider.GetRemoteAsync(worktree);
            Assert(worktreeInfo.Exists && worktreeInfo.FetchUrls.SequenceEqual(["https://worktree.invalid/repo.git"]) &&
                   worktreeInfo.ReadOnlyReason is not null, "worktree origin URL is surfaced as read-only");
            await AssertThrowsVcsAsync(() => provider.SetRemoteUrlAsync(worktree, "https://blocked.invalid/repo.git", worktreeInfo),
                "worktree origin URL cannot be overwritten");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await AssertThrowsCancelledAsync(() => provider.GetRemoteAsync(absent, cancelled.Token), "remote query honors cancellation");
            await AssertThrowsCancelledAsync(() => provider.SetRemoteUrlAsync(absent, "https://cancelled.invalid/repo.git", added, cancelled.Token),
                "remote update honors cancellation");
            Assert((await provider.GetRemoteAsync(absent)).FetchUrls.SequenceEqual(added.FetchUrls), "cancelled update makes no change");
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previousGlobal);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", previousSystem);
            try { RemoveCreatedDirectory(scratch, sandbox); }
            catch when (testFailure is not null) { /* Preserve the test assertion or Git error. */ }
        }
    }

    private static void ValidateUrlInput()
    {
        Assert(RemoteUrl.Validate("  https://host.invalid/team/repo.git ") == "https://host.invalid/team/repo.git",
            "URL validation trims surrounding whitespace");
        Assert(RemoteUrl.Validate("git@host.invalid:team/repo.git") == "git@host.invalid:team/repo.git", "SSH scp-style URL is accepted");
        Assert(RemoteUrl.Validate("ssh://host.invalid/team/repo.git") == "ssh://host.invalid/team/repo.git", "SSH URL is accepted");
        Assert(RemoteUrl.Validate("D:\\local repos\\repo.git") == "D:\\local repos\\repo.git", "local path with spaces is accepted");
        Assert(RemoteUrl.Validate("../local/repo.git") == "../local/repo.git", "relative local path is accepted");
        foreach (var invalid in new[] { "", "   ", "--help", "\nhttps://host.invalid/repo.git", "https://host.invalid/repo.git\t" })
        {
            try
            {
                RemoteUrl.Validate(invalid);
                throw new InvalidOperationException($"Remote test failed: accepted invalid address {Escape(invalid)}");
            }
            catch (VcsException) { }
        }
    }

    private static async Task<string> CreateRepositoryAsync(string sandbox, string name, ICommandRunner runner)
    {
        var path = Path.Combine(sandbox, name);
        Directory.CreateDirectory(path);
        await Run(runner, path, "init", "--quiet");
        return path;
    }

    private static async Task<CommandResult> Run(ICommandRunner runner, string root, params string[] args) =>
        (await runner.RunAsync(new CommandSpec("git", args, root, Timeout: TimeSpan.FromSeconds(30)))).EnsureSuccess();

    private sealed class FailingRemoteQueryRunner(string root) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var args = command.Arguments;
            if (args.SequenceEqual(["rev-parse", "--show-toplevel"]))
                return Result(0, root);
            if (args.SequenceEqual(["symbolic-ref", "-q", "HEAD"]))
                return Result(0, "refs/heads/main");
            if (args.Count >= 2 && args[0] == "config" && args[^2] == "--get-all" && args[^1] == "remote.origin.url")
                return Result(0, "local\0file:.git/config\0https://query.invalid/repo.git\0");
            if (args.Count >= 2 && args[0] == "config" && args[^2] == "--get-all" && args[^1] == "remote.origin.pushurl")
                return Result(1, "");
            if (args.SequenceEqual(["config", "--null", "--name-only", "--get-regexp", "^remote\\.origin\\."]))
                return Result(0, "remote.origin.url\0");
            if (args.SequenceEqual(["remote"]))
                return Result(0, "origin\n");
            if (args.SequenceEqual(["rev-parse", "--git-path", "config"]))
                return Result(0, ".git/config");
            if (args.SequenceEqual(["remote", "get-url", "--all", "origin"]))
                return Task.FromResult(new CommandResult(1, "", "synthetic effective URL query failure"));
            throw new InvalidOperationException($"Unexpected fake Git command: {string.Join(' ', args)}");
        }

        private static Task<CommandResult> Result(int exitCode, string output) =>
            Task.FromResult(new CommandResult(exitCode, output, ""));
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"Remote test failed: {description}");
    }

    private static async Task AssertThrowsVcsAsync(Func<Task> operation, string description)
    {
        try
        {
            await operation();
            throw new InvalidOperationException($"Remote test failed: {description}");
        }
        catch (VcsException) { }
    }

    private static async Task AssertThrowsCancelledAsync(Func<Task> operation, string description)
    {
        try
        {
            await operation();
            throw new InvalidOperationException($"Remote test failed: {description}");
        }
        catch (OperationCanceledException) { }
    }

    private static string Escape(string value) => value.Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);

    private static void RemoveCreatedDirectory(string parent, string target)
    {
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(target);
        if (fullTarget.Equals(Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase) ||
            !fullTarget.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete a test directory outside its scratch folder.");
        if (!Directory.Exists(fullTarget)) return;
        foreach (var file in Directory.EnumerateFiles(fullTarget, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        foreach (var directory in Directory.EnumerateDirectories(fullTarget, "*", SearchOption.AllDirectories))
            File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
        Directory.Delete(fullTarget, recursive: true);
    }
}
