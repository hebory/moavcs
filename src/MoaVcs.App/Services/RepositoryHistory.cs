using System.IO;
using System.Security.Cryptography;
using System.Text;
using MoaVcs.Core;

namespace MoaVcs.App.Services;

/// <summary>Maintains the recent and pinned repository list in the shared settings file.</summary>
public sealed class RepositoryHistory
{
    private const int RecentLimit = 12;
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(5);
    private readonly SettingsStore _store;
    private readonly string _mutexName;

    public RepositoryHistory(SettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        var settingsPath = Path.GetFullPath(store.FilePath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settingsPath)));
        _mutexName = "Local\\MoaVcs.RepositoryHistory." + hash;
    }

    public List<RecentRepository> Load()
    {
        var settings = _store.Load();
        return Normalize(settings.RecentRepositories);
    }

    public void Remember(string path, VcsKind kind)
    {
        var normalizedPath = NormalizePath(path);
        Mutate(repositories =>
        {
            var existing = repositories.FirstOrDefault(r => SamePath(r.Path, normalizedPath));
            var pinned = existing?.IsPinned ?? false;
            repositories.RemoveAll(r => SamePath(r.Path, normalizedPath));
            var entry = new RecentRepository(normalizedPath, kind, pinned);
            repositories.Insert(0, entry);
            return Normalize(repositories);
        });
    }

    public void SetPinned(string path, bool pinned)
    {
        var normalizedPath = NormalizePath(path);
        Mutate(repositories =>
        {
            var index = repositories.FindIndex(r => SamePath(r.Path, normalizedPath));
            if (index < 0 || repositories[index].IsPinned == pinned) return repositories;

            repositories[index] = repositories[index] with { IsPinned = pinned };
            return Normalize(repositories);
        });
    }

    public void Remove(string path)
    {
        var normalizedPath = NormalizePath(path);
        Mutate(repositories =>
        {
            var removed = repositories.RemoveAll(r => SamePath(r.Path, normalizedPath));
            return removed == 0 ? repositories : Normalize(repositories);
        });
    }

    public void ClearRecent() => Mutate(repositories => repositories.Where(r => r.IsPinned).ToList());

    internal static List<RecentRepository> Normalize(IEnumerable<RecentRepository>? repositories)
    {
        if (repositories is null) return [];

        var unique = new List<RecentRepository>();
        var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in repositories)
        {
            if (repository is null || string.IsNullOrWhiteSpace(repository.Path) || !Enum.IsDefined(repository.VcsKind)) continue;
            string path;
            try { path = NormalizePath(repository.Path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }

            if (indices.TryGetValue(path, out var existingIndex))
            {
                if (repository.IsPinned && !unique[existingIndex].IsPinned)
                    unique[existingIndex] = unique[existingIndex] with { IsPinned = true };
                continue;
            }

            indices.Add(path, unique.Count);
            unique.Add(repository with { Path = path });
        }

        var recentCount = 0;
        return unique.Where(repository => repository.IsPinned || recentCount++ < RecentLimit).ToList();
    }

    private void Mutate(Func<List<RecentRepository>, List<RecentRepository>> update)
    {
        using var mutex = new Mutex(initiallyOwned: false, _mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(MutexTimeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("최근 저장소 목록이 다른 창에서 사용 중입니다. 잠시 후 다시 시도해 주세요.");

            _store.UpdateRecentRepositories(update);
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(NormalizePath(left), right, StringComparison.OrdinalIgnoreCase);
}
