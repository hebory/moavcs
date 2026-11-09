using System.Text;
using System.Text.Json;
using MoaVcs.Core;
using MoaVcs.Core.Providers;

namespace MoaVcs.Tests;

internal static class LoreComparisonTests
{
    private const string Signature = "1111111111111111111111111111111111111111111111111111111111111111";
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "moavcs-lore-comparison-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string path = "-한글 [draft].txt";
            await File.WriteAllTextAsync(Path.Combine(root, path), "new\n", new UTF8Encoding(false));
            var runner = new SnapshotRunner(path, "old\n");
            var provider = new LoreProvider(runner);
            var result = await provider.GetComparisonAsync(root, new FileChange(path, "M"));
            Assert(result.LeftText == "old\n" && result.RightText == "new\n" && result.LeftLabel.Contains("r7"), "revision vs worktree content");
            Assert(runner.Commands.All(c => c.Arguments.Contains("--json") && c.Arguments.Contains("--no-pager")), "structured nonpaged commands");
            Assert(runner.Commands.Skip(1).All(c => c.Arguments.Contains(Signature)), "all reads pin the same full revision");
            Assert(runner.Commands[1].Arguments.Last() == "./" + path, "option-like Unicode path stays a single literal argument");
            Assert(!File.Exists(runner.ExportedPath) && !Directory.Exists(Path.GetDirectoryName(runner.ExportedPath)), "snapshot cleanup after reading");
            Assert(File.ReadAllText(Path.Combine(root, path)) == "new\n", "working file remains unchanged");

            var added = await new LoreProvider(new SnapshotRunner(path, null)).GetComparisonAsync(root, new FileChange(path, "A"));
            Assert(added.LeftText == "" && added.RightText == "new\n", "untracked file has an empty baseline");
            var workingOnlyRunner = new SnapshotRunner(path, "", workingOnly: true);
            var workingOnly = await new LoreProvider(workingOnlyRunner).GetComparisonAsync(root, new FileChange(path, "A"));
            Assert(workingOnly.LeftText == "" && workingOnly.RightText == "new\n" && workingOnlyRunner.Commands.Count == 2,
                "file info flagAdded is an absent committed snapshot, not an empty tracked file to export");
            var unbornRunner = new SnapshotRunner(path, null, unborn: true);
            var unborn = await new LoreProvider(unbornRunner).GetComparisonAsync(root, new FileChange(path, "A"));
            Assert(unborn.LeftText == "" && unborn.RightText == "new\n" && unbornRunner.Commands.Count == 1,
                "unborn revision has no snapshot and does not query an invalid zero signature");
            File.Delete(Path.Combine(root, path));
            var deleted = await new LoreProvider(new SnapshotRunner(path, "old\n")).GetComparisonAsync(root, new FileChange(path, "D"));
            Assert(deleted.RightText == "" && deleted.LeftText == "old\n", "deleted file keeps committed contents");
            await File.WriteAllTextAsync(Path.Combine(root, "renamed.txt"), "renamed\n");
            var renameRunner = new SnapshotRunner("old name.txt", "old\n");
            var renamed = await new LoreProvider(renameRunner).GetComparisonAsync(root, new FileChange("renamed.txt", "V", "V", OriginalPath: "old name.txt"));
            Assert(renamed.LeftText == "old\n" && renamed.RightText == "renamed\n" && renameRunner.Commands[1].Arguments.Last() == "./old name.txt", "rename reads committed original path");

            var oversized = new SnapshotRunner(path, new string('x', TextSnapshot.MaxBytes + 1));
            await Reject(() => new LoreProvider(oversized).GetComparisonAsync(root, new FileChange(path, "M")), "oversize baseline");
            Assert(oversized.Commands.Count == 2, "oversize rejected before exporting");
            var binary = new SnapshotRunner(path, "x\0y");
            await Reject(() => new LoreProvider(binary).GetComparisonAsync(root, new FileChange(path, "M")), "binary baseline");
            Assert(!File.Exists(binary.ExportedPath), "binary temporary file removed");
            foreach (var output in new[] { "not-json", "", Complete + "\n" + Complete, "{\"tagName\":\"complete\",\"data\":{\"status\":1}}", "{\"tagName\":\"complete\",\"data\":{\"status\":\"0\"}}" })
                await Reject(() => new LoreProvider(new RawRunner(output)).GetComparisonAsync(root, new FileChange(path, "M")), "malformed or unsuccessful JSON");
            var escaped = new SnapshotRunner(path, "");
            await Reject(() => new LoreProvider(escaped).GetComparisonAsync(root, new FileChange("../outside.txt", "M")), "outside repository");
            Assert(escaped.Commands.Count == 0, "invalid path rejected before CLI");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { await provider.GetComparisonAsync(root, new FileChange(path, "M"), cancellationToken: cancel.Token); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException) { }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Complete => Event("complete", new { status = 0 });
    private static string Event(string tag, object data) => JsonSerializer.Serialize(new { tagName = tag, data });
    private sealed class SnapshotRunner(string path, string? baseline, bool unborn = false, bool workingOnly = false) : ICommandRunner
    {
        public List<CommandSpec> Commands { get; } = [];
        public string ExportedPath { get; private set; } = "";
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Commands.Add(command);
            string output;
            if (command.Arguments.Contains("--revision-only"))
                output = Event("repositoryStatusRevision", new { repository = "01a1c13279f1761c83e85c5af1e541de", revision = unborn ? new string('0', 64) : Signature, revisionNumber = unborn ? 0 : 7 }) + "\n" + Complete;
            else if (command.Arguments.Contains("info"))
                output = (baseline is null ? "" : Event("fileInfo", new { path, isFile = true, isDir = false, flagAdded = workingOnly, size = Encoding.UTF8.GetByteCount(baseline) }) + "\n") + Complete;
            else if (command.Arguments.Contains("write"))
            {
                ExportedPath = command.Arguments[command.Arguments.ToList().IndexOf("--output") + 1];
                File.WriteAllText(ExportedPath, baseline!, new UTF8Encoding(false));
                output = Complete;
            }
            else throw new InvalidOperationException("Unexpected inspection command.");
            return Task.FromResult(new CommandResult(0, output, ""));
        }
    }
    private sealed class RawRunner(string output) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default) => Task.FromResult(new CommandResult(0, output, ""));
    }
    private static async Task Reject(Func<Task<FileComparison>> action, string label)
    {
        try { await action(); } catch (VcsException) { return; }
        throw new InvalidOperationException("Lore comparison did not reject: " + label);
    }
    private static void Assert(bool value, string label) { if (!value) throw new InvalidOperationException("Lore comparison: " + label); }
}
