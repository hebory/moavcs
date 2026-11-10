using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MoaVcs.App.Services;
using MoaVcs.Core;

namespace MoaVcs.App;

internal static class DialogLayout
{
    public static StackPanel Create(Window window, string title, string subtitle)
    {
        window.Title = "MoaVCS · " + title;
        window.SetResourceReference(FrameworkElement.StyleProperty, typeof(Window));
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.ResizeMode = ResizeMode.NoResize;
        window.ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = subtitle, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 22), LineHeight = 22 });
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        return panel;
    }
    public static void Label(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 13, 0, 7) });
    public static Button Button(string text, RoutedEventHandler click, bool accent = false)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 8, 0, 0) };
        if (accent) button.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        button.Click += click;
        return button;
    }
}

public sealed class ProviderDialog : Window
{
    private readonly ComboBox _picker;
    public VcsKind SelectedKind => ((ProviderChoice)_picker.SelectedItem).Kind;
    public ProviderDialog(string path)
    {
        Width = 520; Height = 340;
        var panel = DialogLayout.Create(this, "버전 관리 시스템 선택", "자동으로 저장소 유형을 확인할 수 없습니다. 이 폴더에서 사용하는 도구를 선택하세요.");
        panel.Children.Add(new TextBlock { Text = path, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.SlateGray, ToolTip = path });
        _picker = new ComboBox { ItemsSource = ProviderChoice.All, SelectedIndex = 0, Margin = new Thickness(0, 15, 0, 12) };
        panel.Children.Add(_picker);
        panel.Children.Add(DialogLayout.Button("선택한 도구로 열기", (_, _) => DialogResult = true, true));
    }
}

