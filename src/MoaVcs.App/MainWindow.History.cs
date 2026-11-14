using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MoaVcs.Core;

namespace MoaVcs.App;

public partial class MainWindow
{
    private CancellationTokenSource? _historyDetailOperation;
    private CancellationTokenSource? _historyDiffOperation;
    private HistoryEntry? _activeHistoryEntry;
    private string? _activeHistoryRoot;
    private IVcsProvider? _activeHistoryProvider;
    private bool _settingHistorySelection;
    private bool _settingHistoryFiles;

    private void ResetHistoryOverview(IVcsProvider provider)
    {
        HistoryOverview.Visibility = Visibility.Collapsed;
        HistoryScopeSelector.Visibility = provider.Kind == VcsKind.Git ? Visibility.Visible : Visibility.Collapsed;
        HistoryScopeSelector.IsEnabled = provider.Kind == VcsKind.Git;
        HistoryCurrentBranch.Text = "";
        HistoryHeadId.Text = "";
        HistoryUpstream.Text = "";
        HistoryPushTarget.Text = "";
        HistorySyncStatus.Text = "이력 탭을 열면 저장소 이력 정보를 표시합니다.";
        foreach (var column in HistoryGrid.Columns.Where(column => column.Header is "그래프" or "브랜치 / 태그" or "관계"))
            column.Visibility = provider.Kind == VcsKind.Git ? Visibility.Visible : Visibility.Collapsed;
        HistoryDetailRefs.Visibility = provider.Kind == VcsKind.Git ? Visibility.Visible : Visibility.Collapsed;
        HistoryDetailParents.Visibility = provider.Kind == VcsKind.Git ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateHistoryOverview(HistorySnapshot snapshot)
    {
        var git = snapshot.Git;
        var isGit = git is not null;
        HistoryOverview.Visibility = isGit ? Visibility.Visible : Visibility.Collapsed;
        HistoryScopeSelector.Visibility = isGit ? Visibility.Visible : Visibility.Collapsed;
        foreach (var column in HistoryGrid.Columns.Where(column => column.Header is "그래프" or "브랜치 / 태그" or "관계"))
            column.Visibility = isGit ? Visibility.Visible : Visibility.Collapsed;
        HistoryDetailRefs.Visibility = isGit ? Visibility.Visible : Visibility.Collapsed;
        HistoryDetailParents.Visibility = isGit ? Visibility.Visible : Visibility.Collapsed;
        if (git is null)
        {
            HistorySyncStatus.Text = snapshot.ScopeLabel;
            HistorySyncStatus.ToolTip = snapshot.ScopeLabel;
            return;
        }

        var branch = string.IsNullOrWhiteSpace(git.CurrentBranch) ? (git.IsDetached ? "분리된 HEAD" : "브랜치 정보 없음") : git.CurrentBranch;
        if (git.IsUnborn) branch += " · 첫 커밋 전";
        var head = git.IsUnborn ? "없음" : ShortId(git.HeadId);
        var upstream = string.IsNullOrWhiteSpace(git.Upstream) ? "추적 브랜치 없음" : git.Upstream;
        if (!string.IsNullOrWhiteSpace(git.UpstreamId)) upstream += " · " + ShortId(git.UpstreamId);
        var pushTarget = string.IsNullOrWhiteSpace(git.PushTarget) ? "대상 확인 불가" : git.PushTarget;
        if (!string.IsNullOrWhiteSpace(git.PushTargetId)) pushTarget += " · " + ShortId(git.PushTargetId);
        HistoryCurrentBranch.Text = branch;
        HistoryCurrentBranch.ToolTip = git.CurrentBranch;
        HistoryHeadId.Text = head;
        HistoryHeadId.ToolTip = string.IsNullOrWhiteSpace(git.HeadId) ? "아직 커밋이 없습니다." : git.HeadId;
        HistoryUpstream.Text = upstream;
        HistoryUpstream.ToolTip = string.IsNullOrWhiteSpace(git.Upstream)
            ? "현재 브랜치에 추적 브랜치(upstream)가 설정되지 않았습니다."
            : $"마지막 Fetch에서 로컬에 저장한 추적 참조입니다.\n{git.Upstream}\n{git.UpstreamId}";
        HistoryPushTarget.Text = pushTarget;
        HistoryPushTarget.ToolTip = string.IsNullOrWhiteSpace(git.PushTarget)
            ? "현재 Git push 설정으로 하나의 대상을 계산할 수 없습니다. 별도 push refspec 또는 push.default 설정일 수 있습니다."
            : $"현재 로컬 push 설정 대상입니다.\n{git.PushTarget}\n{git.PushTargetId}";

        var status = git.IsDetached ? "마지막 Fetch 기준 · 분리된 HEAD에는 추적 브랜치가 없습니다." :
            git.IsUnborn ? "마지막 Fetch 기준 · 아직 첫 커밋이 없습니다." :
            string.IsNullOrWhiteSpace(git.Upstream) ? "마지막 Fetch 기준 · 추적 브랜치가 설정되지 않았습니다." :
            git.Ahead is int ahead && git.Behind is int behind
                ? $"마지막 Fetch 기준 · 로컬에만 {ahead}개 · 추적 브랜치에만 {behind}개"
                : "마지막 Fetch 기준 · 추적 브랜치 비교 정보가 없습니다.";
        if (git.IsShallow) status += " · shallow clone이라 전체 이력이 없을 수 있습니다.";
        if (!string.IsNullOrWhiteSpace(git.Status)) status += " · " + git.Status;
        HistorySyncStatus.Text = status;
        HistorySyncStatus.ToolTip = "upstream·ahead/behind는 마지막 Fetch 때 로컬에 저장된 원격 추적 ref 기준입니다. 원격 서버의 실시간 상태가 필요하면 저장소 메뉴에서 Fetch 하세요.";
    }

    private static string BuildHistoryHint(HistorySnapshot snapshot)
    {
        var count = snapshot.Entries.Count == 0 ? "아직 표시할 커밋이 없습니다." : $"{snapshot.Entries.Count}개 기록";
        var limit = snapshot.IsTruncated ? " · 표시 개수 제한에 걸려 더 오래된 이력은 생략되었습니다." : "";
        return string.Join(" · ", new[] { snapshot.ScopeLabel, count + limit }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string ShortId(string id) => string.IsNullOrWhiteSpace(id) ? "" : id[..Math.Min(12, id.Length)];

    private static string ReferenceDescription(HistoryEntry entry) => string.Join(" · ", entry.References.Select(reference =>
        reference.Kind == HistoryReferenceKind.Head ? "HEAD" : reference.Kind == HistoryReferenceKind.Tag ? "tag: " + reference.Name : reference.Name));

    private void SetHistoryEntryMetadata(HistoryEntry entry, HistoryEntry? detail = null)
    {
        var author = detail ?? entry;
        var metadata = new List<string>();
        var authorName = string.IsNullOrWhiteSpace(author.Author) ? entry.Author : author.Author;
        var authorEmail = string.IsNullOrWhiteSpace(author.AuthorEmail) ? entry.AuthorEmail : author.AuthorEmail;
        var authorLabel = string.IsNullOrWhiteSpace(authorEmail) ? authorName : $"{authorName} <{authorEmail}>";
        var authorMetadata = string.IsNullOrWhiteSpace(authorLabel) ? "" : "작성자: " + authorLabel;
        var authored = string.IsNullOrWhiteSpace(author.Date) ? entry.Date : author.Date;
        if (!string.IsNullOrWhiteSpace(authored)) authorMetadata += (authorMetadata.Length > 0 ? " · " : "작성일: ") + authored;
        if (!string.IsNullOrWhiteSpace(authorMetadata)) metadata.Add(authorMetadata);

        var committer = string.IsNullOrWhiteSpace(author.Committer) ? entry.Committer : author.Committer;
        var committerEmail = string.IsNullOrWhiteSpace(author.CommitterEmail) ? entry.CommitterEmail : author.CommitterEmail;
        if (!string.IsNullOrWhiteSpace(committer))
        {
            var committerLabel = string.IsNullOrWhiteSpace(committerEmail) ? committer : $"{committer} <{committerEmail}>";
            var committerMetadata = "커미터: " + committerLabel;
            var committed = string.IsNullOrWhiteSpace(author.CommitDate) ? entry.CommitDate : author.CommitDate;
            if (!string.IsNullOrWhiteSpace(committed)) committerMetadata += " · " + committed;
            metadata.Add(committerMetadata);
        }
        else
        {
            var committed = string.IsNullOrWhiteSpace(author.CommitDate) ? entry.CommitDate : author.CommitDate;
            if (!string.IsNullOrWhiteSpace(committed)) metadata.Add("커밋일: " + committed);
        }
        HistoryDetailMetadata.Text = string.Join(Environment.NewLine, metadata);
        HistoryDetailMetadata.ToolTip = string.Join(Environment.NewLine, metadata);

        var refs = ReferenceDescription(entry);
        HistoryDetailRefs.Text = string.IsNullOrWhiteSpace(refs) ? "참조: 없음" : "참조: " + refs;
        HistoryDetailRefs.ToolTip = HistoryDetailRefs.Text;
        var parents = entry.ParentIds;
        HistoryDetailParents.Text = parents.Count == 0 ? "부모 커밋: 없음 (첫 커밋)" : "부모 커밋: " + string.Join(", ", parents.Select(ShortId));
        HistoryDetailParents.ToolTip = parents.Count == 0 ? HistoryDetailParents.Text : string.Join(Environment.NewLine, parents);
    }

    private void ResetHistoryDetails(string status = "커밋을 선택하면 변경 파일과 내용을 볼 수 있습니다.")
    {
        _historyDetailOperation?.Cancel();
        _historyDetailOperation = null;
        _historyDiffOperation?.Cancel();
        _historyDiffOperation = null;
        _activeHistoryEntry = null;
        _activeHistoryRoot = null;
        _activeHistoryProvider = null;
        _settingHistoryFiles = true;
        HistoryFilesGrid.ItemsSource = null;
        HistoryFilesGrid.SelectedItem = null;
        _settingHistoryFiles = false;
        HistoryFilesEmpty.Visibility = Visibility.Visible;
        HistoryFilesEmpty.Text = status;
        HistoryDetailRevision.Text = "커밋 세부 내역";
        HistoryDetailMetadata.Text = "";
        HistoryDetailMetadata.ToolTip = null;
        HistoryDetailRefs.Text = "";
        HistoryDetailRefs.ToolTip = null;
        HistoryDetailParents.Text = "";
        HistoryDetailParents.ToolTip = null;
        HistoryDetailMessage.Text = "";
        HistoryComparisonLabel.Text = "";
        HistoryDiffPath.Text = "";
        RenderDiff(HistoryDiffViewer, status);
        HistoryDetailStatus.Text = status;
    }

    private bool IsCurrentHistory(IVcsProvider provider, string root, HistoryEntry entry) =>
        ReferenceEquals(_provider, provider) &&
        string.Equals(_repository?.RootPath, root, StringComparison.OrdinalIgnoreCase) &&
        HistoryGrid.SelectedItem is HistoryEntry selected && selected.Id == entry.Id;

    private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, HistoryGrid) || _settingHistorySelection) return;
        if (HistoryGrid.SelectedItem is HistoryEntry entry)
            _ = LoadHistoryDetailsAsync(entry);
        else
            ResetHistoryDetails();
    }

