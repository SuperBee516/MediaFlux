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
        private readonly IMediaProbeService _probeService;
        private readonly IDecodeIntegritySpotCheckService _decodeIntegrityService;

        public EncodeOutputValidationService(
            IMediaProbeService probeService,
            IDecodeIntegritySpotCheckService decodeIntegrityService)
        {
            _probeService = probeService ??
                throw new ArgumentNullException(nameof(probeService));
            _decodeIntegrityService = decodeIntegrityService ??
                throw new ArgumentNullException(nameof(decodeIntegrityService));
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
                    mediaValidation.Evidence!.OutputProbe.DurationSeconds,
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
            try
            {
                length = new FileInfo(outputPath).Length;
            }
            catch (Exception ex)
            {
                return Failed($"The encoded output file could not be inspected: {ex.Message}");
            }

            long minimumSize = MinimumPlausibleSize(sourceProbe.DurationSeconds);
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

            string validationError = ValidateProbe(request, sourceProbe, outputProbe);
            if (!string.IsNullOrWhiteSpace(validationError))
                return Failed(validationError);

            return new EncodeOutputValidationResult
            {
                Success = true,
                Summary =
                    "Essential media structure, stream mapping, codec, format, and duration checks passed.",
                Evidence = new EncodeOutputValidationEvidence
                {
                    SourceProbe = sourceProbe,
                    OutputProbe = outputProbe,
                    OutputSizeBytes = length
                }
            };
        }

        internal static string ValidateProbe(
            EncodeOutputValidationRequest request,
            MediaProbeResult source,
            MediaProbeResult output)
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

            bool outputIsTenBit = IsTenBit(outputVideo);
            if (request.TenBit && !outputIsTenBit)
            {
                return
                    $"The encoded pixel format '{outputVideo.PixelFormat}' is not 10-bit as requested.";
            }
            if (!request.TenBit && outputIsTenBit)
            {
                return
                    $"The encoded pixel format '{outputVideo.PixelFormat}' is not compatible with 8-bit output.";
            }

            string durationError = ValidateDuration(
                request.Input.Kind,
                request.Input.Kind == EncodingInputKind.DvdPhysicalConcat
                    ? request.Input.KnownDurationSeconds ??
                      source.DurationSeconds
                    : source.DurationSeconds ??
                      request.Input.KnownDurationSeconds,
                output.DurationSeconds);
            if (!string.IsNullOrWhiteSpace(durationError))
                return durationError;

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
                int expectedSubtitles = request.Input.HasExplicitStreamSelection
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

            if (source.Chapters.Count > 0 &&
                output.Chapters.Count < source.Chapters.Count)
            {
                return
                    $"The encoded output contains {output.Chapters.Count} chapter(s), but " +
                    $"the source contains {source.Chapters.Count}.";
            }

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

        private static EncodeOutputValidationResult Failed(string message) => new()
        {
            Success = false,
            ErrorMessage = message
        };
    }
}
