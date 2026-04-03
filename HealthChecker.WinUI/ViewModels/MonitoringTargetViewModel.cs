using System.Collections.ObjectModel;
using HealthChecker_WinUI.Infrastructure;
using HealthChecker_WinUI.Models;
using HealthChecker_WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HealthChecker_WinUI.ViewModels;

public sealed class MonitoringTargetViewModel : ObservableObject
{
    private const int RecentSampleCount = 60;
    private static readonly TimeSpan HourWindow = TimeSpan.FromHours(1);

    private static readonly SolidColorBrush BrushOnline = new(Colors.MediumSeaGreen);
    private static readonly SolidColorBrush BrushOffline = new(Colors.IndianRed);
    private static readonly SolidColorBrush BrushUnknown = new(Colors.SlateGray);
    private static readonly SolidColorBrush BrushPing = new(ColorHelper.FromArgb(255, 53, 184, 120));
    private static readonly SolidColorBrush BrushHistoryGood = new(ColorHelper.FromArgb(255, 35, 187, 131));
    private static readonly SolidColorBrush BrushHistoryWarn = new(ColorHelper.FromArgb(255, 233, 184, 67));

    private readonly List<HealthSample> _lastHourSamples = [];
    private readonly List<HealthSample> _recentSamples = [];

    private string _name;
    private string _description;
    private string _address;
    private string? _resolvedIp;
    private bool? _isOnline;
    private long? _currentPingMs;
    private DateTimeOffset? _lastCheckedUtc;
    private DateTimeOffset? _lastOnlineUtc;
    private string _lastError = string.Empty;
    private bool _isExpanded;

    private MonitoringTargetViewModel(
        Guid id,
        string name,
        string description,
        string address,
        string? resolvedIp,
        DateTimeOffset? lastCheckedUtc,
        DateTimeOffset? lastOnlineUtc,
        List<HealthSample>? samples)
    {
        Id = id;
        _name = name;
        _description = description;
        _address = address;
        _resolvedIp = resolvedIp;
        _lastCheckedUtc = lastCheckedUtc;
        _lastOnlineUtc = lastOnlineUtc;

        AvailabilityBars = [];
        PingBars = [];
        FullHistoryBars = [];

        if (samples is { Count: > 0 })
        {
            foreach (var sample in samples.OrderBy(static sample => sample.Timestamp))
            {
                AppendSampleToWindows(sample);
            }

            var latest = _recentSamples[^1];
            _isOnline = latest.IsOnline;
            _currentPingMs = latest.PingMs;
        }

        RebuildRecentCharts();
    }

