// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using HealthChecker_WinUI.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace HealthChecker_WinUI.Pages;

public sealed partial class SettingsPage : Page
{
    private MonitoringCoordinator? Coordinator => (Application.Current as App)?.Monitoring;

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = Coordinator;
    }

    private async void OpenStorageFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Coordinator is not null)
        {
            await Coordinator.OpenStorageFolderAsync();
        }
    }

    private async void CompactDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (Coordinator is not null)
        {
            await Coordinator.CompactDatabaseAsync();
        }
    }

    private async void ResetStatistics_Click(object sender, RoutedEventArgs e)
    {
        if (Coordinator is not null)
        {
            await Coordinator.ResetStatisticsAsync();
        }
    }
}
