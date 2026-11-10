using MoaVcs.Core;
using MoaVcs.Core.Providers;

namespace MoaVcs.Tests;

public static class PushTests
{
    public static async Task RunAsync()
    {
        var runner = new ProcessCommandRunner();
        var provider = new GitProvider(runner);
        var scratch = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local"));
        Directory.CreateDirectory(scratch);
        var sandbox = Path.Combine(scratch, "test-push-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        var globalConfig = Path.Combine(sandbox, "empty-global.config");
        await File.WriteAllTextAsync(globalConfig, string.Empty);
        var previousGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var previousSystem = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
        Exception? testFailure = null;

        try
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", globalConfig);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");

            var first = await CreatePushFixtureAsync(sandbox, "first", runner);
            await Git(runner, first.Working, "remote", "add", "origin", first.Bare);
            var ordinaryPush = await GitRaw(runner, first.Working, "push");
            Assert(!ordinaryPush.Success, "plain first push reproduces the missing-upstream failure");
            AssertConfigAbsent(await GitRaw(runner, first.Working, "config", "--local", "--get-all", "branch.master.remote"),
                "fixture begins without a configured upstream");

            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Push, first.Working))).EnsureSuccess();
            Assert(await ConfigValueAsync(runner, first.Working, "branch.master.remote") == "origin" &&
                   await ConfigValueAsync(runner, first.Working, "branch.master.merge") == "refs/heads/master",
                "first provider push publishes the current branch and sets its upstream");
            Assert(await ConfigValueAsync(runner, first.Working, "push.autoSetupRemote") == "false",
                "automatic upstream behavior is scoped to the provider push and is not persisted");
            Assert(await RefExistsAsync(runner, first.Bare, "refs/heads/master"), "first push publishes only the current branch");

            await CommitChangeAsync(runner, first.Working, "second.txt", "second commit\n", "second commit");
            var secondLocalHead = await Git(runner, first.Working, "rev-parse", "HEAD");
            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Push, first.Working))).EnsureSuccess();
            var secondRemoteHead = await Git(runner, first.Bare, "rev-parse", "refs/heads/master");
            Assert(secondRemoteHead.StandardOutput.Trim() == secondLocalHead.StandardOutput.Trim(),
                "subsequent push transfers the new commit through the established upstream");

            var pushDefault = await CreatePushFixtureAsync(sandbox, "push-default", runner);
            var fork = Path.Combine(sandbox, "push-default-fork.git");
            await CreateBareRepositoryAsync(runner, sandbox, fork);
            await Git(runner, pushDefault.Working, "remote", "add", "origin", pushDefault.Bare);
            await Git(runner, pushDefault.Working, "remote", "add", "fork", fork);
            await Git(runner, pushDefault.Working, "config", "--local", "remote.pushDefault", "fork");
            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Push, pushDefault.Working))).EnsureSuccess();
            Assert(await RefExistsAsync(runner, fork, "refs/heads/master") &&
                   !await RefExistsAsync(runner, pushDefault.Bare, "refs/heads/master"),
                "first push honors remote.pushDefault instead of forcing origin");
            Assert(await ConfigValueAsync(runner, pushDefault.Working, "branch.master.remote") == "fork",
                "upstream follows Git's selected push remote");

            var customRefspec = await CreatePushFixtureAsync(sandbox, "custom-refspec", runner);
            await Git(runner, customRefspec.Working, "remote", "add", "origin", customRefspec.Bare);
            await Git(runner, customRefspec.Working, "config", "--local", "--add", "remote.origin.push",
                "refs/heads/master:refs/heads/published");
            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Push, customRefspec.Working))).EnsureSuccess();
            Assert(await RefExistsAsync(runner, customRefspec.Bare, "refs/heads/published") &&
                   !await RefExistsAsync(runner, customRefspec.Bare, "refs/heads/master"),
                "first push preserves the configured remote refspec");
            Assert(await ConfigValueAsync(runner, customRefspec.Working, "remote.origin.push") ==
                   "refs/heads/master:refs/heads/published", "custom push refspec remains unchanged");

            var nothing = await CreatePushFixtureAsync(sandbox, "push-nothing", runner);
            await Git(runner, nothing.Working, "remote", "add", "origin", nothing.Bare);
            await Git(runner, nothing.Working, "config", "--local", "push.default", "nothing");
            await AssertThrowsVcsAsync(() => provider.ExecuteAsync(new VcsRequest(VcsOperation.Push, nothing.Working)),
                "push.default=nothing keeps its explicit no-push behavior");
            AssertConfigAbsent(await GitRaw(runner, nothing.Working, "config", "--local", "--get-all", "branch.master.remote"),
                "unsupported push.default mode does not create upstream configuration");
            Assert(!await RefExistsAsync(runner, nothing.Bare, "refs/heads/master"), "push.default=nothing sends no branch");

            var stale = await CreatePushFixtureAsync(sandbox, "stale-upstream", runner);
            await Git(runner, stale.Working, "remote", "add", "origin", stale.Bare);
            await Git(runner, stale.Working, "config", "--local", "branch.master.remote", "origin");
            await Git(runner, stale.Working, "config", "--local", "branch.master.merge", "refs/heads/ghost");
            Assert(!await RefExistsAsync(runner, stale.Working, "refs/remotes/origin/ghost"),
                "stale-upstream fixture has configured tracking metadata but no local tracking ref");
            try { await provider.ExecuteAsync(new VcsRequest(VcsOperation.Push, stale.Working)); }
            catch (VcsException) { /* An absent tracking ref may make Git reject the push; configuration must still be preserved. */ }
            Assert(await ConfigValueAsync(runner, stale.Working, "branch.master.remote") == "origin" &&
                   await ConfigValueAsync(runner, stale.Working, "branch.master.merge") == "refs/heads/ghost",
                "a missing tracking ref does not rewrite existing upstream configuration");

            await AssertNonFastForwardDoesNotOverwriteAsync(sandbox, runner, provider);
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
            catch when (testFailure is not null) { /* Preserve the assertion or Git error. */ }
        }
    }

    private static async Task AssertNonFastForwardDoesNotOverwriteAsync(string sandbox, ICommandRunner runner, GitProvider provider)
    {
        var remote = Path.Combine(sandbox, "non-ff.git");
        await CreateBareRepositoryAsync(runner, sandbox, remote);
        var seed = Path.Combine(sandbox, "non-ff-seed");
        await CreateWorkingRepositoryAsync(runner, seed);
        await CommitChangeAsync(runner, seed, "history.txt", "remote base\n", "remote base");
        await Git(runner, seed, "remote", "add", "origin", remote);
        (await Git(runner, seed, "push", "origin", "master")).EnsureSuccess();
        var remoteBefore = await Git(runner, sandbox, "--git-dir", remote, "rev-parse", "refs/heads/master");

        var local = Path.Combine(sandbox, "non-ff-local");
        await CreateWorkingRepositoryAsync(runner, local);
        await CommitChangeAsync(runner, local, "history.txt", "unrelated local history\n", "local divergent commit");
        await Git(runner, local, "remote", "add", "origin", remote);
        await AssertThrowsVcsAsync(() => provider.ExecuteAsync(new VcsRequest(VcsOperation.Push, local)),
            "non-fast-forward first push is rejected");

        var remoteAfter = await Git(runner, sandbox, "--git-dir", remote, "rev-parse", "refs/heads/master");
        Assert(remoteAfter.StandardOutput.Trim() == remoteBefore.StandardOutput.Trim(),
            "non-fast-forward failure leaves the remote commit untouched");
        AssertConfigAbsent(await GitRaw(runner, local, "config", "--local", "--get-all", "branch.master.remote"),
            "failed push does not create an upstream");
    }

    private static async Task<(string Working, string Bare)> CreatePushFixtureAsync(string sandbox, string name, ICommandRunner runner)
    {
        var working = Path.Combine(sandbox, name);
        var bare = Path.Combine(sandbox, name + ".git");
        await CreateWorkingRepositoryAsync(runner, working);
        await CreateBareRepositoryAsync(runner, sandbox, bare);
        await CommitChangeAsync(runner, working, "initial.txt", "first commit\n", "initial commit");
        return (working, bare);
    }

    private static async Task CreateWorkingRepositoryAsync(ICommandRunner runner, string path)
    {
        Directory.CreateDirectory(path);
        await Git(runner, path, "init", "--quiet", "--initial-branch=master");
        await Git(runner, path, "config", "--local", "user.name", "moavcs push test");
        await Git(runner, path, "config", "--local", "user.email", "moavcs-push@example.invalid");
        await Git(runner, path, "config", "--local", "push.autoSetupRemote", "false");
    }

    private static async Task CreateBareRepositoryAsync(ICommandRunner runner, string workingDirectory, string bare)
    {
        Directory.CreateDirectory(bare);
        await Git(runner, workingDirectory, "init", "--quiet", "--bare", "--initial-branch=master", bare);
    }

    private static async Task CommitChangeAsync(ICommandRunner runner, string root, string file, string contents, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(root, file), contents, new System.Text.UTF8Encoding(false));
        await Git(runner, root, "add", "--", file);
        await Git(runner, root, "commit", "--quiet", "-m", message);
    }

    private static async Task<bool> RefExistsAsync(ICommandRunner runner, string repository, string reference)
    {
        var result = await GitRaw(runner, repository, "show-ref", "--verify", "--quiet", reference);
        if (result.ExitCode == 0) return true;
        if (result.ExitCode == 1) return false;
        result.EnsureSuccess();
        return false;
    }

    private static async Task<string> ConfigValueAsync(ICommandRunner runner, string root, string key) =>
        (await Git(runner, root, "config", "--local", "--get", key)).StandardOutput.Trim();

    private static void AssertConfigAbsent(CommandResult result, string message)
    {
        Assert(result.ExitCode == 1 && result.StandardOutput.Length == 0, message);
    }

    private static async Task<CommandResult> Git(ICommandRunner runner, string root, params string[] args) =>
        (await GitRaw(runner, root, args)).EnsureSuccess();

    private static Task<CommandResult> GitRaw(ICommandRunner runner, string root, params string[] args) =>
        runner.RunAsync(new CommandSpec("git", args, root, Timeout: TimeSpan.FromSeconds(30)));

    private static async Task AssertThrowsVcsAsync(Func<Task> operation, string description)
    {
        try
        {
            await operation();
            throw new InvalidOperationException($"Push test failed: {description}");
        }
        catch (VcsException) { }
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"Push test failed: {description}");
    }

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
