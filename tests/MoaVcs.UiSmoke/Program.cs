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
            var grid = (DataGrid)repoWindow.FindName("ChangesGrid");
            if (grid.Items.Count != 2) throw new InvalidOperationException("Expected two fixture changes.");
            grid.SelectedItem = grid.Items.Cast<MoaVcs.Core.FileChange>().Single(c => c.Path == "README.md");
            ((Button)repoWindow.FindName("FileDiffButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (inspections.Count < 1 && DateTime.UtcNow < deadline && ((Button)repoWindow.FindName("CancelButton")).Visibility == Visibility.Visible) Pump();
            if (inspections.Count != 1) throw new InvalidOperationException("Diff command did not open viewer: " + ((TextBlock)repoWindow.FindName("NoticeText")).Text);
            var comparisonGrid = (DataGrid)inspections[0].FindName("ComparisonRows");
            if (comparisonGrid.Items.Count != 5 || !comparisonGrid.Items.Cast<MoaVcs.Core.ComparisonRow>().Any(r => r.Kind == "added"))
                throw new InvalidOperationException("Diff view must show committed and added lines.");
            Render(inspections[0], Path.Combine(output, "diff.png"));
            ((Button)repoWindow.FindName("FileBlameButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
            // Settings dialog initialization and layout must not install registry keys.
            var opened = new List<Uri>();
            var settingsWindow = new SettingsWindow(new AppSettings(), settings, new MoaVcs.Core.ProcessCommandRunner(), opened.Add);
            Render(settingsWindow, Path.Combine(output, "settings.png"));
            foreach (var kind in Enum.GetValues<MoaVcs.Core.VcsKind>())
            {
                var download = Descendants((DependencyObject)settingsWindow.Content).OfType<Button>().Single(button => button.Name == "Download" + kind);
                download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (opened[^1] != VcsTools.Get(kind).DownloadUrl) throw new InvalidOperationException("Wrong download URL for " + kind);
            }
            if (opened.Count != 4) throw new InvalidOperationException("All VCS download buttons must work.");
            var toolOptions = Descendants((DependencyObject)settingsWindow.Content).OfType<CheckBox>().Where(c => Equals(c.Content, "외부 도구 사용")).ToList();
            if (toolOptions.Count != 2 || toolOptions.Any(c => c.IsChecked == true)) throw new InvalidOperationException("Diff and blame must default to internal tools.");
            ((ScrollViewer)settingsWindow.Content).ScrollToVerticalOffset(520);
            Pump();
            Render(settingsWindow, Path.Combine(output, "settings-file-tools.png"));
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

        ((Button)window.FindName("FileDiffButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        deadline = DateTime.UtcNow.AddSeconds(45);
        while (inspections.Count < 1 && DateTime.UtcNow < deadline) Pump();
        if (inspections.Count < 1) throw new InvalidOperationException("Lore compare did not open an inspection window: " + ((TextBlock)window.FindName("NoticeText")).Text);
        var comparison = (DataGrid)inspections[0].FindName("ComparisonRows");
        var comparisonRows = comparison.Items.Cast<MoaVcs.Core.ComparisonRow>().ToArray();
        if (!comparisonRows.Any(row => row.Kind == "modified" && row.LeftText == "Lore baseline line" && row.RightText == "Lore changed line") ||
            !comparisonRows.Any(row => row.Kind == "added" && row.RightText == "Lore added line"))
            throw new InvalidOperationException("Lore compare did not show the committed baseline against the modified working file.");
        Render(inspections[0], Path.Combine(output, "lore-diff.png"));

        ((Button)window.FindName("FileBlameButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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

    private static void RunCancelledOperationSmoke(SettingsStore settings, string fixture)
    {
        var head = GitOutput("rev-parse", "HEAD").Trim();
        var status = GitOutput("status", "--porcelain=v1", "-z");
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
            if (((TextBox)window.FindName("OutputLog")).Text.Contains(expectedLabel, StringComparison.Ordinal))
                throw new InvalidOperationException($"Canceled Explorer {action} must not start the provider operation.");
            if (GitOutput("rev-parse", "HEAD").Trim() != head || GitOutput("status", "--porcelain=v1", "-z") != status)
                throw new InvalidOperationException($"Canceled Explorer {action} changed the Git fixture.");
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
}
