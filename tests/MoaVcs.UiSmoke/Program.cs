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
            Git("add", "README.md"); Git("commit", "-m", "Create workspace");
            File.AppendAllText(Path.Combine(_fixture, "README.md"), "\nGit, SVN, Perforce and Lore.\n");
            File.WriteAllText(Path.Combine(_fixture, "시작하기.txt"), "변경 파일을 선택하여 비교하세요.\n");
            var settings = new SettingsStore(Path.Combine(Environment.CurrentDirectory, ".local", "ui-smoke-settings.json"));
            settings.Save(new AppSettings());
            var window = new MainWindow(new LaunchOptions(null), settings);
            Render(window, Path.Combine(output, "welcome.png"));
            if (((TextBlock)window.FindName("RepoTitle")).Text != "작업 공간") throw new InvalidOperationException("Welcome title failed.");
            if (((Grid)window.FindName("RepositoryPanel")).Visibility != Visibility.Collapsed) throw new InvalidOperationException("Empty state failed.");

            var repoWindow = new MainWindow(new LaunchOptions(_fixture), settings);
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
            ((TabControl)repoWindow.FindName("MainTabs")).SelectedItem = repoWindow.FindName("HistoryTab");
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (((Button)repoWindow.FindName("CancelButton")).Visibility == Visibility.Visible && DateTime.UtcNow < deadline) Pump();
            if (((Button)repoWindow.FindName("CancelButton")).Visibility == Visibility.Visible) throw new TimeoutException("History load did not finish.");
            if (((DataGrid)repoWindow.FindName("HistoryGrid")).Items.Count != 1) throw new InvalidOperationException("History view did not load the fixture commit.");
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
            var createWindow = new CreateRepositoryWindow(kind => kind switch
            {
                MoaVcs.Core.VcsKind.Git => new MoaVcs.Core.Providers.GitProvider(new MoaVcs.Core.ProcessCommandRunner()),
                MoaVcs.Core.VcsKind.Svn => new MoaVcs.Core.Providers.SvnProvider(),
                MoaVcs.Core.VcsKind.Perforce => new MoaVcs.Core.Providers.PerforceProvider(),
                _ => new MoaVcs.Core.Providers.LoreProvider()
            });
            Render(createWindow, Path.Combine(output, "create.png"));
            Console.WriteLine($"PASS: WPF welcome, repository, history, settings, downloads, create and layout rendering. {output}");
            app.Shutdown();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local")) + Path.DirectorySeparatorChar;
            if (_fixture is not null && Path.GetFullPath(_fixture).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_fixture))
            {
                foreach (var file in Directory.EnumerateFiles(_fixture, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                Directory.Delete(_fixture, recursive: true);
            }
        }
    }

    private static void Git(params string[] arguments) => new MoaVcs.Core.ProcessCommandRunner()
        .RunAsync(new MoaVcs.Core.CommandSpec("git", arguments, _fixture!)).GetAwaiter().GetResult().EnsureSuccess();

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
