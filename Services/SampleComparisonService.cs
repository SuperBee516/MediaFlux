using System.Diagnostics;
using System.Globalization;
using System.Text;
using MediaFlux.Models;
using MediaFlux.Services.Encoders;

namespace MediaFlux.Services
{
    public sealed class SampleComparisonSettings
    {
        public VideoEncoderSelection? Encoder { get; init; }
        public string VideoCodec { get; init; } = "libx265";
        public bool UseGpu { get; init; }
        public double? ProjectedTargetMb { get; init; }
        public EncodingService.ScaleMode ScaleMode { get; init; }
        public string EncoderPreset { get; init; } = string.Empty;
        public int QualityValue { get; init; } = 24;
        public bool TenBit { get; init; }
        public int? AudioChannels { get; init; }
        public double AdditionalMappedBitrateKbps { get; init; }
        public int ClipSeconds { get; init; } = 25;
        public VideoRestorationSettings Restoration { get; init; } = new();
    }

    public sealed class SampleComparisonClip
    {
        public string Label { get; init; } = string.Empty;
        public TimeSpan Start { get; init; }
        public string OriginalPath { get; init; } = string.Empty;
        public string EncodedPath { get; init; } = string.Empty;
        public string ComparisonPath { get; init; } = string.Empty;
    }

    public sealed class SampleComparisonResult : IDisposable
    {
        private readonly string _workingFolder;

        internal SampleComparisonResult(
            string workingFolder,
            IReadOnlyList<SampleComparisonClip> clips,
            double projectedFinalMb,
            double projectedLowerMb,
            double projectedUpperMb,
            double averageBitrateKbps,
            double encodeSpeed,
            TimeSpan estimatedCompletion,
            SmartEncodeConfidence projectionConfidence,
            int sampleCount,
            double sampledMediaSeconds,
            bool usedDurationFallback)
        {
            _workingFolder = workingFolder;
            Clips = clips;
            ProjectedFinalMb = projectedFinalMb;
            ProjectedLowerMb = projectedLowerMb;
            ProjectedUpperMb = projectedUpperMb;
            AverageBitrateKbps = averageBitrateKbps;
            EncodeSpeed = encodeSpeed;
            EstimatedCompletion = estimatedCompletion;
            ProjectionConfidence = projectionConfidence;
            SampleCount = sampleCount;
            SampledMediaSeconds = sampledMediaSeconds;
            UsedDurationFallback = usedDurationFallback;
        }

