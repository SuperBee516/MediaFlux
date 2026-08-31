using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MediaFlux.Services;

public enum FfmpegFilterAvailability { Available, Unavailable, Unknown }
public enum FfmpegFilterInventoryState { Available, Unknown }

public sealed record FfmpegRestorationCapabilities(string ExecutablePath, string Version, IReadOnlySet<string> Filters, FfmpegFilterInventoryState State, int ExitCode, int ParsedFilterCount)
{
    public FfmpegFilterAvailability GetAvailability(string filter) => State != FfmpegFilterInventoryState.Available ? FfmpegFilterAvailability.Unknown : Filters.Contains(filter) ? FfmpegFilterAvailability.Available : FfmpegFilterAvailability.Unavailable;
}

/// <summary>Reads both redirected FFmpeg streams through MediaToolProcessRunner and caches only credible inventories.</summary>
public sealed class FfmpegRestorationCapabilityService
{
    private const int MinimumCredibleFilterCount = 20;
    private static readonly ConcurrentDictionary<string, FfmpegRestorationCapabilities> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex FilterLine = new(@"^\s*\S+\s+(?<name>[A-Za-z0-9_]+)\s+\S*->\S*", RegexOptions.Compiled);
    private readonly IMediaToolProcessRunner _runner;
    private readonly Action<string>? _log;
    public FfmpegRestorationCapabilityService(IMediaToolProcessRunner? runner = null, Action<string>? log = null) { _runner = runner ?? new MediaToolProcessRunner(); _log = log; }

    public async Task<FfmpegRestorationCapabilities> GetAsync(string ffmpegPath, CancellationToken cancellationToken = default)
    {
        string identity = Identity(ffmpegPath);
        if (Cache.TryGetValue(identity, out FfmpegRestorationCapabilities? cached) && cached.State == FfmpegFilterInventoryState.Available && cached.ParsedFilterCount >= MinimumCredibleFilterCount) { Log(cached, "cache hit"); return cached; }
        if (cached != null) { Cache.TryRemove(identity, out _); _log?.Invoke($"[RestorationCapabilities] Invalid cached inventory invalidated: {ffmpegPath}."); }
        MediaToolProcessResult run;
        try { run = await _runner.RunAsync(new MediaToolProcessRequest { FileName = ffmpegPath, Arguments = new[] { "-hide_banner", "-filters" }, Timeout = TimeSpan.FromSeconds(15) }, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Unknown(ffmpegPath, -1, $"execution failed: {ex.Message}"); }
        HashSet<string> filters = ParseFilters(run.StandardOutput, run.StandardError);
        bool credible = run.ExitCode == 0 && !run.TimedOut && filters.Count >= MinimumCredibleFilterCount;
        if (!credible) return Unknown(ffmpegPath, run.ExitCode, $"cache miss; stdout={run.StandardOutput.Length} chars; stderr={run.StandardError.Length} chars; parsed={filters.Count}; timedOut={run.TimedOut}");
        var result = new FfmpegRestorationCapabilities(Path.GetFullPath(ffmpegPath), Version(ffmpegPath), filters, FfmpegFilterInventoryState.Available, run.ExitCode, filters.Count);
        Cache[identity] = result; Log(result, $"cache miss; stdout={run.StandardOutput.Length} chars; stderr={run.StandardError.Length} chars"); return result;
    }

    internal static HashSet<string> ParseFilters(string? stdout, string? stderr)
    {
        var filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in ((stdout ?? "") + "\n" + (stderr ?? "")).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            // Filter rows are "flags name input->output description". Do not depend on flag width or description whitespace.
            Match match = FilterLine.Match(line);
            if (match.Success && line.Contains("->", StringComparison.Ordinal)) filters.Add(match.Groups["name"].Value);
        }
        return filters;
    }

    internal static void ClearCacheForTesting() => Cache.Clear();
    private FfmpegRestorationCapabilities Unknown(string path, int exitCode, string detail)
    {
        var result = new FfmpegRestorationCapabilities(Path.GetFullPath(path), Version(path), new HashSet<string>(StringComparer.OrdinalIgnoreCase), FfmpegFilterInventoryState.Unknown, exitCode, 0);
        _log?.Invoke($"[RestorationCapabilities] {detail}; inventory is Unknown. Executable={result.ExecutablePath}; version={result.Version}; exit={exitCode}."); return result;
    }
    private void Log(FfmpegRestorationCapabilities result, string detail) => _log?.Invoke($"[RestorationCapabilities] {detail}; executable={result.ExecutablePath}; version={result.Version}; exit={result.ExitCode}; parsed={result.ParsedFilterCount}; hqdn3d={result.GetAvailability("hqdn3d")}; deblock={result.GetAvailability("deblock")}; deband={result.GetAvailability("deband")}; unsharp={result.GetAvailability("unsharp")}.");
    private static string Identity(string path) { var file = new FileInfo(path); return $"{Path.GetFullPath(path)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{Version(path)}"; }
    private static string Version(string path) { try { return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown"; } catch { return "unknown"; } }
}
