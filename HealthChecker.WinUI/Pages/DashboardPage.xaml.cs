using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HealthChecker_WinUI.ViewModels;

namespace HealthChecker_WinUI.Pages;

public sealed partial class DashboardPage : Page
{
    private MonitoringCoordinator? Coordinator => (Application.Current as App)?.Monitoring;

    public DashboardPage()
    {
        InitializeComponent();
        DataContext = Coordinator;
    }

    private void ToggleMonitoring_Click(object sender, RoutedEventArgs e)
    {
        Coordinator?.ToggleMonitoring();
    }

    private async void ProbeNow_Click(object sender, RoutedEventArgs e)
    {
        if (Coordinator is not null)
        {
            await Coordinator.ProbeNowAsync();
        }
    }
}
