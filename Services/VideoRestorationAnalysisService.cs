using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Bounded probe-based source assessment; uncertain visual properties deliberately remain Unknown.</summary>
public sealed class VideoRestorationAnalysisService
{
    private readonly IMediaProbeService _probe;
    private readonly string? _ffmpegPath;
    private readonly string? _ffprobePath;
    private readonly IMediaToolProcessRunner? _runner;
    private readonly ConcurrentDictionary<string, VideoRestorationAnalysisResult> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _log;
    public VideoRestorationAnalysisService(string appDirectory, string? ffprobePath = null, string? ffmpegPath = null, Action<string>? log = null) : this(new FfprobeService(appDirectory, ffprobePath), log) { FfmpegToolPaths tools = FfmpegToolResolver.Resolve(appDirectory, ffmpegPath, ffprobePath); _ffmpegPath = tools.FfmpegPath; _ffprobePath = tools.FfprobePath; _runner = new MediaToolProcessRunner(); }
    internal VideoRestorationAnalysisService(IMediaProbeService probe, Action<string>? log = null) { _probe = probe; _log = log; }
    public async Task<VideoRestorationAnalysisResult> AnalyzeAsync(string sourcePath, bool? animationHint = null, CancellationToken cancellationToken = default)
    {
        string key = CacheKey(sourcePath); if (_cache.TryGetValue(key, out var cached)) return cached;
        MediaProbeResult probe = await _probe.ProbeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!probe.Success) return new VideoRestorationAnalysisResult { SourcePath = sourcePath, AnimationHint = animationHint, Warnings = new[] { probe.ErrorMessage ?? "FFprobe could not inspect this source." } };
        MediaProbeStreamInfo? video = probe.Streams.FirstOrDefault(stream => stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase) && !(stream.Dispositions.TryGetValue("attached_pic", out bool attached) && attached));
        if (video == null) return new VideoRestorationAnalysisResult { SourcePath = sourcePath, AnimationHint = animationHint, Warnings = new[] { "No playable video stream was found." } };
        bool mpeg2 = video.CodecName?.Contains("mpeg2", StringComparison.OrdinalIgnoreCase) == true;
        bool sd = video.Height is > 0 and <= 576;
        bool interlaced = video.FieldOrder is not null && !video.FieldOrder.Equals("progressive", StringComparison.OrdinalIgnoreCase) && !video.FieldOrder.Equals("unknown", StringComparison.OrdinalIgnoreCase);
        var evidence = new List<string>(); if (mpeg2) evidence.Add("MPEG-2 source codec"); if (sd) evidence.Add("SD source resolution"); if (interlaced) evidence.Add($"FFprobe field order: {video.FieldOrder}");
        var picture = await SamplePictureConditionsAsync(sourcePath, probe.DurationSeconds ?? 0, cancellationToken).ConfigureAwait(false);
        SourceTimingAnalysis? timing = _probe is FfprobeService && !string.IsNullOrWhiteSpace(_ffprobePath) ? await new SourceTimingAnalysisService(_ffprobePath, _runner, _log).AnalyzeAsync(sourcePath, cancellationToken).ConfigureAwait(false) : null;
        var result = new VideoRestorationAnalysisResult { SourcePath = sourcePath, Width = video.Width, Height = video.Height, FrameRate = video.FrameRate, Codec = video.CodecName ?? "Unknown", ScanType = interlaced ? RestorationScanType.InterlacedSuspected : RestorationScanType.Unknown, Noise = picture.Noise, Blocking = picture.Blocking == RestorationEvidenceLevel.Unknown && mpeg2 && sd ? RestorationEvidenceLevel.Moderate : picture.Blocking, Banding = picture.Banding, AnimationHint = animationHint, Confidence = evidence.Count == 0 ? 20 : interlaced ? 70 : picture.Noise == RestorationEvidenceLevel.Unknown ? 40 : 60, Evidence = evidence, Timing = timing };
        _cache[key] = result; _log?.Invoke($"[RestorationAnalysis] {Path.GetFileName(sourcePath)}: {video.Width}x{video.Height} {result.Codec}; scan={result.ScanType}; blocking={result.Blocking}; confidence={result.Confidence}."); return result;
    }
    private static string CacheKey(string path) { var info = new FileInfo(path); return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"; }
    private async Task<(RestorationEvidenceLevel Noise, RestorationEvidenceLevel Banding, RestorationEvidenceLevel Blocking)> SamplePictureConditionsAsync(string path, double duration, CancellationToken token)
    {
        if (_runner == null || string.IsNullOrWhiteSpace(_ffmpegPath) || !File.Exists(_ffmpegPath) || duration <= 0) return (RestorationEvidenceLevel.Unknown, RestorationEvidenceLevel.Unknown, RestorationEvidenceLevel.Unknown);
        var samples = new List<VideoRestorationFrameMetrics>();
        foreach (double point in new[] { .12, .50, .88 })
        {
            double second = Math.Max(0, duration * point);
            try
            {
                MediaToolProcessResult run = await _runner.RunAsync(new MediaToolProcessRequest { FileName = _ffmpegPath, Arguments = new[] { "-hide_banner", "-ss", second.ToString("0.###", CultureInfo.InvariantCulture), "-i", path, "-frames:v", "2", "-vf", "signalstats,metadata=print", "-an", "-f", "null", "-" }, Timeout = TimeSpan.FromSeconds(12) }, token).ConfigureAwait(false);
                if (run.ExitCode != 0 || !TryMetrics(run.StandardError + "\n" + run.StandardOutput, out VideoRestorationFrameMetrics metric)) continue;
                samples.Add(metric);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Sampling is optional; uncertain remains Unknown. */ }
        }
        return VideoRestorationPictureConditionSampling.Classify(samples);
    }
    private static readonly Regex Metric = new(@"lavfi\.signalstats\.(?<name>YDIF|YVAR|YHIGH)=?(?<value>[\d.]+)", RegexOptions.Compiled);
    private static bool TryMetrics(string text, out VideoRestorationFrameMetrics metrics)
    {
        var values = Metric.Matches(text).GroupBy(match => match.Groups["name"].Value).ToDictionary(group => group.Key, group => double.Parse(group.Last().Groups["value"].Value, CultureInfo.InvariantCulture));
        if (!values.TryGetValue("YDIF", out double dif) || !values.TryGetValue("YVAR", out double variance)) { metrics = default!; return false; }
        // YDIF is normalized against luma range; lower edge variation is treated only as a cautious banding signal.
        metrics = new VideoRestorationFrameMetrics(dif / 255d, variance / 65025d, Math.Min(1, Math.Abs(variance - dif) / 65025d), double.NaN); return true;
    }
}
