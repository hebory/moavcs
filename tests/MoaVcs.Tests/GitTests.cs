using MoaVcs.Core;
using MoaVcs.Core.Providers;

namespace MoaVcs.Tests;

public static class GitTests
{
    public static async Task RunAsync()
    {
        var runner = new ProcessCommandRunner();
        var provider = new GitProvider(runner);
        var scratch = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local"));
        Directory.CreateDirectory(scratch);
        var root = Path.Combine(scratch, "test-git-" + Guid.NewGuid().ToString("N"));
        var unborn = Path.Combine(scratch, "test-git-unborn-" + Guid.NewGuid().ToString("N"));
        var renamed = Path.Combine(scratch, "test-git-rename-" + Guid.NewGuid().ToString("N"));
        var clone = Path.Combine(scratch, "test-git-clone-" + Guid.NewGuid().ToString("N"));
        var optionClone = Path.Combine(scratch, "test-git-option-clone-" + Guid.NewGuid().ToString("N"));
        Exception? testFailure = null;
        try
        {
            Directory.CreateDirectory(root);
            (await Run(runner, root, "init")).EnsureSuccess();
            (await Run(runner, root, "config", "user.name", "moavcs test")).EnsureSuccess();
            (await Run(runner, root, "config", "user.email", "moavcs@example.invalid")).EnsureSuccess();
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "one\n", new System.Text.UTF8Encoding(false));
            (await Run(runner, root, "add", "--", "README.md")).EnsureSuccess();
            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Commit, root, Message: "initial"))).EnsureSuccess();

            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "one\ntwo\n", new System.Text.UTF8Encoding(false));
            const string unicodePath = "한글 path.txt";
            await File.WriteAllTextAsync(Path.Combine(root, unicodePath), "new file\n", new System.Text.UTF8Encoding(false));
            var status = await provider.GetStatusAsync(root);
            Assert(status.Any(c => c.Path == "README.md" && c.Status == "Modified" && !c.IsStaged), "worktree modification status");
            Assert(status.Any(c => c.Path == unicodePath && c.Status == "Untracked"), "porcelain -z Unicode/space path parsing");
            var preview = await provider.GetDiffAsync(root, status.Single(c => c.Path == unicodePath));
            Assert(preview.Contains("+new file", StringComparison.Ordinal), "untracked file diff preview");

            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Stage, root, ["README.md", unicodePath]))).EnsureSuccess();
            status = await provider.GetStatusAsync(root);
            Assert(status.Where(c => c.Path is "README.md" or unicodePath).All(c => c.IsStaged), "stage selected paths");
            var indexDiff = await provider.GetDiffAsync(root, status.Single(c => c.Path == "README.md"), staged: true);
            Assert(indexDiff.Contains("+two", StringComparison.Ordinal), "staged index diff");
            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Unstage, root, [unicodePath]))).EnsureSuccess();
            status = await provider.GetStatusAsync(root);
            Assert(status.Single(c => c.Path == unicodePath).Status == "Untracked", "unstage preserves worktree file");

            const string literalName = "[draft].txt";
            await File.WriteAllTextAsync(Path.Combine(root, literalName), "literal path\n", new System.Text.UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "d.txt"), "wildcard neighbor\n", new System.Text.UTF8Encoding(false));
            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Stage, root, [literalName]))).EnsureSuccess();
            status = await provider.GetStatusAsync(root);
            Assert(status.Single(c => c.Path == literalName).IsStaged, "literal bracket filename is staged exactly");
            Assert(status.Single(c => c.Path == "d.txt").Status == "Untracked", "bracket filename does not expand as a pathspec");

            await Run(runner, root, "mv", "--", "README.md", "renamed README.md");
            status = await provider.GetStatusAsync(root);
            Assert(status.Any(c => c.Status == "Renamed" && c.Path == "renamed README.md" && c.OriginalPath == "README.md"), "porcelain -z rename parsing");

            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Stage, root, ["renamed README.md", unicodePath]))).EnsureSuccess();
            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Commit, root, Message: "add unicode file and rename"))).EnsureSuccess();
            var history = await provider.GetHistoryAsync(root);
            Assert(history.Count >= 2 && history[0].Message.Contains("add unicode file", StringComparison.Ordinal), "history with NUL-separated fields");
            Assert(history.All(entry => entry.Id.Length == 40 && entry.Id.All(Uri.IsHexDigit)), "history revision IDs contain no record-separator newline");
            var repository = await provider.GetRepositoryAsync(root);
            Assert(repository.RootPath == root && repository.Branch.Length > 0, "repository root/branch detection");
            Assert((await provider.GetStatusAsync(root)).Single(c => c.Path == "d.txt").Status == "Untracked", "commit leaves unrelated untracked files alone");

            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Clone, scratch, Remote: root, Destination: clone))).EnsureSuccess();
            Assert((await provider.GetRepositoryAsync(clone)).RootPath == clone, "clone accepts a local repository URL after the option terminator");
            try
            {
                await provider.ExecuteAsync(new VcsRequest(VcsOperation.Clone, scratch, Remote: "--help",
                    Destination: optionClone));
                throw new InvalidOperationException("Expected a dash-prefixed Git clone remote to be treated as a remote path, not an option.");
            }
            catch (VcsException) { }

            Directory.CreateDirectory(renamed);
            (await Run(runner, renamed, "init")).EnsureSuccess();
            (await Run(runner, renamed, "config", "user.name", "moavcs test")).EnsureSuccess();
            (await Run(runner, renamed, "config", "user.email", "moavcs@example.invalid")).EnsureSuccess();
            await File.WriteAllTextAsync(Path.Combine(renamed, "old name.txt"), "rename source\n");
            (await Run(runner, renamed, "add", "--", "old name.txt")).EnsureSuccess();
            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Commit, renamed, Message: "rename test base"))).EnsureSuccess();
            (await Run(runner, renamed, "mv", "--", "old name.txt", "new name.txt")).EnsureSuccess();
            var stagedRename = (await provider.GetStatusAsync(renamed)).Single();
            Assert(stagedRename.Status == "Renamed" && stagedRename.OriginalPath == "old name.txt", "rename test setup");
            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Unstage, renamed,
                ["new name.txt", stagedRename.OriginalPath!]))).EnsureSuccess();
            var unstagedRename = await provider.GetStatusAsync(renamed);
            Assert(unstagedRename.Count == 2 && unstagedRename.All(c => !c.IsStaged) &&
                unstagedRename.Any(c => c.Path == "old name.txt" && c.Status == "Deleted") &&
                unstagedRename.Any(c => c.Path == "new name.txt" && c.Status == "Untracked"),
                "unstaging a staged rename with both old and new paths");

            Directory.CreateDirectory(unborn);
            (await Run(runner, unborn, "init")).EnsureSuccess();
            Assert((await provider.GetHistoryAsync(unborn)).Count == 0, "new repositories have an empty history");
            (await Run(runner, unborn, "config", "user.name", "moavcs test")).EnsureSuccess();
            (await Run(runner, unborn, "config", "user.email", "moavcs@example.invalid")).EnsureSuccess();
            await File.WriteAllTextAsync(Path.Combine(unborn, "first.txt"), "first\n");
            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Stage, unborn, ["first.txt"]))).EnsureSuccess();
            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Unstage, unborn, ["first.txt"]))).EnsureSuccess();
            Assert((await provider.GetStatusAsync(unborn)).Single().Status == "Untracked", "unstage in unborn repository");

            try
            {
                await provider.ExecuteAsync(new VcsRequest(VcsOperation.Stage, root, ["../outside.txt"]));
                throw new InvalidOperationException("Expected repository path validation to reject traversal.");
            }
            catch (VcsException) { }
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                RemoveCreatedDirectory(scratch, root);
                RemoveCreatedDirectory(scratch, unborn);
                RemoveCreatedDirectory(scratch, renamed);
                RemoveCreatedDirectory(scratch, clone);
                RemoveCreatedDirectory(scratch, optionClone);
            }
            catch when (testFailure is not null)
            {
                // Keep the assertion or process error as the primary failure.
            }
        }
    }

    private static Task<CommandResult> Run(ICommandRunner runner, string root, params string[] args) =>
        runner.RunAsync(new CommandSpec("git", args, root, Timeout: TimeSpan.FromSeconds(30)));

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"Git test failed: {description}");
    }

    private static void RemoveCreatedDirectory(string parent, string target)
    {
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(target);
        if (fullTarget.Equals(Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase) ||
            !fullTarget.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete a test directory outside its scratch folder.");
        if (!Directory.Exists(fullTarget)) return;

        // Windows marks Git loose object files read-only; clear that attribute only after verifying the exact test target.
        foreach (var file in Directory.EnumerateFiles(fullTarget, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        foreach (var directory in Directory.EnumerateDirectories(fullTarget, "*", SearchOption.AllDirectories))
            File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
        Directory.Delete(fullTarget, recursive: true);
    }
}
