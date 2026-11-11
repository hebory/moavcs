using System.Text;
using MoaVcs.Core;
using MoaVcs.Core.Providers;

namespace MoaVcs.Tests;

/// <summary>Offline CLI fixture checks for safe SVN text-conflict editing.</summary>
public static class SvnConflictTests
{
    public static async Task RunAsync()
    {
        await ReadsNamedConflictSnapshotsSavesAndMarksOnlyTheSelectedFile();
        await ReportsPropertyConflictsWithoutOpeningTextEditor();
        await RejectsPropertyAndTreeConflicts();
        await RefusesStaleMetadataAndStaleWorkingFiles();
        await RefusesEscapingSnapshotPaths();
    }

    private static async Task ReadsNamedConflictSnapshotsSavesAndMarksOnlyTheSelectedFile()
    {
        using var fixture = new Fixture();
        var runner = fixture.Runner(
            fixture.Info(), fixture.Status(), fixture.Info(),
            fixture.Info(), fixture.Status(), fixture.Info(),
            fixture.Info(), fixture.Status(), fixture.Info(), "Resolved conflicted state of 'source.txt'.\n", fixture.Status("modified"));
        var provider = new SvnProvider(runner);
        var conflicted = new FileChange("source.txt", "conflicted");

        var conflict = await provider.GetConflictAsync(fixture.Root, conflicted);
        Equal("base line\n", conflict.BaseText);
        Equal("mine line\n", conflict.LeftText);
        Equal("theirs line\n", conflict.RightText);
        Equal("내 변경 (working copy)", conflict.LeftLabel);
        Equal("받은 변경 (SVN r8)", conflict.RightLabel);
        Equal(fixture.MarkedText, conflict.WorkingText);
        True((provider.Capabilities & VcsCapabilities.ConflictResolution) != 0, "SVN text conflicts advertise the dedicated editor capability.");

        var saved = await provider.SaveConflictAsync(conflict, "mine line\ncombined line\ntheirs line\n");
        Equal("mine line\ncombined line\ntheirs line\n", await File.ReadAllTextAsync(fixture.Target));
        await provider.MarkConflictResolvedAsync(saved);

        var resolve = runner.Commands.Single(c => c.Arguments.Contains("resolve"));
        Contains(resolve.Arguments, "--accept");
        Contains(resolve.Arguments, "working");
        Contains(resolve.Arguments, "--depth");
        Contains(resolve.Arguments, "empty");
        Contains(resolve.Arguments, EscapePegPath(fixture.Target));
        True(!resolve.Arguments.Contains("mine-full") && !resolve.Arguments.Contains("theirs-full"),
            "Marking a hand-edited file resolved must keep the current contents.");
        Equal("mine line\ncombined line\ntheirs line\n", await File.ReadAllTextAsync(fixture.Target));
        Equal("svn", runner.Commands[0].Executable);
        True(runner.Commands.All(c => c.Arguments[0] == "--non-interactive"), "SVN commands must remain non-interactive.");
    }

    private static async Task ReportsPropertyConflictsWithoutOpeningTextEditor()
    {
        using var fixture = new Fixture();
        var runner = fixture.Runner(fixture.Status(props: "conflicted"));
        var changes = await new SvnProvider(runner).GetStatusAsync(fixture.Root);
        Equal(1, changes.Count);
        True(changes[0].IsConflicted, "SVN property conflicts must disable commits and show the VCS-specific guidance.");
    }

    private static async Task RejectsPropertyAndTreeConflicts()
    {
        using var fixture = new Fixture();
        foreach (var status in new[] { fixture.Status(props: "conflicted"), fixture.Status(tree: true) })
        {
            var runner = fixture.Runner(fixture.Info(), status);
            try
            {
                await new SvnProvider(runner).GetConflictAsync(fixture.Root, new FileChange("source.txt", "conflicted"));
                throw new InvalidOperationException("The built-in text editor must refuse a property or tree conflict.");
            }
            catch (VcsException ex) when (ex.Message.Contains("속성" , StringComparison.Ordinal) || ex.Message.Contains("트리", StringComparison.Ordinal)) { }
            Equal(2, runner.Commands.Count);
        }
    }

