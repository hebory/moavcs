using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoaVcs.Core;

namespace MoaVcs.App;

/// <summary>Guided editor for one version-control conflict file.</summary>
public sealed class ConflictResolutionView : UserControl
{
    private readonly IVcsProvider _provider;
    private ConflictFile _snapshot;
    private readonly TextBox _result;
    private readonly ComboBox _blocks;
    private readonly Button _useLeft;
    private readonly Button _useRight;
    private readonly Button _useBoth;
    private readonly Button _save;
    private readonly Button _resolve;
    private readonly TextBlock _status;
    private readonly TextBox _left;
    private readonly TextBox _right;
    private readonly TextBox _base;
    private IReadOnlyList<ConflictBlock> _parsedBlocks = [];
    private bool _busy;
    private bool _resolved;
    private bool _updatingResult;
    private bool _updatingBlocks;

    public event EventHandler? FileChanged;

    public bool HasUnsavedChanges => !SameText(_result.Text, _snapshot.WorkingText);
    public bool IsBusy => _busy;

    public ConflictResolutionView(IVcsProvider provider, ConflictFile snapshot)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

        NameScope.SetNameScope(this, new NameScope());
        _blocks = new ComboBox { Name = "ConflictBlocks", Width = 210, MaxWidth = 210, Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "이동할 충돌 블록을 선택합니다. 왼쪽·오른쪽 적용은 이 블록만 바꿉니다." };
        RegisterName(_blocks.Name, _blocks);
        _blocks.SelectionChanged += (_, _) =>
        {
            if (_updatingBlocks) return;
            SelectBlockInResult();
            UpdateActions();
        };

        _useLeft = MakeButton("왼쪽 적용", "ConflictUseLeft", (_, _) => UseSelectedBlock(ConflictSide.Left));
        _useRight = MakeButton("오른쪽 적용", "ConflictUseRight", (_, _) => UseSelectedBlock(ConflictSide.Right));
        _useBoth = MakeButton("양쪽 이어 붙이기", "ConflictUseBoth", (_, _) => UseSelectedBlock(ConflictSide.Both));
        _save = MakeButton("해결 내용 저장", "ConflictSave", Save_Click);
        _resolve = MakeButton("해결 완료 표시", "ConflictResolve", Resolve_Click);

        _left = MakeReadOnlyTextBox("ConflictLeftText");
        _right = MakeReadOnlyTextBox("ConflictRightText");
        _base = MakeReadOnlyTextBox("ConflictBaseText");
        var sourceTabs = new TabControl { Name = "ConflictSides", MinWidth = 0 };
        RegisterName(sourceTabs.Name, sourceTabs);
        var sideBySide = new Grid();
        sideBySide.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
        sideBySide.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
        var leftPanel = new DockPanel();
        var leftLabel = new TextBlock { Text = _snapshot.LeftLabel, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(10, 7, 10, 7), Background = new SolidColorBrush(Color.FromRgb(255, 236, 232)) };
        DockPanel.SetDock(leftLabel, Dock.Top);
        leftPanel.Children.Add(leftLabel);
        leftPanel.Children.Add(_left);
        var rightPanel = new DockPanel();
        var rightLabel = new TextBlock { Text = _snapshot.RightLabel, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(10, 7, 10, 7), Background = new SolidColorBrush(Color.FromRgb(232, 245, 237)) };
        DockPanel.SetDock(rightLabel, Dock.Top);
        rightPanel.Children.Add(rightLabel);
        rightPanel.Children.Add(_right);
        Grid.SetColumn(leftPanel, 0); sideBySide.Children.Add(leftPanel);
        Grid.SetColumn(rightPanel, 1); sideBySide.Children.Add(rightPanel);
        sourceTabs.Items.Add(new TabItem { Header = "왼쪽 / 오른쪽", Content = sideBySide });
        if (_snapshot.BaseText is not null)
            sourceTabs.Items.Add(new TabItem { Header = "공통 기준", Content = _base });

        _result = new TextBox
        {
            Name = "ConflictResult", AcceptsReturn = true, AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
            FontWeight = FontWeights.Normal, VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(12), BorderBrush = new SolidColorBrush(Color.FromRgb(222, 228, 236)),
            Background = Brushes.White, Text = _snapshot.WorkingText
        };
        RegisterName(_result.Name, _result);
        _result.TextChanged += (_, _) =>
        {
            if (_updatingResult) return;
            ParseDraft();
            UpdateActions();
        };

