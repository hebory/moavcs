using System.Text;
using MoaVcs.Core;
using MoaVcs.Core.Providers;

namespace MoaVcs.Tests;

public static class GitConflictTests
{
    public static async Task RunAsync()
    {
        var runner = new ProcessCommandRunner();
        var provider = new GitProvider(runner);
        var scratch = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local"));
        Directory.CreateDirectory(scratch);
        var root = Path.Combine(scratch, "test-git-conflict-" + Guid.NewGuid().ToString("N"));
        Exception? failure = null;
        try
        {
            Directory.CreateDirectory(root);
            (await Run(root, "init", "-b", "main")).EnsureSuccess();
            (await Run(root, "config", "user.name", "moavcs conflict test")).EnsureSuccess();
            (await Run(root, "config", "user.email", "conflict-test@example.invalid")).EnsureSuccess();

            const string path = "한글 conflict.txt";
            const string otherPath = "second conflict.txt";
            await Write(root, path, "base version\nstable line\n");
            await Write(root, otherPath, "base second\nstable line\n");
            (await Run(root, "add", "--", path, otherPath)).EnsureSuccess();
            (await Run(root, "commit", "-m", "base")).EnsureSuccess();
            (await Run(root, "branch", "incoming")).EnsureSuccess();

            await Write(root, path, "current version\nstable line\n");
            await Write(root, otherPath, "current second\nstable line\n");
            (await Run(root, "commit", "-am", "current side")).EnsureSuccess();
            (await Run(root, "checkout", "incoming")).EnsureSuccess();
            await Write(root, path, "incoming version\nstable line\n");
            await Write(root, otherPath, "incoming second\nstable line\n");
            (await Run(root, "commit", "-am", "incoming side")).EnsureSuccess();
            (await Run(root, "checkout", "main")).EnsureSuccess();
            var merge = await Run(root, "merge", "incoming");
            Assert(merge.ExitCode != 0, "overlapping edits create a real merge conflict");

            var status = await provider.GetStatusAsync(root);
            Assert(status.Count == 2 && status.All(change => change.IsConflicted && !change.IsStaged),
                "both unmerged paths are conflicts and are not presented as staged");
            var conflicted = status.Single(change => change.Path == path);
            var snapshot = await provider.GetConflictAsync(root, conflicted);
            Assert(snapshot.BaseText == "base version\nstable line\n" &&
                   snapshot.LeftText == "current version\nstable line\n" &&
                   snapshot.RightText == "incoming version\nstable line\n",
                "base, stage 2 and stage 3 snapshots are associated with the right sides");
            Assert(snapshot.WorkingText.Contains("<<<<<<<", StringComparison.Ordinal) &&
                   snapshot.LeftLabel.Contains("stage 2", StringComparison.Ordinal) &&
                   snapshot.RightLabel.Contains("stage 3", StringComparison.Ordinal),
                "working conflict markers and explicit Git stage labels are returned");
            Assert((provider.Capabilities & VcsCapabilities.ConflictResolution) != 0,
                "Git advertises conflict resolution support");

            await AssertThrowsVcsAsync(() => provider.MarkConflictResolvedAsync(snapshot),
                "remaining merge markers prevent marking a conflict resolved");
            var conflictedComparison = await provider.GetComparisonAsync(root, conflicted);
            Assert(conflictedComparison.LeftText == snapshot.LeftText && conflictedComparison.RightText == snapshot.RightText &&
                   conflictedComparison.LeftLabel == snapshot.LeftLabel && conflictedComparison.RightLabel == snapshot.RightLabel,
                "the ordinary comparison API exposes the unmerged sides to external diff tools");

            // Resolve a different path outside MoaVCS. The repository-level token pins all unmerged
            // entries, so this stale conflict snapshot cannot overwrite the first path.
            (await Run(root, "add", "--", otherPath)).EnsureSuccess();
            await AssertThrowsVcsAsync(() => provider.SaveConflictAsync(snapshot, "must not overwrite\n"),
                "an unrelated index conflict change invalidates the saved snapshot");
            var diskAfterStaleSave = await File.ReadAllTextAsync(Path.Combine(root, path));
            Assert(diskAfterStaleSave == snapshot.WorkingText, "stale save leaves the working file unchanged");

            status = await provider.GetStatusAsync(root);
            var currentSnapshot = await provider.GetConflictAsync(root, status.Single(change => change.Path == path));
            var resolvedText = ConflictFileStore.NormalizeNewLines("merged version\nstable line\n", currentSnapshot.NewLine);
            var edited = await provider.SaveConflictAsync(currentSnapshot, resolvedText);
            var savedText = await File.ReadAllTextAsync(Path.Combine(root, path));
            Assert(edited.WorkingText == resolvedText && savedText == edited.WorkingText,
                "saving a merge result replaces only the selected working file (returned=" +
                Convert.ToHexString(Encoding.UTF8.GetBytes(edited.WorkingText)) + ", disk=" +
                Convert.ToHexString(Encoding.UTF8.GetBytes(savedText)) + ")");

            await Write(root, path, "changed outside editor\n");
            await AssertThrowsVcsAsync(() => provider.MarkConflictResolvedAsync(edited),
                "a working file changed after review cannot be marked resolved");
            status = await provider.GetStatusAsync(root);
            Assert(status.Single(change => change.Path == path).IsConflicted,
                "stale resolve leaves the selected path unmerged");

            var refreshed = await provider.GetConflictAsync(root, status.Single(change => change.Path == path));
            var final = await provider.SaveConflictAsync(refreshed, "merged version\nstable line\n");
            await provider.MarkConflictResolvedAsync(final);
            status = await provider.GetStatusAsync(root);
            Assert(!status.Single(change => change.Path == path).IsConflicted &&
                   status.Single(change => change.Path == path).IsStaged,
                "marking resolved stages the edited result and removes only its unmerged entries");
            Assert(status.Single(change => change.Path == otherPath).IsStaged,
                "an already resolved sibling remains staged");
            await AssertThrowsVcsAsync(() => provider.GetConflictAsync(root, status.Single(change => change.Path == path)),
                "a stage-zero path cannot be reopened as an unmerged conflict");

            await RunAdditionalConflictKindsAsync(scratch);
            await RunConflictMarkerSizeAsync(scratch);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            try { RemoveCreatedDirectory(scratch, root); }
            catch when (failure is not null) { }
        }
    }

