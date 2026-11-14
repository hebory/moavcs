using System.IO;
using System.Windows;
using System.Windows.Controls;
using MoaVcs.App.Services;
using MoaVcs.Core;

namespace MoaVcs.App;

public partial class MainWindow
{
    private readonly RepositoryHistory _repositoryHistory;

    private void InitializeRepositoryHistory()
    {
        _settings.RecentRepositories = _repositoryHistory.Load();
    }

    private void RememberRepository(RepositoryInfo repository)
    {
        try
        {
            _repositoryHistory.Remember(repository.RootPath, repository.Kind);
            RefreshRepositoryHistory();
        }
        catch (Exception ex) { ShowNotice("저장소 목록을 갱신하지 못했습니다: " + ex.Message); }
    }

    private void RefreshRepositoryHistory()
    {
        _settings.RecentRepositories = _repositoryHistory.Load();
        UpdateRecentList();
        ExplorerOverlays.Wake();
    }

    private void UpdateRecentList()
    {
        var search = RepositorySearchBox?.Text?.Trim() ?? "";
        var filtered = _settings.RecentRepositories
            .Select(repository => new RecentItem(repository.Path, repository.VcsKind, repository.IsPinned,
                _repository is not null && SameRepositoryPath(repository.Path, _repository.RootPath)))
            .Where(item => MatchesSearch(item, search))
            .ToList();
        var pinned = filtered.Where(item => item.IsPinned).ToList();
        var recent = filtered.Where(item => !item.IsPinned).ToList();

        PinnedList.ItemsSource = pinned;
        RecentList.ItemsSource = recent;
        PinnedHeading.Visibility = pinned.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RecentHeading.Visibility = recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var hasMatches = pinned.Count + recent.Count > 0;
        var hasSearch = search.Length > 0;
        RepositoryListEmpty.Visibility = !hasSearch && _settings.RecentRepositories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RepositorySearchEmpty.Visibility = hasSearch && !hasMatches ? Visibility.Visible : Visibility.Collapsed;
        if (RepositoryListMenuButton.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault() is { } clearRecentMenuItem)
            clearRecentMenuItem.IsEnabled = _settings.RecentRepositories.Any(item => !item.IsPinned);
    }

    private static bool SameRepositoryPath(string left, string right)
    {
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return left.Equals(right, StringComparison.OrdinalIgnoreCase); }
    }

    private static bool MatchesSearch(RecentItem item, string search) =>
        search.Length == 0 || item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        item.Path.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        item.KindLabel.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    private void RepositorySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PinnedList is not null) UpdateRecentList();
    }

    private void RepositoryItemMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.DataContext = button.Tag;
        menu.IsOpen = true;
    }

    private void RecentPin_Click(object sender, RoutedEventArgs e)
    {
        if (GetRecentItem(sender) is not { } item) return;
        try
        {
            _repositoryHistory.SetPinned(item.Path, !item.IsPinned);
            RefreshRepositoryHistory();
        }
        catch (Exception ex) { ShowNotice("저장소 고정 상태를 바꾸지 못했습니다: " + ex.Message); }
    }

    private async void RecentOpenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (GetRecentItem(sender) is { } item) await OpenRepositoryAsync(item.Path, item.Kind);
    }

    private void RecentRemove_Click(object sender, RoutedEventArgs e)
    {
        if (GetRecentItem(sender) is not { } item) return;
        try
        {
            _repositoryHistory.Remove(item.Path);
            RefreshRepositoryHistory();
        }
        catch (Exception ex) { ShowNotice("저장소를 목록에서 제거하지 못했습니다: " + ex.Message); }
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _repositoryHistory.ClearRecent();
            RefreshRepositoryHistory();
        }
        catch (Exception ex) { ShowNotice("최근 저장소 목록을 비우지 못했습니다: " + ex.Message); }
    }

    private static RecentItem? GetRecentItem(object sender) => sender switch
    {
        FrameworkElement { Tag: RecentItem item } => item,
        FrameworkElement { DataContext: RecentItem item } => item,
        _ => null
    };
}

public sealed record RecentItem(string Path, VcsKind Kind, bool IsPinned, bool IsCurrent)
{
    public string Name
    {
        get
        {
            var trimmed = Path.TrimEnd('\\', '/');
            return System.IO.Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
        }
    }

    public string ParentPath => System.IO.Path.GetDirectoryName(Path.TrimEnd('\\', '/')) ?? Path;
    public string ParentLabel
    {
        get
        {
            var parent = ParentPath.TrimEnd('\\', '/');
            if (parent.Length <= 26) return parent;
            var root = System.IO.Path.GetPathRoot(parent) ?? "";
            var name = System.IO.Path.GetFileName(parent);
            return root + "…\\" + name;
        }
    }
    public string KindLabel => ProviderChoice.All.First(p => p.Kind == Kind).Label;
    public string PinMenuLabel => IsPinned ? "고정 해제" : "고정";
}
