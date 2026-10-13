using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodeLifecycleDiagnosticsTests
{
    [Theory]
    [InlineData("[mp4] Starting second pass: moving the moov atom to the beginning of the file", true)]
    [InlineData("[mov] Starting second pass - moving moov atom to beginning", true)]
    [InlineData("frame= 100 time=00:01:00.00", false)]
    [InlineData("Starting second pass: rewriting audio packets", false)]
    public void RecognizesOnlyMp4MoovRelocation(string line, bool expected) =>
        Assert.Equal(expected, EncodeLifecycleDiagnostics.IsFaststartMarker(line));

    [Fact]
    public void FaststartPresentationPreventsLateFrameProgressFromRestoringBareHundredPercent()
    {
        var lifecycle = new EncodeLifecycleDiagnostics();
        Assert.True(EncodeLifecycleDiagnostics.ShouldApplyFrameProgress("Encoding"));
        lifecycle.Record(EncodeLifecycleEvent.FaststartStart);
        Assert.True(lifecycle.FaststartActive);
        Assert.Equal("Finalizing MP4…", EncodeLifecycleDiagnostics.FaststartStatus);
        Assert.Equal("Frames done", EncodeLifecycleDiagnostics.FaststartProgress);
        Assert.False(EncodeLifecycleDiagnostics.ShouldApplyFrameProgress(EncodeLifecycleDiagnostics.FaststartStatus));
        lifecycle.Record(EncodeLifecycleEvent.FfmpegExit);
        Assert.False(lifecycle.FaststartActive);
        Assert.False(EncodeLifecycleDiagnostics.ShouldApplyFrameProgress("Verifying output"));
        Assert.False(EncodeLifecycleDiagnostics.ShouldApplyFrameProgress("Finalizing"));
    }

    [Fact]
    public void SimulatedLifecycleRecordsExactBoundariesAndConservativeStorageRate()
    {
        var lifecycle = new EncodeLifecycleDiagnostics();
        DateTime t = new(2026, 9, 28, 20, 0, 0, DateTimeKind.Utc);
        lifecycle.RecordPreviousQueueCompletion(t.AddSeconds(-4));
        lifecycle.Record(EncodeLifecycleEvent.QueueDispatch, t);
        lifecycle.Record(EncodeLifecycleEvent.FfmpegStart, t.AddSeconds(2));
        lifecycle.Record(EncodeLifecycleEvent.FirstProgress, t.AddSeconds(3));
        lifecycle.Record(EncodeLifecycleEvent.LastProgress, t.AddSeconds(100));
        lifecycle.Record(EncodeLifecycleEvent.FaststartStart, t.AddSeconds(101));
        lifecycle.Record(EncodeLifecycleEvent.LastProgress, t.AddSeconds(150)); // final FFmpeg summary is not active frame progress
        lifecycle.Record(EncodeLifecycleEvent.FfmpegExit, t.AddSeconds(201));
        lifecycle.Record(EncodeLifecycleEvent.StdoutComplete, t.AddSeconds(202));
        lifecycle.Record(EncodeLifecycleEvent.StderrComplete, t.AddSeconds(203));
        lifecycle.Record(EncodeLifecycleEvent.VerificationStart, t.AddSeconds(204));
        lifecycle.Record(EncodeLifecycleEvent.StagedVerificationEnd, t.AddSeconds(205));
        lifecycle.Record(EncodeLifecycleEvent.FinalizationStart, t.AddSeconds(205));
        lifecycle.Record(EncodeLifecycleEvent.FinalizationEnd, t.AddSeconds(206));
        lifecycle.Record(EncodeLifecycleEvent.PromotedVerificationStart, t.AddSeconds(206));
        lifecycle.Record(EncodeLifecycleEvent.VerificationEnd, t.AddSeconds(207));
        lifecycle.Record(EncodeLifecycleEvent.QueueCompleted, t.AddSeconds(210));
        lifecycle.RecordProcessIo(10_000, 20_000);
        EncodeLifecycleTiming result = lifecycle.Snapshot(@"Z:\source.mp4", @"Z:\output.mp4", 100_000_000);
        Assert.Equal(97, result.ActiveEncodeSeconds);
        Assert.Equal(1, result.LastProgressToFaststartSeconds);
        Assert.Equal(100, result.ContainerFinalizeSeconds);
        Assert.Equal(2, result.ProcessDrainSeconds);
        Assert.Equal(1, result.ReaderCompletionToVerificationSeconds);
        Assert.Equal(2, result.OutputVerificationSeconds);
        Assert.Equal(1, result.StagedVerificationSeconds);
        Assert.Equal(1, result.PromotedVerificationSeconds);
        Assert.Equal(1, result.FinalizationStageSeconds);
        Assert.Equal(3, result.VerificationEndToQueueCompletedSeconds);
        Assert.Equal(4, result.PreviousCompletionToDispatchSeconds);
        Assert.Equal(1_000_000, result.ContainerFinalizeBytesPerSecond);
        Assert.Equal(20_000, result.FfmpegWrittenBytes);
        Assert.Equal(t.AddSeconds(100), result.LastProgressUtc);
    }

    [Fact]
    public void NoFaststartMarkerLeavesOptionalFieldsUnknown()
    {
        var lifecycle = new EncodeLifecycleDiagnostics();
        lifecycle.Record(EncodeLifecycleEvent.FfmpegStart);
        lifecycle.Record(EncodeLifecycleEvent.FfmpegExit);
        EncodeLifecycleTiming result = lifecycle.Snapshot("source.mkv", "output.mkv", null);
        Assert.Null(result.FaststartStartUtc);
        Assert.Null(result.ContainerFinalizeSeconds);
        Assert.Null(result.ContainerFinalizeBytesPerSecond);
    }

    [Fact]
    public void GpuMissingSamplesStayUnavailableWhileMeasuredZeroCounts()
    {
        EncodingSystemTelemetry[] values =
        [
            new(null, null, null, 0, null, null, null, null, "unavailable"),
            new(null, null, null, 0, 0, 0, 0, 0, "sampled", 0, "RTX", "GPU-1"),
            new(null, null, null, 0, 40, 20, 10, 1024, "sampled", 0, "RTX", "GPU-1")
        ];
        using var service = new EncodingDiagnosticsService(new SequenceTelemetry(values), TimeSpan.FromDays(1));
        service.Start(new("job", "movie", "GPU", "nvenc", "h264_nvenc", "p5", "1080p", "1080p", 8, 60, @"Z:\source.mp4"));
        var lifecycle = new EncodeLifecycleDiagnostics();
        lifecycle.Record(EncodeLifecycleEvent.FirstProgress, DateTime.UtcNow.AddSeconds(-2));
        service.AttachLifecycle("job", lifecycle);
        for (int i = 0; i < 3; i++) service.CaptureNow();
        lifecycle.Record(EncodeLifecycleEvent.LastProgress);
        EncodingDiagnosticSummary summary = service.Complete("job", lifecycle: lifecycle.Snapshot(@"Z:\source.mp4", @"Z:\output.mp4", 100))!;
        Assert.Equal(2, summary.GpuValidSamples);
        Assert.Equal(2, summary.GpuEncodeValidSamples);
        Assert.Equal(2, summary.GpuDecodeValidSamples);
        Assert.Equal(2, summary.GpuMemoryValidSamples);
        Assert.Equal(10, summary.AverageGpuEncodePercent);
        Assert.Equal(10, summary.ActiveEncodeGpuEncodePercent);
        Assert.Contains("index 0", summary.SampledGpuIdentity);
    }

    [Fact]
    public void GpuTotalsSurviveBoundedProgressSampleEviction()
    {
        var telemetry = new EncodingSystemTelemetry(null, null, null, 0, 1, 20, 2, 0, "sampled", 0, "RTX", "GPU-1");
        using var service = new EncodingDiagnosticsService(new SequenceTelemetry([telemetry]), TimeSpan.FromDays(1));
        service.Start(new("job", "movie", "GPU", "nvenc", "h264_nvenc", "p5", "1080p", "1080p", 8, 60, @"Z:\source.mp4"));
        var lifecycle = new EncodeLifecycleDiagnostics();
        lifecycle.Record(EncodeLifecycleEvent.FirstProgress);
        service.AttachLifecycle("job", lifecycle);
        for (int i = 0; i < 350; i++) service.CaptureNow();
        lifecycle.Record(EncodeLifecycleEvent.LastProgress);
        lifecycle.Record(EncodeLifecycleEvent.FaststartStart);
        for (int i = 0; i < 350; i++) service.CaptureNow();
        EncodingDiagnosticSummary result = service.Complete("job", lifecycle: lifecycle.Snapshot(@"Z:\source.mp4", @"Z:\output.mp4", 100))!;
        Assert.Equal(EncodingDiagnosticsService.MaximumSamplesPerSession, result.Samples);
        Assert.Equal(700, result.GpuEncodeValidSamples);
        Assert.Equal(350, result.ActiveEncodeGpuEncodeValidSamples);
        Assert.Equal(20, result.ActiveEncodeGpuEncodePercent);
    }

    [Fact]
    public void LongContainerPhaseIsNotMisclassifiedAsCertainStorageFailure()
    {
        var timing = new EncodeLifecycleTiming { ContainerFinalizeSeconds = 180 };
        string observation = EncodingDiagnosticInterpreter.InterpretCompleted(null, timing);
        Assert.Contains("may be contributing", observation);
        Assert.DoesNotContain("definitely", observation);
    }

    private sealed class SequenceTelemetry(EncodingSystemTelemetry[] values) : IEncodingSystemTelemetryProvider
    {
        private int _index;
        public EncodingSystemTelemetry Sample() => values[Math.Min(Interlocked.Increment(ref _index) - 1, values.Length - 1)];
    }
}