public sealed class CreateRepositoryWindow : Window
{
    private readonly Func<VcsKind, IVcsProvider> _providers;
    private readonly string? _initialDirectory;
    private readonly VcsOperation? _initialOperation;
    private readonly ComboBox _providerPicker = new() { ItemsSource = ProviderChoice.All };
    private readonly ComboBox _modePicker = new();
    private readonly TextBox _remote = new();
    private readonly TextBox _destination = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock _remoteLabel = new() { Text = "원격 저장소 주소", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 7) };
    private readonly Button _create;
    public VcsRequest? Request { get; private set; }
    public VcsKind SelectedKind => ((ProviderChoice)_providerPicker.SelectedItem).Kind;
    public VcsOperation? SelectedOperation => (_modePicker.SelectedItem as ModeChoice)?.Operation;
    public string DestinationPath => _destination.Text;

    public CreateRepositoryWindow(Func<VcsKind, IVcsProvider> providers, string? initialDirectory = null,
        VcsOperation? initialOperation = null, VcsKind? initialVcs = null)
    {
        _providers = providers;
        _initialDirectory = string.IsNullOrWhiteSpace(initialDirectory) ? null : Path.GetFullPath(initialDirectory);
        _initialOperation = initialOperation;
        Width = 620; Height = 610;
        var panel = DialogLayout.Create(this, "새 작업 공간", "새 로컬 저장소를 만들거나 원격 저장소를 복제합니다. 인증은 해당 CLI의 기존 설정을 사용합니다.");
        DialogLayout.Label(panel, "버전 관리 시스템");
        panel.Children.Add(_providerPicker);
        DialogLayout.Label(panel, "시작 방법");
        panel.Children.Add(_modePicker);
        panel.Children.Add(_remoteLabel);
        panel.Children.Add(_remote);
        DialogLayout.Label(panel, "로컬 폴더 경로");
        var pathRow = new DockPanel();
        var browse = DialogLayout.Button("찾아보기", (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "대상 폴더 선택" };
            if (_initialDirectory is not null && Directory.Exists(_initialDirectory)) picker.InitialDirectory = _initialDirectory;
            if (picker.ShowDialog(this) == true) _destination.Text = picker.FolderName;
        });
        browse.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(browse, Dock.Right); pathRow.Children.Add(browse); pathRow.Children.Add(_destination); panel.Children.Add(pathRow);
        panel.Children.Add(new TextBlock { Text = "복제할 때는 존재하지 않는 폴더 또는 빈 폴더를 지정하세요.", FontSize = 11, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 7, 0, 0) });
        panel.Children.Add(_error);
        _create = DialogLayout.Button("작업 공간 만들기", Create_Click, true);
        panel.Children.Add(_create);
        _providerPicker.SelectionChanged += (_, _) => UpdateModes();
        _modePicker.SelectionChanged += (_, _) =>
        {
            var clone = _modePicker.SelectedItem is ModeChoice { Operation: VcsOperation.Clone };
            _remote.IsEnabled = clone;
            _remoteLabel.Opacity = clone ? 1 : 0.4;
        };
        _providerPicker.SelectedItem = initialVcs is { } kind
            ? ProviderChoice.All.FirstOrDefault(choice => choice.Kind == kind) ?? ProviderChoice.All[0]
            : ProviderChoice.All[0];
        if (initialOperation == VcsOperation.Initialize && _initialDirectory is not null)
            _destination.Text = _initialDirectory;
    }

    private void UpdateModes()
    {
        if (_providerPicker.SelectedItem is null) return;
        var caps = _providers(SelectedKind).Capabilities;
        var choices = new List<ModeChoice>();
        if (caps.HasFlag(VcsCapabilities.Initialize)) choices.Add(new(VcsOperation.Initialize, "새 로컬 저장소 만들기"));
        if (caps.HasFlag(VcsCapabilities.Clone)) choices.Add(new(VcsOperation.Clone, SelectedKind == VcsKind.Svn ? "SVN 체크아웃" : "원격 저장소 복제"));
        _modePicker.ItemsSource = choices;
        var selectedIndex = _initialOperation is { } initial
            ? choices.FindIndex(choice => choice.Operation == initial)
            : -1;
        _modePicker.SelectedIndex = choices.Count == 0 ? -1 : selectedIndex >= 0 ? selectedIndex : 0;
        _create.IsEnabled = choices.Count > 0;
        _error.Text = choices.Count == 0 ? "Perforce 클라이언트 작업 공간을 P4V 또는 p4 CLI에서 설정한 뒤 ‘폴더 열기’로 연결하세요." : "";
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_modePicker.SelectedItem is not ModeChoice mode) return;
            if (string.IsNullOrWhiteSpace(_destination.Text) || !Path.IsPathFullyQualified(_destination.Text.Trim())) throw new VcsException("대상 폴더의 절대 경로를 입력하세요.");
            var destination = Path.GetFullPath(_destination.Text.Trim());
            if (File.Exists(destination)) throw new VcsException("대상 경로에 파일이 있습니다. 폴더 경로를 지정하세요.");
            var remote = _remote.Text.Trim();
            if (mode.Operation == VcsOperation.Clone)
            {
                if (string.IsNullOrWhiteSpace(remote) || remote.StartsWith('-')) throw new VcsException("올바른 원격 저장소 주소를 입력하세요.");
                if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new VcsException("복제 대상 폴더가 비어 있어야 합니다.");
                var parent = Path.GetDirectoryName(destination) ?? throw new VcsException("드라이브 루트는 복제 대상으로 사용할 수 없습니다.");
                if (!Directory.Exists(parent)) throw new VcsException("상위 폴더가 존재해야 합니다. 먼저 상위 폴더를 선택하세요.");
                Request = new VcsRequest(mode.Operation, parent, Remote: remote, Destination: destination);
            }
            else
            {
                if (Directory.Exists(Path.Combine(destination, ".git")) || File.Exists(Path.Combine(destination, ".git")) || Directory.Exists(Path.Combine(destination, ".lore")) || Directory.Exists(Path.Combine(destination, ".svn")))
                    throw new VcsException("이 폴더에는 이미 저장소가 있습니다. ‘폴더 열기’를 사용하세요.");
                Request = new VcsRequest(mode.Operation, destination);
            }
            DialogResult = true;
        }
        catch (Exception ex) { _error.Text = ex.Message; }
    }

    private sealed record ModeChoice(VcsOperation Operation, string Label) { public override string ToString() => Label; }
}

