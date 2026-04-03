using Microsoft.UI.Xaml.Media;

namespace HealthChecker_WinUI.ViewModels;

public sealed class SampleBarViewModel
{
    public required double Height { get; init; }

    public required Brush Fill { get; init; }

    public required string ToolTip { get; init; }
}
