using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HealthChecker_WinUI.ViewModels;

namespace HealthChecker_WinUI.Pages;

public sealed partial class MonitoringPage : Page
{
    private MonitoringCoordinator? Coordinator => (Application.Current as App)?.Monitoring;

    public MonitoringPage()
    {
        InitializeComponent();
        DataContext = Coordinator;
    }

    private async void AddTarget_Click(object sender, RoutedEventArgs e)
    {
        if (Coordinator is not null)
        {
            await Coordinator.AddTargetFromInputsAsync();
        }
    }

    private async void RemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MonitoringTargetViewModel target } && Coordinator is not null)
        {
            await Coordinator.RemoveTargetAsync(target);
        }
    }

    private async void ToggleExpand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MonitoringTargetViewModel target } && Coordinator is not null)
        {
            await Coordinator.ToggleExpandedAsync(target);
        }
    }

    private async void OpenTrace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MonitoringTargetViewModel target } && Coordinator is not null)
        {
            await Coordinator.OpenTraceForTargetAsync(target);
        }
    }

    private async void BackFromTrace_Click(object sender, RoutedEventArgs e)
    {
        if (Coordinator is not null)
        {
            await Coordinator.BackFromTraceAsync();
        }
    }

    private async void StopTrace_Click(object sender, RoutedEventArgs e)
    {
        if (Coordinator is not null)
        {
            await Coordinator.StopTraceAsync();
        }
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
