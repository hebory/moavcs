using MoaVcs.App.Services;
using MoaVcs.Core;

namespace MoaVcs.Tests;

public static class RepositoryHistoryTests
{
    public static void Run()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".local", "repository-history-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckLegacyMigrationAndPinRetention(root);
            CheckInvalidEntryDoesNotResetHistory(root);
            CheckPinRemoveAndClear(root);
            CheckRecentLimit(root);
            CheckStaleInstancesAndOtherSettings(root);
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".local")) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(root);
            if (target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Delete(target, recursive: true);
        }
    }

    private static void CheckInvalidEntryDoesNotResetHistory(string root)
    {
        var file = Path.Combine(root, "invalid-entry-settings.json");
        var valid = Escape(Path.Combine(root, "valid-entry"));
        File.WriteAllText(file, "{\"RecentRepositories\":[{\"Path\":\"" + valid + "\",\"VcsKind\":\"Git\"},{\"Path\":\"\\u0000bad\",\"VcsKind\":\"Git\"}]}");

        var store = new SettingsStore(file);
        var loaded = new RepositoryHistory(store).Load();
        Check(store.LoadWarning is null && loaded.Count == 1 && loaded[0].Path == Path.GetFullPath(Path.Combine(root, "valid-entry")),
            "one invalid path is skipped without resetting valid settings");
    }

    private static void CheckLegacyMigrationAndPinRetention(string root)
    {
        var file = Path.Combine(root, "legacy-settings.json");
        var entries = Enumerable.Range(0, 14)
            .Select(index => "{\"Path\":\"" + Escape(Path.Combine(root, "legacy-" + index)) + "\",\"VcsKind\":\"Git\"}")
            .Append("{\"Path\":\"" + Escape(Path.Combine(root, "legacy-pin")) + "\",\"VcsKind\":\"Git\",\"IsPinned\":true}");
        File.WriteAllText(file, "{\"RecentRepositories\":[" + string.Join(',', entries) + "]}");

        var loaded = new RepositoryHistory(new SettingsStore(file)).Load();
        Check(loaded.Count == 13, "legacy history retains twelve recent entries and every pinned entry");
        Check(loaded.All(repository => repository.Path == Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository.Path))),
            "history paths are normalized to full paths without trailing separators");
        Check(loaded.Count(repository => repository.IsPinned) == 1 && loaded.Single(repository => repository.IsPinned).Path.EndsWith("legacy-pin", StringComparison.Ordinal),
            "legacy entries default to unpinned and pinned entries survive the recent cap");
    }

    private static void CheckPinRemoveAndClear(string root)
    {
        var file = Path.Combine(root, "pin-settings.json");
        var history = new RepositoryHistory(new SettingsStore(file));
        var pinned = Path.Combine(root, "repo");
        var sibling = Path.Combine(root, "repo-copy");
        var recent = Path.Combine(root, "recent");
        history.Remember(pinned, VcsKind.Git);
        history.Remember(sibling, VcsKind.Svn);
        history.Remember(recent, VcsKind.Lore);
        history.SetPinned(pinned + Path.DirectorySeparatorChar, true);
        var afterPin = history.Load();
        Check(afterPin.Select(repository => repository.Path).SequenceEqual([Path.GetFullPath(recent), Path.GetFullPath(sibling), Path.GetFullPath(pinned)]) &&
              afterPin.Last().IsPinned,
            "pinning accepts path variants and preserves global recency order");
        history.Remember(pinned, VcsKind.Svn);
        Check(history.Load().First().Path == Path.GetFullPath(pinned) && history.Load().First().IsPinned &&
              history.Load().First().VcsKind == VcsKind.Svn,
            "remembering a pinned repository promotes it to most recent without clearing its pin");

        history.Remove(pinned);
        var afterRemove = history.Load();
        Check(afterRemove.All(repository => !repository.Path.Equals(Path.GetFullPath(pinned), StringComparison.OrdinalIgnoreCase)) &&
              afterRemove.Any(repository => repository.Path.Equals(Path.GetFullPath(sibling), StringComparison.OrdinalIgnoreCase)),
            "remove targets one normalized path and leaves similarly named paths intact");

        history.SetPinned(sibling, true);
        history.ClearRecent();
        Check(history.Load().Count == 1 && history.Load().Single().IsPinned && history.Load().Single().Path == Path.GetFullPath(sibling),
            "clear recent keeps pinned entries only");
        history.SetPinned(sibling, false);
        Check(history.Load().Count == 1 && !history.Load().Single().IsPinned, "unpinning returns an entry to recent history");
    }

    private static void CheckRecentLimit(string root)
    {
        var history = new RepositoryHistory(new SettingsStore(Path.Combine(root, "limit-settings.json")));
        for (var index = 0; index < 15; index++) history.Remember(Path.Combine(root, "limit-" + index), VcsKind.Git);
        var loaded = history.Load();
        Check(loaded.Count == 12 && loaded[0].Path.EndsWith("limit-14", StringComparison.Ordinal),
            "history keeps the twelve newest unpinned repositories");
    }

    private static void CheckStaleInstancesAndOtherSettings(string root)
    {
        var file = Path.Combine(root, "shared-settings.json");
        var store = new SettingsStore(file);
        var removed = Path.Combine(root, "removed");
        store.Save(new AppSettings
        {
            RecentRepositories = [new RecentRepository(removed, VcsKind.Git)],
            ExecutablePaths = new Dictionary<VcsKind, string> { [VcsKind.Perforce] = "p4.exe" },
            ExplorerOverlaysEnabled = true,
            ExplorerOverlaysPreferPriority = true
        });

        var firstWindow = new RepositoryHistory(new SettingsStore(file));
        var secondWindow = new RepositoryHistory(new SettingsStore(file));
        firstWindow.Remove(removed);
        secondWindow.Remember(Path.Combine(root, "added-later"), VcsKind.Svn);

        var saved = store.Load();
        Check(saved.RecentRepositories.Count == 1 && saved.RecentRepositories.Single().Path.EndsWith("added-later", StringComparison.Ordinal),
            "a stale history instance does not restore a repository removed by another window");
        Check(saved.ExecutablePaths[VcsKind.Perforce] == "p4.exe" && saved.ExplorerOverlaysEnabled && saved.ExplorerOverlaysPreferPriority,
            "history updates preserve unrelated application settings");
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Repository history check failed: " + message);
    }
}