    public static async Task RunAdditionalConflictKindsAsync(string? scratchPath = null)
    {
        var runner = new ProcessCommandRunner();
        var provider = new GitProvider(runner);
        var scratch = Path.GetFullPath(scratchPath ?? Path.Combine(Environment.CurrentDirectory, ".local"));
        Directory.CreateDirectory(scratch);
        var addAddRoot = Path.Combine(scratch, "test-git-conflict-add-add-" + Guid.NewGuid().ToString("N"));
        var modifyDeleteRoot = Path.Combine(scratch, "test-git-conflict-modify-delete-" + Guid.NewGuid().ToString("N"));
        var binaryRoot = Path.Combine(scratch, "test-git-conflict-binary-" + Guid.NewGuid().ToString("N"));
        Exception? failure = null;
        try
        {
            await InitializeWithBaseAsync(addAddRoot, new Dictionary<string, string> { ["README.md"] = "base\n" });
            const string addedPath = "both sides.txt";
            await Write(addAddRoot, addedPath, "current side\n");
            (await Run(addAddRoot, "add", "--", addedPath)).EnsureSuccess();
            (await Run(addAddRoot, "commit", "-m", "add on current side")).EnsureSuccess();
            (await Run(addAddRoot, "checkout", "incoming")).EnsureSuccess();
            await Write(addAddRoot, addedPath, "incoming side\n");
            (await Run(addAddRoot, "add", "--", addedPath)).EnsureSuccess();
            (await Run(addAddRoot, "commit", "-m", "add on incoming side")).EnsureSuccess();
            (await Run(addAddRoot, "checkout", "main")).EnsureSuccess();
            Assert((await Run(addAddRoot, "merge", "incoming")).ExitCode != 0,
                "adding a different same-path file on both branches creates add/add conflict");
            var addAddChange = (await provider.GetStatusAsync(addAddRoot)).Single(change => change.Path == addedPath);
            Assert(addAddChange.IsConflicted, "add/add status is reported as a conflict");
            var addAdd = await provider.GetConflictAsync(addAddRoot, addAddChange);
            Assert(addAdd.BaseText is null && addAdd.LeftText == "current side\n" && addAdd.RightText == "incoming side\n",
                "add/add conflicts expose both regular-file sides without inventing a base");
            var merged = await provider.SaveConflictAsync(addAdd, "combined add/add result\n");
            await provider.MarkConflictResolvedAsync(merged);
            var resolvedAddAdd = (await provider.GetStatusAsync(addAddRoot)).Single(change => change.Path == addedPath);
            Assert(!resolvedAddAdd.IsConflicted && resolvedAddAdd.IsStaged,
                "an edited add/add result can be staged as resolved");

            await InitializeWithBaseAsync(modifyDeleteRoot,
                new Dictionary<string, string> { ["README.md"] = "base\n", ["remove here.txt"] = "base file\n" });
            const string deletedPath = "remove here.txt";
            await Write(modifyDeleteRoot, deletedPath, "modified on current side\n");
            (await Run(modifyDeleteRoot, "commit", "-am", "modify on current side")).EnsureSuccess();
            (await Run(modifyDeleteRoot, "checkout", "incoming")).EnsureSuccess();
            (await Run(modifyDeleteRoot, "rm", "--", deletedPath)).EnsureSuccess();
            (await Run(modifyDeleteRoot, "commit", "-m", "delete on incoming side")).EnsureSuccess();
            (await Run(modifyDeleteRoot, "checkout", "main")).EnsureSuccess();
            Assert((await Run(modifyDeleteRoot, "merge", "incoming")).ExitCode != 0,
                "modifying a file against its deletion creates modify/delete conflict");
            var modifyDelete = (await provider.GetStatusAsync(modifyDeleteRoot)).Single(change => change.Path == deletedPath);
            Assert(modifyDelete.IsConflicted, "modify/delete status is reported as a conflict");
            await AssertThrowsVcsMessageAsync(() => provider.GetConflictAsync(modifyDeleteRoot, modifyDelete), "삭제",
                "modify/delete conflict explains that its missing side is unsupported");

            await InitializeWithBaseAsync(binaryRoot, new Dictionary<string, string> { ["README.md"] = "base\n" });
            const string binaryPath = "merge.bin";
            await File.WriteAllBytesAsync(Path.Combine(binaryRoot, binaryPath), [0, 0x41, 0x42]);
            (await Run(binaryRoot, "add", "--", binaryPath)).EnsureSuccess();
            (await Run(binaryRoot, "commit", "-m", "add binary base")).EnsureSuccess();
            (await Run(binaryRoot, "branch", "-D", "incoming")).EnsureSuccess();
            (await Run(binaryRoot, "branch", "incoming")).EnsureSuccess();
            await File.WriteAllBytesAsync(Path.Combine(binaryRoot, binaryPath), [0, 0x41, 0x4d]);
            (await Run(binaryRoot, "commit", "-am", "modify binary current side")).EnsureSuccess();
            (await Run(binaryRoot, "checkout", "incoming")).EnsureSuccess();
            await File.WriteAllBytesAsync(Path.Combine(binaryRoot, binaryPath), [0, 0x41, 0x54]);
            (await Run(binaryRoot, "commit", "-am", "modify binary incoming side")).EnsureSuccess();
            (await Run(binaryRoot, "checkout", "main")).EnsureSuccess();
            Assert((await Run(binaryRoot, "merge", "incoming")).ExitCode != 0,
                "overlapping binary edits create a merge conflict");
            var binaryChange = (await provider.GetStatusAsync(binaryRoot)).Single(change => change.Path == binaryPath);
            Assert(binaryChange.IsConflicted, "binary path is reported as a conflict");
            await AssertThrowsVcsMessageAsync(() => provider.GetConflictAsync(binaryRoot, binaryChange), "바이너리",
                "binary conflict is refused with a text-editing limitation");
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
                RemoveCreatedDirectory(scratch, addAddRoot);
                RemoveCreatedDirectory(scratch, modifyDeleteRoot);
                RemoveCreatedDirectory(scratch, binaryRoot);
            }
            catch when (failure is not null) { }
        }
    }

    public static async Task RunConflictMarkerSizeAsync(string? scratchPath = null)
    {
        var provider = new GitProvider(new ProcessCommandRunner());
        var scratch = Path.GetFullPath(scratchPath ?? Path.Combine(Environment.CurrentDirectory, ".local"));
        Directory.CreateDirectory(scratch);
        var root = Path.Combine(scratch, "test-git-conflict-marker-size-" + Guid.NewGuid().ToString("N"));
        Exception? failure = null;
        try
        {
            Directory.CreateDirectory(root);
            (await Run(root, "init", "-b", "main")).EnsureSuccess();
            (await Run(root, "config", "user.name", "moavcs conflict test")).EnsureSuccess();
            (await Run(root, "config", "user.email", "conflict-test@example.invalid")).EnsureSuccess();
            await Write(root, ".gitattributes", "short.txt conflict-marker-size=3\ncustom.txt conflict-marker-size=9\n");
            await Write(root, "README.md", "base\n");
            await Write(root, "short.txt", "short base\n");
            await Write(root, "custom.txt", "custom base\n");
            (await Run(root, "add", "--", ".gitattributes", "README.md", "short.txt", "custom.txt")).EnsureSuccess();
            (await Run(root, "commit", "-m", "base")).EnsureSuccess();
            (await Run(root, "branch", "incoming")).EnsureSuccess();

            await Write(root, "short.txt", "short current\n");
            await Write(root, "custom.txt", "custom current\n");
            (await Run(root, "commit", "-am", "current edits")).EnsureSuccess();
            (await Run(root, "checkout", "incoming")).EnsureSuccess();
            await Write(root, "short.txt", "short incoming\n");
            await Write(root, "custom.txt", "custom incoming\n");
            (await Run(root, "commit", "-am", "incoming edits")).EnsureSuccess();
            (await Run(root, "checkout", "main")).EnsureSuccess();
            Assert((await Run(root, "merge", "incoming")).ExitCode != 0,
                "both custom marker-size paths enter a real merge conflict");

            var changes = await provider.GetStatusAsync(root);
            var shortChange = changes.Single(change => change.Path == "short.txt");
            Assert(shortChange.IsConflicted, "short-marker path is surfaced as conflicted");
            await AssertThrowsVcsMessageAsync(() => provider.GetConflictAsync(root, shortChange), "7자보다 짧아",
                "marker sizes below the parser's seven-character minimum are rejected");

            var customChange = changes.Single(change => change.Path == "custom.txt");
            var customSnapshot = await provider.GetConflictAsync(root, customChange);
            Assert(customSnapshot.WorkingText.Contains("<<<<<<<<<", StringComparison.Ordinal),
                "a supported custom marker size is retained in the working snapshot");
            await Write(root, ".gitattributes", "short.txt conflict-marker-size=3\ncustom.txt conflict-marker-size=10\n");
            await AssertThrowsVcsMessageAsync(() => provider.SaveConflictAsync(customSnapshot, "edited after attribute change\n"),
                "충돌 상태가 비교 화면을 연 뒤 바뀌었습니다",
                "custom conflict-marker-size is part of the snapshot token");
            Assert((await File.ReadAllTextAsync(Path.Combine(root, "custom.txt"))) == customSnapshot.WorkingText,
                "stale marker-size state does not overwrite the working file");
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            try { RemoveCreatedDirectory(scratch, root); }
            catch when (failure is not null) { }
        }
    }

    private static async Task InitializeWithBaseAsync(string root, IReadOnlyDictionary<string, string> files)
    {
        Directory.CreateDirectory(root);
        (await Run(root, "init", "-b", "main")).EnsureSuccess();
        (await Run(root, "config", "user.name", "moavcs conflict test")).EnsureSuccess();
        (await Run(root, "config", "user.email", "conflict-test@example.invalid")).EnsureSuccess();
        foreach (var (path, text) in files) await Write(root, path, text);
        var addArguments = new List<string> { "add", "--" };
        addArguments.AddRange(files.Keys);
        (await Run(root, addArguments.ToArray())).EnsureSuccess();
        (await Run(root, "commit", "-m", "base")).EnsureSuccess();
        (await Run(root, "branch", "incoming")).EnsureSuccess();
    }

    private static Task<CommandResult> Run(string root, params string[] args) =>
        new ProcessCommandRunner().RunAsync(new CommandSpec("git", args, root, Timeout: TimeSpan.FromSeconds(30)));

    private static Task Write(string root, string path, string text) =>
        File.WriteAllTextAsync(Path.Combine(root, path), text, new UTF8Encoding(false));

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Git conflict test failed: " + description);
    }

    private static async Task AssertThrowsVcsAsync(Func<Task> operation, string description)
    {
        try { await operation(); }
        catch (VcsException) { return; }
        throw new InvalidOperationException("Git conflict test failed: " + description);
    }

    private static async Task AssertThrowsVcsMessageAsync(Func<Task> operation, string messagePart, string description)
    {
        try { await operation(); }
        catch (VcsException ex) when (ex.Message.Contains(messagePart, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("Git conflict test failed: " + description);
    }

    private static void RemoveCreatedDirectory(string parent, string path)
    {
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to remove test data outside .local.");
        if (!Directory.Exists(full)) return;
        // Windows marks Git loose object files read-only.
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        foreach (var directory in Directory.EnumerateDirectories(full, "*", SearchOption.AllDirectories))
            File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
        Directory.Delete(full, recursive: true);
    }
}
