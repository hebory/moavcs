using System.Text;
using System.Text.Json;
using MoaVcs.Core;
using MoaVcs.Core.Providers;

namespace MoaVcs.Tests;

public static class LoreBlameTests
{
    private const string CurrentRevision = "4f62fcfd894dcc890935733bcdb76e5fea3ea7725f73aef61d93f067c13365ac";
    private const string PreviousRevision = "9c294bc1cdba9a26e61bd955c934c92da14feac54f309bd48522d641914328a2";
    private const string LocalRepository = "01a1c13279f1761c83e85c5af1e541de";
    private const string LinkedRepository = "02a1c13279f1761c83e85c5af1e541de";

    public static async Task RunAsync()
    {
        await UsesPinnedAuditRangesAndRevisionMetadata();
        await PreservesForeignRepositoryAttributionWithoutLocalLookup();
        await RejectsIncompleteAuditCoverage();
        await RejectsMalformedNumericFields();
        await EnforcesLineLimitBeforeAudit();
    }

    private static async Task UsesPinnedAuditRangesAndRevisionMetadata()
    {
        using var temp = new TempDirectory();
        const string source = "first line\nlocal edit\nthird line\n";
        var runner = new FakeRunner(source, Audit(
            Range(1, 1, CurrentRevision, "attributed"),
            Range(2, 2, "0000000000000000000000000000000000000000000000000000000000000000", "uncommitted"),
            Range(3, 3, PreviousRevision, "attributed")));
        var provider = new LoreProvider(runner);
        var file = new FileChange("new name.txt", "renamed", "V", "", "old name.txt");

        var result = await provider.GetBlameAsync(temp.Path, file);

        Equal(3, result.Lines.Count);
        Equal("first line", result.Lines[0].Text);
        Equal(CurrentRevision, result.Lines[0].Revision);
        Equal("artist-id", result.Lines[0].Author);
        Equal("2026-11-09T16:44:33.5590000+00:00", result.Lines[0].Date);
        Equal("", result.Lines[1].Revision);
        Equal("이력 확인 불가 (uncommitted)", result.Lines[1].Author);
        Equal("third line", result.Lines[2].Text);
        Equal(PreviousRevision, result.Lines[2].Revision);
        Equal($"Lore revision 2 ({CurrentRevision})", result.RevisionLabel);

        var audit = runner.Commands.Single(command => command.Arguments.Contains("audit"));
        Contains(audit.Arguments, "--revision");
        Contains(audit.Arguments, CurrentRevision);
        Contains(audit.Arguments, "--max-revisions");
        Contains(audit.Arguments, "0");
        Contains(audit.Arguments, "--no-ignore-space-at-eol");
        Contains(audit.Arguments, "./old name.txt");
        var metadata = runner.Commands.Where(command => command.Arguments.Contains("metadata")).ToArray();
        Equal(2, metadata.Length);
        True(metadata.Any(command => command.Arguments.Contains(CurrentRevision)), "Current attributed revision metadata was not requested.");
        True(metadata.Any(command => command.Arguments.Contains(PreviousRevision)), "Previous attributed revision metadata was not requested.");
        True(metadata.All(command => command.Arguments.Contains("get")), "Metadata must use revision metadata get.");
    }

    private static async Task PreservesForeignRepositoryAttributionWithoutLocalLookup()
    {
        using var temp = new TempDirectory();
        var runner = new FakeRunner("local line\nlinked line\n", Audit(
            Range(1, 1, CurrentRevision, "attributed", LocalRepository),
            Range(2, 2, CurrentRevision, "attributed", LinkedRepository)));
        var result = await new LoreProvider(runner).GetBlameAsync(temp.Path, new FileChange("source.txt", "modified"));

        Equal(CurrentRevision, result.Lines[0].Revision);
        Equal("artist-id", result.Lines[0].Author);
        Equal(CurrentRevision, result.Lines[1].Revision);
        Equal("연결 저장소 이력 (작성자 조회 미지원)", result.Lines[1].Author);
        Equal("", result.Lines[1].Date);
        Equal(1, runner.Commands.Count(command => command.Arguments.Contains("metadata")));
    }

    private static async Task RejectsIncompleteAuditCoverage()
    {
        using var temp = new TempDirectory();
        var runner = new FakeRunner("line one\nline two\n", Audit(
            Range(1, 1, CurrentRevision, "attributed")));
        try
        {
            await new LoreProvider(runner).GetBlameAsync(temp.Path, new FileChange("source.txt", "modified"));
            throw new InvalidOperationException("Lore blame should reject missing audit lines.");
        }
        catch (VcsException ex) when (ex.Message.Contains("전체 2줄을 포함하지 않습니다", StringComparison.Ordinal)) { }
        Equal(4, runner.Commands.Count);
        True(runner.Commands.All(command => !command.Arguments.Contains("metadata")),
            "Metadata should not be queried until audit coverage is validated.");
    }

