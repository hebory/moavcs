using System.Text;
using MoaVcs.Core;
using MoaVcs.Core.Providers;

namespace MoaVcs.Tests;

public static class HistoryGitTests
{
    public static async Task RunAsync()
    {
        var scratch = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local"));
        Directory.CreateDirectory(scratch);
        var root = Path.Combine(scratch, "test-git-history-" + Guid.NewGuid().ToString("N"));
        var mergeRoot = Path.Combine(scratch, "test-git-history-merge-" + Guid.NewGuid().ToString("N"));
        var runner = new ProcessCommandRunner();
        var provider = new GitProvider(runner);
        Exception? failure = null;
        try
        {
            await InitializeRepositoryAsync(runner, root);
            await File.WriteAllTextAsync(Path.Combine(root, "old name.txt"), "base line\nstable one\nstable two\nstable three\nstable four\n", Utf8NoBom);
            await File.WriteAllTextAsync(Path.Combine(root, "delete.txt"), "remove me\n", Utf8NoBom);
            await File.WriteAllBytesAsync(Path.Combine(root, "binary.dat"), [0x00, 0x01, 0x02, 0xFF]);
            (await RunAsync(runner, root, "add", "--", ".")).EnsureSuccess();
            await CommitAsync(provider, root, "root commit\n\nRoot body line.");

            var rootEntry = (await provider.GetHistoryAsync(root)).Single();
            var rootDetails = await provider.GetHistoryDetailsAsync(root, rootEntry.Id);
            Assert(rootDetails.Entry.Message == "root commit\n\nRoot body line.", "full root commit message body");
            Assert(rootDetails.ComparisonLabel.StartsWith("첫 커밋", StringComparison.Ordinal), "root commit empty baseline label");
            Assert(rootDetails.Files.Count == 3, "root commit lists all initial files");
            var rootDiff = await provider.GetHistoryDiffAsync(root, rootEntry.Id);
            Assert(rootDiff.Contains("+base line", StringComparison.Ordinal) && rootDiff.Contains("Binary files", StringComparison.Ordinal),
                "root commit patch compares against the empty tree, including binary files");

            (await RunAsync(runner, root, "mv", "--", "old name.txt", "new name.txt")).EnsureSuccess();
            await File.WriteAllTextAsync(Path.Combine(root, "new name.txt"), "base line\nstable one\nstable two\nstable three\nstable four\ncommitted version\n", Utf8NoBom);
            File.Delete(Path.Combine(root, "delete.txt"));
            const string unicodePath = "추가 [draft] 한글.txt";
            await File.WriteAllTextAsync(Path.Combine(root, unicodePath), "added version\n", Utf8NoBom);
            await File.WriteAllBytesAsync(Path.Combine(root, "binary.dat"), [0x00, 0x03, 0x02, 0xFF]);
            (await RunAsync(runner, root, "add", "-A", "--")).EnsureSuccess();
            const string commitMessage = "change files\n\nA detailed body with multiple lines.\nSecond paragraph.";
            await CommitAsync(provider, root, commitMessage);

            var history = await provider.GetHistoryAsync(root);
            var changeEntry = history[0];
            var details = await provider.GetHistoryDetailsAsync(root, changeEntry.Id);
            Assert(details.Entry.Message == commitMessage, "full multiline commit message is retained");
            Assert(details.Files.Any(file => file.Status == "Renamed" && file.Path == "new name.txt" && file.OriginalPath == "old name.txt"),
                "rename details include both destination and original path");
            Assert(details.Files.Any(file => file.Status == "Added" && file.Path == unicodePath), "addition details parse a literal Unicode special-character path");
            Assert(details.Files.Any(file => file.Status == "Deleted" && file.Path == "delete.txt"), "deletion details");
            Assert(details.Files.Any(file => file.Status == "Modified" && file.Path == "binary.dat"), "binary modification file listing");

            var renameDiff = await provider.GetHistoryDiffAsync(root, changeEntry.Id, details.Files.Single(file => file.Status == "Renamed"));
            Assert(renameDiff.Contains("rename from old name.txt", StringComparison.Ordinal) &&
                   renameDiff.Contains("rename to new name.txt", StringComparison.Ordinal) &&
                   renameDiff.Contains("+committed version", StringComparison.Ordinal), "selected rename patch uses old and new paths");
            var literalDiff = await provider.GetHistoryDiffAsync(root, changeEntry.Id,
                details.Files.Single(file => file.Path == unicodePath));
            Assert(literalDiff.Contains("+added version", StringComparison.Ordinal) &&
                   literalDiff.Contains("+++ b/" + unicodePath, StringComparison.Ordinal) &&
                   !literalDiff.Contains("\\354", StringComparison.Ordinal) &&
                   !literalDiff.Contains("rename from", StringComparison.Ordinal), "literal selected path limits the patch to that file");
            var binaryDiff = await provider.GetHistoryDiffAsync(root, changeEntry.Id, details.Files.Single(file => file.Path == "binary.dat"));
            Assert(binaryDiff.Contains("Binary files", StringComparison.Ordinal), "selected binary patch is reported without text conversion");

            await File.WriteAllTextAsync(Path.Combine(root, "new name.txt"), "later dirty worktree content\n", Utf8NoBom);
            var historicalDiff = await provider.GetHistoryDiffAsync(root, changeEntry.Id,
                details.Files.Single(file => file.Path == "new name.txt"));
            Assert(historicalDiff.Contains("+committed version", StringComparison.Ordinal) &&
                   !historicalDiff.Contains("later dirty worktree content", StringComparison.Ordinal),
                "historical patch is pinned to the selected commit and is independent of dirty worktree content");
            await AssertThrowsVcsAsync(() => provider.GetHistoryDetailsAsync(root, "HEAD~1"), "revision expressions are rejected");
            await AssertThrowsVcsAsync(() => provider.GetHistoryDiffAsync(root, changeEntry.Id,
                new FileChange("../outside.txt", "Modified")), "historical path traversal is rejected");

            var shallowRunner = new ShallowBoundaryRunner();
            try
            {
                await new GitProvider(shallowRunner).GetHistoryDetailsAsync(scratch, ShallowBoundaryRunner.Commit);
                throw new InvalidOperationException("Git history test failed: a shallow boundary was mistaken for a root commit");
            }
            catch (VcsException ex)
            {
                Assert(ex.Message.Contains("부모가 로컬 저장소에 없어", StringComparison.Ordinal), "shallow history reports a missing parent");
                Assert(shallowRunner.Commands.Count == 3, "shallow history stops before trying to diff against an empty tree");
            }

            await InitializeRepositoryAsync(runner, mergeRoot);
            await File.WriteAllTextAsync(Path.Combine(mergeRoot, "base.txt"), "base\n", Utf8NoBom);
            (await RunAsync(runner, mergeRoot, "add", "--", "base.txt")).EnsureSuccess();
            await CommitAsync(provider, mergeRoot, "base");
            (await RunAsync(runner, mergeRoot, "switch", "-c", "side")).EnsureSuccess();
            await File.WriteAllTextAsync(Path.Combine(mergeRoot, "side.txt"), "side change\n", Utf8NoBom);
            (await RunAsync(runner, mergeRoot, "add", "--", "side.txt")).EnsureSuccess();
            await CommitAsync(provider, mergeRoot, "side change");
            (await RunAsync(runner, mergeRoot, "switch", "main")).EnsureSuccess();
            await File.WriteAllTextAsync(Path.Combine(mergeRoot, "main.txt"), "main change\n", Utf8NoBom);
            (await RunAsync(runner, mergeRoot, "add", "--", "main.txt")).EnsureSuccess();
            await CommitAsync(provider, mergeRoot, "main change");
            (await RunAsync(runner, mergeRoot, "merge", "--no-ff", "--no-edit", "side")).EnsureSuccess();

            var mergeEntry = (await provider.GetHistoryAsync(mergeRoot)).First();
            var mergeDetails = await provider.GetHistoryDetailsAsync(mergeRoot, mergeEntry.Id);
            Assert(mergeDetails.ComparisonLabel.Contains("첫 번째 부모 기준", StringComparison.Ordinal), "merge details state first-parent comparison");
            Assert(mergeDetails.Files.Count == 1 && mergeDetails.Files[0].Path == "side.txt", "merge file list compares against first parent");
            var mergeDiff = await provider.GetHistoryDiffAsync(mergeRoot, mergeEntry.Id);
            Assert(mergeDiff.Contains("+side change", StringComparison.Ordinal), "merge patch shows first-parent changes");
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            try
            {
                RemoveCreatedDirectory(scratch, root);
                RemoveCreatedDirectory(scratch, mergeRoot);
            }
            catch when (failure is not null)
            {
                // Preserve the primary assertion or process failure.
            }
        }
    }

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private static async Task InitializeRepositoryAsync(ProcessCommandRunner runner, string root)
    {
        Directory.CreateDirectory(root);
        (await RunAsync(runner, root, "init", "-b", "main")).EnsureSuccess();
        (await RunAsync(runner, root, "config", "user.name", "moavcs test")).EnsureSuccess();
        (await RunAsync(runner, root, "config", "user.email", "moavcs@example.invalid")).EnsureSuccess();
    }