    public Guid Id { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    public bool? IsOnline
    {
        get => _isOnline;
        private set => SetProperty(ref _isOnline, value);
    }

    public long? CurrentPingMs
    {
        get => _currentPingMs;
        private set => SetProperty(ref _currentPingMs, value);
    }

    public DateTimeOffset? LastCheckedUtc
    {
        get => _lastCheckedUtc;
        private set => SetProperty(ref _lastCheckedUtc, value);
    }

    public DateTimeOffset? LastOnlineUtc
    {
        get => _lastOnlineUtc;
        private set => SetProperty(ref _lastOnlineUtc, value);
    }

    public string LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public string ResolvedIpDisplay => string.IsNullOrWhiteSpace(_resolvedIp) ? "-" : _resolvedIp;

    public string CurrentPingDisplay => CurrentPingMs.HasValue ? $"{CurrentPingMs.Value} ms" : "-";

    public string LastCheckDisplay => LastCheckedUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "-";

    public string LastSeenDisplay => LastOnlineUtc?.ToLocalTime().ToString("dd.MM HH:mm:ss") ?? "-";

    public string StatusText => IsOnline switch
    {
        true => "Online",
        false => "Offline",
        _ => "Waiting"
    };

    public Brush StatusBrush => IsOnline switch
    {
        true => BrushOnline,
        false => BrushOffline,
        _ => BrushUnknown
    };

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value))
            {
                return;
            }

            OnPropertyChanged(nameof(DetailsVisibility));
            OnPropertyChanged(nameof(ExpandGlyph));
            ExpandedChanged?.Invoke(this, value);
        }
    }

    public Visibility DetailsVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    public string ExpandGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    public ObservableCollection<SampleBarViewModel> AvailabilityBars { get; }

    public ObservableCollection<SampleBarViewModel> PingBars { get; }

    public ObservableCollection<SampleBarViewModel> FullHistoryBars { get; }

    public string NoDataHint => _recentSamples.Count == 0 ? "Waiting for first samples..." : string.Empty;

    public string FullHistoryHint => FullHistoryBars.Count == 0
        ? "No persisted history yet."
        : "Full History compressed to 60 buckets (no horizontal scroll).";

    public double UptimeLastHourPercent
    {
        get
        {
            if (_lastHourSamples.Count == 0)
            {
                return 0;
            }

            var online = _lastHourSamples.Count(static sample => sample.IsOnline);
            return online / (double)_lastHourSamples.Count * 100;
        }
    }

    public string UptimeLastHourDisplay => $"{UptimeLastHourPercent:0.0}%";

    public string AveragePingLastHourDisplay
    {
        get
        {
            var pings = _lastHourSamples.Where(static sample => sample.PingMs.HasValue).Select(static sample => sample.PingMs!.Value).ToList();
            if (pings.Count == 0)
            {
                return "-";
            }

            return $"{pings.Average():0} ms";
        }
    }

    public string MinPingLastHourDisplay
    {
        get
        {
            var min = _lastHourSamples.Where(static sample => sample.PingMs.HasValue).Select(static sample => sample.PingMs!.Value).DefaultIfEmpty().Min();
            return min > 0 ? $"{min} ms" : "-";
        }
    }

    public string MaxPingLastHourDisplay
    {
        get
        {
            var max = _lastHourSamples.Where(static sample => sample.PingMs.HasValue).Select(static sample => sample.PingMs!.Value).DefaultIfEmpty().Max();
            return max > 0 ? $"{max} ms" : "-";
        }
    }

    public event EventHandler<bool>? ExpandedChanged;

    public static MonitoringTargetViewModel CreateNew(string name, string description, string address)
    {
        return new MonitoringTargetViewModel(Guid.NewGuid(), name, description, address, null, null, null, null);
    }

    public static MonitoringTargetViewModel FromState(MonitoredTargetState state)
    {
        return new MonitoringTargetViewModel(
            state.Id == Guid.Empty ? Guid.NewGuid() : state.Id,
            state.Name,
            state.Description,
            state.Address,
            state.ResolvedIp,
            state.LastCheckedUtc,
            state.LastOnlineUtc,
            state.Samples);
    }

    public MonitoredTargetState ToMetadataState()
    {
        return new MonitoredTargetState
        {
            Id = Id,
            Name = Name,
            Description = Description,
            Address = Address,
            ResolvedIp = _resolvedIp,
            LastCheckedUtc = LastCheckedUtc,
            LastOnlineUtc = LastOnlineUtc,
            Samples = []
        };
    }

    public void ApplyProbeResult(ProbeResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ResolvedIp))
        {
            _resolvedIp = result.ResolvedIp;
            OnPropertyChanged(nameof(ResolvedIpDisplay));
        }

        IsOnline = result.IsOnline;
        CurrentPingMs = result.PingMs;
        LastCheckedUtc = result.Timestamp;

        if (result.IsOnline)
        {
            LastOnlineUtc = result.Timestamp;
        }

        LastError = result.Error ?? string.Empty;

        AppendSampleToWindows(new HealthSample
        {
            Timestamp = result.Timestamp,
            IsOnline = result.IsOnline,
            PingMs = result.PingMs,
            ResolvedIp = result.ResolvedIp
        });

        RebuildRecentCharts();
        RaiseComputedProperties();
    }

    public void SetFullHistoryBuckets(IReadOnlyList<HistoryBucket> buckets)
    {
        FullHistoryBars.Clear();

        foreach (var bucket in buckets)
        {
            var uptime = bucket.Samples == 0 ? 0 : bucket.OnlineSamples / (double)bucket.Samples * 100;
            var barBrush = uptime >= 95 ? BrushHistoryGood : uptime >= 80 ? BrushHistoryWarn : BrushOffline;
            var avgPing = bucket.AveragePingMs.HasValue ? $"{bucket.AveragePingMs.Value} ms" : "n/a";

            FullHistoryBars.Add(new SampleBarViewModel
            {
                Height = 8 + (56 * (uptime / 100.0)),
                Fill = barBrush,
                ToolTip = $"{bucket.StartUtc.ToLocalTime():dd.MM HH:mm} - {bucket.EndUtc.ToLocalTime():dd.MM HH:mm}\nUptime: {uptime:0.0}%\nAvg ping: {avgPing}"
            });
        }

        OnPropertyChanged(nameof(FullHistoryHint));
    }

    private void AppendSampleToWindows(HealthSample sample)
    {
        _lastHourSamples.Add(sample);
        _recentSamples.Add(sample);

        if (_recentSamples.Count > RecentSampleCount)
        {
            _recentSamples.RemoveAt(0);
        }

        var cutoff = sample.Timestamp.Subtract(HourWindow);
        while (_lastHourSamples.Count > 0 && _lastHourSamples[0].Timestamp < cutoff)
        {
            _lastHourSamples.RemoveAt(0);
        }
    }

    private void RebuildRecentCharts()
    {
        AvailabilityBars.Clear();
        PingBars.Clear();

        if (_recentSamples.Count == 0)
        {
            return;
        }

        var maxPing = Math.Max(80, _recentSamples.Where(static sample => sample.PingMs.HasValue).Select(static sample => sample.PingMs!.Value).DefaultIfEmpty().Max());

        foreach (var sample in _recentSamples)
        {
            AvailabilityBars.Add(new SampleBarViewModel
            {
                Height = 50,
                Fill = sample.IsOnline ? BrushOnline : BrushOffline,
                ToolTip = $"{sample.Timestamp.ToLocalTime():HH:mm:ss} -> {(sample.IsOnline ? "Online" : "Offline")}"
            });

            if (!sample.IsOnline)
            {
                PingBars.Add(new SampleBarViewModel
                {
                    Height = 4,
                    Fill = BrushOffline,
                    ToolTip = $"{sample.Timestamp.ToLocalTime():HH:mm:ss} -> Offline"
                });
                continue;
            }

            var ping = sample.PingMs ?? 0;
            var height = 6 + (50 * (ping / (double)maxPing));

            PingBars.Add(new SampleBarViewModel
            {
                Height = height,
                Fill = BrushPing,
                ToolTip = $"{sample.Timestamp.ToLocalTime():HH:mm:ss} -> {ping} ms"
            });
        }

        OnPropertyChanged(nameof(NoDataHint));
    }

    private void RaiseComputedProperties()
    {
        OnPropertyChanged(nameof(CurrentPingDisplay));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(LastCheckDisplay));
        OnPropertyChanged(nameof(LastSeenDisplay));
        OnPropertyChanged(nameof(UptimeLastHourPercent));
        OnPropertyChanged(nameof(UptimeLastHourDisplay));
        OnPropertyChanged(nameof(AveragePingLastHourDisplay));
        OnPropertyChanged(nameof(MinPingLastHourDisplay));
        OnPropertyChanged(nameof(MaxPingLastHourDisplay));
    }
}
