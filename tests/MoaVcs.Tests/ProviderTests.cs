using MoaVcs.Core;
using MoaVcs.Core.Providers;

namespace MoaVcs.Tests;

/// <summary>Small offline contract tests for the CLI adapters.</summary>
public static class ProviderTests
{
    public static async Task RunAsync()
    {
        await SvnUsesXmlAndScopesCommit();
        await PerforceUsesConfiguredWorkspaceAndTaggedRecords();
        await PerforceRejectsUnrecognizedStatus();
        await LoreUsesDocumentedTextFormatsAndRepositoryMarker();
        await LoreRejectsUnrecognizedStatus();
        await LorePreservesPathsAndParsesMetadata();
        await SvnRejectsUnsafeCloneArguments();
        await RejectsPathTraversal();
    }

    private static async Task SvnUsesXmlAndScopesCommit()
    {
        using var temp = new TempDirectory();
        var runner = new FakeRunner(
            Ok("<info><entry path=\".\"><url>https://example.test/repo/trunk</url><wc-info><wcroot-abspath>" + Escape(temp.Path) + "</wcroot-abspath></wc-info></entry></info>"),
            Ok("<status><target path=\".\"><entry path=\"" + Escape(Path.Combine(temp.Path, "hello.txt")) + "\"><wc-status item=\"modified\" props=\"none\"/></entry></target></status>"),
            Ok("<log><logentry revision=\"7\"><author>dev</author><date>2026-11-10T00:00:00Z</date><msg>update</msg></logentry></log>"),
            Ok("Committed revision 8."));
        var provider = new SvnProvider(runner);
        var info = await provider.GetRepositoryAsync(temp.Path);
        Equal(temp.Path, info.RootPath);
        Equal("trunk", info.Branch);
        var status = await provider.GetStatusAsync(temp.Path);
        Equal(1, status.Count);
        Equal("modified", status[0].Status);
        Equal("7", (await provider.GetHistoryAsync(temp.Path))[0].Id);
        await provider.ExecuteAsync(new VcsRequest(VcsOperation.Commit, temp.Path, ["hello@world.txt"], "save"));
        True(runner.Commands[^1].Arguments.Any(arg => arg.EndsWith("hello@world.txt@", StringComparison.OrdinalIgnoreCase)), "SVN peg syntax must preserve a literal @ in a selected path.");
        Contains(runner.Commands[^1].Arguments, "save");
        Contains(runner.Commands[^1].Arguments, "--non-interactive");
        Contains(runner.Commands[^1].Arguments, "--depth");
        Contains(runner.Commands[^1].Arguments, "empty");
        True(!runner.Commands[1].Arguments.Contains("--no-ignore"), "SVN ignored files should not be reported as ordinary changes.");
        Equal("--non-interactive", runner.Commands[0].Arguments[0]);
        var callsBeforeUnscopedCommit = runner.Commands.Count;
        try
        {
            await provider.ExecuteAsync(new VcsRequest(VcsOperation.Commit, temp.Path, null, "save everything"));
            throw new InvalidOperationException("SVN commit must require an explicit selection.");
        }
        catch (VcsException) { }
        Equal(callsBeforeUnscopedCommit, runner.Commands.Count);
        True((provider.Capabilities & VcsCapabilities.Unstage) == 0, "SVN has no index-based unstage operation.");
    }

