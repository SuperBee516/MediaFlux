using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MediaFlux.Services
{
    public sealed class SampleComparisonSettings
    {
        public string VideoCodec { get; init; } = "libx265";
        public bool UseGpu { get; init; }
        public double? ProjectedTargetMb { get; init; }
        public EncodingService.ScaleMode ScaleMode { get; init; }
        public string NvencPreset { get; init; } = string.Empty;
        public bool TenBit { get; init; }
        public int? AudioChannels { get; init; }
        public int ClipSeconds { get; init; } = 25;
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
            double averageBitrateKbps,
            double encodeSpeed,
            TimeSpan estimatedCompletion)
        {
            _workingFolder = workingFolder;
            Clips = clips;
            ProjectedFinalMb = projectedFinalMb;
            AverageBitrateKbps = averageBitrateKbps;
            EncodeSpeed = encodeSpeed;
            EstimatedCompletion = estimatedCompletion;
        }

        public IReadOnlyList<SampleComparisonClip> Clips { get; }
        public double ProjectedFinalMb { get; }
        public double AverageBitrateKbps { get; }
        public double EncodeSpeed { get; }
        public TimeSpan EstimatedCompletion { get; }
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

    /// <summary>
    /// Generates short beginning/middle/end source clips, encodes them with the
    /// current MediaFlux settings, and builds synchronized side-by-side previews.
    /// This service is intentionally independent of the live encode queue.
    /// </summary>
    public sealed class SampleComparisonService
    {
        private readonly string _appPath;
        private readonly string _ffmpegPath;
        private readonly string? _configuredFfmpegPath;
        private readonly string? _configuredFfprobePath;
        private readonly Action<string>? _log;

        public SampleComparisonService(
            string appPath,
            string? configuredFfmpegPath,
            string? configuredFfprobePath,
            Action<string>? log = null)
        {
            _appPath = appPath;
            _configuredFfmpegPath = configuredFfmpegPath;
            _configuredFfprobePath = configuredFfprobePath;
            _ffmpegPath = FfmpegToolResolver.Resolve(
                appPath,
                configuredFfmpegPath,
                configuredFfprobePath).FfmpegPath;
            _log = log;
        }

        public async Task<SampleComparisonResult> GenerateAsync(
            string sourcePath,
            TimeSpan sourceDuration,
            SampleComparisonSettings settings,
            IProgress<string>? progress,
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
            var encodedSizes = new List<long>();
            double encodedMediaSeconds = 0;
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
                    await RunFfmpegAsync(
                        $"-y -ss {Seconds(position.Start.TotalSeconds)} -i {Quote(sourcePath)} " +
                        $"-t {Seconds(position.Duration.TotalSeconds)} -map 0:v:0 -map 0:a:0? " +
                        $"-c copy -avoid_negative_ts make_zero {Quote(originalPath)}",
                        cancellationToken).ConfigureAwait(false);

                    double? sampleTargetMb = settings.ProjectedTargetMb.HasValue
                        ? settings.ProjectedTargetMb.Value *
                          (position.Duration.TotalSeconds / sourceDuration.TotalSeconds)
                        : null;

                    progress?.Report($"Encoding {position.Label.ToLowerInvariant()} sample ({i + 1} of {positions.Count})…");
                    var encoder = new EncodingService(
                        _appPath,
                        _ => { },
                        _log,
                        _configuredFfmpegPath,
                        _configuredFfprobePath);

                    encodeStopwatch.Start();
                    var encoded = await encoder.EncodeWithResultAsync(
                        originalPath,
                        root,
                        $"_{stem}_encoded",
                        settings.UseGpu,
                        sampleTargetMb,
                        settings.VideoCodec,
                        settings.ScaleMode,
                        settings.NvencPreset,
                        settings.TenBit,
                        settings.AudioChannels,
                        progressCallback: null,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    encodeStopwatch.Stop();

                    string comparisonPath = Path.Combine(root, $"{stem}_comparison.mp4");
                    progress?.Report($"Building side-by-side {position.Label.ToLowerInvariant()} preview…");
                    await BuildComparisonAsync(
                        originalPath,
                        encoded.OutputPath,
                        comparisonPath,
                        cancellationToken).ConfigureAwait(false);

                    long encodedBytes = new FileInfo(encoded.OutputPath).Length;
                    encodedSizes.Add(encodedBytes);
                    encodedMediaSeconds += position.Duration.TotalSeconds;
                    clips.Add(new SampleComparisonClip
                    {
                        Label = position.Label,
                        Start = position.Start,
                        OriginalPath = originalPath,
                        EncodedPath = encoded.OutputPath,
                        ComparisonPath = comparisonPath
                    });
                }

                double averageKbps = encodedMediaSeconds > 0
                    ? encodedSizes.Sum() * 8d / 1000d / encodedMediaSeconds
                    : 0;
                double projectedMb = averageKbps * sourceDuration.TotalSeconds / 8192d;
                double speed = encodeStopwatch.Elapsed.TotalSeconds > 0
                    ? encodedMediaSeconds / encodeStopwatch.Elapsed.TotalSeconds
                    : 0;
                TimeSpan eta = speed > 0
                    ? TimeSpan.FromSeconds(sourceDuration.TotalSeconds / speed)
                    : TimeSpan.Zero;

                progress?.Report("Sample comparison ready.");
                return new SampleComparisonResult(
                    root,
                    clips,
                    projectedMb,
                    averageKbps,
                    speed,
                    eta);
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

            return new[]
            {
                ("Beginning", TimeSpan.Zero, TimeSpan.FromSeconds(clipSeconds)),
                ("Middle", TimeSpan.FromSeconds(maxStart / 2d), TimeSpan.FromSeconds(clipSeconds)),
                ("End", TimeSpan.FromSeconds(maxStart), TimeSpan.FromSeconds(clipSeconds))
            };
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
                $"-y -i {Quote(originalPath)} -i {Quote(encodedPath)} " +
                $"-filter_complex {Quote(filter)} -map \"[v]\" -map 1:a:0? " +
                $"-c:v libx264 -preset veryfast -crf 14 -c:a aac -b:a 192k " +
                $"-shortest -movflags +faststart {Quote(outputPath)}",
                cancellationToken);
        }

        private async Task RunFfmpegAsync(string arguments, CancellationToken cancellationToken)
        {
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
                    stderr.AppendLine(e.Data);
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
                    $"FFmpeg could not generate a comparison sample (exit code {process.ExitCode}).{Environment.NewLine}" +
                    stderr.ToString().Trim());
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