        _status = new TextBlock
        {
            Name = "ConflictStatus", Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        };
        RegisterName(_status.Name, _status);

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(sourceTabs, 1);
        root.Children.Add(sourceTabs);
        var splitter = new GridSplitter
        {
            Name = "ConflictSplitter", Height = 8, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center, ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext, Background = new SolidColorBrush(Color.FromRgb(222, 228, 236)),
            ShowsPreview = false, ToolTip = "비교 내용과 결과 편집 영역의 높이를 조절합니다."
        };
        RegisterName(splitter.Name, splitter);
        Grid.SetRow(splitter, 2); root.Children.Add(splitter);
        var resultPanel = new DockPanel { Margin = new Thickness(0, 5, 0, 8) };
        var resultLabel = new TextBlock
        {
            Text = "전체 결과 직접 편집 · 위의 왼쪽 / 오른쪽 적용은 선택한 충돌 블록만 바꿉니다.",
            FontWeight = FontWeights.SemiBold, Foreground = Brushes.SlateGray,
            Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(resultLabel, Dock.Top); resultPanel.Children.Add(resultLabel);
        resultPanel.Children.Add(_result);
        Grid.SetRow(resultPanel, 3); root.Children.Add(resultPanel);

        var top = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
        var blockLabel = new TextBlock { Text = "충돌 블록", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(blockLabel, Dock.Left); top.Children.Add(blockLabel);
        DockPanel.SetDock(_blocks, Dock.Left); top.Children.Add(_blocks);
        AddTo(top, MakeButton("이전", "ConflictPrevious", (_, _) => MoveBlock(-1)), Dock.Left);
        AddTo(top, MakeButton("다음", "ConflictNext", (_, _) => MoveBlock(1)), Dock.Left);
        var choices = new StackPanel { Orientation = Orientation.Horizontal };
        choices.Children.Add(_useLeft);
        choices.Children.Add(_useRight);
        choices.Children.Add(_useBoth);
        AddTo(top, choices, Dock.Right);
        Grid.SetRow(top, 0);
        root.Children.Add(top);

        var bottom = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.Children.Add(_status);
        Grid.SetColumn(_save, 1); bottom.Children.Add(_save);
        Grid.SetColumn(_resolve, 2); bottom.Children.Add(_resolve);
        Grid.SetRow(bottom, 4);
        root.Children.Add(bottom);

        Content = root;
        _updatingResult = true;
        _left.Text = _snapshot.LeftText;
        _right.Text = _snapshot.RightText;
        _base.Text = _snapshot.BaseText ?? "공통 기준본이 없습니다.";
        _updatingResult = false;
        ParseDraft();
        SelectBlockInResult();
        UpdateActions();
    }

    private enum ConflictSide { Left, Right, Both }

    private Button MakeButton(string label, string name, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Name = name, Content = label, Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(9, 6, 9, 6), VerticalAlignment = VerticalAlignment.Center
        };
        RegisterName(name, button);
        button.Click += handler;
        return button;
    }

    private TextBox MakeReadOnlyTextBox(string name)
    {
        var box = new TextBox
        {
            Name = name, IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
            FontWeight = FontWeights.Normal, VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(12), BorderThickness = new Thickness(0), Background = Brushes.White
        };
        RegisterName(name, box);
        return box;
    }

    private static void AddTo(DockPanel panel, UIElement child, Dock dock)
    {
        DockPanel.SetDock(child, dock);
        panel.Children.Add(child);
    }

    private void ParseDraft()
    {
        _updatingBlocks = true;
        try
        {
            _parsedBlocks = ConflictMarkers.Parse(_result.Text);
            var oldIndex = _blocks.SelectedIndex;
            _blocks.ItemsSource = _parsedBlocks.Select((block, index) => $"충돌 {index + 1} · {block.LeftLabel} / {block.RightLabel}").ToArray();
            if (_parsedBlocks.Count > 0)
                _blocks.SelectedIndex = Math.Clamp(oldIndex, 0, _parsedBlocks.Count - 1);
            else
                _blocks.SelectedIndex = -1;
            _status.Text = _parsedBlocks.Count > 0
                ? $"{_parsedBlocks.Count}개 충돌 블록이 남아 있습니다. 블록을 선택해 한쪽을 적용하거나 결과를 직접 편집하세요."
                : ConflictMarkers.ContainsMarkers(_result.Text)
                    ? "충돌 표시 형식을 읽지 못했습니다. 결과를 직접 편집한 뒤 올바른 충돌 표시를 모두 제거하세요."
                    : "충돌 표시가 없습니다. 결과를 저장한 뒤 해결 완료를 표시하세요.";
        }
        catch (Exception ex) when (ex is VcsException or FormatException or ArgumentException)
        {
            _parsedBlocks = [];
            _blocks.ItemsSource = null;
            _status.Text = "충돌 블록을 읽지 못했습니다. 결과를 직접 편집해 표시를 정리할 수 있습니다. " + ex.Message;
        }
        finally { _updatingBlocks = false; }
    }

    private void MoveBlock(int direction)
    {
        if (_parsedBlocks.Count == 0) return;
        var next = Math.Clamp(_blocks.SelectedIndex + direction, 0, _parsedBlocks.Count - 1);
        _blocks.SelectedIndex = next;
    }

    private void SelectBlockInResult()
    {
        var index = _blocks.SelectedIndex;
        if (index < 0 || index >= _parsedBlocks.Count || _result is null) return;
        var block = _parsedBlocks[index];
        if (block.Start < 0 || block.Length < 0 || block.Start + block.Length > _result.Text.Length) return;
        _result.Select(block.Start, block.Length);
        _result.ScrollToLine(_result.GetLineIndexFromCharacterIndex(block.Start));
    }

