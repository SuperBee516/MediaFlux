using System.Collections.Concurrent;
using System.Diagnostics;

namespace MediaFlux.Services;

public sealed record FfmpegRestorationCapabilities(string ExecutablePath, string Version, IReadOnlySet<string> Filters)
{
    public bool Supports(string filter) => Filters.Contains(filter);
}

/// <summary>Caches the active FFmpeg filter inventory by executable identity.</summary>
public sealed class FfmpegRestorationCapabilityService
{
    private static readonly ConcurrentDictionary<string, FfmpegRestorationCapabilities> Cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMediaToolProcessRunner _runner;
    public FfmpegRestorationCapabilityService(IMediaToolProcessRunner? runner = null) => _runner = runner ?? new MediaToolProcessRunner();
    public async Task<FfmpegRestorationCapabilities> GetAsync(string ffmpegPath, CancellationToken cancellationToken = default)
    {
        string key = ffmpegPath + "|" + File.GetLastWriteTimeUtc(ffmpegPath).Ticks; if (Cache.TryGetValue(key, out var cached)) return cached;
        MediaToolProcessResult run = await _runner.RunAsync(new MediaToolProcessRequest { FileName = ffmpegPath, Arguments = new[] { "-hide_banner", "-filters" }, Timeout = TimeSpan.FromSeconds(15) }, cancellationToken).ConfigureAwait(false);
        var filters = (run.StandardOutput + "\n" + run.StandardError).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(parts => parts.Length >= 2 && parts[0].StartsWith("-", StringComparison.Ordinal)).Select(parts => parts[^1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Cache[key] = new FfmpegRestorationCapabilities(ffmpegPath, FileVersionInfo.GetVersionInfo(ffmpegPath).FileVersion ?? "unknown", filters);
    }
}
