using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaFlux.Services;

namespace MediaFlux.Services.EncodingV2;

public enum EncodingV2HeadlessOperation { Probe, Plan, Run, RunQueue, CertifyQueue, Estimate, EstimateQualitySet, BenchmarkEstimates }

/// <summary>Versioned JSON request client for the same V2 core used by future GUI clients.</summary>
public static class EncodingV2HeadlessCommand
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static bool IsRequested(string[] args) => args.Length > 0 && args[0] is
        "--v2-probe" or "--v2-plan" or "--v2-run" or "--v2-run-queue" or "--v2-run-queue-certification" or
        "--v2-estimate" or "--v2-estimate-quality-set" or "--v2-benchmark-estimates";

    public static bool TryParse(string[] args, out EncodingV2HeadlessOperation operation,
        out string requestPath, out string error)
    {
        operation = default;
        requestPath = "";
        error = "";
        if (!IsRequested(args)) { error = "Expected a supported --v2-* command and one request JSON path."; return false; }
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            error = "Expected exactly one request JSON path after the V2 command.";
            return false;
        }
        operation = args[0] switch
        {
            "--v2-probe" => EncodingV2HeadlessOperation.Probe,
            "--v2-plan" => EncodingV2HeadlessOperation.Plan,
            "--v2-run" => EncodingV2HeadlessOperation.Run,
            "--v2-run-queue" => EncodingV2HeadlessOperation.RunQueue,
            "--v2-estimate" => EncodingV2HeadlessOperation.Estimate,
            "--v2-estimate-quality-set" => EncodingV2HeadlessOperation.EstimateQualitySet,
            "--v2-benchmark-estimates" => EncodingV2HeadlessOperation.BenchmarkEstimates,
            _ => EncodingV2HeadlessOperation.CertifyQueue
        };
        requestPath = args[1];
        return true;
    }

    public static int Run(string[] args)
    {
        if (!TryParse(args, out EncodingV2HeadlessOperation operation, out string requestPath, out string parseError))
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                operation = "unknown",
                status = "rejected",
                failure = new V2Failure("CommandRejected", parseError)
            }, JsonOptions));
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (operation is EncodingV2HeadlessOperation.Run or EncodingV2HeadlessOperation.RunQueue or EncodingV2HeadlessOperation.CertifyQueue)
            {
                using var primaryMutex = new Mutex(true, @"Local\Encode.ExplorerQueue.Primary", out bool isPrimary);
                if (!isPrimary)
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        operation = "run",
                        status = "rejected",
                        failure = new V2Failure("ApplicationAlreadyRunning",
                            "MediaFlux is already running. Close the GUI instance before starting a V2 headless encode to prevent overlapping source/output work.")
                    }, JsonOptions));
                    return 2;
                }
                return RunAsync(operation, requestPath, Console.Out, Console.Error, cancellation.Token)
                    .GetAwaiter().GetResult();
            }
            return RunAsync(operation, requestPath, Console.Out, Console.Error, cancellation.Token)
                .GetAwaiter().GetResult();
        }
        finally { Console.CancelKeyPress -= cancelHandler; }
    }

    public static async Task<int> RunAsync(
        EncodingV2HeadlessOperation operation,
        string requestPath,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        if (operation is EncodingV2HeadlessOperation.Estimate or EncodingV2HeadlessOperation.EstimateQualitySet or EncodingV2HeadlessOperation.BenchmarkEstimates)
            return await RunEstimateAsync(operation, requestPath, standardOutput, standardError, cancellationToken)
                .ConfigureAwait(false);
        if (operation is EncodingV2HeadlessOperation.RunQueue or EncodingV2HeadlessOperation.CertifyQueue)
            return await RunQueueAsync(operation, requestPath, standardOutput, standardError, cancellationToken)
                .ConfigureAwait(false);

        EncodingV2Request? request = null;
        try
        {
            string jsonPath = Path.GetFullPath(requestPath);
            string json = await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false);
            request = JsonSerializer.Deserialize<EncodingV2Request>(json, JsonOptions)
                ?? throw new JsonException("The request document was empty.");
            if (request.SchemaVersion != 1)
                throw new V2PlanningException("UnsupportedSchema", $"Unsupported V2 request schema version {request.SchemaVersion}.");

            var processRunner = new MediaToolProcessRunner();
            FfmpegToolPaths? tools = null;
            string ffprobePath;
            if (operation == EncodingV2HeadlessOperation.Probe && !string.IsNullOrWhiteSpace(request.FfprobePath))
            {
                ffprobePath = Path.GetFullPath(request.FfprobePath);
                if (!File.Exists(ffprobePath)) throw new V2PlanningException("FfprobeUnavailable", "The requested FFprobe executable does not exist.");
            }
            else
            {
                tools = FfmpegToolResolver.Resolve(AppContext.BaseDirectory, request.FfmpegPath, request.FfprobePath);
                ffprobePath = tools.FfprobePath;
            }

            if (operation != EncodingV2HeadlessOperation.Probe &&
                (tools is null || !tools.AreAllAvailable))
                throw new V2PlanningException("MediaToolsUnavailable", "A matching FFmpeg and FFprobe executable pair could not be resolved.");
            if (!File.Exists(ffprobePath))
                throw new V2PlanningException("FfprobeUnavailable", "FFprobe could not be resolved for the requested operation.");

            var metadata = new V2SourceMetadataProvider(new FfprobeService(ffprobePath, processRunner));
            if (operation == EncodingV2HeadlessOperation.Probe)
            {
                long started = Stopwatch.GetTimestamp();
                V2MediaFacts facts = await metadata.ProbeAsync(request.SourcePath, cancellationToken).ConfigureAwait(false);
                double probeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                await WriteJsonAsync(standardOutput, new
                {
                    schemaVersion = 1,
                    operation = "probe",
                    operationId = Guid.NewGuid().ToString("N"),
                    status = "completed",
                    media = PublicMediaFacts(facts),
                    timings = new { probeMilliseconds = probeMs, totalMilliseconds = probeMs }
                }).ConfigureAwait(false);
                return 0;
            }

            V2EncodingCore core = CreateCore(tools!, metadata, processRunner);
            if (operation == EncodingV2HeadlessOperation.Plan)
            {
                var planEvents = new List<V2LifecycleEvent>();
                long totalStarted = Stopwatch.GetTimestamp();
                string operationId = Guid.NewGuid().ToString("N");
                planEvents.Add(new V2LifecycleEvent(1, operationId, 1, DateTime.UtcNow,
                    V2LifecycleState.Queued, "job.queued", "V2 plan request accepted."));
                planEvents.Add(new V2LifecycleEvent(1, operationId, 2, DateTime.UtcNow,
                    V2LifecycleState.Probing, "source.probe.started", "Probing source metadata."));
                long probeStarted = Stopwatch.GetTimestamp();
                V2MediaFacts source = await core.ProbeAsync(request.SourcePath, cancellationToken).ConfigureAwait(false);
                double probeMs = Stopwatch.GetElapsedTime(probeStarted).TotalMilliseconds;
                planEvents.Add(new V2LifecycleEvent(1, operationId, 3, DateTime.UtcNow,
                    V2LifecycleState.Probing, "source.probe.completed", "Source metadata captured.", probeMs));
                long planStarted = Stopwatch.GetTimestamp();
                FrozenEncodePlan plan = core.CreatePlan(request, source);
                double planMs = Stopwatch.GetElapsedTime(planStarted).TotalMilliseconds;
                operationId = plan.JobId;
                for (int i = 0; i < planEvents.Count; i++)
                    planEvents[i] = planEvents[i] with
                    {
                        JobId = operationId,
                        SourcePath = plan.SourcePath,
                        OutputPath = plan.OutputPath,
                        StagingPath = plan.StagingPath,
                        Encoder = plan.Encoder,
                        QualityMode = plan.QualityMode,
                        QualityValue = plan.QualityValue
                    };
                planEvents.Add(new V2LifecycleEvent(1, operationId, 4, DateTime.UtcNow,
                    V2LifecycleState.Planned, "plan.frozen", "Frozen FFmpeg invocation is ready.", planMs,
                    SourcePath: plan.SourcePath, OutputPath: plan.OutputPath, StagingPath: plan.StagingPath,
                    Encoder: plan.Encoder, QualityMode: plan.QualityMode, QualityValue: plan.QualityValue));
                double totalMs = Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds;
                await WriteJsonAsync(standardOutput, new
                {
                    schemaVersion = 1,
                    operation = "plan",
                    operationId,
                    status = "planned",
                    source = PublicMediaFacts(source),
                    plan = PublicPlan(plan),
                    events = planEvents,
                    timings = new { probeMilliseconds = probeMs, planningMilliseconds = planMs, totalMilliseconds = totalMs }
                }).ConfigureAwait(false);
                return 0;
            }

            ValidateEventLogPath(request.EventLogPath, request.SourcePath, request.OutputPath);
            using V2JsonLineEventLog? eventLog = string.IsNullOrWhiteSpace(request.EventLogPath)
                ? null
                : new V2JsonLineEventLog(request.EventLogPath, JsonOptions);
            void Observe(V2LifecycleEvent item)
            {
                string line = JsonSerializer.Serialize(item, JsonOptions);
                try { standardError.WriteLine(line); standardError.Flush(); } catch { }
                try { eventLog?.Write(item); } catch { }
            }

            V2ExecutionResult result = await core.RunAsync(request, Observe, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(standardOutput, result).ConfigureAwait(false);
            return result.State switch
            {
                V2LifecycleState.Completed => 0,
                V2LifecycleState.Canceled => 130,
                _ => 1
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteJsonAsync(standardOutput, new
            {
                schemaVersion = 1,
                operation = operation.ToString().ToLowerInvariant(),
                status = "canceled",
                failure = new V2Failure("Canceled", "The V2 headless operation was canceled.")
            }).ConfigureAwait(false);
            return 130;
        }
        catch (Exception ex)
        {
            string code = ex is V2PlanningException planning ? planning.Code :
                ex is JsonException ? "InvalidRequestJson" : "HeadlessOperationFailed";
            await WriteJsonAsync(standardOutput, new
            {
                schemaVersion = 1,
                operation = operation.ToString().ToLowerInvariant(),
                status = "failed",
                failure = new V2Failure(code, ex.Message)
            }).ConfigureAwait(false);
            return 2;
        }
    }

    private static async Task<int> RunEstimateAsync(
        EncodingV2HeadlessOperation operation,
        string requestPath,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        long totalStarted = Stopwatch.GetTimestamp();
        try
        {
            string jsonPath = Path.GetFullPath(requestPath);
            string json = await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false);
            V2FastEstimateCommandRequest request = JsonSerializer.Deserialize<V2FastEstimateCommandRequest>(json, JsonOptions)
                ?? throw new JsonException("The estimate request document was empty.");
            if (request.SchemaVersion != 1)
                throw new V2PlanningException("UnsupportedSchema", $"Unsupported V2 estimate request schema version {request.SchemaVersion}.");

            if (operation == EncodingV2HeadlessOperation.EstimateQualitySet &&
                (request.QualityValues is null || request.QualityValues.Length == 0))
                throw new V2PlanningException("QualityValuesRequired", "qualityValues must contain at least one value for --v2-estimate-quality-set.");
            if (operation == EncodingV2HeadlessOperation.BenchmarkEstimates &&
                (request.Repetitions is < 2 or > 10000))
                throw new V2PlanningException("InvalidRepetitions", "benchmark repetitions must be between 2 and 10000.");
            if (request.QualityValues is { Length: > 100 })
                throw new V2PlanningException("TooManyQualityValues", "qualityValues may contain at most 100 values.");

            IV2SourceMetadataProvider? metadata = null;
            if (request.SourceFacts is null)
            {
                // This branch creates an FFprobe-only provider. The estimator never resolves,
                // starts, or passes an FFmpeg executable to the process runner.
                metadata = new V2SourceMetadataProvider(new FfprobeService(
                    AppContext.BaseDirectory, request.FfprobePath, new MediaToolProcessRunner()));
            }

            if (operation == EncodingV2HeadlessOperation.EstimateQualitySet)
            {
                (V2FastEstimateResolvedSource source, IReadOnlyList<V2FastEstimateResult> results) =
                    await V2FastEstimateService.EstimateQualitySetAsync(request, request.QualityValues!, metadata, cancellationToken)
                        .ConfigureAwait(false);
                await WriteJsonAsync(standardOutput, new
                {
                    schemaVersion = 1,
                    operation = "estimateQualitySet",
                    status = "completed",
                    source = PublicEstimateSource(source.Facts),
                    sourceFactsOrigin = source.ProbeOccurred ? "ffprobe" : "supplied",
                    probeOccurred = source.ProbeOccurred,
                    probeMilliseconds = source.ProbeMilliseconds,
                    ffprobeInvocationCount = source.ProbeOccurred ? 1 : 0,
                    ffmpegProductionInvocationCount = 0,
                    ffmpegSampleInvocationCount = 0,
                    adaptiveCandidateGenerationCount = 0,
                    estimates = results,
                    timings = new
                    {
                        calculationMilliseconds = results.Sum(result => result.CalculationMilliseconds),
                        totalMilliseconds = Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds
                    }
                }).ConfigureAwait(false);
                return 0;
            }

            V2FastEstimateResolvedSource resolved = await V2FastEstimateService.ResolveSourceFactsAsync(
                request, metadata, cancellationToken).ConfigureAwait(false);
            if (operation == EncodingV2HeadlessOperation.Estimate)
            {
                V2FastEstimateResult result = V2FastEstimateService.Estimate(
                    V2FastEstimateService.CreateRequest(request, resolved.Facts));
                await WriteJsonAsync(standardOutput, new
                {
                    schemaVersion = 1,
                    operation = "estimate",
                    status = "completed",
                    source = PublicEstimateSource(resolved.Facts),
                    sourceFactsOrigin = resolved.ProbeOccurred ? "ffprobe" : "supplied",
                    probeOccurred = resolved.ProbeOccurred,
                    probeMilliseconds = resolved.ProbeMilliseconds,
                    ffprobeInvocationCount = resolved.ProbeOccurred ? 1 : 0,
                    ffmpegProductionInvocationCount = 0,
                    ffmpegSampleInvocationCount = 0,
                    adaptiveCandidateGenerationCount = 0,
                    estimate = result,
                    timings = new
                    {
                        calculationMilliseconds = result.CalculationMilliseconds,
                        totalMilliseconds = Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds
                    }
                }).ConfigureAwait(false);
                return 0;
            }

            double[] qualities = request.QualityValues is { Length: > 0 }
                ? request.QualityValues
                : [request.QualityValue];
            if (request.Repetitions > 10000)
                throw new V2PlanningException("InvalidRepetitions", "benchmark repetitions may not exceed 10000.");
            var estimates = new List<V2FastEstimateResult>(request.Repetitions);
            for (int index = 0; index < request.Repetitions; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double quality = qualities[index % qualities.Length];
                estimates.Add(V2FastEstimateService.Estimate(
                    V2FastEstimateService.CreateRequest(request, resolved.Facts, quality)));
            }
            double firstEstimateMs = estimates[0].CalculationMilliseconds;
            double[] warm = estimates.Skip(1).Select(result => result.CalculationMilliseconds).ToArray();
            var benchmark = new V2FastEstimateBenchmarkResult(
                resolved.ProbeMilliseconds,
                request.SourceFacts is not null,
                firstEstimateMs,
                Array.AsReadOnly(warm),
                V2FastEstimateService.Percentile(warm, 50),
                V2FastEstimateService.Percentile(warm, 95),
                warm.Length == 0 ? 0 : warm.Max(),
                estimates.Count,
                0,
                Array.AsReadOnly(qualities),
                estimates[0]);
            await WriteJsonAsync(standardOutput, new
            {
                schemaVersion = 1,
                operation = "benchmarkEstimates",
                status = "completed",
                source = PublicEstimateSource(resolved.Facts),
                sourceFactsOrigin = resolved.ProbeOccurred ? "ffprobe" : "supplied",
                probeOccurred = resolved.ProbeOccurred,
                ffprobeInvocationCount = resolved.ProbeOccurred ? 1 : 0,
                ffmpegProductionInvocationCount = 0,
                ffmpegSampleInvocationCount = 0,
                adaptiveCandidateGenerationCount = 0,
                benchmark,
                timings = new
                {
                    totalCalculationMilliseconds = estimates.Sum(result => result.CalculationMilliseconds),
                    totalMilliseconds = Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds
                }
            }).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteJsonAsync(standardOutput, new
            {
                schemaVersion = 1,
                operation = EstimateOperationName(operation),
                status = "canceled",
                ffmpegProductionInvocationCount = 0,
                failure = new V2Failure("Canceled", "The V2 fast-estimate operation was canceled.")
            }).ConfigureAwait(false);
            return 130;
        }
        catch (Exception ex)
        {
            string code = ex is V2PlanningException planning ? planning.Code :
                ex is JsonException ? "InvalidEstimateRequestJson" : "EstimateHeadlessOperationFailed";
            try { standardError.WriteLine("V2 fast estimate failed: " + ex.Message); } catch { }
            await WriteJsonAsync(standardOutput, new
            {
                schemaVersion = 1,
                operation = EstimateOperationName(operation),
                status = "failed",
                ffmpegProductionInvocationCount = 0,
                failure = new V2Failure(code, ex.Message),
                timings = new { totalMilliseconds = Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds }
            }).ConfigureAwait(false);
            return 2;
        }
    }

    private static string EstimateOperationName(EncodingV2HeadlessOperation operation) => operation switch
    {
        EncodingV2HeadlessOperation.Estimate => "estimate",
        EncodingV2HeadlessOperation.EstimateQualitySet => "estimateQualitySet",
        _ => "benchmarkEstimates"
    };

    private static object PublicEstimateSource(V2FastEstimateSourceFacts facts) => new
    {
        sourceIdentity = facts.SourcePath,
        sourceSizeBytes = facts.SourceSizeBytes,
        durationSeconds = facts.DurationSeconds,
        videoCodec = facts.VideoCodec,
        width = facts.Width,
        height = facts.Height,
        frameRate = facts.FrameRate,
        videoBitrateKbps = facts.VideoBitrateKbps,
        audioStreamCount = facts.AudioStreams.Count
    };

    private static async Task<int> RunQueueAsync(
        EncodingV2HeadlessOperation operation,
        string requestPath,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        V2EncodingQueueRequest? queueRequest = null;
        V2JsonLineQueueEventLog? eventLog = null;
        try
        {
            string jsonPath = Path.GetFullPath(requestPath);
            string json = await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false);
            queueRequest = JsonSerializer.Deserialize<V2EncodingQueueRequest>(json, JsonOptions)
                ?? throw new JsonException("The queue request document was empty.");
            if (queueRequest.SchemaVersion != 1)
                throw new V2PlanningException("UnsupportedQueueSchema", $"Unsupported V2 queue schema version {queueRequest.SchemaVersion}.");
            if (operation == EncodingV2HeadlessOperation.RunQueue && queueRequest.Certification is not null)
                throw new V2PlanningException("CertificationRequiresExplicitCommand",
                    "Failure injection data is accepted only by --v2-run-queue-certification.");
            if (operation == EncodingV2HeadlessOperation.CertifyQueue && queueRequest.Certification is null)
                throw new V2PlanningException("CertificationRequestRequired",
                    "The certification command requires an explicit certification object, even when it contains no injections.");

            ValidateQueueEventLogPath(queueRequest, jsonPath);
            if (!string.IsNullOrWhiteSpace(queueRequest.EventLogPath))
                eventLog = new V2JsonLineQueueEventLog(queueRequest.EventLogPath, JsonOptions);

            var itemExecutor = new V2CoreQueueItemExecutor((itemRequest, injection, injectionTriggered) =>
            {
                var processRunner = new MediaToolProcessRunner();
                FfmpegToolPaths tools = FfmpegToolResolver.Resolve(AppContext.BaseDirectory,
                    itemRequest.FfmpegPath, itemRequest.FfprobePath);
                if (!tools.AreAllAvailable)
                    throw new V2PlanningException("MediaToolsUnavailable",
                        "A matching FFmpeg and FFprobe executable pair could not be resolved for this item.");
                var metadata = new V2SourceMetadataProvider(new FfprobeService(tools.FfprobePath, processRunner));
                if (injection is null)
                    return CreateCore(tools, metadata, processRunner);
                return V2QueueCertificationCoreFactory.Create(metadata, new V2EncodePlanner(),
                    new V2ProductionExecutor(processRunner),
                    new V2OutputValidator(metadata,
                        new FfmpegDecodeIntegritySpotCheckService(tools.FfmpegPath, processRunner)),
                    new V2FileOutputPromoter(), tools.FfmpegPath, injection, injectionTriggered);
            });

            void Observe(V2EncodingQueueEvent item)
            {
                string line = JsonSerializer.Serialize(item, JsonOptions);
                try { standardError.WriteLine(line); standardError.Flush(); } catch { }
                try { eventLog?.Write(item); } catch (Exception ex)
                {
                    try { standardError.WriteLine("V2 queue event log write failed: " + ex.Message); } catch { }
                }
            }

            V2EncodingQueueResult result = await new V2EncodingQueueRunner(itemExecutor)
                .RunAsync(queueRequest, Observe, cancellationToken,
                    allowCertificationInjection: operation == EncodingV2HeadlessOperation.CertifyQueue).ConfigureAwait(false);
            await WriteJsonAsync(standardOutput, result).ConfigureAwait(false);
            return result.State switch
            {
                V2EncodingQueueState.Completed => 0,
                V2EncodingQueueState.CompletedWithFailures => 1,
                V2EncodingQueueState.Canceled => 130,
                _ => 2
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteJsonAsync(standardOutput, new
            {
                schemaVersion = 1,
                operation = "runQueue",
                queueId = queueRequest?.QueueId,
                status = "canceled",
                failure = new V2Failure("Canceled", "The V2 queue request was canceled before execution completed.")
            }).ConfigureAwait(false);
            return 130;
        }
        catch (Exception ex)
        {
            string code = ex is V2PlanningException planning ? planning.Code :
                ex is JsonException ? "InvalidQueueRequestJson" : "QueueHeadlessOperationFailed";
            await WriteJsonAsync(standardOutput, new
            {
                schemaVersion = 1,
                operation = "runQueue",
                queueId = queueRequest?.QueueId,
                status = "failed",
                failure = new V2Failure(code, ex.Message)
            }).ConfigureAwait(false);
            return 2;
        }
        finally { eventLog?.Dispose(); }
    }

    private static void ValidateQueueEventLogPath(V2EncodingQueueRequest request, string requestPath)
    {
        if (string.IsNullOrWhiteSpace(request.EventLogPath)) return;
        if (!Path.IsPathFullyQualified(request.EventLogPath))
            throw new V2PlanningException("InvalidEventLogPath", "eventLogPath must be a fully qualified path.");
        string eventLogPath;
        try { eventLogPath = Path.GetFullPath(request.EventLogPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new V2PlanningException("InvalidEventLogPath", ex.Message); }
        if (string.Equals(eventLogPath, Path.GetFullPath(requestPath), StringComparison.OrdinalIgnoreCase))
            throw new V2PlanningException("InvalidEventLogPath", "eventLogPath must differ from the queue request document.");
        foreach (V2EncodingQueueItemRequest item in request.Items ?? [])
        {
            string[] mediaPaths = [item.Request?.SourcePath ?? "", item.Request?.OutputPath ?? ""];
            foreach (string mediaPath in mediaPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                string fullMediaPath;
                try { fullMediaPath = Path.GetFullPath(mediaPath); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                { continue; } // The item itself will report its request error without queue-wide probing.
                if (string.Equals(eventLogPath, fullMediaPath, StringComparison.OrdinalIgnoreCase))
                    throw new V2PlanningException("InvalidEventLogPath", "eventLogPath must differ from every queue source and output path.");
            }
        }
    }

    private static V2EncodingCore CreateCore(FfmpegToolPaths tools, V2SourceMetadataProvider metadata,
        IMediaToolProcessRunner processRunner) => new(
        metadata,
        new V2EncodePlanner(),
        new V2ProductionExecutor(processRunner),
        new V2OutputValidator(metadata, new FfmpegDecodeIntegritySpotCheckService(tools.FfmpegPath, processRunner)),
        new V2FileOutputPromoter(),
        tools.FfmpegPath);

    private static object PublicMediaFacts(V2MediaFacts facts) => new
    {
        sourcePath = facts.SourcePath,
        sourceLength = facts.SourceLength,
        sourceLastWriteUtcTicks = facts.SourceLastWriteUtcTicks,
        format = facts.Probe.FormatName,
        durationSeconds = facts.Probe.DurationSeconds,
        primaryVideo = facts.Probe.Streams.First(stream => stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase)),
        audioStreams = facts.Probe.Streams.Where(stream => stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase)),
        subtitleStreams = facts.Probe.Streams.Where(stream => stream.CodecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase))
    };

    private static object PublicPlan(FrozenEncodePlan plan) => new
    {
        schemaVersion = 1,
        plan.JobId,
        plan.PlanFrozenUtc,
        plan.SourcePath,
        plan.OutputPath,
        plan.StagingPath,
        plan.Encoder,
        plan.Codec,
        plan.Container,
        plan.QualityMode,
        plan.QualityValue,
        plan.Preset,
        plan.PixelFormat,
        plan.AudioMode,
        plan.AudioBitrateKbps,
        plan.SubtitleMode,
        plan.Width,
        plan.Height,
        plan.DeleteSourceOnSuccess,
        ffmpegExecutable = Path.GetFileName(plan.FfmpegPath),
        arguments = plan.Arguments
    };

    private static void ValidateEventLogPath(string? eventLogPath, string sourcePath, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(eventLogPath)) return;
        if (!Path.IsPathFullyQualified(eventLogPath))
            throw new V2PlanningException("InvalidEventLogPath", "eventLogPath must be a fully qualified path.");
        string fullEvents = Path.GetFullPath(eventLogPath);
        if (string.Equals(fullEvents, Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullEvents, Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new V2PlanningException("InvalidEventLogPath", "eventLogPath must differ from the source and output paths.");
    }

    private static Task WriteJsonAsync(TextWriter writer, object value)
    {
        writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
        writer.Flush();
        return Task.CompletedTask;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed class V2JsonLineEventLog : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly JsonSerializerOptions _options;
        public V2JsonLineEventLog(string path, JsonSerializerOptions options)
        {
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (directory is null || !Directory.Exists(directory))
                throw new V2PlanningException("EventLogDirectoryMissing", "The event log directory must already exist.");
            _writer = new StreamWriter(new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _options = options;
        }
        public void Write(V2LifecycleEvent item)
        {
            _writer.WriteLine(JsonSerializer.Serialize(item, _options));
            _writer.Flush();
        }
        public void Dispose() => _writer.Dispose();
    }

    private sealed class V2JsonLineQueueEventLog : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly JsonSerializerOptions _options;
        public V2JsonLineQueueEventLog(string path, JsonSerializerOptions options)
        {
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (directory is null || !Directory.Exists(directory))
                throw new V2PlanningException("EventLogDirectoryMissing", "The queue event log directory must already exist.");
            _writer = new StreamWriter(new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _options = options;
        }
        public void Write(V2EncodingQueueEvent item)
        {
            _writer.WriteLine(JsonSerializer.Serialize(item, _options));
            _writer.Flush();
        }
        public void Dispose() => _writer.Dispose();
    }
}
