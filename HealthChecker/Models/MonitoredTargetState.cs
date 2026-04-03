namespace HealthChecker.Models;

public sealed class MonitoredTargetState
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public string? ResolvedIp { get; set; }

    public DateTimeOffset? LastCheckedUtc { get; set; }

    public DateTimeOffset? LastOnlineUtc { get; set; }

    public List<HealthSample> Samples { get; set; } = [];
}
