using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using HealthChecker_WinUI.Infrastructure;
using HealthChecker_WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HealthChecker_WinUI.ViewModels;

public sealed class MonitoringCoordinator : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan InMemoryHistoryWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan HistoryRefreshInterval = TimeSpan.FromSeconds(15);

    private static readonly SolidColorBrush FleetHistoryGood = new(ColorHelper.FromArgb(255, 35, 187, 131));
    private static readonly SolidColorBrush FleetHistoryWarn = new(ColorHelper.FromArgb(255, 233, 184, 67));
    private static readonly SolidColorBrush FleetHistoryBad = new(ColorHelper.FromArgb(255, 232, 86, 86));

    private readonly SqliteMonitoringStore _stateStore = new();
    private readonly NetworkProbeService _probeService = new();
    private readonly MonitoringWorkerService _worker;
    private readonly StartupRegistrationService _startupService = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Dictionary<Guid, DateTimeOffset> _targetHistoryRefresh = [];
    private readonly object _snapshotGate = new();
    private List<MonitoringTarget> _targetsSnapshot = [];

    private bool _initialized;
    private bool _isMonitoring = true;
    private bool _startWithWindows;
    private bool _startMinimizedToTray;
    private int _onlineTargets;
    private int _offlineTargets;
    private double _fleetUptimeLastHourPercent;
    private string _fleetAveragePingDisplay = "-";
    private string _statusMessage = "Loading monitoring state...";
    private string _newName = string.Empty;
    private string _newAddress = string.Empty;
    private string _newDescription = string.Empty;
    private DateTimeOffset _lastSummaryRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFleetHistoryRefresh = DateTimeOffset.MinValue;
    private long _minimumAcceptedSampleUnix;

    private bool _isTraceView;
    private string _traceCaption = "Traceroute";
    private TraceSessionViewModel? _traceSession;

    public MonitoringCoordinator(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _worker = new MonitoringWorkerService(_probeService, TimeSpan.FromSeconds(1));

        Targets = [];
        FleetHistoryBars = [];
    }

    public ObservableCollection<MonitoringTargetViewModel> Targets { get; }

    public ObservableCollection<SampleBarViewModel> FleetHistoryBars { get; }

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

            if (!_initialized)
            {
                return;
            }

            TryApplyStartup(value);
            _ = SaveStartupSettingAsync(value, _shutdownCts.Token);
        }
    }

    public bool StartMinimizedToTray
    {
        get => _startMinimizedToTray;
        set
        {
            if (!SetProperty(ref _startMinimizedToTray, value))
            {
                return;
            }

            if (!_initialized)
            {
                return;
            }

            _ = SaveStartMinimizedSettingAsync(value, _shutdownCts.Token);
        }
    }

    public int TotalTargets => Targets.Count;

    public int OnlineTargets
    {
        get => _onlineTargets;
        private set => SetProperty(ref _onlineTargets, value);
    }

    public int OfflineTargets
    {
        get => _offlineTargets;
        private set => SetProperty(ref _offlineTargets, value);
    }

    public int WaitingTargets => Math.Max(0, TotalTargets - OnlineTargets - OfflineTargets);

    public double FleetUptimeLastHourPercent
    {
        get => _fleetUptimeLastHourPercent;
        private set
        {
            if (!SetProperty(ref _fleetUptimeLastHourPercent, value))
            {
                return;
            }

            OnPropertyChanged(nameof(FleetUptimeDisplay));
        }
    }

    public string FleetUptimeDisplay => $"{FleetUptimeLastHourPercent:0.0}%";

    public string FleetAveragePingDisplay
    {
        get => _fleetAveragePingDisplay;
        private set => SetProperty(ref _fleetAveragePingDisplay, value);
    }

    public string FleetHistoryHint => FleetHistoryBars.Count == 0
        ? "No persisted data yet."
        : "Global Full History compressed to 60 buckets.";

    public string StateStorePathDisplay => _stateStore.DatabasePathDisplay;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string NewName
    {
        get => _newName;
        set => SetProperty(ref _newName, value);
    }

    public string NewAddress
    {
        get => _newAddress;
        set => SetProperty(ref _newAddress, value);
    }

    public string NewDescription
    {
        get => _newDescription;
        set => SetProperty(ref _newDescription, value);
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

            OnPropertyChanged(nameof(ListPaneVisibility));
            OnPropertyChanged(nameof(TracePaneVisibility));
        }
    }

    public Visibility ListPaneVisibility => IsTraceView ? Visibility.Collapsed : Visibility.Visible;

    public Visibility TracePaneVisibility => IsTraceView ? Visibility.Visible : Visibility.Collapsed;

    public string TraceCaption
    {
        get => _traceCaption;
        private set => SetProperty(ref _traceCaption, value);
    }

    public TraceSessionViewModel? TraceSession
    {
        get => _traceSession;
        private set
        {
            if (!SetProperty(ref _traceSession, value))
            {
                return;
            }

            OnPropertyChanged(nameof(TraceStatusDisplay));
            OnPropertyChanged(nameof(IsTraceRunning));
        }
    }

    public string TraceStatusDisplay => TraceSession?.StatusText ?? "Idle";

    public bool IsTraceRunning => TraceSession?.IsRunning == true;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _stateStore.InitializeAsync(_shutdownCts.Token);

        var loadedTargets = await _stateStore.LoadTargetsAsync(InMemoryHistoryWindow, _shutdownCts.Token);

        await RunOnUiAsync(() =>
        {
            Targets.Clear();

            foreach (var state in loadedTargets)
            {
                var target = MonitoringTargetViewModel.FromState(state);
                AttachTarget(target);
                Targets.Add(target);
            }

            RebuildTargetsSnapshot();
            RecalculateSummaryFromTargets();
        }, _shutdownCts.Token);

        _startWithWindows = await _stateStore.LoadStartWithWindowsAsync(_shutdownCts.Token);
        OnPropertyChanged(nameof(StartWithWindows));
        TryApplyStartup(_startWithWindows, updateStatusOnSuccess: false);

        _startMinimizedToTray = await _stateStore.LoadStartMinimizedToTrayAsync(_shutdownCts.Token);
        OnPropertyChanged(nameof(StartMinimizedToTray));

        await _worker.StartAsync(GetTargetsSnapshot, HandleProbeResultAsync, OnWorkerError, _shutdownCts.Token);
        _worker.IsPaused = !IsMonitoring;

        _initialized = true;

        StatusMessage = Targets.Count == 0
            ? "Add your first domain or IP to start monitoring."
            : $"Loaded targets: {Targets.Count}. Monitoring is active.";

        await RefreshFleetHistoryAsync(force: true, _shutdownCts.Token);
        await _worker.ProbeNowAsync(_shutdownCts.Token);
    }

    public async Task AddTargetFromInputsAsync()
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

        var target = MonitoringTargetViewModel.CreateNew(
            string.IsNullOrWhiteSpace(NewName) ? suggestedName : NewName.Trim(),
            NewDescription.Trim(),
            normalizedAddress);

        await RunOnUiAsync(() =>
        {
            AttachTarget(target);
            Targets.Add(target);
            RebuildTargetsSnapshot();
            RecalculateSummaryFromTargets();
        }, _shutdownCts.Token);

        await _stateStore.UpsertTargetAsync(target.ToMetadataState(), _shutdownCts.Token);

        NewAddress = string.Empty;
        NewName = string.Empty;
        NewDescription = string.Empty;

        StatusMessage = $"Added target: {target.Address}.";

        await RefreshFleetHistoryAsync(force: true, _shutdownCts.Token);
        await _worker.ProbeNowAsync(_shutdownCts.Token);
    }

    public async Task RemoveTargetAsync(MonitoringTargetViewModel? target)
    {
        if (target is null)
        {
            return;
        }

        await RunOnUiAsync(() =>
        {
            DetachTarget(target);
            Targets.Remove(target);
            RebuildTargetsSnapshot();
            RecalculateSummaryFromTargets();
        }, _shutdownCts.Token);

        await _stateStore.RemoveTargetAsync(target.Id, _shutdownCts.Token);
        await RefreshFleetHistoryAsync(force: true, _shutdownCts.Token);

        StatusMessage = $"Removed target: {target.Address}.";
    }

    public async Task ProbeNowAsync()
    {
        await _worker.ProbeNowAsync(_shutdownCts.Token);
    }

    public Task OpenStorageFolderAsync()
    {
        try
        {
            Directory.CreateDirectory(_stateStore.DataDirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = _stateStore.DataDirectoryPath,
                UseShellExecute = true
            });

            StatusMessage = "Opened storage folder.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not open storage folder: {exception.Message}";
        }

        return Task.CompletedTask;
    }

    public async Task CompactDatabaseAsync()
    {
        try
        {
            StatusMessage = "Compacting database...";
            await _stateStore.CompactDatabaseAsync(_shutdownCts.Token);
            await RefreshFleetHistoryAsync(force: true, _shutdownCts.Token);
            StatusMessage = "Database compacted.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusMessage = $"Database compact failed: {exception.Message}";
        }
    }

    public Task ResetStatisticsAsync()
    {
        var resetTimestampUtc = DateTimeOffset.UtcNow;
        Volatile.Write(ref _minimumAcceptedSampleUnix, resetTimestampUtc.ToUnixTimeSeconds());

        foreach (var target in Targets)
        {
            target.ResetStatistics();
        }

        FleetHistoryBars.Clear();
        _targetHistoryRefresh.Clear();
        RecalculateSummaryFromTargets();
        OnPropertyChanged(nameof(FleetHistoryHint));

        StatusMessage = $"Statistics reset requested at {resetTimestampUtc.ToLocalTime():dd.MM HH:mm:ss}.";

        _ = Task.Run(async () =>
        {
            try
            {
                await _stateStore.ResetStatisticsAsync(resetTimestampUtc, _shutdownCts.Token);
                await RunOnUiAsync(() =>
                {
                    StatusMessage = $"Statistics reset completed. Deleted samples up to {resetTimestampUtc.ToLocalTime():dd.MM HH:mm:ss}.";
                }, _shutdownCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await RunOnUiAsync(() =>
                {
                    StatusMessage = $"Statistics reset failed: {exception.Message}";
                }, _shutdownCts.Token);
            }
        });

        return Task.CompletedTask;
    }

    public void ToggleMonitoring()
    {
        IsMonitoring = !IsMonitoring;
        _worker.IsPaused = !IsMonitoring;

        StatusMessage = IsMonitoring ? "Monitoring resumed." : "Monitoring paused.";

        if (IsMonitoring)
        {
            _ = _worker.ProbeNowAsync(_shutdownCts.Token);
        }
    }

    public async Task ToggleExpandedAsync(MonitoringTargetViewModel? target)
    {
        if (target is null)
        {
            return;
        }

        target.IsExpanded = !target.IsExpanded;

        if (target.IsExpanded)
        {
            await RefreshTargetHistoryAsync(target, force: true, _shutdownCts.Token);
        }
    }

    public async Task OpenTraceForTargetAsync(MonitoringTargetViewModel? target)
    {
        if (target is null)
        {
            return;
        }

        await StopTraceSessionAsync(clearSession: true);

        var session = new TraceSessionViewModel(target.Name, target.Address, _dispatcherQueue);
        session.PropertyChanged += OnTraceSessionPropertyChanged;

        TraceSession = session;
        TraceCaption = $"Traceroute - {target.Name} ({target.Address})";
        IsTraceView = true;

        StatusMessage = $"Running traceroute for {target.Address}.";
        await session.StartAsync();
    }

    public async Task BackFromTraceAsync()
    {
        IsTraceView = false;
        await StopTraceSessionAsync(clearSession: true);
        StatusMessage = "Returned to monitor list.";
    }

    public async Task StopTraceAsync()
    {
        if (TraceSession is null)
        {
            return;
        }

        await TraceSession.StopAsync();
        StatusMessage = "Traceroute stopped.";
        OnPropertyChanged(nameof(TraceStatusDisplay));
        OnPropertyChanged(nameof(IsTraceRunning));
    }

    private IReadOnlyList<MonitoringTarget> GetTargetsSnapshot()
    {
        lock (_snapshotGate)
        {
            return _targetsSnapshot.ToList();
        }
    }

    private async Task HandleProbeResultAsync(
        MonitoringTarget targetSnapshot,
        ProbeResult result,
        CancellationToken cancellationToken)
    {
        if (result.Timestamp.ToUnixTimeSeconds() <= Volatile.Read(ref _minimumAcceptedSampleUnix))
        {
            return;
        }

        await _stateStore.AppendProbeAsync(targetSnapshot.Id, result, cancellationToken);

        MonitoringTargetViewModel? targetViewModel = null;

        await RunOnUiAsync(() =>
        {
            targetViewModel = Targets.FirstOrDefault(target => target.Id == targetSnapshot.Id);
            targetViewModel?.ApplyProbeResult(result);
            RecalculateSummaryFromTargets();
        }, cancellationToken);

        if (targetViewModel is null)
        {
            return;
        }

        if (targetViewModel.IsExpanded)
        {
            await RefreshTargetHistoryAsync(targetViewModel, force: false, cancellationToken);
        }

        await RefreshFleetHistoryAsync(force: false, cancellationToken);
    }

    private async Task RefreshTargetHistoryAsync(
        MonitoringTargetViewModel target,
        bool force,
        CancellationToken cancellationToken)
    {
        if (!force && !CanRefreshTargetHistory(target.Id))
        {
            return;
        }

        var buckets = await _stateStore.LoadHistoryBucketsAsync(target.Id, maxBuckets: 60, cancellationToken);

        await RunOnUiAsync(() => target.SetFullHistoryBuckets(buckets), cancellationToken);
    }

    private async Task RefreshFleetHistoryAsync(bool force, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (!force && now - _lastFleetHistoryRefresh < HistoryRefreshInterval)
        {
            return;
        }

        _lastFleetHistoryRefresh = now;

        var buckets = await _stateStore.LoadGlobalHistoryBucketsAsync(maxBuckets: 60, cancellationToken);

        await RunOnUiAsync(() =>
        {
            FleetHistoryBars.Clear();

            foreach (var bucket in buckets)
            {
                var uptime = bucket.Samples == 0 ? 0 : bucket.OnlineSamples / (double)bucket.Samples * 100;
                var color = uptime >= 95 ? FleetHistoryGood : uptime >= 80 ? FleetHistoryWarn : FleetHistoryBad;
                var avgPing = bucket.AveragePingMs.HasValue ? $"{bucket.AveragePingMs.Value} ms" : "n/a";

                FleetHistoryBars.Add(new SampleBarViewModel
                {
                    Height = 8 + (56 * (uptime / 100.0)),
                    Fill = color,
                    ToolTip = $"{bucket.StartUtc.ToLocalTime():dd.MM HH:mm} - {bucket.EndUtc.ToLocalTime():dd.MM HH:mm}\nUptime: {uptime:0.0}%\nAvg ping: {avgPing}"
                });
            }

            OnPropertyChanged(nameof(FleetHistoryHint));
        }, cancellationToken);
    }

    private bool CanRefreshTargetHistory(Guid targetId)
    {
        var now = DateTimeOffset.UtcNow;

        if (!_targetHistoryRefresh.TryGetValue(targetId, out var last))
        {
            _targetHistoryRefresh[targetId] = now;
            return true;
        }

        if (now - last < HistoryRefreshInterval)
        {
            return false;
        }

        _targetHistoryRefresh[targetId] = now;
        return true;
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

    private async Task SaveStartMinimizedSettingAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            await _stateStore.SaveStartMinimizedToTrayAsync(enabled, cancellationToken);
            StatusMessage = enabled
                ? "App will start minimized to tray."
                : "App will start with main window visible.";
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
                StatusMessage = enabled ? "Autostart is enabled." : "Autostart is disabled.";
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not update autostart: {exception.Message}";
        }
    }

    private void OnWorkerError(Exception exception)
    {
        _ = RunOnUiAsync(() =>
        {
            StatusMessage = $"Monitoring loop failed: {exception.Message}";
        });
    }

    private void AttachTarget(MonitoringTargetViewModel target)
    {
        target.ExpandedChanged += OnTargetExpandedChanged;
    }

    private void DetachTarget(MonitoringTargetViewModel target)
    {
        target.ExpandedChanged -= OnTargetExpandedChanged;
        _targetHistoryRefresh.Remove(target.Id);
    }

    private void OnTargetExpandedChanged(object? sender, bool isExpanded)
    {
        if (!isExpanded || sender is not MonitoringTargetViewModel target)
        {
            return;
        }

        _ = RefreshTargetHistoryAsync(target, force: true, _shutdownCts.Token);
    }

    private void OnTraceSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TraceSessionViewModel.StatusText) or nameof(TraceSessionViewModel.IsRunning))
        {
            OnPropertyChanged(nameof(TraceStatusDisplay));
            OnPropertyChanged(nameof(IsTraceRunning));
        }
    }

    private async Task StopTraceSessionAsync(bool clearSession)
    {
        var session = TraceSession;
        if (session is null)
        {
            if (clearSession)
            {
                TraceCaption = "Traceroute";
            }

            return;
        }

        await session.StopAsync();

        if (clearSession)
        {
            session.PropertyChanged -= OnTraceSessionPropertyChanged;
            TraceSession = null;
            TraceCaption = "Traceroute";
            OnPropertyChanged(nameof(TraceStatusDisplay));
            OnPropertyChanged(nameof(IsTraceRunning));
        }
    }

    private void RecalculateSummaryFromTargets()
    {
        var online = Targets.Count(static target => target.IsOnline == true);
        var offline = Targets.Count(static target => target.IsOnline == false);

        OnlineTargets = online;
        OfflineTargets = offline;

        var pings = Targets.Where(static target => target.CurrentPingMs.HasValue).Select(static target => target.CurrentPingMs!.Value).ToList();
        FleetAveragePingDisplay = pings.Count == 0 ? "-" : $"{pings.Average():0} ms";

        FleetUptimeLastHourPercent = Targets.Count == 0
            ? 0
            : Targets.Average(static target => target.UptimeLastHourPercent);

        if (DateTimeOffset.UtcNow - _lastSummaryRefresh > TimeSpan.FromSeconds(1))
        {
            _lastSummaryRefresh = DateTimeOffset.UtcNow;
            OnPropertyChanged(nameof(TotalTargets));
            OnPropertyChanged(nameof(WaitingTargets));
        }
    }

    private void RebuildTargetsSnapshot()
    {
        lock (_snapshotGate)
        {
            _targetsSnapshot = Targets.Select(static target => new MonitoringTarget(target.Id, target.Address)).ToList();
        }
    }

    private Task RunOnUiAsync(Action action, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    action();
                    tcs.TrySetResult();
                }
                catch (Exception exception)
                {
                    tcs.TrySetException(exception);
                }
            }))
        {
            tcs.TrySetException(new InvalidOperationException("Could not enqueue UI work."));
        }

        return tcs.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        await StopTraceSessionAsync(clearSession: true);
        await _worker.StopAsync();
        await _worker.DisposeAsync();
        _shutdownCts.Dispose();
    }
}
