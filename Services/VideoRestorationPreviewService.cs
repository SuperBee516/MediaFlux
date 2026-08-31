using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed record VideoRestorationPreviewRequest(
    string SourcePath,
    TimeSpan SourceDuration,
    TimeSpan Position,
    VideoRestorationSettings Settings,
    EncodingService.ScaleMode EncodeScale = EncodingService.ScaleMode.None);

public sealed class VideoRestorationStillPreview : IDisposable
{
    internal VideoRestorationStillPreview(Image original, Image restored, TimeSpan position, string filterChain, bool resolutionChanged)
    { Original = original; Restored = restored; Position = position; FilterChain = filterChain; ResolutionChanged = resolutionChanged; }
    public Image Original { get; }
    public Image Restored { get; }
    public TimeSpan Position { get; }
    public string FilterChain { get; }
    public bool ResolutionChanged { get; }
    public void Dispose() { Original.Dispose(); Restored.Dispose(); }
}

public sealed record VideoRestorationMotionPreview(TimeSpan Start, TimeSpan Duration, string OriginalPath, string RestoredPath, string ComparisonPath);

/// <summary>
/// Produces cached, accurate-seek restoration previews. The restoration expression is always obtained
/// from <see cref="VideoRestorationPipeline"/>, which is also used by the normal encoding path.
/// </summary>
public sealed class VideoRestorationPreviewService
{
    private const int MaxCacheFiles = 80;
    private readonly string _ffmpegPath;
    private readonly IMediaToolProcessRunner _runner;
    private readonly FfmpegRestorationCapabilityService _capabilities;
    private readonly string _cacheDirectory;
    private readonly Action<string>? _log;

    public VideoRestorationPreviewService(string applicationDirectory, string? configuredFfmpegPath = null, string? configuredFfprobePath = null, Action<string>? log = null)
        : this(FfmpegToolResolver.Resolve(applicationDirectory, configuredFfmpegPath, configuredFfprobePath).FfmpegPath, new MediaToolProcessRunner(), new FfmpegRestorationCapabilityService(log: log), Path.Combine(AppPaths.DataDirectory, "restoration-previews"), log) { }

    internal VideoRestorationPreviewService(string ffmpegPath, IMediaToolProcessRunner runner, FfmpegRestorationCapabilityService capabilities, string cacheDirectory, Action<string>? log = null)
    { _ffmpegPath = ffmpegPath; _runner = runner; _capabilities = capabilities; _cacheDirectory = cacheDirectory; _log = log; }

    public static IReadOnlyList<TimeSpan> BuildRepresentativePositions(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return Array.Empty<TimeSpan>();
        // Avoid misleading fades/credits at the edges while still covering short sources sensibly.
        double edge = Math.Min(20, Math.Max(1, duration.TotalSeconds * .08));
        double end = Math.Max(edge, duration.TotalSeconds - edge);
        return new[] { edge, edge + ((end - edge) * .25), edge + ((end - edge) * .5), edge + ((end - edge) * .75), end }
            .Select(TimeSpan.FromSeconds).Distinct().ToArray();
    }

    public async Task<VideoRestorationStillPreview> GenerateStillAsync(VideoRestorationPreviewRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        string filterChain = await PreparePipelineAsync(request.Settings, request.EncodeScale, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_cacheDirectory); CleanupCache();
        TimeSpan position = ClampPosition(request.Position, request.SourceDuration);
        string originalPath = Path.Combine(_cacheDirectory, "original-" + BuildCacheKey(request, string.Empty) + ".png");
        string restoredPath = Path.Combine(_cacheDirectory, "restored-" + BuildCacheKey(request, filterChain) + ".png");
        if (!File.Exists(originalPath))
            await ExtractStillAsync(request.SourcePath, position, string.Empty, originalPath, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(restoredPath))
            await ExtractStillAsync(request.SourcePath, position, filterChain, restoredPath, cancellationToken).ConfigureAwait(false);
        _log?.Invoke($"[RestorationPreview] still timestamp={position.TotalSeconds:0.###}; filters={(string.IsNullOrWhiteSpace(filterChain) ? "Off" : filterChain)}.");
        return new VideoRestorationStillPreview(LoadBitmap(originalPath), LoadBitmap(restoredPath), position, filterChain, request.Settings.Resize != VideoRestorationResize.Original);
    }

