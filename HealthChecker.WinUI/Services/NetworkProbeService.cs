using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HealthChecker_WinUI.Services;

public sealed class NetworkProbeService
{
    private const int PingTimeoutMs = 1_200;
    private const int RetryDelayMs = 120;
    private const int MaxAttempts = 2;

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
            PingReply? lastReply = null;
            string? lastError = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using var ping = new Ping();
                    lastReply = await ping.SendPingAsync(pingTarget, PingTimeoutMs).WaitAsync(cancellationToken);

                    if (lastReply.Status == IPStatus.Success)
                    {
                        return new ProbeResult
                        {
                            Timestamp = timestamp,
                            IsOnline = true,
                            PingMs = lastReply.RoundtripTime,
                            ResolvedIp = resolvedIp,
                            Error = null
                        };
                    }

                    lastError = lastReply.Status.ToString();
                    if (attempt < MaxAttempts && lastReply.Status == IPStatus.TimedOut)
                    {
                        await Task.Delay(RetryDelayMs, cancellationToken);
                        continue;
                    }

                    break;
                }
                catch (PingException exception) when (attempt < MaxAttempts)
                {
                    lastError = exception.Message;
                    await Task.Delay(RetryDelayMs, cancellationToken);
                }
            }

            return new ProbeResult
            {
                Timestamp = timestamp,
                IsOnline = false,
                PingMs = null,
                ResolvedIp = resolvedIp,
                Error = lastError ?? lastReply?.Status.ToString() ?? "Unknown probe failure."
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
