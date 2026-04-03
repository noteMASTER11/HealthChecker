using System;
using System.Drawing;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HealthChecker_WinUI.Pages;

namespace HealthChecker_WinUI;

public sealed partial class MainWindow : Window
{
    private readonly TaskbarIcon _trayIcon = new();
    private readonly bool _startHiddenInTray;
    private bool _startupHideApplied;
    private bool _isExitRequested;

    public MainWindow(bool startHiddenInTray)
    {
        InitializeComponent();
        _startHiddenInTray = startHiddenInTray;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Closing += OnAppWindowClosing;
        Activated += OnWindowActivated;

        NavFrame.Navigate(typeof(DashboardPage));

        var trayMenu = new MenuFlyout();
        var openItem = new MenuFlyoutItem { Text = "Open" };
        openItem.Click += TrayOpen_Click;
        var exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Click += TrayExit_Click;

        trayMenu.Items.Add(openItem);
        trayMenu.Items.Add(new MenuFlyoutSeparator());
        trayMenu.Items.Add(exitItem);

        _trayIcon.ContextFlyout = trayMenu;
        _trayIcon.ToolTipText = "HealthChecker";
        _trayIcon.Icon = SystemIcons.Shield;
        _trayIcon.ForceCreate();
        Closed += OnWindowClosed;
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack)
        {
            NavFrame.GoBack();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        switch (item.Tag)
        {
            case "dashboard":
                NavFrame.Navigate(typeof(DashboardPage));
                break;
            case "monitoring":
                NavFrame.Navigate(typeof(MonitoringPage));
                break;
            default:
                throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isExitRequested)
        {
            return;
        }

        args.Cancel = true;
        HideToTray();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_startHiddenInTray || _startupHideApplied)
        {
            return;
        }

        _startupHideApplied = true;
        HideToTray();
    }

    private void TrayOpen_Click(object sender, RoutedEventArgs e)
    {
        AppWindow.Show();
        Activate();
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _isExitRequested = true;
        _trayIcon.Dispose();
        Close();
    }

    private void HideToTray()
    {
        AppWindow.Hide();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _trayIcon.Dispose();
    }
}