    public async Task<VideoRestorationMotionPreview> GenerateMotionAsync(VideoRestorationPreviewRequest request, TimeSpan? requestedDuration = null, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        string filterChain = await PreparePipelineAsync(request.Settings, request.EncodeScale, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_cacheDirectory); CleanupCache();
        TimeSpan duration = TimeSpan.FromSeconds(Math.Min(Math.Max(1, (requestedDuration ?? TimeSpan.FromSeconds(5)).TotalSeconds), Math.Max(1, request.SourceDuration.TotalSeconds)));
        TimeSpan start = TimeSpan.FromSeconds(Math.Clamp(request.Position.TotalSeconds - duration.TotalSeconds / 2, 0, Math.Max(0, request.SourceDuration.TotalSeconds - duration.TotalSeconds)));
        string key = BuildCacheKey(request with { Position = start }, filterChain + "|clip|" + duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        string original = Path.Combine(_cacheDirectory, "motion-original-" + key + ".mp4");
        string restored = Path.Combine(_cacheDirectory, "motion-restored-" + key + ".mp4");
        string comparison = Path.Combine(_cacheDirectory, "motion-comparison-" + key + ".mp4");
        if (!File.Exists(original)) await ExtractMotionAsync(request.SourcePath, start, duration, string.Empty, original, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(restored)) await ExtractMotionAsync(request.SourcePath, start, duration, filterChain, restored, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(comparison)) await BuildComparisonAsync(original, restored, comparison, cancellationToken).ConfigureAwait(false);
        _log?.Invoke($"[RestorationPreview] motion start={start.TotalSeconds:0.###}; duration={duration.TotalSeconds:0.###}; filters={(string.IsNullOrWhiteSpace(filterChain) ? "Off" : filterChain)}.");
        return new VideoRestorationMotionPreview(start, duration, original, restored, comparison);
    }

    internal static string BuildCacheKey(VideoRestorationPreviewRequest request, string effectiveChain)
    {
        var file = new FileInfo(request.SourcePath);
        string input = $"{Path.GetFullPath(request.SourcePath)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{request.Position.TotalMilliseconds:0}|{request.EncodeScale}|{effectiveChain}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).Substring(0, 24);
    }

    internal static IReadOnlyList<string> BuildStillArguments(string source, TimeSpan position, string filterChain, string output) =>
        BuildAccurateFrameArguments(source, position, TimeSpan.Zero, filterChain, output, image: true);

    private async Task<string> PreparePipelineAsync(VideoRestorationSettings settings, EncodingService.ScaleMode scaleMode, CancellationToken token)
    {
        VideoRestorationPipeline.Validate(settings, scaleMode);
        if (settings.Preset == VideoRestorationPreset.Off) return string.Empty;
        FfmpegRestorationCapabilities inventory = await _capabilities.GetAsync(_ffmpegPath, token).ConfigureAwait(false);
        if (inventory.State == FfmpegFilterInventoryState.Available)
        { VideoRestorationPipeline.SetAvailableFilters(inventory.Filters); VideoRestorationPipeline.ValidateAvailable(settings); }
        else
        { VideoRestorationPipeline.ClearAvailableFilters(); _log?.Invoke("[RestorationPreview] FFmpeg restoration inventory is Unknown; preview will let FFmpeg report any filter failure."); }
        return BuildEffectivePreviewFilterChain(settings, scaleMode);
    }

    internal static string BuildEffectivePreviewFilterChain(VideoRestorationSettings settings, EncodingService.ScaleMode scaleMode)
    {
        string restoration = VideoRestorationPipeline.BuildFilterChain(settings, scaleMode);
        string scale = scaleMode switch
        {
            EncodingService.ScaleMode.To720p => "-2:720",
            EncodingService.ScaleMode.To1080p => "-2:1080",
            EncodingService.ScaleMode.To1440p => "-2:1440",
            EncodingService.ScaleMode.To4K => "-2:2160",
            _ => string.Empty
        };
        // This mirrors the encoder providers: restoration first, then the normal encode scale.
        return string.IsNullOrWhiteSpace(scale) ? restoration : string.IsNullOrWhiteSpace(restoration) ? $"scale={scale}:flags=lanczos" : $"{restoration},scale={scale}:flags=lanczos";
    }

    private async Task ExtractStillAsync(string source, TimeSpan position, string filterChain, string output, CancellationToken token) =>
        await RunAsync(BuildStillArguments(source, position, filterChain, output), "extracting the restoration preview frame", token).ConfigureAwait(false);

    private async Task ExtractMotionAsync(string source, TimeSpan start, TimeSpan duration, string filterChain, string output, CancellationToken token) =>
        await RunAsync(BuildAccurateFrameArguments(source, start, duration, filterChain, output, image: false), "creating the restoration preview clip", token).ConfigureAwait(false);

    private async Task BuildComparisonAsync(string original, string restored, string output, CancellationToken token) =>
        // scale2ref only normalizes display height for side-by-side playback; it does not feed back
        // into the still preview or encode pipeline, and the UI calls out restoration resizing.
        await RunAsync(new[] { "-hide_banner", "-nostats", "-loglevel", "error", "-y", "-i", original, "-i", restored, "-filter_complex", "[0:v][1:v]scale2ref=w=oh*mdar:h=ih[original][restored];[original][restored]hstack=inputs=2[v]", "-map", "[v]", "-c:v", "libx264", "-preset", "veryfast", "-crf", "14", "-an", "-movflags", "+faststart", output }, "building the synchronized restoration comparison", token).ConfigureAwait(false);

    private async Task RunAsync(IReadOnlyList<string> arguments, string operation, CancellationToken token)
    {
        MediaToolProcessResult result = await _runner.RunAsync(new MediaToolProcessRequest { FileName = _ffmpegPath, Arguments = arguments, Timeout = TimeSpan.FromSeconds(45) }, token).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.TimedOut) throw new InvalidOperationException($"FFmpeg failed while {operation}: {result.StandardError.Trim()}");
    }

