using System.Text;
using MoaVcs.Core;
using MoaVcs.Core.Providers;

namespace MoaVcs.Tests;

internal static class HistoryLoreTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static async Task RunAsync()
    {
        var executable = Environment.GetEnvironmentVariable("MOAVCS_LORE_EXE");
        if (string.IsNullOrWhiteSpace(executable))
        {
            Console.WriteLine("SKIP: Lore revision detail integration (MOAVCS_LORE_EXE is not set)");
            return;
        }
        if (!File.Exists(executable)) throw new FileNotFoundException("MOAVCS_LORE_EXE does not point to a Lore executable.", executable);

        var root = Path.Combine(Path.GetTempPath(), "moavcs-history-lore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Exception? failure = null;
        try
        {
            var runner = new ProcessCommandRunner();
            const string identity = "moavcs-history@example.invalid";
            await RunLoreAsync(runner, executable, root,
                ["--repository", root, "--offline", "--identity", identity, "repository", "create"]);
            var provider = new LoreProvider(runner, executable);

            const string oldName = "-기존 [v1].txt";
            await File.WriteAllTextAsync(Path.Combine(root, oldName), "처음 줄\n", Utf8NoBom);
            await RunLoreAsync(runner, executable, root, ["--repository", root, "stage", "--scan", "--", "."]);
            var firstMessage = "첫 커밋 제목\n\n상세 설명 첫 줄\n상세 설명 둘째 줄";
            await RunLoreAsync(runner, executable, root,
                ["--repository", root, "--offline", "--identity", identity, "commit", "--", firstMessage]);

            var first = (await provider.GetHistoryAsync(root)).First();
            Assert(first.Message == firstMessage && first.Author == identity && first.Date.Contains("UTC", StringComparison.Ordinal),
                "the history list includes full message, author, and commit time");
            var rootDetails = await provider.GetHistoryDetailsAsync(root, first.Id);
            Assert(rootDetails.Entry.Message == firstMessage && rootDetails.Files.Count == 1 &&
                   rootDetails.Files[0].Status == "Added" && rootDetails.ComparisonLabel.Contains("최초", StringComparison.Ordinal),
                "the first Lore revision shows its complete metadata, added file, and empty-tree base");
            var rootDiff = await provider.GetHistoryDiffAsync(root, first.Id, rootDetails.Files[0]);
            Assert(rootDiff.Contains("+처음 줄", StringComparison.Ordinal) && rootDiff.Contains(oldName, StringComparison.Ordinal),
                "the first Lore revision can show an added-file patch");

            await File.WriteAllTextAsync(Path.Combine(root, oldName), "처음 줄\n두 번째 줄\n", Utf8NoBom);
            const string addedName = "새 파일 한글.txt";
            await File.WriteAllTextAsync(Path.Combine(root, addedName), "추가 내용\n", Utf8NoBom);
            await RunLoreAsync(runner, executable, root, ["--repository", root, "stage", "--scan", "--", "."]);
            var secondMessage = "둘째 커밋\n\n파일 두 개 변경";
            await RunLoreAsync(runner, executable, root,
                ["--repository", root, "--offline", "--identity", identity, "commit", "--", secondMessage]);

            var entries = await provider.GetHistoryAsync(root);
            var second = entries.First();
            Assert(second.Message == secondMessage, "the newest history row preserves multiline message text");
            var details = await provider.GetHistoryDetailsAsync(root, second.Id);
            Assert(details.Files.Count == 2 && details.Files.Any(change => change.Path == oldName && change.Status == "Modified") &&
                   details.Files.Any(change => change.Path == addedName && change.Status == "Added"),
                "a Lore revision lists modified and added paths");
            var file = details.Files.Single(change => change.Path == oldName);
            await File.WriteAllTextAsync(Path.Combine(root, oldName), "later uncommitted text\n", Utf8NoBom);
            var fileDiff = await provider.GetHistoryDiffAsync(root, second.Id, file);
            Assert(fileDiff.Contains("두 번째 줄", StringComparison.Ordinal) &&
                   !fileDiff.Contains("later uncommitted text", StringComparison.Ordinal),
                "a selected-file patch compares the selected revision to its parent, independent of worktree edits");
            var fullDiff = await provider.GetHistoryDiffAsync(root, second.Id);
            Assert(fullDiff.Contains(oldName, StringComparison.Ordinal) && fullDiff.Contains(addedName, StringComparison.Ordinal),
                "a Lore revision can show a patch for all of its files");
            await Reject(() => provider.GetHistoryDiffAsync(root, second.Id, new FileChange("../outside.txt", "Modified")),
                "a selected path outside the repository is rejected");

            File.Move(Path.Combine(root, oldName), Path.Combine(root, "renamed 파일.txt"));
            File.Delete(Path.Combine(root, addedName));
            await RunLoreAsync(runner, executable, root, ["--repository", root, "stage", "move", "--", oldName, "renamed 파일.txt"]);
            await RunLoreAsync(runner, executable, root, ["--repository", root, "stage", "--scan", "--", "."]);
            await RunLoreAsync(runner, executable, root,
                ["--repository", root, "--offline", "--identity", identity, "commit", "--", "이동과 삭제"]);
            var newest = (await provider.GetHistoryAsync(root)).First();
            var latest = await provider.GetHistoryDetailsAsync(root, newest.Id);
            Assert(latest.Files.Any(change => change.Status == "Moved" && change.OriginalPath == oldName && change.Path == "renamed 파일.txt") &&
                   latest.Files.Any(change => change.Status == "Deleted" && change.Path == addedName),
                "Lore history reports original and destination paths for moves, and deleted files");
            var renameDiff = await provider.GetHistoryDiffAsync(root, newest.Id, latest.Files.Single(change => change.Status == "Moved"));
            Assert(renameDiff.Contains(oldName, StringComparison.Ordinal) && renameDiff.Contains("renamed 파일.txt", StringComparison.Ordinal),
                "the move patch includes both source and destination paths");
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try { DeleteTestDirectory(root); }
            catch when (failure is not null) { }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine("PASS: Lore revision details, file patches, rename, delete, and worktree independence");
    }

    private static async Task<CommandResult> RunLoreAsync(ICommandRunner runner, string executable, string root,
        IReadOnlyList<string> args)
    {
        var result = await runner.RunAsync(new CommandSpec(executable, args, root));
        if (!result.Success) throw new InvalidOperationException($"Lore history CLI failed ({result.ExitCode}): {result.Output}");
        return result;
    }

    private static async Task Reject(Func<Task<string>> action, string label)
    {
        try { await action(); }
        catch (VcsException) { return; }
        throw new InvalidOperationException("Expected VcsException: " + label);
    }

    private static void DeleteTestDirectory(string root)
    {
        var scratch = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!target.StartsWith(scratch + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(target).StartsWith("moavcs-history-lore-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a Lore history test directory outside the test-owned temp folder.");
        if (!Directory.Exists(target)) return;
        foreach (var path in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        Directory.Delete(target, recursive: true);
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Lore history integration assertion failed: " + label);
    }
}
