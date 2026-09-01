using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed record VideoRestorationPreviewRequest(string SourcePath, TimeSpan SourceDuration, TimeSpan Position, VideoRestorationSettings Settings, EncodingService.ScaleMode EncodeScale = EncodingService.ScaleMode.None);

public sealed class VideoRestorationStillPreview : IDisposable
{
    internal VideoRestorationStillPreview(Image original, Image restored, TimeSpan position, string filterChain, bool resolutionChanged) { Original = original; Restored = restored; Position = position; FilterChain = filterChain; ResolutionChanged = resolutionChanged; }
    public Image Original { get; }
    public Image Restored { get; }
    public TimeSpan Position { get; }
    public string FilterChain { get; }
    public bool ResolutionChanged { get; }
    public void Dispose() { Original.Dispose(); Restored.Dispose(); }
}

public sealed record VideoRestorationMotionPreview(TimeSpan Start, TimeSpan Duration, string OriginalPath, string RestoredPath, string ComparisonPath, TemporalQualityResult? TemporalQuality = null);

/// <summary>Creates accurate, validated preview artifacts. Cache files are promoted only after FFmpeg and validation succeed.</summary>
public sealed class VideoRestorationPreviewService
{
    private const int MaxCacheFiles = 80;
    private const long MinimumMotionBytes = 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly IMediaToolProcessRunner _runner;
    private readonly FfmpegRestorationCapabilityService _capabilities;
    private readonly AiRestorationBackendService _aiBackend;
    private readonly string _cacheDirectory;
    private readonly Action<string>? _log;

    public VideoRestorationPreviewService(string applicationDirectory, string? configuredFfmpegPath = null, string? configuredFfprobePath = null, Action<string>? log = null)
        : this(FfmpegToolResolver.Resolve(applicationDirectory, configuredFfmpegPath, configuredFfprobePath), new MediaToolProcessRunner(), null, Path.Combine(AppPaths.DataDirectory, "restoration-previews"), log) { }

    private VideoRestorationPreviewService(FfmpegToolPaths tools, IMediaToolProcessRunner runner, FfmpegRestorationCapabilityService? capabilities, string cacheDirectory, Action<string>? log)
        : this(tools.FfmpegPath, tools.FfprobePath, runner, capabilities ?? new FfmpegRestorationCapabilityService(runner, log), cacheDirectory, log) { }

    internal VideoRestorationPreviewService(string ffmpegPath, string ffprobePath, IMediaToolProcessRunner runner, FfmpegRestorationCapabilityService capabilities, string cacheDirectory, Action<string>? log = null)
    { _ffmpegPath = ffmpegPath; _ffprobePath = ffprobePath; _runner = runner; _capabilities = capabilities; _cacheDirectory = cacheDirectory; _log = log; _aiBackend = new AiRestorationBackendService(AppDomain.CurrentDomain.BaseDirectory, runner, log); }

    public static IReadOnlyList<TimeSpan> BuildRepresentativePositions(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return Array.Empty<TimeSpan>();
        double edge = Math.Min(20, Math.Max(1, duration.TotalSeconds * .08)), end = Math.Max(edge, duration.TotalSeconds - edge);
        return new[] { edge, edge + ((end - edge) * .25), edge + ((end - edge) * .5), edge + ((end - edge) * .75), end }.Select(TimeSpan.FromSeconds).Distinct().ToArray();
    }

