using System.Globalization;
using System.Text.RegularExpressions;
using MediaFlux.Models;

namespace MediaFlux.Services
{
    public sealed class DeepMediaAnalysisService
    {
        private static readonly Regex IdetRegex = new(
            @"Multi frame detection:\s*TFF:\s*(\d+)\s+BFF:\s*(\d+)\s+Progressive:\s*(\d+)\s+Undetermined:\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly string _ffmpegPath;
        private readonly SampleComparisonService _sampleComparisonService;
        private readonly IMediaToolProcessRunner _processRunner;

        public DeepMediaAnalysisService(
            string appPath,
            string? configuredFfmpegPath,
            string? configuredFfprobePath)
            : this(
                FfmpegToolResolver.Resolve(
                    appPath,
                    configuredFfmpegPath,
                    configuredFfprobePath).FfmpegPath,
                new SampleComparisonService(
                    appPath,
                    configuredFfmpegPath,
                    configuredFfprobePath),
                new MediaToolProcessRunner())
        {
        }

        internal DeepMediaAnalysisService(
            string ffmpegPath,
            SampleComparisonService sampleComparisonService,
            IMediaToolProcessRunner processRunner)
        {
            _ffmpegPath = ffmpegPath;
            _sampleComparisonService = sampleComparisonService;
            _processRunner = processRunner;
        }

        public async Task<DeepMediaAnalysisResult> AnalyzeAsync(
            string sourcePath,
            TimeSpan sourceDuration,
            SampleComparisonSettings projectionSettings,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentNullException.ThrowIfNull(projectionSettings);

            var notes = new List<string>();
            SampleProjectionResult? projection = null;
            try
            {
                projection = await _sampleComparisonService.GenerateProjectionAsync(
                    sourcePath,
                    sourceDuration,
                    projectionSettings,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                notes.Add($"Sample projection was unavailable: {OneLine(ex.Message)}");
            }

            progress?.Report("Checking sampled frames for interlacing…");
            InterlaceEvidence interlace = await AnalyzeInterlaceAsync(
                sourcePath,
                sourceDuration,
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(interlace.Note))
                notes.Add(interlace.Note);

            progress?.Report("Checking sampled visual complexity…");
            VisualEvidence visual = await AnalyzeVisualComplexityAsync(
                sourcePath,
                sourceDuration,
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(visual.Note))
                notes.Add(visual.Note);

            return new DeepMediaAnalysisResult
            {
                ProjectedOutputMb = projection?.ProjectedFinalMb,
                AverageBitrateKbps = projection?.AverageBitrateKbps ?? 0,
                EncodeSpeed = projection?.EncodeSpeed ?? 0,
                EstimatedCompletion = projection?.EstimatedCompletion ?? TimeSpan.Zero,
                InterlaceStatus = interlace.Status,
                InterlacedFrames = interlace.InterlacedFrames,
                ProgressiveFrames = interlace.ProgressiveFrames,
                PossibleSyntheticContent = visual.PossibleSyntheticContent,
                VisualFramesAnalyzed = visual.FrameCount,
                MedianQuantizedColorCount = visual.MedianQuantizedColorCount,
                Notes = notes
            };
        }

        internal async Task<InterlaceEvidence> AnalyzeInterlaceAsync(
            string sourcePath,
            TimeSpan sourceDuration,
            CancellationToken cancellationToken)
        {
            int interlaced = 0;
            int progressive = 0;
            int parsedSamples = 0;

            foreach (var position in SampleComparisonService.BuildSamplePositions(
                         sourceDuration,
                         requestedClipSeconds: 5))
            {
                var result = await _processRunner.RunAsync(
                    new MediaToolProcessRequest
                    {
                        FileName = _ffmpegPath,
                        Arguments = new[]
                        {
                            "-hide_banner", "-nostats", "-loglevel", "info",
                            "-ss", Seconds(position.Start.TotalSeconds),
                            "-i", sourcePath,
                            "-t", Seconds(position.Duration.TotalSeconds),
                            "-map", "0:v:0",
                            "-vf", "idet",
                            "-an", "-sn", "-f", "null", "-"
                        },
                        Timeout = TimeSpan.FromMinutes(2)
                    },
                    cancellationToken).ConfigureAwait(false);

                if (result.TimedOut)
                {
                    return new InterlaceEvidence(
                        SampledInterlaceStatus.Unavailable,
                        interlaced,
                        progressive,
                        "Interlace sampling timed out.");
                }

                if (result.ExitCode != 0)
                {
                    return new InterlaceEvidence(
                        SampledInterlaceStatus.Unavailable,
                        interlaced,
                        progressive,
                        "Interlace sampling was unavailable for this source.");
                }

                if (TryParseIdet(result.StandardError, out int sampleInterlaced, out int sampleProgressive))
                {
                    interlaced += sampleInterlaced;
                    progressive += sampleProgressive;
                    parsedSamples++;
                }
            }

            if (parsedSamples == 0 || interlaced + progressive == 0)
            {
                return new InterlaceEvidence(
                    SampledInterlaceStatus.Unavailable,
                    interlaced,
                    progressive,
                    "FFmpeg did not return usable interlace statistics.");
            }

            double share = interlaced / (double)(interlaced + progressive);
            SampledInterlaceStatus status =
                interlaced >= 10 && share >= 0.20
                    ? SampledInterlaceStatus.Interlaced
                    : interlaced >= 5 && share >= 0.05
                        ? SampledInterlaceStatus.Mixed
                        : SampledInterlaceStatus.Progressive;

            return new InterlaceEvidence(status, interlaced, progressive, "");
        }

        internal static bool TryParseIdet(
            string output,
            out int interlacedFrames,
            out int progressiveFrames)
        {
            interlacedFrames = 0;
            progressiveFrames = 0;
            bool found = false;

            foreach (Match match in IdetRegex.Matches(output ?? string.Empty))
            {
                if (!int.TryParse(match.Groups[1].Value, out int tff) ||
                    !int.TryParse(match.Groups[2].Value, out int bff) ||
                    !int.TryParse(match.Groups[3].Value, out int progressive))
                {
                    continue;
                }

                interlacedFrames += tff + bff;
                progressiveFrames += progressive;
                found = true;
            }

            return found;
        }

        private async Task<VisualEvidence> AnalyzeVisualComplexityAsync(
            string sourcePath,
            TimeSpan sourceDuration,
            CancellationToken cancellationToken)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "MediaFlux",
                "DeepAnalysis",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                int sampleIndex = 0;
                foreach (var position in SampleComparisonService.BuildSamplePositions(
                             sourceDuration,
                             requestedClipSeconds: 3))
                {
                    string pattern = Path.Combine(root, $"sample-{sampleIndex++:00}-%02d.ppm");
                    var result = await _processRunner.RunAsync(
                        new MediaToolProcessRequest
                        {
                            FileName = _ffmpegPath,
                            Arguments = new[]
                            {
                                "-hide_banner", "-nostats", "-loglevel", "error", "-y",
                                "-ss", Seconds(position.Start.TotalSeconds),
                                "-i", sourcePath,
                                "-t", Seconds(position.Duration.TotalSeconds),
                                "-vf", "fps=1,scale=160:-2:flags=area",
                                "-frames:v", "3",
                                "-an", "-sn", pattern
                            },
                            Timeout = TimeSpan.FromMinutes(2)
                        },
                        cancellationToken).ConfigureAwait(false);

                    if (result.TimedOut || result.ExitCode != 0)
                    {
                        return new VisualEvidence(
                            false,
                            0,
                            0,
                            "Visual-complexity sampling was unavailable for this source.");
                    }
                }

                var frames = new List<FrameEvidence>();
                foreach (string path in Directory.EnumerateFiles(root, "*.ppm"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryAnalyzePpm(File.ReadAllBytes(path), out FrameEvidence evidence))
                        frames.Add(evidence);
                }

                if (frames.Count < 6)
                {
                    return new VisualEvidence(
                        false,
                        frames.Count,
                        0,
                        "Too few visual samples were available for content classification.");
                }

                double medianColors = Median(frames.Select(frame => (double)frame.QuantizedColorCount));
                double medianEdges = Median(frames.Select(frame => frame.EdgeDensity));
                bool possibleSynthetic =
                    medianColors <= 160 ||
                    (medianColors <= 400 && medianEdges >= 0.08);

                return new VisualEvidence(
                    possibleSynthetic,
                    frames.Count,
                    medianColors,
                    possibleSynthetic
                        ? "Low color complexity with persistent sharp edges suggests possible animation or screen content."
                        : "");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return new VisualEvidence(
                    false,
                    0,
                    0,
                    "Visual-complexity sampling was unavailable for this source.");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                        Directory.Delete(root, recursive: true);
                }
                catch
                {
                    // Temporary diagnostic frames are harmless if a media tool still holds a handle.
                }
            }
        }

