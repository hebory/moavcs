using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MoaVcs.App;
using MoaVcs.App.Services;
using MoaVcs.Core;

namespace MoaVcs.UiSmoke;

internal static partial class Program
{
    private static string? _fixture;
    private static string? _commitFixture;
    private static string? _historyFixture;
    private static string? _conflictFixture;
    private static string? _loreFixture;
    private static string? _loreSettingsFile;
    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new MoaVcs.App.App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var output = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "screenshots"));
            Directory.CreateDirectory(output);
            var scratch = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local"));
            Directory.CreateDirectory(scratch);
            if (Environment.GetEnvironmentVariable("MOAVCS_UI_SMOKE_ONLY") == "history-info")
            {
                RunHistoryInformationSmoke(scratch, output);
                app.Shutdown();
                return 0;
            }
            if (Environment.GetEnvironmentVariable("MOAVCS_UI_SMOKE_ONLY") == "checkout")
            {
                RunCheckoutDialogSmoke(scratch, output);
                app.Shutdown();
                return 0;
            }
            if (Environment.GetEnvironmentVariable("MOAVCS_UI_SMOKE_ONLY") == "repositories")
            {
                RunRepositorySidebarSmoke(scratch, output);
                app.Shutdown();
                return 0;
            }
            _fixture = Path.Combine(scratch, "ui-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_fixture);
            Git("init"); Git("config", "user.name", "MoaVCS UI test"); Git("config", "user.email", "test@example.invalid");
            File.WriteAllText(Path.Combine(_fixture, "README.md"), "# MoaVCS\n\nOne workspace for version control.\n");
            File.WriteAllText(Path.Combine(_fixture, "unchanged.txt"), "A committed file with no local changes.\n");
            Git("add", "README.md", "unchanged.txt"); Git("commit", "-m", "Create workspace");
            File.AppendAllText(Path.Combine(_fixture, "README.md"), "\nGit, SVN, Perforce and Lore.\n");
            File.WriteAllText(Path.Combine(_fixture, "시작하기.txt"), "변경 파일을 선택하여 비교하세요.\n");
            var settings = new SettingsStore(Path.Combine(Environment.CurrentDirectory, ".local", "ui-smoke-settings.json"));
            settings.Save(new AppSettings());
            var window = new MainWindow(new LaunchOptions(null), settings);
            Render(window, Path.Combine(output, "welcome.png"));
            if (((TextBlock)window.FindName("RepoTitle")).Text != "작업 공간") throw new InvalidOperationException("Welcome title failed.");
            if (((Grid)window.FindName("RepositoryPanel")).Visibility != Visibility.Collapsed) throw new InvalidOperationException("Empty state failed.");

            var inspections = new List<Window>();
            var repoWindow = new MainWindow(new LaunchOptions(_fixture), settings, inspections.Add);
            repoWindow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (((Grid)repoWindow.FindName("RepositoryPanel")).Visibility != Visibility.Visible && DateTime.UtcNow < deadline)
                Pump();
            while (((Button)repoWindow.FindName("CancelButton")).Visibility == Visibility.Visible && DateTime.UtcNow < deadline)
                Pump();
            if (((Button)repoWindow.FindName("CancelButton")).Visibility == Visibility.Visible) throw new TimeoutException("Repository load did not finish.");
            var notice = ((TextBlock)repoWindow.FindName("NoticeText")).Text;
            if (((Grid)repoWindow.FindName("RepositoryPanel")).Visibility != Visibility.Visible) throw new InvalidOperationException("Repository did not load: " + notice);
            if (!string.IsNullOrWhiteSpace(notice)) throw new InvalidOperationException("Repository error: " + notice);
            for (var i = 0; i < 20; i++) Pump();
            Render(repoWindow, Path.Combine(output, "workspace.png"));
            RenderAtMinimum(repoWindow, Path.Combine(output, "workspace-min.png"));
            var grid = (DataGrid)repoWindow.FindName("ChangesGrid");
            if (grid.Items.Count != 2) throw new InvalidOperationException("Expected two fixture changes.");
            var providerMenu = (MenuItem)repoWindow.FindName("ProviderMenu");
            if (providerMenu.Items.Cast<MenuItem>().Count(item => item.IsCheckable && item.IsChecked) != 1)
                throw new InvalidOperationException("The provider menu must show exactly one active VCS.");
            grid.SelectedItem = grid.Items.Cast<MoaVcs.Core.FileChange>().Single(c => c.Path == "README.md");
            ((MenuItem)repoWindow.FindName("FileDiffButton")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (inspections.Count < 1 && DateTime.UtcNow < deadline && ((Button)repoWindow.FindName("CancelButton")).Visibility == Visibility.Visible) Pump();
            if (inspections.Count != 1) throw new InvalidOperationException("Diff command did not open viewer: " + ((TextBlock)repoWindow.FindName("NoticeText")).Text);
            var comparisonGrid = (DataGrid)inspections[0].FindName("ComparisonRows");
            if (comparisonGrid.Items.Count != 5 || !comparisonGrid.Items.Cast<MoaVcs.Core.ComparisonRow>().Any(r => r.Kind == "added"))
                throw new InvalidOperationException("Diff view must show committed and added lines.");
            Render(inspections[0], Path.Combine(output, "diff.png"));
            ((MenuItem)repoWindow.FindName("FileBlameButton")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (inspections.Count < 2 && DateTime.UtcNow < deadline && ((Button)repoWindow.FindName("CancelButton")).Visibility == Visibility.Visible) Pump();
            if (inspections.Count != 2) throw new InvalidOperationException("Blame command did not open viewer: " + ((TextBlock)repoWindow.FindName("NoticeText")).Text);
            var blameGrid = (DataGrid)inspections[1].FindName("BlameRows");
            if (blameGrid.Items.Count != 3 || blameGrid.Items.Cast<MoaVcs.Core.BlameLine>().Any(l => l.Author != "MoaVCS UI test"))
                throw new InvalidOperationException("Blame must show the committed baseline and author.");
            Render(inspections[1], Path.Combine(output, "blame.png"));
            var shellInspections = new List<Window>();
            var shellWindow = new MainWindow(new LaunchOptions(Path.Combine(_fixture, "unchanged.txt"), "blame"), settings, shellInspections.Add);
            shellWindow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (shellInspections.Count == 0 && DateTime.UtcNow < deadline) Pump();
            if (shellInspections.Count != 1 || ((DataGrid)shellInspections[0].FindName("BlameRows")).Items.Count != 1)
                throw new InvalidOperationException("Explorer blame must open an unchanged file directly: " + ((TextBlock)shellWindow.FindName("NoticeText")).Text);
            ((TabControl)repoWindow.FindName("MainTabs")).SelectedItem = repoWindow.FindName("HistoryTab");
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (((Button)repoWindow.FindName("CancelButton")).Visibility == Visibility.Visible && DateTime.UtcNow < deadline) Pump();
            if (((Button)repoWindow.FindName("CancelButton")).Visibility == Visibility.Visible) throw new TimeoutException("History load did not finish.");
            if (((DataGrid)repoWindow.FindName("HistoryGrid")).Items.Count != 1) throw new InvalidOperationException("History view did not load the fixture commit.");
            RunHistoryDetailsSmoke(settings, scratch, output);
            RunHistoryInformationSmoke(scratch, output);
            RunStartupActionSmoke(settings, _fixture, scratch);
            RunRepositorySidebarSmoke(scratch, output);
            RunCheckoutDialogSmoke(scratch, output);
            RunCancelledOperationSmoke(settings, _fixture);
            RunCommitStagingSmoke(settings, scratch);
            RunConflictResolutionSmoke(settings, scratch, output);
            RunFirstPushSmoke(scratch);
            RunRemoteSettingsSmoke(settings, _fixture, output);
            // Settings dialog initialization and layout must not install registry keys.
            var opened = new List<Uri>();
            var settingsWindow = new SettingsWindow(new AppSettings(), settings, new MoaVcs.Core.ProcessCommandRunner(), opened.Add);
            var settingsTabs = FindSettingsTabs(settingsWindow);
            SelectSettingsTab(settingsWindow, settingsTabs, 0);
            Render(settingsWindow, Path.Combine(output, "settings.png"));
            RenderAtMinimum(settingsWindow, Path.Combine(output, "settings-min.png"));
            foreach (var kind in Enum.GetValues<MoaVcs.Core.VcsKind>())
            {
                var download = Descendants((DependencyObject)settingsWindow.Content).OfType<Button>().Single(button => button.Name == "Download" + kind);
                download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (opened[^1] != VcsTools.Get(kind).DownloadUrl) throw new InvalidOperationException("Wrong download URL for " + kind);
            }
            if (opened.Count != 4) throw new InvalidOperationException("All VCS download buttons must work.");
            SelectSettingsTab(settingsWindow, settingsTabs, 1);
            Render(settingsWindow, Path.Combine(output, "settings-file-tools.png"));
            var toolOptions = Descendants((DependencyObject)settingsWindow.Content).OfType<CheckBox>().Where(c => Equals(c.Content, "외부 도구 사용")).ToList();
            if (toolOptions.Count != 2 || toolOptions.Any(c => c.IsChecked == true)) throw new InvalidOperationException("Diff and blame must default to internal tools.");
            var fileToolsSettings = new SettingsWindow(new AppSettings(), settings, new MoaVcs.Core.ProcessCommandRunner(), opened.Add, fileTools: true);
            if (FindSettingsTabs(fileToolsSettings).SelectedIndex != 1)
                throw new InvalidOperationException("The file-tools shortcut must open the comparison and history settings tab.");
            SelectSettingsTab(fileToolsSettings, FindSettingsTabs(fileToolsSettings), 1);
            Render(fileToolsSettings, Path.Combine(output, "settings-file-tools-shortcut.png"));
            SelectSettingsTab(settingsWindow, settingsTabs, 2);
            Render(settingsWindow, Path.Combine(output, "settings-explorer.png"));
            if (!settingsTabs.Items.Cast<TabItem>().Any(tab => Equals(tab.Header, "탐색기")))
                throw new InvalidOperationException("Explorer menu and status settings must share the Explorer tab.");
            var menuOptions = Descendants((DependencyObject)settingsWindow.Content).OfType<CheckBox>()
                .Where(c => c.Name is "ExplorerCommitTopLevel" or "ExplorerUpdateTopLevel").ToList();
            if (menuOptions.Count != 2 || menuOptions.Any(c => c.IsChecked != true))
                throw new InvalidOperationException("Explorer shortcuts must default to the first menu level.");
            var explorerControls = Descendants((DependencyObject)settingsWindow.Content).ToList();
            if (explorerControls.FindIndex(control => control is Button { Name: "RegisterExplorerMenuButton" }) >
                explorerControls.FindIndex(control => control is TextBlock { Text: "파일·폴더 상태 배지" }))
                throw new InvalidOperationException("Context-menu registration controls must appear before the separate status-overlay section.");
            var overlayToggle = Descendants((DependencyObject)settingsWindow.Content).OfType<Button>().Single(button => button.Name == "ExplorerOverlayToggleButton");
            var overlayStatus = Descendants((DependencyObject)settingsWindow.Content).OfType<TextBlock>().Single(block => block.Name == "ExplorerOverlayStatus");
            var overlayHelp = Descendants((DependencyObject)settingsWindow.Content).OfType<Expander>().Single(expander => Equals(expander.Header, "설치 및 표시 안내"));
            if (overlayToggle.Content?.ToString() != "상태 배지 켜기" || string.IsNullOrWhiteSpace(overlayStatus.Text))
                throw new InvalidOperationException("Explorer status overlays must default to off and explain their current state.");
            if (overlayHelp.Content is not TextBlock helpText || !helpText.Text.Contains("15개 슬롯", StringComparison.Ordinal) ||
                !helpText.Text.Contains("관리자 권한", StringComparison.Ordinal))
                throw new InvalidOperationException("Explorer overlay help must explain elevation and the Windows overlay limit.");
            var overlayScope = Descendants((DependencyObject)settingsWindow.Content).OfType<TextBlock>().Single(block => block.Name == "ExplorerOverlayScope");
            if (!overlayScope.Text.Contains("로컬 고정 드라이브", StringComparison.Ordinal) ||
                !overlayScope.Text.Contains("최대 12개", StringComparison.Ordinal) ||
                !overlayScope.Text.Contains("충돌, 수정됨, 추적 안 됨 순", StringComparison.Ordinal))
                throw new InvalidOperationException("Explorer overlay scope must explain that network and removable repositories are excluded.");
            var overlayScratch = Path.Combine(scratch, "overlay-settings-smoke-" + Guid.NewGuid().ToString("N") + ".json");
            var overlaySettings = new AppSettings();
            _ = new SettingsWindow(overlaySettings, new SettingsStore(overlayScratch), new MoaVcs.Core.ProcessCommandRunner());
            if (overlaySettings.ExplorerOverlaysEnabled || File.Exists(overlayScratch))
                throw new InvalidOperationException("Opening settings must not enable or persist Explorer overlays.");
            var submenuSettingsWindow = new SettingsWindow(new AppSettings
            {
                ExplorerMenu = new ExplorerMenuSettings { ShowCommitAtTopLevel = false, ShowUpdateAtTopLevel = false }
            }, settings, new MoaVcs.Core.ProcessCommandRunner(), opened.Add);
            var submenuTabs = FindSettingsTabs(submenuSettingsWindow);
            SelectSettingsTab(submenuSettingsWindow, submenuTabs, 2);
            var submenuContent = (FrameworkElement)submenuSettingsWindow.Content;
            var submenuSize = new Size(submenuSettingsWindow.Width, submenuSettingsWindow.Height - 32);
            submenuContent.Measure(submenuSize);
            submenuContent.Arrange(new Rect(submenuSize));
            submenuContent.UpdateLayout();
            var savedMenuOptions = Descendants((DependencyObject)submenuSettingsWindow.Content).OfType<CheckBox>()
                .Where(c => c.Name is "ExplorerCommitTopLevel" or "ExplorerUpdateTopLevel").ToList();
            if (savedMenuOptions.Count != 2 || savedMenuOptions.Any(c => c.IsChecked != false))
                throw new InvalidOperationException("Explorer settings must display the saved submenu choices.");
            var createWindow = new CreateRepositoryWindow(kind => kind switch
            {
                MoaVcs.Core.VcsKind.Git => new MoaVcs.Core.Providers.GitProvider(new MoaVcs.Core.ProcessCommandRunner()),
                MoaVcs.Core.VcsKind.Svn => new MoaVcs.Core.Providers.SvnProvider(),
                MoaVcs.Core.VcsKind.Perforce => new MoaVcs.Core.Providers.PerforceProvider(),
                _ => new MoaVcs.Core.Providers.LoreProvider()
            });
            Render(createWindow, Path.Combine(output, "create.png"));
            var loreExecutable = Environment.GetEnvironmentVariable("MOAVCS_LORE_EXE");
            if (string.IsNullOrWhiteSpace(loreExecutable))
            {
                Console.WriteLine("SKIP: Lore UI smoke (MOAVCS_LORE_EXE is not set).");
            }
            else
            {
                RunLoreInspectionSmoke(loreExecutable, output);
            }
            Console.WriteLine($"PASS: WPF welcome, repository, diff and blame commands, history, settings, downloads, create and layout rendering. {output}");
            app.Shutdown();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Application.Current?.Shutdown(); return 1; }
        finally
        {
            DeleteSmokePath(_fixture);
            DeleteSmokePath(_commitFixture);
            DeleteSmokePath(_historyFixture);
            DeleteSmokePath(_conflictFixture);
            DeleteSmokePath(_loreFixture);
            if (_loreSettingsFile is not null && IsInsideLocal(_loreSettingsFile) && File.Exists(_loreSettingsFile))
                File.Delete(_loreSettingsFile);
        }
    }

    private static void RunHistoryDetailsSmoke(SettingsStore settings, string scratch, string output)
    {
        var fixture = _historyFixture = Path.Combine(scratch, "ui-history-details-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        GitAt(fixture, "init", "--initial-branch=main");
        GitAt(fixture, "config", "user.name", "MoaVCS history test");
        GitAt(fixture, "config", "user.email", "history-test@example.invalid");
        File.WriteAllText(Path.Combine(fixture, "kept.txt"), "original line\nsecond line\n");
        File.WriteAllText(Path.Combine(fixture, "removed.txt"), "old file\n");
        GitAt(fixture, "add", "-A");
        GitAt(fixture, "commit", "-m", "Initial history fixture");

        File.WriteAllText(Path.Combine(fixture, "kept.txt"), "changed line\nsecond line\ncommit-only text\n");
        File.Delete(Path.Combine(fixture, "removed.txt"));
        File.WriteAllText(Path.Combine(fixture, "added.txt"), "new committed file\n");
        const string fullMessage = "History detail summary\n\nThis body has the first explanatory line.\nIt also keeps a second line.";
        GitAt(fixture, "add", "-A");
        GitAt(fixture, "commit", "--cleanup=verbatim", "-m", fullMessage);
        File.AppendAllText(Path.Combine(fixture, "kept.txt"), "worktree-only text\n");

        var window = new MainWindow(new LaunchOptions(fixture), settings);
        try
        {
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        WaitUntilRepositoryReady(window, TimeSpan.FromSeconds(20), "History-detail fixture did not load.");
        var tabs = (TabControl)window.FindName("MainTabs");
        tabs.SelectedItem = window.FindName("HistoryTab");
        WaitUntilIdle(window, TimeSpan.FromSeconds(20), "History-detail list did not load.");
        var history = (DataGrid)window.FindName("HistoryGrid");
        var files = (DataGrid)window.FindName("HistoryFilesGrid");
        var status = (TextBlock)window.FindName("HistoryDetailStatus");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((files.Items.Count != 3 || !status.Text.StartsWith("변경 파일", StringComparison.Ordinal)) && DateTime.UtcNow < deadline) Pump();
        if (history.Items.Count != 2 || files.Items.Count != 3)
            throw new InvalidOperationException("Selecting the newest commit must show its three changed files.");
        if (((TextBox)window.FindName("HistoryDetailMessage")).Text != fullMessage)
            throw new InvalidOperationException("Commit details must display the full multi-line commit message.");
        if (!((TextBlock)window.FindName("HistoryDetailMetadata")).Text.Contains("MoaVCS history test", StringComparison.Ordinal) ||
            !((TextBox)window.FindName("HistoryDetailRevision")).Text.Contains(((HistoryEntry)history.SelectedItem).Id, StringComparison.Ordinal))
            throw new InvalidOperationException("Commit details must show author and a copyable full revision identifier.");
        var paths = files.Items.Cast<FileChange>().ToDictionary(change => change.Path, change => change.Status, StringComparer.Ordinal);
        if (!paths.ContainsKey("kept.txt") || !paths.ContainsKey("added.txt") || !paths.ContainsKey("removed.txt") ||
            !paths["added.txt"].Contains("add", StringComparison.OrdinalIgnoreCase) ||
            !paths["removed.txt"].Contains("delet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Commit details must list modified, added, and deleted files.");
        var diff = (RichTextBox)window.FindName("HistoryDiffViewer");
        files.SelectedItem = files.Items.Cast<FileChange>().Single(change => change.Path == "kept.txt");
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (!new TextRange(diff.Document.ContentStart, diff.Document.ContentEnd).Text.Contains("commit-only text", StringComparison.Ordinal) && DateTime.UtcNow < deadline) Pump();
        var diffText = new TextRange(diff.Document.ContentStart, diff.Document.ContentEnd).Text;
        if (!diffText.Contains("commit-only text", StringComparison.Ordinal) || diffText.Contains("worktree-only text", StringComparison.Ordinal))
            throw new InvalidOperationException("History diff must compare the selected commit, not the current working tree.");

        files.SelectedItem = files.Items.Cast<FileChange>().Single(change => change.Path == "removed.txt");
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (!new TextRange(diff.Document.ContentStart, diff.Document.ContentEnd).Text.Contains("old file", StringComparison.Ordinal) && DateTime.UtcNow < deadline) Pump();
        if (!new TextRange(diff.Document.ContentStart, diff.Document.ContentEnd).Text.Contains("old file", StringComparison.Ordinal))
            throw new InvalidOperationException("Selecting another changed file must load that file's historical diff.");
        files.SelectedItem = files.Items.Cast<FileChange>().Single(change => change.Path == "kept.txt");
        files.SelectedItem = files.Items.Cast<FileChange>().Single(change => change.Path == "added.txt");
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (!new TextRange(diff.Document.ContentStart, diff.Document.ContentEnd).Text.Contains("new committed file", StringComparison.Ordinal) && DateTime.UtcNow < deadline) Pump();
        if (!new TextRange(diff.Document.ContentStart, diff.Document.ContentEnd).Text.Contains("new committed file", StringComparison.Ordinal))
            throw new InvalidOperationException("Rapid changed-file selection must leave the diff on the latest selected file.");

        var older = (HistoryEntry)history.Items[1];
        var newest = (HistoryEntry)history.Items[0];
        history.SelectedItem = older;
        history.SelectedItem = newest;
        deadline = DateTime.UtcNow.AddSeconds(30);
        while ((((TextBox)window.FindName("HistoryDetailRevision")).Text != "리비전 " + newest.Id ||
                ((TextBox)window.FindName("HistoryDetailMessage")).Text != fullMessage) && DateTime.UtcNow < deadline) Pump();
        if (((TextBox)window.FindName("HistoryDetailRevision")).Text != "리비전 " + newest.Id ||
            ((TextBox)window.FindName("HistoryDetailMessage")).Text != fullMessage)
            throw new InvalidOperationException("Rapid commit selection must leave the detail pane on the latest selected commit.");
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (!new TextRange(diff.Document.ContentStart, diff.Document.ContentEnd).Text.Contains("new committed file", StringComparison.Ordinal) && DateTime.UtcNow < deadline) Pump();
        if (!new TextRange(diff.Document.ContentStart, diff.Document.ContentEnd).Text.Contains("new committed file", StringComparison.Ordinal))
            throw new InvalidOperationException("The selected commit detail must finish loading its default file diff before rendering.");
        Render(window, Path.Combine(output, "history-details.png"));
        RenderAtMinimum(window, Path.Combine(output, "history-details-min.png"));
        Console.WriteLine("PASS: Git commit history shows full message, changed files, and selected-commit diff after rapid selection");
        }
        finally { window.Close(); }
    }

    private static void RunLoreInspectionSmoke(string loreExecutable, string output)
    {
        loreExecutable = Path.GetFullPath(loreExecutable.Trim());
        if (!File.Exists(loreExecutable)) throw new FileNotFoundException("MOAVCS_LORE_EXE must name the Lore executable.", loreExecutable);

        var scratch = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local"));
        Directory.CreateDirectory(scratch);
        _loreFixture = Path.Combine(scratch, "ui-lore-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_loreFixture);
        _loreSettingsFile = Path.Combine(scratch, "ui-lore-settings-" + Guid.NewGuid().ToString("N") + ".json");

        const string identity = "moavcs-ui-smoke@example.invalid";
        RunLore(loreExecutable, _loreFixture,
            "--repository", _loreFixture, "--offline", "--identity", identity, "repository", "create");

        const string sourceName = "Lore UI sample.txt";
        var sourcePath = Path.Combine(_loreFixture, sourceName);
        File.WriteAllText(sourcePath, "Lore baseline line\nstable line\nthird line\n", new System.Text.UTF8Encoding(false));
        RunLore(loreExecutable, _loreFixture, "--repository", _loreFixture, "stage", "--scan", "--", sourceName);
        RunLore(loreExecutable, _loreFixture,
            "--repository", _loreFixture, "--offline", "--identity", identity, "commit", "--", "Create Lore UI baseline");
        File.WriteAllText(sourcePath, "Lore changed line\nstable line\nthird line\nLore added line\n", new System.Text.UTF8Encoding(false));

        var settingsStore = new SettingsStore(_loreSettingsFile);
        settingsStore.Save(new AppSettings
        {
            ExecutablePaths = new Dictionary<MoaVcs.Core.VcsKind, string>
            {
                [MoaVcs.Core.VcsKind.Lore] = loreExecutable
            }
        });

        var inspections = new List<Window>();
        var window = new MainWindow(new LaunchOptions(_loreFixture), settingsStore, inspections.Add);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (((Grid)window.FindName("RepositoryPanel")).Visibility != Visibility.Visible && DateTime.UtcNow < deadline) Pump();
        while (((Button)window.FindName("CancelButton")).Visibility == Visibility.Visible && DateTime.UtcNow < deadline) Pump();
        if (((Button)window.FindName("CancelButton")).Visibility == Visibility.Visible) throw new TimeoutException("Lore repository load did not finish.");
        var notice = ((TextBlock)window.FindName("NoticeText")).Text;
        if (((Grid)window.FindName("RepositoryPanel")).Visibility != Visibility.Visible || !string.IsNullOrWhiteSpace(notice))
            throw new InvalidOperationException("Lore repository did not load: " + notice);

        var changes = (DataGrid)window.FindName("ChangesGrid");
        var changed = changes.Items.Cast<MoaVcs.Core.FileChange>().SingleOrDefault(change => change.Path == sourceName);
        if (changed is null) throw new InvalidOperationException("Lore status did not detect the modified fixture file.");
        changes.SelectedItem = changed;

        ((MenuItem)window.FindName("FileDiffButton")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        deadline = DateTime.UtcNow.AddSeconds(45);
        while (inspections.Count < 1 && DateTime.UtcNow < deadline) Pump();
        if (inspections.Count < 1) throw new InvalidOperationException("Lore compare did not open an inspection window: " + ((TextBlock)window.FindName("NoticeText")).Text);
        var comparison = (DataGrid)inspections[0].FindName("ComparisonRows");
        var comparisonRows = comparison.Items.Cast<MoaVcs.Core.ComparisonRow>().ToArray();
        if (!comparisonRows.Any(row => row.Kind == "modified" && row.LeftText == "Lore baseline line" && row.RightText == "Lore changed line") ||
            !comparisonRows.Any(row => row.Kind == "added" && row.RightText == "Lore added line"))
            throw new InvalidOperationException("Lore compare did not show the committed baseline against the modified working file.");
        Render(inspections[0], Path.Combine(output, "lore-diff.png"));

        ((MenuItem)window.FindName("FileBlameButton")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        deadline = DateTime.UtcNow.AddSeconds(45);
        while (inspections.Count < 2 && DateTime.UtcNow < deadline) Pump();
        if (inspections.Count < 2) throw new InvalidOperationException("Lore blame did not open an inspection window: " + ((TextBlock)window.FindName("NoticeText")).Text);
        var blame = (DataGrid)inspections[1].FindName("BlameRows");
        var blameRows = blame.Items.Cast<MoaVcs.Core.BlameLine>().ToArray();
        if (blameRows.Length != 3 || blameRows.Any(line => line.Author != identity || line.Revision.Length != 64) ||
            blameRows[0].Text != "Lore baseline line")
            throw new InvalidOperationException("Lore blame did not show the committed lines with their recorded author and revision.");
        Render(inspections[1], Path.Combine(output, "lore-blame.png"));
        Console.WriteLine($"PASS: Lore CLI {RunLore(loreExecutable, _loreFixture, "--version").Trim()} compare and blame smoke; {output}");
    }

    private static void RunRepositorySidebarSmoke(string scratch, string output)
    {
        var root = Path.Combine(scratch, "ui-repositories-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MainWindow? window = null;
        try
        {
            var active = Path.Combine(root, "현재 작업", "moavcs");
            Directory.CreateDirectory(active);
            GitAt(active, "init", "--initial-branch=main");
            GitAt(active, "config", "user.name", "MoaVCS sidebar test");
            GitAt(active, "config", "user.email", "sidebar-test@example.invalid");
            File.WriteAllText(Path.Combine(active, "keep.txt"), "repository files stay on disk");
            GitAt(active, "add", "keep.txt");
            GitAt(active, "commit", "-m", "사이드바 검증 기준");
            var client = Path.Combine(root, "클라이언트", "shared");
            var server = Path.Combine(root, "서버", "shared");
            Directory.CreateDirectory(client);
            Directory.CreateDirectory(server);
            File.WriteAllText(Path.Combine(server, "keep.txt"), "must survive history removal");
            var entries = new List<RecentRepository>
            {
                new(Path.Combine(root, "고정 작업", "design"), VcsKind.Git, true),
                new(Path.Combine(root, "고정 작업", "engine"), VcsKind.Lore, true),
                new(client, VcsKind.Git), new(server, VcsKind.Svn)
            };
            entries.AddRange(Enumerable.Range(1, 6).Select(index =>
                new RecentRepository(Path.Combine(root, "최근 작업", "project-" + index), VcsKind.Git)));
            var store = new SettingsStore(Path.Combine(root, "settings.json"));
            store.Save(new AppSettings { RecentRepositories = entries });
            window = new MainWindow(new LaunchOptions(active), store, showDialog: dialog =>
            {
                var save = Descendants((DependencyObject)dialog.Content).OfType<Button>().Single(button => button.Name == "SaveSettingsButton");
                var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                timeout.Tick += (_, _) => { timeout.Stop(); dialog.Close(); };
                dialog.Dispatcher.BeginInvoke(new Action(() => save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                timeout.Start();
                var result = dialog.ShowDialog();
                timeout.Stop();
                return result;
            });
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            WaitUntilRepositoryReady(window, TimeSpan.FromSeconds(20), "Sidebar fixture did not load.");
            var recent = (ItemsControl)window.FindName("RecentList");
            var pinned = (ItemsControl)window.FindName("PinnedList");
            var search = (TextBox)window.FindName("RepositorySearchBox");
            if (recent.Items.Count != 9 || pinned.Items.Count != 2 ||
                !recent.Items.Cast<RecentItem>().Single(item => item.Path == active).IsCurrent ||
                recent.Items.Cast<RecentItem>().Count(item => item.IsCurrent) != 1)
                throw new InvalidOperationException("Sidebar must show all recent items, pinned items and only the active repository highlight.");
            if (recent.Items.Cast<RecentItem>().Single(item => item.Path == client).ParentPath.Length == 0)
                throw new InvalidOperationException("Same-name repositories need a parent-path label.");
            Render(window, Path.Combine(output, "sidebar-repositories.png"));
            RenderAtMinimum(window, Path.Combine(output, "sidebar-repositories-min.png"));

            search.Text = "클라이언트";
            if (recent.Items.Count != 1 || ((RecentItem)recent.Items[0]).Path != client || pinned.Items.Count != 0)
                throw new InvalidOperationException("Sidebar search must filter full paths as well as names.");
            search.Text = "SVN";
            if (recent.Items.Count != 1 || ((RecentItem)recent.Items[0]).Path != server)
                throw new InvalidOperationException("Sidebar search must filter provider names.");
            search.Text = "no matching repository";
            if (recent.Items.Count != 0 || pinned.Items.Count != 0 || store.Load().RecentRepositories.Count != 11)
                throw new InvalidOperationException("An empty search result must not delete stored history.");
            search.Text = "";

            void InvokeItemMenu(string path, string menuName)
            {
                Render(window, Path.Combine(output, "sidebar-menu-state.png"));
                var button = Descendants((DependencyObject)window.Content).OfType<Button>().Single(button =>
                    button.Name == "RepositoryItemMenuButton" && button.Tag is RecentItem item && item.Path == path);
                var context = button.ContextMenu ?? throw new InvalidOperationException("Repository menu is missing.");
                context.PlacementTarget = button;
                context.DataContext = button.Tag;
                var command = context.Items.OfType<MenuItem>().Single(item => item.Name == menuName);
                command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            InvokeItemMenu(client, "RepositoryPinMenuItem");
            if (pinned.Items.Count != 3 || recent.Items.Count != 8 ||
                !store.Load().RecentRepositories.Single(item => item.Path == client).IsPinned)
                throw new InvalidOperationException("Pinning must move an item into the pinned group and persist it.");

            var staleSettings = new SettingsWindow(store.Load(), store, new ProcessCommandRunner());
            InvokeItemMenu(server, "RepositoryRemoveMenuItem");
            if (store.Load().RecentRepositories.Any(item => item.Path == server) ||
                File.ReadAllText(Path.Combine(server, "keep.txt")) != "must survive history removal")
                throw new InvalidOperationException("Removing history must remove only the selected record and preserve repository files.");
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timeout.Tick += (_, _) => { timeout.Stop(); staleSettings.Close(); };
            staleSettings.Dispatcher.BeginInvoke(new Action(() =>
                Descendants((DependencyObject)staleSettings.Content).OfType<Button>().Single(button => button.Name == "SaveSettingsButton")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            timeout.Start();
            var saved = staleSettings.ShowDialog();
            timeout.Stop();
            if (saved != true || store.Load().RecentRepositories.Any(item => item.Path == server))
                throw new InvalidOperationException("A settings dialog opened before history removal must not restore the deleted record.");

            var header = (Button)window.FindName("RepositoryListMenuButton");
            var clear = header.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Name == "ClearRecentMenuItem");
            clear.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var remaining = store.Load().RecentRepositories;
            if (recent.Items.Count != 0 || pinned.Items.Count != 3 || remaining.Count != 3 || remaining.Any(item => !item.IsPinned) ||
                ((TextBlock)window.FindName("RepoPath")).Text != active || !File.Exists(Path.Combine(active, "keep.txt")))
                throw new InvalidOperationException("Clearing recent history must preserve pins, the open workspace and all repository files.");
            InvokeItemMenu(client, "RepositoryPinMenuItem");
            if (pinned.Items.Count != 2 || recent.Items.Count != 1 || store.Load().RecentRepositories.Single(item => item.Path == client).IsPinned)
                throw new InvalidOperationException("Unpinning must return the selected repository to recent history.");
            ((Button)window.FindName("SidebarSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntilIdle(window, TimeSpan.FromSeconds(20), "Settings refresh did not finish.");
            if (store.Load().RecentRepositories.Any(item => item.Path == active) || recent.Items.Count != 1 || pinned.Items.Count != 2)
                throw new InvalidOperationException("Saving preferences must not re-add the open repository after it was removed from history.");
            Render(window, Path.Combine(output, "sidebar-cleaned.png"));
            Console.WriteLine("PASS: sidebar search, complete history, active highlight, pin/unpin, remove, clear and stale-settings preservation");
        }
        finally
        {
            window?.Close();
            DeleteSmokePath(root);
        }
    }

    private static void RunCheckoutDialogSmoke(string scratch, string output)
    {
        var root = Path.Combine(scratch, "ui-checkout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsFile = Path.Combine(root, "settings.json");
        MainWindow? cloneHost = null;
        try
        {
            IVcsProvider Provider(VcsKind kind) => kind switch
            {
                VcsKind.Git => new MoaVcs.Core.Providers.GitProvider(new ProcessCommandRunner()),
                VcsKind.Svn => new MoaVcs.Core.Providers.SvnProvider(),
                VcsKind.Lore => new MoaVcs.Core.Providers.LoreProvider(),
                _ => new MoaVcs.Core.Providers.PerforceProvider()
            };
            TextBox Field(CreateRepositoryWindow window, string name) => (TextBox)window.FindName(name);
            void EqualPath(string expected, string actual, string reason)
            {
                if (!Path.GetFullPath(expected).Equals(Path.GetFullPath(actual), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(reason + $" Expected={expected}; actual={actual}");
            }

            var selectedParent = Path.Combine(root, "선택한 상위 폴더");
            Directory.CreateDirectory(selectedParent);
            string? pickerInitial = null;
            var dialog = new CreateRepositoryWindow(Provider, root, VcsOperation.Clone, VcsKind.Git,
                pickFolder: (_, initial) => { pickerInitial = initial; return selectedParent; });
            EqualPath(root, dialog.ParentDirectoryPath, "Explorer folder must be prefilled.");
            var remote = Field(dialog, "CloneRemoteUrl");
            var folder = Field(dialog, "CloneFolderName");
            remote.Text = "https://example.invalid/team/moavcs.git";
            EqualPath(Path.Combine(root, "moavcs"), dialog.DestinationPath, "Remote name must form a child folder.");
            remote.Text = "git@example.invalid:team/새 프로젝트.git";
            EqualPath(Path.Combine(root, "새 프로젝트"), dialog.DestinationPath, "Remote changes must update an automatic folder name.");
            ((Button)dialog.FindName("CreateBrowseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            EqualPath(root, pickerInitial!, "Folder picker must start in the current parent.");
            EqualPath(Path.Combine(selectedParent, "새 프로젝트"), dialog.DestinationPath, "Browsing must preserve the proposed repository child.");
            folder.Text = "내 작업 폴더";
            remote.Text = "https://example.invalid/team/another.git";
            EqualPath(Path.Combine(selectedParent, "내 작업 폴더"), dialog.DestinationPath, "Explicit folder names must survive remote changes.");
            Field(dialog, "CloneParentPath").Text = root;
            EqualPath(Path.Combine(root, "내 작업 폴더"), dialog.DestinationPath, "Typed parent changes must update the final preview.");
            folder.Text = "";
            remote.Text = "https://example.invalid/team/restored.git";
            EqualPath(Path.Combine(root, "restored"), dialog.DestinationPath, "Clearing a custom name must allow a new automatic proposal.");
            var mode = (ComboBox)dialog.FindName("CreateModePicker");
            mode.SelectedIndex = 0;
            EqualPath(root, dialog.DestinationPath, "Initialize must target the selected folder itself.");
            if (remote.IsEnabled) throw new InvalidOperationException("Initialize must disable remote input.");
            mode.SelectedIndex = 1;
            EqualPath(Path.Combine(root, "restored"), dialog.DestinationPath, "Returning to clone must restore its child-folder proposal.");
            Render(dialog, Path.Combine(output, "create-checkout.png"));
            dialog.Close();

            var svn = new CreateRepositoryWindow(Provider, root, VcsOperation.Clone, VcsKind.Svn);
            Field(svn, "CloneRemoteUrl").Text = "https://example.invalid/svn/project/trunk/";
            EqualPath(Path.Combine(root, "trunk"), svn.DestinationPath, "SVN checkout must use the URL path name.");
            Render(svn, Path.Combine(output, "create-checkout-svn.png"));
            svn.Close();

            var source = Path.Combine(root, "원격 원본.git");
            Directory.CreateDirectory(source);
            GitAt(source, "init", "--initial-branch=main");
            GitAt(source, "config", "user.name", "MoaVCS checkout test");
            GitAt(source, "config", "user.email", "checkout-test@example.invalid");
            File.WriteAllText(Path.Combine(source, "committed.txt"), "local clone content\n");
            GitAt(source, "add", "committed.txt");
            GitAt(source, "commit", "-m", "체크아웃 검증 기준 커밋");
            // The clicked directory may already contain files. Only its new child must be empty.
            File.WriteAllText(Path.Combine(selectedParent, "keep.txt"), "existing parent content");
            var settings = new SettingsStore(settingsFile);
            settings.Save(new AppSettings());
            cloneHost = new MainWindow(new LaunchOptions(selectedParent, "clone", VcsKind.Git), settings,
                showDialog: window =>
                {
                    var create = (CreateRepositoryWindow)window;
                    Field(create, "CloneRemoteUrl").Text = source;
                    EqualPath(Path.Combine(selectedParent, "원격 원본"), create.DestinationPath, "Explorer clone must combine the clicked parent and source repository name.");
                    create.Dispatcher.BeginInvoke(new Action(() =>
                        ((Button)create.FindName("CreateRepositoryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                    return create.ShowDialog();
                });
            cloneHost.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            WaitUntilRepositoryReady(cloneHost, TimeSpan.FromSeconds(30), "Explorer local clone did not open its destination.");
            var destination = Path.Combine(selectedParent, "원격 원본");
            EqualPath(destination, ((TextBlock)cloneHost.FindName("RepoPath")).Text, "Successful clone must open its proposed final folder.");
            if (File.ReadAllText(Path.Combine(destination, "committed.txt")).Replace("\r\n", "\n") != "local clone content\n" ||
                File.ReadAllText(Path.Combine(selectedParent, "keep.txt")) != "existing parent content" ||
                GitAtOutput(destination, "rev-parse", "HEAD").Trim() != GitAtOutput(source, "rev-parse", "HEAD").Trim())
                throw new InvalidOperationException("Local clone must preserve the parent and source commit.");
            Console.WriteLine("PASS: checkout parent context, automatic/custom repository folder names, browse, mode changes and actual local Git clone");
        }
        finally
        {
            cloneHost?.Close();
            DeleteSmokePath(root);
        }
    }

    private static void RunStartupActionSmoke(SettingsStore settings, string fixture, string scratch)
    {
        var parsed = new[] { "update", "push", "clone", "init", "settings" }
            .Select(action => LaunchOptions.Parse(["--action", action]).Action)
            .ToArray();
        if (!parsed.SequenceEqual(["update", "push", "clone", "init", "settings"]))
            throw new InvalidOperationException("The Explorer action parser did not accept all supported startup actions.");

        Window? shown = null;
        var cloneWindow = new MainWindow(new LaunchOptions(fixture, "clone"), settings,
            showDialog: dialog => { shown = dialog; return false; });
        cloneWindow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Pump();
        var cloneNotice = ((TextBlock)cloneWindow.FindName("NoticeText")).Text;
        if (shown is not CreateRepositoryWindow clone || clone.SelectedOperation != MoaVcs.Core.VcsOperation.Clone ||
            !Path.GetFullPath(clone.ParentDirectoryPath).Equals(Path.GetFullPath(fixture), StringComparison.OrdinalIgnoreCase) || clone.Request is not null ||
            ((Grid)cloneWindow.FindName("RepositoryPanel")).Visibility != Visibility.Collapsed)
            throw new InvalidOperationException($"Explorer clone should prefill the clicked parent folder without opening or modifying that repository. Dialog={shown?.GetType().Name ?? "<none>"}; notice={cloneNotice}");

        var initPath = Path.Combine(scratch, "ui-action-init-" + Guid.NewGuid().ToString("N"));
        shown = null;
        var initWindow = new MainWindow(new LaunchOptions(initPath, "init"), settings,
            showDialog: dialog => { shown = dialog; return false; });
        initWindow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Pump();
        if (shown is not CreateRepositoryWindow init || init.SelectedOperation != MoaVcs.Core.VcsOperation.Initialize ||
            !Path.GetFullPath(init.DestinationPath).Equals(Path.GetFullPath(initPath), StringComparison.OrdinalIgnoreCase) ||
            init.Request is not null || Directory.Exists(initPath) ||
            ((Grid)initWindow.FindName("RepositoryPanel")).Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Explorer init should prefill the selected target and wait for dialog confirmation before creating it.");

        shown = null;
        var settingsWindow = new MainWindow(new LaunchOptions(fixture, "settings"), settings,
            showDialog: dialog => { shown = dialog; return false; });
        settingsWindow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Pump();
        if (shown is not SettingsWindow ||
            ((Grid)settingsWindow.FindName("RepositoryPanel")).Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Explorer settings should open its dialog without trying to detect the selected repository.");
    }

    private static void RunRemoteSettingsSmoke(SettingsStore settings, string fixture, string output)
    {
        const string original = "https://example.invalid/original.git";
        const string updated = "git@example.invalid:team/새 저장소.git";
        const string externalChange = "https://example.invalid/changed-elsewhere.git";
        Git("remote", "add", "origin", original);
        var head = GitOutput("rev-parse", "HEAD");
        var status = GitOutput("status", "--porcelain=v1", "-z");

        var canceled = Open(dialog =>
        {
            if (dialog.Url != original) throw new InvalidOperationException("Remote dialog must load the configured origin URL.");
            Render(dialog, Path.Combine(output, "remote-settings.png"));
            dialog.RemoteUrlTextBox.Text = " ";
            Descendants((DependencyObject)dialog.Content).OfType<Button>().Single(b => b.Name == "SaveRemoteButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (dialog.DialogResult == true || string.IsNullOrWhiteSpace(Descendants((DependencyObject)dialog.Content)
                    .OfType<TextBlock>().Single(t => t.Name == "RemoteValidationNotice").Text))
                throw new InvalidOperationException("Invalid remote input must keep the dialog open with an error.");
            dialog.RemoteUrlTextBox.Text = updated;
            return false;
        });
        if (GitOutput("remote", "get-url", "origin").Trim() != original)
            throw new InvalidOperationException("Canceling remote settings changed origin.");
        if (((MenuItem)canceled.FindName("RemoteButton")).IsEnabled != true)
            throw new InvalidOperationException("The repository origin button must be available after loading Git.");
        canceled.Width = 1100;
        canceled.Height = 740;
        Render(canceled, Path.Combine(output, "workspace-remote-min.png"));

        // Exercise the toolbar as well as the Explorer startup action.
        var saved = Open(dialog => { dialog.RemoteUrlTextBox.Text = updated; return true; }, fromToolbar: true);
        if (GitOutput("remote", "get-url", "origin").Trim() != updated)
            throw new InvalidOperationException("Remote settings Save did not update origin.");
        if (((TextBox)saved.FindName("OutputLog")).Text.Contains(updated, StringComparison.Ordinal))
            throw new InvalidOperationException("Remote URL must not be copied into the operation log.");

        var stale = Open(dialog =>
        {
            if (dialog.Url != updated) throw new InvalidOperationException("Reopening remote settings must load the saved URL.");
            Git("remote", "set-url", "origin", externalChange);
            dialog.RemoteUrlTextBox.Text = original;
            return true;
        });
        if (GitOutput("remote", "get-url", "origin").Trim() != externalChange ||
            string.IsNullOrWhiteSpace(((TextBlock)stale.FindName("NoticeText")).Text))
            throw new InvalidOperationException("Stale remote settings must report an error and preserve the external change.");
        if (GitOutput("rev-parse", "HEAD") != head || GitOutput("status", "--porcelain=v1", "-z") != status)
            throw new InvalidOperationException("Origin settings must preserve HEAD, index and worktree state.");

        var readOnly = new RemoteSettingsWindow(fixture, new MoaVcs.Core.RemoteInfo("origin", true,
            [original], ["ssh://git@example.invalid/push.git"], [original], ["ssh://git@example.invalid/push.git"],
            "외부 설정에서 정의된 주소입니다."));
        Render(readOnly, Path.Combine(output, "remote-settings-readonly.png"));
        var saveButton = Descendants((DependencyObject)readOnly.Content).OfType<Button>().Single(b => b.Name == "SaveRemoteButton");
        if (saveButton.IsEnabled || !readOnly.RemoteUrlTextBox.IsReadOnly)
            throw new InvalidOperationException("Read-only origin settings must prevent editing and saving.");
        Console.WriteLine("PASS: Git origin dialog, toolbar and Explorer actions, save/cancel, validation and stale-setting protection");

        MainWindow Open(Func<RemoteSettingsWindow, bool?> respond, bool fromToolbar = false)
        {
            var shown = false;
            var window = new MainWindow(new LaunchOptions(fixture, fromToolbar ? "open" : "remote"), settings,
                showDialog: dialog =>
                {
                    if (dialog is not RemoteSettingsWindow remote) throw new InvalidOperationException("Expected the origin dialog.");
                    shown = true;
                    return respond(remote);
                });
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var deadline = DateTime.UtcNow.AddSeconds(20);
            if (fromToolbar)
            {
                while ((((Grid)window.FindName("RepositoryPanel")).Visibility != Visibility.Visible ||
                        ((Button)window.FindName("CancelButton")).Visibility == Visibility.Visible) && DateTime.UtcNow < deadline) Pump();
                ((MenuItem)window.FindName("RemoteButton")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            while ((!shown || ((Button)window.FindName("CancelButton")).Visibility == Visibility.Visible) && DateTime.UtcNow < deadline) Pump();
            if (!shown || ((Button)window.FindName("CancelButton")).Visibility == Visibility.Visible)
                throw new TimeoutException("Origin settings did not finish: " + ((TextBlock)window.FindName("NoticeText")).Text);
            return window;
        }
    }

    private static void RunCancelledOperationSmoke(SettingsStore settings, string fixture)
    {
        var head = GitOutput("rev-parse", "HEAD").Trim();
        var status = GitOutput("status", "--porcelain=v1", "-z");
        var branchConfig = GitBranchConfig();
        foreach (var (action, expectedLabel) in new[]
                 { ("update", "원격 변경 가져오기"), ("push", "원격 저장소에 보내기") })
        {
            var confirmations = new List<(string Label, string Detail)>();
            var window = new MainWindow(new LaunchOptions(fixture, action), settings,
                confirmOperation: (label, detail) => { confirmations.Add((label, detail)); return false; });
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (confirmations.Count == 0 && DateTime.UtcNow < deadline) Pump();
            if (confirmations.Count != 1 || confirmations[0].Label != expectedLabel)
                throw new InvalidOperationException($"Explorer {action} should reach the existing confirmation before running.");
            if (action == "push" && !confirmations[0].Detail.Contains(
                    "처음 푸시하는 브랜치는 기본 푸시가 성공하면 원격 추적 브랜치(upstream)를 설정합니다.", StringComparison.Ordinal))
                throw new InvalidOperationException("Push confirmation must explain first-push upstream setup.");
            if (((TextBox)window.FindName("OutputLog")).Text.Contains(expectedLabel, StringComparison.Ordinal))
                throw new InvalidOperationException($"Canceled Explorer {action} must not start the provider operation.");
            if (GitOutput("rev-parse", "HEAD").Trim() != head || GitOutput("status", "--porcelain=v1", "-z") != status ||
                GitBranchConfig() != branchConfig)
                throw new InvalidOperationException($"Canceled Explorer {action} changed the Git fixture.");
        }
    }

    private static void RunCommitStagingSmoke(SettingsStore settings, string scratch)
    {
        var fixture = _commitFixture = Path.Combine(scratch, "ui-commit-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        GitAt(fixture, "init", "--initial-branch=master");
        GitAt(fixture, "config", "user.name", "MoaVCS UI test");
        GitAt(fixture, "config", "user.email", "test@example.invalid");
        var file = Path.Combine(fixture, "commit flow.txt");
        File.WriteAllText(file, "baseline\n");
        GitAt(fixture, "add", "--", "commit flow.txt");
        GitAt(fixture, "commit", "-m", "Create commit flow fixture");
        File.WriteAllText(file, "baseline\nchanged\n");

        var window = new MainWindow(new LaunchOptions(fixture), settings);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        WaitUntilRepositoryReady(window, TimeSpan.FromSeconds(20), "Commit-flow fixture did not load.");
        var changes = (DataGrid)window.FindName("ChangesGrid");
        var commitButton = (Button)window.FindName("CommitButton");
        var message = (TextBox)window.FindName("CommitMessage");
        if (changes.Items.Count != 1 || changes.Items.Cast<MoaVcs.Core.FileChange>().Single().IsStaged)
            throw new InvalidOperationException("Commit-flow fixture should begin with only an unstaged modification.");
        message.Text = "Record the selected change";
        if (commitButton.IsEnabled)
            throw new InvalidOperationException("Commit must remain disabled until a change is staged.");

        changes.SelectedItem = changes.Items[0];
        ((Button)window.FindName("StageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitUntilIdle(window, TimeSpan.FromSeconds(20), "Staging the commit-flow fixture did not finish.");
        if (!changes.Items.Cast<MoaVcs.Core.FileChange>().Single().IsStaged || !commitButton.IsEnabled)
            throw new InvalidOperationException("A staged change and nonempty message should enable commit.");

        changes.SelectedItem = changes.Items[0];
        ((Button)window.FindName("UnstageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitUntilIdle(window, TimeSpan.FromSeconds(20), "Unstaging the commit-flow fixture did not finish.");
        if (changes.Items.Cast<MoaVcs.Core.FileChange>().Single().IsStaged || commitButton.IsEnabled)
            throw new InvalidOperationException("Unstaging the last change must disable commit again.");

        changes.SelectedItem = changes.Items[0];
        ((Button)window.FindName("StageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitUntilIdle(window, TimeSpan.FromSeconds(20), "Restaging the commit-flow fixture did not finish.");
        message.Clear();
        if (!changes.Items.Cast<MoaVcs.Core.FileChange>().Single().IsStaged || commitButton.IsEnabled)
            throw new InvalidOperationException("Commit must require a message even when changes are staged.");
        message.Text = "Ready to commit";
        if (!commitButton.IsEnabled)
            throw new InvalidOperationException("Commit should enable only when staged changes and a message are present.");
        Console.WriteLine("PASS: Git commit flow requires staging and a message; unstage disables commit");
    }

    private static void RunConflictResolutionSmoke(SettingsStore settings, string scratch, string output)
    {
        var fixture = _conflictFixture = Path.Combine(scratch, "ui-conflict-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        GitAt(fixture, "init", "--initial-branch=main");
        GitAt(fixture, "config", "user.name", "MoaVCS UI conflict test");
        GitAt(fixture, "config", "user.email", "conflict-test@example.invalid");

        const string fileName = "merge-conflict.txt";
        var filePath = Path.Combine(fixture, fileName);
        static string Content(string side)
        {
            var lines = new List<string> { "header" };
            for (var block = 1; block <= 4; block++)
            {
                lines.Add($"value-{block}={side}-{block}");
                for (var stable = 1; stable <= 12; stable++) lines.Add($"stable-{block}-{stable}");
            }
            lines.Add("footer");
            return string.Join('\n', lines) + "\n";
        }

        File.WriteAllText(filePath, Content("base"));
        GitAt(fixture, "add", "--", fileName);
        GitAt(fixture, "commit", "-m", "Create conflict smoke baseline");
        GitAt(fixture, "switch", "-c", "incoming");
        File.WriteAllText(filePath, Content("incoming"));
        GitAt(fixture, "commit", "-am", "Incoming conflict changes");
        GitAt(fixture, "switch", "main");
        File.WriteAllText(filePath, Content("main"));
        GitAt(fixture, "commit", "-am", "Local conflict changes");
        var merge = new MoaVcs.Core.ProcessCommandRunner().RunAsync(new MoaVcs.Core.CommandSpec(
            "git", ["merge", "incoming"], fixture)).GetAwaiter().GetResult();
        if (merge.Success || !GitAtOutput(fixture, "status", "--porcelain=v1", "--", fileName).StartsWith("UU ", StringComparison.Ordinal))
            throw new InvalidOperationException("The disposable Git fixture must contain a real two-sided merge conflict.");

        static FileInspectionWindow GetConflictWindow(IReadOnlyList<Window> windows, string entryPoint)
        {
            if (windows.Count != 1 || windows[0] is not FileInspectionWindow inspection ||
                inspection.FindName("ConflictEditor") is not ConflictResolutionView)
                throw new InvalidOperationException($"{entryPoint} must open the built-in conflict editor.");
            return inspection;
        }

        static void WaitForInspection(MainWindow main, List<Window> windows, string entryPoint)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (windows.Count == 0 && DateTime.UtcNow < deadline) Pump();
            if (windows.Count != 1) throw new TimeoutException($"{entryPoint} did not open within the UI smoke timeout.");
            WaitUntilIdle(main, TimeSpan.FromSeconds(30), $"{entryPoint} did not finish loading.");
        }

        static void WaitForConflictAction(ConflictResolutionView editor, MainWindow main, string description)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (editor.IsBusy && DateTime.UtcNow < deadline) Pump();
            if (editor.IsBusy) throw new TimeoutException(description);
            WaitUntilIdle(main, TimeSpan.FromSeconds(30), description);
        }

        // The Explorer --action diff path and the normal file-diff path must reach the
        // built-in conflict editor with the internal-tool settings used by this fixture.
        var shellInspections = new List<Window>();
        var shellWindow = new MainWindow(new LaunchOptions(filePath, "diff"), settings, shellInspections.Add);
        shellWindow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        WaitForInspection(shellWindow, shellInspections, "Explorer diff action");
        GetConflictWindow(shellInspections, "Explorer diff action").Close();

        var internalInspections = new List<Window>();
        var internalWindow = new MainWindow(new LaunchOptions(fixture), settings, internalInspections.Add);
        internalWindow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        WaitUntilRepositoryReady(internalWindow, TimeSpan.FromSeconds(30), "Conflict fixture did not load.");
        var changes = (DataGrid)internalWindow.FindName("ChangesGrid");
        var conflict = changes.Items.Cast<MoaVcs.Core.FileChange>().Single(c => c.Path == fileName);
        if (!conflict.IsConflicted) throw new InvalidOperationException("The GUI must mark the merge fixture as conflicted.");
        changes.SelectedItem = conflict;
        ((TextBox)internalWindow.FindName("CommitMessage")).Text = "Must wait for resolution";
        if (((Button)internalWindow.FindName("StageButton")).IsEnabled ||
            ((Button)internalWindow.FindName("UnstageButton")).IsEnabled ||
            ((Button)internalWindow.FindName("CommitButton")).IsEnabled)
            throw new InvalidOperationException("Conflicted files must disable stage, unstage, and commit.");
        if (!((TextBlock)internalWindow.FindName("WorkflowHint")).Text.Contains("충돌 해결", StringComparison.Ordinal))
            throw new InvalidOperationException("The workflow guide must direct the user to resolve conflicts first.");

        ((MenuItem)internalWindow.FindName("FileDiffButton")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        WaitForInspection(internalWindow, internalInspections, "File Diff");
        GetConflictWindow(internalInspections, "File Diff").Close();
        internalInspections.Clear();

        ((MenuItem)internalWindow.FindName("ConflictMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        WaitForInspection(internalWindow, internalInspections, "File Tools conflict command");
        var inspectionWindow = GetConflictWindow(internalInspections, "File Tools conflict command");
        var editor = (ConflictResolutionView)inspectionWindow.FindName("ConflictEditor");
        var blocks = (ComboBox)editor.FindName("ConflictBlocks");
        var result = (TextBox)editor.FindName("ConflictResult");
        if (blocks.Items.Count != 4) throw new InvalidOperationException("The merge fixture should expose four conflict blocks.");
        if (!result.SelectedText.Contains("<<<<<<<", StringComparison.Ordinal))
            throw new InvalidOperationException("Selecting a conflict block must move the result editor to that block.");
        Render(inspectionWindow, Path.Combine(output, "conflict.png"));
        RenderAtMinimum(inspectionWindow, Path.Combine(output, "conflict-min.png"));

        var initialDraft = result.Text;
        result.Select(result.Text.IndexOf("main-1", StringComparison.Ordinal), 6);
        result.SelectedText = "edited-one";
        if (result.SelectedText.Contains("<<<<<<<", StringComparison.Ordinal) || result.SelectionLength > 20)
            throw new InvalidOperationException("Typing in a conflict must not select the whole block and replace it on the next keystroke.");
        result.Text = initialDraft;

        blocks.SelectedIndex = 0;
        ((Button)editor.FindName("ConflictUseLeft")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var afterLeft = ConflictMarkers.Parse(result.Text);
        if (afterLeft.Count != 3 || result.Text.Contains("value-1=incoming-1", StringComparison.Ordinal) ||
            !result.Text.Contains("value-1=main-1", StringComparison.Ordinal) || !result.Text.Contains("value-2=incoming-2", StringComparison.Ordinal))
            throw new InvalidOperationException($"Applying the left side must replace only the selected conflict block. Remaining={afterLeft.Count}; main-1={result.Text.Contains("value-1=main-1", StringComparison.Ordinal)}; incoming-1={result.Text.Contains("value-1=incoming-1", StringComparison.Ordinal)}; status={((TextBlock)editor.FindName("ConflictStatus")).Text}");

        blocks.SelectedIndex = 0;
        ((Button)editor.FindName("ConflictUseRight")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (ConflictMarkers.Parse(result.Text).Count != 2 || result.Text.Contains("value-2=main-2", StringComparison.Ordinal) ||
            !result.Text.Contains("value-2=incoming-2", StringComparison.Ordinal) || !result.Text.Contains("value-3=main-3", StringComparison.Ordinal))
            throw new InvalidOperationException("Applying the right side must preserve other conflict blocks.");

        blocks.SelectedIndex = 0;
        ((Button)editor.FindName("ConflictUseBoth")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (ConflictMarkers.Parse(result.Text).Count != 1 || !result.Text.Contains("value-3=main-3", StringComparison.Ordinal) ||
            !result.Text.Contains("value-3=incoming-3", StringComparison.Ordinal) || !result.Text.Contains("value-4=main-4", StringComparison.Ordinal))
            throw new InvalidOperationException("Applying both sides must change only that block and leave the remaining conflict intact.");

        result.Text += "\nmanual edit beside the unresolved block\n";
        if (((Button)editor.FindName("ConflictResolve")).IsEnabled)
            throw new InvalidOperationException("Resolve must stay disabled while conflict markers remain or edits are unsaved.");
        ((Button)editor.FindName("ConflictSave")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitForConflictAction(editor, internalWindow, "Saving the partially resolved conflict did not finish.");
        RenderAtMinimum(inspectionWindow, Path.Combine(output, "conflict-saved-min.png"));
        var savedStages = GitAtOutput(fixture, "ls-files", "--unmerged", "--", fileName);
        if (string.IsNullOrWhiteSpace(savedStages) ||
            !File.ReadAllText(filePath).Contains("manual edit beside the unresolved block", StringComparison.Ordinal) ||
            !((DataGrid)internalWindow.FindName("ChangesGrid")).Items.Cast<MoaVcs.Core.FileChange>().Any(c => c.IsConflicted))
            throw new InvalidOperationException("Saving must retain unresolved index stages, preserve edits, and refresh the main conflict state.");

        var lastBlock = ConflictMarkers.Parse(result.Text).Single();
        result.Text = result.Text[..lastBlock.Start] + "manually resolved fourth block\n" +
            result.Text[(lastBlock.Start + lastBlock.Length)..];
        if (((Button)editor.FindName("ConflictResolve")).IsEnabled)
            throw new InvalidOperationException("Resolve must wait until direct edits are saved.");
        ((Button)editor.FindName("ConflictSave")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitForConflictAction(editor, internalWindow, "Saving the finished conflict did not finish.");
        if (ConflictMarkers.ContainsMarkers(File.ReadAllText(filePath)) ||
            string.IsNullOrWhiteSpace(GitAtOutput(fixture, "ls-files", "--unmerged", "--", fileName)) ||
            !((Button)editor.FindName("ConflictResolve")).IsEnabled)
            throw new InvalidOperationException("A marker-free saved result should enable a separate resolve action without staging automatically.");

        ((Button)editor.FindName("ConflictResolve")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitForConflictAction(editor, internalWindow, "Marking the conflict resolved did not finish.");
        if (!string.IsNullOrWhiteSpace(GitAtOutput(fixture, "ls-files", "--unmerged", "--", fileName)) ||
            ((DataGrid)internalWindow.FindName("ChangesGrid")).Items.Cast<MoaVcs.Core.FileChange>().Any(c => c.IsConflicted) ||
            !((Button)internalWindow.FindName("CommitButton")).IsEnabled || !result.IsReadOnly)
            throw new InvalidOperationException("Resolve must clear Git stages, refresh the main view, retain the result, and leave commit separate.");
        inspectionWindow.Close();
        Console.WriteLine("PASS: Git conflict editor via File Tools and Explorer diff; block choices, direct edit, separate save/resolve and layout");
    }

    private static TabControl FindSettingsTabs(SettingsWindow window) =>
        Descendants((DependencyObject)window.Content).OfType<TabControl>().Single(tabs => tabs.Name == "SettingsTabs");

    private static void SelectSettingsTab(SettingsWindow window, TabControl tabs, int index)
    {
        tabs.SelectedIndex = index;
        window.UpdateLayout();
        ((FrameworkElement)window.Content).UpdateLayout();
        Pump();
        window.UpdateLayout();
    }

    private static void WaitUntilRepositoryReady(MainWindow window, TimeSpan timeout, string error)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (((Grid)window.FindName("RepositoryPanel")).Visibility != Visibility.Visible && DateTime.UtcNow < deadline) Pump();
        WaitUntilIdle(window, timeout, error);
        if (((Grid)window.FindName("RepositoryPanel")).Visibility != Visibility.Visible)
            throw new InvalidOperationException(error + " " + ((TextBlock)window.FindName("NoticeText")).Text);
    }

    private static void WaitUntilIdle(MainWindow window, TimeSpan timeout, string error)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (((Button)window.FindName("CancelButton")).Visibility == Visibility.Visible && DateTime.UtcNow < deadline) Pump();
        if (((Button)window.FindName("CancelButton")).Visibility == Visibility.Visible)
            throw new TimeoutException(error);
        if (!string.IsNullOrWhiteSpace(((TextBlock)window.FindName("NoticeText")).Text))
            throw new InvalidOperationException(error + " " + ((TextBlock)window.FindName("NoticeText")).Text);
    }

    private static void RunFirstPushSmoke(string scratch)
    {
        var root = Path.Combine(scratch, "ui-first-push-" + Guid.NewGuid().ToString("N"));
        var work = Path.Combine(root, "work");
        var bare = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(bare);
        var settingsPath = Path.Combine(root, "settings.json");
        var globalConfigPath = Path.Combine(root, "empty-global.config");
        var previousGlobalConfig = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var previousNoSystemConfig = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
        try
        {
            File.WriteAllText(globalConfigPath, string.Empty);
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", globalConfigPath);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
            GitAt(bare, "init", "--bare");
            GitAt(work, "init", "--initial-branch=master");
            GitAt(work, "config", "user.name", "MoaVCS UI test");
            GitAt(work, "config", "user.email", "test@example.invalid");
            GitAt(work, "config", "push.default", "simple");
            File.WriteAllText(Path.Combine(work, "first-push.txt"), "first push smoke\n");
            GitAt(work, "add", "first-push.txt");
            GitAt(work, "commit", "-m", "First push smoke");
            GitAt(work, "remote", "add", "origin", bare);
            File.WriteAllText(settingsPath, "{ invalid json");

            var confirmations = new List<(string Label, string Detail)>();
            MainWindow? windowHolder = null;
            var window = new MainWindow(new LaunchOptions(work, "push"), new SettingsStore(settingsPath),
                confirmOperation: (label, detail) =>
                {
                    if (((FrameworkElement)windowHolder!.FindName("NoticePanel")).Visibility != Visibility.Visible)
                        throw new InvalidOperationException("The smoke must begin with an earlier settings error notice.");
                    confirmations.Add((label, detail));
                    return true;
                });
            windowHolder = window;
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                Pump();
                if (confirmations.Count == 1 &&
                    ((Button)window.FindName("CancelButton")).Visibility != Visibility.Visible &&
                    GitConfigAtOrNull(work, "branch.master.remote") == "origin") break;
            }

            var upstreamRemote = GitConfigAtOrNull(work, "branch.master.remote");
            var upstreamBranch = GitConfigAtOrNull(work, "branch.master.merge");
            if (confirmations.Count != 1 || !confirmations[0].Detail.Contains("upstream", StringComparison.Ordinal) ||
                upstreamRemote != "origin" || upstreamBranch != "refs/heads/master" ||
                ((FrameworkElement)window.FindName("NoticePanel")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("First push must confirm upstream setup, publish the branch, and clear the previous notice. " +
                    $"Confirmations={confirmations.Count}; remote={upstreamRemote ?? "<missing>"}; merge={upstreamBranch ?? "<missing>"}; " +
                    $"notice={((TextBlock)window.FindName("NoticeText")).Text}; " +
                    $"log={((TextBox)window.FindName("OutputLog")).Text}");
            var localHead = GitAtOutput(work, "rev-parse", "HEAD").Trim();
            var remoteHead = GitAtOutput(work, "ls-remote", "--heads", "origin", "refs/heads/master").Split('\t')[0].Trim();
            if (localHead != remoteHead)
                throw new InvalidOperationException("First push did not publish the local commit to the disposable bare remote.");
            Console.WriteLine("PASS: first Git push creates upstream on a local bare remote and clears the previous notice");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previousGlobalConfig);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", previousNoSystemConfig);
            DeleteSmokePath(root);
        }
    }

    private static string RunLore(string executable, string workingDirectory, params string[] arguments) =>
        new MoaVcs.Core.ProcessCommandRunner().RunAsync(new MoaVcs.Core.CommandSpec(
            executable, arguments, workingDirectory, Timeout: TimeSpan.FromSeconds(45))).GetAwaiter().GetResult().EnsureSuccess().StandardOutput;

    private static bool IsInsideLocal(string path)
    {
        var allowed = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local")) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(allowed, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteSmokePath(string? path)
    {
        if (path is null || !IsInsideLocal(path) || !Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(path, recursive: true);
    }

    private static void Git(params string[] arguments) => new MoaVcs.Core.ProcessCommandRunner()
        .RunAsync(new MoaVcs.Core.CommandSpec("git", arguments, _fixture!)).GetAwaiter().GetResult().EnsureSuccess();

    private static string GitOutput(params string[] arguments) => new MoaVcs.Core.ProcessCommandRunner()
        .RunAsync(new MoaVcs.Core.CommandSpec("git", arguments, _fixture!)).GetAwaiter().GetResult().EnsureSuccess().StandardOutput;

    private static string GitBranchConfig() => GitBranchConfigAt(_fixture!);

    private static string GitBranchConfigAt(string workingDirectory)
    {
        var result = new MoaVcs.Core.ProcessCommandRunner().RunAsync(new MoaVcs.Core.CommandSpec(
            "git", ["config", "--local", "--null", "--get-regexp", "^branch\\."], workingDirectory))
            .GetAwaiter().GetResult();
        if (result.ExitCode is not (0 or 1))
            throw new InvalidOperationException("Could not read local branch settings: " + result.StandardError);
        return result.StandardOutput;
    }

    private static string? GitConfigAtOrNull(string workingDirectory, string key)
    {
        var result = new MoaVcs.Core.ProcessCommandRunner().RunAsync(new MoaVcs.Core.CommandSpec(
            "git", ["config", "--local", "--get", key], workingDirectory)).GetAwaiter().GetResult();
        if (result.ExitCode == 1) return null;
        return result.EnsureSuccess().StandardOutput.Trim();
    }

    private static void GitAt(string workingDirectory, params string[] arguments) => GitAtOutput(workingDirectory, arguments);

    private static string GitAtOutput(string workingDirectory, params string[] arguments) => new MoaVcs.Core.ProcessCommandRunner()
        .RunAsync(new MoaVcs.Core.CommandSpec("git", arguments, workingDirectory)).GetAwaiter().GetResult().EnsureSuccess().StandardOutput;

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Render(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var size = new Size(window.Width, window.Height - 32);
        content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
        Pump();
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen()) context.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(size));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }

    private static void RenderAtMinimum(Window window, string path)
    {
        var oldWidth = window.Width;
        var oldHeight = window.Height;
        window.Width = Math.Max(window.MinWidth, 720);
        window.Height = Math.Max(window.MinHeight, 520);
        Render(window, path);
        window.Width = oldWidth;
        window.Height = oldHeight;
        window.UpdateLayout();
    }
}