    public async Task<VideoRestorationStillPreview> GenerateStillAsync(VideoRestorationPreviewRequest request, CancellationToken cancellationToken = default)
    {
        request = request with { Settings = VideoRestorationModeResolver.Resolve(request.Settings) };
        ValidateRequest(request); await EnsureAiSourceTimingAsync(request.SourcePath, request.Settings, cancellationToken).ConfigureAwait(false); string filterChain = await PreparePipelineAsync(request.Settings, request.EncodeScale, cancellationToken).ConfigureAwait(false);
        VideoRestorationPipelinePlan plan = VideoRestorationPipeline.BuildPlan(request.Settings, request.EncodeScale);
        string cacheSettings = filterChain;
        if (plan.UsesAi)
        {
            AiRestorationCapabilities ai = await _aiBackend.GetCapabilitiesAsync(request.Settings, cancellationToken).ConfigureAwait(false);
            cacheSettings = $"{plan.DescribeStages()}|{ai.Identity}|{request.Settings.AiModelId}|{request.Settings.AiScale}|{request.Settings.AiDevice}";
        }
        PrepareCache(); TimeSpan position = ClampPosition(request.Position, request.SourceDuration);
        string original = Path.Combine(_cacheDirectory, "original-" + BuildCacheKey(request, string.Empty) + ".png");
        string restored = Path.Combine(_cacheDirectory, "restored-" + BuildCacheKey(request, cacheSettings) + ".png");
        await EnsureStillAsync(original, BuildStillArguments(request.SourcePath, position, string.Empty, "{output}"), cancellationToken).ConfigureAwait(false);
        if (!plan.UsesAi) await EnsureStillAsync(restored, BuildStillArguments(request.SourcePath, position, filterChain, "{output}"), cancellationToken).ConfigureAwait(false);
        else await EnsureAiStillAsync(restored, request, position, plan, cancellationToken).ConfigureAwait(false);
        _log?.Invoke($"[RestorationPreview] still ready; timestamp={position.TotalSeconds:0.###}; filters={(string.IsNullOrWhiteSpace(filterChain) ? "Off" : filterChain)}.");
        return new VideoRestorationStillPreview(LoadBitmap(original), LoadBitmap(restored), position, plan.UsesAi ? plan.DescribeStages() : filterChain, request.Settings.Resize != VideoRestorationResize.Original || request.Settings.AiScale != AiRestorationScale.X1);
    }

