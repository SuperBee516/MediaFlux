using System.Globalization;
using MediaFlux.Models;

namespace MediaFlux.Services;

public enum VideoSplitterProcessingMode
{
    StreamCopy,
    AccurateReencode
}

public sealed class VideoSplitterExportRequest
{
    public required string SourcePath { get; init; }
    public required string OutputFolder { get; init; }
    public required IReadOnlyList<VideoSplitterSegment> Segments { get; init; }
    public required double SourceDurationSeconds { get; init; }
    public VideoSplitterProcessingMode Mode { get; init; } = VideoSplitterProcessingMode.StreamCopy;
    public bool OverwriteExistingOutput { get; init; }
    public string VideoEncoder { get; init; } = "libx264";
    public string EncoderPreset { get; init; } = "medium";
    public int QualityValue { get; init; } = 22;
}

public sealed class VideoSplitterExportProgress
{
    public int SegmentNumber { get; init; }
    public int SegmentCount { get; init; }
    public string OutputFileName { get; init; } = "";
    public string Status { get; init; } = "";
    public double? Percent { get; init; }
    public TimeSpan? CurrentTime { get; init; }
    public TimeSpan? TotalDuration { get; init; }
    public double? Speed { get; init; }
}

public sealed class VideoSplitterSegmentExportResult
{
    public required VideoSplitterSegment Segment { get; init; }
    public string OutputPath { get; init; } = "";
    public bool Success { get; init; }
    public bool WasCanceled { get; init; }
    public string ErrorMessage { get; init; } = "";
    public string DiagnosticOutput { get; init; } = "";
    public string CleanupMessage { get; init; } = "";
}

public sealed class VideoSplitterExportResult
{
    public IReadOnlyList<VideoSplitterSegmentExportResult> Segments { get; init; } = Array.Empty<VideoSplitterSegmentExportResult>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool WasCanceled { get; init; }
    public bool Success => !WasCanceled && Segments.Count > 0 && Segments.All(segment => segment.Success);
}

/// <summary>Safe staged export for splitter ranges. It shares MediaFlux's resolver,
/// ffprobe service, process runner, staging convention, and cancellation semantics.</summary>
public sealed class VideoSplitterExportService
{
    private readonly string _ffmpegPath;
    private readonly IMediaToolProcessRunner _runner;
    private readonly IMediaProbeService _probe;
    private readonly Action<string>? _log;

    public VideoSplitterExportService(string applicationDirectory, string? configuredFfmpegPath, string? configuredFfprobePath, Action<string>? log = null)
        : this(FfmpegToolResolver.Resolve(applicationDirectory, configuredFfmpegPath, configuredFfprobePath).FfmpegPath,
            new MediaToolProcessRunner(), new FfprobeService(applicationDirectory, configuredFfprobePath), log) { }

    internal VideoSplitterExportService(string ffmpegPath, IMediaToolProcessRunner runner, IMediaProbeService probe, Action<string>? log = null)
    {
        _ffmpegPath = ffmpegPath;
        _runner = runner;
        _probe = probe;
        _log = log;
    }