        internal static bool TryAnalyzePpm(byte[] data, out FrameEvidence evidence)
        {
            evidence = default;
            if (data == null || data.Length < 16)
                return false;

            int offset = 0;
            string? magic = ReadPpmToken(data, ref offset);
            string? widthToken = ReadPpmToken(data, ref offset);
            string? heightToken = ReadPpmToken(data, ref offset);
            string? maxToken = ReadPpmToken(data, ref offset);
            if (magic != "P6" ||
                !int.TryParse(widthToken, out int width) ||
                !int.TryParse(heightToken, out int height) ||
                !int.TryParse(maxToken, out int maxValue) ||
                width <= 1 ||
                height <= 1 ||
                maxValue != 255)
            {
                return false;
            }

            if (offset >= data.Length || !char.IsWhiteSpace((char)data[offset]))
                return false;
            if (data[offset] == '\r' &&
                offset + 1 < data.Length &&
                data[offset + 1] == '\n')
            {
                offset += 2;
            }
            else
            {
                offset++;
            }

            int required = checked(width * height * 3);
            if (offset + required > data.Length)
                return false;

            var colors = new HashSet<int>();
            int edges = 0;
            int comparisons = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int pixel = offset + ((y * width + x) * 3);
                    byte r = data[pixel];
                    byte g = data[pixel + 1];
                    byte b = data[pixel + 2];
                    colors.Add((r >> 4) << 8 | (g >> 4) << 4 | (b >> 4));

                    if (x > 0)
                    {
                        int left = pixel - 3;
                        if (ColorDistance(data, pixel, left) >= 42)
                            edges++;
                        comparisons++;
                    }

                    if (y > 0)
                    {
                        int above = pixel - width * 3;
                        if (ColorDistance(data, pixel, above) >= 42)
                            edges++;
                        comparisons++;
                    }
                }
            }

            evidence = new FrameEvidence(
                colors.Count,
                comparisons > 0 ? edges / (double)comparisons : 0);
            return true;
        }

        private static int ColorDistance(byte[] data, int first, int second)
        {
            return (
                Math.Abs(data[first] - data[second]) +
                Math.Abs(data[first + 1] - data[second + 1]) +
                Math.Abs(data[first + 2] - data[second + 2])) / 3;
        }

        private static string? ReadPpmToken(byte[] data, ref int offset)
        {
            while (offset < data.Length)
            {
                if (data[offset] == '#')
                {
                    while (offset < data.Length && data[offset] != '\n')
                        offset++;
                }
                else if (char.IsWhiteSpace((char)data[offset]))
                {
                    offset++;
                }
                else
                {
                    break;
                }
            }

            if (offset >= data.Length)
                return null;

            int start = offset;
            while (offset < data.Length &&
                   !char.IsWhiteSpace((char)data[offset]) &&
                   data[offset] != '#')
            {
                offset++;
            }

            return System.Text.Encoding.ASCII.GetString(data, start, offset - start);
        }

        private static double Median(IEnumerable<double> values)
        {
            double[] ordered = values.OrderBy(value => value).ToArray();
            if (ordered.Length == 0)
                return 0;
            int middle = ordered.Length / 2;
            return ordered.Length % 2 == 0
                ? (ordered[middle - 1] + ordered[middle]) / 2d
                : ordered[middle];
        }

        private static string Seconds(double value) =>
            value.ToString("0.###", CultureInfo.InvariantCulture);

        private static string OneLine(string message)
        {
            string line = (message ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "unknown error";
            return line.Length <= 180 ? line : line[..180] + "…";
        }

        internal readonly record struct InterlaceEvidence(
            SampledInterlaceStatus Status,
            int InterlacedFrames,
            int ProgressiveFrames,
            string Note);

        internal readonly record struct VisualEvidence(
            bool PossibleSyntheticContent,
            int FrameCount,
            double MedianQuantizedColorCount,
            string Note);

        internal readonly record struct FrameEvidence(
            int QuantizedColorCount,
            double EdgeDensity);
    }
}
