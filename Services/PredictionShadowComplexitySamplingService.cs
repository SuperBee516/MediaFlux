using System.Diagnostics;
using System.Globalization;
using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>
/// Bounded, decode-only research sampling. It does not create an encoded
/// comparison clip and never modifies the source.
/// </summary>
public sealed class PredictionShadowComplexitySamplingService
{
    public const string SamplerVersion = "phase3c1-gray-sample-v1";
    private const int OutputWidth = 160;
    private const int FramesPerWindow = 5;
    private readonly string _ffmpegPath;
    private readonly IMediaToolProcessRunner _runner;

    public PredictionShadowComplexitySamplingService(
        string appPath,
        string? configuredFfmpegPath = null)
        : this(
            FfmpegToolResolver.Resolve(appPath, configuredFfmpegPath, null).FfmpegPath,
            new MediaToolProcessRunner())
    {
    }

    internal PredictionShadowComplexitySamplingService(
        string ffmpegPath,
        IMediaToolProcessRunner runner)
    {
        _ffmpegPath = ffmpegPath;
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<PredictionShadowSamplingObservation> AnalyzeAsync(
        string sourcePath,
        double sourceDurationSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceDurationSeconds <= 0 || !double.IsFinite(sourceDurationSeconds))
            return Unavailable("Source duration is unavailable.");
        if (!File.Exists(_ffmpegPath))
            return Unavailable("FFmpeg executable is unavailable.");

        IReadOnlyList<(string Label, TimeSpan Start, TimeSpan Duration)> positions =
            SampleComparisonService.BuildSamplePositions(
                TimeSpan.FromSeconds(sourceDurationSeconds), requestedClipSeconds: 5);
        string temporaryRoot = Path.Combine(
            AppPaths.RuntimeTemporaryDirectory("PredictionShadow"), Guid.NewGuid().ToString("N"));
        var windows = new List<PredictionShadowSampleWindow>(positions.Count);
        var decodedWindows = new List<IReadOnlyList<GrayFrame>>(positions.Count);
        var failures = new List<string>();
        long temporaryBytes = 0;
        double? ffmpegCpuMilliseconds = 0;
        bool cpuMeasurementAvailable = true;
        long startedAt = Stopwatch.GetTimestamp();

        Directory.CreateDirectory(temporaryRoot);
        try
        {
            for (int windowIndex = 0; windowIndex < positions.Count; windowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string label, TimeSpan start, TimeSpan requestedDuration) = positions[windowIndex];
                double durationSeconds = Math.Min(5, requestedDuration.TotalSeconds);
                if (durationSeconds <= 0)
                {
                    failures.Add($"{label}: empty sampling window.");
                    decodedWindows.Add(Array.Empty<GrayFrame>());
                    windows.Add(new(label, start.TotalSeconds, durationSeconds, 0));
                    continue;
                }

                string pattern = Path.Combine(temporaryRoot, $"window-{windowIndex:00}-%03d.pgm");
                double sampleFps = Math.Min(30, FramesPerWindow / durationSeconds);
                string filter =
                    $"fps={sampleFps.ToString("0.####", CultureInfo.InvariantCulture)}:" +
                    "round=near," +
                    $"scale={OutputWidth}:-2:flags=area,format=gray";
                var args = new[]
                {
                    "-hide_banner", "-nostdin", "-loglevel", "error", "-ss",
                    start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", sourcePath, "-t", durationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    "-map", "0:v:0", "-vf", filter, "-frames:v", FramesPerWindow.ToString(CultureInfo.InvariantCulture),
                    "-an", "-sn", "-f", "image2", pattern
                };

                Process? childProcess = null;
                MediaToolProcessResult processResult;
                try
                {
                    processResult = await _runner.RunAsync(
                        new MediaToolProcessRequest
                        {
                            FileName = _ffmpegPath,
                            Arguments = args,
                            Timeout = TimeSpan.FromSeconds(2),
                            ProcessStartedCallback = launch =>
                            {
                                try { childProcess = Process.GetProcessById(launch.ProcessId); }
                                catch { /* CPU timing is optional diagnostic evidence. */ }
                            }
                        }, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (childProcess != null)
                    {
                        try { ffmpegCpuMilliseconds += childProcess.TotalProcessorTime.TotalMilliseconds; }
                        catch { cpuMeasurementAvailable = false; }
                        childProcess.Dispose();
                    }
                    else
                    {
                        cpuMeasurementAvailable = false;
                    }
                }

                if (processResult.TimedOut || processResult.ExitCode != 0)
                {
                    failures.Add($"{label}: FFmpeg {(processResult.TimedOut ? "timed out" : $"exited {processResult.ExitCode}")}.");
                }

                string[] files = Directory.Exists(temporaryRoot)
                    ? Directory.GetFiles(temporaryRoot, $"window-{windowIndex:00}-*.pgm")
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .ToArray()
                    : Array.Empty<string>();
                var frames = new List<GrayFrame>(files.Length);
                foreach (string framePath in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        byte[] data = File.ReadAllBytes(framePath);
                        temporaryBytes += data.LongLength;
                        if (TryReadPgm(data, out GrayFrame frame))
                            frames.Add(frame);
                    }
                    catch (IOException ex)
                    {
                        failures.Add($"{label}: sampled frame could not be read ({OneLine(ex.Message)}).");
                    }
                }

                if (frames.Count == 0 && processResult.ExitCode == 0 && !processResult.TimedOut)
                    failures.Add($"{label}: FFmpeg produced no analyzable frames.");
                decodedWindows.Add(frames);
                windows.Add(new(label, start.TotalSeconds, durationSeconds, frames.Count));
            }

            cancellationToken.ThrowIfCancellationRequested();
            (double? temporal, double? spatial, int frameCount, int pairCount, int? width, int? height) =
                Measure(decodedWindows);
            if (frameCount == 0)
                return new PredictionShadowSamplingObservation
                {
                    SamplerVersion = SamplerVersion,
                    Status = PredictionShadowSamplingStatus.Unavailable,
                    WindowCount = positions.Count,
                    Windows = windows,
                    WallClockMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    FfmpegCpuMilliseconds = cpuMeasurementAvailable ? ffmpegCpuMilliseconds : null,
                    TemporaryBytesWritten = temporaryBytes,
                    FailureReason = failures.Count == 0 ? "No analyzable frames were produced." : string.Join(" ", failures)
                };

            bool complete = failures.Count == 0 && temporal.HasValue && spatial.HasValue;
            return new PredictionShadowSamplingObservation
            {
                SamplerVersion = SamplerVersion,
                Status = complete ? PredictionShadowSamplingStatus.Succeeded : PredictionShadowSamplingStatus.Partial,
                TemporalFrameDifference = temporal,
                SpatialGradientEnergy = spatial,
                FrameCount = frameCount,
                TemporalPairCount = pairCount,
                WindowCount = positions.Count,
                AnalysisFrameWidth = width,
                AnalysisFrameHeight = height,
                Windows = windows,
                WallClockMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                FfmpegCpuMilliseconds = cpuMeasurementAvailable ? ffmpegCpuMilliseconds : null,
                TemporaryBytesWritten = temporaryBytes,
                UsedHardwareDecode = false,
                FailureReason = failures.Count == 0 ? null : string.Join(" ", failures)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PredictionShadowSamplingObservation
            {
                SamplerVersion = SamplerVersion,
                Status = PredictionShadowSamplingStatus.Unavailable,
                WindowCount = positions.Count,
                Windows = windows,
                WallClockMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                FfmpegCpuMilliseconds = cpuMeasurementAvailable ? ffmpegCpuMilliseconds : null,
                TemporaryBytesWritten = temporaryBytes,
                FailureReason = OneLine(ex.Message)
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryRoot))
                    Directory.Delete(temporaryRoot, recursive: true);
            }
            catch
            {
                // Temporary research frames are non-authoritative and best-effort cleaned.
            }
        }
    }

    internal sealed record GrayFrame(int Width, int Height, byte[] Pixels);

    internal static (double? Temporal, double? Spatial, int FrameCount, int PairCount, int? Width, int? Height)
        Measure(IReadOnlyList<IReadOnlyList<GrayFrame>> windows)
    {
        var temporal = new List<double>();
        var spatial = new List<double>();
        int frameCount = 0;
        int? firstWidth = null;
        int? firstHeight = null;
        foreach (IReadOnlyList<GrayFrame> window in windows)
        {
            frameCount += window.Count;
            foreach (GrayFrame frame in window)
            {
                firstWidth ??= frame.Width;
                firstHeight ??= frame.Height;
                if (frame.Width > 2 && frame.Height > 2 && frame.Pixels.Length == frame.Width * frame.Height)
                    spatial.Add(SpatialGradient(frame));
            }
            for (int index = 1; index < window.Count; index++)
            {
                GrayFrame before = window[index - 1];
                GrayFrame after = window[index];
                if (before.Width == after.Width && before.Height == after.Height &&
                    before.Pixels.Length == after.Pixels.Length && before.Pixels.Length > 0)
                    temporal.Add(TemporalDifference(before, after));
            }
        }

        return (temporal.Count == 0 ? null : Median(temporal),
            spatial.Count == 0 ? null : Median(spatial), frameCount, temporal.Count, firstWidth, firstHeight);
    }

    private static double TemporalDifference(GrayFrame before, GrayFrame after)
    {
        long sum = 0;
        for (int index = 0; index < before.Pixels.Length; index++)
            sum += Math.Abs(before.Pixels[index] - after.Pixels[index]);
        return Math.Clamp(sum / (before.Pixels.Length * 255d), 0, 1);
    }

    private static double SpatialGradient(GrayFrame frame)
    {
        double sum = 0;
        int samples = 0;
        for (int y = 1; y < frame.Height - 1; y++)
        {
            int row = y * frame.Width;
            for (int x = 1; x < frame.Width - 1; x++)
            {
                int index = row + x;
                double horizontal = (frame.Pixels[index + 1] - frame.Pixels[index - 1]) / 2d;
                double vertical = (frame.Pixels[index + frame.Width] - frame.Pixels[index - frame.Width]) / 2d;
                sum += Math.Sqrt(horizontal * horizontal + vertical * vertical) / (255d * Math.Sqrt(2));
                samples++;
            }
        }
        return samples == 0 ? 0 : Math.Clamp(sum / samples, 0, 1);
    }

    private static bool TryReadPgm(byte[] data, out GrayFrame frame)
    {
        frame = default!;
        int offset = 0;
        string? magic = ReadToken(data, ref offset);
        string? widthToken = ReadToken(data, ref offset);
        string? heightToken = ReadToken(data, ref offset);
        string? maxToken = ReadToken(data, ref offset);
        if (magic != "P5" || !int.TryParse(widthToken, NumberStyles.None, CultureInfo.InvariantCulture, out int width) ||
            !int.TryParse(heightToken, NumberStyles.None, CultureInfo.InvariantCulture, out int height) ||
            !int.TryParse(maxToken, NumberStyles.None, CultureInfo.InvariantCulture, out int max) ||
            width <= 0 || height <= 0 || max != 255 || offset >= data.Length)
            return false;

        if (data[offset] == (byte)'\r' && offset + 1 < data.Length && data[offset + 1] == (byte)'\n')
            offset += 2;
        else if (char.IsWhiteSpace((char)data[offset]))
            offset++;
        else
            return false;

        int length;
        try { length = checked(width * height); }
        catch (OverflowException) { return false; }
        if (data.Length - offset < length)
            return false;
        frame = new GrayFrame(width, height, data.AsSpan(offset, length).ToArray());
        return true;
    }

    private static string? ReadToken(byte[] data, ref int offset)
    {
        while (offset < data.Length)
        {
            if (data[offset] == (byte)'#')
            {
                while (offset < data.Length && data[offset] != (byte)'\n') offset++;
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
        if (offset >= data.Length) return null;
        int start = offset;
        while (offset < data.Length && !char.IsWhiteSpace((char)data[offset]) && data[offset] != (byte)'#') offset++;
        return Encoding.ASCII.GetString(data, start, offset - start);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] ordered = values.OrderBy(value => value).ToArray();
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2d : ordered[middle];
    }

    private static PredictionShadowSamplingObservation Unavailable(string reason) => new()
    {
        SamplerVersion = SamplerVersion,
        Status = PredictionShadowSamplingStatus.Unavailable,
        FailureReason = reason
    };

    private static string OneLine(string value)
    {
        string line = (value ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "unknown error";
        return line.Length <= 240 ? line : line[..240] + "…";
    }
}