    private static IReadOnlyList<string> BuildAccurateFrameArguments(string source, TimeSpan start, TimeSpan duration, string filterChain, string output, bool image)
    {
        // Seek after input for both sides. It is slower than input seeking but keeps the same decoded frame region.
        var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-y", "-i", source, "-ss", start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) };
        if (duration > TimeSpan.Zero) { args.Add("-t"); args.Add(duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)); }
        if (!string.IsNullOrWhiteSpace(filterChain)) { args.Add("-vf"); args.Add(filterChain); }
        if (image) { args.Add("-frames:v"); args.Add("1"); args.Add("-c:v"); args.Add("png"); }
        else { args.AddRange(new[] { "-map", "0:v:0", "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "12", "-pix_fmt", "yuv420p", "-movflags", "+faststart" }); }
        args.Add(output); return args;
    }

    private static Bitmap LoadBitmap(string path) { using var image = Image.FromFile(path); return new Bitmap(image); }
    private static TimeSpan ClampPosition(TimeSpan position, TimeSpan duration) => TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, Math.Max(0, duration.TotalSeconds - .001)));
    private static void ValidateRequest(VideoRestorationPreviewRequest request)
    { if (!File.Exists(request.SourcePath)) throw new FileNotFoundException("The selected source is unavailable for restoration preview.", request.SourcePath); if (request.SourceDuration <= TimeSpan.Zero) throw new ArgumentException("MediaFlux needs a known source duration to create a restoration preview."); }
    private void CleanupCache()
    {
        try { foreach (var file in new DirectoryInfo(_cacheDirectory).EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxCacheFiles)) file.Delete(); }
        catch { /* Cache cleanup is best effort and never blocks a preview. */ }
    }
}
