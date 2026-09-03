using System.Globalization;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services;

public enum AiIntermediateStage { ExtractingFrames, AiProcessing, Reassembling, Validating }
public sealed record AiIntermediateProgress(
    AiIntermediateStage Stage,
    int Current,
    int Total,
    string Message,
    int ChunkNumber = 0,
    int ChunkTotal = 0,
    double? CurrentAiFramesPerSecond = null,
    double? AverageAiFramesPerSecond = null,
    TimeSpan? Elapsed = null,
    TimeSpan? EstimatedRemaining = null,
    string? Backend = null,
    string? RuntimeConfiguration = null);
public sealed record AiIntermediateVideoRequest(string SourcePath, double FrameRate, TimeSpan SourceDuration, VideoRestorationSettings Settings, VideoRestorationPipelinePlan Plan, TimeSpan? Start = null, TimeSpan? Duration = null, int SourceWidth = 0, int SourceHeight = 0, bool IsMotionPreview = false, long? SourceFrameCount = null);
public sealed record AiIntermediateVideoResult(string Path, TimeSpan Duration, int FrameCount, int Width, int Height, double FrameRate, string? StagingDirectory = null) : IDisposable
{ public void Dispose() { try { if (!string.IsNullOrWhiteSpace(StagingDirectory) && System.IO.Path.GetFileName(StagingDirectory).StartsWith("ai-intermediate-", StringComparison.OrdinalIgnoreCase) && Directory.Exists(StagingDirectory)) Directory.Delete(StagingDirectory, true); else if (File.Exists(Path)) File.Delete(Path); } catch { } } }
internal sealed record AiIntermediateChunkMetadata(string Path, string Codec, int Width, int Height, string PixelFormat, string TimeBase, string FrameRate, int FrameCount, double Duration);

/// <summary>Creates a bounded, frame-based AI video intermediate. It deliberately owns no final encoder policy.</summary>
public sealed class AiRestorationIntermediateVideoService
{
    private readonly string _ffmpegPath, _ffprobePath, _stagingRoot;
    private readonly IMediaToolProcessRunner _runner;
    private readonly IAiRestorationBackend _backend;
    private readonly Action<string>? _log;
    private readonly PerformanceTimingService? _timing;
    private readonly AiRuntimeTelemetryService _runtimeTelemetry;
    private readonly Func<long?> _dedicatedGpuVramProvider;

    public AiRestorationIntermediateVideoService(string ffmpegPath, string ffprobePath, string stagingRoot, IAiRestorationBackend backend, IMediaToolProcessRunner? runner = null, Action<string>? log = null, PerformanceTimingService? timing = null, Func<long?>? dedicatedGpuVramProvider = null, AiRuntimeTelemetryService? runtimeTelemetry = null)
    { _ffmpegPath = ffmpegPath; _ffprobePath = ffprobePath; _stagingRoot = stagingRoot; _backend = backend; _runner = runner ?? new MediaToolProcessRunner(); _log = log; _timing = timing; _runtimeTelemetry = runtimeTelemetry ?? AiRuntimeTelemetryService.Shared; _dedicatedGpuVramProvider = dedicatedGpuVramProvider ?? HardwarePerformanceService.DetectDedicatedGpuVramBytes; }

