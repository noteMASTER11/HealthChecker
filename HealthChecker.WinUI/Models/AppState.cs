namespace HealthChecker_WinUI.Models;

public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;

    public bool StartWithWindows { get; set; }

    public List<MonitoredTargetState> Targets { get; set; } = [];
}