    private async Task LoadHistoryDetailsAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        if (_provider is not { } provider || _repository is not { } repository ||
            !provider.Capabilities.HasFlag(VcsCapabilities.History))
        {
            ResetHistoryDetails("이 저장소에서는 커밋 세부 내역을 볼 수 없습니다.");
            return;
        }

        _historyDetailOperation?.Cancel();
        _historyDiffOperation?.Cancel();
        _activeHistoryEntry = null;
        _activeHistoryRoot = null;
        _activeHistoryProvider = null;
        using var cancellation = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        _historyDetailOperation = cancellation;
        var root = repository.RootPath;
        HistoryDetailRevision.Text = $"리비전 {entry.Id}";
        HistoryDetailRevision.ToolTip = $"전체 리비전: {entry.Id} · 선택해 복사할 수 있습니다.";
        SetHistoryEntryMetadata(entry);
        HistoryDetailMessage.Text = entry.Message;
        HistoryComparisonLabel.Text = "";
        HistoryDiffPath.Text = "";
        _settingHistoryFiles = true;
        HistoryFilesGrid.ItemsSource = null;
        HistoryFilesGrid.SelectedItem = null;
        _settingHistoryFiles = false;
        HistoryFilesEmpty.Visibility = Visibility.Visible;
        HistoryFilesEmpty.Text = "변경 파일을 불러오는 중…";
        HistoryDetailStatus.Text = "세부 내역을 불러오는 중…";
        RenderDiff(HistoryDiffViewer, "변경 파일을 불러오는 중…");