    public async Task<VideoRestorationMotionPreview> GenerateMotionAsync(VideoRestorationPreviewRequest request, TimeSpan? requestedDuration = null, CancellationToken cancellationToken = default)
    {
        request = request with { Settings = VideoRestorationModeResolver.Resolve(request.Settings) };
        ValidateRequest(request); await EnsureAiSourceTimingAsync(request.SourcePath, request.Settings, cancellationToken).ConfigureAwait(false); string filterChain = await PreparePipelineAsync(request.Settings, request.EncodeScale, cancellationToken).ConfigureAwait(false);
        PrepareCache(); TimeSpan duration = TimeSpan.FromSeconds(Math.Min(Math.Max(1, (requestedDuration ?? TimeSpan.FromSeconds(5)).TotalSeconds), Math.Max(1, request.SourceDuration.TotalSeconds)));
        TimeSpan start = TimeSpan.FromSeconds(Math.Clamp(request.Position.TotalSeconds - duration.TotalSeconds / 2, 0, Math.Max(0, request.SourceDuration.TotalSeconds - duration.TotalSeconds)));
        string aiIdentity = request.Settings.AiMode == AiRestorationMode.Off ? "" : (await _aiBackend.GetCapabilitiesAsync(request.Settings, cancellationToken).ConfigureAwait(false)).Identity + "|" + request.Settings.AiModelId + "|" + request.Settings.AiScale;
        string key = BuildCacheKey(request with { Position = start }, filterChain + "|" + aiIdentity + "|clip|" + duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        string original = Path.Combine(_cacheDirectory, "motion-original-" + key + ".mp4"), restored = Path.Combine(_cacheDirectory, "motion-restored-" + key + ".mp4"), comparison = Path.Combine(_cacheDirectory, "motion-comparison-" + key + ".mp4");
        await EnsureMotionAsync(original, output => BuildAccurateFrameArguments(request.SourcePath, start, duration, string.Empty, output, image: false), cancellationToken).ConfigureAwait(false);
        if (request.Settings.AiMode == AiRestorationMode.Off) await EnsureMotionAsync(restored, output => BuildAccurateFrameArguments(request.SourcePath, start, duration, filterChain, output, image: false), cancellationToken).ConfigureAwait(false);
        else await EnsureAiMotionAsync(restored, request, start, duration, cancellationToken).ConfigureAwait(false);
        await EnsureMotionAsync(comparison, output => BuildComparisonArguments(original, restored, output), cancellationToken).ConfigureAwait(false);
        TemporalQualityResult? temporal = request.Settings.AiMode == AiRestorationMode.Off ? null : await new TemporalQualityAnalysisService(_ffmpegPath, _runner, _log).AnalyzeMotionAsync(original, restored, cancellationToken).ConfigureAwait(false);
        if (temporal != null) _log?.Invoke($"[RestorationPreview] temporal sample={start.TotalSeconds:0.###}-{(start + duration).TotalSeconds:0.###}; {temporal.Summary}; {temporal.Reason}");
        _log?.Invoke($"[RestorationPreview] motion ready; start={start.TotalSeconds:0.###}; duration={duration.TotalSeconds:0.###}; filters={(string.IsNullOrWhiteSpace(filterChain) ? "Off" : filterChain)}.");
        return new VideoRestorationMotionPreview(start, duration, original, restored, comparison, temporal);
    }

    private async Task EnsureAiMotionAsync(string final, VideoRestorationPreviewRequest request, TimeSpan start, TimeSpan duration, CancellationToken token)
    {
        await WithCacheLockAsync(final, async () =>
        {
            if (await IsValidMotionAsync(final, token).ConfigureAwait(false)) return;
            string staging = StagingPath(final); AiIntermediateVideoResult? intermediate = null;
            try
            {
                var probe = await new FfprobeService(_ffprobePath, _runner).ProbeAsync(request.SourcePath, token).ConfigureAwait(false);
                double? fps = probe.Streams.FirstOrDefault(stream => stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase))?.FrameRate;
                if (!probe.Success || fps is not > 0) throw new AiRestorationValidationException("AI motion preview requires a source with a known constant frame rate.");
                VideoRestorationPipelinePlan plan = VideoRestorationPipeline.BuildPlan(request.Settings, request.EncodeScale);
                var service = new AiRestorationIntermediateVideoService(_ffmpegPath, _ffprobePath, _cacheDirectory, _aiBackend, _runner, _log);
                intermediate = await service.CreateAsync(new AiIntermediateVideoRequest(request.SourcePath, fps.Value, request.SourceDuration, request.Settings, plan, start, duration), null, token).ConfigureAwait(false);
                string post = BuildPostAiPreviewFilterChain(plan.PostAiFilterChain, request.EncodeScale);
                await RunFfmpegAsync(BuildAccurateFrameArguments(intermediate.Path, TimeSpan.Zero, duration, post, staging, image: false), "generating AI motion preview", token).ConfigureAwait(false);
                if (!await IsValidMotionAsync(staging, token).ConfigureAwait(false)) throw new InvalidOperationException("AI restoration did not produce a valid MP4 preview.");
                Promote(staging, final);
            }
            catch { Invalidate(staging); Invalidate(final); throw; }
            finally { intermediate?.Dispose(); }
        }, token).ConfigureAwait(false);
    }

    internal static string BuildCacheKey(VideoRestorationPreviewRequest request, string effectiveChain)
    {
        var file = new FileInfo(request.SourcePath); string input = $"{Path.GetFullPath(request.SourcePath)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{request.Position.TotalMilliseconds:0}|{request.EncodeScale}|{effectiveChain}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).Substring(0, 24);
    }
    internal static IReadOnlyList<string> BuildStillArguments(string source, TimeSpan position, string filterChain, string output) => BuildAccurateFrameArguments(source, position, TimeSpan.Zero, filterChain, output, image: true);

