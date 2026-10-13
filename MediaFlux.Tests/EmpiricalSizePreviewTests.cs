using MediaFlux.Models;
using MediaFlux.Services;
using System.Reflection;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EmpiricalSizePreviewTests : IDisposable
{
    private readonly string _source = Path.Combine(Path.GetTempPath(), "mf-preview-" + Guid.NewGuid() + ".mp4");
    public EmpiricalSizePreviewTests() => File.WriteAllBytes(_source, new byte[1000]);
    public void Dispose() => File.Delete(_source);
    private AdaptiveQualitySelectionRequest Request()
    {
        var request = AdaptiveStorageSavingsTests.Request();
        return request with { Input = EncodingInputSource.FromFile(_source),
            Envelope = new(request.Video.Encoder, QualityTarget.Balanced, EncoderQualityMechanism.Cq, 24, 26) };
    }

    private sealed class Samples(Func<int, int, double> rates) : IAdaptiveVideoSampleRunner
    {
        public List<int> Qualities { get; } = new();
        public Task<RepresentativeSampleEvidence> MeasureAsync(AdaptiveQualitySelectionRequest request, int quality,
            RepresentativeSample sample, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int index = Qualities.Count(q => q == quality);
            Qualities.Add(quality);
            return Task.FromResult(new RepresentativeSampleEvidence(sample, (long)(rates(quality, index) * 5), 5)
                { Purpose = VideoSamplePurpose.PreviewEstimation });
        }
    }

    private sealed class CallbackSamples(Func<AdaptiveQualitySelectionRequest, int, RepresentativeSample, CancellationToken,
        Task<RepresentativeSampleEvidence>> callback) : IAdaptiveVideoSampleRunner
    {
        public Task<RepresentativeSampleEvidence> MeasureAsync(AdaptiveQualitySelectionRequest request, int quality,
            RepresentativeSample sample, CancellationToken token) => callback(request, quality, sample, token);
    }

    private sealed class SourceProbe(MediaProbeResult source) : IMediaProbeService
    {
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(source);
    }

    private sealed class CountingSourceProbe(MediaProbeResult source) : IMediaProbeService
    {
        public int Calls;
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(source);
        }
    }

    private sealed class BlockingSourceProbe(MediaProbeResult source) : IMediaProbeService
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return source;
        }
    }

    private QueueEmpiricalPreviewProvider Provider(IAdaptiveVideoSampleRunner runner, MediaProbeResult? source = null) =>
        new(new EncodingService(Path.GetTempPath(), _ => { }), new SourceProbe(source ?? AdaptiveStorageSavingsTests.Source()),
            new EncodeExecutionSnapshotBuilder(), "test-tools", (_, _, _) => runner, q => $"video:{q}", new PreviewSamplingCoordinator());

    [Fact]
    public async Task AutomaticQueueAndFiveProfileChangesUseOneProbeAndLaunchNoSamples()
    {
        using (var sourceFile = new FileStream(_source, FileMode.Open, FileAccess.Write, FileShare.Read))
            sourceFile.SetLength(50_000_000);
        var source = new MediaProbeResult
        {
            Success = true,
            DurationSeconds = 100,
            Streams =
            [
                new MediaProbeStreamInfo
                {
                    Index = 0, CodecType = "video", CodecName = "h264", Width = 1920,
                    Height = 1080, FrameRate = 30, BitRate = 4_000_000, PixelFormat = "yuv420p"
                }
            ]
        };
        var probe = new CountingSourceProbe(source);
        var samples = new Samples((_, _) => throw new InvalidOperationException("Automatic target estimates must not encode samples."));
        var provider = new QueueEmpiricalPreviewProvider(
            new EncodingService(Path.GetTempPath(), _ => { }), probe,
            new EncodeExecutionSnapshotBuilder(), "target-plan-test",
            (_, _, _) => samples, q => $"video:{q}", new PreviewSamplingCoordinator());
        QualityTarget[] profiles =
        [
            QualityTarget.SmallerFile, QualityTarget.Efficient, QualityTarget.Balanced,
            QualityTarget.HighQuality, QualityTarget.MaximumQuality
        ];
        var actualTargets = new List<double>();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);

        foreach (QualityTarget profile in profiles)
        {
            using var media = new DisposableMediaInfo();
            using var service = new EstimateBackgroundService(media.Value, provider, Metadata);
            EncodeExecutionSnapshotSettings settings = EncodeExecutionSnapshotBuilderTests.Settings() with
            {
                AutomaticQuality = true,
                UseAutomaticTargetBitrate = true,
                QualityTarget = profile
            };
            Guid queueItemId = Guid.NewGuid();
            service.QueueSmartEstimate(
                _source, auto: true, profile.ToString(), manualTargetMb: 0,
                new(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc"), quality: 24,
                targetHeight: null, targetAudioChannels: null, isCustom: false,
                recommendationsEnabled: false, minimumSavingsPercent: 10, new StorageSavingsOptions(),
                qualityIntent: EncodingQualityIntent.AutomaticTarget(profile),
                queueItemId: queueItemId,
                previewSettings: settings,
                previewItem: new EncodeExecutionSnapshotItem
                {
                    SourceFilePath = _source,
                    LogicalSourcePath = _source,
                    SourceSizeBytes = 50_000_000,
                    MediaDurationSeconds = 100
                },
                empiricalPreviewEnabled: true,
                refinementPriority: QueueEstimateRefinementPriority.Selected);

            await FinishWorkers(service);
            Assert.True(service.TryDequeueSmart(out EstimateBackgroundService.SmartEstimateResult result));
            Assert.Equal(QueueEstimateState.Ready, result.EstimateState);
            Assert.Null(result.EmpiricalPreview);
            Assert.Equal(EstimateAuthorityLevel.Preliminary, result.BoundEstimate?.Authority);
            PreparedProductionPlan prepared = Assert.IsType<PreparedProductionPlan>(result.PreparedProductionPlan);
            Assert.Equal(profile, prepared.Plan.AutomaticTarget?.QualityTarget);
            Assert.True(prepared.Estimate!.Matches(prepared.Plan.ProductionPlan));
            Assert.Equal(prepared.Identity.PlanFingerprint, result.PlanIdentity?.PlanFingerprint);
            actualTargets.Add(prepared.Plan.AutomaticTarget!.TargetVideoBitrateKbps!.Value);
            fingerprints.Add(prepared.Identity.PlanFingerprint);
            Assert.Equal(0, service.EmpiricalJobsLaunched);
            Assert.Equal(0, service.EmpiricalSamplesLaunched);
        }

        Assert.Equal(profiles.Length, fingerprints.Count);
        Assert.True(actualTargets.Zip(actualTargets.Skip(1), (left, right) => left < right).All(ordered => ordered));
        Assert.Equal(1, probe.Calls);
        Assert.Empty(samples.Qualities);
    }

    [Fact]
    public async Task SourceMetadataProbeCoalescesConcurrentRequestsAndReusesTheCompletedResult()
    {
        using (var sourceFile = new FileStream(_source, FileMode.Open, FileAccess.Write, FileShare.Read))
            sourceFile.SetLength(50_000_000);
        var source = new MediaProbeResult
        {
            Success = true,
            DurationSeconds = 100,
            Streams =
            [
                new MediaProbeStreamInfo
                {
                    Index = 0, CodecType = "video", CodecName = "h264", Width = 1920,
                    Height = 1080, FrameRate = 30, BitRate = 4_000_000, PixelFormat = "yuv420p"
                }
            ]
        };
        var probe = new BlockingSourceProbe(source);
        var samples = new Samples((_, _) => throw new InvalidOperationException(
            "Automatic target plans must not launch empirical sample encodes."));
        var provider = new QueueEmpiricalPreviewProvider(
            new EncodingService(Path.GetTempPath(), _ => { }), probe,
            new EncodeExecutionSnapshotBuilder(), "probe-coalescing-test",
            (_, _, _) => samples, q => $"video:{q}", new PreviewSamplingCoordinator());
        EncodeExecutionSnapshotSettings settings = EncodeExecutionSnapshotBuilderTests.Settings() with
        {
            AutomaticQuality = true,
            UseAutomaticTargetBitrate = true,
            QualityTarget = QualityTarget.Balanced
        };
        var item = new EncodeExecutionSnapshotItem
        {
            SourceFilePath = _source,
            LogicalSourcePath = _source,
            SourceSizeBytes = 50_000_000,
            MediaDurationSeconds = 100
        };

        Task<QueueProductionPlanEstimate> first = provider.PrepareAndEstimateAsync(settings, item, 1, default);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<QueueProductionPlanEstimate> second = provider.PrepareAndEstimateAsync(settings, item, 1, default);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(1, provider.MetadataProbeDiagnostics.CoalescedWaiters);

        probe.Release.TrySetResult();
        QueueProductionPlanEstimate[] prepared = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        QueueProductionPlanEstimate cached = await provider.PrepareAndEstimateAsync(settings, item, 2, default);

        Assert.All(prepared.Append(cached), result =>
        {
            Assert.Equal(EstimateAuthorityLevel.Preliminary, result.PreparedPlan.Estimate?.Authority);
            Assert.Null(result.Preview);
        });
        Assert.Equal(1, probe.Calls);
        Assert.Empty(samples.Qualities);
        Assert.Equal(new QueueMetadataProbeDiagnostics(1, 1, 1, 1), provider.MetadataProbeDiagnostics);
    }

    [Fact]
    public async Task AutomaticQueueRowsSharingASourceCoalesceMetadataAndPublishBothPlans()
    {
        using (var sourceFile = new FileStream(_source, FileMode.Open, FileAccess.Write, FileShare.Read))
            sourceFile.SetLength(50_000_000);
        var source = new MediaProbeResult
        {
            Success = true,
            DurationSeconds = 100,
            Streams =
            [
                new MediaProbeStreamInfo
                {
                    Index = 0, CodecType = "video", CodecName = "h264", Width = 1920,
                    Height = 1080, FrameRate = 30, BitRate = 4_000_000, PixelFormat = "yuv420p"
                }
            ]
        };
        var probe = new BlockingSourceProbe(source);
        var samples = new Samples((_, _) => throw new InvalidOperationException(
            "Automatic target plans must not launch empirical sample encodes."));
        var provider = new QueueEmpiricalPreviewProvider(
            new EncodingService(Path.GetTempPath(), _ => { }), probe,
            new EncodeExecutionSnapshotBuilder(), "duplicate-source-queue-test",
            (_, _, _) => samples, q => $"video:{q}", new PreviewSamplingCoordinator());
        using var media = new DisposableMediaInfo();
        using var service = new EstimateBackgroundService(media.Value, provider, Metadata);
        EncodeExecutionSnapshotSettings settings = EncodeExecutionSnapshotBuilderTests.Settings() with
        {
            AutomaticQuality = true,
            UseAutomaticTargetBitrate = true,
            QualityTarget = QualityTarget.Balanced
        };

        foreach (QueueEstimateRefinementPriority priority in new[]
                 { QueueEstimateRefinementPriority.Selected, QueueEstimateRefinementPriority.Visible })
        {
            Guid queueItemId = Guid.NewGuid();
            service.QueueSmartEstimate(
                _source, auto: true, settings.CompressionProfile, manualTargetMb: 0,
                new(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc"), quality: 24,
                targetHeight: null, targetAudioChannels: null, isCustom: false,
                recommendationsEnabled: false, minimumSavingsPercent: 10, new StorageSavingsOptions(),
                qualityIntent: EncodingQualityIntent.AutomaticTarget(QualityTarget.Balanced),
                queueItemId: queueItemId,
                previewSettings: settings,
                previewItem: new EncodeExecutionSnapshotItem
                {
                    SourceFilePath = _source,
                    LogicalSourcePath = _source,
                    SourceSizeBytes = 50_000_000,
                    MediaDurationSeconds = 100
                },
                empiricalPreviewEnabled: true,
                refinementPriority: priority);
        }

        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(
            () => provider.MetadataProbeDiagnostics.CoalescedWaiters > 0,
            TimeSpan.FromSeconds(5)), "The second queue row did not join the in-flight metadata probe.");
        probe.Release.TrySetResult();
        await FinishWorkers(service);

        var results = new List<EstimateBackgroundService.SmartEstimateResult>();
        while (service.TryDequeueSmart(out EstimateBackgroundService.SmartEstimateResult result))
            if (!result.RecommendationOnly)
                results.Add(result);
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal(QueueEstimateState.Ready, result.EstimateState));
        Assert.Equal(1, probe.Calls);
        Assert.Equal(0, service.EmpiricalJobsLaunched);
        Assert.Equal(0, service.EmpiricalSamplesLaunched);
        Assert.Equal(1, provider.MetadataProbeDiagnostics.CoalescedWaiters);
    }

    [Fact]
    public async Task ProviderUsesProductionProgramDurationAndExactManualQuality()
    {
        var source = new MediaProbeResult
        {
            Success = true, DurationSeconds = 100.05,
            Streams = [new MediaProbeStreamInfo { Index = 0, CodecType = "video", CodecName = "h264",
                Width = 1920, Height = 1080, FrameRate = 30, BitRate = 4_000_000, PixelFormat = "yuv420p", DurationSeconds = 100 }]
        };
        // Source stream timing, rather than a trailing audio/container timestamp, owns sampling positions.
        var runner = new CallbackSamples((request, quality, sample, _) =>
        {
            Assert.Equal(100, request.SourceDuration.TotalSeconds);
            Assert.Equal(19, quality);
            Assert.Equal("p5", request.Video.Preset);
            Assert.True(request.Video.TenBit);
            Assert.Equal(1920, request.Video.Geometry.Width);
            return Task.FromResult(new RepresentativeSampleEvidence(sample, 25000, 5) { Purpose = VideoSamplePurpose.PreviewEstimation });
        });
        QueueProductionPlanEstimate result = await Provider(runner, source).PrepareAndEstimateAsync(EncodeExecutionSnapshotBuilderTests.Settings() with
            { NumericQualityValue = 19, TenBit = true }, new() { SourceFilePath = _source, LogicalSourcePath = _source }, 0, default);
        Assert.Equal(19, result.PreparedPlan.Plan.Quality?.EffectiveQuality);
        Assert.Equal(result.PreparedPlan.Identity.PlanFingerprint, result.PreparedPlan.Estimate?.Plan.PlanFingerprint);
        Assert.Equal(19, Assert.Single(result.Preview!.Candidates).Quality);
    }

    [Fact]
    public async Task SourceMutationDuringSamplingCannotPopulateCache()
    {
        var cache = new PreviewVideoEvidenceCache();
        var preview = new EmpiricalSizePreviewService(cache);
        bool mutate = true;
        var runner = new CallbackSamples((_, _, sample, _) =>
        {
            if (mutate) { mutate = false; File.WriteAllBytes(_source, new byte[1001]); }
            return Task.FromResult(new RepresentativeSampleEvidence(sample, 25000, 5) { Purpose = VideoSamplePurpose.PreviewEstimation });
        });
        await Assert.ThrowsAsync<PreviewUnavailableException>(() => preview.EstimateAsync(Request(), false, runner, _ => "same", default));
        var result = await preview.EstimateAsync(Request(), false, runner, _ => "same", default);
        Assert.Equal(0, result.CacheHits);
        Assert.Equal(1, result.CacheMisses);
    }

    [Fact]
    public async Task PreviewCacheRequiresTheSameProductionPlanFingerprint()
    {
        var cache = new PreviewVideoEvidenceCache();
        var preview = new EmpiricalSizePreviewService(cache);
        var runner = new Samples((_, _) => 5000);
        ProductionPlanFingerprintInput inputs = new(
            "hevc_nvenc", "hevc", "cq", "manual-numeric", 24, "p5", 8, "none", null,
            "preserve-source-timing", "{}", "copy-compatible", null, null, "copy-compatible",
            false, false, "mp4", "intelligent", "hardware", false, "keep-all", null);
        ProductionPlanIdentity firstPlan = ProductionPlanFingerprintService.Create(inputs, [_source]);
        ProductionPlanIdentity changedPlan = ProductionPlanFingerprintService.Create(inputs with { Preset = "p6" }, [_source]);

        EmpiricalPreviewResult first = await preview.EstimateAsync(Request(), false, runner, _ => "same-video-signature",
            default, firstPlan);
        EmpiricalPreviewResult changed = await preview.EstimateAsync(Request(), false, runner, _ => "same-video-signature",
            default, changedPlan);
        EmpiricalPreviewResult repeated = await preview.EstimateAsync(Request(), false, runner, _ => "same-video-signature",
            default, firstPlan);

        Assert.Equal(0, first.CacheHits);
        Assert.Equal(1, changed.CacheMisses);
        Assert.Equal(1, repeated.CacheHits);
        Assert.Equal(6, runner.Qualities.Count);
    }

    private async Task FinishWorkers(EstimateBackgroundService service)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object queue = typeof(EstimateBackgroundService).GetField("_workItems", flags)!.GetValue(service)!;
        queue.GetType().GetMethod("CompleteAdding")!.Invoke(queue, null);
        var tasks = (Task[])typeof(EstimateBackgroundService).GetField("_workers", flags)!.GetValue(service)!;
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        await service.WaitForRefinementsAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Tests that need the immediate preliminary result read it before calling
        // this helper. Once all work is idle, retain the final state per queue item.
        var finalEstimates = new Dictionary<Guid, EstimateBackgroundService.SmartEstimateResult>();
        var recommendations = new Dictionary<Guid, EstimateBackgroundService.SmartEstimateResult>();
        while (service.TryDequeueSmart(out EstimateBackgroundService.SmartEstimateResult result))
        {
            if (result.RecommendationOnly)
                recommendations[result.QueueItemId] = result;
            else
                finalEstimates[result.QueueItemId] = result;
        }
        var resultQueue = (System.Collections.Concurrent.ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult>)
            typeof(EstimateBackgroundService).GetField("_smartResults", flags)!.GetValue(service)!;
        foreach (EstimateBackgroundService.SmartEstimateResult result in finalEstimates.Values)
            resultQueue.Enqueue(result);
        foreach (EstimateBackgroundService.SmartEstimateResult result in recommendations.Values)
            resultQueue.Enqueue(result);
    }

    private void Queue(EstimateBackgroundService service, int quality = 24, double target = 0, long authorityRevision = 0,
        bool empiricalPreviewEnabled = false, DateTime? qualityChanged = null,
        EncodingQualityIntent? qualityIntent = null, bool historicalCalibration = true,
        Guid queueItemId = default, StorageSavingsOptions? storageSavings = null, bool? auto = null,
        QueueEstimateRefinementPriority refinementPriority = QueueEstimateRefinementPriority.Selected,
        bool recommendationsEnabled = true)
    {
        storageSavings ??= new();
        if (queueItemId == Guid.Empty)
            queueItemId = Guid.NewGuid();
        ProductionPlanFingerprintInput inputs = new(
            "hevc_nvenc", "hevc", "cq", "manual-numeric", quality, "p5", 8, "none", null,
            "preserve-source-timing", "{}", "copy-compatible", null, null, "copy-compatible",
            false, false, "mp4", "intelligent", "hardware", false, "keep-all", null);
        ProductionPlanIdentity planIdentity = ProductionPlanFingerprintService.Create(inputs, [_source]);
        service.QueueSmartEstimate(_source, auto ?? target <= 0, "Medium", target,
            new(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc"), quality, null, null, false, recommendationsEnabled, 10, storageSavings,
            qualityIntent: qualityIntent, historicalCalibrationEnabled: historicalCalibration,
            previewSettings: EncodeExecutionSnapshotBuilderTests.Settings() with { NumericQualityValue = quality,
                TargetSizeText = target > 0 ? target.ToString(System.Globalization.CultureInfo.CurrentCulture) : "",
                StorageSavings = storageSavings },
            previewItem: new() { SourceFilePath = _source, LogicalSourcePath = _source },
            estimateAuthorityRevision: authorityRevision, queueItemId: queueItemId,
            empiricalPreviewEnabled: empiricalPreviewEnabled, qualityChangeStartedUtc: qualityChanged,
            planIdentity: planIdentity, refinementPriority: refinementPriority);
    }

    [Fact]
    public async Task PendingAndMeasuringStatesDoNotPublishArithmeticBeforeTheMeasuredPlan()
    {
        using var media = new DisposableMediaInfo();
        var samplingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSampling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var samples = new CallbackSamples(async (_, _, sample, token) =>
        {
            samplingStarted.TrySetResult();
            await releaseSampling.Task.WaitAsync(token);
            return new RepresentativeSampleEvidence(sample, 25000, 5)
                { Purpose = VideoSamplePurpose.PreviewEstimation };
        });
        using var service = new EstimateBackgroundService(media.Value, Provider(samples), Metadata);

        Queue(service, empiricalPreviewEnabled: true, recommendationsEnabled: false);
        await samplingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        EstimateBackgroundService.SmartEstimateResult measuring = default;
        bool foundMeasuring = SpinWait.SpinUntil(() =>
        {
            while (service.TryDequeueSmart(out var result))
            {
                if (result.EstimateState == QueueEstimateState.Measuring)
                {
                    measuring = result;
                    return true;
                }
            }
            return false;
        }, TimeSpan.FromSeconds(10));
        Assert.True(foundMeasuring, "The exact-plan sampling state was not published before the blocked sample completed.");
        Assert.Equal(0, measuring.EstimatedMb);
        Assert.Null(measuring.BoundEstimate);
        Assert.Equal(1, service.EmpiricalJobsLaunched);
        Assert.Equal(1, service.PendingRefinements);

        releaseSampling.TrySetResult();
        await FinishWorkers(service);
        Assert.True(service.TryDequeueSmart(out var measured));
        Assert.Equal(QueueEstimateState.Measured, measured.EstimateState);
        Assert.Equal(EstimateAuthorityLevel.Measured, measured.BoundEstimate!.Authority);
        Assert.NotNull(measured.EmpiricalPreview);
    }

    [Fact]
    public async Task HundredQueueItemsMeasureOnlySelectedVisibleAndNearTermRows()
    {
        using var media = new DisposableMediaInfo();
        var sampleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSampling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new CallbackSamples(async (_, _, sample, token) =>
        {
            sampleStarted.TrySetResult();
            await releaseSampling.Task.WaitAsync(token);
            return new RepresentativeSampleEvidence(sample, 25000, 5)
                { Purpose = VideoSamplePurpose.PreviewEstimation };
        });
        using var service = new EstimateBackgroundService(media.Value, Provider(runner), Metadata);
        string root = Path.Combine(Path.GetTempPath(), $"MediaFlux.queue-scale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var queueIds = new HashSet<Guid>();
        try
        {
            EncodeExecutionSnapshotSettings settings = EncodeExecutionSnapshotBuilderTests.Settings() with
            {
                AutomaticQuality = true,
                NumericQualityValue = null,
                AutoTargetSize = true,
                TargetSizeText = string.Empty,
                QualityTarget = QualityTarget.Balanced
            };
            var encoder = new VideoEncoderSelection(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc");
            for (int index = 0; index < 100; index++)
            {
                string path = Path.Combine(root, $"queue-{index:D3}.mp4");
                File.WriteAllBytes(path, new byte[1000]);
                Guid itemId = Guid.NewGuid();
                queueIds.Add(itemId);
                QueueEstimateRefinementPriority priority = index switch
                {
                    0 => QueueEstimateRefinementPriority.Selected,
                    1 or 2 => QueueEstimateRefinementPriority.Visible,
                    3 or 4 => QueueEstimateRefinementPriority.NearTerm,
                    _ => QueueEstimateRefinementPriority.Background
                };
                var item = new EncodeExecutionSnapshotItem
                {
                    SourceFilePath = path,
                    LogicalSourcePath = path,
                    SourceSizeBytes = 1000
                };
                service.QueueSmartEstimate(path, true, settings.CompressionProfile, 0, encoder, 0,
                    null, null, false, false, 10, settings.StorageSavings,
                    qualityIntent: EncodingQualityIntent.Automatic(QualityTarget.Balanced),
                    historicalCalibrationEnabled: false,
                    queueItemId: itemId,
                    encoderPreset: settings.EncoderPreset,
                    outputBitDepth: 8,
                    previewSettings: settings,
                    previewItem: item,
                    empiricalPreviewEnabled: true,
                    refinementPriority: priority);
            }

            await sampleStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            object workItems = typeof(EstimateBackgroundService)
                .GetField("_workItems", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service)!;
            workItems.GetType().GetMethod("CompleteAdding")!.Invoke(workItems, null);
            Task[] workers = (Task[])typeof(EstimateBackgroundService)
                .GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service)!;
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15));

            var initialResults = new List<EstimateBackgroundService.SmartEstimateResult>();
            while (service.TryDequeueSmart(out EstimateBackgroundService.SmartEstimateResult result))
                initialResults.Add(result);
            EstimateBackgroundService.SmartEstimateResult[] pending = initialResults
                .Where(result => !result.RecommendationOnly && result.EstimateState == QueueEstimateState.Pending)
                .ToArray();
            Assert.Equal(100, pending.Length);
            Assert.Equal(100, pending.Select(result => result.QueueItemId).Distinct().Count());
            Assert.All(pending, result => Assert.Contains(result.QueueItemId, queueIds));
            Assert.Equal(5, service.PendingRefinements);
            Assert.Equal(1, service.EmpiricalJobsLaunched);
            Assert.Equal(1, service.EmpiricalSamplesLaunched);

            releaseSampling.TrySetResult();
            await service.WaitForRefinementsAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(5, service.EmpiricalJobsLaunched);
            Assert.InRange(service.EmpiricalSamplesLaunched, 1, 15);
            Assert.Equal(0, service.PendingRefinements);
        }
        finally
        {
            releaseSampling.TrySetResult();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MeasuredRefinementReevaluatesRecommendationAgainstItsDisplayedEstimate()
    {
        using var media = new DisposableMediaInfo();
        var recommendationTargets = new List<double>();
        using var service = new EstimateBackgroundService(media.Value, Provider(new Samples((_, _) => 5000)), Metadata,
            recommendationEvaluator: (source, intent) =>
            {
                recommendationTargets.Add(intent.EstimatedOutputMb);
                return new SmartEncodeRecommendation
                {
                    Kind = SmartEncodeRecommendationKind.Review,
                    Confidence = SmartEncodeConfidence.Medium,
                    EstimatedSavingsMb = source.SourceMb - intent.EstimatedOutputMb,
                    EstimatedSavingsPercent = 10,
                    PrimaryReason = "Synthetic recommendation tied to the displayed estimate."
                };
            });

        Queue(service, empiricalPreviewEnabled: true);
        await FinishWorkers(service);

        Assert.True(service.TryDequeueSmart(out var measured));
        Assert.Equal(QueueEstimateState.Measured, measured.EstimateState);
        Assert.Single(recommendationTargets);
        Assert.Equal(measured.EstimatedMb, recommendationTargets[^1], 8);
        Assert.True(service.TryDequeueSmart(out var recommendation));
        Assert.True(recommendation.RecommendationOnly);
        Assert.Equal(measured.SourceMb - measured.EstimatedMb,
            recommendation.Recommendation!.EstimatedSavingsMb!.Value, 8);
    }

    [Fact]
    public async Task OrdinaryBackgroundItemsDoNotLaunchMeasuredPreviewWork()
    {
        using var media = new DisposableMediaInfo();
        var samples = new Samples((_, _) => 5000);
        using var service = new EstimateBackgroundService(media.Value, Provider(samples), Metadata);

        Queue(service, empiricalPreviewEnabled: true,
            storageSavings: new StorageSavingsOptions { Enabled = true, SourceVideoBitratePercent = 50 },
            refinementPriority: QueueEstimateRefinementPriority.Background);
        await FinishWorkers(service);

        Assert.True(service.TryDequeueSmart(out var result));
        Assert.Equal(QueueEstimateState.Pending, result.EstimateState);
        Assert.Equal(0, result.EstimatedMb);
        Assert.Null(result.EmpiricalPreview);
        Assert.Equal(0, service.EmpiricalJobsLaunched);
        Assert.Empty(samples.Qualities);
    }

    [Fact]
    public async Task HundredBackgroundQueueItemsRemainPendingWithoutEmpiricalFanout()
    {
        using var media = new DisposableMediaInfo();
        var samples = new Samples((_, _) => 5000);
        using var service = new EstimateBackgroundService(media.Value, Provider(samples), Metadata);

        for (int index = 0; index < 100; index++)
            Queue(service, empiricalPreviewEnabled: true,
                refinementPriority: QueueEstimateRefinementPriority.Background,
                recommendationsEnabled: false);

        await FinishWorkers(service);
        int estimates = 0;
        while (service.TryDequeueSmart(out var result))
        {
            Assert.Equal(QueueEstimateState.Pending, result.EstimateState);
            Assert.Equal(0, result.EstimatedMb);
            Assert.Null(result.BoundEstimate);
            estimates++;
        }

        Assert.Equal(100, estimates);
        Assert.Equal(0, service.EmpiricalJobsLaunched);
        Assert.Equal(0, service.PendingRefinements);
        Assert.Empty(samples.Qualities);
    }

    [Fact]
    public async Task MeasuredEstimatePublishesBeforeBlockedInformationalRecommendation()
    {
        using var media = new DisposableMediaInfo();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var service = new EstimateBackgroundService(media.Value, Provider(new Samples((_, _) => 5000)), Metadata,
            recommendationEvaluator: (_, _) =>
            {
                entered.SetResult();
                release.Wait();
                throw new IOException("Optional recommendation/history unavailable");
            });
        try
        {
            Queue(service, empiricalPreviewEnabled: true, historicalCalibration: true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            EstimateBackgroundService.SmartEstimateResult first = default;
            bool foundMeasured = SpinWait.SpinUntil(() =>
            {
                while (service.TryDequeueSmart(out var candidate))
                {
                    if (candidate.EstimateState == QueueEstimateState.Measured)
                    {
                        first = candidate;
                        return true;
                    }
                }
                return false;
            }, TimeSpan.FromSeconds(2));
            Assert.True(foundMeasured);
            Assert.True(first.EstimatedMb > 0);
            Assert.Null(first.Recommendation);
            Assert.Null(first.SizeCalibration);
        }
        finally { release.Set(); }
        await FinishWorkers(service);
        Assert.True(service.TryDequeueSmart(out var advice));
        Assert.True(advice.RecommendationOnly);
        Assert.Equal(SmartEncodeRecommendationKind.Unavailable, advice.Recommendation!.Kind);
        Assert.True(advice.EstimatedMb > 0);
    }

    [Theory]
    [InlineData(QualityTarget.SmallerFile)]
    [InlineData(QualityTarget.Efficient)]
    [InlineData(QualityTarget.Balanced)]
    [InlineData(QualityTarget.HighQuality)]
    [InlineData(QualityTarget.MaximumQuality)]
    public async Task WarmQualityRefreshUsesCapturedMetadataEvenWhenSourceCannotBeRead(QualityTarget target)
    {
        using var media = new DisposableMediaInfo();
        int metadataCalls = 0;
        var samples = new Samples((_, _) => throw new InvalidOperationException("No samples"));
        using var service = new EstimateBackgroundService(media.Value, Provider(samples), path =>
        {
            if (++metadataCalls > 1) throw new InvalidOperationException("Metadata must not be reread");
            return Metadata(path);
        });
        Guid queueItemId = Guid.NewGuid();
        Queue(service, queueItemId: queueItemId);
        await FinishWorkers(service);
        service.ResetAndCancel();
        File.Delete(_source); // A settings-only estimate may use the captured snapshot; Start still validates the source.
        Queue(service, queueItemId: queueItemId, qualityChanged: DateTime.UtcNow,
            qualityIntent: EncodingQualityIntent.Automatic(target));
        await FinishWorkers(service);
        Assert.True(service.TryDequeueSmart(out var result));
        Assert.Equal(0, result.EstimatedMb);
        Assert.Equal(QueueEstimateState.Pending, result.EstimateState);
        Assert.Equal(target, result.QualityResolution!.Intent.Target);
        Assert.Equal(1, metadataCalls);
        Assert.Empty(samples.Qualities);
        Assert.Null(result.Recommendation);
        Assert.Null(result.SizeCalibration);
    }

    private static MediaInfoService.MediaInfo Metadata(string _) => new()
    { Width = 1920, Height = 1080, Fps = 30, VideoCodec = "h264", DurationSeconds = 100, BitrateKbps = 4000, VideoStreamCount = 1 };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CapturedMetadataCannotBeReusedByAnotherRowOrProductionRevision(bool replaceRow)
    {
        using var media = new DisposableMediaInfo();
        int reads = 0;
        using var service = new EstimateBackgroundService(media.Value,
            Provider(new Samples((_, _) => throw new InvalidOperationException("No samples"))),
            path => { reads++; return Metadata(path); });
        Guid firstId = Guid.NewGuid();
        Queue(service, queueItemId: firstId);
        await FinishWorkers(service);
        service.ResetAndCancel();
        Queue(service, queueItemId: replaceRow ? Guid.NewGuid() : firstId,
            authorityRevision: replaceRow ? 0 : 1, qualityChanged: DateTime.UtcNow);
        await FinishWorkers(service);
        Assert.True(service.TryDequeueSmart(out var result));
        Assert.Equal(0, result.EstimatedMb);
        Assert.Equal(QueueEstimateState.Pending, result.EstimateState);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task BackgroundUsesMeasuredAuthorityAndFailureStaysUnavailable()
    {
        using var media = new DisposableMediaInfo();
        using (var service = new EstimateBackgroundService(media.Value, Provider(new Samples((_, _) => 5000)), Metadata))
        {
            Queue(service, empiricalPreviewEnabled: true);
            await FinishWorkers(service);
            Assert.True(service.TryDequeueSmart(out var result));
            Assert.Null(result.SizeCalibration);
            Assert.Equal(EstimateAuthorityLevel.Measured, result.BoundEstimate!.Authority);
            Assert.NotNull(result.EmpiricalPreview);
            Assert.Equal(700000 / 1048576d, result.EstimatedMb, 8);
        }
        using (var service = new EstimateBackgroundService(media.Value, Provider(new CallbackSamples((_, _, _, _) =>
                   throw new InvalidDataException("sample failure"))), Metadata))
        {
            Queue(service, empiricalPreviewEnabled: true);
            await FinishWorkers(service);
            Assert.True(service.TryDequeueSmart(out var result));
            Assert.Null(result.SizeCalibration);
            Assert.Null(result.EmpiricalPreview);
            Assert.Equal(QueueEstimateState.Unavailable, result.EstimateState);
            Assert.Null(result.BoundEstimate);
            Assert.Equal(0, result.EstimatedMb);
            Assert.Contains("Measured estimate unavailable", result.EstimateDiagnostic);
        }
    }

    [Fact]
    public async Task OrdinaryQueueEstimateDoesNotWaitForEmpiricalSampling()
    {
        using var media = new DisposableMediaInfo();
        var samples = new Samples((_, _) => 5000);
        using var service = new EstimateBackgroundService(media.Value, Provider(samples), Metadata);

        Queue(service);
        await FinishWorkers(service);

        Assert.True(service.TryDequeueSmart(out var result));
        Assert.Equal(0, result.EstimatedMb);
        Assert.Equal(QueueEstimateState.Pending, result.EstimateState);
        Assert.Null(result.EmpiricalPreview);
        Assert.Null(result.SizeCalibration);
        Assert.Empty(samples.Qualities);
        Assert.DoesNotContain("PreviewUnavailable", result.EstimateDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixedManualQualityUsesMeasuredPreviewWithStorageSavingsEnabled()
    {
        using var media = new DisposableMediaInfo();
        var samples = new Samples((_, _) => 5000);
        using var service = new EstimateBackgroundService(media.Value, Provider(samples), Metadata);

        Queue(service, quality: 24, empiricalPreviewEnabled: true, auto: false,
            storageSavings: new StorageSavingsOptions { Enabled = true, SourceVideoBitratePercent = 50 });
        await FinishWorkers(service);

        Assert.True(service.TryDequeueSmart(out var result));
        Assert.NotNull(result.EmpiricalPreview);
        Assert.Null(result.SizeCalibration);
        Assert.Equal(24, result.QualityResolution!.EffectiveQuality);
        Assert.Equal(EstimateAuthorityLevel.Measured, result.BoundEstimate!.Authority);
        Assert.Equal(3, samples.Qualities.Count);
        Assert.All(samples.Qualities, quality => Assert.Equal(24, quality));
    }

    [Fact]
    public async Task HighTemporalDispersionExpandsPreviewFromThreeToFiveSamples()
    {
        AdaptiveQualitySelectionRequest request = Request();
        var samples = new Samples((_, index) => new[] { 1000d, 3000d, 5000d, 7000d, 9000d }[index]);
        EmpiricalPreviewResult result = await new EmpiricalSizePreviewService(new()).EstimateAsync(
            request, false, samples, quality => $"video:{quality}", CancellationToken.None);

        Assert.Equal(5, result.RegionsPerAnchor);
        Assert.Equal(5, samples.Qualities.Count);
        Assert.Equal(5000, result.Candidates[0].MeanVideoBytesPerSecond);
        Assert.StartsWith("Low confidence; 5-sample temporal projection", result.Confidence, StringComparison.Ordinal);
        Assert.Equal("middle 3 of 5 sample rates, expanded to include the five-sample mean", result.RangeMethod);
        Assert.Equal(5, result.SampleEvidence.Count);
        Assert.Equal(5000, result.SampleEvidence[2].VideoBytesPerSecond);
        Assert.Equal(5, result.SampleEvidence[2].MeasuredSeconds);
        Assert.Equal(300_000 + request.Ancillary.MinimumStreamBytes, result.Candidates[0].ProjectedLowerBytes);
        var rawExtremeProjection = StorageSavingsSampleSelector.ProjectRates(
            request.SourceDuration.TotalSeconds, request.Ancillary, 1000, 5000, 9000, null);
        Assert.True(result.Candidates[0].ProjectedUpperBytes < rawExtremeProjection.Upper);
    }

    [Fact]
    public async Task FiveSampleTrimmedRangeAlwaysContainsTheAllSampleMean()
    {
        var rates = new[] { 20d, 700d, 770d, 840d, 860d };
        var result = await new EmpiricalSizePreviewService(new()).EstimateAsync(Request(), false,
            new Samples((_, index) => rates[index]), quality => $"video:{quality}", CancellationToken.None);
        var candidate = Assert.Single(result.Candidates);

        Assert.Equal(rates.Average(), candidate.MeanVideoBytesPerSecond);
        Assert.InRange(candidate.ProjectedTypicalBytes,
            candidate.ProjectedLowerBytes, candidate.ProjectedUpperBytes);
        Assert.Equal("middle 3 of 5 sample rates, expanded to include the five-sample mean", result.RangeMethod);
        Assert.StartsWith("Low confidence; 5-sample temporal projection", result.Confidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecommendationFailureKeepsMeasuredPlanEstimateAvailable()
    {
        using var media = new DisposableMediaInfo();
        using var service = new EstimateBackgroundService(media.Value, Provider(new Samples((_, _) => 5000)), Metadata,
            recommendationEvaluator: (_, _) => throw new InvalidOperationException("synthetic recommendation failure"));

        Queue(service, empiricalPreviewEnabled: true);
        await FinishWorkers(service);

        Assert.True(service.TryDequeueSmart(out var result));
        Assert.True(result.EstimatedMb > 0);
        Assert.Equal(QueueEstimateState.Measured, result.EstimateState);
        Assert.Null(result.Recommendation);
        Assert.True(service.TryDequeueSmart(out result));
        Assert.True(result.RecommendationOnly);
        Assert.Equal(SmartEncodeRecommendationKind.Unavailable, result.Recommendation!.Kind);
        Assert.Contains("prepared production plan remains valid", result.Recommendation.PrimaryReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerPreservesCapturedAuthorityRevisionOnEmpiricalOrFallbackResult(bool failPreview)
    {
        using var media = new DisposableMediaInfo();
        IAdaptiveVideoSampleRunner runner = failPreview
            ? new CallbackSamples((_, _, _, _) => throw new InvalidDataException("sample unavailable"))
            : new Samples((_, _) => 5000);
        using var service = new EstimateBackgroundService(media.Value, Provider(runner), Metadata);
        Queue(service, authorityRevision: 7, empiricalPreviewEnabled: true);
        await FinishWorkers(service);
        Assert.True(service.TryDequeueSmart(out var result));
        Assert.Equal(7, result.EstimateAuthorityRevision);
        Assert.Null(result.SizeCalibration);
        if (failPreview)
        {
            Assert.Null(result.EmpiricalPreview);
            Assert.Equal(QueueEstimateState.Unavailable, result.EstimateState);
            Assert.Null(result.BoundEstimate);
        }
        else
            Assert.Equal(EstimateAuthorityLevel.Measured, result.BoundEstimate!.Authority);
    }

    [Fact]
    public async Task CancelledOldGenerationCannotPublishOverNewQuality()
    {
        using var media = new DisposableMediaInfo();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new CallbackSamples(async (_, quality, sample, token) =>
        {
            if (quality == 24)
            {
                started.TrySetResult();
                using var registration = token.Register(() => cancelled.TrySetResult());
                await release.Task;
                token.ThrowIfCancellationRequested();
            }
            return new(sample, quality == 24 ? 50000 : 25000, 5) { Purpose = VideoSamplePurpose.PreviewEstimation };
        });
        using var service = new EstimateBackgroundService(media.Value, Provider(runner), Metadata);
        Queue(service, 24, empiricalPreviewEnabled: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        service.ResetAndCancel();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Queue(service, 25, empiricalPreviewEnabled: true);
        release.SetResult();
        await FinishWorkers(service);
        Assert.True(service.TryDequeueSmart(out var result));
        Assert.Equal(service.CurrentGeneration, result.Generation);
        Assert.Equal(25, result.EmpiricalPreview!.DisplayQuality);
        Assert.True(service.TryDequeueSmart(out result));
        Assert.True(result.RecommendationOnly);
        Assert.False(service.TryDequeueSmart(out _));
    }

    [Fact]
    public async Task ActiveSourceOwnershipAndTargetModeNeverLaunchCqSampling()
    {
        using var media = new DisposableMediaInfo();
        var runner = new Samples((_, _) => 5000);
        using (var service = new EstimateBackgroundService(media.Value, Provider(runner), Metadata, _ => true))
        {
            Queue(service, empiricalPreviewEnabled: true);
            await FinishWorkers(service);
            Assert.False(service.TryDequeueSmart(out _));
            Assert.Empty(runner.Qualities);
        }
        using (var service = new EstimateBackgroundService(media.Value, Provider(runner), Metadata))
        {
            Queue(service, target: 123);
            await FinishWorkers(service);
            Assert.True(service.TryDequeueSmart(out var result));
            Assert.Equal(0, result.EstimatedMb);
            Assert.Equal(QueueEstimateState.Pending, result.EstimateState);
            Assert.Null(result.SizeCalibration);
            Assert.Empty(runner.Qualities);
        }
    }

    private sealed class DisposableMediaInfo : IDisposable
    {
        internal MediaInfoService Value { get; } = new(Path.GetTempPath(), persistentCacheEnabled: false);
        public void Dispose() { }
    }

    [Fact]
    public async Task FiveSampleTrimmedRangeUsesEncodedSecondsAndExactAncillaryContainerMath()
    {
        var request = Request() with { Ancillary = new(12000, 3000, 4000, 1234) };
        var result = await new EmpiricalSizePreviewService(new()).EstimateAsync(request, false,
            new Samples((_, i) => new[] { 1000d, 3000d, 5000d, 7000d, 9000d }[i]), q => $"video:{q}", CancellationToken.None);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(5000, candidate.MeanVideoBytesPerSecond);
        Assert.Equal(500000, candidate.ProjectedVideoBytes);
        Assert.Equal(20234, result.AncillaryBytes);
        Assert.Equal(200000, candidate.ContainerBytes, 5);
        Assert.Equal(720234, result.ProjectedTotalBytes, 5);
        Assert.Equal(301234, candidate.ProjectedLowerBytes);
        Assert.Equal(920234, candidate.ProjectedUpperBytes, 5);
        Assert.Equal(5, result.RegionsPerAnchor);
        Assert.Equal(25, result.SecondsPerAnchor);
        Assert.Null(result.LikelyDisposition);
    }

    [Fact]
    public async Task ManualQualityChangesAndReturningToQualityReusesVideoEvidence()
    {
        var cache = new PreviewVideoEvidenceCache();
        var preview = new EmpiricalSizePreviewService(cache);
        var runner = new Samples((q, _) => q == 24 ? 8000 : 4000);
        var request = Request();
        var first = await preview.EstimateAsync(request, false, runner, q => $"video:{q}", default);
        var next = request with { Envelope = new(request.Video.Encoder, QualityTarget.Balanced, EncoderQualityMechanism.Cq, 25, 25) };
        var second = await preview.EstimateAsync(next, false, runner, q => $"video:{q}", default);
        var again = await preview.EstimateAsync(request with { Ancillary = new(10000, 0, 0, 0) }, false, runner, q => $"video:{q}", default);
        Assert.True(second.ProjectedTotalBytes < first.ProjectedTotalBytes);
        Assert.Equal(6, runner.Qualities.Count);
        Assert.Equal(1, again.CacheHits);
        Assert.Equal(first.ProjectedTotalBytes + 10000, again.ProjectedTotalBytes, 5);
    }

    [Fact]
    public async Task TwoAnchorsInterpolateMeanInLogSpaceAndSelectLikelyIntermediate()
    {
        var runner = new Samples((q, _) => q == 24 ? 10000 : 2500);
        var result = await new EmpiricalSizePreviewService(new()).EstimateAsync(Request(), true, runner, q => $"video:{q}", default);
        Assert.Equal(new[] { 24, 26 }, result.Anchors);
        Assert.Equal(6, runner.Qualities.Count);
        Assert.DoesNotContain(25, runner.Qualities);
        Assert.Equal(5000, result.Candidates.Single(c => c.Quality == 25).MeanVideoBytesPerSecond, 7);
        Assert.True(result.Candidates.Single(c => c.Quality == 25).Interpolated);
        Assert.Equal(25, result.PredictedSelectedQuality);
        Assert.Equal(700000, result.ProjectedTotalBytes, 5);
        Assert.Equal(AdaptiveSelectionDisposition.Selected, result.LikelyDisposition);
    }

    [Fact]
    public async Task NonMonotonicAnchorsMeasureIntermediateDirectly()
    {
        var runner = new Samples((q, _) => q == 24 ? 7000 : q == 25 ? 6000 : 8000);
        var result = await new EmpiricalSizePreviewService(new()).EstimateAsync(Request(), true, runner, q => $"video:{q}", default);
        Assert.Equal(15, runner.Qualities.Count);
        Assert.All(result.Candidates, c => Assert.False(c.Interpolated));
        Assert.Equal(6000, result.Candidates.Single(c => c.Quality == 25).MeanVideoBytesPerSecond);
    }

    [Fact]
    public async Task TypicalProjectionPreventsOptimisticBorderlineAdmissionAndForecastsSkip()
    {
        var result = await new EmpiricalSizePreviewService(new()).EstimateAsync(Request(), true,
            new Samples((_, i) => i == 0 ? 8000 : 10000), q => $"video:{q}", default);
        Assert.Null(result.PredictedSelectedQuality);
        Assert.Equal(26, result.DisplayQuality);
        Assert.Equal(AdaptiveSelectionDisposition.Skipped, result.LikelyDisposition);
        Assert.All(result.Candidates, c => Assert.Equal(AdaptiveSampleClassification.Borderline, c.Classification));
        Assert.Equal(result.Candidates[^1].ProjectedTypicalBytes, result.ProjectedTotalBytes);
        Assert.Contains("advisory only", result.Diagnostic);
    }

    [Fact]
    public async Task SingleCandidateAdaptiveEnvelopeNeedsOnlyOneAnchor()
    {
        var request = Request();
        request = request with { Envelope = new(request.Video.Encoder, QualityTarget.Balanced, EncoderQualityMechanism.Cq, 24, 24) };
        var runner = new Samples((_, _) => 5000);
        var result = await new EmpiricalSizePreviewService(new()).EstimateAsync(request, true, runner, q => $"video:{q}", default);
        Assert.Single(result.Anchors);
        Assert.Single(result.Candidates);
        Assert.Equal(3, runner.Qualities.Count);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("write-time")]
    [InlineData("video-settings")]
    [InlineData("duration")]
    public async Task SourceAndVideoIdentityChangesInvalidateCache(string change)
    {
        var preview = new EmpiricalSizePreviewService(new());
        var runner = new Samples((_, _) => 5000);
        var request = Request();
        await preview.EstimateAsync(request, false, runner, q => $"video:{q}", default);
        if (change == "size") File.WriteAllBytes(_source, new byte[1001]);
        if (change == "write-time") File.SetLastWriteTimeUtc(_source, File.GetLastWriteTimeUtc(_source).AddSeconds(1));
        if (change == "duration") request = request with { SourceDuration = TimeSpan.FromSeconds(110) };
        var result = await preview.EstimateAsync(request, false, runner,
            q => change == "video-settings" ? $"new-preset-filter-depth:{q}" : $"video:{q}", default);
        Assert.Equal(6, runner.Qualities.Count);
        Assert.Equal(0, result.CacheHits);
    }

    [Fact]
    public async Task BoundedCacheEvictsLeastRecentlyUsedEvidence()
    {
        var preview = new EmpiricalSizePreviewService(new(1));
        var runner = new Samples((_, _) => 5000);
        await preview.EstimateAsync(Request(), false, runner, _ => "a", default);
        await preview.EstimateAsync(Request(), false, runner, _ => "b", default);
        await preview.EstimateAsync(Request(), false, runner, _ => "a", default);
        Assert.Equal(9, runner.Qualities.Count);
    }

    [Theory]
    [InlineData(4, 1, 4)]
    [InlineData(10, 3, 10)]
    [InlineData(30, 3, 15)]
    [InlineData(900, 3, 15)]
    public void PreviewBudgetAndTemporalStrataAreBounded(double seconds, int count, double budget)
    {
        var positions = VideoSamplingProfile.Preview.Positions(TimeSpan.FromSeconds(seconds));
        Assert.Equal(count, positions.Count);
        Assert.Equal(budget, positions.Sum(p => p.Duration.TotalSeconds), 5);
        for (int i = 0; i < count; i++)
        {
            Assert.InRange(positions[i].Start.TotalSeconds, seconds * i / count - 1e-6, seconds * (i + 1) / count);
            Assert.InRange((positions[i].Start + positions[i].Duration).TotalSeconds, seconds * i / count, seconds * (i + 1) / count + 1e-6);
        }
        Assert.Equal(225, VideoSamplingProfile.Production.Positions(TimeSpan.FromSeconds(900)).Sum(p => p.Duration.TotalSeconds));
    }

    [Fact]
    public async Task PreviewEvidenceCannotBeUsedByProductionSelectorOrFrozenPlan()
    {
        var request = Request();
        AdaptiveQualitySelectionEvidence fallback = await new StorageSavingsSampleSelector(new Samples((_, _) => 5000))
            .SelectAsync(request, null, default);
        Assert.Equal(AdaptiveSelectionDisposition.NotSuitable, fallback.Disposition);
        Assert.Equal(request.Envelope.PreferredQuality, fallback.SelectedQuality);
        Assert.Empty(fallback.Candidates);
        var measured = new RepresentativeSampleEvidence(new("preview", TimeSpan.Zero, TimeSpan.FromSeconds(5)), 1000, 5)
            { Purpose = VideoSamplePurpose.PreviewEstimation };
        Assert.Throws<InvalidDataException>(() => StorageSavingsSampleSelector.ProjectCandidate(request, 24, [measured]));
        var context = AdaptiveStorageSavingsTests.Context();
        var evidence = new AdaptiveQualitySelectionEvidence(AdaptiveSelectionDisposition.Selected, "nvenc", "hevc_nvenc",
            QualityTarget.Balanced, EncoderQualityMechanism.Cq, 24, 24, 26, 2, 29, false, request.Ancillary,
            [new(24, [measured], 1000, 2000, 1000, AdaptiveSampleClassification.ClearlyMeets)], "preview");
        Assert.Throws<InvalidDataException>(() => EncodingPlanService.FreezeAdaptiveSelection(EncodingPlanService.Create(context), evidence));
    }

    [Fact]
    public async Task ProductionCancelsAndDrainsPreviewThenExcludesNewWork()
    {
        var coordinator = new PreviewSamplingCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preview = coordinator.PreviewAsync(async token =>
        {
            started.SetResult();
            using var registration = token.Register(() => cancelled.TrySetResult());
            await cancelled.Task;
            await drain.Task;
            token.ThrowIfCancellationRequested();
            return 1;
        }, default);
        await started.Task;
        var production = coordinator.EnterProductionAsync(default);
        await cancelled.Task;
        Assert.False(production.IsCompleted);
        drain.SetResult();
        await Assert.ThrowsAsync<PreviewUnavailableException>(() => preview);
        using (await production)
            await Assert.ThrowsAsync<PreviewUnavailableException>(() => coordinator.PreviewAsync(_ => Task.FromResult(1), default));
        Assert.Equal(1, await coordinator.PreviewAsync(_ => Task.FromResult(1), default));
    }

    [Fact]
    public async Task PreviewConcurrencyIsOneAndCancelledWaiterNeverStarts()
    {
        var coordinator = new PreviewSamplingCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.PreviewAsync(_ => { started.SetResult(); return finish.Task; }, default);
        await started.Task;
        using var cancellation = new CancellationTokenSource();
        bool secondStarted = false;
        var second = coordinator.PreviewAsync(_ => { secondStarted = true; return Task.FromResult(2); }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.False(secondStarted);
        finish.SetResult(1);
        Assert.Equal(1, await first);
    }
}