public sealed class SettingsWindow : Window
{
    private readonly Dictionary<VcsKind, TextBox> _paths = [];
    private readonly Dictionary<VcsKind, TextBlock> _versions = [];
    private readonly Dictionary<ExternalToolKind, CheckBox> _externalEnabled = [];
    private readonly Dictionary<ExternalToolKind, TextBox> _externalExecutables = [];
    private readonly Dictionary<ExternalToolKind, TextBox> _externalArguments = [];
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 10, 0, 6), MaxHeight = 70 };
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly ICommandRunner _runner;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Button _check;
    private readonly Action<Uri> _openDownload;
    private readonly CheckBox _commitTopLevel;
    private readonly CheckBox _updateTopLevel;
    public SettingsWindow(AppSettings settings, SettingsStore store, ICommandRunner runner, Action<Uri>? openDownload = null, bool fileTools = false)
    {
        _settings = settings; _store = store; _runner = runner;
        _openDownload = openDownload ?? VcsTools.OpenDownload;
        Title = "MoaVCS · 도구 및 탐색기 설정";
        SetResourceReference(FrameworkElement.StyleProperty, typeof(Window));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;
        Width = 840; Height = 760; MinWidth = 760; MinHeight = 620;

        var layout = new Grid { Margin = new Thickness(26, 22, 26, 18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = "도구 및 탐색기 설정", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        header.Children.Add(new TextBlock
        {
            Text = "필요한 항목만 탭에서 설정하세요. 저장하면 이후 저장소 작업에 적용됩니다.",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(header);

        var tabs = new TabControl { Name = "SettingsTabs", Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(tabs, 1);
        layout.Children.Add(tabs);

        var toolsPanel = NewTabPanel();
        toolsPanel.Children.Add(new TextBlock
        {
            Text = "각 버전 관리 도구의 실행 파일을 지정합니다. 경로를 비우면 PATH에서 검색합니다. 설치가 필요하면 공식 다운로드 페이지를 여세요.",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), LineHeight = 21
        });
        foreach (var choice in ProviderChoice.All)
        {
            var tool = VcsTools.Get(choice.Kind);
            var cardPanel = new StackPanel();
            var card = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(227, 231, 237)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 10),
                Child = cardPanel
            };
            var label = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var download = DialogLayout.Button("다운로드 ↗", (_, _) =>
            {
                try { _openDownload(tool.DownloadUrl); _notice.Text = tool.DownloadHint + " 설치가 끝나면 ‘설치된 도구 확인’을 눌러 주세요."; }
                catch (Exception ex) { _notice.Text = "다운로드 페이지를 열지 못했습니다: " + ex.Message; }
            });
            download.Name = "Download" + choice.Kind;
            download.Tag = choice.Kind;
            download.ToolTip = tool.DownloadHint + "\n" + tool.DownloadUrl.AbsoluteUri;
            download.Margin = new Thickness(10, 0, 0, 0);
            download.Padding = new Thickness(10, 5, 10, 5);
            download.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(download, Dock.Right); label.Children.Add(download);
            var version = new TextBlock { FontSize = 11, Foreground = Brushes.SlateGray, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 280, TextTrimming = TextTrimming.CharacterEllipsis };
            DockPanel.SetDock(version, Dock.Right); label.Children.Add(version);
            label.Children.Add(new TextBlock { Text = choice.Label, FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            cardPanel.Children.Add(label);
            var row = new DockPanel();
            var input = new TextBox { Text = settings.ExecutablePaths.GetValueOrDefault(choice.Kind, ""), Height = 36, ToolTip = "실행 파일 이름 또는 .exe 절대 경로 (명령 인수 제외)" };
            var browse = DialogLayout.Button("찾기", (_, _) =>
            {
                var picker = new OpenFileDialog { Title = choice.Label + " 실행 파일 선택", Filter = "프로그램 (*.exe)|*.exe" };
                if (picker.ShowDialog(this) == true) input.Text = picker.FileName;
            });
            browse.Margin = new Thickness(10, 0, 0, 0); DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(input); cardPanel.Children.Add(row);
            toolsPanel.Children.Add(card);
            _paths[choice.Kind] = input; _versions[choice.Kind] = version;
        }
        _check = DialogLayout.Button("설치된 도구 확인", Check_Click);
        _check.Name = "CheckToolsButton";
        _check.HorizontalAlignment = HorizontalAlignment.Left;
        toolsPanel.Children.Add(_check);
        tabs.Items.Add(CreateTab("버전 관리 도구", "Git · SVN · Perforce · Lore", toolsPanel));

        var externalPanel = NewTabPanel();
        externalPanel.Children.Add(new TextBlock
        {
            Text = "기본값은 MoaVCS 내장 비교와 이력 화면입니다. 외부 프로그램을 쓰려면 해당 도구를 켜고 실행 파일을 선택하세요. 인수 설정은 ‘고급 인수 설정’에서 필요할 때 펼칠 수 있습니다.",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), LineHeight = 21
        });
        foreach (var kind in Enum.GetValues<ExternalToolKind>())
        {
            var config = settings.ExternalTools.GetValueOrDefault(kind) ?? new ExternalToolSettings();
            var title = kind == ExternalToolKind.Diff ? "차이 보기 · Diff" : "작성 이력 · Blame";
            var cardPanel = new StackPanel();
            var card = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(227, 231, 237)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 0, 12),
                Child = cardPanel
            };
            cardPanel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
            var enabled = new CheckBox { Content = "외부 도구 사용", IsChecked = config.UseExternal, Margin = new Thickness(0, 0, 0, 8) };
            cardPanel.Children.Add(enabled);
            cardPanel.Children.Add(new TextBlock { Text = "실행 파일 (.exe)", FontSize = 11, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 5) });
            var exeRow = new DockPanel();
            var exe = new TextBox { Text = config.ExecutablePath, Height = 34, IsEnabled = config.UseExternal, ToolTip = "외부 프로그램 실행 파일의 절대 경로" };
            var browseExe = DialogLayout.Button("찾기", (_, _) =>
            {
                var picker = new OpenFileDialog { Title = title + " 프로그램 선택", Filter = "프로그램 (*.exe)|*.exe" };
                if (picker.ShowDialog(this) == true) exe.Text = picker.FileName;
            });
            browseExe.IsEnabled = config.UseExternal;
            browseExe.Margin = new Thickness(10, 0, 0, 0); DockPanel.SetDock(browseExe, Dock.Right); exeRow.Children.Add(browseExe); exeRow.Children.Add(exe); cardPanel.Children.Add(exeRow);
            var args = new TextBox
            {
                Text = config.ArgumentTemplates.Count > 0 ? string.Join(Environment.NewLine, config.ArgumentTemplates) :
                    kind == ExternalToolKind.Diff ? "{left}" + Environment.NewLine + "{right}" : "{blame}",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 82,
                IsEnabled = config.UseExternal,
                Margin = new Thickness(0, 7, 0, 0),
                FontFamily = new FontFamily("Consolas")
            };
            args.ToolTip = kind == ExternalToolKind.Diff
                ? "한 줄에 인수 하나. {left}+{right} 또는 {patch}가 필요합니다. {file}, {repo}도 사용할 수 있습니다."
                : "한 줄에 인수 하나. {blame} 또는 {file}이 필요합니다. {blame}은 TSV 임시 파일 경로입니다. {repo}도 사용할 수 있습니다.";
            var advanced = new StackPanel();
            advanced.Children.Add(new TextBlock { Text = "인수 (한 줄에 하나)", FontSize = 11, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 2, 0, 5) });
            advanced.Children.Add(args);
            advanced.Children.Add(new TextBlock
            {
                Text = kind == ExternalToolKind.Diff
                    ? "사용 가능: {left}, {right}는 비교 스냅샷, {patch}는 패치 스냅샷, {file}은 원본 파일, {repo}는 저장소 경로입니다."
                    : "사용 가능: {blame}은 UTF-8 TSV 작성 이력 파일, {file}은 원본 파일, {repo}는 저장소 경로입니다.",
                FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0)
            });
            var expander = new Expander { Header = "고급 인수 설정", Content = advanced, IsExpanded = false, Margin = new Thickness(0, 9, 0, 0) };
            cardPanel.Children.Add(expander);
            externalPanel.Children.Add(card);
            enabled.Checked += (_, _) => { exe.IsEnabled = true; browseExe.IsEnabled = true; args.IsEnabled = true; };
            enabled.Unchecked += (_, _) => { exe.IsEnabled = false; browseExe.IsEnabled = false; args.IsEnabled = false; };
            _externalEnabled[kind] = enabled; _externalExecutables[kind] = exe; _externalArguments[kind] = args;
        }
        tabs.Items.Add(CreateTab("비교·이력 도구", "Diff · Blame", externalPanel));

        var explorerPanel = NewTabPanel();
        explorerPanel.Children.Add(new TextBlock
        {
            Text = "파일과 폴더의 오른쪽 클릭 메뉴를 관리합니다. Windows 11에서는 ‘더 많은 옵션 표시’에서 찾을 수 있습니다.",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14), LineHeight = 21
        });
        var explorerMenu = settings.ExplorerMenu ?? new ExplorerMenuSettings();
        _commitTopLevel = new CheckBox
        {
            Name = "ExplorerCommitTopLevel",
            Content = "커밋을 첫 번째 메뉴에 표시",
            IsChecked = explorerMenu.ShowCommitAtTopLevel,
            Margin = new Thickness(0, 5, 0, 4)
        };
        _updateTopLevel = new CheckBox
        {
            Name = "ExplorerUpdateTopLevel",
            Content = "업데이트 / Pull을 첫 번째 메뉴에 표시 (폴더)",
            IsChecked = explorerMenu.ShowUpdateAtTopLevel,
            Margin = new Thickness(0, 4, 0, 9)
        };
        explorerPanel.Children.Add(_commitTopLevel);
        explorerPanel.Children.Add(_updateTopLevel);
        explorerPanel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(227, 231, 237)), Margin = new Thickness(0, 12, 0, 14) });
        explorerPanel.Children.Add(new TextBlock
        {
            Text = "자주 쓰는 작업을 첫 번째 메뉴에 둘 수 있습니다. 체크를 해제하면 MoaVCS 하위 메뉴에 표시됩니다.",
            FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14), LineHeight = 19
        });
        var shellButtons = new StackPanel { Orientation = Orientation.Horizontal };
        shellButtons.Children.Add(DialogLayout.Button("메뉴 등록 / 갱신", (_, _) => SetExplorerMenu(true)));
        var remove = DialogLayout.Button("메뉴 제거", (_, _) => SetExplorerMenu(false)); remove.Margin = new Thickness(10, 8, 0, 0); shellButtons.Children.Add(remove);
        explorerPanel.Children.Add(shellButtons);
        tabs.Items.Add(CreateTab("탐색기 메뉴", "우클릭 동작과 표시 위치", explorerPanel));
        tabs.SelectedIndex = fileTools ? 1 : 0;

        var footer = new StackPanel();
        footer.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(227, 231, 237)), Margin = new Thickness(0, 0, 0, 8) });
        _notice.Margin = new Thickness(0, 2, 0, 8);
        _notice.MaxHeight = 64;
        footer.Children.Add(_notice);
        var footerButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = DialogLayout.Button("취소", (_, _) => Close());
        cancel.Name = "CancelSettingsButton";
        cancel.Margin = new Thickness(0, 4, 8, 0);
        footerButtons.Children.Add(cancel);
        var save = DialogLayout.Button("설정 저장", Save_Click, true);
        save.Name = "SaveSettingsButton";
        footerButtons.Children.Add(save);
        footer.Children.Add(footerButtons);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        Content = layout;

        try
        {
            _notice.Text = ExplorerIntegration.IsInstalled(MainWindow.GetExecutablePath())
                ? "현재 설치본의 탐색기 메뉴가 등록되어 있습니다."
                : ExplorerIntegration.IsInstalled()
                    ? "다른 설치본의 메뉴가 등록되어 있습니다. 현재 설치본으로 바꾸려면 메뉴 등록 / 갱신을 누르세요."
                    : "탐색기 메뉴가 아직 등록되지 않았습니다.";
        }
        catch (Exception ex) { _notice.Text = ex.Message; }
        Closed += (_, _) => _cancellation.Cancel();

        static StackPanel NewTabPanel() => new() { Margin = new Thickness(20, 16, 20, 20) };
        static TabItem CreateTab(string headerText, string purpose, UIElement content)
        {
            var body = new DockPanel();
            var hint = new TextBlock
            {
                Text = purpose,
                FontSize = 11,
                Foreground = Brushes.SlateGray,
                Margin = new Thickness(20, 12, 20, 0)
            };
            DockPanel.SetDock(hint, Dock.Top);
            body.Children.Add(hint);
            body.Children.Add(new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });
            return new TabItem { Header = headerText, Content = body };
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        _check.IsEnabled = false;
        try
        {
            foreach (var choice in ProviderChoice.All)
            {
                var exe = VcsTools.ResolveExecutable(choice.Kind, _paths[choice.Kind].Text);
                _versions[choice.Kind].Text = "확인 중…";
                try
                {
                    var result = await _runner.RunAsync(new CommandSpec(exe, [choice.Kind == VcsKind.Perforce ? "-V" : "--version"], Environment.CurrentDirectory, Timeout: TimeSpan.FromSeconds(12)), _cancellation.Token);
                    _versions[choice.Kind].Text = result.Success ? result.Output.Split('\n').FirstOrDefault()?.Trim() ?? "설치됨" : "실행 실패";
                    _versions[choice.Kind].ToolTip = exe + "\n" + result.Output;
                    if (result.Success && string.IsNullOrWhiteSpace(_paths[choice.Kind].Text) && Path.IsPathFullyQualified(exe))
                        _paths[choice.Kind].Text = exe;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _versions[choice.Kind].Text = "설치 경로 확인 필요"; _versions[choice.Kind].ToolTip = ex.Message; }
            }
        }
        finally { _check.IsEnabled = true; }
    }

    private void SetExplorerMenu(bool install)
    {
        try
        {
            if (install)
            {
                var executable = MainWindow.GetExecutablePath();
                var menuSettings = ReadExplorerMenuSettings();
                _store.SaveExplorerMenu(menuSettings);
                ExplorerIntegration.Install(executable, menuSettings);
                _settings.ExplorerMenu = menuSettings;
                _notice.Text = "MoaVCS 탐색기 메뉴를 등록했습니다. 선택한 첫 단계 작업도 저장했습니다.";
            }
            else
            {
                ExplorerIntegration.Uninstall(MainWindow.GetExecutablePath());
                _notice.Text = ExplorerIntegration.IsInstalled()
                    ? "다른 설치본의 탐색기 메뉴는 유지했습니다. 해당 설치본에서 제거하세요."
                    : "MoaVCS 탐색기 메뉴를 제거했습니다.";
            }
        }
        catch (Exception ex)
        {
            _notice.Text = install
                ? "탐색기 메뉴 설정 저장 또는 등록에 실패했습니다: " + ex.Message
                : ex.Message;
        }
    }

    private ExplorerMenuSettings ReadExplorerMenuSettings() => new()
    {
        ShowCommitAtTopLevel = _commitTopLevel.IsChecked == true,
        ShowUpdateAtTopLevel = _updateTopLevel.IsChecked == true
    };

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var externalTools = new Dictionary<ExternalToolKind, ExternalToolSettings>();
            foreach (var kind in Enum.GetValues<ExternalToolKind>())
            {
                var external = new ExternalToolSettings
                {
                    UseExternal = _externalEnabled[kind].IsChecked == true,
                    ExecutablePath = _externalExecutables[kind].Text.Trim(),
                    ArgumentTemplates = _externalArguments[kind].Text.Split('\n')
                        .Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToList()
                };
                if (external.UseExternal) ExternalToolLauncher.Validate(external, kind);
                externalTools[kind] = external;
            }
            var updated = new AppSettings
            {
                RecentRepositories = _settings.RecentRepositories,
                ExecutablePaths = _paths.ToDictionary(pair => pair.Key, pair => pair.Value.Text.Trim()),
                ExternalTools = externalTools,
                ExplorerMenu = ReadExplorerMenuSettings()
            };
            var previousMenuSettings = _settings.ExplorerMenu ?? new ExplorerMenuSettings();
            var menuSettingsChanged = previousMenuSettings.ShowCommitAtTopLevel != updated.ExplorerMenu.ShowCommitAtTopLevel ||
                previousMenuSettings.ShowUpdateAtTopLevel != updated.ExplorerMenu.ShowUpdateAtTopLevel;
            _store.Save(updated);
            _settings.ExecutablePaths = updated.ExecutablePaths;
            _settings.ExternalTools = updated.ExternalTools;
            if (menuSettingsChanged)
            {
                try
                {
                    var executable = MainWindow.GetExecutablePath();
                    if (ExplorerIntegration.IsInstalled(executable))
                        ExplorerIntegration.Install(executable, updated.ExplorerMenu);
                }
                catch (Exception ex)
                {
                    _notice.Text = "설정은 저장했지만 탐색기 메뉴 확인 또는 갱신에 실패했습니다: " + ex.Message;
                    return;
                }
            }
            _settings.ExplorerMenu = updated.ExplorerMenu;
            DialogResult = true;
        }
        catch (Exception ex) { _notice.Text = ex.Message; }
    }
}
