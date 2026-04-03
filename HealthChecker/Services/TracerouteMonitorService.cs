using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HealthChecker.Services;

public sealed class TracerouteMonitorService
{
    private static readonly byte[] Payload = Enumerable.Repeat((byte)32, 64).ToArray();

    private readonly ConcurrentDictionary<string, string> _hostnameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _maxHopLock = new();

    private int _dynamicMaxHops;

    public async Task RunAsync(
        string address,
        int maxHops,
        TimeSpan interval,
        Action<TraceProbeResult> onProbe,
        CancellationToken cancellationToken)
    {
        if (!AddressParser.TryNormalize(address, out var normalizedAddress, out _))
        {
            throw new InvalidOperationException("Address format is invalid for traceroute.");
        }

        var destinationAddress = await ResolveDestinationAddressAsync(normalizedAddress, cancellationToken);
        _dynamicMaxHops = maxHops;

        var tasks = new List<Task>(maxHops);

        for (var ttl = 1; ttl <= maxHops; ttl++)
        {
            var hopNumber = ttl;
            tasks.Add(RunHopLoopAsync(hopNumber, destinationAddress, interval, onProbe, cancellationToken));
        }

        await Task.WhenAll(tasks);
    }

    private async Task RunHopLoopAsync(
        int hopNumber,
        IPAddress destinationAddress,
        TimeSpan interval,
        Action<TraceProbeResult> onProbe,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var maxHopSnapshot = GetDynamicMaxHops();
            if (hopNumber > maxHopSnapshot)
            {
                break;
            }

            var stopwatch = Stopwatch.StartNew();
            var probe = await ProbeHopAsync(hopNumber, destinationAddress, cancellationToken);
            onProbe(probe);

            if (probe.IsDestinationReached)
            {
                TryReduceDynamicMaxHops(hopNumber);
            }

            var remaining = interval - stopwatch.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(remaining, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<TraceProbeResult> ProbeHopAsync(int hopNumber, IPAddress destinationAddress, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var options = new PingOptions(hopNumber, true);
            var reply = await ping
                .SendPingAsync(destinationAddress, 4_000, Payload, options)
                .WaitAsync(cancellationToken);

            var isUsefulReply = reply.Status is IPStatus.Success or IPStatus.TtlExpired;
            var isDestination = reply.Status == IPStatus.Success;

            if (isUsefulReply)
            {
                var address = reply.Address?.ToString();
                var hostname = await ResolveHostnameAsync(address, cancellationToken);

                return new TraceProbeResult
                {
                    HopNumber = hopNumber,
                    IsSuccessfulReply = true,
                    IsDestinationReached = isDestination,
                    StatusText = isDestination ? "Destination reached." : "TTL expired in transit.",
                    Address = address,
                    Hostname = hostname,
                    RoundTripTimeMs = reply.RoundtripTime,
                    Status = reply.Status
                };
            }

            return new TraceProbeResult
            {
                HopNumber = hopNumber,
                IsSuccessfulReply = false,
                IsDestinationReached = false,
                StatusText = MapStatus(reply.Status),
                Address = null,
                Hostname = null,
                RoundTripTimeMs = null,
                Status = reply.Status
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new TraceProbeResult
            {
                HopNumber = hopNumber,
                IsSuccessfulReply = false,
                IsDestinationReached = false,
                StatusText = exception.Message,
                Address = null,
                Hostname = null,
                RoundTripTimeMs = null,
                Status = null
            };
        }
    }

    private async Task<IPAddress> ResolveDestinationAddressAsync(string normalizedAddress, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(normalizedAddress, out var parsed))
        {
            return parsed;
        }

        var addresses = await Dns.GetHostAddressesAsync(normalizedAddress).WaitAsync(cancellationToken);
        var preferred = addresses.FirstOrDefault(static ip => ip.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault();

        return preferred ?? throw new InvalidOperationException("Could not resolve destination address.");
    }

    private async Task<string?> ResolveHostnameAsync(string? address, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        if (_hostnameCache.TryGetValue(address, out var cached))
        {
            return cached;
        }

        var resolved = address;

        try
        {
            using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dnsTimeout.CancelAfter(TimeSpan.FromMilliseconds(800));

            var entry = await Dns.GetHostEntryAsync(address).WaitAsync(dnsTimeout.Token);
            if (!string.IsNullOrWhiteSpace(entry.HostName))
            {
                resolved = entry.HostName;
            }
        }
        catch
        {
            resolved = address;
        }

        _hostnameCache[address] = resolved;
        return resolved;
    }

    private int GetDynamicMaxHops()
    {
        lock (_maxHopLock)
        {
            return _dynamicMaxHops;
        }
    }

    private void TryReduceDynamicMaxHops(int hopNumber)
    {
        lock (_maxHopLock)
        {
            if (hopNumber < _dynamicMaxHops)
            {
                _dynamicMaxHops = hopNumber;
            }
        }
    }

    private static string MapStatus(IPStatus status)
    {
        return status switch
        {
            IPStatus.TimedOut => "Request timed out.",
            IPStatus.DestinationHostUnreachable => "Destination host unreachable.",
            IPStatus.DestinationNetworkUnreachable => "Destination network unreachable.",
            IPStatus.DestinationProhibited => "Destination prohibited.",
            IPStatus.PacketTooBig => "Packet was too big.",
            IPStatus.BadRoute => "Bad route.",
            IPStatus.BadDestination => "Bad destination.",
            _ => status.ToString()
        };
    }
}
