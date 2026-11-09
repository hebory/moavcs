using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using MoaVcs.Core;

namespace MoaVcs.App;

public sealed class FileInspectionWindow : Window
{
    private readonly Grid _layout = new();
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox _search = new() { Width = 220, ToolTip = "내용 · 작성자 · 리비전 검색", Margin = new Thickness(14, 0, 8, 0) };
    private readonly TextBlock _status = new() { Foreground = Brushes.SlateGray, Margin = new Thickness(22, 8, 22, 8) };
    private DataGrid? _grid;
    private TabControl? _tabs;
    private TabItem? _rowsTab;
    private IReadOnlyList<ComparisonRow>? _comparisonRows;
    private IReadOnlyList<BlameLine>? _blameRows;

    public FileInspectionWindow(FileComparison comparison, LineDiffResult diff, string patch)
    {
        Initialize("좌우 비교", comparison.Path, comparison.LeftLabel + "  →  " + comparison.RightLabel);
        _comparisonRows = diff.Rows;
        _grid = CreateGrid("ComparisonRows");
        _grid.Columns.Add(Column("줄", nameof(ComparisonRow.LeftNumber), 52));
        _grid.Columns.Add(CodeColumn(comparison.LeftLabel, nameof(ComparisonRow.LeftText), true));
        _grid.Columns.Add(Column("줄", nameof(ComparisonRow.RightNumber), 52));
        _grid.Columns.Add(CodeColumn(comparison.RightLabel, nameof(ComparisonRow.RightText), false));
        _grid.ItemsSource = diff.Rows;
        _tabs = new TabControl { BorderThickness = new Thickness(0), Background = Brushes.White };
        _rowsTab = new TabItem { Header = "좌우 비교", Content = _grid };
        _tabs.Items.Add(_rowsTab);
        _tabs.Items.Add(new TabItem { Header = "통합 diff", Content = PatchViewer(patch) });
        _tabs.Items.Add(new TabItem { Header = "이전 내용", Content = PlainText(comparison.LeftText) });
        _tabs.Items.Add(new TabItem { Header = "현재 내용", Content = PlainText(comparison.RightText) });
        SetBody(_tabs);
        AddButton("← 이전 변경", (_, _) => MoveChange(-1));
        AddButton("다음 변경 →", (_, _) => MoveChange(1));
        AddSearch();
        AddButton("패치 저장", (_, _) => SaveText(patch, "diff.patch"));
        var blocks = diff.Rows.Count(r => r.Kind != "unchanged");
        _status.Text = $"변경 행 {blocks:N0}개 · 빨강: 이전 / 초록: 현재 · " +
            $"이전 {LineDiff.LineEndingLabel(comparison.LeftText)} / 현재 {LineDiff.LineEndingLabel(comparison.RightText)}" +
            (diff.Simplified ? " · 변경량이 커서 일부를 교체 블록으로 표시합니다." : "");
    }

    public FileInspectionWindow(string path, string patch)
    {
        Initialize("통합 diff", path, "버전 관리 도구가 제공한 패치 · 읽기 전용");
        SetBody(PatchViewer(patch));
        AddButton("패치 저장", (_, _) => SaveText(patch, "diff.patch"));
        _status.Text = "Ctrl+A / Ctrl+C로 패치 전체를 복사할 수 있습니다.";
    }

    public FileInspectionWindow(BlameResult blame)
    {
        Initialize("Blame · 행별 이력", blame.Path, blame.RevisionLabel + " · 마지막으로 각 줄을 변경한 기록 · 작업 중인 변경은 포함하지 않습니다.");
        _blameRows = blame.Lines;
        _grid = CreateGrid("BlameRows");
        _grid.Columns.Add(Column("줄", nameof(BlameLine.LineNumber), 58));
        var revision = Column("리비전", nameof(BlameLine.Revision), 120);
        revision.Binding = new Binding(nameof(BlameLine.Revision)) { Converter = new RevisionDisplayConverter() };
        _grid.Columns.Add(revision);
        _grid.Columns.Add(Column("작성자", nameof(BlameLine.Author), 150));
        var date = Column("날짜", nameof(BlameLine.Date), 210);
        date.Binding = new Binding(nameof(BlameLine.Date)) { Converter = new DateDisplayConverter() };
        _grid.Columns.Add(date);
        _grid.Columns.Add(Column("내용", nameof(BlameLine.Text), new DataGridLength(1, DataGridLengthUnitType.SizeToCells), 450));
        _grid.ItemsSource = blame.Lines;
        SetBody(_grid);
        AddSearch();
        AddButton("이력 저장", (_, _) => SaveText(ExportBlame(blame), "blame.tsv"));
        _status.Text = $"{blame.Lines.Count:N0}줄 · 행을 선택하고 Ctrl+C로 복사할 수 있습니다.";
    }

    public static string ExportBlame(BlameResult blame)
    {
        static string Cell(string text) => text.Replace("\t", "    ").Replace("\r", " ").Replace("\n", " ");
        return "Line\tRevision\tAuthor\tDate\tText\n" + string.Join('\n', blame.Lines.Select(l =>
            $"{l.LineNumber}\t{Cell(l.Revision)}\t{Cell(l.Author)}\t{Cell(l.Date)}\t{Cell(l.Text)}")) + "\n";
    }

