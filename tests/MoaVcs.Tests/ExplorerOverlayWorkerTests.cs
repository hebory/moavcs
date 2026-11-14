using MoaVcs.App.Services;
using MoaVcs.Core;

namespace MoaVcs.Tests;

internal static class ExplorerOverlayWorkerTests
{
    public static async Task RunAsync()
    {
        await ScansActualGitAndCleanTransitionAsync();
        await InjectedRunnerCoversNestedOwnershipAndFailureAsync();
        SettingsAndLaunchOptionsStayScoped();
    }

    private static async Task ScansActualGitAndCleanTransitionAsync()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "outer");
        Directory.CreateDirectory(root);
        var runner = new ProcessCommandRunner();
        await RunGit(runner, root, "init", "-b", "main");
        await ConfigureGit(runner, root);
        await Write(root, ".gitignore", "nested/\n");
        await Write(root, "src/deep/conflict.txt", "base\n");
        await Write(root, "src/modified.txt", "base\n");
        await RunGit(runner, root, "add", "--", ".gitignore", "src");
        await RunGit(runner, root, "commit", "-m", "base");
        await RunGit(runner, root, "branch", "incoming");

        await Write(root, "src/deep/conflict.txt", "main branch\n");
        await RunGit(runner, root, "commit", "-am", "main change");
        await RunGit(runner, root, "checkout", "incoming");
        await Write(root, "src/deep/conflict.txt", "incoming branch\n");
        await RunGit(runner, root, "commit", "-am", "incoming change");
        await RunGit(runner, root, "checkout", "main");
        var merge = await RunGitRaw(runner, root, "merge", "incoming");
        Check(!merge.Success, "actual Git fixture creates an unresolved merge conflict");

        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        await RunGit(runner, nested, "init", "-b", "main");
        await ConfigureGit(runner, nested);
        await Write(nested, "owned.txt", "base\n");
        await RunGit(runner, nested, "add", "--", "owned.txt");
        await RunGit(runner, nested, "commit", "-m", "nested base");
        await Write(nested, "owned.txt", "nested modified\n");

        await Write(root, "src/modified.txt", "working edit\n");
        await Write(root, "new.txt", "untracked\n");
        var settings = new AppSettings
        {
            RecentRepositories = [new(root, VcsKind.Git), new(nested, VcsKind.Git)]
        };
        var scan = await ExplorerOverlayWorker.ScanOnceAsync(settings, runner);
        var byPath = scan.Entries.ToDictionary(entry => entry.Path, entry => entry.State, StringComparer.OrdinalIgnoreCase);
        Check(byPath[Path.Combine(root, "src", "deep")] == ExplorerOverlayState.Conflict,
            "real Git conflict is aggregated onto its containing folder");
        Check(byPath[Path.Combine(root, "src", "modified.txt")] == ExplorerOverlayState.Modified,
            "real Git modification reaches worker snapshot");
        Check(byPath[Path.Combine(root, "new.txt")] == ExplorerOverlayState.Untracked,
            "real Git untracked file reaches worker snapshot");
        Check(byPath[Path.Combine(nested, "owned.txt")] == ExplorerOverlayState.Modified,
            "nested checkout reports its own dirty file");
        Check(!scan.Entries.Any(entry => IsWithin(Path.Combine(root, ".git"), entry.Path) || IsWithin(Path.Combine(nested, ".git"), entry.Path)),
            "real Git worker snapshot excludes repository metadata");

        var oldEntries = scan.Entries;
        await RunGit(runner, root, "reset", "--hard", "HEAD");
        await Write(root, "src/deep/conflict.txt", "main branch\n");
        await Write(root, "src/modified.txt", "base\n");
        File.Delete(Path.Combine(root, "new.txt"));
        await Write(nested, "owned.txt", "base\n");
        var rawCleanStatus = await RunGitRaw(runner, root, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        var clean = await ExplorerOverlayWorker.ScanOnceAsync(settings, runner);
        var changedPaths = ExplorerOverlayWorker.ChangedPaths(oldEntries, clean.Entries);
        Check(clean.Entries.Count == 0 && changedPaths.Count > 0,
            "clean transition removes stale file and folder badges; remaining=" + string.Join(", ", clean.Entries.Select(entry => entry.State + ":" + Path.GetRelativePath(root, entry.Path))) +
            "; rawGit=" + rawCleanStatus.StandardOutput.Replace('\0', '|') +
            "; warnings=" + string.Join(" | ", clean.Warnings));

        var cachePath = Path.Combine(temp.Path, "cache", "status.bin");
        ExplorerOverlayWorker.WriteCache(cachePath, scan.Entries);
        using (var cache = File.OpenRead(cachePath))
        {
            var decoded = ExplorerOverlaySnapshot.Read(cache, out var timestamp);
            Check(decoded.SequenceEqual(scan.Entries) && timestamp.Kind == DateTimeKind.Utc,
                "atomic cache output decodes with the complete worker snapshot");
        }
        ExplorerOverlayWorker.WriteCache(cachePath, clean.Entries);
        using (var cache = File.OpenRead(cachePath))
            Check(ExplorerOverlaySnapshot.Read(cache, out _).Count == 0, "atomic cache replacement publishes the clean snapshot");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(cachePath)!, "*.tmp").Any(), "atomic write leaves no temporary files");
    }

    private static async Task InjectedRunnerCoversNestedOwnershipAndFailureAsync()
    {
        using var temp = new TempDirectory();
        var outer = Path.Combine(temp.Path, "outer");
        var inner = Path.Combine(outer, "inner");
        Directory.CreateDirectory(inner);
        var fixture = new OverlayFixtureRunner(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [outer] = "?? outer.txt\0?? inner/owned.txt\0",
            [inner] = " M owned.txt\0"
        });
        var settings = new AppSettings { RecentRepositories = [new(outer, VcsKind.Git), new(inner, VcsKind.Git)] };
        var scan = await ExplorerOverlayWorker.ScanOnceAsync(settings, fixture);
        Check(scan.Entries.Any(entry => entry.Path == Path.Combine(outer, "outer.txt")), "outer checkout keeps its own status");
        Check(scan.Entries.Any(entry => entry.Path == Path.Combine(inner, "owned.txt") && entry.State == ExplorerOverlayState.Modified),
            "nested checkout takes ownership of statuses below its root");
        Check(scan.Entries.Single(entry => entry.Path == Path.Combine(inner, "owned.txt")).State == ExplorerOverlayState.Modified,
            "nested checkout's own state wins over outer repository status for the same path");

        fixture.FailStatus = true;
        var failed = await ExplorerOverlayWorker.ScanOnceAsync(new AppSettings
        {
            RecentRepositories = [new(outer, VcsKind.Git)]
        }, fixture);
        Check(failed.Entries.Count == 0 && failed.Warnings.Count == 1, "failed status read yields warning and no stale entries");
        var cachePath = Path.Combine(temp.Path, "cache", "status.bin");
        ExplorerOverlayWorker.WriteCache(cachePath, scan.Entries);
        ExplorerOverlayWorker.WriteCache(cachePath, failed.Entries);
        using var cache = File.OpenRead(cachePath);
        Check(ExplorerOverlaySnapshot.Read(cache, out _).Count == 0, "failed scan replacement clears stale cache data");
    }

    private static void SettingsAndLaunchOptionsStayScoped()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new SettingsStore(path);
        store.Save(new AppSettings
        {
            RecentRepositories = [new(temp.Path, VcsKind.Git)],
            ExplorerMenu = new ExplorerMenuSettings { ShowCommitAtTopLevel = false, ShowUpdateAtTopLevel = true },
            ExecutablePaths = new Dictionary<VcsKind, string> { [VcsKind.Git] = "git-custom" },
            ExternalTools = new Dictionary<ExternalToolKind, ExternalToolSettings>
            {
                [ExternalToolKind.Diff] = new() { UseExternal = true, ExecutablePath = "diff-tool.exe", ArgumentTemplates = ["{left}", "{right}"] }
            }
        });
        store.SaveExplorerOverlays(true);
        var saved = store.Load();
        Check(saved.ExplorerOverlaysEnabled && saved.RecentRepositories.Count == 1 &&
              !saved.ExplorerMenu.ShowCommitAtTopLevel && saved.ExplorerMenu.ShowUpdateAtTopLevel &&
              saved.ExecutablePaths[VcsKind.Git] == "git-custom" && saved.ExternalTools[ExternalToolKind.Diff].ArgumentTemplates.Count == 2,
            "overlay preference update preserves existing repository and tool settings");

        Throws<FormatException>(() => LaunchOptions.Parse(["--overlay-worker", "--path", temp.Path]), "overlay worker cannot combine with a repository launch");
        Throws<FormatException>(() => LaunchOptions.Parse(["--overlay-worker", "--pause-overlays"]), "overlay operations are mutually exclusive");
        Throws<FormatException>(() => LaunchOptions.Parse(["--disable-overlays", "--install-context-menu"]), "overlay operation cannot combine with menu registration");
        Check(LaunchOptions.Parse(["--overlay-worker"]).OverlayOperation == "--overlay-worker", "valid overlay worker launch is recognized");
    }

    private static async Task ConfigureGit(ICommandRunner runner, string root)
    {
        await RunGit(runner, root, "config", "user.name", "moavcs overlay test");
        await RunGit(runner, root, "config", "user.email", "overlay-test@example.invalid");
        await RunGit(runner, root, "config", "core.autocrlf", "false");
        await RunGit(runner, root, "config", "core.safecrlf", "false");
    }

    private static async Task RunGit(ICommandRunner runner, string root, params string[] args)
    {
        var result = await RunGitRaw(runner, root, args);
        if (!result.Success) throw new InvalidOperationException("Explorer overlay Git fixture command failed: " + result.Output);
    }

    private static Task<CommandResult> RunGitRaw(ICommandRunner runner, string root, params string[] args) =>
        runner.RunAsync(new CommandSpec("git", args, root, Timeout: TimeSpan.FromSeconds(30)));

    private static Task Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(path, content, new System.Text.UTF8Encoding(false));
    }

    private static bool IsWithin(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Explorer overlay worker test failed: " + label);
    }

    private static void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Explorer overlay worker test failed: " + label);
    }

    private sealed class OverlayFixtureRunner(Dictionary<string, string> statuses) : ICommandRunner
    {
        public bool FailStatus { get; set; }

        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.Arguments.SequenceEqual(["rev-parse", "--show-toplevel"]))
                return Task.FromResult(new CommandResult(0, Path.GetFullPath(command.WorkingDirectory), ""));
            if (command.Arguments.SequenceEqual(["symbolic-ref", "-q", "HEAD"]))
                return Task.FromResult(new CommandResult(0, "refs/heads/main", ""));
            if (command.Arguments.Contains("status", StringComparer.Ordinal))
            {
                if (FailStatus) return Task.FromResult(new CommandResult(1, "", "fixture status failure"));
                return Task.FromResult(new CommandResult(0, statuses.GetValueOrDefault(Path.GetFullPath(command.WorkingDirectory)) ?? "", ""));
            }
            throw new InvalidOperationException("Unexpected overlay fixture command: " + string.Join(' ', command.Arguments));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, ".local", "moavcs-overlay-worker-" + Guid.NewGuid().ToString("N")));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            var localRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, ".local")) + System.IO.Path.DirectorySeparatorChar;
            if (!Path.StartsWith(localRoot, StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(Path).StartsWith("moavcs-overlay-worker-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove an unexpected Explorer overlay fixture path.");
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            foreach (var directory in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).Append(Path))
                File.SetAttributes(directory, FileAttributes.Directory);
            Directory.Delete(Path, recursive: true);
        }
    }
}
