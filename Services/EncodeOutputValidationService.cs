using System.Globalization;
using MediaFlux.Models;

namespace MediaFlux.Services
{
    public interface IDecodeIntegritySpotCheckService
    {
        Task<DecodeIntegritySpotCheckResult> CheckAsync(
            string outputPath,
            double? durationSeconds,
            CancellationToken cancellationToken = default);
    }

    public sealed class FfmpegDecodeIntegritySpotCheckService :
        IDecodeIntegritySpotCheckService
    {
        private const double DecodeSeconds = 0.35;
        private readonly string _ffmpegPath;
        private readonly IMediaToolProcessRunner _processRunner;

        public FfmpegDecodeIntegritySpotCheckService(
            string ffmpegPath,
            IMediaToolProcessRunner? processRunner = null)
        {
            _ffmpegPath = ffmpegPath;
            _processRunner = processRunner ?? new MediaToolProcessRunner();
        }

        public async Task<DecodeIntegritySpotCheckResult> CheckAsync(
            string outputPath,
            double? durationSeconds,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(_ffmpegPath))
            {
                return Failed(
                    $"FFmpeg was not found at '{_ffmpegPath}' for decode-integrity validation.");
            }

            IReadOnlyList<double> positions = BuildPositions(durationSeconds);
            foreach (double position in positions)
            {
                MediaToolProcessResult result = await _processRunner.RunAsync(
                    new MediaToolProcessRequest
                    {
                        FileName = _ffmpegPath,
                        Timeout = TimeSpan.FromSeconds(30),
                        Arguments = BuildArguments(outputPath, position)
                    },
                    cancellationToken).ConfigureAwait(false);
                if (result.TimedOut)
                {
                    return Failed(
                        $"The decode-integrity check timed out near {position:0.##} seconds.",
                        positions);
                }

                if (result.ExitCode != 0)
                {
                    string detail = LastUsefulLine(result.StandardError);
                    return Failed(
                        $"Video decoding failed near {position:0.##} seconds" +
                        (string.IsNullOrWhiteSpace(detail) ? "." : $": {detail}"),
                        positions);
                }
            }

            return new DecodeIntegritySpotCheckResult
            {
                Success = true,
                PositionsSeconds = positions
            };
        }

        internal static IReadOnlyList<double> BuildPositions(double? durationSeconds)
        {
            if (durationSeconds is not > 0 || !double.IsFinite(durationSeconds.Value))
                return new[] { 0d };

            double duration = durationSeconds.Value;
            double end = Math.Max(0, duration - Math.Max(1, DecodeSeconds));
            return new[] { 0d, duration / 2d, end }
                .Select(value => Math.Max(0, value))
                .DistinctBy(value => Math.Round(value, 1))
                .OrderBy(value => value)
                .ToArray();
        }

        internal static IReadOnlyList<string> BuildArguments(
            string outputPath,
            double positionSeconds) =>
            new[]
            {
                "-hide_banner",
                "-v", "error",
                "-ss", positionSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", outputPath,
                "-map", "0:v:0",
                "-t", DecodeSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                "-an", "-sn", "-dn",
                "-f", "null",
                "-"
            };

        private static DecodeIntegritySpotCheckResult Failed(
            string message,
            IReadOnlyList<double>? positions = null) => new()
        {
            Success = false,
            ErrorMessage = message,
            PositionsSeconds = positions ?? Array.Empty<double>()
        };

