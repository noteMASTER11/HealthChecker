using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HealthChecker_WinUI.Services;

public sealed class NetworkProbeService
{
    private const int PingTimeoutMs = 900;

    public async Task<ProbeResult> ProbeAsync(string address, CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow;

        if (!AddressParser.TryNormalize(address, out var normalizedAddress, out _))
        {
            return new ProbeResult
            {
                Timestamp = timestamp,
                IsOnline = false,
                PingMs = null,
                ResolvedIp = null,
                Error = "Address format is invalid."
            };
        }

        try
        {
            var resolvedIp = await ResolveIpAsync(normalizedAddress, cancellationToken);
            var pingTarget = resolvedIp ?? normalizedAddress;

            using var ping = new Ping();
            var reply = await ping.SendPingAsync(pingTarget, PingTimeoutMs).WaitAsync(cancellationToken);

            return new ProbeResult
            {
                Timestamp = timestamp,
                IsOnline = reply.Status == IPStatus.Success,
                PingMs = reply.Status == IPStatus.Success ? reply.RoundtripTime : null,
                ResolvedIp = resolvedIp,
                Error = reply.Status == IPStatus.Success ? null : reply.Status.ToString()
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ProbeResult
            {
                Timestamp = timestamp,
                IsOnline = false,
                PingMs = null,
                ResolvedIp = null,
                Error = exception.Message
            };
        }
    }

    private static async Task<string?> ResolveIpAsync(string normalizedAddress, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(normalizedAddress, out var ip))
        {
            return ip.ToString();
        }

        var addresses = await Dns.GetHostAddressesAsync(normalizedAddress).WaitAsync(cancellationToken);
        var preferred = addresses.FirstOrDefault(static address => address.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault();

        return preferred?.ToString();
    }
}
