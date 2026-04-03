using System.ComponentModel;
using System.Windows;
using HealthChecker.ViewModels;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace HealthChecker;

public partial class MainWindow : Window
{
    private readonly bool _startInTray;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private bool _allowClose;

    public MainWindow(bool startInTray)
    {
        _startInTray = startInTray;

        InitializeComponent();

        DataContext = new MainViewModel();

        _notifyIcon = BuildNotifyIcon();

        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();

        if (_startInTray)
        {
            SendToTray();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            SendToTray();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        SendToTray();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        await ViewModel.ShutdownAsync();
    }

    private WinForms.NotifyIcon BuildNotifyIcon()
    {
        var notifyIcon = new WinForms.NotifyIcon
        {
            Visible = false,
            Text = "HealthChecker",
            Icon = Drawing.SystemIcons.Shield,
            ContextMenuStrip = new WinForms.ContextMenuStrip()
        };

        notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);

        notifyIcon.ContextMenuStrip.Items.Add("Open", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        notifyIcon.ContextMenuStrip.Items.Add("Pause/Resume", null, (_, _) => Dispatcher.Invoke(() => ViewModel.ToggleMonitoringCommand.Execute(null)));
        notifyIcon.ContextMenuStrip.Items.Add("Probe Now", null, (_, _) => Dispatcher.Invoke(() => ViewModel.ProbeNowCommand.Execute(null)));
        notifyIcon.ContextMenuStrip.Items.Add(new WinForms.ToolStripSeparator());
        notifyIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        return notifyIcon;
    }

    private void SendToTray()
    {
        ShowInTaskbar = false;
        Hide();
        _notifyIcon.Visible = true;
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        _notifyIcon.Visible = true;
    }

    private void ExitApplication()
    {
        _allowClose = true;
        _notifyIcon.Visible = false;
        ShowInTaskbar = true;
        Show();
        Close();
    }

    private void TrayButton_OnClick(object sender, RoutedEventArgs e)
    {
        SendToTray();
    }
}