        public IReadOnlyList<SampleComparisonClip> Clips { get; }
        public double ProjectedFinalMb { get; }
        public double ProjectedLowerMb { get; }
        public double ProjectedUpperMb { get; }
        public double AverageBitrateKbps { get; }
        public double EncodeSpeed { get; }
        public TimeSpan EstimatedCompletion { get; }
        public SmartEncodeConfidence ProjectionConfidence { get; }
        public int SampleCount { get; }
        public double SampledMediaSeconds { get; }
        public bool UsedDurationFallback { get; }
        public string WorkingFolder => _workingFolder;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_workingFolder))
                    Directory.Delete(_workingFolder, recursive: true);
            }
            catch
            {
                // A media player may still have a preview open. Stale sample folders
                // are harmless and are cleaned on a later sample run.
            }
        }
    }

    public sealed class SampleProjectionResult
    {
        internal SampleProjectionResult(
            double projectedFinalMb,
            double projectedLowerMb,
            double projectedUpperMb,
            double averageBitrateKbps,
            double encodeSpeed,
            TimeSpan estimatedCompletion,
            SmartEncodeConfidence projectionConfidence,
            int sampleCount,
            double sampledMediaSeconds,
            bool usedDurationFallback)
        {
            ProjectedFinalMb = projectedFinalMb;
            ProjectedLowerMb = projectedLowerMb;
            ProjectedUpperMb = projectedUpperMb;
            AverageBitrateKbps = averageBitrateKbps;
            EncodeSpeed = encodeSpeed;
            EstimatedCompletion = estimatedCompletion;
            ProjectionConfidence = projectionConfidence;
            SampleCount = sampleCount;
            SampledMediaSeconds = sampledMediaSeconds;
            UsedDurationFallback = usedDurationFallback;
        }

        public double ProjectedFinalMb { get; }
        public double ProjectedLowerMb { get; }
        public double ProjectedUpperMb { get; }
        public double AverageBitrateKbps { get; }
        public double EncodeSpeed { get; }
        public TimeSpan EstimatedCompletion { get; }
        public SmartEncodeConfidence ProjectionConfidence { get; }
        public int SampleCount { get; }
        public double SampledMediaSeconds { get; }
        public bool UsedDurationFallback { get; }
    }

    internal readonly record struct SampleProjectionMeasurement(
        long EncodedBytes,
        double MeasuredDurationSeconds,
        double RequestedDurationSeconds);

    internal sealed record SampleProjectionCalculation(
        double ProjectedFinalMb,
        double ProjectedLowerMb,
        double ProjectedUpperMb,
        double AverageBitrateKbps,
        SmartEncodeConfidence Confidence,
        int SampleCount,
        double SampledMediaSeconds,
        bool UsedDurationFallback);

    /// <summary>
    /// Generates short beginning/middle/end source clips, encodes them with the
    /// current MediaFlux settings, and builds synchronized side-by-side previews.
    /// This service is intentionally independent of the live encode queue.
    /// </summary>
    public sealed class SampleComparisonService
    {
        private const int MaxCapturedFfmpegCharacters = 256 * 1024;
        private readonly string _appPath;
        private readonly string _ffmpegPath;
        private readonly MediaInfoService _sampleMediaInfoService;
        private readonly string? _configuredFfmpegPath;
        private readonly string? _configuredFfprobePath;
        private readonly Action<string>? _log;
        private readonly Func<string, string, CancellationToken, Task>? _runFfmpegOverride;

        public SampleComparisonService(
            string appPath,
            string? configuredFfmpegPath,
            string? configuredFfprobePath,
            Action<string>? log = null)
        {
            _appPath = appPath;
            _configuredFfmpegPath = configuredFfmpegPath;
            _configuredFfprobePath = configuredFfprobePath;
            FfmpegToolPaths toolPaths = FfmpegToolResolver.Resolve(
                appPath,
                configuredFfmpegPath,
                configuredFfprobePath);
            _ffmpegPath = toolPaths.FfmpegPath;
            _sampleMediaInfoService = new MediaInfoService(
                appPath,
                toolPaths.FfprobePath,
                persistentCacheEnabled: false);
            _log = log;
        }

        internal SampleComparisonService(
            string appPath,
            Func<string, string, CancellationToken, Task> runFfmpegOverride,
            Action<string>? log = null)
            : this(appPath, null, null, log)
        {
            _runFfmpegOverride = runFfmpegOverride
                ?? throw new ArgumentNullException(nameof(runFfmpegOverride));
        }

        public async Task<SampleComparisonResult> GenerateAsync(
            string sourcePath,
            TimeSpan sourceDuration,
            SampleComparisonSettings settings,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            return await GenerateCoreAsync(
                sourcePath,
                sourceDuration,
                settings,
                progress,
                buildComparisonVideos: true,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<SampleProjectionResult> GenerateProjectionAsync(
            string sourcePath,
            TimeSpan sourceDuration,
            SampleComparisonSettings settings,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            using SampleComparisonResult result = await GenerateCoreAsync(
                sourcePath,
                sourceDuration,
                settings,
                progress,
                buildComparisonVideos: false,
                cancellationToken).ConfigureAwait(false);

            return new SampleProjectionResult(
                result.ProjectedFinalMb,
                result.ProjectedLowerMb,
                result.ProjectedUpperMb,
                result.AverageBitrateKbps,
                result.EncodeSpeed,
                result.EstimatedCompletion,
                result.ProjectionConfidence,
                result.SampleCount,
                result.SampledMediaSeconds,
                result.UsedDurationFallback);
        }

        private async Task<SampleComparisonResult> GenerateCoreAsync(
            string sourcePath,
            TimeSpan sourceDuration,
            SampleComparisonSettings settings,
            IProgress<string>? progress,
            bool buildComparisonVideos,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("The selected source video no longer exists.", sourcePath);
            if (sourceDuration <= TimeSpan.Zero)
                throw new InvalidOperationException("MediaFlux could not determine the selected video's duration.");

            CleanupOldSampleFolders();

            string root = Path.Combine(
                Path.GetTempPath(),
                "MediaFlux",
                "SampleComparisons",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var clips = new List<SampleComparisonClip>();
            var projectionMeasurements = new List<SampleProjectionMeasurement>();
            var encodeStopwatch = new Stopwatch();

            try
            {
                var positions = BuildSamplePositions(sourceDuration, settings.ClipSeconds);
                for (int i = 0; i < positions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var position = positions[i];
                    string stem = $"{i + 1:00}_{position.Label.ToLowerInvariant()}";
                    string originalPath = Path.Combine(root, $"{stem}_original.mkv");

                    progress?.Report($"Preparing {position.Label.ToLowerInvariant()} sample ({i + 1} of {positions.Count})…");
                    await PrepareSourceClipAsync(
                        sourcePath,
                        position.Start,
                        position.Duration,
                        originalPath,
                        position.Label,
                        progress,
                        cancellationToken).ConfigureAwait(false);

                    double preparedDurationSeconds =
                        _sampleMediaInfoService.GetDurationSeconds(originalPath);
                    if (preparedDurationSeconds <= 0)
                        preparedDurationSeconds = position.Duration.TotalSeconds;
                    double? sampleTargetMb = settings.ProjectedTargetMb.HasValue
                        ? settings.ProjectedTargetMb.Value *
                          (preparedDurationSeconds / sourceDuration.TotalSeconds)
                        : null;

                    progress?.Report($"Encoding {position.Label.ToLowerInvariant()} sample ({i + 1} of {positions.Count})…");
                    var encoder = new EncodingService(
                        _appPath,
                        _ => { },
                        _log,
                        _configuredFfmpegPath,
                        _configuredFfprobePath);

                    encodeStopwatch.Start();
                    ResolvedVideoEncoder selectedEncoder =
                        settings.Encoder == null
                            ? EncoderRegistry.Default.ResolveLegacyCodec(
                                settings.VideoCodec)
                            : EncoderRegistry.Default.Resolve(
                                settings.Encoder.EncoderId,
                                settings.Encoder.CodecFamily);
                    var encodeRequest = new EncodingRequest
                    {
                        Input = EncodingInputSource.FromFile(originalPath),
                        OutputFolder = root,
                        Suffix = $"_{stem}_encoded",
                        Encoder = selectedEncoder.Selection,
                        UseGpu = settings.UseGpu,
                        TargetMb = sampleTargetMb,
                        ScaleMode = settings.ScaleMode,
                        EncoderPreset = settings.EncoderPreset,
                        QualityValue = settings.QualityValue,
                        TenBit = settings.TenBit,
                        AudioChannels = settings.AudioChannels,
                        Restoration = settings.Restoration.Clone(),
                        CancellationToken = cancellationToken
                    };
                    var encoded = await encoder.EncodeWithResultAsync(
                        encodeRequest).ConfigureAwait(false);
                    _log?.Invoke($"[SampleComparison] Restoration {settings.Restoration.Preset}; filters: {VideoRestorationPipeline.BuildFilterChain(settings.Restoration, settings.ScaleMode)}");
                    encodeStopwatch.Stop();

                    string comparisonPath = string.Empty;
                    if (buildComparisonVideos)
                    {
                        comparisonPath = Path.Combine(root, $"{stem}_comparison.mp4");
                        progress?.Report($"Building side-by-side {position.Label.ToLowerInvariant()} preview…");
                        await BuildComparisonAsync(
                            originalPath,
                            encoded.OutputPath,
                            comparisonPath,
                            cancellationToken).ConfigureAwait(false);
                    }

                    long encodedBytes = new FileInfo(encoded.OutputPath).Length;
                    double measuredDurationSeconds =
                        _sampleMediaInfoService.GetDurationSeconds(encoded.OutputPath);
                    projectionMeasurements.Add(new SampleProjectionMeasurement(
                        encodedBytes,
                        measuredDurationSeconds,
                        position.Duration.TotalSeconds));
                    clips.Add(new SampleComparisonClip
                    {
                        Label = position.Label,
                        Start = position.Start,
                        OriginalPath = originalPath,
                        EncodedPath = encoded.OutputPath,
                        ComparisonPath = comparisonPath
                    });
                }

                SampleProjectionCalculation projection = CalculateProjection(
                    projectionMeasurements,
                    sourceDuration.TotalSeconds,
                    settings.ProjectedTargetMb.HasValue
                        ? 0
                        : settings.AdditionalMappedBitrateKbps);
                double speed = encodeStopwatch.Elapsed.TotalSeconds > 0
                    ? projection.SampledMediaSeconds / encodeStopwatch.Elapsed.TotalSeconds
                    : 0;
                TimeSpan eta = speed > 0
                    ? TimeSpan.FromSeconds(sourceDuration.TotalSeconds / speed)
                    : TimeSpan.Zero;

                progress?.Report(
                    buildComparisonVideos
                        ? "Sample comparison ready."
                        : "Sample projection ready.");
                return new SampleComparisonResult(
                    root,
                    clips,
                    projection.ProjectedFinalMb,
                    projection.ProjectedLowerMb,
                    projection.ProjectedUpperMb,
                    projection.AverageBitrateKbps,
                    speed,
                    eta,
                    projection.Confidence,
                    projection.SampleCount,
                    projection.SampledMediaSeconds,
                    projection.UsedDurationFallback);
            }
            catch
            {
                try { Directory.Delete(root, recursive: true); } catch { }
                throw;
            }
        }

        internal static IReadOnlyList<(string Label, TimeSpan Start, TimeSpan Duration)> BuildSamplePositions(
            TimeSpan sourceDuration,
            int requestedClipSeconds)
        {
            double clipSeconds = Math.Min(
                Math.Max(5, requestedClipSeconds),
                Math.Max(1, sourceDuration.TotalSeconds));
            double maxStart = Math.Max(0, sourceDuration.TotalSeconds - clipSeconds);

            if (sourceDuration.TotalSeconds <= clipSeconds * 1.5)
            {
                return new[]
                {
                    ("Full video", TimeSpan.Zero, sourceDuration)
                };
            }

            if (sourceDuration.TotalSeconds < clipSeconds * 3)
            {
                return new[]
                {
                    ("Beginning", TimeSpan.Zero, TimeSpan.FromSeconds(clipSeconds)),
                    ("End", TimeSpan.FromSeconds(maxStart), TimeSpan.FromSeconds(clipSeconds))
                };
            }

            return new[]
            {
                ("Beginning", TimeSpan.Zero, TimeSpan.FromSeconds(clipSeconds)),
                ("Middle", TimeSpan.FromSeconds(maxStart / 2d), TimeSpan.FromSeconds(clipSeconds)),
                ("End", TimeSpan.FromSeconds(maxStart), TimeSpan.FromSeconds(clipSeconds))
            };
        }

        internal static SampleProjectionCalculation CalculateProjection(
            IReadOnlyList<SampleProjectionMeasurement> measurements,
            double sourceDurationSeconds,
            double additionalMappedBitrateKbps = 0)
        {
            if (measurements == null || measurements.Count == 0 || sourceDurationSeconds <= 0)
            {
                return new SampleProjectionCalculation(
                    0, 0, 0, 0, SmartEncodeConfidence.Low, 0, 0, false);
            }

            var rates = new List<double>(measurements.Count);
            long totalBytes = 0;
            double totalSeconds = 0;
            bool usedDurationFallback = false;
            foreach (SampleProjectionMeasurement measurement in measurements)
            {
                if (measurement.EncodedBytes <= 0)
                    continue;

                double seconds = measurement.MeasuredDurationSeconds > 0
                    ? measurement.MeasuredDurationSeconds
                    : measurement.RequestedDurationSeconds;
                if (seconds <= 0)
                    continue;

                usedDurationFallback |= measurement.MeasuredDurationSeconds <= 0;
                totalBytes += measurement.EncodedBytes;
                totalSeconds += seconds;
                rates.Add(measurement.EncodedBytes / seconds);
            }

            if (rates.Count == 0 || totalSeconds <= 0)
            {
                return new SampleProjectionCalculation(
                    0, 0, 0, 0, SmartEncodeConfidence.Low, 0, 0, usedDurationFallback);
            }

            double bytesPerSecond = totalBytes / totalSeconds;
            double projectedMb =
                bytesPerSecond * sourceDurationSeconds / (1024d * 1024d);
            projectedMb += Math.Max(0, additionalMappedBitrateKbps) *
                           sourceDurationSeconds / 8192d;
            double averageRate = rates.Average();
            double variance = rates.Sum(rate => Math.Pow(rate - averageRate, 2)) /
                              rates.Count;
            double relativeSpread = averageRate > 0
                ? Math.Sqrt(variance) / averageRate
                : 1;
            double uncertaintyPercent = Math.Clamp(
                Math.Max(8, relativeSpread * 110),
                8,
                40);
            if (usedDurationFallback)
                uncertaintyPercent = Math.Max(uncertaintyPercent, 25);

            double coverageRatio = Math.Min(1, totalSeconds / sourceDurationSeconds);
            SmartEncodeConfidence confidence =
                usedDurationFallback || relativeSpread > 0.30
                    ? SmartEncodeConfidence.Low
                    : coverageRatio >= 0.80
                        ? SmartEncodeConfidence.High
                    : rates.Count < 2
                        ? SmartEncodeConfidence.Low
                    : relativeSpread > 0.15
                        ? SmartEncodeConfidence.Medium
                        : SmartEncodeConfidence.High;

            return new SampleProjectionCalculation(
                projectedMb,
                projectedMb * (1 - uncertaintyPercent / 100d),
                projectedMb * (1 + uncertaintyPercent / 100d),
                totalBytes * 8d / 1000d / totalSeconds,
                confidence,
                rates.Count,
                totalSeconds,
                usedDurationFallback);
        }

        internal async Task PrepareSourceClipAsync(
            string sourcePath,
            TimeSpan start,
            TimeSpan duration,
            string outputPath,
            string label,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            string operation = $"preparing the {label.ToLowerInvariant()} source clip";
            try
            {
                await RunFfmpegAsync(
                    BuildStreamCopyClipArguments(sourcePath, start, duration, outputPath),
                    operation,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (IsUnknownTimestampFailure(ex))
            {
                string message =
                    $"The {label.ToLowerInvariant()} sample has missing timestamps; " +
                    "normalizing it for comparison…";
                progress?.Report(message);
                _log?.Invoke(
                    $"[SampleComparison] {message} Retrying with lossless video normalization.");

                await RunFfmpegAsync(
                    BuildNormalizedClipArguments(sourcePath, start, duration, outputPath),
                    $"normalizing timestamps for the {label.ToLowerInvariant()} source clip",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        internal static string BuildStreamCopyClipArguments(
            string sourcePath,
            TimeSpan start,
            TimeSpan duration,
            string outputPath)
        {
            return
                $"-hide_banner -nostats -loglevel error -y -fflags +genpts " +
                $"-ss {Seconds(start.TotalSeconds)} -i {Quote(sourcePath)} " +
                $"-t {Seconds(duration.TotalSeconds)} -map 0:v:0 -map 0:a? " +
                $"-c copy -avoid_negative_ts make_zero {Quote(outputPath)}";
        }

        internal static string BuildNormalizedClipArguments(
            string sourcePath,
            TimeSpan start,
            TimeSpan duration,
            string outputPath)
        {
            return
                $"-hide_banner -nostats -loglevel error -y -fflags +genpts " +
                $"-ss {Seconds(start.TotalSeconds)} -i {Quote(sourcePath)} " +
                $"-t {Seconds(duration.TotalSeconds)} -map 0:v:0 -map 0:a? " +
                $"-c:v ffv1 -level 3 -c:a aac -b:a 192k " +
                $"-avoid_negative_ts make_zero {Quote(outputPath)}";
        }

        internal static bool IsUnknownTimestampFailure(InvalidOperationException exception)
        {
            string message = exception.Message;
            return message.Contains(
                       "Can't write packet with unknown timestamp",
                       StringComparison.OrdinalIgnoreCase) ||
                   message.Contains(
                       "Timestamps are unset in a packet",
                       StringComparison.OrdinalIgnoreCase);
        }

        private Task BuildComparisonAsync(
            string originalPath,
            string encodedPath,
            string outputPath,
            CancellationToken cancellationToken)
        {
            const string filter =
                "[0:v]setpts=PTS-STARTPTS,scale=-2:540:flags=lanczos,setsar=1[left];" +
                "[1:v]setpts=PTS-STARTPTS,scale=-2:540:flags=lanczos,setsar=1[right];" +
                "[left][right]hstack=inputs=2[v]";

            return RunFfmpegAsync(
                $"-hide_banner -nostats -loglevel error -y " +
                $"-i {Quote(originalPath)} -i {Quote(encodedPath)} " +
                $"-filter_complex {Quote(filter)} -map \"[v]\" -map 1:a:0? " +
                $"-c:v libx264 -preset veryfast -crf 14 -c:a aac -b:a 192k " +
                $"-shortest -movflags +faststart {Quote(outputPath)}",
                "building the side-by-side preview",
                cancellationToken);
        }

        private async Task RunFfmpegAsync(
            string arguments,
            string operation,
            CancellationToken cancellationToken)
        {
            if (_runFfmpegOverride != null)
            {
                await _runFfmpegOverride(arguments, operation, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var stderr = new StringBuilder();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    AppendBounded(stderr, e.Data);
            };

            _log?.Invoke($"[SampleComparison] ffmpeg arguments: {arguments}");
            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
                throw;
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"FFmpeg failed while {operation} (exit code {process.ExitCode}).{Environment.NewLine}" +
                    $"Command: {Quote(_ffmpegPath)} {arguments}{Environment.NewLine}" +
                    stderr.ToString().Trim());
        }

        private static void AppendBounded(StringBuilder builder, string line)
        {
            if (builder.Length >= MaxCapturedFfmpegCharacters)
                return;

            int available = MaxCapturedFfmpegCharacters - builder.Length;
            if (line.Length <= available)
                builder.AppendLine(line);
            else
            {
                builder.Append(line.AsSpan(0, available));
                builder.AppendLine();
                builder.AppendLine("[Additional FFmpeg diagnostic output truncated by MediaFlux.]");
            }
        }

        private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

        private static string Seconds(double seconds) =>
            seconds.ToString("0.###", CultureInfo.InvariantCulture);

        private static void CleanupOldSampleFolders()
        {
            string parent = Path.Combine(Path.GetTempPath(), "MediaFlux", "SampleComparisons");
            if (!Directory.Exists(parent))
                return;

            foreach (string folder in Directory.EnumerateDirectories(parent))
            {
                try
                {
                    if (Directory.GetCreationTimeUtc(folder) < DateTime.UtcNow.AddDays(-2))
                        Directory.Delete(folder, recursive: true);
                }
                catch { }
            }
        }
    }
}
