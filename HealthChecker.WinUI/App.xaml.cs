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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        Monitoring = new MonitoringCoordinator(dispatcherQueue);

        _window = new MainWindow();
        _window.Closed += OnMainWindowClosed;

        _ = Monitoring.InitializeAsync();

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