    private async Task EnsureStillAsync(string final, IReadOnlyList<string> template, CancellationToken token)
    {
        await WithCacheLockAsync(final, async () =>
        {
            if (IsValidStill(final)) { _log?.Invoke($"[RestorationPreview] Using cached frame: {Path.GetFileName(final)}."); return; }
            Invalidate(final); string staging = StagingPath(final);
            try { await RunFfmpegAsync(ReplaceOutput(template, staging), "generating preview frame", token).ConfigureAwait(false); if (!IsValidStill(staging)) throw new InvalidOperationException("FFmpeg produced an unreadable preview image."); Promote(staging, final); }
            catch { Invalidate(staging); Invalidate(final); throw; }
        }, token).ConfigureAwait(false);
    }

    private async Task EnsureMotionAsync(string final, Func<string, IReadOnlyList<string>> buildArguments, CancellationToken token)
    {
        await WithCacheLockAsync(final, async () =>
        {
            if (await IsValidMotionAsync(final, token).ConfigureAwait(false)) { _log?.Invoke($"[RestorationPreview] Using cached motion preview: {Path.GetFileName(final)}."); return; }
            Invalidate(final); string staging = StagingPath(final);
            try { await RunFfmpegAsync(buildArguments(staging), "generating 5-second preview", token).ConfigureAwait(false); if (!await IsValidMotionAsync(staging, token).ConfigureAwait(false)) throw new InvalidOperationException("FFmpeg did not produce a valid MP4 preview."); Promote(staging, final); }
            catch { Invalidate(staging); Invalidate(final); throw; }
        }, token).ConfigureAwait(false);
    }

