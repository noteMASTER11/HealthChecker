using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using HealthChecker.Services;

namespace HealthChecker.ViewModels;

public sealed class TraceSessionViewModel : ObservableObject
{
    private const int DefaultMaxHops = 30;

    private readonly TracerouteMonitorService _service = new();
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, TraceHopViewModel> _hopLookup = [];

    private CancellationTokenSource? _traceCts;
    private Task? _traceTask;
    private bool _isRunning;
    private string _statusText = "Idle";

    public TraceSessionViewModel(string targetName, string address)
    {
        TargetName = string.IsNullOrWhiteSpace(targetName) ? address : targetName;
        Address = address;
        Hops = [];
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public string TargetName { get; }

    public string Address { get; }

    public ObservableCollection<TraceHopViewModel> Hops { get; }

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public Task StartAsync()
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _traceCts = new CancellationTokenSource();
        IsRunning = true;
        StatusText = "Tracing route...";

        _traceTask = RunTraceAsync(_traceCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_traceCts is null)
        {
            return;
        }

        _traceCts.Cancel();

        if (_traceTask is not null)
        {
            try
            {
                await _traceTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _traceCts.Dispose();
        _traceCts = null;
        _traceTask = null;
    }

    private async Task RunTraceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _service.RunAsync(Address, DefaultMaxHops, TimeSpan.FromSeconds(1), HandleProbe, cancellationToken);

            await _dispatcher.InvokeAsync(() =>
            {
                IsRunning = false;
                StatusText = "Trace completed.";
            });
        }
        catch (OperationCanceledException)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                IsRunning = false;
                StatusText = "Trace stopped.";
            });
        }
        catch (Exception exception)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                IsRunning = false;
                StatusText = $"Trace failed: {exception.Message}";
            });
        }
    }

    private void HandleProbe(TraceProbeResult probe)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            if (!_hopLookup.TryGetValue(probe.HopNumber, out var hop))
            {
                hop = new TraceHopViewModel(probe.HopNumber);
                InsertHopInOrder(hop);
                _hopLookup[probe.HopNumber] = hop;
            }

            hop.RegisterProbe(probe);
        });
    }

    private void InsertHopInOrder(TraceHopViewModel hop)
    {
        var insertAt = Hops.Count;
        for (var index = 0; index < Hops.Count; index++)
        {
            if (Hops[index].HopNumber > hop.HopNumber)
            {
                insertAt = index;
                break;
            }
        }

        Hops.Insert(insertAt, hop);
    }
}
