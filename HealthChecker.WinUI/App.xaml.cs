using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using HealthChecker_WinUI.ViewModels;

namespace HealthChecker_WinUI;

public partial class App : Application
{
    private Window? _window;

    public MonitoringCoordinator? Monitoring { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        Monitoring = new MonitoringCoordinator(dispatcherQueue);

        await Monitoring.InitializeAsync();

        var launchArgs = args.Arguments ?? string.Empty;
        var launchInTray = launchArgs.Contains("--tray", StringComparison.OrdinalIgnoreCase) || Monitoring.StartMinimizedToTray;

        _window = new MainWindow(launchInTray);
        _window.Closed += OnMainWindowClosed;

        _window.Activate();
    }

    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        if (Monitoring is not null)
        {
            await Monitoring.DisposeAsync();
        }
    }
}
