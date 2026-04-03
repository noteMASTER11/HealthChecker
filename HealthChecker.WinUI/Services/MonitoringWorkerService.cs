namespace HealthChecker_WinUI.Services;

public sealed record MonitoringTarget(Guid Id, string Address);

public sealed class MonitoringWorkerService : IAsyncDisposable
{
    private readonly NetworkProbeService _probeService;
    private readonly TimeSpan _interval;
    private readonly int _maxParallelProbes;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);

    private Func<IReadOnlyList<MonitoringTarget>>? _targetsProvider;
    private Func<MonitoringTarget, ProbeResult, CancellationToken, Task>? _resultHandler;
    private Action<Exception>? _errorHandler;
    private CancellationTokenSource? _lifetimeCts;
    private Task? _loopTask;
    private bool _disposed;

    public MonitoringWorkerService(NetworkProbeService probeService, TimeSpan? interval = null, int? maxParallelProbes = null)
    {
        _probeService = probeService;
        _interval = interval ?? TimeSpan.FromSeconds(1);
        var defaultParallelism = Math.Clamp(Environment.ProcessorCount * 2, 8, 32);
        _maxParallelProbes = Math.Clamp(maxParallelProbes ?? defaultParallelism, 1, 128);
    }

    public bool IsPaused { get; set; }

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public Task StartAsync(
        Func<IReadOnlyList<MonitoringTarget>> targetsProvider,
        Func<MonitoringTarget, ProbeResult, CancellationToken, Task> resultHandler,
        Action<Exception> errorHandler,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _targetsProvider = targetsProvider;
        _resultHandler = resultHandler;
        _errorHandler = errorHandler;
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => RunLoopAsync(_lifetimeCts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task ProbeNowAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!IsRunning)
        {
            return;
        }

        await RunCycleCoreAsync(cancellationToken);
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        _lifetimeCts?.Cancel();

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _loopTask = null;
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (IsPaused)
            {
                continue;
            }

            try
            {
                await RunCycleCoreAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _errorHandler?.Invoke(exception);
            }
        }
    }

    private async Task RunCycleCoreAsync(CancellationToken cancellationToken)
    {
        if (!await _cycleGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var targetsProvider = _targetsProvider;
            var resultHandler = _resultHandler;

            if (targetsProvider is null || resultHandler is null)
            {
                return;
            }

            var snapshot = targetsProvider();
            if (snapshot.Count == 0)
            {
                return;
            }

            var options = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _maxParallelProbes
            };

            await Parallel.ForEachAsync(
                snapshot,
                options,
                async (target, token) => await ProbeTargetAsync(target, resultHandler, token));
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    private async Task ProbeTargetAsync(
        MonitoringTarget target,
        Func<MonitoringTarget, ProbeResult, CancellationToken, Task> resultHandler,
        CancellationToken cancellationToken)
    {
        ProbeResult result;

        try
        {
            result = await _probeService.ProbeAsync(target.Address, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            result = new ProbeResult
            {
                Timestamp = DateTimeOffset.UtcNow,
                IsOnline = false,
                PingMs = null,
                ResolvedIp = null,
                Error = exception.Message
            };
        }

        await resultHandler(target, result, cancellationToken);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync();
        _cycleGate.Dispose();
    }
}
