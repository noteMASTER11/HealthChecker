using System.Collections.ObjectModel;
using HealthChecker_WinUI.Infrastructure;
using HealthChecker_WinUI.Services;
using Microsoft.UI.Dispatching;

namespace HealthChecker_WinUI.ViewModels;

public sealed class TraceSessionViewModel : ObservableObject
{
    private const int DefaultMaxHops = 30;

    private readonly TracerouteMonitorService _service = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Dictionary<int, TraceHopViewModel> _hopLookup = [];

    private CancellationTokenSource? _traceCts;
    private Task? _traceTask;
    private bool _isRunning;
    private string _statusText = "Idle";

    public TraceSessionViewModel(string targetName, string address, DispatcherQueue dispatcherQueue)
    {
        TargetName = string.IsNullOrWhiteSpace(targetName) ? address : targetName;
        Address = address;
        _dispatcherQueue = dispatcherQueue;
        Hops = [];
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
            await RunOnUiAsync(() =>
            {
                IsRunning = false;
                StatusText = "Trace completed.";
            });
        }
        catch (OperationCanceledException)
        {
            await RunOnUiAsync(() =>
            {
                IsRunning = false;
                StatusText = "Trace stopped.";
            });
        }
        catch (Exception exception)
        {
            await RunOnUiAsync(() =>
            {
                IsRunning = false;
                StatusText = $"Trace failed: {exception.Message}";
            });
        }
    }

    private void HandleProbe(TraceProbeResult probe)
    {
        _ = RunOnUiAsync(() =>
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

    private Task RunOnUiAsync(Action action)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult();
                }
                catch (Exception exception)
                {
                    tcs.TrySetException(exception);
                }
            }))
        {
            tcs.TrySetException(new InvalidOperationException("Could not enqueue UI work."));
        }

        return tcs.Task;
    }
}