    private static async Task PerforceUsesConfiguredWorkspaceAndTaggedRecords()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "source.txt");
        var runner = new FakeRunner(
            Ok("... clientRoot " + temp.Path + "\n... clientName ws-main\n... serverAddress p4.example:1666\n"),
            Ok("... depotFile //depot/source.txt\n... clientFile " + file + "\n... action edit\n"),
            Ok("... change 42\n... user dev\n... time 1794268800\n... desc fix parser\n"));
        var provider = new PerforceProvider(runner);
        var info = await provider.GetRepositoryAsync(temp.Path);
        Equal(temp.Path, info.RootPath);
        Equal("ws-main", info.Branch);
        var status = await provider.GetStatusAsync(temp.Path);
        Equal(1, status.Count);
        Equal("edit", status[0].Status);
        var history = await provider.GetHistoryAsync(temp.Path);
        Equal("42", history[0].Id);
        Equal("fix parser", history[0].Message);
        True((provider.Capabilities & VcsCapabilities.Commit) == 0, "P4 submit selection is not exposed until the CLI scope is safe.");
        True((provider.Capabilities & VcsCapabilities.Clone) == 0, "P4 workspaces are configured by a server and client spec, not cloned like a repository URL.");
        var diffRunner = new FakeRunner(Ok("diff"));
        await new PerforceProvider(diffRunner).GetDiffAsync(temp.Path, new FileChange("source.txt", "edit"));
        Contains(diffRunner.Commands[0].Arguments, "-f");
    }

    private static async Task PerforceRejectsUnrecognizedStatus()
    {
        using var temp = new TempDirectory();
        var provider = new PerforceProvider(new FakeRunner(Ok("not tagged output\n")));
        try
        {
            await provider.GetStatusAsync(temp.Path);
            throw new InvalidOperationException("Unknown Perforce status output must not be treated as clean.");
        }
        catch (VcsException ex) when (ex.Message.Contains("tagged format", StringComparison.Ordinal)) { }
    }

    private static async Task LoreUsesDocumentedTextFormatsAndRepositoryMarker()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, ".lore"));
        File.WriteAllText(Path.Combine(temp.Path, ".lore", "config.toml"), "remote_url = \"lores://example.test/project\"\n");
        const string status = "Repository example\nOn branch main revision 3 -> abc123\nChanges staged for commit:\nM docs/readme.md\nUntracked files:\nA notes [draft].txt\n";
        const string history = "12 docs update\n";
        var runner = new FakeRunner(Ok(status), Ok(status), Ok(history));
        var provider = new LoreProvider(runner);
        var info = await provider.GetRepositoryAsync(temp.Path);
        Equal("main", info.Branch);
        Equal("lores://example.test/project", info.Summary);
        var changes = await provider.GetStatusAsync(temp.Path);
        Equal("docs/readme.md", changes[0].Path);
        Equal("notes [draft].txt", changes[1].Path);
        var entries = await provider.GetHistoryAsync(temp.Path);
        Equal("12", entries[0].Id);
        Equal("docs update", entries[0].Message);
        True(runner.Commands[0].Arguments.Contains("--scan"), "Lore status must reconcile working-tree changes.");
        Equal("status", runner.Commands[0].Arguments[2]);
        True(changes[0].IsStaged, "Lore staged status must remain distinct from worktree status.");
        True((provider.Capabilities & VcsCapabilities.Commit) != 0, "Lore exposes commit for its staged revision.");
    }

    private static async Task LoreRejectsUnrecognizedStatus()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, ".lore"));
        File.WriteAllText(Path.Combine(temp.Path, ".lore", "config.toml"), "");
        var runner = new FakeRunner(Ok("unrecognized output\n"));
        var provider = new LoreProvider(runner);
        try
        {
            await provider.GetStatusAsync(temp.Path);
            throw new InvalidOperationException("Unrecognized Lore output must not be treated as a clean working copy.");
        }
        catch (VcsException ex) when (ex.Message.Contains("format is not recognized", StringComparison.Ordinal)) { }
    }

    private static async Task LorePreservesPathsAndParsesMetadata()
    {
        using var temp = new TempDirectory();
        var output = "Repository example\nOn branch main revision 3 -> abc123\n" +
            "Changes staged for commit:\nV old name.txt -> moved name.txt (M)\nM literal (M) \n" +
            "Changes in conflict:\nM conflict.txt (M)!\n" +
            "Changes not staged for commit:\nM literal (M)\n";
        var provider = new LoreProvider(new FakeRunner(Ok(output)));
        var changes = await provider.GetStatusAsync(temp.Path);
        var moved = changes.Single(c => c.Path == "moved name.txt");
        Equal("old name.txt", moved.OriginalPath);
        Equal("V", moved.IndexStatus);
        Equal("conflicted", changes.Single(c => c.Path == "conflict.txt").Status);
        var literal = changes.Single(c => c.Path == "literal (M)");
        True(literal.IsStaged && literal.WorkTreeStatus == "M", "Literal marker filenames must survive and mixed states must merge.");
    }

    private static async Task SvnRejectsUnsafeCloneArguments()
    {
        using var temp = new TempDirectory();
        var occupied = Path.Combine(temp.Path, "occupied");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "existing.txt"), "keep");
        var runner = new FakeRunner();
        var provider = new SvnProvider(runner);
        foreach (var request in new[]
        {
            new VcsRequest(VcsOperation.Clone, temp.Path, Remote: "--help", Destination: Path.Combine(temp.Path, "new")),
            new VcsRequest(VcsOperation.Clone, temp.Path, Remote: "https://example.test/repo", Destination: occupied)
        })
        {
            try
            {
                await provider.ExecuteAsync(request);
                throw new InvalidOperationException("Unsafe SVN checkout arguments should have been rejected.");
            }
            catch (VcsException) { }
        }
        Equal(0, runner.Commands.Count);
    }

    private static async Task RejectsPathTraversal()
    {
        using var temp = new TempDirectory();
        var runner = new FakeRunner();
        var provider = new SvnProvider(runner);
        try
        {
            await provider.ExecuteAsync(new VcsRequest(VcsOperation.Commit, temp.Path, ["..\\outside.txt"], "bad"));
            throw new InvalidOperationException("Path traversal should have been rejected.");
        }
        catch (VcsException) { }
        Equal(0, runner.Commands.Count);
    }

    private static CommandResult Ok(string stdout) => new(0, stdout, string.Empty);
    private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? value;

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
    private static void True(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Contains(IReadOnlyList<string> values, string expected)
    { if (!values.Contains(expected)) throw new InvalidOperationException($"Expected command arguments to contain '{expected}'."); }

    private sealed class FakeRunner(params CommandResult[] results) : ICommandRunner
    {
        private readonly Queue<CommandResult> _results = new(results);
        public List<CommandSpec> Commands { get; } = [];
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (_results.Count == 0) throw new InvalidOperationException("Unexpected CLI invocation.");
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "moavcs-test-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
