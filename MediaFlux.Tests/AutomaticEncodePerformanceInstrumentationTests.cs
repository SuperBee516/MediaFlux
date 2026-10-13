using System.Diagnostics;
using System.Text.Json;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AutomaticEncodePerformanceInstrumentationTests
{
    [Fact]
    public async Task AutomaticSelectionAndPlanIdentityAreUnchangedWhenDiagnosticsAreActive()
    {
        AdaptiveQualitySelectionRequest request = AdaptiveStorageSavingsTests.Request();
        AdaptiveQualitySelectionEvidence baseline = await Select(request);
        var instrumentation = new AutomaticEncodePerformanceInstrumentation("Automatic", "source.mkv", DateTime.UtcNow);
        AdaptiveQualitySelectionEvidence observed;
        using (instrumentation.Activate())
        {
            instrumentation.ObserveProgress("Analyzing compression…");
            observed = await Select(request);
            instrumentation.RecordAdaptiveSelection(observed, request.Contract.MaximumAcceptedOutputBytes);
        }
        AutomaticEncodePerformanceDiagnostics report = instrumentation.BuildReport();

        Assert.Equal(baseline.Disposition, observed.Disposition);
        Assert.Equal(baseline.SelectedQuality, observed.SelectedQuality);
        Assert.Equal(16, baseline.SamplesExecuted);
        Assert.True(baseline.SevenFirstEarlyPositive);
        Assert.Equal(baseline.SamplesExecuted, observed.SamplesExecuted);
        Assert.Equal(baseline.SevenFirstEarlyPositive, observed.SevenFirstEarlyPositive);
        Assert.True(report.SevenFirstEarlyPositive);
        Assert.Equal(16, report.EvidenceSampleCount);
        Assert.Contains("Seven-first early positive", AutomaticEncodePerformanceInstrumentation.FormatHumanReadable(report));
        Assert.Equal(baseline.Candidates.Select(candidate => candidate.Quality),
            observed.Candidates.Select(candidate => candidate.Quality));
        Assert.Equal(baseline.Candidates.SelectMany(candidate => candidate.Samples).Select(sample => sample.Sample),
            observed.Candidates.SelectMany(candidate => candidate.Samples).Select(sample => sample.Sample));

        EncodingPlan plan = EncodingPlanService.Create(AdaptiveStorageSavingsTests.Context());
        EncodingPlan frozenWithoutDiagnostics = EncodingPlanService.FreezeAdaptiveSelection(plan, observed);
        using (instrumentation.Activate())
        {
            using IDisposable? freezeScope = instrumentation.MeasurePlanFreeze();
            EncodingPlan frozenWithDiagnostics = EncodingPlanService.FreezeAdaptiveSelection(plan, observed);
            Assert.Equal(frozenWithoutDiagnostics.PlanId, frozenWithDiagnostics.PlanId);
            Assert.Equal(frozenWithoutDiagnostics.Quality?.EffectiveQuality, frozenWithDiagnostics.Quality?.EffectiveQuality);
            Assert.Equal(frozenWithoutDiagnostics.AdaptiveSelection?.Disposition, frozenWithDiagnostics.AdaptiveSelection?.Disposition);
        }
    }

    [Theory]
    [InlineData(AdaptiveSelectionDisposition.Selected)]
    [InlineData(AdaptiveSelectionDisposition.Skipped)]
    public void AutomaticReportRetainsCandidateSampleAndProcessEvidenceForEncodeOrSkip(AdaptiveSelectionDisposition disposition)
    {
        var instrumentation = new AutomaticEncodePerformanceInstrumentation("Automatic / Balanced", "source.mkv", DateTime.UtcNow);
        RepresentativeSample first = new("region-1", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        RepresentativeSample second = new("region-2", TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(10));
        using (instrumentation.Activate())
        {
            instrumentation.ObserveProgress("Analyzing compression…");
            instrumentation.ObserveProgress("Testing compression… CQ 30");
            RecordSample(instrumentation, 30, first, 10_000, 10);
            AdaptiveCandidateEvidence firstCandidate = Candidate(30, first, 10_000, 10,
                disposition == AdaptiveSelectionDisposition.Selected
                    ? AdaptiveSampleClassification.ClearlyMeets
                    : AdaptiveSampleClassification.ClearlyMisses);
            AdaptiveCandidateEvidence[] candidates = [firstCandidate];
            if (disposition == AdaptiveSelectionDisposition.Skipped)
            {
                instrumentation.ObserveProgress("Testing compression… CQ 31");
                RecordSample(instrumentation, 31, second, 12_000, 10);
                candidates = [firstCandidate, Candidate(31, second, 12_000, 10, AdaptiveSampleClassification.ClearlyMisses)];
            }

            AdaptiveQualitySelectionEvidence evidence = Evidence(disposition,
                disposition == AdaptiveSelectionDisposition.Selected ? 30 : null,
                candidates);
            instrumentation.RecordAdaptiveSelection(evidence, 500_000);
            using (instrumentation.MeasurePlanFreeze()) { }
            instrumentation.SetTerminalResult(disposition == AdaptiveSelectionDisposition.Skipped
                ? EncodingTerminalResult.AdaptiveStorageSavingsSkipped.ToString()
                : EncodingTerminalResult.Completed.ToString());
        }

        AutomaticEncodePerformanceDiagnostics report = instrumentation.BuildReport();
        int expectedCount = disposition == AdaptiveSelectionDisposition.Selected ? 1 : 2;
        Assert.Equal(expectedCount, report.CandidateCount);
        Assert.Equal(expectedCount, report.SampleCount);
        Assert.Equal(expectedCount, report.EvidenceSampleCount);
        Assert.Equal(expectedCount * 10, report.TotalMeasuredSampleMediaSeconds);
        Assert.Equal(disposition.ToString(), report.AdaptiveDisposition);
        Assert.All(report.Stages, stage =>
        {
            Assert.True(stage.DurationSeconds is null or >= 0);
            Assert.True(stage.StartOffsetSeconds is null or >= 0);
            Assert.True(stage.EndOffsetSeconds is null or >= 0);
        });
        Assert.Equal(report.Stages.OrderBy(stage => stage.StartOffsetSeconds ?? double.MaxValue)
                .ThenBy(stage => stage.EndOffsetSeconds ?? double.MaxValue).Select(stage => stage.Name),
            report.Stages.Select(stage => stage.Name));
        Assert.All(report.Samples, sample =>
        {
            Assert.True(sample.TotalWallSeconds >= 0);
            Assert.True(sample.FfmpegProcessLaunchSeconds is null or >= 0);
            Assert.True(sample.FfmpegEncodeSeconds is null or >= 0);
            Assert.True(sample.MetadataProbeSeconds is null or >= 0);
            Assert.True(sample.PacketByteScanSeconds is null or >= 0);
            Assert.Equal(1, sample.FfmpegProcessCount);
        });
        Assert.Equal(0, report.ProductionFfmpegProcessCount);
        Assert.Null(report.ProductionFfmpegProcessSeconds);
        Assert.Null(report.ProductionFfmpegStartupSeconds);
        Assert.Equal(AutomaticPerformanceProcessPurpose.AdaptiveSampleFfmpeg,
            Assert.Single(report.ProcessCounts, process => process.Tool == AutomaticPerformanceProcessTool.Ffmpeg).Purpose);
        Assert.All(report.Candidates, candidate => Assert.Equal(500_000L, candidate.MaximumAcceptedOutputBytes));
    }

    [Fact]
    public void ManualReportHasNoAdaptiveWorkAndContextsDoNotAccumulateAcrossJobs()
    {
        var manual = new AutomaticEncodePerformanceInstrumentation("Manual / 23", "manual.mkv", DateTime.UtcNow);
        using (manual.Activate())
        {
            manual.RecordQualityResolution(new(
                EncodingQualityIntent.LegacyNumeric(23), 23, EncoderQualityMechanism.Cq,
                EncodingQualityAssessment.Unknown, false, []));
            manual.RecordPreviewDrain(active: false, cancellationRequested: false, Stopwatch.GetTimestamp(), completed: true);
        }
        AutomaticEncodePerformanceDiagnostics manualReport = manual.BuildReport("manual-id", "Completed");
        Assert.Null(manualReport.AdaptiveSelectionSeconds);
        Assert.Null(manualReport.SelectorExecutionSeconds);
        Assert.Equal(0, manualReport.CandidateCount);
        Assert.Equal(0, manualReport.SampleCount);
        Assert.Equal(23, manualReport.SelectedQuality);
        Assert.Equal("inactive", manualReport.Preview.Status);
        Assert.Null(manualReport.Preview.DrainSeconds);
        Assert.Null(AutomaticEncodePerformanceInstrumentation.Current);

        var next = new AutomaticEncodePerformanceInstrumentation("Automatic", "next.mkv", DateTime.UtcNow);
        using (next.Activate()) { }
        AutomaticEncodePerformanceDiagnostics nextReport = next.BuildReport("next-id", "Skipped");
        Assert.Equal(0, nextReport.CandidateCount);
        Assert.Equal(0, nextReport.SampleCount);
        Assert.Empty(nextReport.ProcessCounts);
    }

    [Fact]
    public void BatchJsonContainsStructuredDiagnosticsAndHumanSummary()
    {
        var instrumentation = new AutomaticEncodePerformanceInstrumentation("Automatic", "source.mkv", DateTime.UtcNow);
        using (instrumentation.Activate())
            instrumentation.RecordNamedStage("snapshotPreparation", Stopwatch.GetTimestamp(), TimeSpan.Zero, completed: true);
        AutomaticEncodePerformanceDiagnostics report = instrumentation.BuildReport("operation-123", "Skipped");
        string summary = AutomaticEncodePerformanceInstrumentation.FormatHumanReadable(report);
        var job = new HeadlessEncodeJobResult(1, "operation-123", "source.mkv", HeadlessEncodeJobStatus.Skipped,
            report.StartedUtc, report.EndedUtc, Performance: report, PerformanceSummary: summary);
        var batch = new HeadlessEncodeBatchResult(1, "CompletedWithRejections", 6,
            report.StartedUtc, report.EndedUtc, [job]);

        using JsonDocument json = JsonDocument.Parse(batch.ToJson());
        JsonElement item = json.RootElement.GetProperty("jobs")[0];
        JsonElement performance = item.GetProperty("performance");
        Assert.Equal(1, performance.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("operation-123", performance.GetProperty("operationId").GetString());
        Assert.True(performance.GetProperty("totalWallSeconds").GetDouble() >= 0);
        Assert.True(performance.GetProperty("stages").GetArrayLength() > 0);
        Assert.Contains("Automatic Encode Performance", item.GetProperty("performanceSummary").GetString());
    }

    [Fact]
    public void FailedOrCancelledSampleCanLeavePartialTimingWithoutInventingMeasuredMedia()
    {
        var instrumentation = new AutomaticEncodePerformanceInstrumentation("Automatic", "source.mkv", DateTime.UtcNow);
        var sample = new RepresentativeSample("partial-region", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8));
        using (instrumentation.Activate())
        {
            instrumentation.ObserveProgress("Analyzing compression…");
            instrumentation.ObserveProgress("Testing compression… CRF 28");
            using AutomaticEncodePerformanceInstrumentation.SampleScope timing =
                instrumentation.BeginAdaptiveSample(28, sample)!;
            timing.RecordFfmpegEncode(TimeSpan.Zero);
            instrumentation.SetTerminalResult("Cancelled");
        }

        AutomaticEncodePerformanceDiagnostics report = instrumentation.BuildReport("partial", "Cancelled");
        AutomaticEncodeSampleTiming partial = Assert.Single(report.Samples);
        Assert.Null(partial.MeasuredMediaSeconds);
        Assert.Null(partial.VideoPayloadBytes);
        Assert.Equal(0, report.EvidenceSampleCount);
        Assert.Equal(1, report.SampleCount);
        Assert.Equal("partial", report.OperationId);
        Assert.Equal("Cancelled", report.TerminalResult);
        Assert.All(report.Stages, stage => Assert.True(stage.DurationSeconds is null or >= 0));
    }

    [Fact]
    public void AdaptiveSampleTimingCapturesSeekMappingAndDurationValidation()
    {
        var instrumentation = new AutomaticEncodePerformanceInstrumentation("Automatic", "source.mp4", DateTime.UtcNow);
        var sample = new RepresentativeSample("opening-region", TimeSpan.Zero, TimeSpan.FromSeconds(25));
        using (instrumentation.Activate())
        {
            using AutomaticEncodePerformanceInstrumentation.SampleScope timing =
                instrumentation.BeginAdaptiveSample(25, sample)!;
            instrumentation.RecordAdaptiveSampleSeekMapping(0.033, TimeSpan.FromSeconds(0.033));
            timing.RecordDurationValidation(25, 25, 60, 1 / 60d + 0.001, passed: true);
            timing.RecordEvidence(1_000, 25);
        }

        AutomaticEncodeSampleTiming captured = Assert.Single(instrumentation.BuildReport("run", "Completed").Samples);
        Assert.Equal(0, captured.SourceStartSeconds);
        Assert.Equal(0.033, captured.SourceVideoStartOffsetSeconds);
        Assert.Equal(0.033, captured.EffectiveInputStartSeconds);
        Assert.Equal(25, captured.RequestedDurationSeconds);
        Assert.Equal(25, captured.ExpectedAvailableDurationSeconds);
        Assert.Equal(25, captured.MeasuredMediaSeconds);
        Assert.Equal(60, captured.OutputFrameRate);
        Assert.Equal(1 / 60d + 0.001, captured.DurationToleranceSeconds);
        Assert.True(captured.DurationValidationPassed);
    }

    private static async Task<AdaptiveQualitySelectionEvidence> Select(AdaptiveQualitySelectionRequest request) =>
        await new StorageSavingsSampleSelector(new AdaptiveStorageSavingsTests.FakeSamples(
                (quality, _) => quality <= request.Envelope.PreferredQuality ? 10_000 : 4_000))
            .SelectAsync(request, null, CancellationToken.None);

    private static void RecordSample(
        AutomaticEncodePerformanceInstrumentation instrumentation,
        int quality,
        RepresentativeSample sample,
        long bytes,
        double mediaSeconds)
    {
        using AutomaticEncodePerformanceInstrumentation.SampleScope timing =
            instrumentation.BeginAdaptiveSample(quality, sample)!;
        using (AutomaticEncodePerformanceInstrumentation.EnterProcessPurpose(AutomaticPerformanceProcessPurpose.AdaptiveSampleFfmpeg))
            instrumentation.RecordExternalProcessStart("ffmpeg.exe", FfmpegDiagnosticComponent.Ffmpeg,
                TimeSpan.Zero, AutomaticPerformanceProcessPurpose.AdaptiveSampleFfmpeg);
        using (AutomaticEncodePerformanceInstrumentation.EnterProcessPurpose(AutomaticPerformanceProcessPurpose.AdaptiveSampleMetadata))
            instrumentation.RecordExternalProcessStart("ffprobe.exe", FfmpegDiagnosticComponent.Ffprobe,
                TimeSpan.Zero, AutomaticPerformanceProcessPurpose.AdaptiveSampleMetadata);
        using (AutomaticEncodePerformanceInstrumentation.EnterProcessPurpose(AutomaticPerformanceProcessPurpose.AdaptiveSamplePacketAccounting))
            instrumentation.RecordExternalProcessStart("ffprobe.exe", FfmpegDiagnosticComponent.Ffprobe,
                TimeSpan.Zero, AutomaticPerformanceProcessPurpose.AdaptiveSamplePacketAccounting);
        timing.RecordFfmpegEncode(TimeSpan.Zero);
        timing.RecordMetadataProbe(TimeSpan.Zero);
        timing.RecordPacketScan(TimeSpan.Zero);
        timing.RecordEvidence(bytes, mediaSeconds);
    }

    private static AdaptiveCandidateEvidence Candidate(
        int quality,
        RepresentativeSample sample,
        long bytes,
        double measuredSeconds,
        AdaptiveSampleClassification classification) =>
        new(quality,
            [new RepresentativeSampleEvidence(sample, bytes, measuredSeconds)],
            classification == AdaptiveSampleClassification.ClearlyMisses ? 600_000 : 300_000,
            classification == AdaptiveSampleClassification.ClearlyMisses ? 700_000 : 350_000,
            10_000,
            classification)
        { ProjectedTypicalBytes = classification == AdaptiveSampleClassification.ClearlyMisses ? 650_000 : 325_000 };

    private static AdaptiveQualitySelectionEvidence Evidence(
        AdaptiveSelectionDisposition disposition,
        int? selectedQuality,
        IReadOnlyList<AdaptiveCandidateEvidence> candidates) =>
        new(disposition, VideoEncoderIds.Nvenc, "hevc_nvenc", QualityTarget.Balanced,
            EncoderQualityMechanism.Cq, 30, selectedQuality, 32, null, null, false,
            new(10, 0, 0, 0), candidates, "Instrumentation fixture")
        {
            SamplingSeconds = 0.02,
            SourceDurationSeconds = 100
        };
}
