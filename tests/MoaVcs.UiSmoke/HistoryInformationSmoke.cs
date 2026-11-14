using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MoaVcs.App;
using MoaVcs.App.Services;
using MoaVcs.Core;

namespace MoaVcs.UiSmoke;

internal static partial class Program
{
    private static void RunHistoryInformationSmoke(string scratch, string output)
    {
        RunHistoryGraphSmoke();
        var root = Path.Combine(scratch, "ui-history-info-" + Guid.NewGuid().ToString("N"));
        var bare = Path.Combine(root, "origin.git");
        var work = Path.Combine(root, "work");
        var peer = Path.Combine(root, "peer");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(work);
        MainWindow? window = null;
        try
        {
            GitAt(bare, "init", "--bare");
            GitAt(work, "init", "--initial-branch=main");
            Configure(work);
            CommitFile(work, "base.txt", "Shared baseline");
            var sharedId = GitAtOutput(work, "rev-parse", "HEAD").Trim();
            GitAt(work, "remote", "add", "origin", bare);
            GitAt(work, "push", "-u", "origin", "main");
            GitAt(work, "switch", "-c", "feature/history");
            CommitFile(work, "feature.txt", "History feature");
            GitAt(work, "switch", "main");
            CommitFile(work, "main.txt", "Local work");
            GitAt(work, "merge", "--no-ff", "feature/history", "-m", "Merge history feature");
            var localId = GitAtOutput(work, "rev-parse", "HEAD").Trim();
            GitAt(work, "tag", "-a", "v0.13-demo", "-m", "History information demo");
            GitAt(work, "switch", "-c", "other/branch", sharedId);
            CommitFile(work, "other.txt", "Another branch commit");
            var otherId = GitAtOutput(work, "rev-parse", "HEAD").Trim();
            GitAt(work, "switch", "main");
            GitAt(root, "clone", "--branch", "main", bare, peer);
            Configure(peer);
            CommitFile(peer, "remote.txt", "Incoming remote work");
            var remoteId = GitAtOutput(peer, "rev-parse", "HEAD").Trim();
            GitAt(peer, "push");
            File.WriteAllText(Path.Combine(work, "uncommitted.txt"), "Keep working files unchanged.\n");
            var beforeStatus = GitAtOutput(work, "status", "--porcelain=v1", "-z");

            var settings = new SettingsStore(Path.Combine(root, "settings.json"));
            settings.Save(new AppSettings());
            window = new MainWindow(new LaunchOptions(work), settings);
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            WaitUntilRepositoryReady(window, TimeSpan.FromSeconds(25), "History-info fixture did not load.");
            ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("HistoryTab");
            WaitUntilIdle(window, TimeSpan.FromSeconds(25), "History-info query did not finish.");
            var history = (DataGrid)window.FindName("HistoryGrid");
            AssertText("HistoryCurrentBranch", "main");
            AssertText("HistoryUpstream", "origin/main");
            if (Rows().Any(entry => entry.Id == remoteId) || Rows().Any(entry => entry.Id == otherId))
                throw new InvalidOperationException("Opening history must use cached tracking refs and the selected history scope without fetching.");
            if (Rows().Single(entry => entry.Id == localId).Relation != HistoryRelation.LocalOnly ||
                Rows().Single(entry => entry.Id == sharedId).Relation != HistoryRelation.Shared)
                throw new InvalidOperationException("Commit relations must distinguish local-only work from the shared baseline.");
            AssertText("HistorySyncStatus", "3");
            var fetch = (MenuItem)window.FindName("FetchMenuButton");
            if (!fetch.IsEnabled) throw new InvalidOperationException("Git Fetch must be available from the repository menu.");
            fetch.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            WaitUntilIdle(window, TimeSpan.FromSeconds(30), "History Fetch did not finish.");
            if (Rows().SingleOrDefault(entry => entry.Id == remoteId)?.Relation != HistoryRelation.UpstreamOnly)
                throw new InvalidOperationException("Explicit Fetch must expose incoming remote commits and their relation in history.");
            AssertText("HistorySyncStatus", "1");
            if (GitAtOutput(work, "rev-parse", "HEAD").Trim() != localId ||
                GitAtOutput(work, "status", "--porcelain=v1", "-z") != beforeStatus)
                throw new InvalidOperationException("Fetch/history browsing changed HEAD or working files.");
            var entry = Rows().Single(item => item.Id == localId);
            if (entry.ParentIds.Count != 2 || !entry.References.Any(reference => reference.Kind == HistoryReferenceKind.Head) ||
                !entry.References.Any(reference => reference.Name == "v0.13-demo" && reference.Kind == HistoryReferenceKind.Tag))
                throw new InvalidOperationException("History must expose merge parents, HEAD, and annotated tags.");
            history.SelectedItem = entry;
            WaitForText("HistoryDetailParents", entry.ParentIds[0][..8]);
            AssertText("HistoryDetailRefs", "main");
            WaitForText("HistoryDetailMetadata", "history-info@example.invalid");
            var detailDeadline = DateTime.UtcNow.AddSeconds(25);
            while (!((TextBlock)window.FindName("HistoryDetailStatus")).Text.StartsWith("변경 파일 ", StringComparison.Ordinal) && DateTime.UtcNow < detailDeadline) Pump();
            if (((DataGrid)window.FindName("HistoryFilesGrid")).Items.Count == 0)
                throw new InvalidOperationException("Selecting the merge commit must finish loading changed files before rendering.");
            var diffViewer = (RichTextBox)window.FindName("HistoryDiffViewer");
            while (!new TextRange(diffViewer.Document.ContentStart, diffViewer.Document.ContentEnd).Text.Contains("+History feature", StringComparison.Ordinal) && DateTime.UtcNow < detailDeadline) Pump();
            if (!new TextRange(diffViewer.Document.ContentStart, diffViewer.Document.ContentEnd).Text.Contains("+History feature", StringComparison.Ordinal))
                throw new InvalidOperationException("The merge commit must display its first-parent file diff.");
            Render(window, Path.Combine(output, "history-information.png"));
            RenderAtMinimum(window, Path.Combine(output, "history-information-min.png"));

            var scope = (ComboBox)window.FindName("HistoryScopeSelector");
            scope.SelectedIndex = 1;
            WaitUntilIdle(window, TimeSpan.FromSeconds(25), "All-branch history did not finish.");
            if (!Rows().Any(item => item.Id == otherId))
                throw new InvalidOperationException("All-branch scope must include commits outside HEAD/upstream.");
            scope.SelectedIndex = 0;
            WaitUntilIdle(window, TimeSpan.FromSeconds(25), "Current-branch history did not finish.");
            if (Rows().Any(item => item.Id == otherId))
                throw new InvalidOperationException("Returning to current scope must remove unrelated branch commits.");

            GitAt(work, "branch", "--unset-upstream");
            ((Button)window.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntilIdle(window, TimeSpan.FromSeconds(25), "Untracked history refresh did not finish.");
            if (Rows().Any(item => item.Relation != HistoryRelation.Unknown))
                throw new InvalidOperationException("No-upstream commits must not be described as unpushed/shared.");
            if (!((TextBlock)window.FindName("HistoryUpstream")).Text.Contains("없", StringComparison.Ordinal) &&
                !((TextBlock)window.FindName("HistoryUpstream")).Text.Contains("미설정", StringComparison.Ordinal))
                throw new InvalidOperationException("An unset upstream needs an explicit explanation.");
            Console.WriteLine("PASS: Git history branch/upstream/ref metadata, merge graph, local/incoming relations, scope and explicit non-mutating Fetch");

            IEnumerable<HistoryEntry> Rows() => history.Items.Cast<HistoryEntry>();
            void AssertText(string name, string expected)
            {
                var actual = ((TextBlock)window.FindName(name)).Text;
                if (!actual.Contains(expected, StringComparison.Ordinal))
                    throw new InvalidOperationException($"{name} must contain {expected}; actual={actual}");
            }
            void WaitForText(string name, string expected)
            {
                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (!((TextBlock)window.FindName(name)).Text.Contains(expected, StringComparison.Ordinal) && DateTime.UtcNow < deadline) Pump();
                AssertText(name, expected);
            }
        }
        finally { window?.Close(); DeleteSmokePath(root); }

        static void Configure(string directory)
        {
            GitAt(directory, "config", "user.name", "MoaVCS history information test");
            GitAt(directory, "config", "user.email", "history-info@example.invalid");
        }
        static void CommitFile(string directory, string name, string message)
        {
            File.WriteAllText(Path.Combine(directory, name), message + "\n");
            GitAt(directory, "add", "--", name);
            GitAt(directory, "commit", "-m", message);
        }
    }
}
