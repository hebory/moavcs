using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MoaVcs.App.Services;
using MoaVcs.Core;
using MoaVcs.Core.Providers;

namespace MoaVcs.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly LaunchOptions _launchOptions;
    private readonly ICommandRunner _runner = new ProcessCommandRunner();
    private IVcsProvider? _provider;
    private RepositoryInfo? _repository;
    private List<FileChange> _changes = [];
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _diffOperation;
    private bool _busy;
    private bool _historyLoaded;
    private string? _selectedPath;
    private readonly Action<Window>? _showInspection;
    private readonly Func<string, string, bool> _confirmOperation;
    private readonly Func<Window, bool?> _showDialog;

    public MainWindow(LaunchOptions options, SettingsStore? settingsStore = null, Action<Window>? showInspection = null,
        Func<string, string, bool>? confirmOperation = null, Func<Window, bool?>? showDialog = null)
    {
        _showInspection = showInspection;
        _confirmOperation = confirmOperation ?? ((title, detail) =>
            MessageBox.Show(this, detail, "MoaVCS · " + title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK);
        _showDialog = showDialog ?? ShowModalDialog;
        _settingsStore = settingsStore ?? new SettingsStore();
        _launchOptions = options;
        _settings = _settingsStore.Load();
        InitializeComponent();
        VersionText.Text = "MoaVCS  /  " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
        UpdateRecentList();
        UpdateControls();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_settingsStore.LoadWarning is { } warning) ShowNotice(warning);
        if (_launchOptions.InstallContextMenu || _launchOptions.UninstallContextMenu)
        {
            try
            {
                if (_launchOptions.InstallContextMenu) ExplorerIntegration.Install(GetExecutablePath(), _settings.ExplorerMenu);
                else ExplorerIntegration.Uninstall(GetExecutablePath());
                ShowNotice(_launchOptions.InstallContextMenu ? "탐색기 MoaVCS 메뉴를 등록했습니다." : "탐색기 MoaVCS 메뉴를 제거했습니다.");
            }
            catch (Exception ex) { ShowNotice(ex.Message); }
        }
        try
        {
            if (_launchOptions.Action == "settings")
                await OpenSettingsAsync();
            else if (_launchOptions.Action is "clone" or "init")
                await OpenCreateRepositoryAsync();
            else if (_launchOptions.Path is { } path)
                await OpenRepositoryAsync(path, _launchOptions.Vcs, _launchOptions.Action);
        }
        catch (Exception ex) { ShowNotice(ex.Message); }
    }

    public static string GetExecutablePath()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("앱 실행 파일을 찾을 수 없습니다.");
        if (!Path.GetFileName(path).Equals("moavcs.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("탐색기 메뉴는 moavcs.exe로 앱을 실행한 뒤 등록하세요.");
        return path;
    }

    private IVcsProvider CreateProvider(VcsKind kind)
    {
        _settings.ExecutablePaths.TryGetValue(kind, out var configured);
        var executable = VcsTools.ResolveExecutable(kind, configured);
        return kind switch
        {
            VcsKind.Git => new GitProvider(_runner, executable),
            VcsKind.Svn => new SvnProvider(_runner, executable),
            VcsKind.Perforce => new PerforceProvider(_runner, executable),
            VcsKind.Lore => new LoreProvider(_runner, executable),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private bool? ShowModalDialog(Window window)
    {
        window.Owner = this;
        return window.ShowDialog();
    }

    private static VcsKind? DetectKind(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            var root = directory.FullName;
            // Prefer the closest enclosing checkout, including Git worktree .git files.
            if (Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git"))) return VcsKind.Git;
            if (Directory.Exists(Path.Combine(root, ".svn"))) return VcsKind.Svn;
            if (Directory.Exists(Path.Combine(root, ".lore"))) return VcsKind.Lore;
            var p4config = Environment.GetEnvironmentVariable("P4CONFIG");
            if (File.Exists(Path.Combine(root, ".p4config")) || File.Exists(Path.Combine(root, "p4config.txt")) ||
                (!string.IsNullOrWhiteSpace(p4config) && File.Exists(Path.Combine(root, p4config)))) return VcsKind.Perforce;
        }
        return null;
    }

    private async Task OpenRepositoryAsync(string path, VcsKind? kind = null, string action = "open")
    {
        if (_busy) return;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var selectedFile = File.Exists(fullPath) ? fullPath : null;
            var directory = selectedFile is null ? fullPath : Path.GetDirectoryName(fullPath)!;
            if (!Directory.Exists(directory)) throw new VcsException($"폴더가 존재하지 않습니다: {directory}");
            kind ??= DetectKind(directory);
            if (kind is null)
            {
                var choose = new ProviderDialog(directory) { Owner = this };
                if (choose.ShowDialog() != true) return;
                kind = choose.SelectedKind;
            }
            var provider = CreateProvider(kind.Value);
            await RunBusyAsync("저장소를 여는 중…", async token =>
            {
                var repository = await provider.GetRepositoryAsync(directory, token);
                var changes = await provider.GetStatusAsync(repository.RootPath, token);
                _provider = provider;
                _repository = repository;
                _selectedPath = selectedFile;
                _historyLoaded = false;
                HistoryGrid.ItemsSource = null;
                WelcomePanel.Visibility = Visibility.Collapsed;
                RepositoryPanel.Visibility = Visibility.Visible;
                SetRepositoryHeader();
                ApplyChanges(changes);
                MainTabs.SelectedItem = action == "log" ? HistoryTab : ChangesTab;
                if (action == "log") await LoadHistoryAsync(token);
                RememberRepository(repository);
                Log($"{provider.Name} · {repository.RootPath}");
            });
            if (_repository?.RootPath is not null && _provider == provider)
            {
                await LoadDiffAsync();
                if (selectedFile is not null && ChangesGrid.SelectedItem is null)
                {
                    DiffTitle.Text = Path.GetFileName(selectedFile);
                    RenderDiff("선택한 파일에는 표시할 변경 내용이 없습니다.");
                }
                if (selectedFile is not null && action is "diff" or "blame")
                {
                    var relative = Path.GetRelativePath(_repository.RootPath, selectedFile).Replace('\\', '/');
                    var target = _changes.FirstOrDefault(c => c.Path.Equals(relative, StringComparison.OrdinalIgnoreCase)) ?? new FileChange(relative, "Unchanged");
                    await InspectFileAsync(action == "blame", target: target);
                }
                else if (action == "blame") ShowNotice("행별 이력은 파일 단위로 확인합니다. ‘파일 도구 → 다른 파일의 행별 이력…’에서 파일을 선택하세요.");
                else if (action == "remote") await EditRemoteUrlAsync();
                else if (action is "update" or "push")
                    await ExecuteAsync(action == "update" ? VcsOperation.Update : VcsOperation.Push);
                if (action == "commit")
                {
                    CommitMessage.Focus();
                    CommitMessage.CaretIndex = CommitMessage.Text.Length;
                }
            }
        }
        catch (Exception ex) { ShowNotice(ex.Message); }
    }

    private void SetRepositoryHeader()
    {
        if (_repository is null || _provider is null) return;
        RepoTitle.Text = new DirectoryInfo(_repository.RootPath).Name is { Length: > 0 } name ? name : _repository.RootPath;
        RepoPath.Text = _repository.RootPath;
        RepoPath.ToolTip = _repository.RootPath;
        BranchText.Text = _repository.Branch;
        BranchBadge.Visibility = string.IsNullOrWhiteSpace(_repository.Branch) ? Visibility.Collapsed : Visibility.Visible;
        RepositoryKind.Text = _provider.Name + " 저장소";
        RepositorySummary.Text = string.IsNullOrWhiteSpace(_repository.Summary)
            ? (_provider.Capabilities.HasFlag(VcsCapabilities.Push)
                ? "가져오기로 원격 변경을 받고, 보내기로 기록한 커밋을 공유하세요."
                : "동기화로 서버의 변경을 이 작업 공간에 적용하세요.")
            : _repository.Summary;
        RepositorySummary.ToolTip = _repository.Summary;
        Title = $"{RepoTitle.Text} · MoaVCS";
        StageButton.Content = _provider.Kind switch { VcsKind.Svn => "새 파일 등록", VcsKind.Perforce => "변경 등록", _ => "커밋에 포함" };
        UnstageButton.Content = _provider.Kind == VcsKind.Perforce ? "등록 해제" : "포함 해제";
        StageButton.ToolTip = _provider.Kind switch
        {
            VcsKind.Svn => "선택한 새 파일을 SVN 관리 대상으로 등록합니다.",
            VcsKind.Perforce => "선택한 파일의 변경을 Perforce에 등록합니다 (reconcile).",
            _ => "선택한 파일의 현재 변경을 다음 커밋에 포함합니다 (Stage)."
        };
        UnstageButton.ToolTip = "등록 또는 커밋 포함을 해제합니다. 작업 파일의 내용은 유지됩니다.";
        UpdateButton.Content = _provider.Kind == VcsKind.Git ? "↓  가져오기" : "↓  동기화";
        CommitDestinationHint.Text = _provider.Kind switch { VcsKind.Svn => "SVN 서버에 기록", VcsKind.Perforce => "P4V에서 제출", _ => "이 PC에 기록" };
        StagedColumn.Visibility = _provider.Kind is VcsKind.Git or VcsKind.Lore ? Visibility.Visible : Visibility.Collapsed;
        foreach (MenuItem item in ProviderMenu.Items)
            item.IsChecked = string.Equals(item.Tag as string, _provider.Kind.ToString(), StringComparison.Ordinal);
    }

    private void ApplyChanges(IReadOnlyList<FileChange> changes)
    {
        var previous = (ChangesGrid.SelectedItem as FileChange)?.Path;
        _changes = changes.ToList();
        ChangesGrid.ItemsSource = _changes;
        ChangeCount.Text = $"{_changes.Count}개 변경";
        ChangesTab.Header = $"변경 내용  {_changes.Count}";
        CleanMessage.Visibility = changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var selected = _selectedPath is null ? previous : Path.GetRelativePath(_repository!.RootPath, _selectedPath).Replace('\\', '/');
        var match = _changes.FirstOrDefault(c => c.Path.Replace('\\', '/').Equals(selected, StringComparison.OrdinalIgnoreCase));
        if (match is not null) { ChangesGrid.SelectedItem = match; ChangesGrid.ScrollIntoView(match); }
        else if (_changes.Count > 0 && _selectedPath is null) ChangesGrid.SelectedIndex = 0;
        else RenderDiff("변경된 파일이 없습니다.");
        _selectedPath = null;
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (!IsInitialized) return;
        var caps = _provider?.Capabilities ?? VcsCapabilities.None;
        bool Can(VcsCapabilities cap) => !_busy && _repository is not null && caps.HasFlag(cap);
        var hasSelection = ChangesGrid.SelectedItems.Count > 0;
        var selectedConflict = ChangesGrid.SelectedItems.Cast<FileChange>().Any(c => c.IsConflicted);
        StageButton.IsEnabled = Can(VcsCapabilities.Stage) && hasSelection && !selectedConflict;
        UnstageButton.IsEnabled = Can(VcsCapabilities.Unstage) && hasSelection && !selectedConflict &&
            (_provider?.Kind != VcsKind.Git || ChangesGrid.SelectedItems.Cast<FileChange>().Any(c => c.IsStaged));
        StageButton.Visibility = caps.HasFlag(VcsCapabilities.Stage) ? Visibility.Visible : Visibility.Collapsed;
        UnstageButton.Visibility = caps.HasFlag(VcsCapabilities.Unstage) ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.IsEnabled = Can(VcsCapabilities.Update);
        RemoteButton.Visibility = caps.HasFlag(VcsCapabilities.RemoteUrl) ? Visibility.Visible : Visibility.Collapsed;
        RemoteButton.IsEnabled = Can(VcsCapabilities.RemoteUrl);
        PushButton.Visibility = caps.HasFlag(VcsCapabilities.Push) ? Visibility.Visible : Visibility.Collapsed;
        PushButton.IsEnabled = Can(VcsCapabilities.Push);
        CommitMessage.IsEnabled = Can(VcsCapabilities.Commit);
        CommitButton.IsEnabled = Can(VcsCapabilities.Commit) && !_changes.Any(c => c.IsConflicted) && !string.IsNullOrWhiteSpace(CommitMessage.Text) &&
            (_provider?.Kind == VcsKind.Svn ? hasSelection : _changes.Any(c => c.IsStaged));
        HistoryTab.IsEnabled = caps.HasFlag(VcsCapabilities.History);
        StagedDiffCheck.Visibility = _provider?.Kind == VcsKind.Git ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !_busy;
        ProviderMenu.IsEnabled = !_busy && _repository is not null;
        CreateButton.IsEnabled = !_busy;
        FileToolsMenu.IsEnabled = !_busy && _repository is not null;
        var singleFile = ChangesGrid.SelectedItems.Count == 1;
        FileDiffButton.IsEnabled = Can(VcsCapabilities.Diff) && singleFile;
        ConflictMenu.IsEnabled = Can(VcsCapabilities.ConflictResolution) && singleFile && selectedConflict;
        BrowseDiffButton.IsEnabled = Can(VcsCapabilities.Diff);
        var canBlame = Can(VcsCapabilities.Blame) || (!_busy && _repository is not null &&
            _settings.ExternalTools.GetValueOrDefault(ExternalToolKind.Blame) is { UseExternal: true } external &&
            !ExternalToolLauncher.UsesPlaceholder(external, "blame"));
        FileBlameButton.IsEnabled = canBlame && singleFile;
        BrowseBlameButton.IsEnabled = canBlame;
        InternalDiffMenu.IsEnabled = Can(VcsCapabilities.Diff) && singleFile;
        InternalBlameMenu.IsEnabled = Can(VcsCapabilities.Blame) && singleFile;
        FileToolsMenu.ToolTip = $"비교: {ToolLabel(ExternalToolKind.Diff)} 도구 · 행별 이력: {ToolLabel(ExternalToolKind.Blame)} 도구";
        CancelButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateWorkflowGuide();
    }

    private void UpdateWorkflowGuide()
    {
        var selected = ChangesGrid.SelectedItems.Count;
        var staged = _changes.Count(c => c.IsStaged);
        SelectionHint.Text = selected == 0 ? "파일을 선택하세요. Ctrl / Shift로 여러 파일을 고를 수 있습니다." : $"{selected}개 선택 · Ctrl / Shift로 여러 파일 선택";
        CommitHint.Text = _provider?.Kind switch
        {
            VcsKind.Svn => $"선택한 {selected}개 파일을 SVN 서버에 기록합니다. 새 파일은 먼저 등록하세요.",
            VcsKind.Perforce => "변경 등록 후 P4V 또는 p4 CLI에서 변경 목록을 제출하세요.",
            _ => $"커밋에 포함된 {staged}개 파일의 변경을 함께 기록합니다. 목록의 선택과 커밋 범위는 다릅니다."
        };
        var conflicts = _changes.Count(c => c.IsConflicted);
        if (conflicts > 0)
        {
            WorkflowTitle.Text = $"충돌 {conflicts}개를 먼저 해결하세요";
            WorkflowHint.Text = _provider?.Capabilities.HasFlag(VcsCapabilities.ConflictResolution) == true
                ? "충돌 파일을 선택하고 ‘파일 도구 → 선택 파일의 충돌 해결’에서 결과를 편집·저장한 뒤 해결로 표시하세요."
                : $"{_provider?.Name}의 충돌은 전용 VCS 도구에서 해결한 뒤 새로 고침하세요. 내장 편집은 Git과 SVN의 텍스트 충돌을 지원합니다.";
        }
        else if (_changes.Count == 0)
        {
            WorkflowTitle.Text = "기록할 변경이 없습니다";
            WorkflowHint.Text = "파일을 수정한 뒤 새로 고침하세요. 이전 기록은 ‘커밋 이력’에서, 개별 파일 비교는 ‘파일 도구’에서 확인할 수 있습니다.";
        }
        else if (_provider?.Kind == VcsKind.Perforce)
        {
            WorkflowTitle.Text = "변경 확인 → 변경 등록 → P4V에서 제출";
            WorkflowHint.Text = "파일을 선택해 내용을 확인하고 ‘변경 등록’을 누르세요. 변경 목록 제출은 P4V 또는 p4 CLI에서 진행합니다.";
        }
        else if (_provider?.Kind == VcsKind.Svn)
        {
            WorkflowTitle.Text = "변경 확인 → 파일 선택 → 서버에 커밋";
            WorkflowHint.Text = selected == 0 ? "함께 기록할 파일을 목록에서 선택하세요. 새 파일은 ‘새 파일 등록’을 먼저 누르세요." : "선택한 파일의 내용을 확인하고 아래에 변경 설명을 적으세요. 커밋하면 SVN 서버에 바로 기록됩니다.";
        }
        else if (staged == 0)
        {
            WorkflowTitle.Text = "변경 확인 → 커밋에 포함 → 기록 남기기";
            WorkflowHint.Text = "파일의 변경 내용을 확인한 뒤 ‘커밋에 포함’을 누르세요. 포함할 파일을 준비하면 커밋할 수 있습니다.";
        }
        else
        {
            WorkflowTitle.Text = $"{staged}개 파일의 변경을 기록할 준비가 됐습니다";
            WorkflowHint.Text = string.IsNullOrWhiteSpace(CommitMessage.Text)
                ? "아래에 무엇을 바꿨는지 짧게 적고 ‘커밋’을 누르세요. 커밋 후 ‘보내기’로 원격 저장소에 공유할 수 있습니다."
                : "‘커밋’을 누르면 포함된 변경이 이 PC에 기록됩니다. 원격 저장소에 공유하려면 이후 ‘보내기’를 누르세요.";
        }
    }

    private string ToolLabel(ExternalToolKind kind) => _settings.ExternalTools.GetValueOrDefault(kind) is { UseExternal: true } ? "외부" : "내장";

    private async Task InspectFileAsync(bool blame, bool browse = false, bool forceInternal = false, FileChange? target = null)
    {
        if (_busy || _provider is null || _repository is null) return;
        var provider = _provider; var repository = _repository;
        FileChange? file = target ?? (ChangesGrid.SelectedItems.Count == 1 ? ChangesGrid.SelectedItem as FileChange : null);
        if (browse)
        {
            var picker = new OpenFileDialog { Title = blame ? "이력을 확인할 저장소 파일 선택" : "비교할 저장소 파일 선택", InitialDirectory = repository.RootPath, CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            var relative = Path.GetRelativePath(repository.RootPath, picker.FileName).Replace('\\', '/');
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            { ShowNotice("현재 저장소 내부의 파일을 선택하세요."); return; }
            file = _changes.FirstOrDefault(c => c.Path.Equals(relative, StringComparison.OrdinalIgnoreCase)) ?? new FileChange(relative, "Unchanged");
        }
        if (file is null) { ShowNotice("파일을 하나 선택하세요."); return; }
        var selected = file;
        var kind = blame ? ExternalToolKind.Blame : ExternalToolKind.Diff;
        var tool = _settings.ExternalTools.GetValueOrDefault(kind);
        var external = !forceInternal && tool is { UseExternal: true };
        var staged = provider.Kind == VcsKind.Git && StagedDiffCheck.IsChecked == true;
        Window? viewer = null;
        await RunBusyAsync(blame ? "행별 이력을 불러오는 중…" : "파일을 비교하는 중…", async token =>
        {
            if (external) ExternalToolLauncher.Validate(tool!, kind);
            if (blame)
            {
                BlameResult? result = null;
                if (!external || ExternalToolLauncher.UsesPlaceholder(tool, "blame"))
                    result = await provider.GetBlameAsync(repository.RootPath, selected, token);
                if (external)
                {
                    token.ThrowIfCancellationRequested();
                    using var process = ExternalToolLauncher.Launch(tool!, kind, new ExternalToolContext(repository.RootPath,
                        Path.Combine(repository.RootPath, selected.Path), BlameText: result is null ? null : FileInspectionWindow.ExportBlame(result)));
                    Log("외부 blame 도구를 열었습니다: " + selected.Path);
                }
                else viewer = new FileInspectionWindow(result!);
            }
            else if (!external && selected.IsConflicted && provider.Capabilities.HasFlag(VcsCapabilities.ConflictResolution))
            {
                var conflict = await provider.GetConflictAsync(repository.RootPath, selected, token);
                var conflictWindow = new FileInspectionWindow(provider, conflict);
                conflictWindow.ConflictChanged += async (_, _) =>
                {
                    // Saving and resolving can finish while an earlier refresh is still reading status.
                    while (_busy && _provider == provider && _repository?.RootPath == repository.RootPath)
                        await Task.Delay(100);
                    if (_provider == provider && _repository?.RootPath == repository.RootPath)
                        await RefreshAsync();
                };
                viewer = conflictWindow;
            }
            else
            {
                var needsPair = external ? ExternalToolLauncher.UsesPlaceholder(tool, "left") || ExternalToolLauncher.UsesPlaceholder(tool, "right") :
                    provider.Capabilities.HasFlag(VcsCapabilities.FileComparison);
                FileComparison? comparison = needsPair ? await provider.GetComparisonAsync(repository.RootPath, selected, staged, token) : null;
                var patch = !external || ExternalToolLauncher.UsesPlaceholder(tool, "patch")
                    ? await provider.GetDiffAsync(repository.RootPath, selected, staged, token) : null;
                if (external)
                {
                    token.ThrowIfCancellationRequested();
                    using var process = ExternalToolLauncher.Launch(tool!, kind, new ExternalToolContext(repository.RootPath,
                        Path.Combine(repository.RootPath, selected.Path), comparison?.LeftText, comparison?.RightText, patch));
                    Log("외부 diff 도구를 열었습니다: " + selected.Path);
                }
                else if (comparison is not null)
                {
                    var rows = await Task.Run(() => LineDiff.Compare(comparison.LeftText, comparison.RightText, token), token);
                    viewer = new FileInspectionWindow(comparison, rows, patch!);
                }
                else viewer = new FileInspectionWindow(selected.Path, patch!);
            }
        });
        if (viewer is not null)
        {
            if (_showInspection is not null) _showInspection(viewer);
            else { viewer.Owner = this; viewer.Show(); }
        }
    }

    private async void FileDiff_Click(object sender, RoutedEventArgs e) => await InspectFileAsync(false);
    private async void FileBlame_Click(object sender, RoutedEventArgs e) => await InspectFileAsync(true);
    private async void BrowseDiff_Click(object sender, RoutedEventArgs e) => await InspectFileAsync(false, browse: true);
    private async void BrowseBlame_Click(object sender, RoutedEventArgs e) => await InspectFileAsync(true, browse: true);
    private async void InternalDiff_Click(object sender, RoutedEventArgs e) => await InspectFileAsync(false, forceInternal: true);
    private async void InternalBlame_Click(object sender, RoutedEventArgs e) => await InspectFileAsync(true, forceInternal: true);

    private async Task RunBusyAsync(string label, Func<CancellationToken, Task> operation)
    {
        if (_busy) return;
        _busy = true;
        _diffOperation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        StatusText.Text = label;
        StatusDot.Fill = Brushes.DarkOrange;
        UpdateControls();
        try
        {
            await operation(cancellation.Token);
            StatusText.Text = $"준비됨  ·  {DateTime.Now:HH:mm:ss} 갱신";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "작업이 중지되었습니다. 새로 고침으로 현재 상태를 확인하세요.";
            Log("작업 중지 요청. 이미 완료된 변경은 취소되지 않습니다.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "작업을 완료하지 못했습니다";
            ShowNotice(ex.Message);
            Log("오류: " + ex.Message);
        }
        finally
        {
            _operation = null;
            _busy = false;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(79, 168, 137));
            UpdateControls();
        }
    }

    private async Task RefreshAsync()
    {
        if (_repository is null || _provider is null || _busy) return;
        await RunBusyAsync("변경 내용을 확인하는 중…", async token =>
        {
            _repository = await _provider.GetRepositoryAsync(_repository.RootPath, token);
            SetRepositoryHeader();
            ApplyChanges(await _provider.GetStatusAsync(_repository.RootPath, token));
            _historyLoaded = false;
            if (MainTabs.SelectedItem == HistoryTab) await LoadHistoryAsync(token);
        });
        await LoadDiffAsync();
    }

    private async Task LoadDiffAsync()
    {
        if (_busy || _provider is null || _repository is null || !_provider.Capabilities.HasFlag(VcsCapabilities.Diff)) return;
        _diffOperation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _diffOperation = cancellation;
        var file = ChangesGrid.SelectedItem as FileChange;
        if (file is null) { RenderDiff("파일을 선택하면 변경 내용을 볼 수 있습니다."); _diffOperation = null; return; }
        DiffTitle.Text = Path.GetFileName(file.Path);
        DiffTitle.ToolTip = file.Path;
        var provider = _provider;
        var repository = _repository;
        try
        {
            // Selection often changes several times during a refresh or keyboard navigation.
            await Task.Delay(100, cancellation.Token);
            var diff = await provider.GetDiffAsync(repository.RootPath, file, StagedDiffCheck.IsChecked == true, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
                RenderDiff(string.IsNullOrWhiteSpace(diff) ? (file.IsStaged && provider.Kind == VcsKind.Git ? "커밋 포함 이후의 변경이 없습니다. ‘커밋할 내용’을 선택하세요." : "표시할 텍스트 차이가 없습니다.") : diff);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) RenderDiff(ex.Message); }
        finally { if (ReferenceEquals(_diffOperation, cancellation)) _diffOperation = null; }
    }

    private void RenderDiff(string text)
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
        DiffViewer.Document = doc;
    }

    private async Task LoadHistoryAsync(CancellationToken token)
    {
        if (_provider is null || _repository is null || !_provider.Capabilities.HasFlag(VcsCapabilities.History)) return;
        var entries = await _provider.GetHistoryAsync(_repository.RootPath, token);
        HistoryGrid.ItemsSource = entries;
        HistoryHint.Text = entries.Count == 0 ? "아직 커밋 이력이 없습니다." : $"최근 {entries.Count}개 기록";
        _historyLoaded = true;
    }

    private async Task ExecuteAsync(VcsOperation operation)
    {
        if (_busy || _repository is null || _provider is null) return;
        var required = operation switch
        {
            VcsOperation.Stage => VcsCapabilities.Stage,
            VcsOperation.Unstage => VcsCapabilities.Unstage,
            VcsOperation.Commit => VcsCapabilities.Commit,
            VcsOperation.Update => VcsCapabilities.Update,
            VcsOperation.Push => VcsCapabilities.Push,
            _ => VcsCapabilities.None
        };
        if (required == VcsCapabilities.None || !_provider.Capabilities.HasFlag(required))
        {
            ShowNotice($"{_provider.Name}에서는 이 작업을 지원하지 않습니다.");
            return;
        }
        var selectedChanges = ChangesGrid.SelectedItems.Cast<FileChange>();
        if ((operation == VcsOperation.Commit && _changes.Any(c => c.IsConflicted)) ||
            (operation is VcsOperation.Stage or VcsOperation.Unstage && selectedChanges.Any(c => c.IsConflicted)))
        {
            ShowNotice("충돌 파일은 먼저 ‘파일 도구 → 선택 파일의 충돌 해결’에서 해결하세요. 지원하지 않는 충돌은 전용 VCS 도구를 사용하세요.");
            return;
        }
        if (operation == VcsOperation.Unstage && _provider.Kind == VcsKind.Git)
            selectedChanges = selectedChanges.Where(c => c.IsStaged);
        // A renamed row represents both paths; unstaging only the destination leaves a staged deletion.
        var selected = selectedChanges.SelectMany(c => _provider.Kind == VcsKind.Git &&
            operation == VcsOperation.Unstage && c.OriginalPath is { } original
            ? new[] { c.Path, original } : new[] { c.Path }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var message = CommitMessage.Text.Trim();
        if (operation is VcsOperation.Stage or VcsOperation.Unstage && selected.Length == 0) return;
        if (operation == VcsOperation.Commit && string.IsNullOrWhiteSpace(message)) return;
        if (operation == VcsOperation.Commit && _provider.Kind == VcsKind.Svn && selected.Length == 0) return;
        var label = operation switch
        {
            VcsOperation.Stage => "선택한 변경 등록", VcsOperation.Unstage => "선택한 스테이지 해제",
            VcsOperation.Commit => "커밋", VcsOperation.Update => "원격 변경 가져오기", VcsOperation.Push => "원격 저장소에 보내기", _ => operation.ToString()
        };
        if (operation is VcsOperation.Commit or VcsOperation.Update or VcsOperation.Push)
        {
            var detail = operation switch
            {
                VcsOperation.Commit => _provider.Kind == VcsKind.Svn ? $"선택한 {selected.Length}개 파일을 SVN 서버에 커밋합니다.\n\n{message}" : $"현재 스테이지에 포함된 모든 변경을 커밋합니다.\n\n{message}",
                VcsOperation.Update => _provider.Kind == VcsKind.Git ? "원격 변경을 가져오고 fast-forward로 적용합니다. 분기된 이력은 자동 병합하지 않습니다." : "서버의 변경 내용을 작업 공간에 동기화합니다. 충돌이 발생하면 별도로 해결해야 합니다.",
                _ when _provider.Kind == VcsKind.Git => "Git 설정에 따라 원격 저장소에 커밋을 전송합니다. 처음 푸시하는 브랜치는 기본 푸시가 성공하면 원격 추적 브랜치(upstream)를 설정합니다.",
                _ => "현재 브랜치의 커밋을 설정된 원격 저장소에 전송합니다."
            };
            if (!_confirmOperation(label, detail)) return;
        }
        await RunBusyAsync(label + " 중…", async token =>
        {
            Log(label + (_provider.Kind == VcsKind.Svn || operation is VcsOperation.Stage or VcsOperation.Unstage ? $" ({selected.Length}개 파일)" : ""));
            var requestPaths = operation is VcsOperation.Stage or VcsOperation.Unstage or VcsOperation.Commit ? selected : null;
            var result = (await _provider.ExecuteAsync(new VcsRequest(operation, _repository.RootPath, requestPaths, message), token)).EnsureSuccess();
            if (operation == VcsOperation.Push) NoticePanel.Visibility = Visibility.Collapsed;
            Log(string.IsNullOrWhiteSpace(result.Output) ? "완료" : result.Output);
            if (operation == VcsOperation.Commit) CommitMessage.Clear();
            ApplyChanges(await _provider.GetStatusAsync(_repository.RootPath, token));
            _historyLoaded = false;
            if (MainTabs.SelectedItem == HistoryTab) await LoadHistoryAsync(token);
        });
        await LoadDiffAsync();
    }

    private void RememberRepository(RepositoryInfo repository)
    {
        _settings.RecentRepositories.RemoveAll(r => r.Path.Equals(repository.RootPath, StringComparison.OrdinalIgnoreCase));
        _settings.RecentRepositories.Insert(0, new RecentRepository(repository.RootPath, repository.Kind));
        _settings.RecentRepositories = _settings.RecentRepositories.Take(12).ToList();
        try { _settingsStore.SaveRecentRepositories(_settings.RecentRepositories); }
        catch (Exception ex) { ShowNotice("최근 저장소를 저장하지 못했습니다: " + ex.Message); }
        UpdateRecentList();
        ExplorerOverlays.Wake();
    }

    private void UpdateRecentList()
    {
        RecentList.ItemsSource = _settings.RecentRepositories.Take(6).Select(r => new RecentItem(r.Path, r.VcsKind)).ToList();
        RecentEmpty.Visibility = _settings.RecentRepositories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowNotice(string message) { NoticeText.Text = message; NoticeText.ToolTip = message; NoticePanel.Visibility = Visibility.Visible; }
    private void Log(string message)
    {
        var text = $"[{DateTime.Now:HH:mm:ss}] {message}\n\n";
        if (OutputLog.Text.Length > 150_000) OutputLog.Text = OutputLog.Text[^100_000..];
        OutputLog.AppendText(text.Length > 50_000 ? text[..50_000] + "\n(출력 생략)\n" : text);
        OutputLog.ScrollToEnd();
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFolderDialog { Title = "MoaVCS에서 열 저장소 폴더 선택", Multiselect = false };
        if (dialog.ShowDialog(this) == true) await OpenRepositoryAsync(dialog.FolderName);
    }
    private async void Recent_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: RecentItem item }) await OpenRepositoryAsync(item.Path, item.Kind); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Stage_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(VcsOperation.Stage);
    private async void Unstage_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(VcsOperation.Unstage);
    private async void Commit_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(VcsOperation.Commit);
    private async void Update_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(VcsOperation.Update);
    private async void Push_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(VcsOperation.Push);
    private async void Remote_Click(object sender, RoutedEventArgs e) => await EditRemoteUrlAsync();

    private async Task EditRemoteUrlAsync()
    {
        if (_busy || _repository is null || _provider is null) return;
        var provider = _provider;
        var repository = _repository;
        if (!provider.Capabilities.HasFlag(VcsCapabilities.RemoteUrl))
        {
            ShowNotice($"{provider.Name}에서는 앱에서 원격 주소를 변경할 수 없습니다.");
            return;
        }

        RemoteInfo? remote = null;
        await RunBusyAsync("원격 주소를 불러오는 중…", async token =>
        {
            remote = await provider.GetRemoteAsync(repository.RootPath, token);
        });
        if (remote is null || _provider != provider || _repository?.RootPath != repository.RootPath) return;

        var dialog = new RemoteSettingsWindow(repository.RootPath, remote);
        if (_showDialog(dialog) != true) return;

        var saved = false;
        await RunBusyAsync("원격 주소를 저장하는 중…", async token =>
        {
            await provider.SetRemoteUrlAsync(repository.RootPath, dialog.Url, remote, token);
            saved = true;
        });
        if (!saved) return;

        Log("origin 주소를 저장했습니다.");
        await RefreshAsync();
    }
    private async void ChangesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateControls(); await LoadDiffAsync(); }
    private async void StagedDiff_Changed(object sender, RoutedEventArgs e) => await LoadDiffAsync();
    private void CommitMessage_TextChanged(object sender, TextChangedEventArgs e) => UpdateControls();
    private void DismissNotice_Click(object sender, RoutedEventArgs e) => NoticePanel.Visibility = Visibility.Collapsed;
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void WorkspaceNav_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedItem = ChangesTab;

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabs) || _busy || _historyLoaded || MainTabs.SelectedItem != HistoryTab) return;
        await RunBusyAsync("이력을 불러오는 중…", LoadHistoryAsync);
    }
    private async void ProviderMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _repository is null || sender is not MenuItem { Tag: string tag } || !Enum.TryParse<VcsKind>(tag, out var kind)) return;
        await OpenRepositoryAsync(_repository.RootPath, kind);
        SetRepositoryHeader();
    }
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await OpenSettingsAsync();
    }

    private async void FileToolSettings_Click(object sender, RoutedEventArgs e) => await OpenSettingsAsync(fileTools: true);

    private async Task OpenSettingsAsync(bool fileTools = false)
    {
        if (_busy) return;
        var dialog = new SettingsWindow(_settings, _settingsStore, _runner, fileTools: fileTools);
        if (_showDialog(dialog) == true && _repository is not null) await OpenRepositoryAsync(_repository.RootPath, _repository.Kind);
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new CreateRepositoryWindow(CreateProvider);
        if (_showDialog(dialog) != true) return;
        await CreateRepositoryAsync(dialog);
    }

    private async Task OpenCreateRepositoryAsync()
    {
        if (_busy) return;
        var actionPath = Path.GetFullPath(_launchOptions.Path ?? Environment.CurrentDirectory);
        var directory = File.Exists(actionPath)
            ? Path.GetDirectoryName(actionPath)!
            : actionPath;
        var operation = _launchOptions.Action == "init" ? VcsOperation.Initialize : VcsOperation.Clone;
        var initialDirectory = operation == VcsOperation.Initialize || Directory.Exists(directory)
            ? directory
            : Path.GetDirectoryName(directory) ?? Environment.CurrentDirectory;
        var kind = _launchOptions.Vcs ?? VcsKind.Git;
        var dialog = new CreateRepositoryWindow(CreateProvider, initialDirectory, operation, kind);
        if (_showDialog(dialog) != true) return;
        await CreateRepositoryAsync(dialog);
    }

    private async Task CreateRepositoryAsync(CreateRepositoryWindow dialog)
    {
        if (_busy || dialog.Request is not { } request) return;
        var kind = dialog.SelectedKind;
        var success = false;
        await RunBusyAsync(request.Operation == VcsOperation.Clone ? "저장소를 복제하는 중…" : "저장소를 만드는 중…", async token =>
        {
            if (request.Operation == VcsOperation.Initialize) Directory.CreateDirectory(request.WorkingDirectory);
            var result = (await CreateProvider(kind).ExecuteAsync(request, token)).EnsureSuccess();
            Log(result.Output);
            success = true;
        });
        if (success) await OpenRepositoryAsync(request.Operation == VcsOperation.Clone ? request.Destination! : request.WorkingDirectory, kind);
    }
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { e.Handled = true; await RefreshAsync(); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { e.Handled = true; OpenFolder_Click(this, new RoutedEventArgs()); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter && CommitButton.IsEnabled) { e.Handled = true; await ExecuteAsync(VcsOperation.Commit); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy && MessageBox.Show(this, "진행 중인 작업을 중지하고 닫을까요? 이미 적용된 변경은 되돌아가지 않습니다.", "MoaVCS", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) { e.Cancel = true; return; }
        _operation?.Cancel();
        _diffOperation?.Cancel();
    }
}

public sealed record ProviderChoice(VcsKind Kind, string Label)
{
    public static IReadOnlyList<ProviderChoice> All { get; } = [new(VcsKind.Git, "Git"), new(VcsKind.Svn, "SVN"), new(VcsKind.Perforce, "Perforce"), new(VcsKind.Lore, "Lore")];
    public override string ToString() => Label;
}

public sealed record RecentItem(string Path, VcsKind Kind)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
    public string KindLabel => ProviderChoice.All.First(p => p.Kind == Kind).Label;
}