    public static IReadOnlyList<string> Validate(VideoSplitterExportRequest request)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.SourcePath) || !File.Exists(request.SourcePath)) errors.Add("The source file no longer exists.");
        if (request.Segments.Count == 0) errors.Add("Add at least one segment before exporting.");
        if (string.IsNullOrWhiteSpace(request.OutputFolder)) errors.Add("Choose an output folder.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VideoSplitterSegment segment in request.Segments)
        {
            if (!VideoSplitterSegmentRules.TryValidate(segment.StartSeconds, segment.EndSeconds, request.SourceDurationSeconds, out string rangeError)) errors.Add($"Segment {segment.Number}: {rangeError}");
            string name = OutputPathService.SanitizeFileName(segment.OutputFileName, $"Source-Part{segment.Number:00}.mp4");
            if (!names.Add(name)) errors.Add($"Duplicate output filename: {name}");
        }
        if (errors.Count > 0 || string.IsNullOrWhiteSpace(request.OutputFolder)) return errors;
        try
        {
            string source = Path.GetFullPath(request.SourcePath);
            string folder = Path.GetFullPath(request.OutputFolder);
            Directory.CreateDirectory(folder);
            foreach (VideoSplitterSegment segment in request.Segments)
            {
                string output = Path.Combine(folder, OutputPathService.SanitizeFileName(segment.OutputFileName));
                if (Path.GetFullPath(output).Equals(source, StringComparison.OrdinalIgnoreCase)) errors.Add("An output path must not replace the source file.");
                if (File.Exists(output) && !request.OverwriteExistingOutput) errors.Add($"Output already exists: {Path.GetFileName(output)}");
            }
            if (request.SourceDurationSeconds > 0)
            {
                long sourceBytes = new FileInfo(source).Length;
                double selectedFraction = Math.Min(1, request.Segments.Sum(segment => segment.DurationSeconds) / request.SourceDurationSeconds);
                double processingAllowance = request.Mode == VideoSplitterProcessingMode.StreamCopy ? 1.05 : 1.25;
                long estimatedBytes = (long)Math.Ceiling(sourceBytes * selectedFraction * processingAllowance);
                string? root = Path.GetPathRoot(folder);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.AvailableFreeSpace < estimatedBytes)
                        errors.Add($"The output drive has insufficient free space (approximately {estimatedBytes / 1024d / 1024d:N0} MB required).");
                }
            }
            string probe = Path.Combine(folder, $".mediaflux-write-test-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe)) { }
            File.Delete(probe);
        }
        catch (Exception ex) { errors.Add($"The output folder is not writable: {ex.Message}"); }
        return errors;
    }

    public async Task<VideoSplitterExportResult> ExportAsync(VideoSplitterExportRequest request, IProgress<VideoSplitterExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<string> errors = Validate(request);
        if (errors.Count > 0) return new VideoSplitterExportResult { Warnings = errors };
        if (!File.Exists(_ffmpegPath)) return new VideoSplitterExportResult { Warnings = new[] { $"FFmpeg was not found at '{_ffmpegPath}'." } };

        MediaProbeResult sourceProbe;
        try
        {
            sourceProbe = await _probe.ProbeAsync(request.SourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new VideoSplitterExportResult { Warnings = new[] { $"The source streams could not be inspected: {ex.Message}" } };
        }

        if (!sourceProbe.Success)
            return new VideoSplitterExportResult { Warnings = new[] { $"The source streams could not be inspected: {sourceProbe.ErrorMessage}" } };

        if (!sourceProbe.Streams.Any(IsPlayableVideo))
            return new VideoSplitterExportResult { Warnings = new[] { "The source does not contain a playable video stream." } };

        var results = new List<VideoSplitterSegmentExportResult>();
        bool canceled = false;
        for (int index = 0; index < request.Segments.Count; index++)
        {
            VideoSplitterSegment segment = request.Segments[index];
            try
            {
                VideoSplitterSegmentExportResult result = await ExportSegmentAsync(request, sourceProbe, segment, index + 1, progress, cancellationToken).ConfigureAwait(false);
                results.Add(result);
                if (result.WasCanceled) { canceled = true; break; }
            }
            catch (OperationCanceledException)
            {
                results.Add(new VideoSplitterSegmentExportResult { Segment = segment, WasCanceled = true, ErrorMessage = "Canceled before this segment could be completed." });
                canceled = true;
                break;
            }
        }
        return new VideoSplitterExportResult { Segments = results, WasCanceled = canceled };
    }

    private async Task<VideoSplitterSegmentExportResult> ExportSegmentAsync(VideoSplitterExportRequest request, MediaProbeResult sourceProbe, VideoSplitterSegment segment, int position, IProgress<VideoSplitterExportProgress>? progress, CancellationToken token)
    {
        string finalPath = Path.Combine(Path.GetFullPath(request.OutputFolder), OutputPathService.SanitizeFileName(segment.OutputFileName));
        string stagingPath = OutputPathService.CreateStagingPath(finalPath);
        string diagnostics = "";
        try
        {
            double duration = segment.DurationSeconds;
            StreamMappingPlan mapping = CreateStreamMappingPlan(sourceProbe, stagingPath);
            IReadOnlyList<string> args = request.Mode == VideoSplitterProcessingMode.StreamCopy
                ? BuildStreamCopyArguments(request.SourcePath, stagingPath, segment.StartSeconds, duration, mapping)
                : BuildAccurateReencodeArguments(request.SourcePath, stagingPath, segment.StartSeconds, duration, request.VideoEncoder, request.EncoderPreset, request.QualityValue, mapping);
            _log?.Invoke($"[VideoSplitter] {request.Mode}: {string.Join(" ", args)}");
            var state = new ProgressState(position, request.Segments.Count, Path.GetFileName(finalPath), duration, progress, request.Mode);
            MediaToolProcessResult process = await _runner.RunAsync(new MediaToolProcessRequest { FileName = _ffmpegPath, Arguments = args, Timeout = Timeout.InfiniteTimeSpan, SendQuitOnCancellation = true, StandardOutputLineCallback = state.Handle }, token).ConfigureAwait(false);
            diagnostics = process.StandardError;
            if (process.ExitCode != 0) return Failed(segment, finalPath, $"FFmpeg failed: {LastLine(diagnostics)}", diagnostics, stagingPath);

            MediaProbeResult probe = await _probe.ProbeAsync(stagingPath, token).ConfigureAwait(false);
            if (!probe.Success || probe.DurationSeconds is not > 0 || !probe.Streams.Any(IsPlayableVideo))
                return Failed(segment, finalPath, "The staged output failed FFprobe validation.", diagnostics, stagingPath);
            if (Math.Abs(probe.DurationSeconds.Value - duration) > Math.Max(2, duration * .12))
                return Failed(segment, finalPath, "The staged output duration differs too much from the requested selection.", diagnostics, stagingPath);
            token.ThrowIfCancellationRequested();
            if (File.Exists(finalPath) && !request.OverwriteExistingOutput) return Failed(segment, finalPath, "The output file was created by another process.", diagnostics, stagingPath);
            File.Move(stagingPath, finalPath, request.OverwriteExistingOutput);
            progress?.Report(new VideoSplitterExportProgress { SegmentNumber = position, SegmentCount = request.Segments.Count, OutputFileName = Path.GetFileName(finalPath), Status = "Completed", Percent = 100, CurrentTime = TimeSpan.FromSeconds(duration), TotalDuration = TimeSpan.FromSeconds(duration) });
            return new VideoSplitterSegmentExportResult { Segment = segment, OutputPath = finalPath, Success = true, DiagnosticOutput = diagnostics };
        }
        catch (OperationCanceledException)
        {
            return new VideoSplitterSegmentExportResult { Segment = segment, OutputPath = finalPath, WasCanceled = true, ErrorMessage = "Canceled. The source was not modified.", DiagnosticOutput = diagnostics, CleanupMessage = DeleteStage(stagingPath) };
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[VideoSplitter] Segment {segment.Number} failed: {ex}");
            return Failed(segment, finalPath, ex.Message, diagnostics, stagingPath);
        }
    }

    internal static IReadOnlyList<string> BuildStreamCopyArguments(string source, string staging, double start, double duration) =>
        BuildStreamCopyArguments(source, staging, start, duration, StreamMappingPlan.Fallback(staging));

    internal static IReadOnlyList<string> BuildStreamCopyArguments(string source, string staging, double start, double duration, StreamMappingPlan mapping)
    {
        var args = new List<string> { "-hide_banner", "-y", "-progress", "pipe:1", "-nostats", "-ss", F(start), "-i", source, "-t", F(duration) };
        AddVideoMaps(args, mapping);
        args.AddRange(new[] { "-map", "0:a?", "-map", "0:s?", "-map", "0:t?", "-c", "copy", "-map_metadata", "0", "-avoid_negative_ts", "make_zero", staging });
        return args;
    }

    internal static IReadOnlyList<string> BuildAccurateReencodeArguments(string source, string staging, double start, double duration, string encoder, string preset, int quality) =>
        BuildAccurateReencodeArguments(source, staging, start, duration, encoder, preset, quality, StreamMappingPlan.Fallback(staging));

    internal static IReadOnlyList<string> BuildAccurateReencodeArguments(string source, string staging, double start, double duration, string encoder, string preset, int quality, StreamMappingPlan mapping)
    {
        string selectedEncoder = string.IsNullOrWhiteSpace(encoder) ? "libx264" : encoder;
        var args = new List<string> { "-hide_banner", "-y", "-progress", "pipe:1", "-nostats", "-i", source, "-ss", F(start), "-t", F(duration) };
        AddVideoMaps(args, mapping);
        args.AddRange(new[] { "-map", "0:a?", "-map", "0:s?", "-c:v", selectedEncoder });
        if (selectedEncoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase)) args.AddRange(new[] { "-preset", string.IsNullOrWhiteSpace(preset) ? "p5" : preset, "-rc", "vbr", "-cq", Math.Clamp(quality, 0, 51).ToString(CultureInfo.InvariantCulture), "-b:v", "0" });
        else if (selectedEncoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase)) args.AddRange(new[] { "-preset", string.IsNullOrWhiteSpace(preset) ? "medium" : preset, "-global_quality", Math.Clamp(quality, 0, 51).ToString(CultureInfo.InvariantCulture) });
        else args.AddRange(new[] { "-preset", string.IsNullOrWhiteSpace(preset) ? "medium" : preset, "-crf", Math.Clamp(quality, 0, 51).ToString(CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-c:a", "aac", "-c:s", "copy" });
        for (int artworkIndex = 0; artworkIndex < mapping.AttachedPictureStreamIndexes.Count; artworkIndex++)
        {
            int outputVideoIndex = mapping.PlayableVideoStreamIndexes.Count + artworkIndex;
            args.AddRange(new[] { $"-c:v:{outputVideoIndex}", "copy", $"-disposition:v:{outputVideoIndex}", "attached_pic" });
        }
        args.AddRange(new[] { "-map_metadata", "0", staging });
        return args;
    }

    internal static StreamMappingPlan CreateStreamMappingPlan(MediaProbeResult source, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        IReadOnlyList<int> playable = source.Streams.Where(IsPlayableVideo).Select(stream => stream.Index).ToArray();
        IReadOnlyList<int> attachedPictures = CanCopyAttachedArtwork(source.Streams.Where(IsAttachedPicture), outputPath)
            ? source.Streams.Where(IsAttachedPicture).Select(stream => stream.Index).ToArray()
            : Array.Empty<int>();
        return new StreamMappingPlan(playable, attachedPictures);
    }

    private static void AddVideoMaps(List<string> args, StreamMappingPlan mapping)
    {
        if (mapping.PlayableVideoStreamIndexes.Count == 0)
            args.AddRange(new[] { "-map", "0:V?" });
        else
            foreach (int index in mapping.PlayableVideoStreamIndexes)
                args.AddRange(new[] { "-map", $"0:{index}" });

        foreach (int index in mapping.AttachedPictureStreamIndexes)
            args.AddRange(new[] { "-map", $"0:{index}" });
    }

    private static bool IsPlayableVideo(MediaProbeStreamInfo stream) =>
        stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase) && !IsAttachedPicture(stream);

    private static bool IsAttachedPicture(MediaProbeStreamInfo stream) =>
        stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase) &&
        stream.Dispositions.TryGetValue("attached_pic", out bool attached) && attached;

    private static bool CanCopyAttachedArtwork(IEnumerable<MediaProbeStreamInfo> streams, string outputPath)
    {
        MediaProbeStreamInfo[] artwork = streams.ToArray();
        if (artwork.Length == 0) return false;
        string extension = Path.GetExtension(outputPath);
        if (extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)) return true;
        if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)) return false;
        return artwork.All(stream => stream.CodecName.Equals("mjpeg", StringComparison.OrdinalIgnoreCase) || stream.CodecName.Equals("png", StringComparison.OrdinalIgnoreCase));
    }

    internal sealed record StreamMappingPlan(IReadOnlyList<int> PlayableVideoStreamIndexes, IReadOnlyList<int> AttachedPictureStreamIndexes)
    {
        public static StreamMappingPlan Fallback(string outputPath) => new(Array.Empty<int>(), Array.Empty<int>());
    }
    private static string F(double value) => Math.Max(0, value).ToString("0.###", CultureInfo.InvariantCulture);
    private static VideoSplitterSegmentExportResult Failed(VideoSplitterSegment segment, string output, string error, string diagnostics, string staging) => new() { Segment = segment, OutputPath = output, ErrorMessage = error, DiagnosticOutput = diagnostics, CleanupMessage = DeleteStage(staging) };
    private static string DeleteStage(string path) { try { if (File.Exists(path)) File.Delete(path); return File.Exists(path) ? "Incomplete staging file could not be removed." : "Incomplete staging output removed."; } catch (Exception ex) { return $"Incomplete staging file remains: {ex.Message}"; } }
    private static string LastLine(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Unknown FFmpeg error.";
    private sealed class ProgressState(int number, int count, string output, double duration, IProgress<VideoSplitterExportProgress>? progress, VideoSplitterProcessingMode mode)
    {
        private double _time; private double? _speed;
        public void Handle(string line)
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..], out long microseconds)) _time = microseconds / 1_000_000d;
            if (line.StartsWith("speed=", StringComparison.Ordinal) && double.TryParse(line[6..].TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out double speed)) _speed = speed;
            if (line is "progress=continue" or "progress=end") progress?.Report(new VideoSplitterExportProgress { SegmentNumber = number, SegmentCount = count, OutputFileName = output, Status = mode == VideoSplitterProcessingMode.StreamCopy ? "Stream copying" : "Re-encoding", Percent = duration > 0 ? Math.Clamp(_time / duration * 100, 0, 99) : null, CurrentTime = TimeSpan.FromSeconds(_time), TotalDuration = TimeSpan.FromSeconds(duration), Speed = _speed });
        }
    }
}