        try
        {
            var details = await provider.GetHistoryDetailsAsync(root, entry.Id, cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsCurrentHistory(provider, root, entry)) return;

            HistoryDetailRevision.Text = $"리비전 {details.Entry.Id}";
            HistoryDetailRevision.ToolTip = $"전체 리비전: {details.Entry.Id} · 선택해 복사할 수 있습니다.";
            SetHistoryEntryMetadata(entry, details.Entry);
            HistoryDetailMessage.Text = details.Entry.Message;
            HistoryComparisonLabel.Text = details.ComparisonLabel;
            _activeHistoryEntry = entry;
            _activeHistoryRoot = root;
            _activeHistoryProvider = provider;
            HistoryFilesGrid.ItemsSource = details.Files;
            HistoryFilesEmpty.Visibility = details.Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryFilesEmpty.Text = details.Files.Count == 0 ? "이 커밋에서 변경한 파일이 없습니다." : "";
            HistoryDetailStatus.Text = details.Files.Count == 0 ? "변경 파일 없음" : $"변경 파일 {details.Files.Count}개";

            _settingHistoryFiles = true;
            HistoryFilesGrid.SelectedItem = details.Files.FirstOrDefault();
            _settingHistoryFiles = false;
            if (details.Files.Count == 0)
            {
                HistoryDiffPath.Text = "";
                RenderDiff(HistoryDiffViewer, "표시할 파일 변경이 없습니다.");
            }
            else
            {
                await LoadHistoryDiffAsync(provider, root, entry, details.Files[0], cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested && IsCurrentHistory(provider, root, entry))
            {
                HistoryDetailStatus.Text = "세부 내역을 불러오지 못했습니다.";
                HistoryFilesEmpty.Text = ex.Message;
                HistoryFilesEmpty.Visibility = Visibility.Visible;
                RenderDiff(HistoryDiffViewer, ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_historyDetailOperation, cancellation))
            {
                _historyDetailOperation = null;
            }
        }
    }

