using System.Globalization;
using MediaFlux.Models;

namespace MediaFlux.Services
{
    public sealed class MediaRemuxService
    {
        private readonly string _ffmpegPath;
        private readonly IMediaToolProcessRunner _processRunner;
        private readonly IMediaProbeService _probeService;
        private readonly Action<string>? _diagnosticLog;

        public MediaRemuxService(
            string applicationDirectory,
            string? configuredFfmpegPath,
            string? configuredFfprobePath,
            Action<string>? diagnosticLog = null,
            IMediaToolProcessRunner? processRunner = null,
            IMediaProbeService? probeService = null)
            : this(
                FfmpegToolResolver.Resolve(
                    applicationDirectory,
                    configuredFfmpegPath,
                    configuredFfprobePath).FfmpegPath,
                processRunner ?? new MediaToolProcessRunner(),
                probeService ?? new FfprobeService(
                    applicationDirectory,
                    configuredFfprobePath),
                diagnosticLog)
        {
        }

        internal MediaRemuxService(
            string ffmpegPath,
            IMediaToolProcessRunner processRunner,
            IMediaProbeService probeService,
            Action<string>? diagnosticLog = null)
        {
            _ffmpegPath = ffmpegPath;
            _processRunner = processRunner ??
                throw new ArgumentNullException(nameof(processRunner));
            _probeService = probeService ??
                throw new ArgumentNullException(nameof(probeService));
            _diagnosticLog = diagnosticLog;
        }

        public async Task<MediaRemuxResult> RemuxAsync(
            MediaRemuxRequest request,
            IProgress<MediaRemuxProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!File.Exists(request.SourcePath))
                return Failed("The source file no longer exists.");
            if (!File.Exists(_ffmpegPath))
                return Failed($"FFmpeg was not found at '{_ffmpegPath}'.");

            string sourcePath;
            string finalOutputPath;
            try
            {
                sourcePath = Path.GetFullPath(request.SourcePath);
                finalOutputPath = OutputPathService.EnsureMkvExtension(request.OutputPath);
            }
            catch (Exception ex)
            {
                return Failed($"The source or output path is invalid: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(finalOutputPath))
                return Failed("Choose an output filename before starting the remux.");
            if (sourcePath.Equals(finalOutputPath, StringComparison.OrdinalIgnoreCase))
                return Failed("The remux output must not replace the source file.");
            if (File.Exists(finalOutputPath) && !request.OverwriteExistingOutput)
                return Failed("The requested output file already exists.");

            try
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(finalOutputPath) ??
                    throw new InvalidOperationException(
                        "The output folder could not be determined."));
            }
            catch (Exception ex)
            {
                return Failed($"The output folder could not be created: {ex.Message}");
            }

            progress?.Report(new MediaRemuxProgress
            {
                Status = "Inspecting source streams",
                Percent = 0
            });
            MediaProbeResult sourceProbe;
            try
            {
                sourceProbe = await _probeService.ProbeAsync(
                    sourcePath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new MediaRemuxResult
                {
                    Success = false,
                    WasCanceled = true,
                    ErrorMessage =
                        "The remux was canceled before output creation. MediaFlux did not modify the source."
                };
            }
            catch (Exception ex)
            {
                return Failed($"FFprobe could not inspect the source: {ex.Message}");
            }
            if (!sourceProbe.Success)
            {
                return Failed(
                    $"FFprobe could not inspect the source before remuxing: " +
                    sourceProbe.ErrorMessage);
            }
            if (!sourceProbe.Streams.Any(stream =>
                    stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase)))
            {
                return Failed("The source does not contain a video stream.");
            }

            string stagingPath = OutputPathService.CreateStagingPath(finalOutputPath);
            string diagnosticCommand = "";
            string diagnosticOutput = "";
            MediaRemuxResult? operationResult = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<string> arguments = BuildArguments(
                    sourcePath,
                    stagingPath);
                diagnosticCommand = FormatCommand(_ffmpegPath, arguments);
                _diagnosticLog?.Invoke($"[MediaRemuxService] {diagnosticCommand}");