        private static string LastUsefulLine(string value) =>
            (value ?? "")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .LastOrDefault(line => line.Length > 0) ?? "";
    }

    public interface IEncodeOutputValidationService
    {
        Task<EncodeOutputValidationResult> ValidateStagedAsync(
            EncodeOutputValidationRequest request,
            CancellationToken cancellationToken = default);

        Task<EncodeOutputValidationResult> ValidatePromotedAsync(
            EncodeOutputValidationRequest request,
            EncodeOutputValidationEvidence stagedEvidence,
            CancellationToken cancellationToken = default);
    }

    public sealed class EncodeOutputValidationService :
        IEncodeOutputValidationService
    {
        // FFmpeg can discard container-created chapter entries shorter than this.
        private const double MinimumMeaningfulChapterDurationSeconds = 0.050;

        // Allows common mux timestamp rebasing/rounding while still detecting a
        // meaningful chapter boundary or duration change.
        private const double ChapterTimestampToleranceSeconds = 0.125;

        private readonly IMediaProbeService _probeService;
        private readonly IDecodeIntegritySpotCheckService _decodeIntegrityService;
        private readonly Action<string>? _log;

        public EncodeOutputValidationService(
            IMediaProbeService probeService,
            IDecodeIntegritySpotCheckService decodeIntegrityService,
            Action<string>? log = null)
        {
            _probeService = probeService ??
                throw new ArgumentNullException(nameof(probeService));
            _decodeIntegrityService = decodeIntegrityService ??
                throw new ArgumentNullException(nameof(decodeIntegrityService));
            _log = log;
        }

        public async Task<EncodeOutputValidationResult> ValidateStagedAsync(
            EncodeOutputValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            string sourceProbePath = GetSourceProbePath(request.Input);
            MediaProbeResult sourceProbe = request.SourceProbe ??
                await _probeService.ProbeAsync(
                    sourceProbePath,
                    cancellationToken).ConfigureAwait(false);
            if (!sourceProbe.Success)
            {
                return Failed(
                    $"FFprobe could not inspect the source for comparison: {sourceProbe.ErrorMessage}");
            }

            EncodeOutputValidationResult mediaValidation = await ValidateMediaAsync(
                request,
                sourceProbe,
                request.OutputPath,
                cancellationToken).ConfigureAwait(false);
            if (!mediaValidation.Success)
                return mediaValidation;

            DecodeIntegritySpotCheckResult decode =
                await _decodeIntegrityService.CheckAsync(
                    request.OutputPath,
                    ProgramDurationResolver.Resolve(mediaValidation.Evidence!.OutputProbe).DurationSeconds,
                    cancellationToken).ConfigureAwait(false);
            if (!decode.Success)
                return Failed($"Decode-integrity spot check failed: {decode.ErrorMessage}");

            EncodeOutputValidationEvidence evidence = mediaValidation.Evidence!;
            return new EncodeOutputValidationResult
            {
                Success = true,
                Summary =
                    "FFprobe media validation and beginning/middle/end decode-integrity checks passed.",
                Evidence = new EncodeOutputValidationEvidence
                {
                    SourceProbe = evidence.SourceProbe,
                    OutputProbe = evidence.OutputProbe,
                    OutputSizeBytes = evidence.OutputSizeBytes,
                    OutputLastWriteUtcTicks = evidence.OutputLastWriteUtcTicks,
                    DecodePositionsSeconds = decode.PositionsSeconds
                }
            };
        }

        public async Task<EncodeOutputValidationResult> ValidatePromotedAsync(
            EncodeOutputValidationRequest request,
            EncodeOutputValidationEvidence stagedEvidence,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(stagedEvidence);
            EncodeOutputValidationResult result = await ValidateMediaAsync(
                request,
                stagedEvidence.SourceProbe,
                request.FinalOutputPath,
                cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                return result;
            if (result.Evidence!.OutputSizeBytes != stagedEvidence.OutputSizeBytes)
            {
                return Failed(
                    "The promoted output size changed after staged validation.");
            }
            if (stagedEvidence.OutputLastWriteUtcTicks != 0 &&
                result.Evidence.OutputLastWriteUtcTicks !=
                stagedEvidence.OutputLastWriteUtcTicks)
            {
                return Failed(
                    "The promoted output modification identity changed after staged validation.");
            }

            return new EncodeOutputValidationResult
            {
                Success = true,
                Summary = "The promoted final output passed essential FFprobe validation.",
                Evidence = result.Evidence
            };
        }

        private async Task<EncodeOutputValidationResult> ValidateMediaAsync(
            EncodeOutputValidationRequest request,
            MediaProbeResult sourceProbe,
            string outputPath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
                return Failed("The encoded output file does not exist.");

            long length;
            long lastWriteUtcTicks;
            try
            {
                var initialFile = new FileInfo(outputPath);
                length = initialFile.Length;
                lastWriteUtcTicks = initialFile.LastWriteTimeUtc.Ticks;
            }
            catch (Exception ex)
            {
                return Failed($"The encoded output file could not be inspected: {ex.Message}");
            }

            long minimumSize = MinimumPlausibleSize(
                request.ExpectedDurationSeconds ?? ProgramDurationResolver.Resolve(sourceProbe).DurationSeconds);
            if (length < minimumSize)
            {
                return Failed(
                    $"The encoded output is suspiciously small ({length:N0} bytes; " +
                    $"minimum plausible size is {minimumSize:N0} bytes).");
            }

            MediaProbeResult outputProbe = await _probeService.ProbeAsync(
                outputPath,
                cancellationToken).ConfigureAwait(false);
            if (!outputProbe.Success)
            {
                return Failed(
                    $"FFprobe could not read the encoded output: {outputProbe.ErrorMessage}");
            }

            string validationError = ValidateProbe(request, sourceProbe, outputProbe, _log);
            if (!string.IsNullOrWhiteSpace(validationError))
                return Failed(validationError);

            try
            {
                var currentFile = new FileInfo(outputPath);
                if (!currentFile.Exists ||
                    currentFile.Length != length ||
                    currentFile.LastWriteTimeUtc.Ticks != lastWriteUtcTicks)
                {
                    return Failed(
                        "The encoded output changed while MediaFlux was validating it.");
                }
            }
            catch (Exception ex)
            {
                return Failed(
                    $"The encoded output could not be rechecked after validation: {ex.Message}");
            }

            return new EncodeOutputValidationResult
            {
                Success = true,
                Summary =
                    "Essential media structure, stream mapping, codec, format, and duration checks passed.",
                Evidence = new EncodeOutputValidationEvidence
                {
                    SourceProbe = sourceProbe,
                    OutputProbe = outputProbe,
                    OutputSizeBytes = length,
                    OutputLastWriteUtcTicks = lastWriteUtcTicks
                }
            };
        }

        internal static string ValidateProbe(
            EncodeOutputValidationRequest request,
            MediaProbeResult source,
            MediaProbeResult output,
            Action<string>? log = null)
        {
            MediaProbeStreamInfo? sourceVideo = FirstStream(source, "video");
            MediaProbeStreamInfo? outputVideo = FirstStream(output, "video");
            if (sourceVideo == null)
                return "The source analysis does not contain a video stream.";
            if (outputVideo == null)
                return "The encoded output does not contain a video stream.";

            string expectedCodec = request.Encoder.CodecFamily switch
            {
                VideoCodecFamily.H264 => "h264",
                VideoCodecFamily.Hevc => "hevc",
                VideoCodecFamily.Av1 => "av1",
                _ => ""
            };
            if (!outputVideo.CodecName.Equals(
                    expectedCodec,
                    StringComparison.OrdinalIgnoreCase))
            {
                return
                    $"The encoded video codec is '{outputVideo.CodecName}', but " +
                    $"'{expectedCodec}' was requested.";
            }

            if (request.ExpectedVideoWidth is > 0 && request.ExpectedVideoHeight is > 0)
            {
                bool exactDimensions = outputVideo.Width == request.ExpectedVideoWidth && outputVideo.Height == request.ExpectedVideoHeight;
                bool originalRotationApplied = request.ExpectedVideoWidth == sourceVideo.Width && request.ExpectedVideoHeight == sourceVideo.Height && outputVideo.Width == sourceVideo.Height && outputVideo.Height == sourceVideo.Width;
                if (!exactDimensions && !originalRotationApplied)
                    return $"The encoded resolution is {Describe(outputVideo.Width)}×{Describe(outputVideo.Height)}, but {request.ExpectedVideoWidth}×{request.ExpectedVideoHeight} was expected.";
            }
            else
            {
            int? expectedHeight = request.ScaleMode switch
            {
                EncodingService.ScaleMode.To720p => 720,
                EncodingService.ScaleMode.To1080p => 1080,
                EncodingService.ScaleMode.To1440p => 1440,
                EncodingService.ScaleMode.To4K => 2160,
                _ => sourceVideo.Height
            };
            if (request.ScaleMode == EncodingService.ScaleMode.None &&
                sourceVideo.Width is > 0 &&
                sourceVideo.Height is > 0)
            {
                bool exactDimensions =
                    outputVideo.Width == sourceVideo.Width &&
                    outputVideo.Height == sourceVideo.Height;
                bool rotationApplied =
                    outputVideo.Width == sourceVideo.Height &&
                    outputVideo.Height == sourceVideo.Width;
                if (!exactDimensions && !rotationApplied)
                {
                    return
                        $"The encoded resolution is {Describe(outputVideo.Width)}×" +
                        $"{Describe(outputVideo.Height)}, but {sourceVideo.Width}×" +
                        $"{sourceVideo.Height} was expected.";
                }
            }
            else if (expectedHeight is > 0 && outputVideo.Height != expectedHeight)
            {
                return
                    $"The encoded video height is {Describe(outputVideo.Height)}, but " +
                    $"{expectedHeight} was expected.";
            }
            }

            bool outputIsTenBit = IsTenBit(outputVideo);
            if (request.TenBit && !outputIsTenBit)
            {
                return
                    $"{DescribeVideoFormatIntent(request, sourceVideo, outputVideo)} " +
                    "The encoded pixel format is not 10-bit as requested.";
            }
            if (!request.TenBit && outputIsTenBit)
            {
                return
                    $"{DescribeVideoFormatIntent(request, sourceVideo, outputVideo)} " +
                    "The encoded pixel format is not compatible with 8-bit output.";
            }

            string profileError = ValidateRequestedProfile(request, outputVideo);
            if (!string.IsNullOrWhiteSpace(profileError))
                return profileError;

            double? authoritativeDuration = request.ExpectedDurationSeconds ??
                (request.Input.Kind == EncodingInputKind.DvdPhysicalConcat
                    ? request.Input.KnownDurationSeconds
                    : ProgramDurationResolver.Resolve(source).DurationSeconds);
            log?.Invoke($"[EncodeOutputValidation] Duration basis=authoritative video/program timeline {authoritativeDuration?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"}s; output container={output.DurationSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"}s.");
            string durationError = ValidateDuration(
                request.Input.Kind,
                authoritativeDuration ?? request.Input.KnownDurationSeconds,
                ProgramDurationResolver.Resolve(output).DurationSeconds);
            if (!string.IsNullOrWhiteSpace(durationError))
                return durationError;

            if (request.ExpectedVideoFrameCount is > 0)
            {
                MediaProbeStreamInfo? outputVideoForFrames = FirstStream(output, "video");
                if (outputVideoForFrames?.FrameCount is > 0)
                {
                    long delta = outputVideoForFrames.FrameCount.Value - request.ExpectedVideoFrameCount.Value;
                    double fps = sourceVideo.FrameRate is > 0 ? sourceVideo.FrameRate.Value : outputVideoForFrames.FrameRate ?? 0;
                    double deltaSeconds = fps > 0 ? Math.Abs(delta) / fps : double.PositiveInfinity;
                    double allowedSeconds = request.ExpectedVideoFrameCountProvenance == FrameCountProvenance.Measured
                        ? Math.Max(0.75, fps > 0 ? 3d / fps : 0.75)
                        : Math.Max(1.0, fps > 0 ? 4d / fps : 1.0);
                    log?.Invoke($"[EncodeOutputValidation] Frame basis=source {request.ExpectedVideoFrameCount} ({request.ExpectedVideoFrameCountProvenance}); output={outputVideoForFrames.FrameCount}; delta={delta}; time-equivalent={deltaSeconds:0.###}s; allowed={allowedSeconds:0.###}s; duration-basis=authoritative; result={(deltaSeconds <= allowedSeconds ? "accepted" : "rejected")}.");
                    if (deltaSeconds > allowedSeconds)
                        return $"The encoded output contains {outputVideoForFrames.FrameCount} video frames versus {request.ExpectedVideoFrameCount} expected; the {deltaSeconds:0.###}-second frame deficit exceeds the time-aware {allowedSeconds:0.###}-second boundary allowance.";
                }
                else
                    log?.Invoke($"[EncodeOutputValidation] Frame basis=source {request.ExpectedVideoFrameCount} ({request.ExpectedVideoFrameCountProvenance}); output=unavailable; duration validation remains authoritative.");
            }

            int sourceAudioCount = CountStreams(source, "audio");
            int expectedAudioCount = request.Input.HasExplicitStreamSelection
                ? request.Input.AudioStreamIndexes.Count
                : request.MapMode == EncodingService.StreamMapMode.FirstAudioOnly
                    ? Math.Min(1, sourceAudioCount)
                    : sourceAudioCount;
            int outputAudioCount = CountStreams(output, "audio");
            if (outputAudioCount < expectedAudioCount)
            {
                return
                    $"The encoded output contains {outputAudioCount} audio stream(s), but " +
                    $"{expectedAudioCount} were expected from the selected mapping.";
            }

            if (request.AudioChannels is > 0 &&
                output.Streams
                    .Where(stream => IsType(stream, "audio"))
                    .Any(stream => stream.Channels != request.AudioChannels.Value))
            {
                return
                    $"One or more encoded audio streams do not have the requested " +
                    $"{request.AudioChannels.Value} channels.";
            }

            if (request.CopySubtitles)
            {
                int expectedSubtitles = request.ContainerDecision.Resolved == OutputContainer.Mp4 && request.ContainerDecision.StreamPlans.Count > 0
                    ? request.ContainerDecision.StreamPlans.Count(plan =>
                        plan.StreamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase) &&
                        plan.Action is StreamCompatibilityAction.Copy or StreamCompatibilityAction.Transcode)
                    : request.Input.HasExplicitStreamSelection
                    ? request.Input.SubtitleStreamIndexes.Count
                    : CountStreams(source, "subtitle");
                int actualSubtitles = CountStreams(output, "subtitle");
                if (actualSubtitles < expectedSubtitles)
                {
                    return
                        $"The encoded output contains {actualSubtitles} subtitle stream(s), but " +
                        $"{expectedSubtitles} were expected from the selected mapping.";
                }
            }

            if (request.CopyDataStreams)
            {
                int expected = CountStreams(source, "data");
                int actual = CountStreams(output, "data");
                if (actual < expected)
                    return $"The encoded output contains {actual} data stream(s), but {expected} were selected for preservation.";
            }

            if (request.CopyAttachments)
            {
                int expected = CountStreams(source, "attachment");
                int actual = CountStreams(output, "attachment");
                if (actual < expected)
                    return $"The encoded output contains {actual} attachment stream(s), but {expected} were selected for preservation.";
            }

            string chapterError = ValidateChapterPreservation(
                source.Chapters,
                output.Chapters,
                log);
            if (!string.IsNullOrWhiteSpace(chapterError))
                return chapterError;

            if (source.FormatTags.TryGetValue("title", out string? sourceTitle) &&
                !string.IsNullOrWhiteSpace(sourceTitle) &&
                (!output.FormatTags.TryGetValue("title", out string? outputTitle) ||
                 !string.Equals(sourceTitle, outputTitle, StringComparison.Ordinal)))
            {
                return "The encoded output did not preserve the source title metadata.";
            }

            if (!IsExpectedContainer(output.FormatName, request.ContainerDecision.Resolved))
            {
                return
                    $"FFprobe identified the output container as '{output.FormatName}', " +
                    $"not the requested {request.ContainerDecision.Resolved} container.";
            }

            return "";
        }

        private static string ValidateChapterPreservation(
            IReadOnlyList<MediaProbeChapterInfo> sourceChapters,
            IReadOnlyList<MediaProbeChapterInfo> outputChapters,
            Action<string>? log)
        {
            IReadOnlyList<MediaProbeChapterInfo> source = NormalizeChapters(
                sourceChapters,
                "source",
                log);
            IReadOnlyList<MediaProbeChapterInfo> output = NormalizeChapters(
                outputChapters,
                "output",
                log);

            if (source.Count == 0)
                return "";

            if (output.Count != source.Count)
            {
                return
                    $"Meaningful chapter preservation failed: the encoded output contains " +
                    $"{output.Count} meaningful chapter(s), but the source contains " +
                    $"{source.Count}.";
            }

            // FFmpeg may rebase container timestamps (for example, a source
            // beginning at 0.083 seconds becoming 0 in the output). Compare
            // chapter positions in that normalized timeline, but always compare
            // the chapter durations directly.
            double positionOffset = GetChapterPositionOffset(source[0], output[0]);
            for (int index = 0; index < source.Count; index++)
            {
                MediaProbeChapterInfo expected = source[index];
                MediaProbeChapterInfo actual = output[index];
                int chapterNumber = index + 1;

                if (!string.IsNullOrWhiteSpace(expected.Title) &&
                    !string.Equals(
                        expected.Title.Trim(),
                        actual.Title?.Trim() ?? "",
                        StringComparison.Ordinal))
                {
                    return
                        $"Meaningful chapter preservation failed: chapter {chapterNumber} " +
                        $"title changed from '{expected.Title}' to " +
                        $"'{actual.Title ?? ""}'.";
                }

                string timestampError = ValidateChapterTimestamps(
                    expected,
                    actual,
                    chapterNumber,
                    positionOffset);
                if (!string.IsNullOrWhiteSpace(timestampError))
                    return timestampError;
            }

            return "";
        }

        private static double GetChapterPositionOffset(
            MediaProbeChapterInfo source,
            MediaProbeChapterInfo output) =>
            source.StartSeconds is double sourceStart &&
            output.StartSeconds is double outputStart
                ? outputStart - sourceStart
                : 0;

        private static IReadOnlyList<MediaProbeChapterInfo> NormalizeChapters(
            IReadOnlyList<MediaProbeChapterInfo> chapters,
            string side,
            Action<string>? log)
        {
            var meaningful = new List<MediaProbeChapterInfo>(chapters.Count);
            foreach (MediaProbeChapterInfo chapter in chapters)
            {
                if (IsDegenerateChapter(chapter, out string reason))
                {
                    log?.Invoke(
                        $"[EncodeOutputValidation] Ignored malformed/degenerate {side} " +
                        $"chapter {chapter.Id} ({FormatChapterRange(chapter)}): {reason}.");
                    continue;
                }

                meaningful.Add(chapter);
            }

            return meaningful;
        }

        private static bool IsDegenerateChapter(
            MediaProbeChapterInfo chapter,
            out string reason)
        {
            if (chapter.StartSeconds is not double start ||
                chapter.EndSeconds is not double end ||
                !double.IsFinite(start) ||
                !double.IsFinite(end))
            {
                reason = "chapter timestamps are unavailable";
                return false;
            }

            double duration = end - start;
            if (duration <= 0)
            {
                reason = "end is not after start";
                return true;
            }

            if (duration < MinimumMeaningfulChapterDurationSeconds)
            {
                reason =
                    $"duration {duration:0.######} seconds is below the " +
                    $"{MinimumMeaningfulChapterDurationSeconds * 1000:0} ms tolerance";
                return true;
            }

            reason = "";
            return false;
        }

        private static string ValidateChapterTimestamps(
            MediaProbeChapterInfo expected,
            MediaProbeChapterInfo actual,
            int chapterNumber,
            double positionOffset)
        {
            if (expected.StartSeconds is not double expectedStart ||
                expected.EndSeconds is not double expectedEnd)
            {
                return "";
            }

            if (actual.StartSeconds is not double actualStart ||
                actual.EndSeconds is not double actualEnd)
            {
                return
                    $"Meaningful chapter preservation failed: chapter {chapterNumber} " +
                    "timestamps are missing from the encoded output.";
            }

            double positionDifference = Math.Abs(
                actualStart - (expectedStart + positionOffset));
            double durationDifference = Math.Abs(
                (actualEnd - actualStart) - (expectedEnd - expectedStart));
            if (positionDifference > ChapterTimestampToleranceSeconds ||
                durationDifference > ChapterTimestampToleranceSeconds)
            {
                return
                    $"Meaningful chapter preservation failed: chapter {chapterNumber} " +
                    $"position changed by {positionDifference:0.###} seconds and duration " +
                    $"changed by {durationDifference:0.###} seconds (allowed " +
                    $"{ChapterTimestampToleranceSeconds:0.###} seconds for mux timestamp " +
                    "rebasing/rounding).";
            }

            return "";
        }

        private static string FormatChapterRange(MediaProbeChapterInfo chapter) =>
            $"{chapter.StartSeconds?.ToString("0.######", CultureInfo.InvariantCulture) ?? "unknown"}" +
            $"-{chapter.EndSeconds?.ToString("0.######", CultureInfo.InvariantCulture) ?? "unknown"}";

        internal static long MinimumPlausibleSize(double? durationSeconds)
        {
            if (durationSeconds is >= 60)
                return 64 * 1024;
            if (durationSeconds is >= 10)
                return 16 * 1024;
            return 1024;
        }

        private static string ValidateDuration(
            EncodingInputKind inputKind,
            double? sourceDuration,
            double? outputDuration)
        {
            if (sourceDuration is not > 0)
                return "";
            if (outputDuration is not > 0)
                return "FFprobe could not determine the encoded output duration.";

            double difference = Math.Abs(outputDuration.Value - sourceDuration.Value);
            double allowed = inputKind == EncodingInputKind.DvdPhysicalConcat
                ? Math.Max(5, sourceDuration.Value * 0.10)
                : Math.Max(2, sourceDuration.Value * 0.02);
            return difference > allowed
                ? $"The encoded output duration differs from the source by " +
                  $"{difference:0.##} seconds (allowed {allowed:0.##} seconds)."
                : "";
        }

        private static string GetSourceProbePath(EncodingInputSource input) =>
            input.Kind == EncodingInputKind.File
                ? input.SourcePath
                : input.SourceFiles.FirstOrDefault() ?? input.SourcePath;

        private static bool IsReadableMp4(string formatName) =>
            (formatName ?? "")
                .Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .Any(value =>
                    value.Equals("mov", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("mp4", StringComparison.OrdinalIgnoreCase));

        private static bool IsExpectedContainer(string formatName, OutputContainer container) =>
            container == OutputContainer.Matroska
                ? (formatName ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(value => value.Equals("matroska", StringComparison.OrdinalIgnoreCase) ||
                                  value.Equals("webm", StringComparison.OrdinalIgnoreCase))
                : IsReadableMp4(formatName);

        private static bool IsTenBit(MediaProbeStreamInfo stream) =>
            stream.BitsPerRawSample is >= 10 ||
            stream.PixelFormat.Contains("10", StringComparison.OrdinalIgnoreCase) ||
            stream.PixelFormat.StartsWith("p010", StringComparison.OrdinalIgnoreCase) ||
            stream.PixelFormat.StartsWith("p210", StringComparison.OrdinalIgnoreCase);

        private static MediaProbeStreamInfo? FirstStream(
            MediaProbeResult probe,
            string type) =>
            probe.Streams.FirstOrDefault(stream => IsType(stream, type));

        private static int CountStreams(MediaProbeResult probe, string type) =>
            probe.Streams.Count(stream => IsType(stream, type));

        private static bool IsType(MediaProbeStreamInfo stream, string type) =>
            stream.CodecType.Equals(type, StringComparison.OrdinalIgnoreCase);

        private static string Describe(int? value) =>
            value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

        private static string DescribeVideoFormatIntent(
            EncodeOutputValidationRequest request,
            MediaProbeStreamInfo source,
            MediaProbeStreamInfo actual) =>
            $"Requested: {request.Encoder.CodecFamily} " +
            $"{(request.TenBit ? "10-bit" : "8-bit")}; " +
            $"Source: {source.CodecName} {source.Profile} / {source.PixelFormat}; " +
            $"Encoder: {request.Encoder.FfmpegCodec}; " +
            $"Actual: {actual.CodecName} {actual.Profile} / {actual.PixelFormat}.";

        private static string ValidateRequestedProfile(
            EncodeOutputValidationRequest request,
            MediaProbeStreamInfo output)
        {
            if (string.IsNullOrWhiteSpace(output.Profile))
                return "";

            string? expected = request.Encoder.CodecFamily switch
            {
                VideoCodecFamily.Hevc => request.TenBit ? "main10" : "main",
                VideoCodecFamily.H264 => "high",
                _ => null
            };
            if (expected == null)
                return "";

            string actual = output.Profile.Replace(" ", "", StringComparison.Ordinal)
                .Trim();
            return actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
                ? ""
                : $"The encoded video profile '{output.Profile}' is not the expected " +
                  $"{expected} profile for the requested output.";
        }

        private static EncodeOutputValidationResult Failed(string message) => new()
        {
            Success = false,
            ErrorMessage = message
        };
    }
}
