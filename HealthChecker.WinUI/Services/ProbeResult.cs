namespace HealthChecker_WinUI.Services;

public sealed class ProbeResult
{
    public required DateTimeOffset Timestamp { get; init; }

    public required bool IsOnline { get; init; }

    public required long? PingMs { get; init; }

    public required string? ResolvedIp { get; init; }

    public string? Error { get; init; }
}