                var progressState = new RemuxProgressState(
                    sourceProbe.DurationSeconds ?? 0,
                    progress);
                MediaToolProcessResult processResult = await _processRunner.RunAsync(
                    new MediaToolProcessRequest
                    {
                        FileName = _ffmpegPath,
                        Arguments = arguments,
                        Timeout = Timeout.InfiniteTimeSpan,
                        SendQuitOnCancellation = true,
                        StandardOutputLineCallback = progressState.HandleLine
                    },
                    cancellationToken).ConfigureAwait(false);
                diagnosticOutput = processResult.StandardError;
                if (processResult.ExitCode != 0)
                {
                    string detail = LastUsefulLine(diagnosticOutput);
                    string error =
                        "Stream-copy remuxing failed. MediaFlux did not re-encode or modify the source.";
                    if (!string.IsNullOrWhiteSpace(detail))
                        error += $" FFmpeg reported: {detail}";

                    operationResult = new MediaRemuxResult
                    {
                        Success = false,
                        ErrorMessage = error,
                        DiagnosticCommand = diagnosticCommand,
                        DiagnosticOutput = diagnosticOutput
                    };
                    return operationResult;
                }

                progress?.Report(new MediaRemuxProgress
                {
                    Status = "Verifying remuxed output",
                    Percent = 99,
                    TotalDuration = sourceProbe.DurationSeconds is > 0
                        ? TimeSpan.FromSeconds(sourceProbe.DurationSeconds.Value)
                        : null
                });
                MediaProbeResult outputProbe = await _probeService.ProbeAsync(
                    stagingPath,
                    cancellationToken).ConfigureAwait(false);
                string validationError = ValidateOutput(sourceProbe, outputProbe);
                if (!string.IsNullOrWhiteSpace(validationError))
                {
                    operationResult = new MediaRemuxResult
                    {
                        Success = false,
                        ErrorMessage =
                            $"The staged remux failed validation: {validationError}",
                        DiagnosticCommand = diagnosticCommand,
                        DiagnosticOutput = diagnosticOutput
                    };
                    return operationResult;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(finalOutputPath) && !request.OverwriteExistingOutput)
                {
                    operationResult = Failed(
                        "The requested output file was created by another process.");
                    return operationResult;
                }

