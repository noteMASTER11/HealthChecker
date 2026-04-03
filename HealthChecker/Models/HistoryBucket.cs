namespace HealthChecker.Models;

public sealed class HistoryBucket
{
    public DateTimeOffset StartUtc { get; set; }

    public DateTimeOffset EndUtc { get; set; }

    public int Samples { get; set; }

    public int OnlineSamples { get; set; }

    public long? AveragePingMs { get; set; }
}