    private void UseSelectedBlock(ConflictSide side)
    {
        var index = _blocks.SelectedIndex;
        if (index < 0 || index >= _parsedBlocks.Count || _busy || _resolved) return;
        var block = _parsedBlocks[index];
        if (block.Start < 0 || block.Length < 0 || block.Start + block.Length > _result.Text.Length)
        {
            _status.Text = "충돌 블록의 위치가 현재 결과와 일치하지 않습니다. 화면을 새로고침한 뒤 다시 시도하세요.";
            return;
        }

        var replacement = side switch
        {
            ConflictSide.Left => block.LeftText,
            ConflictSide.Right => block.RightText,
            _ => JoinBoth(block.LeftText, block.RightText, _snapshot.NewLine)
        };
        var text = _result.Text;
        _updatingResult = true;
        _result.Text = text[..block.Start] + replacement + text[(block.Start + block.Length)..];
        _result.CaretIndex = Math.Min(block.Start + replacement.Length, _result.Text.Length);
        _updatingResult = false;
        ParseDraft();
        UpdateActions();
        _status.Text = side switch
        {
            ConflictSide.Left => "선택한 충돌 블록에 왼쪽 내용을 적용했습니다. 다른 변경을 확인하고 결과를 저장하세요.",
            ConflictSide.Right => "선택한 충돌 블록에 오른쪽 내용을 적용했습니다. 다른 변경을 확인하고 결과를 저장하세요.",
            _ => "선택한 충돌 블록에 양쪽 내용을 이어 붙였습니다. 결과를 확인하고 저장하세요."
        };
    }

    private static string JoinBoth(string left, string right, string newLine)
    {
        if (left.Length == 0) return right;
        if (right.Length == 0) return left;
        var separator = string.IsNullOrEmpty(newLine) ? Environment.NewLine : newLine;
        return left.EndsWith('\n') || left.EndsWith('\r') ? left + right : left + separator + right;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _resolved || !HasUnsavedChanges) return;
        SetBusy(true);
        try
        {
            var text = WithSnapshotNewLines(_result.Text, _snapshot.NewLine);
            _snapshot = await _provider.SaveConflictAsync(_snapshot, text);
            _updatingResult = true;
            _result.Text = _snapshot.WorkingText;
            _updatingResult = false;
            ParseDraft();
            _status.Text = ConflictMarkers.ContainsMarkers(_snapshot.WorkingText)
                ? "충돌 해결 내용을 저장했습니다. 남은 블록을 해결하거나 결과를 직접 편집하세요."
                : "결과를 저장했습니다. 내용을 확인한 뒤 ‘해결 완료 표시’를 누르세요.";
            if (_snapshot.BackupPath is { } backup)
            {
                _status.Text += " 원본 백업 경로는 이 안내에 마우스를 올려 확인할 수 있습니다.";
                _status.ToolTip = "저장 직전 원본: " + backup;
            }
            FileChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _status.Text = "충돌 내용을 저장하지 못했습니다. " + ex.Message;
        }
        finally { SetBusy(false); }
    }

    private async void Resolve_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _resolved || HasUnsavedChanges || ConflictMarkers.ContainsMarkers(_result.Text)) return;
        SetBusy(true);
        try
        {
            await _provider.MarkConflictResolvedAsync(_snapshot);
            _resolved = true;
            _result.IsReadOnly = true;
            _status.Text = "충돌 해결을 표시했습니다. 커밋은 별도로 진행하세요.";
            FileChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _status.Text = "해결 완료로 표시하지 못했습니다. " + ex.Message;
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _result.IsEnabled = !busy && !_resolved;
        IsEnabled = true;
        UpdateActions();
    }

    private void UpdateActions()
    {
        var hasBlock = _blocks.SelectedIndex >= 0 && _blocks.SelectedIndex < _parsedBlocks.Count;
        _blocks.IsEnabled = !_busy && !_resolved && _parsedBlocks.Count > 0;
        _useLeft.IsEnabled = hasBlock && !_busy && !_resolved;
        _useRight.IsEnabled = hasBlock && !_busy && !_resolved;
        _useBoth.IsEnabled = hasBlock && !_busy && !_resolved;
        _save.IsEnabled = HasUnsavedChanges && !_busy && !_resolved;
        _resolve.IsEnabled = !HasUnsavedChanges && !ConflictMarkers.ContainsMarkers(_result.Text) && !_busy && !_resolved;
        _left.IsEnabled = !_busy;
        _right.IsEnabled = !_busy;
        _base.IsEnabled = !_busy;
    }

    private bool SameText(string left, string right) => NormalizeLines(left) == NormalizeLines(right);

    private static string NormalizeLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string WithSnapshotNewLines(string text, string newLine)
    {
        var separator = string.IsNullOrEmpty(newLine) ? Environment.NewLine : newLine;
        return ConflictFileStore.NormalizeNewLines(text, separator);
    }
}
