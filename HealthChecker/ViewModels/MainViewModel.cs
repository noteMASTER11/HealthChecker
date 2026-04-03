using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using HealthChecker.Models;
using HealthChecker.Services;

namespace HealthChecker.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppStateStore _stateStore = new();
    private readonly NetworkProbeService _probeService = new();
    private readonly StartupRegistrationService _startupService = new();
    private readonly DispatcherTimer _monitorTimer;
    private readonly SemaphoreSlim _cycleSemaphore = new(1, 1);
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);
    private readonly CancellationTokenSource _shutdownTokenSource = new();

    private bool _isInitialized;
    private bool _isMonitoring = true;
    private bool _startWithWindows;
    private bool _isTraceView;
    private string _newAddress = string.Empty;
    private string _newName = string.Empty;
    private string _newDescription = string.Empty;
    private string _statusMessage = "Ready.";
    private string _traceCaption = "Traceroute";
    private int _saveTickCounter;
    private TraceSessionViewModel? _traceSession;

    public MainViewModel()
    {
        Targets = [];

        AddTargetCommand = new RelayCommand(AddTarget);
        RemoveTargetCommand = new RelayCommand<TargetViewModel>(RemoveTarget);
        ToggleMonitoringCommand = new RelayCommand(ToggleMonitoring);
        ProbeNowCommand = new RelayCommand(ProbeNow);
        OpenTraceCommand = new RelayCommand<TargetViewModel>(OpenTrace);
        BackToListCommand = new RelayCommand(BackToList);
        StopTraceCommand = new RelayCommand(StopTrace);

        _monitorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _monitorTimer.Tick += async (_, _) => await RunMonitoringCycleAsync();
    }

    public ObservableCollection<TargetViewModel> Targets { get; }

    public ICommand AddTargetCommand { get; }

    public ICommand RemoveTargetCommand { get; }

    public ICommand ToggleMonitoringCommand { get; }

    public ICommand ProbeNowCommand { get; }

    public ICommand OpenTraceCommand { get; }

    public ICommand BackToListCommand { get; }

    public ICommand StopTraceCommand { get; }

    public string NewAddress
    {
        get => _newAddress;
        set => SetProperty(ref _newAddress, value);
    }

    public string NewName
    {
        get => _newName;
        set => SetProperty(ref _newName, value);
    }

    public string NewDescription
    {
        get => _newDescription;
        set => SetProperty(ref _newDescription, value);
    }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (!SetProperty(ref _isMonitoring, value))
            {
                return;
            }

            OnPropertyChanged(nameof(MonitoringButtonText));
        }
    }

    public string MonitoringButtonText => IsMonitoring ? "Pause Monitoring" : "Resume Monitoring";

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetProperty(ref _startWithWindows, value))
            {
                return;
            }

            if (!_isInitialized)
            {
                return;
            }

            TryApplyStartup(value);
            _ = SaveStateSafeAsync();
        }
    }

    public bool IsTraceView
    {
        get => _isTraceView;
        private set
        {
            if (!SetProperty(ref _isTraceView, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsListView));
        }
    }

    public bool IsListView => !IsTraceView;

    public TraceSessionViewModel? TraceSession
    {
        get => _traceSession;
        private set => SetProperty(ref _traceSession, value);
    }

    public string TraceCaption
    {
        get => _traceCaption;
        private set => SetProperty(ref _traceCaption, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        var state = await _stateStore.LoadAsync(_shutdownTokenSource.Token);

        Targets.Clear();
        foreach (var targetState in state.Targets)
        {
            Targets.Add(TargetViewModel.FromState(targetState));
        }

        _startWithWindows = state.StartWithWindows;
        OnPropertyChanged(nameof(StartWithWindows));

        TryApplyStartup(_startWithWindows, updateStatusOnSuccess: false);

        _monitorTimer.Start();
        _isInitialized = true;

        StatusMessage = Targets.Count == 0
            ? "Add your first domain or IP to start monitoring."
            : $"Loaded targets: {Targets.Count}. Monitoring is active.";

        await RunMonitoringCycleAsync();
    }

    public async Task ShutdownAsync()
    {
        await StopTraceSessionAsync();
        _monitorTimer.Stop();
        await SaveStateSafeAsync();
        _shutdownTokenSource.Cancel();
        _shutdownTokenSource.Dispose();
    }

    private void AddTarget()
    {
        if (!AddressParser.TryNormalize(NewAddress, out var normalizedAddress, out var suggestedName))
        {
            StatusMessage = "Enter a valid domain or IP address.";
            return;
        }

        if (Targets.Any(target => target.Address.Equals(normalizedAddress, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "This address is already monitored.";
            return;
        }

        var target = TargetViewModel.CreateNew(
            string.IsNullOrWhiteSpace(NewName) ? suggestedName : NewName.Trim(),
            NewDescription.Trim(),
            normalizedAddress);

        Targets.Add(target);

        NewAddress = string.Empty;
        NewName = string.Empty;
        NewDescription = string.Empty;

        StatusMessage = $"Added target: {target.Address}.";

        _ = SaveStateSafeAsync();
        _ = ProbeTargetAsync(target, _shutdownTokenSource.Token);
    }

    private void RemoveTarget(TargetViewModel? target)
    {
        if (target is null)
        {
            return;
        }

        Targets.Remove(target);
        StatusMessage = $"Removed target: {target.Address}.";
        _ = SaveStateSafeAsync();
    }

    private void ToggleMonitoring()
    {
        IsMonitoring = !IsMonitoring;

        StatusMessage = IsMonitoring
            ? "Monitoring resumed."
            : "Monitoring paused.";

        if (IsMonitoring)
        {
            _ = RunMonitoringCycleAsync();
        }
    }

    private void ProbeNow()
    {
        _ = RunMonitoringCycleAsync(force: true);
    }

    private void OpenTrace(TargetViewModel? target)
    {
        if (target is null)
        {
            return;
        }

        _ = OpenTraceAsync(target);
    }

    private void BackToList()
    {
        _ = BackToListAsync();
    }

    private void StopTrace()
    {
        _ = StopTraceAsync();
    }

    private async Task OpenTraceAsync(TargetViewModel target)
    {
        await StopTraceSessionAsync();

        var traceSession = new TraceSessionViewModel(target.Name, target.Address);
        TraceSession = traceSession;
        TraceCaption = $"Traceroute - {target.Name} ({target.Address})";
        IsTraceView = true;

        StatusMessage = $"Running traceroute for {target.Address}.";
        await traceSession.StartAsync();
    }

    private async Task BackToListAsync()
    {
        IsTraceView = false;
        await StopTraceSessionAsync();
        StatusMessage = "Returned to monitor list.";
    }

    private async Task StopTraceAsync()
    {
        if (TraceSession is null)
        {
            return;
        }

        await TraceSession.StopAsync();
        StatusMessage = "Traceroute stopped.";
    }

    private async Task StopTraceSessionAsync()
    {
        if (TraceSession is null)
        {
            return;
        }

        await TraceSession.StopAsync();
        TraceSession = null;
        TraceCaption = "Traceroute";
    }

    private async Task RunMonitoringCycleAsync(bool force = false)
    {
        if (!force && (!IsMonitoring || Targets.Count == 0))
        {
            return;
        }

        if (!await _cycleSemaphore.WaitAsync(0, _shutdownTokenSource.Token))
        {
            return;
        }

        try
        {
            var snapshot = Targets.ToList();
            var tasks = snapshot.Select(target => ProbeTargetAsync(target, _shutdownTokenSource.Token));
            await Task.WhenAll(tasks);

            _saveTickCounter++;
            if (_saveTickCounter >= 10)
            {
                _saveTickCounter = 0;
                await SaveStateSafeAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusMessage = $"Monitoring loop failed: {exception.Message}";
        }
        finally
        {
            _cycleSemaphore.Release();
        }
    }

    private async Task ProbeTargetAsync(TargetViewModel target, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _probeService.ProbeAsync(target.Address, cancellationToken);
            target.ApplyProbeResult(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            target.ApplyProbeResult(new ProbeResult
            {
                Timestamp = DateTimeOffset.UtcNow,
                IsOnline = false,
                PingMs = null,
                ResolvedIp = null,
                Error = exception.Message
            });
        }
    }

    private void TryApplyStartup(bool enabled, bool updateStatusOnSuccess = true)
    {
        try
        {
            _startupService.SetEnabled(enabled);
            if (updateStatusOnSuccess)
            {
                StatusMessage = enabled
                    ? "Autostart is enabled."
                    : "Autostart is disabled.";
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not update autostart: {exception.Message}";
        }
    }

    private async Task SaveStateSafeAsync()
    {
        await _saveSemaphore.WaitAsync();
        try
        {
            await _stateStore.SaveAsync(new AppState
            {
                StartWithWindows = StartWithWindows,
                Targets = Targets.Select(static target => target.ToState()).ToList()
            });
        }
        catch (Exception exception)
        {
            StatusMessage = $"Save failed: {exception.Message}";
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }
}