    private static async Task CommitAsync(GitProvider provider, string root, string message) =>
        (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Commit, root, Message: message))).EnsureSuccess();

    private static Task<CommandResult> RunAsync(ProcessCommandRunner runner, string root, params string[] args) =>
        runner.RunAsync(new CommandSpec("git", args, root, Timeout: TimeSpan.FromSeconds(30)));

    private static async Task AssertThrowsVcsAsync(Func<Task> operation, string description)
    {
        try
        {
            await operation();
            throw new InvalidOperationException($"Git history test failed: {description}");
        }
        catch (VcsException) { }
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"Git history test failed: {description}");
    }

    private static void RemoveCreatedDirectory(string parent, string target)
    {
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(target);
        if (fullTarget.Equals(Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase) ||
            !fullTarget.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete a history test directory outside its scratch folder.");
        if (!Directory.Exists(fullTarget)) return;
        foreach (var file in Directory.EnumerateFiles(fullTarget, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        foreach (var directory in Directory.EnumerateDirectories(fullTarget, "*", SearchOption.AllDirectories))
            File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
        Directory.Delete(fullTarget, recursive: true);
    }

    private sealed class ShallowBoundaryRunner : ICommandRunner
    {
        public const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string Parent = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        public List<CommandSpec> Commands { get; } = [];

        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (command.Arguments.SequenceEqual(new[] { "rev-parse", "--verify", $"{Commit}^{{commit}}" }))
                return Task.FromResult(new CommandResult(0, Commit + "\n", ""));
            if (command.Arguments.SequenceEqual(new[] { "cat-file", "-p", Commit }))
                return Task.FromResult(new CommandResult(0,
                    $"tree cccccccccccccccccccccccccccccccccccccccc\nparent {Parent}\nauthor test <test@example.invalid> 0 +0000\ncommitter test <test@example.invalid> 0 +0000\n\nshallow commit\n", ""));
            if (command.Arguments.SequenceEqual(new[] { "cat-file", "-e", $"{Parent}^{{commit}}" }))
                return Task.FromResult(new CommandResult(1, "", "missing object"));
            throw new InvalidOperationException("Unexpected command in shallow boundary test: " + string.Join(' ', command.Arguments));
        }
    }
}