    private async Task<bool> IsValidMotionAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MinimumMotionBytes) { Invalidate(path); return false; }
        try
        {
            MediaToolProcessResult result = await _runner.RunAsync(new MediaToolProcessRequest { FileName = _ffprobePath, Arguments = new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_type:format=duration", "-of", "default=noprint_wrappers=1", path }, Timeout = TimeSpan.FromSeconds(15) }, token).ConfigureAwait(false);
            string? durationLine = result.StandardOutput.Split('\n').FirstOrDefault(line => line.StartsWith("duration=", StringComparison.OrdinalIgnoreCase));
            bool valid = result.ExitCode == 0 && !result.TimedOut && result.StandardOutput.Contains("codec_type=video", StringComparison.OrdinalIgnoreCase) && durationLine != null && double.TryParse(durationLine.Split('=')[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double duration) && duration > 0;
            if (!valid) { _log?.Invoke($"[RestorationPreview] Invalid cached MP4 removed: {Path.GetFileName(path)}."); Invalidate(path); }
            return valid;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log?.Invoke($"[RestorationPreview] MP4 validation failed for {Path.GetFileName(path)}: {ex.Message}"); Invalidate(path); return false; }
    }

    private async Task<string> PreparePipelineAsync(VideoRestorationSettings settings, EncodingService.ScaleMode scaleMode, CancellationToken token)
    {
        VideoRestorationPipeline.Validate(settings, scaleMode); if (settings.Preset == VideoRestorationPreset.Off && settings.AiMode == AiRestorationMode.Off) return BuildEffectivePreviewFilterChain(settings, scaleMode);
        FfmpegRestorationCapabilities inventory = await _capabilities.GetAsync(_ffmpegPath, token).ConfigureAwait(false);
        if (inventory.State == FfmpegFilterInventoryState.Available) { VideoRestorationPipeline.SetAvailableFilters(inventory.Filters); VideoRestorationPipeline.ValidateAvailable(settings); }
        else { VideoRestorationPipeline.ClearAvailableFilters(); _log?.Invoke("[RestorationPreview] FFmpeg restoration inventory is Unknown; FFmpeg will report a filter failure if needed."); }
        if (settings.AiMode != AiRestorationMode.Off) await _aiBackend.ValidateSelectionAsync(settings, token).ConfigureAwait(false);
        return settings.AiMode == AiRestorationMode.Off ? BuildEffectivePreviewFilterChain(settings, scaleMode) : VideoRestorationPipeline.BuildPlan(settings, scaleMode).DescribeStages();
    }

    private async Task EnsureAiSourceTimingAsync(string sourcePath, VideoRestorationSettings settings, CancellationToken token)
    {
        if (settings.AiMode == AiRestorationMode.Off) return;
        SourceTimingAnalysis timing = await new SourceTimingAnalysisService(_ffprobePath, _runner, _log).AnalyzeAsync(sourcePath, token).ConfigureAwait(false);
        SourceTimingAnalysisService.EnsureCurrentCfrSupported(timing);
    }

    private async Task EnsureAiStillAsync(string final, VideoRestorationPreviewRequest request, TimeSpan position, VideoRestorationPipelinePlan plan, CancellationToken token)
    {
        await WithCacheLockAsync(final, async () =>
        {
            if (IsValidStill(final)) return;
            string staging = StagingPath(final), pre = final + ".pre.png", ai = final + ".ai.png";
            try
            {
                await EnsureStillAsync(pre, BuildStillArguments(request.SourcePath, position, plan.PreAiFilterChain, "{output}"), token).ConfigureAwait(false);
                await _aiBackend.ProcessFrameAsync(request.Settings, pre, ai, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(plan.PostAiFilterChain)) File.Copy(ai, staging, true);
                else await RunFfmpegAsync(BuildImageArguments(ai, plan.PostAiFilterChain, staging), "finishing AI preview frame", token).ConfigureAwait(false);
                if (!IsValidStill(staging)) throw new InvalidOperationException("AI restoration produced an unreadable preview image.");
                Promote(staging, final);
            }
            catch { Invalidate(staging); Invalidate(final); throw; }
            finally { Invalidate(pre); Invalidate(ai); }
        }, token).ConfigureAwait(false);
    }

    internal static string BuildEffectivePreviewFilterChain(VideoRestorationSettings settings, EncodingService.ScaleMode scaleMode)
    {
        string restoration = VideoRestorationPipeline.BuildFilterChain(settings, scaleMode); string scale = scaleMode switch { EncodingService.ScaleMode.To720p => "-2:720", EncodingService.ScaleMode.To1080p => "-2:1080", EncodingService.ScaleMode.To1440p => "-2:1440", EncodingService.ScaleMode.To4K => "-2:2160", _ => string.Empty };
        return string.IsNullOrWhiteSpace(scale) ? restoration : string.IsNullOrWhiteSpace(restoration) ? $"scale={scale}:flags=lanczos" : $"{restoration},scale={scale}:flags=lanczos";
    }
    private static string BuildPostAiPreviewFilterChain(string post, EncodingService.ScaleMode scaleMode)
    {
        string scale = scaleMode switch { EncodingService.ScaleMode.To720p => "-2:720", EncodingService.ScaleMode.To1080p => "-2:1080", EncodingService.ScaleMode.To1440p => "-2:1440", EncodingService.ScaleMode.To4K => "-2:2160", _ => "" };
        return string.IsNullOrWhiteSpace(scale) ? post : string.IsNullOrWhiteSpace(post) ? $"scale={scale}:flags=lanczos" : $"{post},scale={scale}:flags=lanczos";
    }

    private async Task RunFfmpegAsync(IReadOnlyList<string> arguments, string operation, CancellationToken token)
    {
        _log?.Invoke($"[RestorationPreview] FFmpeg {operation}: {string.Join(' ', arguments)}");
        MediaToolProcessResult result = await _runner.RunAsync(new MediaToolProcessRequest { FileName = _ffmpegPath, Arguments = arguments, Timeout = TimeSpan.FromSeconds(45) }, token).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.TimedOut) { _log?.Invoke($"[RestorationPreview] FFmpeg failure while {operation}: {result.StandardError}"); throw new InvalidOperationException($"FFmpeg could not complete the {operation}."); }
    }

    private static IReadOnlyList<string> BuildAccurateFrameArguments(string source, TimeSpan start, TimeSpan duration, string filterChain, string output, bool image)
    {
        var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-y", "-i", source, "-ss", start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) };
        if (duration > TimeSpan.Zero) { args.Add("-t"); args.Add(duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)); }
        if (!string.IsNullOrWhiteSpace(filterChain)) { args.Add("-vf"); args.Add(filterChain); }
        if (image) args.AddRange(new[] { "-frames:v", "1", "-c:v", "png" }); else args.AddRange(new[] { "-map", "0:v:0", "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "12", "-pix_fmt", "yuv420p", "-movflags", "+faststart" });
        args.Add(output); return args;
    }
    private static IReadOnlyList<string> BuildImageArguments(string source, string filterChain, string output)
    {
        var args = new List<string> { "-y", "-i", source };
        if (!string.IsNullOrWhiteSpace(filterChain)) { args.Add("-vf"); args.Add(filterChain); }
        args.AddRange(new[] { "-frames:v", "1", output }); return args;
    }
    private static IReadOnlyList<string> BuildComparisonArguments(string original, string restored, string output) => new[] { "-hide_banner", "-nostats", "-loglevel", "error", "-y", "-i", original, "-i", restored, "-filter_complex", "[0:v][1:v]scale2ref=w=oh*mdar:h=ih[original][restored];[original][restored]hstack=inputs=2[v]", "-map", "[v]", "-c:v", "libx264", "-preset", "veryfast", "-crf", "14", "-an", "-movflags", "+faststart", output };
    private static IReadOnlyList<string> ReplaceOutput(IReadOnlyList<string> arguments, string output) { var copy = arguments.ToList(); copy[^1] = output; return copy; }
    private static bool IsValidStill(string path) { try { if (!File.Exists(path) || new FileInfo(path).Length == 0) return false; using var image = Image.FromFile(path); return image.Width > 0 && image.Height > 0; } catch { return false; } }
    private static Bitmap LoadBitmap(string path) { using var image = Image.FromFile(path); return new Bitmap(image); }
    private static string StagingPath(string final) => Path.Combine(Path.GetDirectoryName(final)!, $".{Path.GetFileNameWithoutExtension(final)}-{Guid.NewGuid():N}.staging{Path.GetExtension(final)}");
    private static void Promote(string staging, string final) => File.Move(staging, final, true);
    private static void Invalidate(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static TimeSpan ClampPosition(TimeSpan position, TimeSpan duration) => TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, Math.Max(0, duration.TotalSeconds - .001)));
    private static void ValidateRequest(VideoRestorationPreviewRequest request) { if (!File.Exists(request.SourcePath)) throw new FileNotFoundException("The selected source is unavailable for restoration preview.", request.SourcePath); if (request.SourceDuration <= TimeSpan.Zero) throw new ArgumentException("MediaFlux needs a known source duration to create a restoration preview."); }
    private async Task WithCacheLockAsync(string path, Func<Task> action, CancellationToken token) { SemaphoreSlim gate = CacheLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1)); await gate.WaitAsync(token).ConfigureAwait(false); try { await action().ConfigureAwait(false); } finally { gate.Release(); } }
    private void PrepareCache() { Directory.CreateDirectory(_cacheDirectory); try { foreach (var file in new DirectoryInfo(_cacheDirectory).EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxCacheFiles)) file.Delete(); foreach (var staging in Directory.EnumerateFiles(_cacheDirectory, "*.staging.*")) if (File.GetLastWriteTimeUtc(staging) < DateTime.UtcNow.AddHours(-1)) Invalidate(staging); } catch { } }
}
