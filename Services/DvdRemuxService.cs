using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaFlux.Models;

namespace MediaFlux.Services
{
    public sealed class DvdRemuxService
    {
        private static readonly Regex FailedStreamPattern = new(
            @"stream\s+#?0:(?<index>\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly string _ffmpegPath;
        private readonly IMediaToolProcessRunner _processRunner;
        private readonly IDvdOutputValidationService _validationService;
        private readonly Action<string>? _diagnosticLog;

        public DvdRemuxService(
            string applicationDirectory,
            string? configuredFfmpegPath,
            IDvdOutputValidationService validationService,
            Action<string>? diagnosticLog = null,
            IMediaToolProcessRunner? processRunner = null)
            : this(
                FfmpegToolResolver.Resolve(
                    applicationDirectory,
                    configuredFfmpegPath: configuredFfmpegPath).FfmpegPath,
                processRunner ?? new MediaToolProcessRunner(),
                validationService,
                diagnosticLog)
        {
        }

        public DvdRemuxService(
            string ffmpegPath,
            IMediaToolProcessRunner processRunner,
            IDvdOutputValidationService validationService,
            Action<string>? diagnosticLog = null)
        {
            _ffmpegPath = ffmpegPath;
            _processRunner = processRunner ??
                throw new ArgumentNullException(nameof(processRunner));
            _validationService = validationService ??
                throw new ArgumentNullException(nameof(validationService));
            _diagnosticLog = diagnosticLog;
        }

        public async Task<DvdRemuxResult> RemuxAsync(
            DvdImportOptions options,
            IProgress<DvdOperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(options.Candidate);
            if (options.OutputMode != DvdOutputMode.LosslessRemuxToMkv)
                return Failed("The selected DVD operation is not a lossless remux.");
            if (!options.Candidate.IsValidForConversion)
                return Failed("The selected DVD title has analysis errors and cannot be remuxed safely.");
            if (!File.Exists(_ffmpegPath))
                return Failed($"FFmpeg was not found at '{_ffmpegPath}'.");

            string finalOutputPath;
            try
            {
                finalOutputPath = OutputPathService.EnsureMkvExtension(options.OutputPath);
            }
            catch (Exception ex)
            {
                return Failed($"The output path is invalid: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(finalOutputPath))
                return Failed("Choose an output filename before starting the remux.");

            string sourceDirectory = Path.GetDirectoryName(options.Candidate.Segments[0].Path) ?? "";
            if (OutputPathService.IsPathWithinDirectory(finalOutputPath, sourceDirectory))
            {
                return Failed(
                    "The output cannot be written inside the source VIDEO_TS folder. " +
                    "Choose its parent folder or another destination.");
            }

            if (File.Exists(finalOutputPath) && !options.OverwriteExistingOutput)
                return Failed("The requested output file already exists.");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(finalOutputPath)!);
            }
            catch (Exception ex)
            {
                return Failed($"The output folder could not be created: {ex.Message}");
            }

            string stagingPath = OutputPathService.CreateStagingPath(finalOutputPath);
            string diagnosticCommand = "";
            string diagnosticOutput = "";
            DvdRemuxResult? operationResult = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new DvdOperationProgress
                {
                    Status = "Preparing DVD segments",
                    Percent = 0,
                    TotalDuration = TimeSpan.FromSeconds(options.Candidate.CombinedDurationSeconds)
                });

                DvdPhysicalInput physicalInput =
                    DvdPhysicalInputBuilder.Create(options.Candidate);
                var streamSelection = BuildSelectedStreams(options);
                if (streamSelection.Video == null)
                {
                    operationResult = Failed(
                        "The selected DVD title does not contain a video stream.");
                    return operationResult;
                }

                var arguments = BuildArguments(
                    physicalInput.InputUrl,
                    stagingPath,
                    streamSelection);
                diagnosticCommand = FormatCommand(_ffmpegPath, arguments);
                _diagnosticLog?.Invoke($"[DvdRemuxService] {diagnosticCommand}");

                var progressState = new RemuxProgressState(
                    options.Candidate.CombinedDurationSeconds,
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
                    (int? sourceIndex, string description) = IdentifyFailedStream(
                        diagnosticOutput,
                        streamSelection);
                    string error =
                        "Remuxing failed. MediaFlux did not automatically re-encode the video.";
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        error += $" FFmpeg reported a problem with {description}. " +
                                 "Return to DVD Title Selection to exclude that optional stream " +
                                 "or choose normal encoding.";
                    }
                    else
                    {
                        string detail = LastUsefulLine(diagnosticOutput);
                        if (!string.IsNullOrWhiteSpace(detail))
                            error += $" FFmpeg reported: {detail}";
                    }

                    _diagnosticLog?.Invoke($"[DvdRemuxService] {error}{Environment.NewLine}{diagnosticOutput}");
                    operationResult = new DvdRemuxResult
                    {
                        Success = false,
                        ErrorMessage = error,
                        FailedSourceStreamIndex = sourceIndex,
                        FailedStreamDescription = description,
                        DiagnosticCommand = diagnosticCommand,
                        DiagnosticOutput = diagnosticOutput
                    };
                    return operationResult;
                }

