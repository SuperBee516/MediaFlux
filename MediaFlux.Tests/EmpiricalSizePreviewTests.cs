using MediaFlux.Models;
using MediaFlux.Services;
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

    private QueueEmpiricalPreviewProvider Provider(IAdaptiveVideoSampleRunner runner, MediaProbeResult? source = null) =>
        new(new EncodingService(Path.GetTempPath(), _ => { }), new SourceProbe(source ?? AdaptiveStorageSavingsTests.Source()),
            new EncodeExecutionSnapshotBuilder(), "test-tools", (_, _, _) => runner, q => $"video:{q}", new PreviewSamplingCoordinator());

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
        var result = await Provider(runner, source).EstimateAsync(EncodeExecutionSnapshotBuilderTests.Settings() with
            { NumericQualityValue = 19, TenBit = true }, new() { SourceFilePath = _source, LogicalSourcePath = _source }, default);
        Assert.Equal(19, result.Quality.EffectiveQuality);
        Assert.Equal(19, result.Result.DisplayQuality);
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

    private async Task FinishWorkers(EstimateBackgroundService service)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object queue = typeof(EstimateBackgroundService).GetField("_workItems", flags)!.GetValue(service)!;
        queue.GetType().GetMethod("CompleteAdding")!.Invoke(queue, null);
        var tasks = (Task[])typeof(EstimateBackgroundService).GetField("_workers", flags)!.GetValue(service)!;
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private void Queue(EstimateBackgroundService service, int quality = 24, double target = 0, long authorityRevision = 0,
        bool empiricalPreviewEnabled = false, DateTime? qualityChanged = null,
        EncodingQualityIntent? qualityIntent = null, bool historicalCalibration = true,
        Guid queueItemId = default)
    {
        service.QueueSmartEstimate(_source, target <= 0, "Medium", target,
            new(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc"), quality, null, null, false, true, 10, new(),
            qualityIntent: qualityIntent, historicalCalibrationEnabled: historicalCalibration,
            previewSettings: EncodeExecutionSnapshotBuilderTests.Settings() with { NumericQualityValue = quality,
                TargetSizeText = target > 0 ? target.ToString(System.Globalization.CultureInfo.CurrentCulture) : "" },
            previewItem: new() { SourceFilePath = _source, LogicalSourcePath = _source },
            estimateAuthorityRevision: authorityRevision, queueItemId: queueItemId,
            empiricalPreviewEnabled: empiricalPreviewEnabled, qualityChangeStartedUtc: qualityChanged);
    }

    [Fact]
    public async Task FirstEstimateIsPublishedBeforeBlockedRecommendationAndWithoutHistory()
    {
        using var media = new DisposableMediaInfo();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var service = new EstimateBackgroundService(media.Value, Provider(new Samples((_, _) =>
            throw new InvalidOperationException("Sampling is not permitted"))), Metadata,
            recommendationEvaluator: (_, _) =>
            {
                entered.SetResult();
                release.Wait();
                throw new IOException("Optional recommendation/history unavailable");
            });
        try
        {
            Queue(service, historicalCalibration: true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(service.TryDequeueSmart(out var first));
            Assert.True(first.EstimatedMb > 0);
            Assert.Null(first.Recommendation);
            Assert.False(first.SizeCalibration!.Applied);
            Assert.Equal(0, first.SizeCalibration.SampleCount);
            Assert.Contains("optional history is not queried", first.SizeCalibration.Reason);
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
        Queue(service);
        await FinishWorkers(service);
        service.ResetAndCancel();
        File.Delete(_source); // A settings-only estimate may use the captured snapshot; Start still validates the source.
        Queue(service, qualityChanged: DateTime.UtcNow, qualityIntent: EncodingQualityIntent.Automatic(target));
        await FinishWorkers(service);
        Assert.True(service.TryDequeueSmart(out var result));
        Assert.True(result.EstimatedMb > 0);
        Assert.Equal(target, result.QualityResolution!.Intent.Target);
        Assert.Equal(1, metadataCalls);
        Assert.Empty(samples.Qualities);
        Assert.Null(result.Recommendation);
        Assert.Equal(0, result.SizeCalibration!.SampleCount);
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
        Assert.True(result.EstimatedMb > 0);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task BackgroundUsesEmpiricalAuthorityAndFailureUsesVisibleGenericFallback()
    {
        using var media = new DisposableMediaInfo();
        using (var service = new EstimateBackgroundService(media.Value, Provider(new Samples((_, _) => 5000)), Metadata))
        {
            Queue(service, empiricalPreviewEnabled: true);
            await FinishWorkers(service);
            Assert.True(service.TryDequeueSmart(out var result));
            Assert.Equal(EmpiricalPreviewResult.ModelId, result.SizeCalibration!.EstimateModelId);
            Assert.NotNull(result.EmpiricalPreview);
            Assert.Equal(700000 / 1048576d, result.EstimatedMb, 8);
        }
        using (var service = new EstimateBackgroundService(media.Value, Provider(new CallbackSamples((_, _, _, _) =>
                   throw new InvalidDataException("sample failure"))), Metadata))
        {
            Queue(service, empiricalPreviewEnabled: true);
            await FinishWorkers(service);
            Assert.True(service.TryDequeueSmart(out var result));
            Assert.Equal("GenericBppV1", result.SizeCalibration!.EstimateModelId);
            Assert.Null(result.EmpiricalPreview);
            Assert.Contains("low confidence", result.EstimateDiagnostic);
            Assert.Equal(SmartEncodeConfidence.Low, SmartEncodeDecisionService.ResolveEstimateConfidenceCeiling(result.SizeCalibration));
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
        Assert.True(result.EstimatedMb > 0);
        Assert.Null(result.EmpiricalPreview);
        Assert.Equal("BitrateArithmetic", result.SizeCalibration!.EstimateModelId);
        Assert.Empty(samples.Qualities);
        Assert.DoesNotContain("PreviewUnavailable", result.EstimateDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecommendationFailureKeepsDeterministicEstimateAvailable()
    {
        using var media = new DisposableMediaInfo();
        using var service = new EstimateBackgroundService(media.Value, Provider(new Samples((_, _) => 5000)), Metadata,
            recommendationEvaluator: (_, _) => throw new InvalidOperationException("synthetic recommendation failure"));

        Queue(service, empiricalPreviewEnabled: false);
        await FinishWorkers(service);

        Assert.True(service.TryDequeueSmart(out var result));
        Assert.True(result.EstimatedMb > 0);
        Assert.Null(result.Recommendation);
        Assert.True(service.TryDequeueSmart(out result));
        Assert.True(result.RecommendationOnly);
        Assert.Equal(SmartEncodeRecommendationKind.Unavailable, result.Recommendation!.Kind);
        Assert.Contains("estimate remains valid", result.Recommendation.PrimaryReason, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal(failPreview ? "GenericBppV1" : EmpiricalPreviewResult.ModelId, result.SizeCalibration!.EstimateModelId);
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
            Assert.Equal(123, result.EstimatedMb);
            Assert.Equal("ManualTarget", result.SizeCalibration!.EstimateModelId);
            Assert.Empty(runner.Qualities);
        }
    }

    private sealed class DisposableMediaInfo : IDisposable
    {
        internal MediaInfoService Value { get; } = new(Path.GetTempPath(), persistentCacheEnabled: false);
        public void Dispose() { }
    }

    [Fact]
    public async Task EqualStrataMeanUsesEncodedSecondsAndExactAncillaryContainerMath()
    {
        var request = Request() with { Ancillary = new(12000, 3000, 4000, 1234) };
        var result = await new EmpiricalSizePreviewService(new()).EstimateAsync(request, false,
            new Samples((_, i) => (i + 1) * 1000), q => $"video:{q}", CancellationToken.None);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(5000, candidate.MeanVideoBytesPerSecond);
        Assert.Equal(500000, candidate.ProjectedVideoBytes);
        Assert.Equal(20234, result.AncillaryBytes);
        Assert.Equal(200000, candidate.ContainerBytes, 5);
        Assert.Equal(720234, result.ProjectedTotalBytes, 5);
        Assert.Equal(101234, candidate.ProjectedLowerBytes);
        Assert.Equal(1120234, candidate.ProjectedUpperBytes, 5);
        Assert.Equal(9, result.RegionsPerAnchor);
        Assert.Equal(45, result.SecondsPerAnchor);
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
        Assert.Equal(18, runner.Qualities.Count);
        Assert.Equal(1, again.CacheHits);
        Assert.Equal(first.ProjectedTotalBytes + 10000, again.ProjectedTotalBytes, 5);
    }

    [Fact]
    public async Task TwoAnchorsInterpolateMeanInLogSpaceAndSelectLikelyIntermediate()
    {
        var runner = new Samples((q, _) => q == 24 ? 10000 : 2500);
        var result = await new EmpiricalSizePreviewService(new()).EstimateAsync(Request(), true, runner, q => $"video:{q}", default);
        Assert.Equal(new[] { 24, 26 }, result.Anchors);
        Assert.Equal(18, runner.Qualities.Count);
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
        Assert.Equal(27, runner.Qualities.Count);
        Assert.All(result.Candidates, c => Assert.False(c.Interpolated));
        Assert.Equal(6000, result.Candidates.Single(c => c.Quality == 25).MeanVideoBytesPerSecond);
    }

    [Fact]
    public async Task TypicalProjectionPreventsOptimisticBorderlineAdmissionAndForecastsSkip()
    {
        var result = await new EmpiricalSizePreviewService(new()).EstimateAsync(Request(), true,
            new Samples((_, i) => i == 0 ? 1000 : 10000), q => $"video:{q}", default);
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
        Assert.Equal(9, runner.Qualities.Count);
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
        Assert.Equal(18, runner.Qualities.Count);
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
        Assert.Equal(27, runner.Qualities.Count);
    }

    [Theory]
    [InlineData(4, 1, 4)]
    [InlineData(10, 3, 10)]
    [InlineData(30, 9, 30)]
    [InlineData(900, 9, 45)]
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
