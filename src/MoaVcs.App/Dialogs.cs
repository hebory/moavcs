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
    private readonly ComboBox _providerPicker = new() { ItemsSource = ProviderChoice.All };
    private readonly ComboBox _modePicker = new();
    private readonly TextBox _remote = new();
    private readonly TextBox _destination = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock _remoteLabel = new() { Text = "원격 저장소 주소", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 7) };
    private readonly Button _create;
    public VcsRequest? Request { get; private set; }
    public VcsKind SelectedKind => ((ProviderChoice)_providerPicker.SelectedItem).Kind;

    public CreateRepositoryWindow(Func<VcsKind, IVcsProvider> providers)
    {
        _providers = providers;
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
        _providerPicker.SelectedIndex = 0;
    }

    private void UpdateModes()
    {
        if (_providerPicker.SelectedItem is null) return;
        var caps = _providers(SelectedKind).Capabilities;
        var choices = new List<ModeChoice>();
        if (caps.HasFlag(VcsCapabilities.Initialize)) choices.Add(new(VcsOperation.Initialize, "새 로컬 저장소 만들기"));
        if (caps.HasFlag(VcsCapabilities.Clone)) choices.Add(new(VcsOperation.Clone, SelectedKind == VcsKind.Svn ? "SVN 체크아웃" : "원격 저장소 복제"));
        _modePicker.ItemsSource = choices;
        _modePicker.SelectedIndex = choices.Count > 0 ? 0 : -1;
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
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 10, 0, 6), MaxHeight = 70 };
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly ICommandRunner _runner;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Button _check;
    public SettingsWindow(AppSettings settings, SettingsStore store, ICommandRunner runner)
    {
        _settings = settings; _store = store; _runner = runner;
        Width = 700; Height = 820;
        var panel = DialogLayout.Create(this, "도구 및 탐색기 설정", "MoaVCS는 설치된 각 VCS의 CLI를 사용합니다. 비워 두면 PATH에서 찾습니다. 로그인 정보는 각 도구에서 설정하세요.");
        foreach (var choice in ProviderChoice.All)
        {
            var label = new DockPanel { Margin = new Thickness(0, 10, 0, 6) };
            var version = new TextBlock { FontSize = 11, Foreground = Brushes.SlateGray, HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 440, TextTrimming = TextTrimming.CharacterEllipsis };
            DockPanel.SetDock(version, Dock.Right); label.Children.Add(version);
            label.Children.Add(new TextBlock { Text = choice.Label, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(label);
            var row = new DockPanel();
            var input = new TextBox { Text = settings.ExecutablePaths.GetValueOrDefault(choice.Kind, ""), ToolTip = "실행 파일 이름 또는 .exe 절대 경로 (명령 인수 제외)" };
            var browse = DialogLayout.Button("찾기", (_, _) =>
            {
                var picker = new OpenFileDialog { Title = choice.Label + " 실행 파일 선택", Filter = "프로그램 (*.exe)|*.exe" };
                if (picker.ShowDialog(this) == true) input.Text = picker.FileName;
            });
            browse.Margin = new Thickness(10, 0, 0, 0); DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(input); panel.Children.Add(row);
            _paths[choice.Kind] = input; _versions[choice.Kind] = version;
        }
        _check = DialogLayout.Button("설치된 도구 확인", Check_Click);
        panel.Children.Add(_check);
        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(227, 231, 237)), Margin = new Thickness(0, 20, 0, 14) });
        panel.Children.Add(new TextBlock { Text = "탐색기 우클릭 메뉴", FontSize = 16, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "파일 · 폴더 · 폴더 배경에 MoaVCS 메뉴를 추가합니다.\nWindows 11에서는 ‘더 많은 옵션 표시’에 나타납니다.", Foreground = Brushes.SlateGray, Margin = new Thickness(0, 7, 0, 8), LineHeight = 21 });
        var shellButtons = new StackPanel { Orientation = Orientation.Horizontal };
        shellButtons.Children.Add(DialogLayout.Button("메뉴 등록 / 갱신", (_, _) => SetExplorerMenu(true)));
        var remove = DialogLayout.Button("메뉴 제거", (_, _) => SetExplorerMenu(false)); remove.Margin = new Thickness(10, 8, 0, 0); shellButtons.Children.Add(remove); panel.Children.Add(shellButtons);
        panel.Children.Add(_notice);
        panel.Children.Add(DialogLayout.Button("설정 저장", Save_Click, true));
        try { _notice.Text = ExplorerIntegration.IsInstalled() ? "탐색기 메뉴가 등록되어 있습니다." : "탐색기 메뉴가 아직 등록되지 않았습니다."; }
        catch (Exception ex) { _notice.Text = ex.Message; }
        Closed += (_, _) => _cancellation.Cancel();
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        _check.IsEnabled = false;
        try
        {
            foreach (var choice in ProviderChoice.All)
            {
                var exe = _paths[choice.Kind].Text.Trim();
                if (exe.Length == 0) exe = choice.Kind switch { VcsKind.Git => "git", VcsKind.Svn => "svn", VcsKind.Perforce => "p4", _ => "lore" };
                _versions[choice.Kind].Text = "확인 중…";
                try
                {
                    var result = await _runner.RunAsync(new CommandSpec(exe, [choice.Kind == VcsKind.Perforce ? "-V" : "--version"], Environment.CurrentDirectory, Timeout: TimeSpan.FromSeconds(12)), _cancellation.Token);
                    _versions[choice.Kind].Text = result.Success ? result.Output.Split('\n').FirstOrDefault()?.Trim() ?? "설치됨" : "실행 실패";
                    _versions[choice.Kind].ToolTip = result.Output;
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
            if (install) ExplorerIntegration.Install(MainWindow.GetExecutablePath()); else ExplorerIntegration.Uninstall();
            _notice.Text = install ? "MoaVCS 탐색기 메뉴를 등록했습니다. 폴더를 우클릭해 확인하세요." : "MoaVCS 탐색기 메뉴를 제거했습니다.";
        }
        catch (Exception ex) { _notice.Text = ex.Message; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var updated = new AppSettings { RecentRepositories = _settings.RecentRepositories, ExecutablePaths = _paths.ToDictionary(pair => pair.Key, pair => pair.Value.Text.Trim()) };
            _store.Save(updated);
            _settings.ExecutablePaths = updated.ExecutablePaths;
            DialogResult = true;
        }
        catch (Exception ex) { _notice.Text = ex.Message; }
    }
}