                File.Move(
                    stagingPath,
                    finalOutputPath,
                    overwrite: request.OverwriteExistingOutput);
                progress?.Report(new MediaRemuxProgress
                {
                    Status = "Remux complete",
                    Percent = 100,
                    CurrentTime = sourceProbe.DurationSeconds is > 0
                        ? TimeSpan.FromSeconds(sourceProbe.DurationSeconds.Value)
                        : null,
                    TotalDuration = sourceProbe.DurationSeconds is > 0
                        ? TimeSpan.FromSeconds(sourceProbe.DurationSeconds.Value)
                        : null
                });
                operationResult = new MediaRemuxResult
                {
                    Success = true,
                    OutputPath = finalOutputPath,
                    DiagnosticCommand = diagnosticCommand,
                    DiagnosticOutput = diagnosticOutput
                };
                return operationResult;
            }
            catch (OperationCanceledException)
            {
                operationResult = new MediaRemuxResult
                {
                    Success = false,
                    WasCanceled = true,
                    ErrorMessage =
                        "The remux was canceled. MediaFlux did not modify the source.",
                    DiagnosticCommand = diagnosticCommand,
                    DiagnosticOutput = diagnosticOutput
                };
                return operationResult;
            }
            catch (Exception ex)
            {
                operationResult = new MediaRemuxResult
                {
                    Success = false,
                    ErrorMessage =
                        $"Remuxing failed: {ex.Message} MediaFlux did not modify the source.",
                    DiagnosticCommand = diagnosticCommand,
                    DiagnosticOutput = diagnosticOutput
                };
                return operationResult;
            }
            finally
            {
                string cleanupError = TryDeleteStagingFile(stagingPath);
                if (operationResult != null)
                {
                    operationResult.CleanupSucceeded =
                        string.IsNullOrWhiteSpace(cleanupError);
                    operationResult.CleanupMessage =
                        string.IsNullOrWhiteSpace(cleanupError)
                            ? "No incomplete output remains."
                            : cleanupError;
                }
            }
        }

        internal static IReadOnlyList<string> BuildArguments(
            string sourcePath,
            string stagingPath)
        {
            return new[]
            {
                "-hide_banner",
                "-y",
                "-progress", "pipe:1",
                "-stats_period", "0.5",
                "-nostats",
                "-fflags", "+genpts",
                "-i", sourcePath,
                "-map", "0:v?",
                "-map", "0:a?",
                "-map", "0:s?",
                "-map", "0:t?",
                "-c", "copy",
                "-map_metadata", "0",
                "-map_chapters", "0",
                "-avoid_negative_ts", "make_zero",
                stagingPath
            };
        }

        internal static string ValidateOutput(
            MediaProbeResult source,
            MediaProbeResult output)
        {
            if (!output.Success)
                return $"FFprobe could not read the completed output: {output.ErrorMessage}";

            foreach (string streamType in new[]
                     {
                         "video", "audio", "subtitle", "attachment"
                     })
            {
                string[] expected = CodecsForType(source, streamType);
                string[] actual = CodecsForType(output, streamType);
                if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
                {
                    return
                        $"The {streamType} stream set changed. Expected " +
                        $"{DescribeCodecs(expected)}, found {DescribeCodecs(actual)}.";
                }
            }

            if (source.Chapters.Count > 0 &&
                output.Chapters.Count < source.Chapters.Count)
            {
                return
                    $"The output contains {output.Chapters.Count} chapter(s), but " +
                    $"the source contains {source.Chapters.Count}.";
            }

            if (source.DurationSeconds is > 0)
            {
                if (output.DurationSeconds is not > 0)
                    return "FFprobe could not determine the completed output duration.";

                double difference = Math.Abs(
                    output.DurationSeconds.Value - source.DurationSeconds.Value);
                double allowedDifference = Math.Max(
                    2,
                    source.DurationSeconds.Value * 0.02);
                if (difference > allowedDifference)
                {
                    return
                        $"The output duration differs from the source by " +
                        $"{difference:0.##} seconds.";
                }
            }

            return "";
        }

        private static string[] CodecsForType(
            MediaProbeResult probe,
            string streamType)
        {
            return probe.Streams
                .Where(stream => stream.CodecType.Equals(
                    streamType,
                    StringComparison.OrdinalIgnoreCase))
                .Select(stream => stream.CodecName ?? "")
                .OrderBy(codec => codec, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string DescribeCodecs(IReadOnlyList<string> codecs)
        {
            return codecs.Count == 0
                ? "none"
                : string.Join(", ", codecs);
        }

        private static string FormatCommand(
            string executable,
            IReadOnlyList<string> arguments)
        {
            return string.Join(
                " ",
                new[] { executable }
                    .Concat(arguments)
                    .Select(QuoteForDisplay));
        }

        private static string QuoteForDisplay(string value)
        {
            if (value.Length > 0 &&
                value.All(character =>
                    !char.IsWhiteSpace(character) &&
                    character != '"'))
            {
                return value;
            }

            return "\"" +
                   value.Replace("\"", "\\\"", StringComparison.Ordinal) +
                   "\"";
        }

        private static string LastUsefulLine(string value)
        {
            return (value ?? "")
                .Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .LastOrDefault(line => line.Length > 0) ?? "";
        }

        private static string TryDeleteStagingFile(string stagingPath)
        {
            const int attempts = 3;
            Exception? lastError = null;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    if (!File.Exists(stagingPath))
                        return "";

                    File.Delete(stagingPath);
                    if (!File.Exists(stagingPath))
                        return "";
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                if (attempt < attempts)
                    Thread.Sleep(100 * attempt);
            }

            return lastError?.Message ??
                   "The incomplete output still exists after three cleanup attempts.";
        }

        private static MediaRemuxResult Failed(string message) => new()
        {
            Success = false,
            ErrorMessage = message
        };

        private sealed class RemuxProgressState
        {
            private readonly double _totalSeconds;
            private readonly IProgress<MediaRemuxProgress>? _progress;

            public RemuxProgressState(
                double totalSeconds,
                IProgress<MediaRemuxProgress>? progress)
            {
                _totalSeconds = totalSeconds;
                _progress = progress;
            }

            public void HandleLine(string line)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(
                        line["out_time_us=".Length..],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long microseconds))
                {
                    Report(microseconds / 1_000_000d);
                    return;
                }

                if (line.StartsWith("out_time=", StringComparison.Ordinal) &&
                    TimeSpan.TryParse(
                        line["out_time=".Length..],
                        CultureInfo.InvariantCulture,
                        out TimeSpan time))
                {
                    Report(time.TotalSeconds);
                }
            }

            private void Report(double currentSeconds)
            {
                double? percent = _totalSeconds > 0
                    ? Math.Clamp(currentSeconds / _totalSeconds * 100d, 0, 99)
                    : null;
                _progress?.Report(new MediaRemuxProgress
                {
                    Status = "Remuxing streams without re-encoding",
                    Percent = percent,
                    CurrentTime = TimeSpan.FromSeconds(
                        Math.Max(0, currentSeconds)),
                    TotalDuration = _totalSeconds > 0
                        ? TimeSpan.FromSeconds(_totalSeconds)
                        : null
                });
            }
        }
    }
}