    private static async Task RefusesStaleMetadataAndStaleWorkingFiles()
    {
        using var fixture = new Fixture();
        var metadataRunner = fixture.Runner(fixture.Info(), fixture.Status(), fixture.Info(), fixture.Info(),
            fixture.Status(), fixture.Info(operation: "switch"));
        var metadataProvider = new SvnProvider(metadataRunner);
        var snapshot = await metadataProvider.GetConflictAsync(fixture.Root, new FileChange("source.txt", "conflicted"));
        try
        {
            await metadataProvider.SaveConflictAsync(snapshot, "my new result\n");
            throw new InvalidOperationException("Changed SVN conflict metadata must stop a save.");
        }
        catch (VcsException ex) when (ex.Message.Contains("바뀌었습니다", StringComparison.Ordinal)) { }
        Equal(fixture.MarkedText, await File.ReadAllTextAsync(fixture.Target));

        var fileRunner = fixture.Runner(fixture.Info(), fixture.Status(), fixture.Info(), fixture.Info(), fixture.Status(), fixture.Info());
        var fileProvider = new SvnProvider(fileRunner);
        var fileSnapshot = await fileProvider.GetConflictAsync(fixture.Root, new FileChange("source.txt", "conflicted"));
        await File.WriteAllTextAsync(fixture.Target, "another tool changed this\n");
        try
        {
            await fileProvider.SaveConflictAsync(fileSnapshot, "new result\n");
            throw new InvalidOperationException("Changed working contents must stop a save.");
        }
        catch (VcsException ex) when (ex.Message.Contains("다른 곳에서 변경", StringComparison.Ordinal)) { }
        Equal("another tool changed this\n", await File.ReadAllTextAsync(fixture.Target));

        var sideRunner = fixture.Runner(fixture.Info(), fixture.Status(), fixture.Info(), fixture.Info(), fixture.Status(), fixture.Info());
        var sideProvider = new SvnProvider(sideRunner);
        var sideSnapshot = await sideProvider.GetConflictAsync(fixture.Root, new FileChange("source.txt", "conflicted"));
        File.WriteAllText(fixture.Mine, "mine side changed\n");
        try
        {
            await sideProvider.SaveConflictAsync(sideSnapshot, "saved result\n");
            throw new InvalidOperationException("Changed SVN conflict side files must stop a save.");
        }
        catch (VcsException ex) when (ex.Message.Contains("바뀌었습니다", StringComparison.Ordinal)) { }
        Equal("another tool changed this\n", await File.ReadAllTextAsync(fixture.Target));
    }

    private static async Task RefusesEscapingSnapshotPaths()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "outside-conflict.txt");
        File.WriteAllText(outside, "must never be read");
        try
        {
            var runner = fixture.Runner(fixture.Info(), fixture.Status(), fixture.Info(basePath: outside));
            try
            {
                await new SvnProvider(runner).GetConflictAsync(fixture.Root, new FileChange("source.txt", "conflicted"));
                throw new InvalidOperationException("Conflict artifacts outside the working copy must be rejected.");
            }
            catch (VcsException ex) when (ex.Message.Contains("바깥", StringComparison.Ordinal)) { }
        }
        finally { File.Delete(outside); }
    }

    private static string EscapePegPath(string path) => path + "@";
    private static CommandResult Ok(string output) => new(0, output, string.Empty);
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'."); }
    private static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Contains(IReadOnlyList<string> args, string value)
    { if (!args.Contains(value)) throw new InvalidOperationException($"Expected SVN arguments to contain '{value}'."); }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "moavcs-svn-conflict-" + Guid.NewGuid().ToString("N"));
        public string Target => Path.Combine(Root, "source.txt");
        public string Base => Path.Combine(Root, "source.txt.merge-left.r4");
        public string Mine => Path.Combine(Root, "source.txt.working");
        public string Their => Path.Combine(Root, "source.txt.merge-right.r8");
        public string MarkedText => "before\n<<<<<<< .working\nmine line\n=======\ntheirs line\n>>>>>>> .merge-right.r8\nafter\n";

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Target, MarkedText, new UTF8Encoding(false));
            File.WriteAllText(Base, "base line\n", new UTF8Encoding(false));
            File.WriteAllText(Mine, "mine line\n", new UTF8Encoding(false));
            File.WriteAllText(Their, "theirs line\n", new UTF8Encoding(false));
        }

        public string Status(string item = "conflicted", string props = "none", bool tree = false) =>
            $"<status><target path=\"{Escape(Root)}\"><entry path=\"{Escape(Target)}\"><wc-status item=\"{item}\" props=\"{props}\" tree-conflicted=\"{tree.ToString().ToLowerInvariant()}\"/></entry></target></status>";

        public string Info(string operation = "update", string? basePath = null) =>
            $"<info><entry path=\"source.txt\" kind=\"file\"><url>https://example.test/trunk/source.txt</url><relative-url>^/trunk/source.txt</relative-url><wc-info>" +
            $"<wcroot-abspath>{Escape(Root)}</wcroot-abspath><schedule>normal</schedule></wc-info><conflict type=\"text\" operation=\"{operation}\">" +
            "<version side=\"source-left\" revision=\"4\"><path-in-repos>trunk/source.txt</path-in-repos></version>" +
            "<version side=\"source-right\" revision=\"8\"><path-in-repos>trunk/source.txt</path-in-repos></version>" +
            $"<prev-base-file>{Escape(basePath ?? Base)}</prev-base-file><prev-wc-file>{Escape(Mine)}</prev-wc-file><cur-base-file>{Escape(Their)}</cur-base-file>" +
            "</conflict></entry></info>";

        public FakeRunner Runner(params string[] output) => new(output.Select(Ok).ToArray());
        private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? value;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FakeRunner(params CommandResult[] responses) : ICommandRunner
    {
        private readonly Queue<CommandResult> _responses = new(responses);
        public List<CommandSpec> Commands { get; } = [];
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (_responses.Count == 0) throw new InvalidOperationException("Unexpected SVN CLI invocation.");
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
