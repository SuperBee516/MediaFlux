using System.Diagnostics;

namespace MediaFlux.Services;

/// <summary>Opt-in diagnostics for local latency investigations, including Release builds.</summary>
internal static class CriticalPathTiming
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("MEDIAFLUX_TIMING_LOG");
    private static readonly object Sync = new();

    internal static void WriteLine(string message)
    {
        Debug.WriteLine(message);
        if (string.IsNullOrWhiteSpace(LogPath)) return;
        try
        {
            lock (Sync) File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (Exception) { /* Diagnostics cannot change production behavior. */ }
    }
}