    public async Task<AiIntermediateVideoResult> CreateAsync(AiIntermediateVideoRequest request, IProgress<AiIntermediateProgress>? progress = null, CancellationToken token = default)
    {
        if (!File.Exists(request.SourcePath)) throw new FileNotFoundException("AI intermediate source is unavailable.", request.SourcePath);
        if (request.FrameRate is < 1 or > 240 || double.IsNaN(request.FrameRate))
        {
            var exception = new AiRestorationValidationException("AI intermediate processing requires a known constant frame rate between 1 and 240 fps; VFR sources are not supported yet.");
            _log?.Invoke(new AiForensicContext(request.SourcePath, "<working-directory-not-created>", token).BuildReport(exception));
            throw exception;
        }
        if (!request.Plan.UsesAi)
        {
            var exception = new AiRestorationValidationException("AI intermediate processing requires AI restoration to be enabled.");
            _log?.Invoke(new AiForensicContext(request.SourcePath, "<working-directory-not-created>", token).BuildReport(exception));
            throw exception;
        }
        AiRestorationSession session;
        try
        {
            using PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiPreparation);
            session = await _backend.CreateSessionAsync(request.Settings, token).ConfigureAwait(false); scope?.Complete();
        }
        catch (AiRestorationValidationException exception)
        {
            _log?.Invoke(new AiForensicContext(request.SourcePath, "<working-directory-not-created>", token).BuildReport(exception));
            throw;
        }
        _timing?.SetAiBackend(session.Capabilities.BackendId, session.Capabilities.ExecutablePath);
        AiChunkHardwareMetricsCollector? activeChunkHardware = null;
        using HardwarePerformanceSampler? hardwareSampler = _timing is null ? null : new HardwarePerformanceSampler(
            _timing,
            sampleObserver: sample => { Volatile.Read(ref activeChunkHardware)?.Record(sample); _runtimeTelemetry.RecordHardwareSample(sample); });
        hardwareSampler?.SampleNow();
        Directory.CreateDirectory(_stagingRoot); AiProductionHardeningService.CleanupOrphans(_stagingRoot); string root = Path.Combine(_stagingRoot, "ai-intermediate-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); AiProductionHardeningService.Register(root);
        var forensic = new AiForensicContext(request.SourcePath, root, token);
        try
        {
            TimeSpan duration = request.Duration ?? request.SourceDuration;
            if (duration <= TimeSpan.Zero) throw new AiRestorationValidationException("AI intermediate processing requires a known source duration.");
            int total = await ResolveExpectedFrameCountAsync(request, duration, token).ConfigureAwait(false);
            if (total <= 0) throw new AiRestorationValidationException("AI intermediate processing could not determine an expected frame count.");
            _runtimeTelemetry.Begin(session, request.Settings, total, request.SourceWidth, request.SourceHeight, _timing?.GetHardwareSnapshot());
            AiTemporaryStorageEstimate planningEstimate = AiProductionHardeningService.Estimate(request.SourceWidth, request.SourceHeight, total, request.Settings.AiScale, _stagingRoot, AiChunkPlanner.MinimumFramesPerChunk);
            long? dedicatedGpuVram = _timing?.DedicatedGpuVramBytes ?? _dedicatedGpuVramProvider();
            var planner = new AiChunkPlanner();
            var plannerInput = new AiChunkPlannerInput(request.SourceWidth, request.SourceHeight, request.Settings.AiScale, dedicatedGpuVram, planningEstimate, session.Capabilities.Identity);
            AiChunkPlan chunkPlan = planner.Plan(plannerInput);
            AiTemporaryStorageEstimate estimate = AiProductionHardeningService.Estimate(request.SourceWidth, request.SourceHeight, total, request.Settings.AiScale, _stagingRoot, chunkPlan.FrameCount);
            AiChunkPlannerDecision plannerDecision = planner.DescribeDecision(plannerInput with { TemporaryStorageEstimate = estimate }, chunkPlan);
            _timing?.SetAiChunkPlannerDecision(plannerDecision);
            _runtimeTelemetry.SetPlanner(plannerDecision);
            _log?.Invoke(FormatPlannerDecision(plannerDecision, session.Capabilities.Identity));
            AiProductionHardeningService.EnsureSpace(estimate); using (File.Create(Path.Combine(root, ".mediaflux-ai-staging"))) { }
            var chunks = new List<AiIntermediateChunkMetadata>();
            NcnnRuntimeSelection runtimeSelection = new(NcnnRuntimeConfiguration.SafeDefault, NcnnRuntimeConfigurationSource.SafeDefault);
            int chunkTotal = (total + chunkPlan.FrameCount - 1) / chunkPlan.FrameCount;
            long aiStartedAt = Stopwatch.GetTimestamp();
            TimeSpan completedInferenceElapsed = TimeSpan.Zero;
            void ReportProgress(AiIntermediateStage stage, int current, int chunkNumber, int? activeChunkFrames = null, long? activeInferenceStartedAt = null)
            {
                TimeSpan elapsed = Stopwatch.GetElapsedTime(aiStartedAt);
                TimeSpan activeInferenceElapsed = activeInferenceStartedAt is long startedAt ? Stopwatch.GetElapsedTime(startedAt) : TimeSpan.Zero;
                TimeSpan inferenceElapsed = completedInferenceElapsed + activeInferenceElapsed;
                double? currentFps = activeChunkFrames is int activeFrames && activeInferenceElapsed > TimeSpan.Zero ? PerformanceTimingService.CalculateAiFramesPerSecond(activeFrames, activeInferenceElapsed) : null;
                double? averageFps = inferenceElapsed > TimeSpan.Zero ? PerformanceTimingService.CalculateAiFramesPerSecond(current, inferenceElapsed) : null;
                TimeSpan? remaining = inferenceElapsed > TimeSpan.Zero ? AiRestorationProgressEstimator.EstimateRemaining(current, total, inferenceElapsed) : null;
                string runtime = session.Runtime is null ? $"Threads {runtimeSelection.Configuration.ThreadsDisplay}; Tile {runtimeSelection.Configuration.TileDisplay}; {Describe(runtimeSelection.Source)}" : $"{session.Runtime.Precision}; Engine {session.Runtime.CacheState}; Dynamic shapes";
                var update = new AiIntermediateProgress(stage, current, total,
                    FormatProgressMessage(stage, chunkNumber, chunkTotal, current, total, currentFps, averageFps, elapsed, remaining, session.Capabilities.BackendId, runtime),
                    chunkNumber, chunkTotal, currentFps, averageFps, elapsed, remaining, session.Capabilities.BackendId, runtime);
                _runtimeTelemetry.ReportProgress(update);
                progress?.Report(update);
            }
            for (int offset = 0, chunkIndex = 0; offset < total; offset += chunkPlan.FrameCount, chunkIndex++)
            {
                var chunkHardware = new AiChunkHardwareMetricsCollector();
                Volatile.Write(ref activeChunkHardware, chunkHardware);
                hardwareSampler?.SampleNow();
                long chunkStartedAt = Stopwatch.GetTimestamp();
                int count = Math.Min(chunkPlan.FrameCount, total - offset);
                AiProductionHardeningService.EnsureSpace(AiProductionHardeningService.Estimate(request.SourceWidth, request.SourceHeight, count, request.Settings.AiScale, _stagingRoot, chunkPlan.FrameCount), runtime: true);
                string chunk = Path.Combine(root, $"chunk-{chunkIndex:D5}"), input = Path.Combine(chunk, "input"), output = Path.Combine(chunk, "output"); Directory.CreateDirectory(input); Directory.CreateDirectory(output);
                forensic.SetChunk(chunkIndex + 1, chunkTotal, chunk, input, output, count);
                ReportProgress(AiIntermediateStage.ExtractingFrames, offset, chunkIndex + 1);
                long stageStartedAt = Stopwatch.GetTimestamp();
                TimeSpan ffmpegProcessLaunchElapsed = TimeSpan.Zero;
                IReadOnlyList<string> extractionArguments = BuildExtractArguments(request, offset, count, input);
                MediaToolProcessResult extractionProcess;
                using (PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiExtraction))
                { extractionProcess = await RunAsync(extractionArguments, "extract AI frames", chunks, token).ConfigureAwait(false); ffmpegProcessLaunchElapsed += extractionProcess.ProcessLaunchElapsed; scope?.Complete(); }
                TimeSpan extractionElapsed = Stopwatch.GetElapsedTime(stageStartedAt);
                TimeSpan validationElapsed = TimeSpan.Zero;
                string[] extracted = ExpectedFrames(input, count);
                forensic.SetValidation("ExtractedInput", input, extracted, _ffmpegPath, extractionArguments, extractionProcess);
                stageStartedAt = Stopwatch.GetTimestamp();
                using (PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiValidation))
                { ValidateFrameSet(input, extracted); scope?.Complete(); }
                validationElapsed += Stopwatch.GetElapsedTime(stageStartedAt);
                if (chunkIndex == 0)
                {
                    if (session.Capabilities.BackendId.Equals("ncnn-vulkan", StringComparison.OrdinalIgnoreCase))
                    {
                        string? capturedGpuIdentity = _timing?.GpuIdentity;
                        string gpuIdentity = capturedGpuIdentity ?? HardwarePerformanceService.DetectGpuIdentity();
                        bool canTune = !request.IsMotionPreview && !string.IsNullOrWhiteSpace(capturedGpuIdentity) && !capturedGpuIdentity.Equals("Unavailable", StringComparison.OrdinalIgnoreCase) && _timing?.DedicatedGpuVramBytes is > 0;
                        runtimeSelection = await new NcnnPerformanceAutoTuner(_backend, log: _log).SelectAsync(
                            session, request.Settings, input, Path.Combine(root, "ncnn-tuning"), request.SourceWidth, request.SourceHeight,
                            gpuIdentity, _timing?.GpuDriver ?? "Unavailable", _timing?.DedicatedGpuVramBytes, canTune, token).ConfigureAwait(false);
                        _timing?.SetNcnnRuntimeSelection(runtimeSelection);
                        _runtimeTelemetry.SetRuntime(runtimeSelection);
                        _log?.Invoke($"NCNN Performance Configuration{Environment.NewLine}GPU: {gpuIdentity}; Model: {session.Model.BackendModelName}; Scale: {(int)request.Settings.AiScale}x; Resolution: {request.SourceWidth}x{request.SourceHeight}; Threads: {runtimeSelection.Configuration.ThreadsDisplay}; Tile: {runtimeSelection.Configuration.TileDisplay}; Source: {Describe(runtimeSelection.Source)}.");
                    }
                    else if (session.Runtime is not null)
                    {
                        _runtimeTelemetry.SetRuntime(session.Runtime);
                        _log?.Invoke($"TensorRT Runtime Configuration{Environment.NewLine}Version: {session.Runtime.RuntimeVersion}; Precision: {session.Runtime.Precision}; Engine: {session.Runtime.EngineStatus}; Cache: {session.Runtime.CacheState}; Source: {session.Runtime.BuildSource}.");
                    }
                }
                string[] processed = ExpectedFrames(output, count);
                stageStartedAt = Stopwatch.GetTimestamp();
                long inferenceStartedAt = stageStartedAt;
                ReportProgress(AiIntermediateStage.AiProcessing, offset, chunkIndex + 1, 0, inferenceStartedAt);
                AiDirectoryProcessDiagnostic ncnnDiagnostic;
                using (PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiProcessing))
                { ncnnDiagnostic = await _backend.ProcessDirectoryAsync(
                    session,
                    request.Settings,
                    input,
                    output,
                    processed,
                    completed => ReportProgress(AiIntermediateStage.AiProcessing, offset + completed, chunkIndex + 1, completed, inferenceStartedAt),
                    token,
                    runtimeSelection.Configuration).ConfigureAwait(false); scope?.Complete(); }
                forensic.NcnnDiagnostic = ncnnDiagnostic;
                TimeSpan inferenceElapsed = Stopwatch.GetElapsedTime(stageStartedAt);
                completedInferenceElapsed += inferenceElapsed;
                forensic.SetValidation("RestoredOutput", output, processed, null, null, null);
                ReportProgress(AiIntermediateStage.Validating, offset + count, chunkIndex + 1);
                stageStartedAt = Stopwatch.GetTimestamp();
                using (PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiValidation))
                {
                    try { ValidateRestoredFrameSet(extracted, processed, request.Settings.AiScale, chunkIndex + 1, chunkTotal); if (session.Capabilities.BackendId.Equals("nvidia-tensorrt", StringComparison.OrdinalIgnoreCase)) TensorRtRuntimeDiagnostics.Shared.RecordValidation("Shared frame validation passed."); }
                    catch (AiRestorationValidationException exception)
                    {
                        string diagnostic = $"AI Restoration Failure{Environment.NewLine}Chunk: {chunkIndex + 1}/{chunkTotal}{Environment.NewLine}Backend: {session.Capabilities.BackendId}{Environment.NewLine}Reason: The AI backend completed successfully but produced an invalid frame set.{Environment.NewLine}Backend Exit Code: {ncnnDiagnostic.ExitCode}{Environment.NewLine}Frames Expected: {ncnnDiagnostic.ExpectedFrames}{Environment.NewLine}Frames Produced: {ncnnDiagnostic.RestoredFrames}{Environment.NewLine}Validation: Failed{Environment.NewLine}Recommendation: Investigate backend output generation before pipeline retry.{Environment.NewLine}{exception.Message}";
                        _log?.Invoke($"[AI Chunk {chunkIndex + 1}] Frames Expected: {count}; Frames Restored: {ncnnDiagnostic.RestoredFrames}; Frames Validated: 0; Frames Failed: {Math.Max(0, count - ncnnDiagnostic.RestoredFrames)}; Elapsed Extraction: {Format(extractionElapsed)}; Elapsed Restoration: {Format(inferenceElapsed)}; Elapsed Validation: {Format(Stopwatch.GetElapsedTime(stageStartedAt))}; Elapsed Reassembly: <not-run>; Peak RAM: {FormatBytes(Process.GetCurrentProcess().PeakWorkingSet64)}; Peak VRAM: {FormatBytes(chunkHardware.Snapshot().PeakVramUsedBytes)}; GPU Utilization Average: {Percent(chunkHardware.Snapshot().AverageGpuPercent)}; CPU Utilization Average: {Percent(chunkHardware.Snapshot().AverageCpuPercent)}; Disk Throughput Average: {BytesPerSecond(chunkHardware.Snapshot().AverageDiskThroughputBytesPerSecond)}.");
                        _log?.Invoke(diagnostic);
                        throw new AiRestorationValidationException(diagnostic);
                    }
                    scope?.Complete();
                }
                validationElapsed += Stopwatch.GetElapsedTime(stageStartedAt);
                string chunkVideo = Path.Combine(root, $"chunk-{chunkIndex:D5}.mkv");
                ReportProgress(AiIntermediateStage.Reassembling, offset + count, chunkIndex + 1);
                stageStartedAt = Stopwatch.GetTimestamp();
                using (PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiReassembly))
                { MediaToolProcessResult reassemblyProcess = await RunAsync(BuildReassemblyArguments(processed[0], request.FrameRate, chunkVideo), "reassemble AI chunk", chunks, token).ConfigureAwait(false); ffmpegProcessLaunchElapsed += reassemblyProcess.ProcessLaunchElapsed; scope?.Complete(); }
                TimeSpan reassemblyElapsed = Stopwatch.GetElapsedTime(stageStartedAt);
                stageStartedAt = Stopwatch.GetTimestamp();
                using (PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiValidation))
                { chunks.Add(await ProbeChunkAsync(chunkVideo, count, request.FrameRate, token).ConfigureAwait(false)); scope?.Complete(); }
                validationElapsed += Stopwatch.GetElapsedTime(stageStartedAt);
                TimeSpan totalElapsed = Stopwatch.GetElapsedTime(chunkStartedAt);
                hardwareSampler?.SampleNow();
                long measuredTemporaryStorage = MeasureTemporaryStorage(chunk, chunkVideo);
                TimeSpan startupShutdownOverhead = totalElapsed - extractionElapsed - inferenceElapsed - validationElapsed - reassemblyElapsed;
                if (startupShutdownOverhead < TimeSpan.Zero) startupShutdownOverhead = TimeSpan.Zero;
                AiChunkPerformanceMetrics metrics = new(chunkIndex + 1, count, extractionElapsed, inferenceElapsed, validationElapsed, reassemblyElapsed, totalElapsed, chunkHardware.Snapshot(), measuredTemporaryStorage, ffmpegProcessLaunchElapsed, startupShutdownOverhead);
                _timing?.RecordAiChunk(metrics);
                _log?.Invoke(FormatChunkMetrics(metrics));
                // Flush completed-chunk, planner, and calibration diagnostics before starting
                // another owned chunk. Cancellation after this point must not hide completed work.
                _timing?.LogSummary(_log);
                Volatile.Write(ref activeChunkHardware, null);
                Directory.Delete(chunk, true);
            }

            string final = Path.Combine(root, "intermediate.mkv"), staging = Path.Combine(root, "intermediate.staging.mkv");
            if (!ShouldJoinChunks(chunks.Count))
            {
                // A concat demuxer is needless and less reliable for a bounded preview containing one chunk.
                File.Move(chunks[0].Path, staging, true);
                _log?.Invoke($"[AI Intermediate] Promoting validated single chunk without concat: {chunks[0].Path}");
            }
            else
            {
                using PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiIntermediateJoin);
                ValidateChunkCompatibility(chunks);
                string list = Path.Combine(root, "chunks.ffconcat");
                // The concat demuxer consumes this file itself rather than receiving the
                // chunk paths through ProcessStartInfo.ArgumentList. Pin its encoding to
                // UTF-8 (without a BOM) so a Unicode staging root survives this textual
                // process boundary unchanged.
                await WriteConcatListAsync(list, chunks.Select(chunk => chunk.Path), token).ConfigureAwait(false);
                await RunAsync(new[] { "-y", "-f", "concat", "-safe", "0", "-i", list, "-map", "0:v:0", "-c:v", "copy", staging }, "join AI chunks", chunks, token).ConfigureAwait(false);
                scope?.Complete();
            }
            ReportProgress(AiIntermediateStage.Validating, total, chunkTotal);
            AiIntermediateVideoResult result;
            using (PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiValidation))
            { result = await ValidateAsync(staging, duration, total, request.FrameRate, request.Settings.AiScale, token).ConfigureAwait(false); scope?.Complete(); }
            File.Move(staging, final, true); _runtimeTelemetry.Complete(); return result with { Path = final, StagingDirectory = root };
        }
        catch (AiRestorationValidationException exception)
        {
            _runtimeTelemetry.Fail();
            string report = forensic.BuildReport(exception);
            _log?.Invoke(report);
            throw new AiRestorationValidationException($"{exception.Message}{Environment.NewLine}Preserved AI working directory: {root}");
        }
        catch { _runtimeTelemetry.Fail(); try { using PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.TemporaryFileCleanup); if (Directory.Exists(root)) Directory.Delete(root, true); scope?.Complete(); } catch { } throw; }
        finally { AiProductionHardeningService.Unregister(root); }
    }

    internal static string FormatProgressMessage(
        AiIntermediateStage stage,
        int chunkNumber,
        int chunkTotal,
        int completedFrames,
        int totalFrames,
        double? currentFps,
        double? averageFps,
        TimeSpan elapsed,
        TimeSpan? remaining,
        string backend,
        string runtimeConfiguration) =>
        $"AI {stage switch { AiIntermediateStage.ExtractingFrames => "Extract", AiIntermediateStage.AiProcessing => "Restore", AiIntermediateStage.Reassembling => "Reassemble", AiIntermediateStage.Validating => "Validate", _ => "Prepare" }} | " +
        $"Chunk {chunkNumber}/{chunkTotal} | Frames {completedFrames:N0}/{totalFrames:N0} | " +
        $"Current AI FPS {FormatProgressFps(currentFps)} | Average AI FPS {FormatProgressFps(averageFps)} | " +
        $"Elapsed {FormatProgressDuration(elapsed)} | ETA {FormatProgressDuration(remaining)} | " +
        $"Backend {backend} | Runtime {runtimeConfiguration}";

    private static string FormatProgressFps(double? value) => value is > 0 ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) : "Calculating";
    private static string FormatProgressDuration(TimeSpan? value) => value is null ? "Calculating" : Format(value.Value);

    internal static string[] ExpectedFrames(string directory, int count) => Enumerable.Range(0, count).Select(index => Path.Combine(directory, $"frame-{index:D8}.png")).ToArray();
    /// <summary>
    /// Full-source CFR jobs use the container's declared frame count when available. Duration is
    /// often rounded to a stream time base and must not synthesize an EOF frame from duration × fps.
    /// Excerpts retain their bounded duration calculation because their frame interval is not the full stream.
    /// </summary>
    internal static int ResolveExpectedFrameCount(AiIntermediateVideoRequest request, TimeSpan duration)
    {
        if (request.Start is null && request.Duration is null && request.SourceFrameCount is > 0)
            return checked((int)request.SourceFrameCount.Value);
        return checked((int)Math.Round(duration.TotalSeconds * request.FrameRate, MidpointRounding.AwayFromZero));
    }
    private async Task<int> ResolveExpectedFrameCountAsync(AiIntermediateVideoRequest request, TimeSpan duration, CancellationToken token)
    {
        if (request.Start is not null || request.Duration is not null || request.SourceFrameCount is > 0)
            return ResolveExpectedFrameCount(request, duration);

        MediaToolProcessResult result = await _runner.RunAsync(new MediaToolProcessRequest
        {
            FileName = _ffprobePath,
            Arguments = new[] { "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "default=noprint_wrappers=1", request.SourcePath },
            Timeout = TimeSpan.FromMinutes(5),
            SendQuitOnCancellation = true
        }, token).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.TimedOut || !TryReadPositiveFrameCount(result.StandardOutput, out int count))
            throw new AiRestorationValidationException($"AI restoration could not determine the exact decodable source frame count before extraction. executable={_ffprobePath}; exitCode={result.ExitCode}; timedOut={result.TimedOut}; stderr={Sanitize(result.StandardError, 4096)}");
        return count;
    }
    private static bool TryReadPositiveFrameCount(string output, out int count)
    {
        count = 0;
        string? text = output.Split('\n').Select(line => line.Trim()).Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2 && parts[0].Equals("nb_read_frames", StringComparison.OrdinalIgnoreCase)).Select(parts => parts[1].Trim()).LastOrDefault();
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out count) && count > 0;
    }
    internal static void ValidateFrameSet(string directory, IReadOnlyList<string> expected)
    {
        AiFrameSetValidationReport report = AuditFrameSet(directory, expected, null, 0, 0);
        if (!report.IsValid) throw new AiRestorationValidationException(report.Format());
    }
    internal static void ValidateRestoredFrameSet(IReadOnlyList<string> inputs, IReadOnlyList<string> outputs, AiRestorationScale scale, int chunkNumber = 0, int chunkTotal = 0)
    {
        if (inputs.Count != outputs.Count || inputs.Count == 0)
            throw new AiRestorationValidationException("AI restored-frame validation requires matching non-empty input and output frame sets.");
        AiFrameSetValidationReport report = AuditFrameSet(Path.GetDirectoryName(outputs[0])!, outputs, inputs, chunkNumber, chunkTotal);
        for (int index = 0; index < inputs.Count; index++)
        {
            if (!File.Exists(inputs[index]) || !File.Exists(outputs[index])) continue;
            try
            {
            (int inputWidth, int inputHeight) = ReadPngDimensions(inputs[index]);
            (int outputWidth, int outputHeight) = ReadPngDimensions(outputs[index]);
            int expectedWidth = checked(inputWidth * (int)scale), expectedHeight = checked(inputHeight * (int)scale);
            if (outputWidth != expectedWidth || outputHeight != expectedHeight)
                report.DimensionFailures.Add($"{Path.GetFileName(outputs[index])} ({outputWidth}x{outputHeight}; expected {expectedWidth}x{expectedHeight})");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or AiRestorationValidationException)
            { report.UnreadableImages.Add($"{Path.GetFileName(outputs[index])} ({exception.Message})"); }
        }
        if (!report.IsValid) throw new AiRestorationValidationException(report.Format());
    }
    internal static AiFrameSetValidationReport AuditFrameSet(string restoredDirectory, IReadOnlyList<string> expected, IReadOnlyList<string>? inputs, int chunkNumber, int chunkTotal)
    {
        var report = new AiFrameSetValidationReport(chunkNumber, chunkTotal, inputs is { Count: > 0 } ? Path.GetDirectoryName(inputs[0])! : "<not-applicable>", restoredDirectory, expected.Count);
        report.ValidationStartedAt = DateTimeOffset.UtcNow;
        Stopwatch stopwatch = Stopwatch.StartNew();
        FileInfo[] files;
        try { files = Directory.Exists(restoredDirectory) ? Directory.EnumerateFiles(restoredDirectory).Select(path => new FileInfo(path)).OrderBy(file => file.Name, StringComparer.Ordinal).ToArray() : Array.Empty<FileInfo>(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { report.EnumerationFailures.Add(exception.Message); files = Array.Empty<FileInfo>(); }
        report.DirectoryEnumerationElapsed = stopwatch.Elapsed;
        report.ActualFrameCount = files.Length;
        var expectedNames = new HashSet<string>(expected.Select(path => Path.GetFileName(path)!), StringComparer.Ordinal);
        var firstSnapshot = files.ToDictionary(file => file.FullName, file => (file.Length, file.LastWriteTimeUtc), StringComparer.OrdinalIgnoreCase);
        stopwatch.Restart();
        foreach (string expectedPath in expected)
            if (!File.Exists(expectedPath)) report.MissingFrameNumbers.Add(Path.GetFileName(expectedPath));
        foreach (FileInfo file in files)
        {
            if (!expectedNames.Contains(file.Name)) report.UnexpectedFilenames.Add(file.Name);
            if (!file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) report.IncorrectExtensions.Add(file.Name);
            if (file.Length == 0) report.ZeroByteFiles.Add(file.Name);
            if (file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                try { ReadPngDimensions(file.FullName); }
                catch (AiRestorationValidationException exception) { report.UnreadableImages.Add($"{file.Name} ({exception.Message})"); report.CorruptPngFiles.Add(file.Name); }
                catch (IOException exception) { report.UnreadableImages.Add($"{file.Name} ({exception.Message})"); }
            }
            string stem = Path.GetFileNameWithoutExtension(file.Name);
            if (stem.StartsWith("frame-", StringComparison.OrdinalIgnoreCase) && int.TryParse(stem[6..], NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                if (!file.Name.Equals($"frame-{number:D8}.png", StringComparison.Ordinal)) report.FilenameOrderingErrors.Add(file.Name);
            }
        }
        foreach (IGrouping<int, FileInfo> group in files.Where(file => Path.GetFileNameWithoutExtension(file.Name).StartsWith("frame-", StringComparison.OrdinalIgnoreCase))
            .Select(file => (File: file, Stem: Path.GetFileNameWithoutExtension(file.Name)))
            .Where(value => int.TryParse(value.Stem[6..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .GroupBy(value => int.Parse(value.Stem[6..], CultureInfo.InvariantCulture), value => value.File))
            if (group.Count() > 1) report.DuplicateFrameNumbers.Add($"{group.Key:D8}: {string.Join(", ", group.Select(file => file.Name))}");
        report.ImageVerificationElapsed = stopwatch.Elapsed;
        stopwatch.Restart();
        Thread.Sleep(TimeSpan.FromMilliseconds(50));
        foreach (FileInfo file in files)
        {
            try
            {
                FileInfo refreshed = new(file.FullName);
                if (!refreshed.Exists || !firstSnapshot.TryGetValue(file.FullName, out var first) || first.Length != refreshed.Length || first.LastWriteTimeUtc != refreshed.LastWriteTimeUtc)
                    report.FilesStillChanging.Add(file.Name);
            }
            catch (IOException) { report.FilesStillChanging.Add(file.Name); }
        }
        report.FilesystemWaitElapsed = stopwatch.Elapsed;
        report.ValidationFinishedAt = DateTimeOffset.UtcNow;
        return report;
    }

    internal sealed class AiFrameSetValidationReport
    {
        public AiFrameSetValidationReport(int chunkNumber, int chunkTotal, string sourceDirectory, string restoredDirectory, int expectedFrameCount)
        { ChunkNumber = chunkNumber; ChunkTotal = chunkTotal; SourceDirectory = sourceDirectory; RestoredDirectory = restoredDirectory; ExpectedFrameCount = expectedFrameCount; }
        public int ChunkNumber { get; } public int ChunkTotal { get; } public string SourceDirectory { get; } public string RestoredDirectory { get; } public int ExpectedFrameCount { get; } public int ActualFrameCount { get; set; }
        public DateTimeOffset ValidationStartedAt { get; set; } public DateTimeOffset ValidationFinishedAt { get; set; } public TimeSpan DirectoryEnumerationElapsed { get; set; } public TimeSpan ImageVerificationElapsed { get; set; } public TimeSpan FilesystemWaitElapsed { get; set; }
        public List<string> MissingFrameNumbers { get; } = new(); public List<string> DuplicateFrameNumbers { get; } = new(); public List<string> UnexpectedFilenames { get; } = new(); public List<string> ZeroByteFiles { get; } = new(); public List<string> UnreadableImages { get; } = new(); public List<string> FilesStillChanging { get; } = new(); public List<string> IncorrectExtensions { get; } = new(); public List<string> FilenameOrderingErrors { get; } = new(); public List<string> CorruptPngFiles { get; } = new(); public List<string> DimensionFailures { get; } = new(); public List<string> EnumerationFailures { get; } = new();
        public bool IsValid => MissingFrameNumbers.Count == 0 && DuplicateFrameNumbers.Count == 0 && UnexpectedFilenames.Count == 0 && ZeroByteFiles.Count == 0 && UnreadableImages.Count == 0 && FilesStillChanging.Count == 0 && IncorrectExtensions.Count == 0 && FilenameOrderingErrors.Count == 0 && CorruptPngFiles.Count == 0 && DimensionFailures.Count == 0 && EnumerationFailures.Count == 0 && ActualFrameCount == ExpectedFrameCount;
        public string Format() => $"AI Frame Set Validation{Environment.NewLine}Chunk: {(ChunkNumber > 0 ? $"{ChunkNumber}/{ChunkTotal}" : "<not-applicable>")}{Environment.NewLine}Source Frame Directory: {SourceDirectory}{Environment.NewLine}Restored Frame Directory: {RestoredDirectory}{Environment.NewLine}Expected Frame Count: {ExpectedFrameCount}{Environment.NewLine}Actual Frame Count: {ActualFrameCount}{Environment.NewLine}Validation Start: {ValidationStartedAt:O}{Environment.NewLine}Validation Finish: {ValidationFinishedAt:O}{Environment.NewLine}Directory Enumeration Time: {DirectoryEnumerationElapsed:g}{Environment.NewLine}Image Verification Time: {ImageVerificationElapsed:g}{Environment.NewLine}Time Waiting for Filesystem: {FilesystemWaitElapsed:g}{Environment.NewLine}Missing: {List(MissingFrameNumbers)}{Environment.NewLine}Duplicate: {List(DuplicateFrameNumbers)}{Environment.NewLine}Unexpected: {List(UnexpectedFilenames)}{Environment.NewLine}Zero-byte: {List(ZeroByteFiles)}{Environment.NewLine}Unreadable: {List(UnreadableImages)}{Environment.NewLine}Output files were still being written: {List(FilesStillChanging)}{Environment.NewLine}Incorrect extension: {List(IncorrectExtensions)}{Environment.NewLine}Filename ordering errors: {List(FilenameOrderingErrors)}{Environment.NewLine}Corrupt PNG: {List(CorruptPngFiles)}{Environment.NewLine}Dimension failures: {List(DimensionFailures)}{Environment.NewLine}Enumeration failures: {List(EnumerationFailures)}";
        private static string List(IReadOnlyCollection<string> values) => values.Count == 0 ? "none" : string.Join(Environment.NewLine, values);
    }

    private sealed class AiForensicContext
    {
        private readonly string _sourcePath, _root;
        private readonly CancellationToken _token;
        private int _chunkIndex, _chunkTotal, _expectedCount;
        private string? _chunkDirectory, _inputDirectory, _outputDirectory;
        private string _validationStage = "<not-started>";
        private string? _validationDirectory;
        private IReadOnlyList<string>? _validationExpected;
        private string? _extractionExecutable;
        private IReadOnlyList<string>? _extractionArguments;
        private MediaToolProcessResult? _extractionResult;
        public AiDirectoryProcessDiagnostic? NcnnDiagnostic { get; set; }
        public AiForensicContext(string sourcePath, string root, CancellationToken token) { _sourcePath = sourcePath; _root = root; _token = token; }
        public void SetChunk(int chunkIndex, int chunkTotal, string chunkDirectory, string inputDirectory, string outputDirectory, int expectedCount)
        { _chunkIndex = chunkIndex; _chunkTotal = chunkTotal; _chunkDirectory = chunkDirectory; _inputDirectory = inputDirectory; _outputDirectory = outputDirectory; _expectedCount = expectedCount; }
        public void SetValidation(string stage, string directory, IReadOnlyList<string> expected, string? extractionExecutable, IReadOnlyList<string>? extractionArguments, MediaToolProcessResult? extractionResult)
        { _validationStage = stage; _validationDirectory = directory; _validationExpected = expected; _extractionExecutable = extractionExecutable; _extractionArguments = extractionArguments; _extractionResult = extractionResult; }
        public string BuildReport(Exception exception)
        {
            DateTimeOffset started = DateTimeOffset.UtcNow;
            string? directory = _validationDirectory ?? _outputDirectory;
            FileInfo[] actual = Enumerate(directory);
            string[] expected = _validationExpected?.ToArray() ?? (directory is null ? Array.Empty<string>() : ExpectedFrames(directory, _expectedCount));
            var expectedNames = new HashSet<string>(expected.Select(path => Path.GetFileName(path)!), StringComparer.Ordinal);
            string[] missing = expected.Where(path => !File.Exists(path)).Select(path => Path.GetFileName(path)!).ToArray();
            string[] unexpected = actual.Select(file => file.Name).Where(name => !expectedNames.Contains(name)).ToArray();
            var numbered = actual.Select(file => (File: file, Stem: Path.GetFileNameWithoutExtension(file.Name)))
                .Where(value => value.Stem.StartsWith("frame-", StringComparison.OrdinalIgnoreCase) && int.TryParse(value.Stem[6..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                .Select(value => (value.File, Number: int.Parse(value.Stem[6..], CultureInfo.InvariantCulture))).ToArray();
            string[] duplicates = numbered.GroupBy(value => value.Number).Where(group => group.Count() > 1).Select(group => $"{group.Key:D8}: {string.Join(", ", group.Select(value => value.File.Name))}").ToArray();
            int[] numbers = numbered.Select(value => value.Number).Distinct().OrderBy(value => value).ToArray();
            string[] gaps = numbers.Length < 2 ? Array.Empty<string>() : Enumerable.Range(numbers[0], numbers[^1] - numbers[0] + 1).Except(numbers).Select(value => $"frame-{value:D8}.png").ToArray();
            string pattern = numbered.Length == 0 ? "none" : string.Join(", ", numbered.GroupBy(value => (Digits: Path.GetFileNameWithoutExtension(value.File.Name).Length - 6, Extension: value.File.Extension), value => value.File.Name).Select(group => $"frame-{'0'}x{group.Key.Digits}{group.Key.Extension} ({group.Count()})"));
            DriveInfo? drive = TryGetDrive(_root);
            DateTimeOffset finished = DateTimeOffset.UtcNow;
            AiDirectoryProcessDiagnostic? process = NcnnDiagnostic;
            string ncnnNotInvoked = "not invoked (validation failed before inference)";
            string extractionArguments = _extractionArguments is null ? "<not-run>" : string.Join(" ", _extractionArguments);
            string extractionExitCode = _extractionResult?.ExitCode.ToString(CultureInfo.InvariantCulture) ?? "<not-run>";
            string extractionTimedOut = _extractionResult?.TimedOut.ToString() ?? "<not-run>";
            return $"AI Restoration Forensic Failure Report{Environment.NewLine}Validation Stage: {_validationStage}{Environment.NewLine}Chunk: {(_chunkIndex > 0 ? $"{_chunkIndex}/{_chunkTotal}" : "<not-started>")}{Environment.NewLine}Source File: {_sourcePath}{Environment.NewLine}Working Directory (preserved): {_root}{Environment.NewLine}Chunk Working Directory: {_chunkDirectory ?? "<not-created>"}{Environment.NewLine}Input Directory: {_inputDirectory ?? "<not-created>"}{Environment.NewLine}Output Directory: {_outputDirectory ?? "<not-created>"}{Environment.NewLine}Validation Directory: {directory ?? "<not-created>"}{Environment.NewLine}Expected Frame Count: {expected.Length}{Environment.NewLine}Actual Frame Count: {actual.Length}{Environment.NewLine}First/Last Expected Frame: {Path.GetFileName(expected.FirstOrDefault()) ?? "none"} / {Path.GetFileName(expected.LastOrDefault()) ?? "none"}{Environment.NewLine}First/Last Actual Frame: {actual.FirstOrDefault()?.Name ?? "none"} / {actual.LastOrDefault()?.Name ?? "none"}{Environment.NewLine}Missing Frame Filenames ({missing.Length}; first 50): {List(missing, 50)}{Environment.NewLine}Unexpected Frame Filenames ({unexpected.Length}; first 50): {List(unexpected, 50)}{Environment.NewLine}Duplicate Frame Filenames: {List(duplicates, 50)}{Environment.NewLine}Sequential Numbering Gaps: {List(gaps, 50)}{Environment.NewLine}Frame Numbering Pattern Detected: {pattern}{Environment.NewLine}Zero-byte Files: {List(actual.Where(file => file.Length == 0).Select(file => file.Name), 50)}{Environment.NewLine}Corrupt/Unreadable PNGs: {List(actual.Where(file => file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)).Where(file => !IsReadablePng(file.FullName)).Select(file => file.Name), 50)}{Environment.NewLine}Directory File Count/Listing Summary: {actual.Length}; {List(actual.Select(file => file.Name), 50)}{Environment.NewLine}FFmpeg Extraction Executable: {_extractionExecutable ?? "<not-run>"}{Environment.NewLine}FFmpeg Extraction Arguments: {extractionArguments}{Environment.NewLine}FFmpeg Extraction Exit Code: {extractionExitCode}; Timed Out: {extractionTimedOut}; Stderr: {LastLines(_extractionResult?.StandardError, 100)}{Environment.NewLine}NCNN Executable: {process?.ExecutablePath ?? ncnnNotInvoked}{Environment.NewLine}NCNN Arguments: {process?.CommandLine ?? ncnnNotInvoked}{Environment.NewLine}NCNN Exit Code: {process?.ExitCode.ToString(CultureInfo.InvariantCulture) ?? ncnnNotInvoked}{Environment.NewLine}Process Runtime: {process?.Elapsed.ToString("g") ?? ncnnNotInvoked}{Environment.NewLine}Timed Out: {process?.TimedOut.ToString() ?? ncnnNotInvoked}; Cancellation Requested: {_token.IsCancellationRequested}{Environment.NewLine}NCNN Stdout (last 100 lines):{Environment.NewLine}{LastLines(process?.StandardOutput, 100)}{Environment.NewLine}NCNN Stderr (last 100 lines):{Environment.NewLine}{LastLines(process?.StandardError, 100)}{Environment.NewLine}Validation Start: {started:O}{Environment.NewLine}Validation End: {finished:O}{Environment.NewLine}Validation Duration: {(finished - started):g}{Environment.NewLine}Filesystem Stabilization Attempts: 1 observation only (no retry){Environment.NewLine}Directory Enumeration Retries: 0{Environment.NewLine}Total Files in Working Directory: {CountFiles(_root)}{Environment.NewLine}Disk Free Space: {(drive is null ? "<unavailable>" : FormatBytes(drive.AvailableFreeSpace))}{Environment.NewLine}Inner Exception Details:{Environment.NewLine}{exception}";
        }
        private static FileInfo[] Enumerate(string? directory) { try { return directory is not null && Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Select(path => new FileInfo(path)).OrderBy(file => file.Name, StringComparer.Ordinal).ToArray() : Array.Empty<FileInfo>(); } catch { return Array.Empty<FileInfo>(); } }
        private static long CountFiles(string directory) { try { return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).LongCount() : 0; } catch { return -1; } }
        private static DriveInfo? TryGetDrive(string path) { try { string? root = Path.GetPathRoot(path); return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root); } catch { return null; } }
        private static string LastLines(string? value, int count) => string.IsNullOrWhiteSpace(value) ? "<none>" : string.Join(Environment.NewLine, value.Replace("\r", "").Split('\n').TakeLast(count));
        private static string List(IEnumerable<string> values, int limit) { string[] array = values.ToArray(); return array.Length == 0 ? "none" : string.Join(Environment.NewLine, array.Take(limit)); }
        private static bool IsReadablePng(string path) { try { ReadPngDimensions(path); return true; } catch { return false; } }
    }
    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        byte[] header = new byte[24];
        using var stream = File.OpenRead(path);
        if (stream.Read(header, 0, header.Length) != header.Length ||
            header[0] != 137 || header[1] != 80 || header[2] != 78 || header[3] != 71 ||
            header[4] != 13 || header[5] != 10 || header[6] != 26 || header[7] != 10 ||
            header[12] != 73 || header[13] != 72 || header[14] != 68 || header[15] != 82)
            throw new AiRestorationValidationException($"AI restored frame '{Path.GetFileName(path)}' is not a readable PNG.");
        int width = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)), height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));
        if (width <= 0 || height <= 0) throw new AiRestorationValidationException($"AI restored frame '{Path.GetFileName(path)}' has invalid PNG dimensions.");
        return (width, height);
    }
    internal static IReadOnlyList<string> BuildExtractArguments(AiIntermediateVideoRequest r, int offset, int count, string dir)
    { double seconds = (r.Start ?? TimeSpan.Zero).TotalSeconds + offset / r.FrameRate; return new[] { "-y", "-ss", seconds.ToString("0.######", CultureInfo.InvariantCulture), "-i", r.SourcePath, "-frames:v", count.ToString(), "-vf", string.IsNullOrWhiteSpace(r.Plan.PreAiFilterChain) ? "fps=" + r.FrameRate.ToString("0.######", CultureInfo.InvariantCulture) : r.Plan.PreAiFilterChain + ",fps=" + r.FrameRate.ToString("0.######", CultureInfo.InvariantCulture), "-start_number", "0", Path.Combine(dir, "frame-%08d.png") }; }
    private static IReadOnlyList<string> BuildReassemblyArguments(string first, double fps, string output) => new[] { "-y", "-framerate", fps.ToString("0.######", CultureInfo.InvariantCulture), "-start_number", "0", "-i", Path.Combine(Path.GetDirectoryName(first)!, "frame-%08d.png"), "-map", "0:v:0", "-c:v", "ffv1", "-level", "3", output };
    internal static bool ShouldJoinChunks(int count) => count > 1;
    internal static IReadOnlyList<string> BuildConcatListLines(IEnumerable<string> paths) => paths.Select(path => "file '" + path.Replace("'", "'\\''") + "'").ToArray();
    internal static Task WriteConcatListAsync(string path, IEnumerable<string> chunks, CancellationToken token = default) =>
        File.WriteAllLinesAsync(path, BuildConcatListLines(chunks), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), token);
    internal static void ValidateChunkCompatibility(IReadOnlyList<AiIntermediateChunkMetadata> chunks)
    {
        if (chunks.Count < 2) return;
        AiIntermediateChunkMetadata first = chunks[0];
        foreach (AiIntermediateChunkMetadata chunk in chunks.Skip(1))
            if (!string.Equals(first.Codec, chunk.Codec, StringComparison.OrdinalIgnoreCase) || first.Width != chunk.Width || first.Height != chunk.Height || !string.Equals(first.PixelFormat, chunk.PixelFormat, StringComparison.OrdinalIgnoreCase) || !string.Equals(first.TimeBase, chunk.TimeBase, StringComparison.Ordinal) || !string.Equals(first.FrameRate, chunk.FrameRate, StringComparison.Ordinal))
                throw new AiRestorationValidationException($"AI intermediate chunks are incompatible and cannot be joined without altering timing: '{Path.GetFileName(first.Path)}' ({Describe(first)}) vs '{Path.GetFileName(chunk.Path)}' ({Describe(chunk)}).");
    }
    private async Task<MediaToolProcessResult> RunAsync(IReadOnlyList<string> args, string action, IReadOnlyList<AiIntermediateChunkMetadata> chunks, CancellationToken token)
    {
        MediaToolProcessResult result = await _runner.RunAsync(new MediaToolProcessRequest
        {
            FileName = _ffmpegPath,
            Arguments = args,
            Timeout = TimeSpan.FromMinutes(5),
            SendQuitOnCancellation = true,
            ProcessStartedCallback = launch => _log?.Invoke(
                $"[AI Intermediate] FFmpeg launch; pid={launch.ProcessId}; executable={launch.FileName}; workingDirectory={launch.WorkingDirectory}; argumentList={string.Join(" | ", launch.ArgumentList)}")
        }, token).ConfigureAwait(false);
        if (result.ExitCode == 0 && !result.TimedOut) return result;
        string diagnostic = BuildFailureDiagnostic(action, _ffmpegPath, args, result, chunks); _log?.Invoke(diagnostic); throw new AiRestorationValidationException(diagnostic);
    }
    internal static string BuildFailureDiagnostic(string action, string executable, IReadOnlyList<string> args, MediaToolProcessResult result, IReadOnlyList<AiIntermediateChunkMetadata> chunks)
    {
        string stderr = Sanitize(result.StandardError, 4096); string summary = chunks.Count == 0 ? "none" : string.Join("; ", chunks.Select(chunk => Path.GetFileName(chunk.Path) + " (" + Describe(chunk) + ")"));
        return $"AI intermediate {action} failed. executable={Sanitize(executable, 512)}; arguments={string.Join(" ", args.Select(argument => Sanitize(argument, 512)))}; exitCode={result.ExitCode}; timedOut={result.TimedOut}; chunks={chunks.Count}; chunkMetadata={summary}; stderr={stderr}";
    }
    private async Task<AiIntermediateChunkMetadata> ProbeChunkAsync(string path, int expectedFrames, double fps, CancellationToken token)
    {
        AiIntermediateVideoResult result = await ValidateAsync(path, TimeSpan.FromSeconds(expectedFrames / fps), expectedFrames, fps, AiRestorationScale.X1, token).ConfigureAwait(false); Dictionary<string, string> map = await ProbeAsync(path, token).ConfigureAwait(false);
        string Required(string key) => map.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw new AiRestorationValidationException($"AI intermediate chunk validation failed: '{Path.GetFileName(path)}' is missing {key}.");
        return new(path, Required("codec_name"), result.Width, result.Height, Required("pix_fmt"), Required("time_base"), Required("r_frame_rate"), result.FrameCount, result.Duration.TotalSeconds);
    }
    private async Task<AiIntermediateVideoResult> ValidateAsync(string path, TimeSpan expectedDuration, int expectedFrames, double fps, AiRestorationScale scale, CancellationToken token)
    {
        Dictionary<string, string> map = await ProbeAsync(path, token).ConfigureAwait(false);
        if (!map.TryGetValue("width", out string? w) || !map.TryGetValue("height", out string? h) || !int.TryParse(w, out int width) || !int.TryParse(h, out int height)) throw new AiRestorationValidationException("AI intermediate validation failed: no readable video stream.");
        double duration = map.TryGetValue("duration", out string? d) && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
        if (Math.Abs(duration - expectedDuration.TotalSeconds) > Math.Max(1, 2 / fps)) throw new AiRestorationValidationException($"AI intermediate validation failed: duration mismatch (expected {expectedDuration.TotalSeconds:0.###}, actual {duration:0.###}).");
        if (map.TryGetValue("nb_frames", out string? frames) && long.TryParse(frames, out long actualFrames) && Math.Abs(actualFrames - expectedFrames) > 1) throw new AiRestorationValidationException($"AI intermediate validation failed: frame-count mismatch (expected {expectedFrames}, actual {actualFrames}).");
        return new(path, TimeSpan.FromSeconds(duration), expectedFrames, width, height, fps);
    }
    private async Task<Dictionary<string, string>> ProbeAsync(string path, CancellationToken token)
    {
        MediaToolProcessResult result = await _runner.RunAsync(new MediaToolProcessRequest { FileName = _ffprobePath, Arguments = new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height,pix_fmt,time_base,nb_frames,r_frame_rate:format=duration", "-of", "default=noprint_wrappers=1", path }, Timeout = TimeSpan.FromSeconds(30), SendQuitOnCancellation = true }, token).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.TimedOut) throw new AiRestorationValidationException(BuildFailureDiagnostic("validate AI intermediate", _ffprobePath, Array.Empty<string>(), result, Array.Empty<AiIntermediateChunkMetadata>()));
        return result.StandardOutput.Split('\n').Select(line => line.Trim()).Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0])).GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
    }
    private static string Describe(AiIntermediateChunkMetadata chunk) => $"codec={chunk.Codec}, {chunk.Width}x{chunk.Height}, pix_fmt={chunk.PixelFormat}, time_base={chunk.TimeBase}, fps={chunk.FrameRate}";
    private static string Describe(NcnnRuntimeConfigurationSource source) => source switch
    {
        NcnnRuntimeConfigurationSource.AutoTuned => "Auto-tuned",
        NcnnRuntimeConfigurationSource.Cached => "Cached",
        NcnnRuntimeConfigurationSource.BenchmarkDatabase => "Benchmark database",
        _ => "Safe default"
    };
    private static string FormatPlannerDecision(AiChunkPlannerDecision decision, string backend) =>
        "[AI Chunk Planner]" + Environment.NewLine +
        $"Resolution: {decision.SourceWidth}x{decision.SourceHeight}; AI Scale: {(int)decision.AiScale}x; Estimated Bytes per Frame: {FormatBytes(decision.EstimatedBytesPerFrame)}" + Environment.NewLine +
        $"Estimated Peak Extracted Storage: {FormatBytes(decision.EstimatedPeakExtractedStorageBytes)}; Estimated Peak Restored Storage: {FormatBytes(decision.EstimatedPeakRestoredStorageBytes)}" + Environment.NewLine +
        $"Estimated Intermediate Storage: {FormatBytes(decision.EstimatedIntermediateStorageBytes)}; Active Working Files: {FormatBytes(decision.ActiveWorkingFilesBytes)}; Safety Margin: {FormatBytes(decision.SafetyMarginBytes)}" + Environment.NewLine +
        $"Final Required Storage: {FormatBytes(decision.FinalRequiredStorageBytes)}; Available Storage: {FormatBytes(decision.AvailableTemporaryStorageBytes)}; GPU VRAM: {FormatBytes(decision.DedicatedGpuVramBytes)}" + Environment.NewLine +
        $"Default Chunk Size: {decision.DefaultChunkSize}; Storage-Limited Chunk Size: {decision.StorageLimitedChunkSize}; VRAM-Limited Chunk Size: {decision.VramLimitedChunkSize}; Final Selected Chunk Size: {decision.FinalSelectedChunkSize}" + Environment.NewLine +
        $"Constraint: {decision.DeterminingConstraint}; Decision Reason: {decision.DecisionReason}; Backend: {backend}.";
    private static string FormatChunkMetrics(AiChunkPerformanceMetrics metrics) =>
        $"[AI Chunk {metrics.ChunkNumber}] Frames Expected: {metrics.FrameCount}; Frames Restored: {metrics.FrameCount}; Frames Validated: {metrics.FrameCount}; Frames Failed: 0; Elapsed Extraction: {Format(metrics.ExtractionElapsed)}; Elapsed Restoration: {Format(metrics.InferenceElapsed)}; Elapsed Validation: {Format(metrics.ValidationElapsed)}; Elapsed Reassembly: {Format(metrics.ReassemblyElapsed)}; Peak RAM: {FormatBytes(Process.GetCurrentProcess().PeakWorkingSet64)}; Peak VRAM: {FormatBytes(metrics.Hardware.PeakVramUsedBytes)}; GPU Utilization Average: {Percent(metrics.Hardware.AverageGpuPercent)}; CPU Utilization Average: {Percent(metrics.Hardware.AverageCpuPercent)}; Disk Throughput Average: {BytesPerSecond(metrics.Hardware.AverageDiskThroughputBytesPerSecond)}; Startup/Shutdown: {Format(metrics.StartupShutdownOverhead)}; FFmpeg Launch: {Format(metrics.FfmpegProcessLaunchElapsed)}; Total: {Format(metrics.TotalElapsed)}; FPS: {metrics.EffectiveFramesPerSecond:0.##}; GPU Peak: {Percent(metrics.Hardware.PeakGpuPercent)}; VRAM Average: {FormatBytes(metrics.Hardware.AverageVramUsedBytes)}.";
    private static long MeasureTemporaryStorage(string chunkDirectory, string chunkVideo)
    {
        try
        {
            long total = Directory.EnumerateFiles(chunkDirectory, "*", SearchOption.AllDirectories)
                .Aggregate(0L, (current, path) => SaturatingAdd(current, new FileInfo(path).Length));
            return File.Exists(chunkVideo) ? SaturatingAdd(total, new FileInfo(chunkVideo).Length) : total;
        }
        catch { return 0; }
    }
    private static long SaturatingAdd(long value, long addition) => addition > 0 && value > long.MaxValue - addition ? long.MaxValue : value + Math.Max(0, addition);
    private static string Format(TimeSpan elapsed) => elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss\.fff") : elapsed.ToString(@"m\:ss\.fff");
    private static string FormatBytes(long? bytes) => bytes is not long value ? "Unavailable"
        : value >= 1024L * 1024 * 1024 ? $"{value / 1024d / 1024d / 1024d:0.0} GB"
        : $"{value / 1024d / 1024d:0.##} MiB";
    private static string Percent(double? value) => value.HasValue ? $"{value:0.#}%" : "Unavailable";
    private static string BytesPerSecond(double? value) => value.HasValue ? $"{value.Value / 1024d / 1024d:0.##} MiB/s" : "Unavailable";
    private static string Sanitize(string? value, int maximum) { string sanitized = string.IsNullOrWhiteSpace(value) ? "<none>" : value.Replace("\r", " ").Replace("\n", " ").Trim(); return sanitized[..Math.Min(sanitized.Length, maximum)]; }
}
