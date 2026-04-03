using System.Net.NetworkInformation;

namespace HealthChecker.Services;

public sealed class TraceProbeResult
{
    public required int HopNumber { get; init; }

    public required bool IsSuccessfulReply { get; init; }

    public required bool IsDestinationReached { get; init; }

    public required string StatusText { get; init; }

    public string? Address { get; init; }

    public string? Hostname { get; init; }

    public long? RoundTripTimeMs { get; init; }

    public IPStatus? Status { get; init; }
}
