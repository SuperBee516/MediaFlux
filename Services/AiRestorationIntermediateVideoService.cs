using System.Globalization;
using System.Buffers.Binary;
using System.Diagnostics;
using MediaFlux.Models;

namespace MediaFlux.Services;

public enum AiIntermediateStage { ExtractingFrames, AiProcessing, Reassembling, Validating }
public sealed record AiIntermediateProgress(AiIntermediateStage Stage, int Current, int Total, string Message);
public sealed record AiIntermediateVideoRequest(string SourcePath, double FrameRate, TimeSpan SourceDuration, VideoRestorationSettings Settings, VideoRestorationPipelinePlan Plan, TimeSpan? Start = null, TimeSpan? Duration = null, int SourceWidth = 0, int SourceHeight = 0, bool IsMotionPreview = false);
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
    private readonly Func<long?> _dedicatedGpuVramProvider;

    public AiRestorationIntermediateVideoService(string ffmpegPath, string ffprobePath, string stagingRoot, IAiRestorationBackend backend, IMediaToolProcessRunner? runner = null, Action<string>? log = null, PerformanceTimingService? timing = null, Func<long?>? dedicatedGpuVramProvider = null)
    { _ffmpegPath = ffmpegPath; _ffprobePath = ffprobePath; _stagingRoot = stagingRoot; _backend = backend; _runner = runner ?? new MediaToolProcessRunner(); _log = log; _timing = timing; _dedicatedGpuVramProvider = dedicatedGpuVramProvider ?? HardwarePerformanceService.DetectDedicatedGpuVramBytes; }

    public async Task<AiIntermediateVideoResult> CreateAsync(AiIntermediateVideoRequest request, IProgress<AiIntermediateProgress>? progress = null, CancellationToken token = default)
    {
        if (!File.Exists(request.SourcePath)) throw new FileNotFoundException("AI intermediate source is unavailable.", request.SourcePath);
        if (request.FrameRate is < 1 or > 240 || double.IsNaN(request.FrameRate)) throw new AiRestorationValidationException("AI intermediate processing requires a known constant frame rate between 1 and 240 fps; VFR sources are not supported yet.");
        if (!request.Plan.UsesAi) throw new AiRestorationValidationException("AI intermediate processing requires AI restoration to be enabled.");
        AiRestorationSession session;
        using (PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiPreparation))
        { session = await _backend.CreateSessionAsync(request.Settings, token).ConfigureAwait(false); scope?.Complete(); }
        _timing?.SetAiBackend(session.Capabilities.BackendId, session.Capabilities.ExecutablePath);
        AiChunkHardwareMetricsCollector? activeChunkHardware = null;
        using HardwarePerformanceSampler? hardwareSampler = _timing is null ? null : new HardwarePerformanceSampler(
            _timing,
            sampleObserver: sample => Volatile.Read(ref activeChunkHardware)?.Record(sample));
        hardwareSampler?.SampleNow();
        Directory.CreateDirectory(_stagingRoot); AiProductionHardeningService.CleanupOrphans(_stagingRoot); string root = Path.Combine(_stagingRoot, "ai-intermediate-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); AiProductionHardeningService.Register(root);
        try
        {
            TimeSpan duration = request.Duration ?? request.SourceDuration;
            if (duration <= TimeSpan.Zero) throw new AiRestorationValidationException("AI intermediate processing requires a known source duration.");
            int total = checked((int)Math.Round(duration.TotalSeconds * request.FrameRate, MidpointRounding.AwayFromZero));
            if (total <= 0) throw new AiRestorationValidationException("AI intermediate processing could not determine an expected frame count.");
            AiTemporaryStorageEstimate planningEstimate = AiProductionHardeningService.Estimate(request.SourceWidth, request.SourceHeight, total, request.Settings.AiScale, _stagingRoot, AiChunkPlanner.MinimumFramesPerChunk);
            long? dedicatedGpuVram = _timing?.DedicatedGpuVramBytes ?? _dedicatedGpuVramProvider();
            var planner = new AiChunkPlanner();
            var plannerInput = new AiChunkPlannerInput(request.SourceWidth, request.SourceHeight, request.Settings.AiScale, dedicatedGpuVram, planningEstimate, session.Capabilities.Identity);
            AiChunkPlan chunkPlan = planner.Plan(plannerInput);
            AiTemporaryStorageEstimate estimate = AiProductionHardeningService.Estimate(request.SourceWidth, request.SourceHeight, total, request.Settings.AiScale, _stagingRoot, chunkPlan.FrameCount);
            AiChunkPlannerDecision plannerDecision = planner.DescribeDecision(plannerInput with { TemporaryStorageEstimate = estimate }, chunkPlan);
            _timing?.SetAiChunkPlannerDecision(plannerDecision);
            _log?.Invoke(FormatPlannerDecision(plannerDecision, session.Capabilities.Identity));
            AiProductionHardeningService.EnsureSpace(estimate); using (File.Create(Path.Combine(root, ".mediaflux-ai-staging"))) { }
            var chunks = new List<AiIntermediateChunkMetadata>();
            NcnnRuntimeSelection runtimeSelection = new(NcnnRuntimeConfiguration.SafeDefault, NcnnRuntimeConfigurationSource.SafeDefault);
            int chunkTotal = (total + chunkPlan.FrameCount - 1) / chunkPlan.FrameCount;
            for (int offset = 0, chunkIndex = 0; offset < total; offset += chunkPlan.FrameCount, chunkIndex++)
            {
                var chunkHardware = new AiChunkHardwareMetricsCollector();
                Volatile.Write(ref activeChunkHardware, chunkHardware);
                hardwareSampler?.SampleNow();
                long chunkStartedAt = Stopwatch.GetTimestamp();
                int count = Math.Min(chunkPlan.FrameCount, total - offset);
                AiProductionHardeningService.EnsureSpace(AiProductionHardeningService.Estimate(request.SourceWidth, request.SourceHeight, count, request.Settings.AiScale, _stagingRoot, chunkPlan.FrameCount), runtime: true);
                string chunk = Path.Combine(root, $"chunk-{chunkIndex:D5}"), input = Path.Combine(chunk, "input"), output = Path.Combine(chunk, "output"); Directory.CreateDirectory(input); Directory.CreateDirectory(output);
                progress?.Report(new(AiIntermediateStage.ExtractingFrames, offset, total, $"Extracting AI frames (chunk {chunkIndex + 1}/{chunkTotal})"));
                long stageStartedAt = Stopwatch.GetTimestamp();
                using (PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiExtraction))
                { await RunAsync(BuildExtractArguments(request, offset, count, input), "extract AI frames", chunks, token).ConfigureAwait(false); scope?.Complete(); }
                TimeSpan extractionElapsed = Stopwatch.GetElapsedTime(stageStartedAt);
                TimeSpan validationElapsed = TimeSpan.Zero;
                string[] extracted = ExpectedFrames(input, count);
                stageStartedAt = Stopwatch.GetTimestamp();
                using (PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiValidation))
                { ValidateFrameSet(input, extracted); scope?.Complete(); }
                validationElapsed += Stopwatch.GetElapsedTime(stageStartedAt);
                if (chunkIndex == 0)
                {
                    string? capturedGpuIdentity = _timing?.GpuIdentity;
                    string gpuIdentity = capturedGpuIdentity ?? HardwarePerformanceService.DetectGpuIdentity();
                    bool canTune = !request.IsMotionPreview && !string.IsNullOrWhiteSpace(capturedGpuIdentity) && !capturedGpuIdentity.Equals("Unavailable", StringComparison.OrdinalIgnoreCase) && _timing?.DedicatedGpuVramBytes is > 0;
                    runtimeSelection = await new NcnnPerformanceAutoTuner(_backend, log: _log).SelectAsync(
                        session, request.Settings, input, Path.Combine(root, "ncnn-tuning"), request.SourceWidth, request.SourceHeight,
                        gpuIdentity, _timing?.DedicatedGpuVramBytes, canTune, token).ConfigureAwait(false);
                    _timing?.SetNcnnRuntimeSelection(runtimeSelection);
                    _log?.Invoke($"NCNN Performance Configuration{Environment.NewLine}GPU: {gpuIdentity}; Model: {session.Model.BackendModelName}; Scale: {(int)request.Settings.AiScale}x; Resolution: {request.SourceWidth}x{request.SourceHeight}; Threads: {runtimeSelection.Configuration.ThreadsDisplay}; Tile: {runtimeSelection.Configuration.TileDisplay}; Source: {Describe(runtimeSelection.Source)}.");
                }
                string[] processed = ExpectedFrames(output, count);
                progress?.Report(new(AiIntermediateStage.AiProcessing, offset, total, $"AI restoring frames (chunk {chunkIndex + 1}/{chunkTotal})"));
                stageStartedAt = Stopwatch.GetTimestamp();
                using (PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiProcessing))
                { await _backend.ProcessDirectoryAsync(
                    session,
                    request.Settings,
                    input,
                    output,
                    processed,
                    completed => progress?.Report(new(AiIntermediateStage.AiProcessing, offset + completed, total, $"AI restoring frames (chunk {chunkIndex + 1}/{chunkTotal})")),
                    token,
                    runtimeSelection.Configuration).ConfigureAwait(false); scope?.Complete(); }
                TimeSpan inferenceElapsed = Stopwatch.GetElapsedTime(stageStartedAt);
                stageStartedAt = Stopwatch.GetTimestamp();
                using (PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiValidation))
                { ValidateRestoredFrameSet(extracted, processed, request.Settings.AiScale); scope?.Complete(); }
                validationElapsed += Stopwatch.GetElapsedTime(stageStartedAt);
                string chunkVideo = Path.Combine(root, $"chunk-{chunkIndex:D5}.mkv");
                progress?.Report(new(AiIntermediateStage.Reassembling, offset + count, total, $"Reassembling AI frames (chunk {chunkIndex + 1}/{chunkTotal})"));
                stageStartedAt = Stopwatch.GetTimestamp();
                using (PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiReassembly))
                { await RunAsync(BuildReassemblyArguments(processed[0], request.FrameRate, chunkVideo), "reassemble AI chunk", chunks, token).ConfigureAwait(false); scope?.Complete(); }
                TimeSpan reassemblyElapsed = Stopwatch.GetElapsedTime(stageStartedAt);
                stageStartedAt = Stopwatch.GetTimestamp();
                using (PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiValidation))
                { chunks.Add(await ProbeChunkAsync(chunkVideo, count, request.FrameRate, token).ConfigureAwait(false)); scope?.Complete(); }
                validationElapsed += Stopwatch.GetElapsedTime(stageStartedAt);
                TimeSpan totalElapsed = Stopwatch.GetElapsedTime(chunkStartedAt);
                hardwareSampler?.SampleNow();
                long measuredTemporaryStorage = MeasureTemporaryStorage(chunk, chunkVideo);
                AiChunkPerformanceMetrics metrics = new(chunkIndex + 1, count, extractionElapsed, inferenceElapsed, validationElapsed, reassemblyElapsed, totalElapsed, chunkHardware.Snapshot(), measuredTemporaryStorage);
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
                await File.WriteAllLinesAsync(list, BuildConcatListLines(chunks.Select(chunk => chunk.Path)), token).ConfigureAwait(false);
                await RunAsync(new[] { "-y", "-f", "concat", "-safe", "0", "-i", list, "-map", "0:v:0", "-c:v", "copy", staging }, "join AI chunks", chunks, token).ConfigureAwait(false);
                scope?.Complete();
            }
            progress?.Report(new(AiIntermediateStage.Validating, total, total, "Validating AI intermediate video"));
            AiIntermediateVideoResult result;
            using (PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.AiValidation))
            { result = await ValidateAsync(staging, duration, total, request.FrameRate, request.Settings.AiScale, token).ConfigureAwait(false); scope?.Complete(); }
            File.Move(staging, final, true); return result with { Path = final, StagingDirectory = root };
        }
        catch { try { using PerformanceTimingService.PerformanceScope? scope = _timing?.Measure(PerformanceTimingStage.TemporaryFileCleanup); if (Directory.Exists(root)) Directory.Delete(root, true); scope?.Complete(); } catch { } throw; }
        finally { AiProductionHardeningService.Unregister(root); }
    }

    internal static string[] ExpectedFrames(string directory, int count) => Enumerable.Range(0, count).Select(index => Path.Combine(directory, $"frame-{index:D8}.png")).ToArray();
    internal static void ValidateFrameSet(string directory, IReadOnlyList<string> expected)
    {
        string[] actual = Directory.EnumerateFiles(directory, "*.png").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal) || actual.Any(path => new FileInfo(path).Length < 64))
            throw new AiRestorationValidationException("AI frame set is missing, incomplete, or contains unexpected frames.");
    }
    internal static void ValidateRestoredFrameSet(IReadOnlyList<string> inputs, IReadOnlyList<string> outputs, AiRestorationScale scale)
    {
        if (inputs.Count != outputs.Count || inputs.Count == 0)
            throw new AiRestorationValidationException("AI restored-frame validation requires matching non-empty input and output frame sets.");
        ValidateFrameSet(Path.GetDirectoryName(outputs[0])!, outputs);
        for (int index = 0; index < inputs.Count; index++)
        {
            (int inputWidth, int inputHeight) = ReadPngDimensions(inputs[index]);
            (int outputWidth, int outputHeight) = ReadPngDimensions(outputs[index]);
            int expectedWidth = checked(inputWidth * (int)scale), expectedHeight = checked(inputHeight * (int)scale);
            if (outputWidth != expectedWidth || outputHeight != expectedHeight)
                throw new AiRestorationValidationException($"AI restored frame '{Path.GetFileName(outputs[index])}' has invalid dimensions {outputWidth}x{outputHeight}; expected {expectedWidth}x{expectedHeight}.");
        }
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
    private static IReadOnlyList<string> BuildExtractArguments(AiIntermediateVideoRequest r, int offset, int count, string dir)
    { double seconds = (r.Start ?? TimeSpan.Zero).TotalSeconds + offset / r.FrameRate; return new[] { "-y", "-ss", seconds.ToString("0.######", CultureInfo.InvariantCulture), "-i", r.SourcePath, "-frames:v", count.ToString(), "-vf", string.IsNullOrWhiteSpace(r.Plan.PreAiFilterChain) ? "fps=" + r.FrameRate.ToString("0.######", CultureInfo.InvariantCulture) : r.Plan.PreAiFilterChain + ",fps=" + r.FrameRate.ToString("0.######", CultureInfo.InvariantCulture), "-start_number", "0", Path.Combine(dir, "frame-%08d.png") }; }
    private static IReadOnlyList<string> BuildReassemblyArguments(string first, double fps, string output) => new[] { "-y", "-framerate", fps.ToString("0.######", CultureInfo.InvariantCulture), "-start_number", "0", "-i", Path.Combine(Path.GetDirectoryName(first)!, "frame-%08d.png"), "-map", "0:v:0", "-c:v", "ffv1", "-level", "3", output };
    internal static bool ShouldJoinChunks(int count) => count > 1;
    internal static IReadOnlyList<string> BuildConcatListLines(IEnumerable<string> paths) => paths.Select(path => "file '" + path.Replace("'", "'\\''") + "'").ToArray();
    internal static void ValidateChunkCompatibility(IReadOnlyList<AiIntermediateChunkMetadata> chunks)
    {
        if (chunks.Count < 2) return;
        AiIntermediateChunkMetadata first = chunks[0];
        foreach (AiIntermediateChunkMetadata chunk in chunks.Skip(1))
            if (!string.Equals(first.Codec, chunk.Codec, StringComparison.OrdinalIgnoreCase) || first.Width != chunk.Width || first.Height != chunk.Height || !string.Equals(first.PixelFormat, chunk.PixelFormat, StringComparison.OrdinalIgnoreCase) || !string.Equals(first.TimeBase, chunk.TimeBase, StringComparison.Ordinal) || !string.Equals(first.FrameRate, chunk.FrameRate, StringComparison.Ordinal))
                throw new AiRestorationValidationException($"AI intermediate chunks are incompatible and cannot be joined without altering timing: '{Path.GetFileName(first.Path)}' ({Describe(first)}) vs '{Path.GetFileName(chunk.Path)}' ({Describe(chunk)}).");
    }
    private async Task RunAsync(IReadOnlyList<string> args, string action, IReadOnlyList<AiIntermediateChunkMetadata> chunks, CancellationToken token)
    {
        MediaToolProcessResult result = await _runner.RunAsync(new MediaToolProcessRequest { FileName = _ffmpegPath, Arguments = args, Timeout = TimeSpan.FromMinutes(5), SendQuitOnCancellation = true }, token).ConfigureAwait(false);
        if (result.ExitCode == 0 && !result.TimedOut) return;
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
        $"[AI Chunk {metrics.ChunkNumber}] Frames: {metrics.FrameCount}; Extraction: {Format(metrics.ExtractionElapsed)}; Inference: {Format(metrics.InferenceElapsed)}; Validation: {Format(metrics.ValidationElapsed)}; Reassembly: {Format(metrics.ReassemblyElapsed)}; Total: {Format(metrics.TotalElapsed)}; FPS: {metrics.EffectiveFramesPerSecond:0.##}; GPU Avg/Peak: {Percent(metrics.Hardware.AverageGpuPercent)}/{Percent(metrics.Hardware.PeakGpuPercent)}; VRAM Avg/Peak: {FormatBytes(metrics.Hardware.AverageVramUsedBytes)}/{FormatBytes(metrics.Hardware.PeakVramUsedBytes)}; CPU Avg: {Percent(metrics.Hardware.AverageCpuPercent)}; Disk Avg: {BytesPerSecond(metrics.Hardware.AverageDiskThroughputBytesPerSecond)}.";
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
