using System.Text.Json;
using HealthChecker_WinUI.Models;

namespace HealthChecker_WinUI.Services;

public sealed class AppStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _dataDirectory;
    private readonly string _stateFilePath;

    public AppStateStore()
    {
        _dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HealthChecker");

        _stateFilePath = Path.Combine(_dataDirectory, "state.json");
    }

    public async Task<AppState> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return new AppState();
            }

            await using var stream = File.OpenRead(_stateFilePath);
            var state = await JsonSerializer.DeserializeAsync<AppState>(stream, SerializerOptions, cancellationToken);
            return state ?? new AppState();
        }
        catch
        {
            return new AppState();
        }
    }
}
