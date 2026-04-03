using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using HealthChecker.Models;
using HealthChecker.Services;

namespace HealthChecker.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly TimeSpan InMemoryHistoryWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan FullHistoryRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly SqliteMonitoringStore _stateStore = new();
    private readonly NetworkProbeService _probeService = new();
    private readonly MonitoringWorkerService _monitorWorker;
    private readonly StartupRegistrationService _startupService = new();
    private readonly CancellationTokenSource _shutdownTokenSource = new();
    private readonly Dispatcher _dispatcher;
    private readonly object _historyRefreshSync = new();
    private readonly Dictionary<Guid, DateTimeOffset> _historyRefreshMoments = [];

    private bool _isInitialized;
    private bool _isMonitoring = true;
    private bool _startWithWindows;
    private bool _isTraceView;
    private string _newAddress = string.Empty;
    private string _newName = string.Empty;
    private string _newDescription = string.Empty;
    private string _statusMessage = "Ready.";
    private string _traceCaption = "Traceroute";
    private TraceSessionViewModel? _traceSession;

    public MainViewModel()
    {
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _monitorWorker = new MonitoringWorkerService(_probeService, TimeSpan.FromSeconds(1));

        Targets = [];

        AddTargetCommand = new RelayCommand(AddTarget);
        RemoveTargetCommand = new RelayCommand<TargetViewModel>(RemoveTarget);
        ToggleMonitoringCommand = new RelayCommand(ToggleMonitoring);
        ProbeNowCommand = new RelayCommand(ProbeNow);
        OpenTraceCommand = new RelayCommand<TargetViewModel>(OpenTrace);
        BackToListCommand = new RelayCommand(BackToList);
        StopTraceCommand = new RelayCommand(StopTrace);
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
            _ = SaveStartupSettingAsync(value, _shutdownTokenSource.Token);
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

    public string StateStorePathDisplay => _stateStore.DatabasePathDisplay;

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        await _stateStore.InitializeAsync(_shutdownTokenSource.Token);

        var loadedTargets = await _stateStore.LoadTargetsAsync(InMemoryHistoryWindow, _shutdownTokenSource.Token);

        Targets.Clear();
        foreach (var targetState in loadedTargets)
        {
            var target = TargetViewModel.FromState(targetState);
            AttachTarget(target);
            Targets.Add(target);
        }

        _startWithWindows = await _stateStore.LoadStartWithWindowsAsync(_shutdownTokenSource.Token);
        OnPropertyChanged(nameof(StartWithWindows));

        TryApplyStartup(_startWithWindows, updateStatusOnSuccess: false);

        await _monitorWorker.StartAsync(
            GetMonitoringTargetsSnapshot,
            HandleProbeResultAsync,
            OnWorkerError,
            _shutdownTokenSource.Token);

        _monitorWorker.IsPaused = !IsMonitoring;

        _isInitialized = true;

        StatusMessage = Targets.Count == 0
            ? "Add your first domain or IP to start monitoring."
            : $"Loaded targets: {Targets.Count}. Monitoring is active.";

        await _monitorWorker.ProbeNowAsync(_shutdownTokenSource.Token);
    }

    public async Task ShutdownAsync()
    {
        await StopTraceSessionAsync();
        await _monitorWorker.StopAsync();
        await _monitorWorker.DisposeAsync();
        _shutdownTokenSource.Cancel();
        _shutdownTokenSource.Dispose();
    }

    private void AddTarget()
    {
        _ = AddTargetAsync();
    }

    private async Task AddTargetAsync()
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

        AttachTarget(target);
        Targets.Add(target);

        await _stateStore.UpsertTargetAsync(target.ToMetadataState(), _shutdownTokenSource.Token);

        NewAddress = string.Empty;
        NewName = string.Empty;
        NewDescription = string.Empty;

        StatusMessage = $"Added target: {target.Address}.";

        await _monitorWorker.ProbeNowAsync(_shutdownTokenSource.Token);
    }

    private void RemoveTarget(TargetViewModel? target)
    {
        if (target is null)
        {
            return;
        }

        _ = RemoveTargetAsync(target);
    }

    private async Task RemoveTargetAsync(TargetViewModel target)
    {
        Targets.Remove(target);
        DetachTarget(target);

        await _stateStore.RemoveTargetAsync(target.Id, _shutdownTokenSource.Token);

        StatusMessage = $"Removed target: {target.Address}.";
    }

    private void ToggleMonitoring()
    {
        IsMonitoring = !IsMonitoring;
        _monitorWorker.IsPaused = !IsMonitoring;

        StatusMessage = IsMonitoring
            ? "Monitoring resumed."
            : "Monitoring paused.";

        if (IsMonitoring)
        {
            _ = _monitorWorker.ProbeNowAsync(_shutdownTokenSource.Token);
        }
    }

    private void ProbeNow()
    {
        _ = _monitorWorker.ProbeNowAsync(_shutdownTokenSource.Token);
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

    private IReadOnlyList<MonitoringTarget> GetMonitoringTargetsSnapshot()
    {
        if (_dispatcher.CheckAccess())
        {
            return Targets.Select(static target => new MonitoringTarget(target.Id, target.Address)).ToList();
        }

        return _dispatcher.Invoke(() =>
            Targets.Select(static target => new MonitoringTarget(target.Id, target.Address)).ToList());
    }

    private async Task HandleProbeResultAsync(
        MonitoringTarget targetSnapshot,
        ProbeResult result,
        CancellationToken cancellationToken)
    {
        await _stateStore.AppendProbeAsync(targetSnapshot.Id, result, cancellationToken);

        TargetViewModel? targetViewModel = null;

        await _dispatcher.InvokeAsync(() =>
        {
            targetViewModel = Targets.FirstOrDefault(target => target.Id == targetSnapshot.Id);
            targetViewModel?.ApplyProbeResult(result);
        }, DispatcherPriority.Background, cancellationToken);

        if (targetViewModel is null)
        {
            return;
        }

        if (targetViewModel.IsExpanded)
        {
            await RefreshFullHistoryAsync(targetViewModel, force: false, cancellationToken);
        }
    }

    private async Task RefreshFullHistoryAsync(
        TargetViewModel target,
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (!force && !ShouldRefreshFullHistory(target.Id))
        {
            return;
        }

        var buckets = await _stateStore.LoadHistoryBucketsAsync(target.Id, maxBuckets: 120, cancellationToken);

        var viewModels = buckets.Select(static bucket => new HistoryBucketViewModel
        {
            StartUtc = bucket.StartUtc,
            EndUtc = bucket.EndUtc,
            Samples = bucket.Samples,
            OnlineSamples = bucket.OnlineSamples,
            AveragePingMs = bucket.AveragePingMs
        }).ToList();

        await _dispatcher.InvokeAsync(() => target.SetFullHistoryBuckets(viewModels), DispatcherPriority.Background, cancellationToken);
    }

    private bool ShouldRefreshFullHistory(Guid targetId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_historyRefreshSync)
        {
            if (!_historyRefreshMoments.TryGetValue(targetId, out var lastRefresh))
            {
                _historyRefreshMoments[targetId] = now;
                return true;
            }

            if (now - lastRefresh < FullHistoryRefreshInterval)
            {
                return false;
            }

            _historyRefreshMoments[targetId] = now;
            return true;
        }
    }

    private async Task SaveStartupSettingAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            await _stateStore.SaveStartWithWindowsAsync(enabled, cancellationToken);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Save failed: {exception.Message}";
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

    private void OnWorkerError(Exception exception)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            StatusMessage = $"Monitoring loop failed: {exception.Message}";
        });
    }

    private void AttachTarget(TargetViewModel target)
    {
        target.ExpandedChanged += OnTargetExpandedChanged;
    }

    private void DetachTarget(TargetViewModel target)
    {
        target.ExpandedChanged -= OnTargetExpandedChanged;
        lock (_historyRefreshSync)
        {
            _historyRefreshMoments.Remove(target.Id);
        }
    }

    private void OnTargetExpandedChanged(object? sender, bool isExpanded)
    {
        if (!isExpanded || sender is not TargetViewModel target)
        {
            return;
        }

        _ = RefreshFullHistoryAsync(target, force: true, _shutdownTokenSource.Token);
    }
}
