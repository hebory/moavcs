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
        HistoryDetailMetadata.Text = string.Join(" · ", new[] { entry.Author, entry.Date }.Where(value => !string.IsNullOrWhiteSpace(value)));
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
            HistoryDetailMetadata.Text = string.Join(" · ", new[] { details.Entry.Author, details.Entry.Date }.Where(value => !string.IsNullOrWhiteSpace(value)));
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
