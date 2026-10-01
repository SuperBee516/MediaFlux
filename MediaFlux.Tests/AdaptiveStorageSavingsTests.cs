using System.Text.Json;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AdaptiveStorageSavingsTests
{
    [Theory]
    [InlineData("applicable", true)]
    [InlineData("off", false)]
    [InlineData("hevc", false)]
    [InlineData("other-source", false)]
    [InlineData("manual", false)]
    [InlineData("target", false)]
    [InlineData("qsv", false)]
    [InlineData("other-output", false)]
    [InlineData("dvd", false)]
    [InlineData("benchmark", false)]
    [InlineData("comparison", false)]
    [InlineData("restoration", false)]
    [InlineData("ai", false)]
    [InlineData("upscale", false)]
    [InlineData("unknown-pixels", false)]
    [InlineData("unknown-duration", false)]
    public void RuntimeApplicabilityPreservesRestrictedPrimaryPath(string scenario, bool expected)
    {
        EncodingDecisionContext context = Context();
        context = scenario switch
        {
            "hevc" => context with { Source = Source("hevc") },
            "other-source" => context with { Source = Source("mpeg2video") },
            "manual" => context with { QualityIntent = EncodingQualityIntent.LegacyNumeric(24) },
            "target" => context with { TargetMb = 1 },
            "qsv" => context with { Encoder = new(VideoEncoderIds.Qsv, VideoCodecFamily.Hevc, "hevc_qsv") },
            "other-output" => context with { Encoder = new(VideoEncoderIds.Nvenc, VideoCodecFamily.H264, "h264_nvenc") },
            "dvd" => context with { Input = new EncodingInputSource { Kind = EncodingInputKind.DvdPhysicalConcat } },
            "benchmark" => context with { ValidationProfile = EncodeOutputValidationProfile.BenchmarkSample },
            "comparison" => context with { ValidationProfile = EncodeOutputValidationProfile.SampleComparison },
            "restoration" => context with { Restoration = new() { Mode = VideoRestorationMode.Custom, Preset = VideoRestorationPreset.VintageAnimationLight } },
            "ai" => context with { Restoration = new() { Mode = VideoRestorationMode.Custom, AiMode = AiRestorationMode.General } },
            "upscale" => context with { ScaleMode = EncodingService.ScaleMode.To4K },
            "unknown-pixels" => context with { Source = Source(pixelFormat: "") },
            "unknown-duration" => context with { KnownDuration = TimeSpan.Zero },
            _ => context
        };
        EncodingPlan plan = EncodingPlanService.Create(context);
        Assert.Equal(expected, AdaptiveStorageSavingsPolicy.TryCreateRequest(scenario != "off", true,
            Contract, context, plan, out var request, out _));
        if (expected)
        {
            Assert.Equal(context.EncoderPreset, request!.Video.Preset);
            Assert.Equal(context.Encoder, request.Video.Encoder);
            Assert.Equal(context.TenBit, request.Video.TenBit);
            Assert.True(request.Video.ConcurrentEncoderSessions);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DownscaleUsesExactProductionGeometryAndBitDepth(bool tenBit)
    {
        EncodingDecisionContext context = Context() with { ScaleMode = EncodingService.ScaleMode.To720p, TenBit = tenBit,
            Source = Source(pixelFormat: tenBit ? "yuv420p10le" : "yuv420p") };
        EncodingPlan plan = EncodingPlanService.Create(context);
        Assert.True(AdaptiveStorageSavingsPolicy.TryCreateRequest(true, false, Contract, context, plan, out var request, out _));
        Assert.Equal(EncodingPlanService.GetExecutionValues(plan).Geometry, request!.Video.Geometry);
        Assert.Equal(720, request.Video.Geometry.Height);
        Assert.Equal(tenBit ? "p010le" : "nv12", request.Video.Geometry.PixelFormat);
        Assert.Equal(context.Source.Streams[0].PixelFormat, request.Video.SourcePixelFormat);
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(25.001)]
    [InlineData(40)]
    [InlineData(74)]
    [InlineData(75)]
    [InlineData(10000)]
    public void RepresentativePositionsAreDeterministicBoundedAndNeverOverlap(double seconds)
    {
        TimeSpan duration = TimeSpan.FromSeconds(seconds);
        var positions = StorageSavingsSampleSelector.Positions(duration);
        Assert.Equal(positions, StorageSavingsSampleSelector.Positions(duration));
        Assert.Equal(seconds <= 25 ? 1 : 3, positions.Count);
        Assert.Equal(TimeSpan.Zero, positions[0].Start);
        Assert.Equal(duration, positions[^1].Start + positions[^1].Duration);
        Assert.All(positions, p => Assert.InRange(p.Duration.TotalSeconds, double.Epsilon, 25));
        for (int i = 1; i < positions.Count; i++)
            Assert.True(positions[i - 1].Start + positions[i - 1].Duration <= positions[i].Start);
    }

    [Fact]
    public async Task PreferredClearlyMeetsAndStopsBeforeAnyStrongerCandidate()
    {
        var runner = new FakeSamples((_, _) => 5_000);
        var result = await Select(runner);
        Assert.Equal(24, result.SelectedQuality);
        Assert.Single(result.Candidates);
        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal(AdaptiveSampleClassification.ClearlyMeets, result.Candidates[0].Classification);
    }

    [Fact]
    public async Task HighestQualityClearlyMeetingTestedCandidateWinsOverBorderline()
    {
        var runner = new FakeSamples((q, i) => q == 24 ? 10_000 : q == 25 ? i == 0 ? 5_000 : 10_000 : 4_000);
        var result = await Select(runner);
        Assert.Equal(new[] { 24, 25, 26 }, result.Candidates.Select(c => c.Quality));
        Assert.Equal(26, result.SelectedQuality);
        Assert.Equal(AdaptiveSampleClassification.Borderline, result.Candidates[1].Classification);
        Assert.DoesNotContain(runner.Calls, c => c.Quality == 27);
    }

    [Fact]
    public async Task AllMissReturnsTypedPolicyEvidenceWithoutExtrapolating()
    {
        var runner = new FakeSamples((_, _) => 10_000);
        var result = await Select(runner);
        Assert.Equal(AdaptiveSelectionDisposition.Skipped, result.Disposition);
        Assert.Null(result.SelectedQuality);
        Assert.Equal(new[] { 24, 25, 26, 27 }, result.Candidates.Select(c => c.Quality));
        Assert.All(result.Candidates, c => Assert.Equal(AdaptiveSampleClassification.ClearlyMisses, c.Classification));
        Assert.False(EncodingRetryPolicy.AllowsAutomaticRetry(EncodingTerminalResult.AdaptiveStorageSavingsSkipped));
        Assert.Contains("acceptable quality", new AdaptiveStorageSavingsSkippedException(result).Message);
    }

    [Fact]
    public async Task BorderlineIsNotMissAndProceedsAtHighestQualityBorderline()
    {
        var result = await Select(new FakeSamples((q, i) => q == 24 ? 10_000 : i == 0 ? 5_000 : 10_000));
        Assert.Equal(AdaptiveSelectionDisposition.Selected, result.Disposition);
        Assert.Equal(25, result.SelectedQuality);
        Assert.Contains("borderline", result.Reason);
    }

    [Fact]
    public async Task ExactByteBoundaryClearlyMeets()
    {
        var result = await Select(new FakeSamples((_, _) => 7_000));
        Assert.Equal(Contract.MaximumAcceptedOutputBytes!.Value, result.Candidates[0].ProjectedUpperBytes);
        Assert.Equal(AdaptiveSampleClassification.ClearlyMeets, result.Candidates[0].Classification);
    }

    [Fact]
    public async Task UnusableMeasurementRetainsPreferredAndDoesNotSkip()
    {
        var result = await Select(new FakeSamples((_, _) => 0));
        Assert.Equal(AdaptiveSelectionDisposition.NotSuitable, result.Disposition);
        Assert.Equal(24, result.SelectedQuality);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task AncillaryAllowanceChangesCandidateRelationshipWithoutAuthorizingOutput()
    {
        AdaptiveQualitySelectionRequest request = Request() with { Ancillary = new(500_000, 0, 0, 0) };
        var result = await Select(new FakeSamples((_, _) => 5_000), request);
        Assert.Equal(AdaptiveSelectionDisposition.Selected, result.Disposition);
        Assert.All(result.Candidates, c => Assert.Equal(AdaptiveSampleClassification.Borderline, c.Classification));
        Assert.Equal(StorageSavingsAcceptance.Rejected, StorageSavingsContractService.Evaluate(Contract, 900_001).Acceptance);
        Assert.Equal(StorageSavingsAcceptance.Accepted, StorageSavingsContractService.Evaluate(Contract, 900_000).Acceptance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task CancellationNeverStartsLaterSamplesOrCandidates(int cancelAfter)
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeSamples((_, _) => 10_000) { OnMeasure = count => { if (count == cancelAfter) cancellation.Cancel(); } };
        if (cancelAfter == 0) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StorageSavingsSampleSelector(runner).SelectAsync(Request(), null, cancellation.Token));
        Assert.Equal(cancelAfter, runner.Calls.Count);
    }

    [Fact]
    public async Task FinalPlanUsesChosenQualityAndKeepsAutomaticIntentWithoutPreferredPredictionEvidence()
    {
        EncodingPlan initial = EncodingPlanService.Create(Context());
        var selected = await Select(new FakeSamples((q, _) => q < 26 ? 10_000 : 4_000));
        EncodingPlan final = EncodingPlanService.FreezeAdaptiveSelection(initial, selected);
        Assert.Equal(24, initial.Quality!.EffectiveQuality);
        Assert.Equal(26, final.Quality!.EffectiveQuality);
        Assert.Equal(initial.Quality.Intent, final.Quality.Intent);
        Assert.Equal(26, EncodingPlanService.GetExecutionValues(final).QualityResolution.EffectiveQuality);
        Assert.Same(selected, final.AdaptiveSelection);
        Assert.Null(final.SourceAdaptiveShadow);
        Assert.Null(final.Estimates.HistoricalPrediction);
        Assert.Null(final.SizePredictionCalibration);
        Assert.False(final.Recovery!.TolerantRecoveryPermitted);
        Assert.Equal(0, final.Recovery.MaximumRetryCount);
        Assert.All(final.RecoveryCapabilities!.Items, capability =>
        {
            Assert.False(capability.Permitted);
            Assert.Equal(0, capability.MaximumAttempts);
        });
        AdaptiveQualitySelectionEvidence? roundTrip = JsonSerializer.Deserialize<AdaptiveQualitySelectionEvidence>(JsonSerializer.Serialize(selected));
        Assert.Equal(selected.SelectedQuality, roundTrip!.SelectedQuality);
        Assert.Equal(selected.Candidates[0].Samples[0], roundTrip.Candidates[0].Samples[0]);
    }

    [Theory]
    [InlineData(false, 192_000)]
    [InlineData(true, 384_000)]
    public void ActualAacActionIsBudgetedByPlannedChannelMode(bool surround, long rate)
    {
        MediaProbeResult source = new() { Success = true, Streams = [new() { Index = 1, CodecType = "audio", CodecName = "ac3" }] };
        var container = new OutputContainerDecision { StreamPlans = [new(1, "audio", "ac3", StreamCompatibilityAction.Transcode, "test", "aac")] };
        Assert.True(AdaptiveStorageSavingsPolicy.TryAncillaryAllowance(source, container, surround ? 6 : null, 100, out var allowance, out _));
        Assert.Equal(rate * 100 / 8d, allowance!.AudioBytes);
    }

    [Fact]
    public void AncillaryInventoryCountsOnlyResolvedMappedActionsAndRejectsUnknownCopySizes()
    {
        MediaProbeResult source = new() { Success = true, Streams = [
            new() { Index = 1, CodecType = "audio", BitRate = 128_000 },
            new() { Index = 2, CodecType = "subtitle" },
            new() { Index = 3, CodecType = "data", BitRate = 16_000 },
            new() { Index = 4, CodecType = "attachment", ExtraDataSizeBytes = 1234 }] };
        var container = new OutputContainerDecision { CopyDataStreams = true, CopyAttachments = true, StreamPlans = [
            new(1, "audio", "aac", StreamCompatibilityAction.Copy, "test"),
            new(2, "subtitle", "ass", StreamCompatibilityAction.Copy, "test"),
            new(3, "data", "bin", StreamCompatibilityAction.Copy, "test"),
            new(4, "attachment", "font", StreamCompatibilityAction.Copy, "test")] };
        Assert.True(AdaptiveStorageSavingsPolicy.TryAncillaryAllowance(source, container, null, 100, out var allowance, out _));
        Assert.Equal(1_600_000, allowance!.AudioBytes);
        Assert.Equal(100_000, allowance.SubtitleBytes);
        Assert.Equal(200_000, allowance.DataBytes);
        Assert.Equal(1234, allowance.AttachmentBytes);
        var unknown = new MediaProbeResult { Streams = [new() { Index = 1, CodecType = "audio" }] };
        Assert.False(AdaptiveStorageSavingsPolicy.TryAncillaryAllowance(unknown, container, null, 100, out _, out _));
        var omitted = new OutputContainerDecision { StreamPlans = [new(1, "audio", "aac", StreamCompatibilityAction.Omit, "test")] };
        Assert.True(AdaptiveStorageSavingsPolicy.TryAncillaryAllowance(unknown, omitted, null, 100, out allowance, out _));
        Assert.Equal(0, allowance!.StreamBytes);
    }

    [Fact]
    public async Task StatisticsCannotCoerceSkippedSampleEvidenceIntoSuccessfulOutput()
    {
        var evidence = await Select(new FakeSamples((_, _) => 10_000));
        string root = Path.Combine(Path.GetTempPath(), "MediaFluxAdaptiveStatisticsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var statistics = new EncodingStatisticsService(Path.Combine(root, "statistics.jsonl"));
            Assert.True(statistics.AppendFinalized(new EncodingStatisticsRecord
            {
                Outcome = EncodingStatisticsOutcome.Success, OutputSizeBytes = 123456,
                ProcessingSeconds = 12, AdaptiveSelection = evidence
            }));
            var read = Assert.Single(new EncodingStatisticsService(Path.Combine(root, "statistics.jsonl")).GetAll());
            Assert.Equal(EncodingStatisticsOutcome.AdaptiveStorageSavingsSkipped, read.Outcome);
            Assert.Null(read.OutputSizeBytes);
            Assert.Null(read.SourceAdaptiveShadow);
            Assert.Equal(0, read.ProcessingSeconds);
            var aggregate = EncodingStatisticsCalculator.Aggregate([read]);
            Assert.Equal(0, aggregate.Successful);
            Assert.Equal(1, aggregate.Skipped);
            Assert.Equal(0, aggregate.Failed);
            var history = new JobHistoryRecord { Status = JobStatus.Skipped,
                TerminalResult = EncodingTerminalResult.AdaptiveStorageSavingsSkipped, AdaptiveSelection = evidence };
            Assert.Contains("acceptable quality", JobHistoryPresentation.OutcomeLabel(history));
            Assert.Contains("No full encode", JobHistoryPresentation.OutcomeSummary(history));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("data")]
    [InlineData("attachment")]
    public void UnknownMappedAncillarySizeBypassesAdaptationWithoutInventingBytes(string type)
    {
        var source = new MediaProbeResult { Streams = [new() { Index = 1, CodecType = type }] };
        var container = new OutputContainerDecision { CopyDataStreams = true, CopyAttachments = true,
            StreamPlans = [new(1, type, "unknown", StreamCompatibilityAction.Copy, "test")] };
        Assert.False(AdaptiveStorageSavingsPolicy.TryAncillaryAllowance(source, container, null, 100, out _, out _));
    }

    [Fact]
    public void ProbePreservesActualAttachmentByteInventory()
    {
        var parsed = FfprobeService.ParseProbeJson("""
            {"streams":[{"index":3,"codec_type":"attachment","codec_name":"ttf","extradata_size":12345}],
             "format":{"duration":"100","size":"1000000"}}
            """, "source.mkv");
        Assert.Equal(12345, parsed.Streams[0].ExtraDataSizeBytes);
    }

    internal static readonly StorageSavingsContract Contract = StorageSavingsContractService.Resolve(true, 1_000_000);
    internal static EncodingDecisionContext Context() => new(Source(), EncodingInputSource.FromFile("C:\\media\\sample-source.mkv"),
        new(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc"), true, null, EncodingService.ScaleMode.None,
        new(), "p5", 24, false, null, EncodingService.StreamMapMode.KeepAll, true, true, true,
        OutputContainerSelection.Matroska, ContainerCompatibilityPolicy.Intelligent, TimeSpan.FromSeconds(100),
        QualityIntent: EncodingQualityIntent.Automatic(QualityTarget.Balanced));

    internal static MediaProbeResult Source(string codec = "h264", string pixelFormat = "yuv420p") => new()
    {
        Success = true, DurationSeconds = 100,
        Streams = [new() { Index = 0, CodecType = "video", CodecName = codec, Width = 1920, Height = 1080,
            FrameRate = 30, BitRate = 4_000_000, PixelFormat = pixelFormat }]
    };

    internal static AdaptiveQualitySelectionRequest Request()
    {
        var context = Context();
        Assert.True(AdaptiveStorageSavingsPolicy.TryCreateRequest(true, false, Contract, context,
            EncodingPlanService.Create(context), out var request, out _));
        return request!;
    }

    private static Task<AdaptiveQualitySelectionEvidence> Select(FakeSamples runner, AdaptiveQualitySelectionRequest? request = null) =>
        new StorageSavingsSampleSelector(runner).SelectAsync(request ?? Request(), null, CancellationToken.None);

    internal sealed class FakeSamples(Func<int, int, double> rate) : IAdaptiveVideoSampleRunner
    {
        public List<(int Quality, RepresentativeSample Sample)> Calls { get; } = [];
        public Action<int>? OnMeasure { get; init; }
        public Task<RepresentativeSampleEvidence> MeasureAsync(AdaptiveQualitySelectionRequest request, int quality,
            RepresentativeSample sample, CancellationToken cancellationToken)
        {
            Calls.Add((quality, sample));
            OnMeasure?.Invoke(Calls.Count);
            int position = Calls.Count(c => c.Quality == quality) - 1;
            return Task.FromResult(new RepresentativeSampleEvidence(sample, (long)(rate(quality, position) * sample.Duration.TotalSeconds), sample.Duration.TotalSeconds));
        }
    }
}
