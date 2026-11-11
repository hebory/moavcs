using System.IO;
using System.Text.Json;
using MoaVcs.Core;
using MoaVcs.Core.Providers;

namespace MoaVcs.App.Services;

public sealed record OverlayScan(IReadOnlyList<ExplorerOverlayEntry> Entries, IReadOnlyList<string> Warnings);

/// <summary>All VCS execution stays outside Explorer. No repository content is changed.</summary>
public static class ExplorerOverlayWorker
{
    public static async Task RunAsync(string executable, SettingsStore store)
    {
        // Use the existence of a named kernel object as the guard; async continuations need not own a mutex thread.
        using var singleton = new Mutex(false, ExplorerOverlays.ObjectName("worker"), out var created);
        if (!created) return;
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, ExplorerOverlays.ObjectName("stop"));
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, ExplorerOverlays.ObjectName("wake"));
        using var cancellation = new CancellationTokenSource();
        var registration = ThreadPool.RegisterWaitForSingleObject(stop, (_, _) => cancellation.Cancel(), null, Timeout.Infinite, true);
        IReadOnlyList<ExplorerOverlayEntry> previous = [];
        try
        {
            while (!cancellation.IsCancellationRequested && ExplorerOverlays.IsStartupOwner(executable))
            {
                var settings = store.Load();
                if (store.LoadWarning is not null || !settings.ExplorerOverlaysEnabled) break;
                var scan = await ScanOnceAsync(settings, new ProcessCommandRunner(), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                // A disable or install switch while a CLI was running must never republish its old result.
                if (!ExplorerOverlays.IsStartupOwner(executable) || !store.Load().ExplorerOverlaysEnabled) break;
                WriteCache(ExplorerOverlays.CachePath, scan.Entries);
                WriteWarnings(scan.Warnings);
                var changed = ChangedPaths(previous, scan.Entries);
                previous = scan.Entries;
                if (changed.Count > 0)
                {
                    // The native handler reloads at most once per two seconds. Notify after that cache expires.
                    await Task.Delay(TimeSpan.FromMilliseconds(2200), cancellation.Token);
                    foreach (var path in changed) ExplorerOverlays.NotifyPath(path);
                }
                await Task.Run(() => WaitHandle.WaitAny([stop, wake], TimeSpan.FromSeconds(10)));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            registration.Unregister(null);
            ExplorerOverlays.ClearCache();
            foreach (var path in previous.Select(entry => entry.Path).Take(2000)) ExplorerOverlays.NotifyPath(path);
        }
    }

    public static async Task<OverlayScan> ScanOnceAsync(AppSettings settings, ICommandRunner runner, CancellationToken cancellationToken = default)
    {
        var repositories = settings.RecentRepositories
            .Where(repo => Enum.IsDefined(repo.VcsKind) && IsLocalRepository(repo.Path))
            .DistinctBy(repo => Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo.Path)), StringComparer.OrdinalIgnoreCase)
            .Take(12).ToArray();
        using var concurrency = new SemaphoreSlim(4);
        var results = await Task.WhenAll(repositories.Select(async repository =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(TimeSpan.FromSeconds(20));
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository.Path));
                var provider = CreateProvider(repository.VcsKind, settings, runner);
                var current = await provider.GetRepositoryAsync(root, limit.Token);
                if (!Path.TrimEndingDirectorySeparator(current.RootPath).Equals(root, StringComparison.OrdinalIgnoreCase))
                    throw new VcsException("저장소 루트가 변경되었습니다. MoaVCS에서 다시 열어 주세요.");
                var changes = await provider.GetStatusAsync(root, limit.Token);
                return (Root: root, Entries: ExplorerOverlaySnapshot.Build(root, changes), Warning: (string?)null, Time: DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is VcsException or IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException or System.Xml.XmlException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reason = ex is OperationCanceledException ? "상태 조회가 20초를 초과했습니다." : ex.Message;
                return (Root: repository.Path, Entries: (IReadOnlyList<ExplorerOverlayEntry>)[],
                    Warning: (string?)($"{repository.VcsKind} · {repository.Path}: " + reason[..Math.Min(reason.Length, 1000)]), Time: DateTime.UtcNow);
            }
            finally { concurrency.Release(); }
        }));
        var entries = new Dictionary<string, ExplorerOverlayEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results.Where(result => DateTime.UtcNow - result.Time < TimeSpan.FromSeconds(60)))
        foreach (var entry in result.Entries)
        {
            // An explicitly opened nested checkout owns its paths, even when it is clean or unavailable.
            if (results.Any(other => !other.Root.Equals(result.Root, StringComparison.OrdinalIgnoreCase) &&
                IsWithin(result.Root, other.Root) && IsWithin(other.Root, entry.Path))) continue;
            entries[entry.Path] = entry;
        }
        return new OverlayScan(entries.Values.ToArray(), results.Where(result => result.Warning is not null).Select(result => result.Warning!).ToArray());
    }

    public static void WriteCache(string path, IReadOnlyList<ExplorerOverlayEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ExplorerOverlaySnapshot.Write(stream, DateTime.UtcNow, entries);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static IReadOnlyList<string> ChangedPaths(IReadOnlyList<ExplorerOverlayEntry> before, IReadOnlyList<ExplorerOverlayEntry> after)
    {
        var oldStates = before.ToDictionary(entry => entry.Path, entry => entry.State, StringComparer.OrdinalIgnoreCase);
        var newStates = after.ToDictionary(entry => entry.Path, entry => entry.State, StringComparer.OrdinalIgnoreCase);
        return oldStates.Keys.Union(newStates.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(path => oldStates.GetValueOrDefault(path) != newStates.GetValueOrDefault(path)).ToArray();
    }

    private static bool IsLocalRepository(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            return new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Fixed && Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool IsWithin(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static IVcsProvider CreateProvider(VcsKind kind, AppSettings settings, ICommandRunner runner)
    {
        var executable = VcsTools.ResolveExecutable(kind, settings.ExecutablePaths.GetValueOrDefault(kind));
        return kind switch
        {
            VcsKind.Git => new GitProvider(runner, executable),
            VcsKind.Svn => new SvnProvider(runner, executable),
            VcsKind.Perforce => new PerforceProvider(runner, executable),
            VcsKind.Lore => new LoreProvider(runner, executable),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static void WriteWarnings(IReadOnlyList<string> warnings)
    {
        try
        {
            File.WriteAllText(Path.Combine(ExplorerOverlays.CacheDirectory, "status.json"),
                JsonSerializer.Serialize(new { Updated = DateTimeOffset.UtcNow, Warnings = warnings }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("상태 배지 진단 파일을 저장하지 못했습니다: " + ex.Message);
        }
    }
}
