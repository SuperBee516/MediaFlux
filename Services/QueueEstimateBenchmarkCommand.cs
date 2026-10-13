using System.Diagnostics;
using System.Text.Json;
using MediaFlux.Models;

namespace MediaFlux.Services;

internal sealed record QueueEstimateBenchmarkRequest(
    string SourceDirectory,
    string UserDataRoot,
    string OutputDirectory,
    int FileCount,
    string QualityMode,
    bool MetadataReplay);

internal sealed record QueueEstimateBenchmarkItem(
    string SourcePath,
    string PlanFingerprint,
    string EncoderId,
    string Codec,
    double? QualityValue,
    string Preset,
    int BitDepth,
    long ProjectedBytes,
    long? LowerBytes,
    long? UpperBytes,
    string Confidence,
    int? SampleCount,
    string Method,
    string? RangeMethod);

internal sealed record QueueEstimateBenchmarkReport(
    int FileCount,
    int UniqueSourceCount,
    string QualityMode,
    int SelectedPriorityItems,
    int VisiblePriorityItems,
    int NearTermPriorityItems,
    int BackgroundPriorityItems,
    double QueueAdmissionMilliseconds,
    double EstimateWorkSchedulingMilliseconds,
    double? InitialEstimatePublicationMilliseconds,
    double? TimeToFirstPlanStateMilliseconds,
    double? TimeToFirstReadyPlanMilliseconds,
    double? TimeToFirstReviewPlanMilliseconds,
    double? TimeToFirstMeasuredEstimateMilliseconds,
    double? FiveProfileRecalculationMilliseconds,
    double? FiveProfileRecalculationSchedulingMilliseconds,
    int SelectorRecalculationPlanStatesPublished,
    int SelectorProfilesRecalculated,
    int InitialPlanStatesPublished,
    IReadOnlyDictionary<string, int> InitialEstimateStateCounts,
    double? ReadyPlanLatencyP50Milliseconds,
    double? ReadyPlanLatencyP95Milliseconds,
    double? ReviewPlanLatencyP50Milliseconds,
    double? ReviewPlanLatencyP95Milliseconds,
    int MeasuredEstimatesCompleted,
    IReadOnlyList<QueueEstimateBenchmarkItem> MeasuredEstimates,
    long MetadataProbesStarted,
    long MetadataCacheHits,
    long MetadataCoalescedWaiters,
    int MaximumConcurrentMetadataProbes,
    string MetadataProbeSource,
    long EmpiricalJobsLaunched,
    long EmpiricalSamplesLaunched,
    int MaximumConcurrentEmpiricalSamples,
    long CancelledOrSupersededWork,
    double TotalElapsedMilliseconds);

