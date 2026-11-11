using System.Windows;
using MoaVcs.App.Services;

namespace MoaVcs.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var options = LaunchOptions.Parse(e.Args);
            if (options.OverlayOperation is { } overlay)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var executable = global::MoaVcs.App.MainWindow.GetExecutablePath();
                var store = new SettingsStore();
                switch (overlay)
                {
                    case "--overlay-worker": await ExplorerOverlayWorker.RunAsync(executable, store); break;
                    case "--pause-overlays": await ExplorerOverlays.PauseAsync(executable); break;
                    case "--resume-overlays": ExplorerOverlays.Resume(executable, store); break;
                    case "--disable-overlays":
                        if (ExplorerOverlays.IsStartupOwner(executable))
                        {
                            await ExplorerOverlays.DisableAsync(executable, store);
                        }
                        break;
                }
                Shutdown(0);
                return;
            }
            if (options.Headless)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var executable = global::MoaVcs.App.MainWindow.GetExecutablePath();
                if (options.InstallContextMenu) ExplorerIntegration.Install(executable, new SettingsStore().Load().ExplorerMenu);
                else ExplorerIntegration.Uninstall(executable);
                Shutdown(0);
                return;
            }
            // Keep snapshot copies long enough for external tools that launch a child process.
            try
            {
                SnapshotStore.CleanupExpired();
                foreach (var warning in SnapshotStore.LastCleanupWarnings) System.Diagnostics.Trace.TraceWarning(warning);
            }
            catch (Exception cleanupError) when (cleanupError is System.IO.IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning(cleanupError.Message);
            }
            var window = new MainWindow(options);
            MainWindow = window;
            window.Show();
            // WPF smoke hosts this Application in its own process; only the installed app owns startup entries.
            var appPath = Environment.ProcessPath;
            if (appPath is not null && System.IO.Path.GetFileName(appPath).Equals("moavcs.exe", StringComparison.OrdinalIgnoreCase) &&
                ExplorerOverlays.IsStartupOwner(appPath)) ExplorerOverlays.StartWorker(appPath);
        }
        catch (Exception ex)
        {
            if (e.Args.Contains("--headless", StringComparer.OrdinalIgnoreCase) || e.Args.Contains("--overlay-worker", StringComparer.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(ex.Message);
                try
                {
                    var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoaVcs");
                    System.IO.Directory.CreateDirectory(folder);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "setup-error.log"), $"{DateTimeOffset.Now:O}\n{ex}");
                }
                catch (Exception logError) when (logError is System.IO.IOException or UnauthorizedAccessException) { }
            }
            else MessageBox.Show(ex.Message, "MoaVCS · 시작할 수 없습니다", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
