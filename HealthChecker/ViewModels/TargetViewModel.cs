using HealthChecker.Models;
using HealthChecker.Services;

namespace HealthChecker.ViewModels;

public sealed class TargetViewModel : ObservableObject
{
    private const int HistoryWindowSeconds = 86_400;

    private readonly List<HealthSample> _samples = [];

    private string _name;
    private string _description;
    private string _address;
    private string? _resolvedIp;
    private bool? _isOnline;
    private long? _currentPingMs;
    private DateTimeOffset? _lastCheckedUtc;
    private DateTimeOffset? _lastOnlineUtc;
    private string? _lastError;
    private bool _isExpanded;

    private TargetViewModel(
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

        if (samples is { Count: > 0 })
        {
            _samples.AddRange(samples.OrderBy(static sample => sample.Timestamp));
            var latestSample = _samples[^1];
            _isOnline = latestSample.IsOnline;
            _currentPingMs = latestSample.PingMs;
        }
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

    public string ResolvedIpDisplay => string.IsNullOrWhiteSpace(_resolvedIp) ? "-" : _resolvedIp;

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

    public string CurrentPingDisplay => CurrentPingMs.HasValue ? $"{CurrentPingMs.Value} ms" : "-";

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

    public string LastCheckDisplay => LastCheckedUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "-";

    public string LastSeenDisplay => LastOnlineUtc?.ToLocalTime().ToString("dd.MM HH:mm:ss") ?? "-";

    public string LastError
    {
        get => _lastError ?? string.Empty;
        private set => SetProperty(ref _lastError, value);
    }

    public string StatusText => IsOnline switch
    {
        true => "Online",
        false => "Offline",
        _ => "Waiting"
    };

    public int SamplesCount => _samples.Count;

    public IReadOnlyList<HealthSample> RecentSamples
    {
        get
        {
            if (_samples.Count <= 60)
            {
                return _samples.ToList();
            }

            return _samples[^60..];
        }
    }

    public string NoDataHint => _samples.Count == 0 ? "Waiting for first samples..." : string.Empty;

    public double MaxRecentPing
    {
        get
        {
            var max = RecentSamples
                .Where(static sample => sample.PingMs.HasValue)
                .Select(static sample => (double)sample.PingMs!.Value)
                .DefaultIfEmpty(100)
                .Max();

            return Math.Max(60, max);
        }
    }

    public double UptimeLastHourPercent => CalculateUptime(TimeSpan.FromHours(1));

    public double UptimeLast24HoursPercent => CalculateUptime(TimeSpan.FromHours(24));

    public string AveragePingLastHourDisplay
    {
        get
        {
            var average = CalculateAveragePing(TimeSpan.FromHours(1));
            return average.HasValue ? $"{average.Value:0} ms" : "-";
        }
    }

    public string MinPingLastHourDisplay
    {
        get
        {
            var minimum = CalculatePingWindow(TimeSpan.FromHours(1)).DefaultIfEmpty().Min();
            return minimum > 0 ? $"{minimum} ms" : "-";
        }
    }

    public string MaxPingLastHourDisplay
    {
        get
        {
            var maximum = CalculatePingWindow(TimeSpan.FromHours(1)).DefaultIfEmpty().Max();
            return maximum > 0 ? $"{maximum} ms" : "-";
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public static TargetViewModel CreateNew(string name, string description, string address)
    {
        return new TargetViewModel(Guid.NewGuid(), name, description, address, null, null, null, null);
    }

    public static TargetViewModel FromState(MonitoredTargetState state)
    {
        return new TargetViewModel(
            state.Id == Guid.Empty ? Guid.NewGuid() : state.Id,
            state.Name,
            state.Description,
            state.Address,
            state.ResolvedIp,
            state.LastCheckedUtc,
            state.LastOnlineUtc,
            state.Samples);
    }

    public MonitoredTargetState ToState()
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
            Samples = _samples.ToList()
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

        _samples.Add(new HealthSample
        {
            Timestamp = result.Timestamp,
            IsOnline = result.IsOnline,
            PingMs = result.PingMs,
            ResolvedIp = result.ResolvedIp
        });

        var cutoff = result.Timestamp.AddSeconds(-HistoryWindowSeconds);
        _samples.RemoveAll(sample => sample.Timestamp < cutoff);

        RaiseComputedProperties();
    }

    private void RaiseComputedProperties()
    {
        OnPropertyChanged(nameof(CurrentPingDisplay));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LastCheckDisplay));
        OnPropertyChanged(nameof(LastSeenDisplay));
        OnPropertyChanged(nameof(SamplesCount));
        OnPropertyChanged(nameof(RecentSamples));
        OnPropertyChanged(nameof(NoDataHint));
        OnPropertyChanged(nameof(MaxRecentPing));
        OnPropertyChanged(nameof(UptimeLastHourPercent));
        OnPropertyChanged(nameof(UptimeLast24HoursPercent));
        OnPropertyChanged(nameof(AveragePingLastHourDisplay));
        OnPropertyChanged(nameof(MinPingLastHourDisplay));
        OnPropertyChanged(nameof(MaxPingLastHourDisplay));
    }

    private double CalculateUptime(TimeSpan timeWindow)
    {
        var from = DateTimeOffset.UtcNow.Subtract(timeWindow);
        var snapshot = _samples.Where(sample => sample.Timestamp >= from).ToList();

        if (snapshot.Count == 0)
        {
            return 0;
        }

        var onlineCount = snapshot.Count(static sample => sample.IsOnline);
        return (onlineCount / (double)snapshot.Count) * 100;
    }

    private double? CalculateAveragePing(TimeSpan timeWindow)
    {
        var pings = CalculatePingWindow(timeWindow).ToList();

        if (pings.Count == 0)
        {
            return null;
        }

        return pings.Average();
    }

    private IEnumerable<long> CalculatePingWindow(TimeSpan timeWindow)
    {
        var from = DateTimeOffset.UtcNow.Subtract(timeWindow);

        return _samples
            .Where(sample => sample.Timestamp >= from && sample.PingMs.HasValue)
            .Select(sample => sample.PingMs!.Value);
    }
}