/// <summary>Runs the real queue estimate service without starting WinForms or production encoding.</summary>
internal static class QueueEstimateBenchmarkCommand
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".mts", ".m2ts", ".ts", ".webm", ".wmv"
    };

    internal static bool IsRequested(string[] args) => args.Length > 0 &&
        string.Equals(args[0], "--benchmark-queue-estimates", StringComparison.OrdinalIgnoreCase);

    internal static bool TryParse(string[] args, out QueueEstimateBenchmarkRequest? request, out string error)
    {
        request = null;
        error = "";
        string? source = null, userData = null, output = null, quality = "Automatic";
        bool metadataReplay = false;
        int count = 10;
        for (int i = 1; i < args.Length; i++)
        {
            string option = args[i];
            if (string.Equals(option, "--metadata-replay", StringComparison.OrdinalIgnoreCase))
            {
                metadataReplay = true;
                continue;
            }
            if (i + 1 >= args.Length)
            {
                error = $"Missing value for {option}.";
                return false;
            }
            string value = args[++i];
            switch (option.ToLowerInvariant())
            {
                case "--source-directory": source = value; break;
                case "--user-data": userData = value; break;
                case "--output-directory": output = value; break;
                case "--count":
                    if (!int.TryParse(value, out count) || count is < 1 or > 1000)
                    {
                        error = "Count must be between 1 and 1000.";
                        return false;
                    }
                    break;
                case "--quality-mode": quality = value; break;
                default:
                    error = $"Unknown option: {option}.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(userData) || string.IsNullOrWhiteSpace(output))
        {
            error = "Syntax: --benchmark-queue-estimates --source-directory <directory> --user-data <prepared-isolated-root> --output-directory <existing-directory> [--count <1..1000>] [--quality-mode Automatic|Manual] [--metadata-replay].";
            return false;
        }
        if (quality is not ("Automatic" or "Manual"))
        {
            error = "Quality mode must be Automatic or Manual.";
            return false;
        }
        try
        {
            request = new QueueEstimateBenchmarkRequest(
                Path.GetFullPath(source), Path.GetFullPath(userData), Path.GetFullPath(output), count, quality,
                metadataReplay);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "A supplied path is invalid: " + ex.Message;
            return false;
        }
        return true;
    }

    internal static int Run(string[] args)
    {
        DateTime startedUtc = DateTime.UtcNow;
        if (!TryParse(args, out QueueEstimateBenchmarkRequest? request, out string error))
        {
            Console.Error.WriteLine("Queue estimate benchmark rejected: " + error);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
            Console.Error.WriteLine("Cancellation requested; waiting for estimate workers to stop.");
        };
        Console.CancelKeyPress += cancel;
        try
        {
            QueueEstimateBenchmarkReport report = RunAsync(request!, cancellation.Token).GetAwaiter().GetResult();
            Console.Out.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Queue estimate benchmark cancelled.");
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Queue estimate benchmark failed: " + ex);
            return 8;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            _ = startedUtc;
        }
    }

    private static async Task<QueueEstimateBenchmarkReport> RunAsync(
        QueueEstimateBenchmarkRequest request,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(request.SourceDirectory))
            throw new DirectoryNotFoundException("Source directory does not exist: " + request.SourceDirectory);
        if (!Directory.Exists(request.OutputDirectory))
            throw new DirectoryNotFoundException("Output directory does not exist: " + request.OutputDirectory);
        MediaFluxStoragePathService paths = MediaFluxStoragePathService.ForIsolatedHeadlessRoot(request.UserDataRoot);
        if (!File.Exists(paths.Config))
            throw new FileNotFoundException("Prepared isolated UserData must contain config.json.", paths.Config);
        using IDisposable storage = AppPaths.UseIsolatedHeadlessStorage(paths);
        Config config = Config.LoadRequired(paths.Config);
        string applicationDirectory = HeadlessSavedJobEnvironment.ResolveApplicationDirectory(
            AppPaths.InstallDirectory,
            AppPaths.LauncherExecutablePath,
            config,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaFlux", "current"));

        Stopwatch total = Stopwatch.StartNew();
        Stopwatch admission = Stopwatch.StartNew();
        var admitted = new List<AdmittedQueueItem>(request.FileCount);
        string[] availablePaths = Directory.EnumerateFiles(request.SourceDirectory, "*", SearchOption.AllDirectories)
                     .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                     .ToArray();
        if (availablePaths.Length == 0)
            throw new InvalidOperationException("No supported video files were admitted from the source directory.");
        foreach (string path in availablePaths.Take(request.FileCount))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            file.Refresh();
            if (!file.Exists || file.Length <= 0)
                continue;
            admitted.Add(new AdmittedQueueItem(Path.GetFullPath(path), file.Length, DateTime.UtcNow, Guid.NewGuid()));
        }
        admission.Stop();
        if (admitted.Count == 0)
            throw new InvalidOperationException("No supported video files were admitted from the source directory.");

        string firstPath = admitted[0].Path;
        EncodeJobSettings jobSettings = HeadlessSavedJobItemPipeline.CreateTransientSettings(
            config, firstPath, request.OutputDirectory, request.QualityMode, deleteSourceAfterCompression: false);
        EncodeExecutionSnapshotSettings settings = EncodeExecutionSnapshotSettings.FromSavedJob(jobSettings, config);
        var mediaInfo = new MediaInfoService(applicationDirectory, config.FfprobePath, persistentCacheEnabled: false);
        var sizeEstimates = new SizeEstimateService(mediaInfo);
        var snapshots = new EncodeExecutionSnapshotBuilder(sizeEstimates, mediaInfo.GetBitrateKbps);
        IMediaProbeService probe = request.MetadataReplay
            ? new DeterministicReplayProbeService()
            : new FfprobeService(applicationDirectory, config.FfprobePath);
        var encoding = new EncodingService(applicationDirectory, _ => { }, message => Console.Error.WriteLine(message),
            config.FfmpegPath, config.FfprobePath);
        using var estimates = new EstimateBackgroundService(
            mediaInfo,
            previewEncodingService: encoding,
            previewProbeService: probe,
            snapshots: snapshots,
            previewToolSignature: $"{config.FfmpegPath}|{config.FfprobePath}");

        Stopwatch estimateScheduling = Stopwatch.StartNew();
        for (int itemIndex = 0; itemIndex < admitted.Count; itemIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdmittedQueueItem item = admitted[itemIndex];
            var previewItem = new EncodeExecutionSnapshotItem
            {
                SourceFilePath = item.Path,
                LogicalSourcePath = item.Path,
                SourceSizeBytes = item.SourceBytes
            };
            EncodeExecutionSnapshot plan = snapshots.Build(
                new EncodeExecutionSnapshotIdentity
                {
                    OperationId = item.QueueItemId.ToString("N"),
                    StatisticsStartUtc = item.AcceptedUtc,
                    MediaFluxVersion = Application.ProductVersion,
                    StatisticsPath = AppPaths.EncodingStatisticsFile,
                    ContainerCompatibilityConfirmed = false
                },
                settings,
                previewItem);
            double manualTargetMb = 0;
            bool rowAuto = SizeEstimateService.ShouldUseProfileEstimate(settings.AutoTargetSize, manualTargetMb);
            QueueEstimateRefinementPriority refinementPriority = itemIndex == 0
                ? QueueEstimateRefinementPriority.Selected
                : itemIndex < 3
                    ? QueueEstimateRefinementPriority.Visible
                    : itemIndex < 5
                        ? QueueEstimateRefinementPriority.NearTerm
                        : QueueEstimateRefinementPriority.Background;
            estimates.QueueSmartEstimate(
                item.Path,
                rowAuto,
                settings.CompressionProfile,
                manualTargetMb,
                plan.Encoder,
                plan.QualityValue ?? 0,
                RequestedTargetHeight(plan.ScaleMode),
                plan.AudioChannels,
                isCustom: false,
                config.SmartRecommendationsEnabled,
                config.MinimumExpectedSavingsPercent,
                settings.StorageSavings,
                plan.QualityIntent,
                plan.AdaptiveStorageSavingsEnabled,
                config.UseHistoricalSizePredictionCalibration,
                item.QueueItemId,
                plan.EncoderPreset ?? "",
                plan.TenBit ? 10 : 8,
                plan.ConcurrentEncoderSessions,
                previewSettings: settings,
                previewItem: previewItem,
                queueAcceptedUtc: item.AcceptedUtc,
                planIdentity: plan.ProductionPlan,
                empiricalPreviewEnabled: !settings.CompressionProfile.Equals("No Compression", StringComparison.OrdinalIgnoreCase) &&
                    (plan.QualityIntent is not null || settings.AutomaticQuality || settings.NumericQualityValue.HasValue),
                refinementPriority: refinementPriority);
        }
        estimateScheduling.Stop();

        var terminalItems = new HashSet<Guid>();
        var initialEstimateStateCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var readyPlanLatencies = new List<double>();
        var reviewPlanLatencies = new List<double>();
        int initialPlanStatesPublished = 0, measuredCount = 0;
        var measuredItems = new HashSet<Guid>();
        var measuredEstimates = new List<QueueEstimateBenchmarkItem>();
        double? firstPlanStateMs = null, firstReadyPlanMs = null, firstReviewPlanMs = null;
        double? firstMeasuredMs = null, initialEstimatePublicationMs = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (estimates.TryDequeueSmart(out EstimateBackgroundService.SmartEstimateResult result))
            {
                if (result.RecommendationOnly)
                    continue;
                if (!IsTerminalEstimateState(result.EstimateState))
                    continue;
                if (terminalItems.Add(result.QueueItemId))
                {
                    firstPlanStateMs ??= total.Elapsed.TotalMilliseconds;
                    initialPlanStatesPublished++;
                    string stateName = result.EstimateState.ToString();
                    initialEstimateStateCounts[stateName] = initialEstimateStateCounts.GetValueOrDefault(stateName) + 1;
                    double planLatencyMs = Math.Max(0, (DateTime.UtcNow - result.QueueAcceptedUtc).TotalMilliseconds);
                    if (result.EstimateState == QueueEstimateState.Ready)
                    {
                        firstReadyPlanMs ??= total.Elapsed.TotalMilliseconds;
                        readyPlanLatencies.Add(planLatencyMs);
                    }
                    else if (result.EstimateState == QueueEstimateState.Review)
                    {
                        firstReviewPlanMs ??= total.Elapsed.TotalMilliseconds;
                        reviewPlanLatencies.Add(planLatencyMs);
                    }
                }
                if (result.BoundEstimate is { Authority: EstimateAuthorityLevel.Measured } measured &&
                    measuredItems.Add(result.QueueItemId))
                {
                    measuredCount++;
                    firstMeasuredMs ??= total.Elapsed.TotalMilliseconds;
                    measuredEstimates.Add(new QueueEstimateBenchmarkItem(
                        result.Path,
                        measured.Plan.PlanFingerprint,
                        measured.Plan.Inputs.EncoderId,
                        measured.Plan.Inputs.Codec,
                        measured.Plan.Inputs.QualityValue,
                        measured.Plan.Inputs.Preset,
                        measured.Plan.Inputs.BitDepth,
                        measured.ProjectedBytes,
                        measured.LowerBytes,
                        measured.UpperBytes,
                        measured.Confidence,
                        measured.SampleCount,
                        measured.Method,
                        measured.RangeMethod));
                }
            }

            if (terminalItems.Count >= admitted.Count && estimates.PendingEstimates == 0 && estimates.PendingRefinements == 0)
            {
                initialEstimatePublicationMs ??= total.Elapsed.TotalMilliseconds;
                break;
            }
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        double? fiveProfileRecalculationMs = null;
        double? fiveProfileSchedulingMs = null;
        int selectorPlanStatesPublished = 0;
        int selectorProfilesRecalculated = 0;
        if (request.QualityMode == "Automatic")
        {
            QualityTarget[] targets =
            [
                QualityTarget.SmallerFile, QualityTarget.Efficient, QualityTarget.Balanced,
                QualityTarget.HighQuality, QualityTarget.MaximumQuality
            ];
            var selectorTotal = Stopwatch.StartNew();
            Stopwatch selectorScheduling = new();
            for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                QualityTarget target = targets[targetIndex];
                EncodeExecutionSnapshotSettings selectorSettings = settings with { QualityTarget = target };
                var pendingProfile = new HashSet<Guid>();
                selectorScheduling.Start();
                for (int itemIndex = 0; itemIndex < admitted.Count; itemIndex++)
                {
                    AdmittedQueueItem item = admitted[itemIndex];
                    var previewItem = new EncodeExecutionSnapshotItem
                    {
                        SourceFilePath = item.Path,
                        LogicalSourcePath = item.Path,
                        SourceSizeBytes = item.SourceBytes
                    };
                    EncodeExecutionSnapshot selectorPlan = snapshots.Build(
                        new EncodeExecutionSnapshotIdentity
                        {
                            OperationId = $"queue-profile-{targetIndex}-{item.QueueItemId:N}",
                            StatisticsStartUtc = item.AcceptedUtc,
                            MediaFluxVersion = Application.ProductVersion,
                            StatisticsPath = AppPaths.EncodingStatisticsFile,
                            ContainerCompatibilityConfirmed = false
                        },
                        selectorSettings,
                        previewItem);
                    QueueEstimateRefinementPriority priority = itemIndex == 0
                        ? QueueEstimateRefinementPriority.Selected
                        : itemIndex < 3
                            ? QueueEstimateRefinementPriority.Visible
                            : itemIndex < 5
                                ? QueueEstimateRefinementPriority.NearTerm
                                : QueueEstimateRefinementPriority.Background;
                    estimates.QueueSmartEstimate(
                        item.Path,
                        SizeEstimateService.ShouldUseProfileEstimate(selectorSettings.AutoTargetSize, 0),
                        selectorSettings.CompressionProfile,
                        0,
                        selectorPlan.Encoder,
                        selectorPlan.QualityValue ?? 0,
                        RequestedTargetHeight(selectorPlan.ScaleMode),
                        selectorPlan.AudioChannels,
                        isCustom: false,
                        config.SmartRecommendationsEnabled,
                        config.MinimumExpectedSavingsPercent,
                        selectorSettings.StorageSavings,
                        qualityIntent: selectorPlan.QualityIntent,
                        historicalCalibrationEnabled: config.UseHistoricalSizePredictionCalibration,
                        queueItemId: item.QueueItemId,
                        encoderPreset: selectorPlan.EncoderPreset ?? "",
                        outputBitDepth: selectorPlan.TenBit ? 10 : 8,
                        concurrentEncoderSessions: selectorPlan.ConcurrentEncoderSessions,
                        previewSettings: selectorSettings,
                        previewItem: previewItem,
                        estimateAuthorityRevision: targetIndex + 1,
                        empiricalPreviewEnabled: !selectorSettings.CompressionProfile.Equals("No Compression", StringComparison.OrdinalIgnoreCase) &&
                            (selectorPlan.QualityIntent is not null || selectorSettings.AutomaticQuality || selectorSettings.NumericQualityValue.HasValue),
                        queueAcceptedUtc: item.AcceptedUtc,
                        qualityChangeStartedUtc: DateTime.UtcNow,
                        planIdentity: selectorPlan.ProductionPlan,
                        refinementPriority: priority);
                }
                selectorScheduling.Stop();
                selectorProfilesRecalculated++;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    while (estimates.TryDequeueSmart(out EstimateBackgroundService.SmartEstimateResult result))
                    {
                        if (result.RecommendationOnly || !IsTerminalEstimateState(result.EstimateState))
                            continue;
                        if (result.EstimateAuthorityRevision != targetIndex + 1)
                            continue;
                        if (pendingProfile.Add(result.QueueItemId))
                            selectorPlanStatesPublished++;
                    }

                    if (pendingProfile.Count >= admitted.Count && estimates.PendingEstimates == 0 &&
                        estimates.PendingRefinements == 0)
                        break;
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
            }
            selectorTotal.Stop();
            fiveProfileRecalculationMs = selectorTotal.Elapsed.TotalMilliseconds;
            fiveProfileSchedulingMs = selectorScheduling.Elapsed.TotalMilliseconds;
        }

        total.Stop();
        QueueMetadataProbeDiagnostics metadata = estimates.MetadataProbeDiagnostics ?? default;
        int uniqueSourceCount = admitted.Select(item => item.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new QueueEstimateBenchmarkReport(
            admitted.Count,
            uniqueSourceCount,
            request.QualityMode,
            SelectedPriorityItems: admitted.Count > 0 ? 1 : 0,
            VisiblePriorityItems: Math.Max(0, Math.Min(admitted.Count, 3) - 1),
            NearTermPriorityItems: Math.Max(0, Math.Min(admitted.Count, 5) - 3),
            BackgroundPriorityItems: Math.Max(0, admitted.Count - 5),
            admission.Elapsed.TotalMilliseconds,
            estimateScheduling.Elapsed.TotalMilliseconds,
            initialEstimatePublicationMs,
            firstPlanStateMs,
            firstReadyPlanMs,
            firstReviewPlanMs,
            firstMeasuredMs,
            fiveProfileRecalculationMs,
            fiveProfileSchedulingMs,
            selectorPlanStatesPublished,
            selectorProfilesRecalculated,
            initialPlanStatesPublished,
            initialEstimateStateCounts,
            Percentile(readyPlanLatencies, 0.50),
            Percentile(readyPlanLatencies, 0.95),
            Percentile(reviewPlanLatencies, 0.50),
            Percentile(reviewPlanLatencies, 0.95),
            measuredCount,
            measuredEstimates,
            metadata.ProbesStarted,
            metadata.CacheHits,
            metadata.CoalescedWaiters,
            metadata.MaximumConcurrentProbes,
            request.MetadataReplay ? "deterministic-replay" : "ffprobe",
            estimates.EmpiricalJobsLaunched,
            estimates.EmpiricalSamplesLaunched,
            estimates.MaximumConcurrentEmpiricalSamples,
            estimates.CancelledOrSupersededWork,
            total.Elapsed.TotalMilliseconds);
    }

    private static double? Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0)
            return null;
        double[] sorted = values.OrderBy(value => value).ToArray();
        int index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static bool IsTerminalEstimateState(QueueEstimateState state) => state is
        QueueEstimateState.Preliminary or QueueEstimateState.Ready or QueueEstimateState.Review or
        QueueEstimateState.Measured or QueueEstimateState.Skip or QueueEstimateState.Unavailable or
        QueueEstimateState.Failed;

    private static int? RequestedTargetHeight(EncodingService.ScaleMode mode) => mode switch
    {
        EncodingService.ScaleMode.To720p => 720,
        EncodingService.ScaleMode.To1080p => 1080,
        EncodingService.ScaleMode.To1440p => 1440,
        EncodingService.ScaleMode.To4K => 2160,
        _ => null
    };

    private sealed class DeterministicReplayProbeService : IMediaProbeService
    {
        public async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            file.Refresh();
            if (!file.Exists || file.Length <= 0)
                return MediaProbeResult.Failed("Metadata replay fixture is missing or empty.");

            double durationSeconds = file.Length * 8d / 1000d / 4000d;
            double sourceTotalKbps = file.Length * 8d / 1000d / durationSeconds;
            long sourceVideoBitrate = checked((long)Math.Round(Math.Max(100d, sourceTotalKbps - 200d) * 1000d));
            return new MediaProbeResult
            {
                Success = true,
                DurationSeconds = durationSeconds,
                SizeBytes = file.Length,
                FormatName = "metadata-replay-fixture",
                Streams =
                [
                    new MediaProbeStreamInfo
                    {
                        Index = 0,
                        CodecType = "video",
                        CodecName = "h264",
                        Width = 1920,
                        Height = 1080,
                        FrameRate = 30,
                        DurationSeconds = durationSeconds,
                        BitRate = sourceVideoBitrate,
                        PixelFormat = "yuv420p"
                    }
                ]
            };
        }
    }

    private sealed record AdmittedQueueItem(string Path, long SourceBytes, DateTime AcceptedUtc, Guid QueueItemId);
}