                progress?.Report(new DvdOperationProgress
                {
                    Status = "Verifying completed output",
                    Percent = 99,
                    TotalDuration = TimeSpan.FromSeconds(options.Candidate.CombinedDurationSeconds)
                });
                DvdOutputValidationResult validation = await _validationService.ValidateAsync(
                    stagingPath,
                    options.Candidate,
                    streamSelection.Audio.Count,
                    streamSelection.Subtitles.Count,
                    cancellationToken).ConfigureAwait(false);
                if (!validation.Success)
                {
                    string error =
                        $"The output was created but failed validation: {validation.ErrorMessage}";
                    _diagnosticLog?.Invoke($"[DvdRemuxService] {error}");
                    operationResult = new DvdRemuxResult
                    {
                        Success = false,
                        ErrorMessage = error,
                        DiagnosticCommand = diagnosticCommand,
                        DiagnosticOutput = diagnosticOutput
                    };
                    return operationResult;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(finalOutputPath) && !options.OverwriteExistingOutput)
                {
                    operationResult = Failed(
                        "The requested output file was created by another process.");
                    return operationResult;
                }

                File.Move(
                    stagingPath,
                    finalOutputPath,
                    overwrite: options.OverwriteExistingOutput);
                progress?.Report(new DvdOperationProgress
                {
                    Status = "DVD remux complete",
                    Percent = 100,
                    CurrentTime = TimeSpan.FromSeconds(options.Candidate.CombinedDurationSeconds),
                    TotalDuration = TimeSpan.FromSeconds(options.Candidate.CombinedDurationSeconds)
                });
                operationResult = new DvdRemuxResult
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
                operationResult = new DvdRemuxResult
                {
                    Success = false,
                    WasCanceled = true,
                    ErrorMessage =
                        "The operation was canceled. MediaFlux did not modify the DVD source.",
                    DiagnosticCommand = diagnosticCommand,
                    DiagnosticOutput = diagnosticOutput
                };
                return operationResult;
            }
            catch (Exception ex)
            {
                string error =
                    $"DVD remuxing failed: {ex.Message} MediaFlux did not automatically re-encode the video.";
                _diagnosticLog?.Invoke($"[DvdRemuxService] {error}");
                operationResult = new DvdRemuxResult
                {
                    Success = false,
                    ErrorMessage = error,
                    DiagnosticCommand = diagnosticCommand,
                    DiagnosticOutput = diagnosticOutput
                };
                return operationResult;
            }
            finally
            {
                progress?.Report(new DvdOperationProgress
                {
                    Status = "Cleaning incomplete output"
                });
                var cleanupErrors = new List<string>();
                string stagingCleanupError = TryDeleteStagingFile(stagingPath);
                if (!string.IsNullOrWhiteSpace(stagingCleanupError))
                {
                    cleanupErrors.Add(
                        $"Incomplete output: {stagingCleanupError}");
                    _diagnosticLog?.Invoke(
                        $"[DvdRemuxService] Incomplete output cleanup failed: " +
                        stagingCleanupError);
                }

                if (operationResult != null)
                {
                    operationResult.CleanupSucceeded = cleanupErrors.Count == 0;
                    operationResult.CleanupMessage = cleanupErrors.Count == 0
                        ? "No incomplete output remains."
                        : string.Join(" ", cleanupErrors);
                }
            }
        }

        private static SelectedStreams BuildSelectedStreams(DvdImportOptions options)
        {
            MediaProbeResult? representative = options.Candidate.Segments
                .Select(segment => segment.ProbeResult)
                .FirstOrDefault(probe => probe?.Success == true);
            if (representative == null)
                return new SelectedStreams();

            MediaProbeStreamInfo? video = representative.Streams.FirstOrDefault(stream =>
                stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
            var selectedAudio = options.SelectedAudioStreamIndexes.ToHashSet();
            var selectedSubtitles = options.SelectedSubtitleStreamIndexes.ToHashSet();
            return new SelectedStreams
            {
                Video = video == null ? null : ToInputStream(video),
                Audio = representative.Streams
                    .Where(stream =>
                        stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase) &&
                        selectedAudio.Contains(stream.Index))
                    .Select(ToInputStream)
                    .ToList(),
                Subtitles = representative.Streams
                    .Where(stream =>
                        stream.CodecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase) &&
                        selectedSubtitles.Contains(stream.Index))
                    .Select(ToInputStream)
                    .ToList()
            };
        }

        private static MappedStream ToInputStream(MediaProbeStreamInfo stream) =>
            new(stream, stream.Index);

        private static IReadOnlyList<string> BuildArguments(
            string inputUrl,
            string stagingPath,
            SelectedStreams streams)
        {
            var arguments = new List<string>
            {
                "-hide_banner",
                "-y",
                "-progress", "pipe:1",
                "-stats_period", "0.5",
                "-nostats",
                "-fflags", "+genpts",
                "-i", inputUrl
            };

            AddMap(arguments, streams.Video!);
            foreach (MappedStream stream in streams.Audio)
                AddMap(arguments, stream);
            foreach (MappedStream stream in streams.Subtitles)
                AddMap(arguments, stream);

            arguments.AddRange(new[]
            {
                "-dn",
                "-c", "copy",
                "-map_metadata", "0",
                "-map_chapters", "0",
                "-avoid_negative_ts", "make_zero",
                stagingPath
            });
            return arguments;
        }

        private static void AddMap(List<string> arguments, MappedStream stream)
        {
            arguments.Add("-map");
            arguments.Add($"0:{stream.InputIndex}");
        }

        private static (int? SourceIndex, string Description) IdentifyFailedStream(
            string diagnosticOutput,
            SelectedStreams selection)
        {
            Match match = FailedStreamPattern.Match(diagnosticOutput ?? "");
            if (!match.Success ||
                !int.TryParse(
                    match.Groups["index"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int inputIndex))
            {
                return (null, "");
            }

            MappedStream? stream = selection.All.FirstOrDefault(item =>
                item.InputIndex == inputIndex);
            if (stream == null)
                return (null, $"stream 0:{inputIndex}");

            string language = string.IsNullOrWhiteSpace(stream.Source.Language)
                ? ""
                : $", language {stream.Source.Language}";
            return (
                stream.Source.Index,
                $"{stream.Source.CodecType} stream {stream.Source.Index} " +
                $"({stream.Source.CodecName}{language})");
        }

        private static string FormatCommand(string executable, IReadOnlyList<string> arguments)
        {
            return string.Join(
                " ",
                new[] { QuoteForDisplay(executable) }
                    .Concat(arguments.Select(QuoteForDisplay)));
        }

        private static string QuoteForDisplay(string value)
        {
            if (value.Length > 0 &&
                value.All(character => !char.IsWhiteSpace(character) && character != '"'))
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        private static string LastUsefulLine(string value)
        {
            return value
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
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
                   "The incomplete output file still exists after three cleanup attempts.";
        }

        private static DvdRemuxResult Failed(string message) => new()
        {
            Success = false,
            ErrorMessage = message
        };

        private sealed class SelectedStreams
        {
            public MappedStream? Video { get; init; }
            public List<MappedStream> Audio { get; init; } = new();
            public List<MappedStream> Subtitles { get; init; } = new();
            public IEnumerable<MappedStream> All =>
                Video == null
                    ? Audio.Concat(Subtitles)
                    : new[] { Video }.Concat(Audio).Concat(Subtitles);
        }

        private sealed record MappedStream(MediaProbeStreamInfo Source, int InputIndex);

        private sealed class RemuxProgressState
        {
            private readonly double _totalSeconds;
            private readonly IProgress<DvdOperationProgress>? _progress;

            public RemuxProgressState(
                double totalSeconds,
                IProgress<DvdOperationProgress>? progress)
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
                    ? Math.Clamp(currentSeconds / _totalSeconds * 100d, 0, 98)
                    : null;
                _progress?.Report(new DvdOperationProgress
                {
                    Status = "Combining DVD title",
                    Percent = percent,
                    CurrentTime = TimeSpan.FromSeconds(Math.Max(0, currentSeconds)),
                    TotalDuration = _totalSeconds > 0
                        ? TimeSpan.FromSeconds(_totalSeconds)
                        : null
                });
            }
        }
    }
}
