using System.Text;
using MoaVcs.Core;
using MoaVcs.Core.Providers;

namespace MoaVcs.Tests;

/// <summary>Optional end-to-end checks against the official Lore CLI. Set MOAVCS_LORE_EXE to enable.</summary>
public static class LoreIntegrationTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static async Task RunAsync()
    {
        var executable = Environment.GetEnvironmentVariable("MOAVCS_LORE_EXE");
        if (string.IsNullOrWhiteSpace(executable))
        {
            Console.WriteLine("SKIP: Lore integration tests (set MOAVCS_LORE_EXE to the Lore CLI path)");
            return;
        }

        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("MOAVCS_LORE_EXE does not point to a file.", executable);

        var runner = new ProcessCommandRunner();
        var scratch = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local"));
        Directory.CreateDirectory(scratch);
        var root = Path.GetFullPath(Path.Combine(scratch, "test-lore-" + Guid.NewGuid().ToString("N")));
        var succeeded = false;
        Exception? failure = null;
        try
        {
            Directory.CreateDirectory(root);
            await RunLoreAsync(runner, executable, root,
                ["--repository", root, "--offline", "--identity", "moavcs-author-a@example.invalid", "repository", "create"]);

            var provider = new LoreProvider(runner, executable);
            const string beforeFirstCommitName = "before first commit.txt";
            await File.WriteAllTextAsync(Path.Combine(root, beforeFirstCommitName), "born before revision one\n", Utf8NoBom);
            var unbornChange = new FileChange(beforeFirstCommitName, "Added");
            var unbornComparison = await provider.GetComparisonAsync(root, unbornChange);
            Assert(unbornComparison.LeftText.Length == 0 && unbornComparison.RightText == "born before revision one\n",
                "a revision-zero repository compares a new file against an empty baseline");
            await AssertBlameThrowsVcsAsync(() => provider.GetBlameAsync(root, unbornChange),
                "uncommitted files in a revision-zero repository have no blame attribution");

            const string unusualName = "-draft [v1] 한글 note.txt";
            var unusualPath = Path.Combine(root, unusualName);
            await File.WriteAllTextAsync(unusualPath, "first line\nsecond line\n", Utf8NoBom);
            const string emptyName = "committed empty.txt";
            var emptyPath = Path.Combine(root, emptyName);
            await File.WriteAllBytesAsync(emptyPath, []);
            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Stage, root, [unusualName, emptyName]))).EnsureSuccess();
            await RunLoreAsync(runner, executable, root,
                ["--repository", root, "--offline", "--identity", "moavcs-author-a@example.invalid", "commit", "--", "initial unusual-path snapshot"]);

            await File.WriteAllTextAsync(emptyPath, "working text after empty commit\n", Utf8NoBom);
            var emptyComparison = await provider.GetComparisonAsync(root, new FileChange(emptyName, "Modified"));
            Assert(emptyComparison.LeftText.Length == 0 && emptyComparison.RightText == "working text after empty commit\n",
                "an empty committed file compares cleanly with later working content");
            var emptyBlame = await provider.GetBlameAsync(root, new FileChange(emptyName, "Modified"));
            Assert(emptyBlame.Lines.Count == 0,
                "a committed empty file has zero blame lines");

            await File.AppendAllTextAsync(unusualPath, "third line\n", Utf8NoBom);
            // Staged edits are deliberately included on the working side: Lore compares against its current
            // committed revision and has no Git-style index snapshot.
            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Stage, root, [unusualName]))).EnsureSuccess();
            var selected = new FileChange(unusualName, "Modified", IndexStatus: "M");
            var comparison = await provider.GetComparisonAsync(root, selected);
            Assert(comparison.Path == unusualName && comparison.LeftText == "first line\nsecond line\n" &&
                   comparison.RightText == "first line\nsecond line\nthird line\n",
                "Lore comparison is pinned to the current committed revision and includes staged edits in the working snapshot");

            var blame = await provider.GetBlameAsync(root, selected);
            Assert(blame.Path == unusualName && blame.Lines.Count == 2 &&
                   blame.Lines.Select(line => line.Text).SequenceEqual(["first line", "second line"]) &&
                   blame.Lines.All(line => line.Revision.Length > 0 &&
                                           line.Author == "moavcs-author-a@example.invalid" && line.Date.Length > 0),
                "Lore blame includes only committed lines with revision, author, and date metadata");

            const string renamedName = "renamed -draft [v1] 한글 note.txt";
            var renamedPath = Path.Combine(root, renamedName);
            File.Move(unusualPath, renamedPath);
            await RunLoreAsync(runner, executable, root,
                ["--repository", root, "stage", "move", "--", unusualName, renamedName]);
            var rename = (await provider.GetStatusAsync(root)).Single(change => change.Path == renamedName);
            Assert(rename.IndexStatus == "V" && rename.OriginalPath == unusualName,
                "Lore status preserves the original path for a staged move");
            var renameComparison = await provider.GetComparisonAsync(root, rename);
            Assert(renameComparison.LeftText == "first line\nsecond line\n" &&
                   renameComparison.RightText == "first line\nsecond line\nthird line\n",
                "a staged Lore move compares the original committed path with its renamed working file");
            var renameBlame = await provider.GetBlameAsync(root, rename);
            Assert(renameBlame.Lines.Count == 2 && renameBlame.Lines.All(line =>
                       line.Author == "moavcs-author-a@example.invalid" && line.Revision.Length > 0),
                "Lore blame follows a staged move back to the original committed path");

            const string addedName = "added file.txt";
            var addedPath = Path.Combine(root, addedName);
            await File.WriteAllTextAsync(addedPath, "not committed yet\n", Utf8NoBom);
            var added = await provider.GetComparisonAsync(root, new FileChange(addedName, "Added"));
            Assert(added.LeftText.Length == 0 && added.RightText == "not committed yet\n",
                "a new working file compares against an absent committed snapshot");

            const string deletedName = "deleted file.txt";
            var deletedPath = Path.Combine(root, deletedName);
            await File.WriteAllTextAsync(deletedPath, "committed then deleted\n", Utf8NoBom);
            (await provider.ExecuteAsync(new VcsRequest(VcsOperation.Stage, root, [deletedName]))).EnsureSuccess();
            await RunLoreAsync(runner, executable, root,
                ["--repository", root, "--offline", "--identity", "moavcs-author-b@example.invalid", "commit", "--", "add file to delete"]);
            File.Delete(deletedPath);
            var renamedBlameAfterCommit = await provider.GetBlameAsync(root, new FileChange(renamedName, "Modified"));
            Assert(renamedBlameAfterCommit.Lines.Count == 3 &&
                   renamedBlameAfterCommit.Lines.Select(line => line.LineNumber).SequenceEqual([1, 2, 3]) &&
                   renamedBlameAfterCommit.Lines.Select(line => line.Author).SequenceEqual([
                       "moavcs-author-a@example.invalid",
                       "moavcs-author-a@example.invalid",
                       "moavcs-author-b@example.invalid"]),
                "Lore blame follows a rename and attributes a later line addition to its second author");
            var deleted = await provider.GetComparisonAsync(root, new FileChange(deletedName, "Deleted"));
            Assert(deleted.LeftText == "committed then deleted\n" && deleted.RightText.Length == 0,
                "a deleted working file compares against its committed content");
            var secondAuthorBlame = await provider.GetBlameAsync(root, new FileChange(deletedName, "Deleted"));
            Assert(secondAuthorBlame.Lines.Count == 1 &&
                   secondAuthorBlame.Lines[0].Author == "moavcs-author-b@example.invalid" &&
                   secondAuthorBlame.Lines[0].Revision.Length > 0,
                "Lore blame resolves attribution across two commit identities");

            await File.WriteAllBytesAsync(Path.Combine(root, "binary.dat"), [0x41, 0x00, 0x42]);
            await AssertThrowsVcsAsync(() => provider.GetComparisonAsync(root, new FileChange("binary.dat", "Added")),
                "binary files are rejected from Lore text comparison");

            const string largeName = "large.txt";
            await File.WriteAllTextAsync(Path.Combine(root, largeName),
                new string('x', checked((int)TextSnapshot.MaxBytes + 1)), Utf8NoBom);
            await AssertThrowsVcsAsync(() => provider.GetComparisonAsync(root, new FileChange(largeName, "Added")),
                "oversized files are rejected from Lore text comparison");
            succeeded = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            try
            {
                DeleteTestDirectory(root, scratch);
            }
            catch when (failure is not null)
            {
                // Preserve the assertion/CLI failure; a cleanup issue must not hide the actual test failure.
            }
        }

        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (succeeded) Console.WriteLine("PASS: Lore CLI snapshot and blame integration");
    }

    private static async Task<CommandResult> RunLoreAsync(ICommandRunner runner, string executable, string root,
        IReadOnlyList<string> arguments)
    {
        var result = await runner.RunAsync(new CommandSpec(executable, arguments, root));
        if (!result.Success)
            throw new InvalidOperationException($"Lore CLI command failed ({result.ExitCode}): {result.Output}");
        return result;
    }

    private static async Task AssertThrowsVcsAsync(Func<Task<FileComparison>> action, string label)
    {
        try { await action(); }
        catch (VcsException) { return; }
        throw new InvalidOperationException("Expected VcsException: " + label);
    }

    private static async Task AssertBlameThrowsVcsAsync(Func<Task<BlameResult>> action, string label)
    {
        try { await action(); }
        catch (VcsException) { return; }
        throw new InvalidOperationException("Expected VcsException: " + label);
    }

    private static void DeleteTestDirectory(string target, string scratch)
    {
        target = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        scratch = Path.GetFullPath(scratch).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!target.StartsWith(scratch + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(target).StartsWith("test-lore-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a Lore integration test directory outside .local/test-lore-*.");
        if (!Directory.Exists(target)) return;

        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(target, recursive: true);
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Lore integration assertion failed: " + label);
    }
}
