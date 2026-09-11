using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed record EncoderBenchmarkSettings(
    VideoEncoderSelection Encoder,
    string EncoderDisplayName,
    bool UseGpu,
    double? FullFileTargetMb,
    EncodingService.ScaleMode ScaleMode,
    string CurrentPreset,
    int QualityValue,
    bool TenBit,
    int? AudioChannels,
    EncodingService.StreamMapMode MapMode,
    bool CopySubtitles,
    bool CopyDataStreams,
    bool CopyAttachments,
    OutputContainerSelection OutputContainer,
    bool ContainerCompatibilityConfirmed);

public sealed record EncoderBenchmarkDefinition(
    string SourcePath,
    TimeSpan SourceDuration,
    long SourceSizeBytes,
    string SourceCodec,
    string SourceFormat,
    string SourceResolution,
    double? SourceFps,
    EncoderBenchmarkSettings Settings,
    IReadOnlyList<EncoderPresetOption> AvailablePresets,
    IReadOnlyList<int> AvailableConcurrency);

public sealed record EncoderBenchmarkRequest(
    EncoderBenchmarkDefinition Definition,
    IReadOnlyList<string> Presets,
    IReadOnlyList<int> ConcurrencyValues,
    int SampleSeconds = 25);

public sealed record EncoderBenchmarkSample(string Label, TimeSpan Start, TimeSpan Duration);

public sealed record EncoderBenchmarkJobRequest(
    EncoderBenchmarkDefinition Definition,
    EncoderBenchmarkSample Sample,
    string Preset,
    int Concurrency,
    int JobNumber,
    string OutputFolder,
    double? SampleTargetMb,
    bool TolerantVideoDecode = false);

public sealed record EncoderBenchmarkJobMeasurement(
    int JobNumber,
    bool Success,
    TimeSpan Elapsed,
    double EncodeFps,
    double RealtimeMultiplier,
    long OutputBytes,
    string FfmpegArguments,
    string Pipeline,
    int? ExitCode,
    string Error,
    int AttemptCount = 1,
    bool UsedDecodeRecovery = false,
    bool UsedAlternateSample = false,
    EncoderBenchmarkSample? ActualSample = null,
    string FailureClassification = "",
    string AttemptDetails = "");

public sealed record EncoderBenchmarkConfigurationResult(
    string Preset,
    int Concurrency,
    bool Success,
    IReadOnlyList<EncoderBenchmarkJobMeasurement> Jobs,
    double AverageJobFps,
    double AverageJobRealtimeMultiplier,
    double AggregateFps,
    double AggregateRealtimeMultiplier,
    TimeSpan Elapsed,
    TimeSpan? EstimatedFullFileTime,
    double? EstimatedSourceReadMbps,
    double? OutputWriteMbps,
    double? CpuPercent,
    double? GpuPercent,
    double? GpuEncodePercent,
    double? GpuDecodePercent,
    long? PeakVramBytes,
    string TelemetryStatus,
    string Status,
    string Error);

public sealed record EncoderBenchmarkReport(
    EncoderBenchmarkDefinition Definition,
    EncoderBenchmarkSample Sample,
    IReadOnlyList<EncoderBenchmarkConfigurationResult> Results,
    DateTime CompletedUtc);

