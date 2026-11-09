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
            var window = new MainWindow(options);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "MoaVCS · 시작할 수 없습니다", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