    private void Initialize(string kind, string path, string subtitle)
    {
        NameScope.SetNameScope(this, new NameScope());
        Title = $"{Path.GetFileName(path)} · {kind} · MoaVCS";
        Width = 1320; Height = 820; MinWidth = 940; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        _layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };
        header.Children.Add(new TextBlock { Text = kind, FontSize = 23, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = path, FontSize = 13, Margin = new Thickness(0, 8, 0, 5), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path });
        header.Children.Add(new TextBlock { Text = subtitle, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap });
        _layout.Children.Add(header);
        var toolbarBorder = new Border { Background = Brushes.White, Padding = new Thickness(22, 10, 22, 10), Child = _toolbar };
        Grid.SetRow(toolbarBorder, 1); _layout.Children.Add(toolbarBorder);
        Grid.SetRow(_status, 3); _layout.Children.Add(_status);
        Content = _layout;
    }

    private void SetBody(UIElement element) { Grid.SetRow(element, 2); _layout.Children.Add(element); }
    private void AddButton(string label, RoutedEventHandler handler)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 8, 12, 8) };
        button.Click += handler; _toolbar.Children.Add(button);
    }
    private void AddSearch()
    {
        _toolbar.Children.Add(new TextBlock { Text = "찾을 내용", Margin = new Thickness(12, 0, 0, 0), Foreground = Brushes.SlateGray });
        _search.Name = "InspectionSearch"; RegisterName(_search.Name, _search);
        _toolbar.Children.Add(_search);
        AddButton("다음 찾기", (_, _) => FindNext());
        _search.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; FindNext(); } };
    }
    private DataGrid CreateGrid(string name)
    {
        var grid = new DataGrid { Name = name, RowHeight = 25, FontSize = 12, FontWeight = FontWeights.Normal, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            EnableRowVirtualization = true, EnableColumnVirtualization = true, CanUserSortColumns = false, SelectionMode = DataGridSelectionMode.Extended,
            ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader };
        RegisterName(name, grid);
        return grid;
    }
    private static DataGridTextColumn Column(string title, string property, DataGridLength width, double minWidth = 0)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(ToolTipProperty, new Binding(property)));
        return new DataGridTextColumn { Header = title, Binding = new Binding(property), Width = width, MinWidth = minWidth, ElementStyle = style };
    }
    private static DataGridTextColumn CodeColumn(string title, string property, bool left)
    {
        var column = Column(title, property, new DataGridLength(1, DataGridLengthUnitType.Star), 350);
        var style = new Style(typeof(DataGridCell), (Style)Application.Current.FindResource(typeof(DataGridCell)));
        foreach (var kind in new[] { "modified", left ? "removed" : "added" })
        {
            var trigger = new DataTrigger { Binding = new Binding(nameof(ComparisonRow.Kind)), Value = kind };
            trigger.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString(left ? "#FFECE8" : "#E8F5ED"))));
            trigger.Setters.Add(new Setter(ForegroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString(left ? "#9C3930" : "#216545"))));
            style.Triggers.Add(trigger);
        }
        column.CellStyle = style;
        return column;
    }
    private static TextBox PlainText(string text) => new() { Text = text, IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, FontWeight = FontWeights.Normal, Padding = new Thickness(18), BorderThickness = new Thickness(0),
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.White };
    private static TextBox PatchViewer(string patch) => PlainText(string.IsNullOrEmpty(patch) ? "표시할 차이가 없습니다." : patch);
    private void Select(int index)
    {
        if (_grid is null) return;
        if (_tabs is not null) _tabs.SelectedItem = _rowsTab;
        _grid.SelectedIndex = index; _grid.ScrollIntoView(_grid.SelectedItem);
    }
    private void MoveChange(int direction)
    {
        if (_grid is null || _comparisonRows is null || _comparisonRows.Count == 0) return;
        var count = _comparisonRows.Count;
        // Jump to the first row of the next/previous changed block, wrapping at the ends.
        for (var step = 1; step <= count; step++)
        {
            var index = ((_grid.SelectedIndex < 0 ? (direction > 0 ? -1 : 0) : _grid.SelectedIndex) + direction * step + count * 2) % count;
            if (_comparisonRows[index].Kind != "unchanged" && (index == 0 || _comparisonRows[index - 1].Kind == "unchanged")) { Select(index); return; }
        }
    }
    private void FindNext()
    {
        if (_grid is null || string.IsNullOrWhiteSpace(_search.Text)) return;
        for (var step = 1; step <= _grid.Items.Count; step++)
        {
            var index = (_grid.SelectedIndex + step) % _grid.Items.Count;
            var text = _comparisonRows is not null ? _comparisonRows[index].LeftText + "\n" + _comparisonRows[index].RightText :
                _blameRows is not null ? $"{_blameRows[index].Revision} {_blameRows[index].Author} {_blameRows[index].Text}" : "";
            if (text.Contains(_search.Text, StringComparison.OrdinalIgnoreCase)) { Select(index); return; }
        }
        _status.Text = "검색 결과가 없습니다: " + _search.Text;
    }
    private void SaveText(string text, string name)
    {
        var picker = new SaveFileDialog { FileName = name, Filter = "텍스트 파일|*.*" };
        if (picker.ShowDialog(this) != true) return;
        try { File.WriteAllText(picker.FileName, text, new System.Text.UTF8Encoding(false)); _status.Text = "저장했습니다: " + picker.FileName; }
        catch (Exception ex) { _status.Text = "저장하지 못했습니다: " + ex.Message; }
    }
    private sealed class RevisionDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            value is string { Length: > 12 } text ? text[..12] : value;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
    private sealed class DateDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            value is string text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
                ? date.ToString("yyyy-MM-dd HH:mm zzz") : value;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}
