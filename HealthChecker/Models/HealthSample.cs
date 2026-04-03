namespace HealthChecker.Models;

public sealed class HealthSample
{
    public DateTimeOffset Timestamp { get; set; }

    public bool IsOnline { get; set; }

    public long? PingMs { get; set; }

    public string? ResolvedIp { get; set; }
}