public interface IEncoderBenchmarkJobRunner
{
    Task<EncoderBenchmarkJobMeasurement> RunAsync(
        EncoderBenchmarkJobRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

public sealed class EncodingServiceBenchmarkJobRunner : IEncoderBenchmarkJobRunner
{
    private readonly string _applicationDirectory;
    private readonly string? _ffmpegPath;
    private readonly string? _ffprobePath;
    private readonly Action<string>? _log;

    public EncodingServiceBenchmarkJobRunner(
        string applicationDirectory,
        string? ffmpegPath = null,
        string? ffprobePath = null,
        Action<string>? log = null)
    {
        _applicationDirectory = applicationDirectory;
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _log = log;
    }

    public async Task<EncoderBenchmarkJobMeasurement> RunAsync(
        EncoderBenchmarkJobRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var speedValues = new List<double>();
        var fpsValues = new List<double>();
        long encodedFrames = 0;
        var decoderDiagnostics = new StringBuilder();
        string pipeline = "Unavailable";
        Action<string> callback = line =>
        {
            if (line.StartsWith("[MediaFlux] Video pipeline:", StringComparison.OrdinalIgnoreCase))
                pipeline = line[(line.IndexOf(':') + 1)..].Trim();
            progress?.Report($"{request.Preset}, {request.Concurrency} job(s): " + line);
        };
        var encoder = new EncodingService(
            _applicationDirectory, callback, _log, _ffmpegPath, _ffprobePath);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            EncodingService.EncodeResult result = await encoder.EncodeWithResultAsync(new EncodingRequest
            {
                Input = EncodingInputSource.FromFile(request.Definition.SourcePath),
                OutputFolder = request.OutputFolder,
                Suffix = $"_benchmark_{request.Preset}_{request.Concurrency}_{request.JobNumber}",
                Encoder = request.Definition.Settings.Encoder,
                UseGpu = request.Definition.Settings.UseGpu,
                TargetMb = request.SampleTargetMb,
                ScaleMode = request.Definition.Settings.ScaleMode,
                EncoderPreset = request.Preset,
                QualityValue = request.Definition.Settings.QualityValue,
                TenBit = request.Definition.Settings.TenBit,
                AudioChannels = request.Definition.Settings.AudioChannels,
                ProgressCallback = callback,
                ConcurrentEncoderSessions = request.Concurrency > 1,
                MapMode = request.Definition.Settings.MapMode,
                CopySubtitles = request.Definition.Settings.CopySubtitles,
                CopyDataStreams = request.Definition.Settings.CopyDataStreams,
                CopyAttachments = request.Definition.Settings.CopyAttachments,
                OutputContainer = request.Definition.Settings.OutputContainer,
                ContainerCompatibilityConfirmed = request.Definition.Settings.ContainerCompatibilityConfirmed,
                CancellationToken = cancellationToken,
                SampleStart = request.Sample.Start,
                SampleDuration = request.Sample.Duration,
                SourceDecodeMode = request.TolerantVideoDecode
                    ? FfmpegSourceDecodeMode.RecoverVideo
                    : FfmpegSourceDecodeMode.Strict,
                DisableAutomaticFfmpegRecovery = true,
                FfmpegDiagnosticCallback = line =>
                {
                    if (decoderDiagnostics.Length < 32_768)
                        decoderDiagnostics.AppendLine(line);
                    if (EncodingDiagnosticsService.TryParseProgress(
                        line, request.Sample.Duration.TotalSeconds, out var value))
                    {
                        if (value.Speed > 0) speedValues.Add(value.Speed);
                        if (value.Fps > 0) fpsValues.Add(value.Fps);
                    }
                    Match frame = Regex.Match(line, @"(?:^|\s)frame=\s*(\d+)", RegexOptions.IgnoreCase);
                    if (frame.Success && long.TryParse(frame.Groups[1].Value, out long count))
                        encodedFrames = Math.Max(encodedFrames, count);
                }
                ,ValidationProfile = EncodeOutputValidationProfile.BenchmarkSample
            }).ConfigureAwait(false);
            stopwatch.Stop();
            double realtime = stopwatch.Elapsed.TotalSeconds > 0
                ? request.Sample.Duration.TotalSeconds / stopwatch.Elapsed.TotalSeconds
                : 0;
            return new EncoderBenchmarkJobMeasurement(
                request.JobNumber, result.Success && result.FinalizationSucceeded,
                stopwatch.Elapsed,
                EncoderBenchmarkService.CalculateSuccessfulFps(fpsValues, encodedFrames, stopwatch.Elapsed),
                speedValues.Count == 0 ? realtime : speedValues.Average(),
                result.FinalOutputSizeBytes ?? 0,
                result.DiagnosticArguments, pipeline, 0,
                result.Success ? "" : "The benchmark output did not validate.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Match exit = Regex.Match(ex.Message, @"exit(?:ed)?\s+(?:with\s+)?code\s+(\d+)", RegexOptions.IgnoreCase);
            return new EncoderBenchmarkJobMeasurement(
                request.JobNumber, false, stopwatch.Elapsed, 0, 0, 0, "", pipeline,
                exit.Success ? int.Parse(exit.Groups[1].Value) : null,
                string.IsNullOrWhiteSpace(decoderDiagnostics.ToString())
                    ? ex.Message
                    : $"{ex.Message}{Environment.NewLine}{decoderDiagnostics}");
        }
    }

}

public sealed class EncoderBenchmarkService : IDisposable
{
    private readonly IEncoderBenchmarkJobRunner _runner;
    private readonly IEncodingSystemTelemetryProvider _telemetry;
    private readonly bool _ownsTelemetry;
    private readonly string _temporaryRoot;
    private readonly TimeSpan _telemetryInterval;

    public EncoderBenchmarkService(
        IEncoderBenchmarkJobRunner runner,
        IEncodingSystemTelemetryProvider? telemetry = null,
        string? temporaryRoot = null,
        TimeSpan? telemetryInterval = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _ownsTelemetry = telemetry == null;
        _telemetry = telemetry ?? new WindowsEncodingSystemTelemetryProvider();
        _temporaryRoot = temporaryRoot ?? Path.Combine(Path.GetTempPath(), "MediaFlux", "EncoderBenchmarks");
        _telemetryInterval = telemetryInterval ?? TimeSpan.FromSeconds(1);
    }

    public async Task<EncoderBenchmarkReport> RunAsync(
        EncoderBenchmarkRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        EncoderBenchmarkSample sample = SelectRepresentativeSample(
            request.Definition.SourceDuration, request.SampleSeconds);
        string operationRoot = Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operationRoot);
        var results = new List<EncoderBenchmarkConfigurationResult>();
        try
        {
            foreach (string preset in request.Presets.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (int concurrency in request.ConcurrencyValues.Distinct().Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Benchmarking {preset} with {concurrency} simultaneous job(s)…");
                results.Add(await RunConfigurationAsync(
                    request.Definition, sample, preset, concurrency,
                    operationRoot, progress, cancellationToken).ConfigureAwait(false));
            }
            return new EncoderBenchmarkReport(request.Definition, sample, results, DateTime.UtcNow);
        }
        finally
        {
            TryDeleteDirectory(operationRoot);
        }
    }

    private async Task<EncoderBenchmarkConfigurationResult> RunConfigurationAsync(
        EncoderBenchmarkDefinition definition,
        EncoderBenchmarkSample sample,
        string preset,
        int concurrency,
        string operationRoot,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string folder = Path.Combine(operationRoot, $"{preset}-{concurrency}");
        Directory.CreateDirectory(folder);
        double? sampleTarget = definition.Settings.FullFileTargetMb is > 0
            ? definition.Settings.FullFileTargetMb.Value *
              sample.Duration.TotalSeconds / definition.SourceDuration.TotalSeconds
            : null;
        var telemetry = new ConcurrentQueue<EncodingSystemTelemetry>();
        using var samplingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task sampling = SampleTelemetryAsync(telemetry, samplingCts.Token);
        var wall = Stopwatch.StartNew();
        Task<EncoderBenchmarkJobMeasurement>[] jobs = Enumerable.Range(1, concurrency)
            .Select(index => RunJobWithDecodeRecoveryAsync(
                definition, sample, preset, concurrency, index, folder, sampleTarget,
                progress, cancellationToken))
            .ToArray();
        EncoderBenchmarkJobMeasurement[] measurements;
        try
        {
            measurements = await Task.WhenAll(jobs).ConfigureAwait(false);
        }
        finally
        {
            wall.Stop();
            samplingCts.Cancel();
            try { await sampling.ConfigureAwait(false); } catch (OperationCanceledException) { }
            TryDeleteDirectory(folder);
        }
        EncodingSystemTelemetry[] samples = telemetry.ToArray();
        double averageSpeed = Average(measurements.Where(x => x.Success).Select(x => x.RealtimeMultiplier));
        double aggregateSpeed = wall.Elapsed.TotalSeconds > 0
            ? measurements.Where(x => x.Success).Sum(_ => sample.Duration.TotalSeconds) / wall.Elapsed.TotalSeconds
            : 0;
        long outputBytes = measurements.Where(x => x.Success).Sum(x => x.OutputBytes);
        bool anySuccess = measurements.Any(x => x.Success);
        double? sourceRead = anySuccess && definition.SourceDuration.TotalSeconds > 0 && wall.Elapsed.TotalSeconds > 0
            ? definition.SourceSizeBytes * 8d / definition.SourceDuration.TotalSeconds * aggregateSpeed / 1_000_000d
            : null;
        string status = measurements.All(x => x.Success)
            ? measurements.Any(x => x.UsedAlternateSample) ? "Completed (alternate sample)"
            : measurements.Any(x => x.UsedDecodeRecovery) ? "Completed (decode recovery)" : "Completed"
            : measurements.Where(x => !x.Success).All(x => x.FailureClassification == "Video decode corruption")
                ? "Source sample decode failed" : "Encoder failed";
        return new EncoderBenchmarkConfigurationResult(
            preset, concurrency, measurements.All(x => x.Success), measurements,
            Average(measurements.Where(x => x.Success).Select(x => x.EncodeFps)), averageSpeed,
            measurements.Where(x => x.Success).Sum(x => x.EncodeFps), aggregateSpeed,
            wall.Elapsed,
            averageSpeed > 0 ? TimeSpan.FromSeconds(definition.SourceDuration.TotalSeconds / averageSpeed) : null,
            sourceRead,
            anySuccess && wall.Elapsed.TotalSeconds > 0 ? outputBytes * 8d / wall.Elapsed.TotalSeconds / 1_000_000d : null,
            AverageNullable(samples.Select(x => x.SystemCpuPercent)),
            AverageNullable(samples.Select(x => x.GpuPercent)),
            AverageNullable(samples.Select(x => x.GpuEncodePercent)),
            AverageNullable(samples.Select(x => x.GpuDecodePercent)),
            Peak(samples.Select(x => x.VramUsedBytes)),
            samples.Select(x => x.GpuStatus).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "Telemetry unavailable.",
            status,
            string.Join(" | ", measurements.Where(x => !x.Success).Select(x => x.Error)));
    }

    private async Task<EncoderBenchmarkJobMeasurement> RunJobWithDecodeRecoveryAsync(
        EncoderBenchmarkDefinition definition, EncoderBenchmarkSample primarySample, string preset,
        int concurrency, int jobNumber, string folder, double? sampleTarget,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var attempts = new List<EncoderBenchmarkJobMeasurement>();
        var attemptDetails = new List<string>();
        foreach ((EncoderBenchmarkSample sample, bool tolerant, bool alternate) attempt in
            EnumerateAttempts(definition, primarySample))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EncoderBenchmarkJobMeasurement measurement = await _runner.RunAsync(new EncoderBenchmarkJobRequest(
                definition, attempt.sample, preset, concurrency, jobNumber, folder,
                sampleTarget, attempt.tolerant), progress, cancellationToken).ConfigureAwait(false);
            measurement = measurement with { ActualSample = attempt.sample };
            attempts.Add(measurement);
            if (measurement.Success)
            {
                attemptDetails.Add($"{attempt.sample.Start:g} ({(attempt.tolerant ? "tolerant" : "strict")}): succeeded");
                return measurement with
                {
                    AttemptCount = attempts.Count,
                    UsedDecodeRecovery = attempts.Any(x => !x.Success),
                    UsedAlternateSample = attempt.alternate,
                    ActualSample = attempt.sample,
                    FailureClassification = attempts.Count > 1 ? "Video decode corruption recovered" : "",
                    AttemptDetails = string.Join("; ", attemptDetails)
                };
            }

            FfmpegVideoDecodeRecoveryDecision decision = FfmpegVideoDecodeRecoveryPolicy.EvaluateForBenchmark(
                measurement.Error, cancellationToken.IsCancellationRequested,
                definition.Settings.Encoder.EncoderId.Equals(VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase), folder);
            if (!decision.Eligible)
            {
                attemptDetails.Add($"{attempt.sample.Start:g} ({(attempt.tolerant ? "tolerant" : "strict")}): failed; Other failure");
                return measurement with { AttemptCount = attempts.Count, FailureClassification = "Other failure", AttemptDetails = string.Join("; ", attemptDetails) };
            }
            attemptDetails.Add($"{attempt.sample.Start:g} ({(attempt.tolerant ? "tolerant" : "strict")}): failed; video decode corruption ({decision.Evidence})");

            // Each sample begins strict. A tolerant retry follows only its recognized video-decode failure.
            if (!attempt.tolerant)
                continue;
        }

        EncoderBenchmarkJobMeasurement last = attempts[^1];
        return last with
        {
            AttemptCount = attempts.Count,
            UsedDecodeRecovery = true,
            UsedAlternateSample = attempts.Skip(2).Any(),
            ActualSample = last.ActualSample ?? primarySample,
            FailureClassification = "Video decode corruption",
            Error = "Source unsuitable for benchmarking: persistent video decode errors were encountered in representative samples.",
            AttemptDetails = string.Join("; ", attemptDetails)
        };
    }

    private static IEnumerable<(EncoderBenchmarkSample sample, bool tolerant, bool alternate)> EnumerateAttempts(
        EncoderBenchmarkDefinition definition, EncoderBenchmarkSample primary)
    {
        yield return (primary, false, false);
        yield return (primary, true, false);
        foreach (EncoderBenchmarkSample alternate in SelectAlternateSamples(definition.SourceDuration, primary.Duration, primary.Start))
        {
            yield return (alternate, false, true);
            yield return (alternate, true, true);
        }
    }

    private static IEnumerable<EncoderBenchmarkSample> SelectAlternateSamples(TimeSpan sourceDuration, TimeSpan duration, TimeSpan primaryStart)
    {
        if (sourceDuration <= duration + TimeSpan.FromSeconds(2)) yield break;
        foreach (double point in new[] { .60, .30 })
        {
            double start = Math.Clamp(sourceDuration.TotalSeconds * point - duration.TotalSeconds / 2d, 0, sourceDuration.TotalSeconds - duration.TotalSeconds);
            if (Math.Abs(start - primaryStart.TotalSeconds) < 2d) continue;
            yield return new EncoderBenchmarkSample($"Alternate representative section ({point:P0})", TimeSpan.FromSeconds(start), duration);
        }
    }

    private async Task SampleTelemetryAsync(
        ConcurrentQueue<EncodingSystemTelemetry> samples,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { samples.Enqueue(_telemetry.Sample()); } catch { }
            await Task.Delay(_telemetryInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static double CalculateSuccessfulFps(
        IEnumerable<double> observedFps,
        long encodedFrames,
        TimeSpan successfulElapsed)
    {
        double[] values = observedFps.Where(value => value > 0 && double.IsFinite(value)).ToArray();
        if (values.Length > 0)
            return values.Average();
        return encodedFrames > 0 && successfulElapsed.TotalSeconds > 0
            ? encodedFrames / successfulElapsed.TotalSeconds
            : 0;
    }

    public static EncoderBenchmarkSample SelectRepresentativeSample(TimeSpan sourceDuration, int requestedSeconds)
    {
        if (sourceDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sourceDuration));
        int seconds = Math.Clamp(requestedSeconds, 5, 120);
        TimeSpan duration = TimeSpan.FromSeconds(Math.Min(seconds, sourceDuration.TotalSeconds));
        if (sourceDuration <= duration + TimeSpan.FromSeconds(2))
            return new EncoderBenchmarkSample("Full video", TimeSpan.Zero, sourceDuration);
        double start = Math.Max(0, sourceDuration.TotalSeconds * 0.45 - duration.TotalSeconds / 2d);
        start = Math.Min(start, sourceDuration.TotalSeconds - duration.TotalSeconds);
        return new EncoderBenchmarkSample("Representative middle section", TimeSpan.FromSeconds(start), duration);
    }

    public static TimeSpan? EstimateFullFileTime(TimeSpan sourceDuration, double realtimeMultiplier) =>
        sourceDuration > TimeSpan.Zero && realtimeMultiplier > 0
            ? TimeSpan.FromSeconds(sourceDuration.TotalSeconds / realtimeMultiplier)
            : null;

    public static string BuildTechnicalDetails(
        EncoderBenchmarkDefinition definition,
        EncoderBenchmarkSample sample,
        EncoderBenchmarkConfigurationResult result)
    {
        var text = new StringBuilder();
        text.AppendLine("MediaFlux Encoder Benchmark Diagnostic")
            .AppendLine($"Source: {Path.GetFileName(definition.SourcePath)}")
            .AppendLine($"Source media: {definition.SourceCodec}; {definition.SourceResolution}; {definition.SourceFps?.ToString("0.###") ?? "FPS unavailable"}; {definition.SourceFormat}")
            .AppendLine($"Sample: {sample.Label}, {sample.Start:g} for {sample.Duration:g}")
            .AppendLine($"Encoder / preset: {definition.Settings.EncoderDisplayName} / {result.Preset}")
            .AppendLine($"Encoder codec / quality: {definition.Settings.Encoder.FfmpegCodec} / {definition.Settings.QualityValue}")
            .AppendLine($"Options: {(definition.Settings.TenBit ? "10-bit" : "8-bit")}; scale {definition.Settings.ScaleMode}; audio channels {(definition.Settings.AudioChannels?.ToString() ?? "copy")}; container {definition.Settings.OutputContainer}")
            .AppendLine($"Concurrency: {result.Concurrency}")
            .AppendLine($"Status: {result.Status}")
            .AppendLine($"Per-job FPS / speed: {result.AverageJobFps:0.0} / {result.AverageJobRealtimeMultiplier:0.00}x")
            .AppendLine($"Aggregate FPS / speed: {result.AggregateFps:0.0} / {result.AggregateRealtimeMultiplier:0.00}x")
            .AppendLine($"Estimated full-file time: {result.EstimatedFullFileTime?.ToString("g") ?? "Unavailable"}")
            .AppendLine($"Estimated source media read rate: {Rate(result.EstimatedSourceReadMbps)} (not a disk benchmark)")
            .AppendLine($"Output file write rate: {Rate(result.OutputWriteMbps)} (encoded bytes / elapsed; not device maximum)")
            .AppendLine($"CPU / GPU / encode / decode: {Percent(result.CpuPercent)} / {Percent(result.GpuPercent)} / {Percent(result.GpuEncodePercent)} / {Percent(result.GpuDecodePercent)}")
            .AppendLine($"Peak VRAM: {(result.PeakVramBytes.HasValue ? $"{result.PeakVramBytes.Value / 1048576d:0} MiB" : "Unavailable")}")
            .AppendLine($"Telemetry: {result.TelemetryStatus}");
        foreach (EncoderBenchmarkJobMeasurement job in result.Jobs)
        {
            text.AppendLine().AppendLine($"Job {job.JobNumber}: {(job.Success ? "Succeeded" : "Failed")}; elapsed {job.Elapsed:g}; {job.EncodeFps:0.0} FPS; {job.RealtimeMultiplier:0.00}x; exit {(job.ExitCode?.ToString() ?? "Unavailable")}")
                .AppendLine($"Benchmark attempts: {job.AttemptCount}; sample: {(job.ActualSample is null ? sample.Label : $"{job.ActualSample.Label}, {job.ActualSample.Start:g}")}; decode: {(job.UsedDecodeRecovery ? "tolerant recovery used" : "strict")}; classification: {(string.IsNullOrWhiteSpace(job.FailureClassification) ? "None" : job.FailureClassification)}")
                .AppendLine($"Attempt detail: {(string.IsNullOrWhiteSpace(job.AttemptDetails) ? "Unavailable" : job.AttemptDetails)}")
                .AppendLine($"Hardware/decode path: {job.Pipeline}")
                .AppendLine($"FFmpeg arguments: {(string.IsNullOrWhiteSpace(job.FfmpegArguments) ? "Unavailable" : job.FfmpegArguments)}");
            if (!string.IsNullOrWhiteSpace(job.Error)) text.AppendLine($"Error: {job.Error}");
        }
        return text.ToString();
        static string Percent(double? value) => value.HasValue ? $"{value:0.#}%" : "Unavailable";
        static string Rate(double? value) => value.HasValue ? $"{value:0.00} Mbit/s" : "Unavailable";
    }

    public static string BuildTechnicalDetailsForResults(
        EncoderBenchmarkDefinition definition,
        EncoderBenchmarkSample sample,
        IEnumerable<EncoderBenchmarkConfigurationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        EncoderBenchmarkConfigurationResult[] materialized = results.ToArray();
        return string.Join(
            Environment.NewLine + Environment.NewLine + new string('-', 72) + Environment.NewLine + Environment.NewLine,
            materialized.Select(result => BuildTechnicalDetails(definition, sample, result)));
    }

    private static void Validate(EncoderBenchmarkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(request.Definition.SourcePath))
            throw new FileNotFoundException("The benchmark source no longer exists.", request.Definition.SourcePath);
        if (request.Definition.SourceDuration <= TimeSpan.Zero)
            throw new InvalidOperationException("The source duration is unavailable.");
        if (request.Presets.Count == 0) throw new ArgumentException("Select at least one encoder preset.");
        if (request.ConcurrencyValues.Count == 0 || request.ConcurrencyValues.Any(x => x is < 1 or > 8))
            throw new ArgumentException("Select valid benchmark concurrency values.");
    }

    private static double Average(IEnumerable<double> values)
    {
        double[] materialized = values.Where(x => x > 0 && double.IsFinite(x)).ToArray();
        return materialized.Length == 0 ? 0 : materialized.Average();
    }

    private static double? AverageNullable(IEnumerable<double?> values)
    {
        double[] materialized = values.Where(x => x.HasValue && double.IsFinite(x.Value)).Select(x => x!.Value).ToArray();
        return materialized.Length == 0 ? null : materialized.Average();
    }

    private static long? Peak(IEnumerable<long?> values)
    {
        long[] materialized = values.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return materialized.Length == 0 ? null : materialized.Max();
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    public void Dispose()
    {
        if (_ownsTelemetry && _telemetry is IDisposable disposable) disposable.Dispose();
    }
}
