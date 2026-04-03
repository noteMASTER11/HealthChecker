using System.Net;

namespace HealthChecker_WinUI.Services;

public static class AddressParser
{
    public static bool TryNormalize(string? rawInput, out string normalizedAddress, out string suggestedName)
    {
        normalizedAddress = string.Empty;
        suggestedName = string.Empty;

        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return false;
        }

        var trimmed = rawInput.Trim();

        if (IPAddress.TryParse(trimmed, out _))
        {
            normalizedAddress = trimmed;
            suggestedName = trimmed;
            return true;
        }

        if (TryExtractHost(trimmed, out var host))
        {
            normalizedAddress = host;
            suggestedName = host;
            return true;
        }

        return false;
    }

    private static bool TryExtractHost(string input, out string host)
    {
        host = string.Empty;

        if (Uri.TryCreate(input, UriKind.Absolute, out var absolute) && !string.IsNullOrWhiteSpace(absolute.Host))
        {
            host = absolute.Host;
            return true;
        }

        var withScheme = input.Contains("://", StringComparison.Ordinal) ? input : $"https://{input}";

        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var fallback) || string.IsNullOrWhiteSpace(fallback.Host))
        {
            return false;
        }

        host = fallback.Host;
        return true;
    }
}
