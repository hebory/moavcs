using System.Text.Json;
using MoaVcs.Core;
using MoaVcs.Core.Providers;

namespace MoaVcs.Tests;

internal static class HistoryProviderTests
{
    public static async Task RunAsync()
    {
        await SvnCommittedPathsAndDeletedPeg();
        await PerforceSubmittedSnapshots();
        await MalformedDetailsAreRejected();
    }

    private const string SvnInfo = "<info><entry><repository><root>https://example.test/repo</root></repository></entry></info>";
    private const string SvnLog = """
        <log><logentry revision="7"><author>작성자</author><date>2026-11-14T00:00:00Z</date>
        <msg>제목

        전체 설명</msg><paths>
        <path action="D" kind="file">/trunk/old @ 한글.txt</path>
        <path action="A" kind="file" copyfrom-path="/trunk/source.txt" copyfrom-rev="6">/trunk/new.txt</path>
        <path action="M" kind="dir">/trunk</path></paths></logentry></log>
        """;

    private static async Task SvnCommittedPathsAndDeletedPeg()
    {
        var runner = new Runner(SvnInfo, SvnLog, SvnInfo, SvnLog, "--- old\n+++ new\n-old text\n", SvnInfo, "whole patch");
        var provider = new SvnProvider(runner);
        var details = await provider.GetHistoryDetailsAsync(Environment.CurrentDirectory, "7");
        Check(details.Entry.Message.Contains("전체 설명") && details.Files.Count == 3, "SVN details preserve message and changed paths.");
        Check(details.Files[1].OriginalPath == "/trunk/source.txt" && details.Files[1].Status == "copied", "SVN copies retain source path.");
        var patch = await provider.GetHistoryDiffAsync(Environment.CurrentDirectory, "7", details.Files[0]);
        Check(patch.Contains("-old text"), "SVN deleted patch returned.");
        var diff = runner.Commands[4].Arguments;
        Check(diff.Contains("--internal-diff") && diff.Contains("--notice-ancestry") && diff.Contains("-c") && diff.Contains("7"), "SVN uses internal committed revision diff.");
        Check(diff[^1] == "https://example.test/repo/trunk/old%20%40%20%ED%95%9C%EA%B8%80.txt@6", "SVN deleted path is URL-escaped and pinned before deletion.");
        Check(await provider.GetHistoryDiffAsync(Environment.CurrentDirectory, "7") == "whole patch", "Whole revision diff returned.");
        Check(runner.Commands[^1].Arguments[^1] == "https://example.test/repo@7", "Whole revision uses repository root, never working copy.");
    }

