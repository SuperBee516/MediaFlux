using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MediaFlux.Services;

public enum FfmpegNvencRuntimeState
{
    Available,
    Unavailable,
    DriverIncompatible,
    EncoderMissing,
    TimedOut,
    ProbeFailed,
    Unknown
}

public sealed record FfmpegNvencRuntimeCapability(
    FfmpegNvencRuntimeState State,
    string Encoder,
    string FfmpegPath,
    string FfmpegVersion,
    int? ExitCode,
    string Diagnostic,
    string RawDiagnostic,
    string? RequiredApiVersion = null,
    string? DetectedApiVersion = null,
    string? MinimumDriverVersion = null)
{
    public bool IsAvailable => State == FfmpegNvencRuntimeState.Available;
}

public interface IFfmpegNvencProbeRunner
{
    Task<(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut)> RunAsync(
        string ffmpegPath, string encoder, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class FfmpegNvencRuntimeCapabilityService
{
    private static readonly Regex ApiMismatch = new(
        @"required\s*:\s*(?<required>\d+(?:\.\d+)*)\s+found\s*:\s*(?<found>\d+(?:\.\d+)*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MinimumDriver = new(
        @"minimum required nvidia driver(?: for nvenc)? is\s*(?<version>\d+(?:\.\d+)*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex VersionLine = new(
        @"^ffmpeg version\s+([^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, Lazy<Task<FfmpegNvencRuntimeCapability>>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IFfmpegNvencProbeRunner _runner;

    public static FfmpegNvencRuntimeCapabilityService Shared { get; } = new(new ProcessProbeRunner(), LogCapability);
    private readonly Action<FfmpegNvencRuntimeCapability>? _log;

    public FfmpegNvencRuntimeCapabilityService(IFfmpegNvencProbeRunner runner, Action<FfmpegNvencRuntimeCapability>? log = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _log = log;
    }

    public Task<FfmpegNvencRuntimeCapability> CheckAsync(string ffmpegPath, string encoder, CancellationToken cancellationToken = default)
    {
        if (encoder is not ("h264_nvenc" or "hevc_nvenc"))
            throw new ArgumentException("Only NVENC encoders supported by MediaFlux can be probed.", nameof(encoder));
        string key = CacheKey(ffmpegPath, encoder);
        Lazy<Task<FfmpegNvencRuntimeCapability>> lazy = _cache.GetOrAdd(key, _ =>
            new Lazy<Task<FfmpegNvencRuntimeCapability>>(() => ProbeAsync(ffmpegPath, encoder, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
        return AwaitAndEvictCanceledAsync(key, lazy);
    }

    public void ClearCache() => _cache.Clear();

    public static FfmpegNvencRuntimeCapability Classify(
        string ffmpegPath, string encoder, int? exitCode, string stdout, string stderr, bool timedOut = false)
    {
        string raw = (stderr + Environment.NewLine + stdout).Trim();
        string version = VersionLine.Match(raw) is { Success: true } match ? match.Groups[1].Value : "Unknown";
        string required = "", found = "", minimumDriver = "";
        Match api = ApiMismatch.Match(raw);
        if (api.Success) { required = api.Groups["required"].Value; found = api.Groups["found"].Value; }
        Match driver = MinimumDriver.Match(raw);
        if (driver.Success) minimumDriver = driver.Groups["version"].Value;

        FfmpegNvencRuntimeState state;
        string diagnostic;
        if (timedOut) { state = FfmpegNvencRuntimeState.TimedOut; diagnostic = $"The {encoder} initialization probe timed out."; }
        else if (raw.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase))
        { state = FfmpegNvencRuntimeState.EncoderMissing; diagnostic = $"This FFmpeg build does not provide {encoder}."; }
        else if (api.Success || raw.Contains("minimum required Nvidia driver", StringComparison.OrdinalIgnoreCase))
        {
            state = FfmpegNvencRuntimeState.DriverIncompatible;
            diagnostic = api.Success
                ? $"NVENC unavailable — FFmpeg requires NVENC API {required}, but the installed NVIDIA driver provides {found}. Update the NVIDIA driver to use GPU encoding."
                : minimumDriver.Length > 0
                    ? $"NVENC unavailable — this FFmpeg build requires NVIDIA driver {minimumDriver} or newer."
                    : "NVENC unavailable — the installed NVIDIA driver is incompatible with this FFmpeg build.";
            if (minimumDriver.Length > 0 && api.Success)
                diagnostic = $"NVENC unavailable — FFmpeg requires NVENC API {required}, but the installed NVIDIA driver provides {found}; this build requires NVIDIA driver {minimumDriver} or newer.";
        }
        else if (exitCode == 0) { state = FfmpegNvencRuntimeState.Available; diagnostic = $"{encoder} initialized successfully."; }
        else if (exitCode.HasValue)
        {
            state = FfmpegNvencRuntimeState.Unavailable;
            diagnostic = "NVENC could not initialize with the current FFmpeg and GPU runtime.";
        }
        else { state = FfmpegNvencRuntimeState.ProbeFailed; diagnostic = "The NVENC initialization probe could not be completed."; }

        return new(state, encoder, ffmpegPath, version, exitCode, diagnostic, raw, required.Length == 0 ? null : required,
            found.Length == 0 ? null : found, minimumDriver.Length == 0 ? null : minimumDriver);
    }

    private async Task<FfmpegNvencRuntimeCapability> ProbeAsync(string path, string encoder, CancellationToken token)
    {
        try
        {
            var result = await _runner.RunAsync(path, encoder, TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
            return Publish(Classify(path, encoder, result.ExitCode, result.StandardOutput, result.StandardError, result.TimedOut));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Publish(new(FfmpegNvencRuntimeState.ProbeFailed, encoder, path, "Unknown", null,
                "The NVENC initialization probe failed. See diagnostics for details.", ex.ToString()));
        }
    }

    private FfmpegNvencRuntimeCapability Publish(FfmpegNvencRuntimeCapability capability)
    {
        try { _log?.Invoke(capability); } catch { }
        return capability;
    }

    private static void LogCapability(FfmpegNvencRuntimeCapability capability) =>
        ErrorLogService.Append(AppPaths.InstallDirectory, "NVENC runtime capability",
            details: $"FFmpeg: {capability.FfmpegPath}{Environment.NewLine}Version: {capability.FfmpegVersion}{Environment.NewLine}Encoder: {capability.Encoder}{Environment.NewLine}Result: {capability.State}{Environment.NewLine}Exit code: {capability.ExitCode?.ToString() ?? "n/a"}{Environment.NewLine}Required NVENC API: {capability.RequiredApiVersion ?? "not reported"}{Environment.NewLine}Detected NVENC API: {capability.DetectedApiVersion ?? "not reported"}{Environment.NewLine}Minimum driver: {capability.MinimumDriverVersion ?? "not reported"}{Environment.NewLine}{capability.Diagnostic}{Environment.NewLine}{capability.RawDiagnostic}");

    private async Task<FfmpegNvencRuntimeCapability> AwaitAndEvictCanceledAsync(string key, Lazy<Task<FfmpegNvencRuntimeCapability>> lazy)
    {
        try { return await lazy.Value.ConfigureAwait(false); }
        catch (OperationCanceledException) { _cache.TryRemove(key, out _); throw; }
    }

    private static string CacheKey(string path, string encoder)
    {
        string full = Path.GetFullPath(path);
        try { var file = new FileInfo(full); return $"{full}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{encoder}"; }
        catch { return $"{full}|{encoder}"; }
    }

    private sealed class ProcessProbeRunner : IFfmpegNvencProbeRunner
    {
        public async Task<(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut)> RunAsync(
            string ffmpegPath, string encoder, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var process = new Process { StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }};
            foreach (string arg in new[] { "-nostdin", "-f", "lavfi", "-i", "color=c=black:s=64x64:r=1", "-frames:v", "1", "-an", "-c:v", encoder, "-f", "null", "-" })
                process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                if (cancellationToken.IsCancellationRequested) throw;
                return (null, SafeResult(stdout), SafeResult(stderr), true);
            }
        }

        private static string SafeResult(Task<string> task) => task.IsCompletedSuccessfully ? task.Result : string.Empty;
    }
}
