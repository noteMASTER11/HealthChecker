namespace HealthChecker;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        var startInTray = e.Args.Any(static arg =>
            arg.Equals("--tray", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        var window = new MainWindow(startInTray);
        MainWindow = window;
        window.Show();
    }
}
