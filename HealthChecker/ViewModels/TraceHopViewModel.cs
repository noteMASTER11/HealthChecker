using HealthChecker.Services;

namespace HealthChecker.ViewModels;

public sealed class TraceHopViewModel : ObservableObject
{
    private string _hostname = "No response from host";
    private int _sent;
    private int _received;
    private int _best;
    private int _worst;
    private int _last;
    private long _total;

    public TraceHopViewModel(int hopNumber)
    {
        HopNumber = hopNumber;
    }

    public int HopNumber { get; }

    public string Hostname
    {
        get => _hostname;
        private set => SetProperty(ref _hostname, value);
    }

    public int Sent
    {
        get => _sent;
        private set => SetProperty(ref _sent, value);
    }

    public int Received
    {
        get => _received;
        private set => SetProperty(ref _received, value);
    }

    public int LossPercent => Sent == 0 ? 0 : 100 - (100 * Received / Sent);

    public string BestDisplay => Received == 0 ? "-" : _best.ToString();

    public string AvgDisplay => Received == 0 ? "-" : ((int)(_total / Received)).ToString();

    public string WorstDisplay => Received == 0 ? "-" : _worst.ToString();

    public string LastDisplay => Received == 0 ? "-" : _last.ToString();

    public void RegisterProbe(TraceProbeResult probe)
    {
        Sent = Sent + 1;

        if (probe.IsSuccessfulReply)
        {
            Received = Received + 1;

            if (probe.RoundTripTimeMs.HasValue)
            {
                var current = (int)probe.RoundTripTimeMs.Value;
                _last = current;
                _total += current;

                if (Received == 1 || current < _best)
                {
                    _best = current;
                }

                if (current > _worst)
                {
                    _worst = current;
                }
            }

            if (!string.IsNullOrWhiteSpace(probe.Hostname))
            {
                Hostname = probe.Hostname;
            }
            else if (!string.IsNullOrWhiteSpace(probe.Address))
            {
                Hostname = probe.Address;
            }
        }
        else
        {
            if (Received == 0)
            {
                Hostname = probe.StatusText;
            }
        }

        OnPropertyChanged(nameof(LossPercent));
        OnPropertyChanged(nameof(BestDisplay));
        OnPropertyChanged(nameof(AvgDisplay));
        OnPropertyChanged(nameof(WorstDisplay));
        OnPropertyChanged(nameof(LastDisplay));
    }
}
