using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MoaVcs.App;
using MoaVcs.App.Services;

namespace MoaVcs.UiSmoke;

internal static class Program
{
    private static string? _fixture;
    private static string? _commitFixture;
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
            RunStartupActionSmoke(settings, _fixture, scratch);
            RunCancelledOperationSmoke(settings, _fixture);
            RunCommitStagingSmoke(settings, scratch);
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
            var menuOptions = Descendants((DependencyObject)settingsWindow.Content).OfType<CheckBox>()
                .Where(c => c.Name is "ExplorerCommitTopLevel" or "ExplorerUpdateTopLevel").ToList();
            if (menuOptions.Count != 2 || menuOptions.Any(c => c.IsChecked != true))
                throw new InvalidOperationException("Explorer shortcuts must default to the first menu level.");
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
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            DeleteSmokePath(_fixture);
            DeleteSmokePath(_commitFixture);
            DeleteSmokePath(_loreFixture);
            if (_loreSettingsFile is not null && IsInsideLocal(_loreSettingsFile) && File.Exists(_loreSettingsFile))
                File.Delete(_loreSettingsFile);
        }
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
            clone.DestinationPath.Length != 0 || clone.Request is not null ||
            ((Grid)cloneWindow.FindName("RepositoryPanel")).Visibility != Visibility.Collapsed)
            throw new InvalidOperationException($"Explorer clone should open a blank-destination dialog without opening or modifying the clicked repository. Dialog={shown?.GetType().Name ?? "<none>"}; notice={cloneNotice}");

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