    private static async Task RejectsMalformedNumericFields()
    {
        using var temp = new TempDirectory();
        var badRange = "{\"tagName\":\"fileAudit\",\"data\":{\"repository\":\"" + LocalRepository + "\",\"revision\":\"" +
            CurrentRevision + "\",\"lineFirst\":\"1\",\"lineLast\":1,\"outcome\":\"attributed\"}}";
        var rangeRunner = new FakeRunner("single line\n", Audit(badRange));
        await ExpectVcsError(() => new LoreProvider(rangeRunner).GetBlameAsync(temp.Path, new FileChange("source.txt", "modified")), "양의 정수");

        var range = Range(1, 1, CurrentRevision, "attributed");
        var badTimestamp = JsonSerializer.Serialize(new
        {
            tagName = "metadata",
            data = new { key = "timestamp", value = new { tagName = "numeric", data = "1794242673559" } }
        });
        var timestampRunner = new FakeRunner("single line\n", Audit(range), badTimestamp + "\n" + Complete());
        await ExpectVcsError(() => new LoreProvider(timestampRunner).GetBlameAsync(temp.Path, new FileChange("source.txt", "modified")), "정수여야 합니다");
    }

    private static async Task ExpectVcsError(Func<Task<BlameResult>> action, string expectedText)
    {
        try
        {
            await action();
            throw new InvalidOperationException("Malformed Lore numeric data should be rejected.");
        }
        catch (VcsException ex) when (ex.Message.Contains(expectedText, StringComparison.Ordinal)) { }
    }

    private static async Task EnforcesLineLimitBeforeAudit()
    {
        using var temp = new TempDirectory();
        var tooManyLines = string.Concat(Enumerable.Repeat("x\n", LineDiff.MaxLines + 1));
        var runner = new FakeRunner(tooManyLines, Audit());
        try
        {
            await new LoreProvider(runner).GetBlameAsync(temp.Path, new FileChange("large.txt", "modified"));
            throw new InvalidOperationException("Lore blame should enforce the shared line cap.");
        }
        catch (VcsException ex) when (ex.Message.Contains(LineDiff.MaxLines.ToString("N0"), StringComparison.Ordinal)) { }
        Equal(3, runner.Commands.Count);
        True(runner.Commands.All(command => !command.Arguments.Contains("audit")), "Oversized files must not run lore file audit.");
    }

    private static string Audit(params string[] ranges) => string.Join('\n', ranges.Append(Complete()));

    private static string Range(int first, int last, string revision, string outcome, string repository = LocalRepository) => JsonSerializer.Serialize(new
    {
        tagName = "fileAudit",
        data = new { repository, revision, lineFirst = first, lineLast = last, outcome }
    });

    private static string Complete() => "{\"tagName\":\"complete\",\"data\":{\"status\":0,\"error\":{\"errorCode\":0,\"message\":\"\",\"traceLocations\":[]}}}";

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private static void Contains(IReadOnlyList<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.Ordinal))
            throw new InvalidOperationException($"Expected command arguments to contain '{value}'.");
    }

    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class FakeRunner(string snapshot, string auditJson, string? metadataJson = null) : ICommandRunner
    {
        public List<CommandSpec> Commands { get; } = [];

        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            var args = command.Arguments;
            if (args.Contains("audit")) return Ok(auditJson);
            if (args.Contains("write"))
            {
                var index = IndexOf(args, "--output");
                if (index < 0 || index + 1 >= args.Count) throw new InvalidOperationException("Missing Lore snapshot output path.");
                File.WriteAllText(args[index + 1], snapshot, new UTF8Encoding(false));
                return Ok(Complete());
            }
            if (args.Contains("file"))
            {
                var pathIndex = IndexOf(args, "--") + 1;
                var relativePath = pathIndex > 0 && pathIndex < args.Count ? args[pathIndex] : "source.txt";
                if (relativePath.StartsWith("./", StringComparison.Ordinal)) relativePath = relativePath[2..];
                return Ok("{\"tagName\":\"fileInfo\",\"data\":{\"path\":" + System.Text.Json.JsonSerializer.Serialize(relativePath) + ",\"isFile\":true,\"isDir\":false,\"flagAdded\":false,\"size\":" +
                    Encoding.UTF8.GetByteCount(snapshot).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}\n" + Complete());
            }
            if (args.Contains("metadata"))
                return Ok(metadataJson ?? ("{\"tagName\":\"metadata\",\"data\":{\"key\":\"committed-by\",\"value\":{\"tagName\":\"string\",\"data\":\"artist-id\"}}}\n" +
                    "{\"tagName\":\"metadata\",\"data\":{\"key\":\"timestamp\",\"value\":{\"tagName\":\"numeric\",\"data\":1794242673559}}}\n" + Complete()));
            if (args.Contains("--revision-only"))
                return Ok("{\"tagName\":\"repositoryStatusRevision\",\"data\":{\"repository\":\"" + LocalRepository + "\",\"revision\":\"" +
                    CurrentRevision + "\",\"revisionNumber\":2}}\n" + Complete());
            throw new InvalidOperationException("Unexpected Lore command: " + string.Join(' ', args));
        }

        private static int IndexOf(IReadOnlyList<string> values, string value)
        {
            for (var i = 0; i < values.Count; i++) if (values[i] == value) return i;
            return -1;
        }

        private static Task<CommandResult> Ok(string output) => Task.FromResult(new CommandResult(0, output, ""));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "moavcs-lore-blame-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
