namespace HealthChecker.ViewModels;

public sealed class HistoryBucketViewModel
{
    public required DateTimeOffset StartUtc { get; init; }

    public required DateTimeOffset EndUtc { get; init; }

    public required int Samples { get; init; }

    public required int OnlineSamples { get; init; }

    public long? AveragePingMs { get; init; }

    public double UptimePercent => Samples == 0 ? 0 : (OnlineSamples / (double)Samples) * 100;

    public bool? StatusByUptime => Samples == 0
        ? null
        : UptimePercent >= 60 ? true : false;

    public double UptimeBarHeight => 4 + (52 * (UptimePercent / 100.0));

    public string TimeRangeDisplay
    {
        get
        {
            var start = StartUtc.ToLocalTime();
            var end = EndUtc.ToLocalTime();
            return $"{start:dd.MM.yyyy HH:mm} - {end:dd.MM.yyyy HH:mm}";
        }
    }

    public string AveragePingDisplay => AveragePingMs.HasValue ? $"{AveragePingMs.Value} ms" : "n/a";
}