    private static string P4Detail(string status = "submitted", string path = "//depot/old%40name.txt") => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["change"] = "42", ["user"] = "작성자", ["time"] = "1794614400", ["status"] = status,
        ["desc"] = "제목\n\n전체 설명\n... fake metadata\n", ["depotFile0"] = path, ["action0"] = "delete", ["rev0"] = "3", ["type0"] = "text",
        ["depotFile1"] = "//depot/새 파일.txt", ["action1"] = "add", ["rev1"] = "1", ["type1"] = "text",
        ["depotFile2"] = "//depot/edit.txt", ["action2"] = "edit", ["rev2"] = "5", ["type2"] = "text",
        ["depotFile3"] = "//depot/data.bin", ["action3"] = "edit", ["rev3"] = "2", ["type3"] = "binary"
    });

    private static async Task PerforceSubmittedSnapshots()
    {
        var json = P4Detail();
        var runner = new Runner(json, json, "deleted content\n", json, "new content", json, "--- previous\n+++ committed\n", json);
        var provider = new PerforceProvider(runner);
        var details = await provider.GetHistoryDetailsAsync(Environment.CurrentDirectory, "42");
        Check(details.Entry.Message.EndsWith("... fake metadata\n") && details.Files.Count == 4, "P4 JSON preserves multiline description and files.");
        Check(runner.Commands[0].Arguments.SequenceEqual(new[] { "-Mj", "-ztag", "describe", "-s", "42" }), "P4 uses documented JSON tagged describe.");
        var deleted = await provider.GetHistoryDiffAsync(Environment.CurrentDirectory, "42", details.Files[0]);
        Check(deleted.Contains("-deleted content") && runner.Commands[2].Arguments[^1] == "//depot/old%40name.txt#2", "P4 deleted file reads previous committed revision.");
        var added = await provider.GetHistoryDiffAsync(Environment.CurrentDirectory, "42", details.Files[1]);
        Check(added.Contains("+new content") && added.Contains("No newline at end of file") && runner.Commands[4].Arguments[^1] == "//depot/새 파일.txt#1", "P4 added file reads selected revision with newline marker.");
        await provider.GetHistoryDiffAsync(Environment.CurrentDirectory, "42", details.Files[2]);
        Check(runner.Commands[6].Arguments.SequenceEqual(new[] { "diff2", "-du", "//depot/edit.txt#4", "//depot/edit.txt#5" }), "P4 modified file compares fixed depot revisions.");
        await Throws(() => provider.GetHistoryDiffAsync(Environment.CurrentDirectory, "42", details.Files[3]));
        Check(runner.Commands.Count == 8, "Binary P4 files must not be printed as text.");
        var purged = new Runner(json.Replace("\"delete\"", "\"purge\""));
        var purgedDiff = await new PerforceProvider(purged).GetHistoryDiffAsync(Environment.CurrentDirectory, "42", details.Files[0]);
        Check(purgedDiff.Contains("제거되어") && purged.Commands.Count == 1, "P4 purged content is explained without reading missing revisions.");
    }

    private static async Task MalformedDetailsAreRejected()
    {
        var empty = new Runner();
        foreach (var value in new[] { "--help", "7:HEAD", "-1", "0", "1\n2", "HEAD" })
        {
            await Throws(() => new SvnProvider(empty).GetHistoryDetailsAsync(Environment.CurrentDirectory, value));
            await Throws(() => new PerforceProvider(empty).GetHistoryDetailsAsync(Environment.CurrentDirectory, value));
        }
        Check(empty.Commands.Count == 0, "Invalid revisions never run CLI commands.");
        await Throws(() => new SvnProvider(new Runner(SvnInfo, SvnLog.Replace("revision=\"7\"", "revision=\"8\""))).GetHistoryDetailsAsync(Environment.CurrentDirectory, "7"));
        await Throws(() => new SvnProvider(new Runner(SvnInfo, SvnLog.Replace("/trunk/new.txt", "/trunk/../outside"))).GetHistoryDetailsAsync(Environment.CurrentDirectory, "7"));
        await Throws(() => new PerforceProvider(new Runner(P4Detail("pending"))).GetHistoryDetailsAsync(Environment.CurrentDirectory, "42"));
        await Throws(() => new PerforceProvider(new Runner(P4Detail(path: "//depot/...") )).GetHistoryDetailsAsync(Environment.CurrentDirectory, "42"));
        await Throws(() => new PerforceProvider(new Runner("{}" )).GetHistoryDetailsAsync(Environment.CurrentDirectory, "42"));
        var foreign = new Runner(P4Detail());
        await Throws(() => new PerforceProvider(foreign).GetHistoryDiffAsync(Environment.CurrentDirectory, "42", new FileChange("//elsewhere/secret.txt", "edit")));
        Check(foreign.Commands.Count == 1, "P4 file selection must belong to selected submitted change.");
    }

    private static async Task Throws(Func<Task> action)
    {
        try { await action(); }
        catch (VcsException) { return; }
        throw new InvalidOperationException("Expected invalid history request to fail.");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Runner(params string[] output) : ICommandRunner
    {
        private readonly Queue<string> _output = new(output);
        public List<CommandSpec> Commands { get; } = [];
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(new CommandResult(0, _output.Dequeue(), ""));
        }
    }
}