    private void HistoryFilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, HistoryFilesGrid) || _settingHistoryFiles ||
            HistoryGrid.SelectedItem is not HistoryEntry entry || HistoryFilesGrid.SelectedItem is not FileChange file ||
            _activeHistoryEntry?.Id != entry.Id || _activeHistoryProvider is not { } provider || _activeHistoryRoot is not { } root)
            return;
        _ = LoadHistoryDiffAsync(provider, root, entry, file);
    }

    private async Task LoadHistoryDiffAsync(IVcsProvider provider, string root, HistoryEntry entry, FileChange file,
        CancellationToken parentToken = default)
    {
        _historyDiffOperation?.Cancel();
        using var operation = parentToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(parentToken)
            : new CancellationTokenSource();
        _historyDiffOperation = operation;
        HistoryDiffPath.Text = file.Path;
        HistoryDiffPath.ToolTip = file.OriginalPath is { Length: > 0 } oldPath && oldPath != file.Path
            ? $"{oldPath} → {file.Path}" : file.Path;
        RenderDiff(HistoryDiffViewer, "이 커밋의 파일 변경 내용을 불러오는 중…");
        try
        {
            var diff = await provider.GetHistoryDiffAsync(root, entry.Id, file, operation.Token);
            if (!operation.IsCancellationRequested && ReferenceEquals(_activeHistoryEntry, entry) && IsCurrentHistory(provider, root, entry) &&
                ReferenceEquals(_historyDiffOperation, operation) && HistoryFilesGrid.SelectedItem is FileChange selected && selected.Path == file.Path)
                RenderDiff(HistoryDiffViewer, string.IsNullOrWhiteSpace(diff) ? "이 파일의 텍스트 변경 내용이 없습니다." : diff);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!operation.IsCancellationRequested && ReferenceEquals(_activeHistoryEntry, entry) && IsCurrentHistory(provider, root, entry) &&
                ReferenceEquals(_historyDiffOperation, operation) && HistoryFilesGrid.SelectedItem is FileChange selected && selected.Path == file.Path)
                RenderDiff(HistoryDiffViewer, ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_historyDiffOperation, operation)) _historyDiffOperation = null;
        }
    }

    private static void RenderDiff(RichTextBox viewer, string text)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, PageWidth = 2000 };
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var line in lines.Take(2500))
        {
            var paragraph = new Paragraph(new Run(line.Length > 1600 ? line[..1600] + " …" : line)) { Margin = new Thickness(0), LineHeight = 19 };
            if (line.StartsWith('+')) { paragraph.Foreground = new SolidColorBrush(Color.FromRgb(31, 119, 76)); paragraph.Background = new SolidColorBrush(Color.FromRgb(233, 247, 238)); }
            else if (line.StartsWith('-')) { paragraph.Foreground = new SolidColorBrush(Color.FromRgb(176, 59, 53)); paragraph.Background = new SolidColorBrush(Color.FromRgb(255, 239, 236)); }
            else if (line.StartsWith("@@")) { paragraph.Foreground = new SolidColorBrush(Color.FromRgb(58, 101, 160)); paragraph.Background = new SolidColorBrush(Color.FromRgb(237, 243, 251)); }
            else paragraph.Foreground = new SolidColorBrush(Color.FromRgb(83, 99, 119));
            doc.Blocks.Add(paragraph);
        }
        if (lines.Length > 2500) doc.Blocks.Add(new Paragraph(new Run("미리 보기는 2,500줄까지 표시됩니다.")));
        viewer.Document = doc;
    }
}
