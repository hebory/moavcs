using System.Windows;
using MoaVcs.App.Services;

namespace MoaVcs.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var options = LaunchOptions.Parse(e.Args);
            if (options.Headless)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var executable = global::MoaVcs.App.MainWindow.GetExecutablePath();
                if (options.InstallContextMenu) ExplorerIntegration.Install(executable);
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
        }
        catch (Exception ex)
        {
            if (e.Args.Contains("--headless", StringComparer.OrdinalIgnoreCase))
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
