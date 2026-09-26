using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class FailureDiagnosticReportBuilderTests
{
    [Fact]
    public void NoisyH264ReportIsProbabilisticBoundedAndKeepsUnknownEvidence()
    {
        var collector = new FfmpegDiagnosticCollector();
        for (int i = 0; i < 900; i++) collector.Observe($"[h264 @ {i + 10000000:x8}] Invalid NAL unit size (0 > {26000 + i}).", FfmpegDiagnosticComponent.Ffmpeg);
        for (int i = 0; i < 400; i++) collector.Observe("[h264 @ 000001d6df9649c0] Error splitting the input into NAL units.", FfmpegDiagnosticComponent.Ffmpeg);
        for (int i = 0; i < 300; i++) collector.Observe("[dec:h264 @ 000001d6df96c7c0] Error submitting packet to decoder: Invalid data found when processing input", FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe("unrecognized retained diagnostic", FfmpegDiagnosticComponent.Ffmpeg);
        string raw = string.Join('\n', Enumerable.Range(0, 60).Select(i => "terminal line " + i));

        string report = new FailureDiagnosticReportBuilder().Build(new FailureDiagnosticReportContext(
            "Encode", "C:\\source.mkv", "C:\\output.mkv", -1094995529, "Source video decode",
            collector.Complete(), raw, CreatePlan(), CreateRecoveryOutcome()));

        Assert.Contains("MEDIAFLUX FAILURE DIAGNOSTIC REPORT", report);
        Assert.Contains("Malformed or corrupt H.264 bitstream data is probable.", report);
        Assert.Contains("900 occurrence(s)", report);
        Assert.Contains("expected-size=26000–26899", report);
        Assert.Contains("Unknown: 1 occurrence(s)", report);
        Assert.Contains("Last 20 retained raw stderr line(s), in original order:", report);
        Assert.Contains("terminal line 59", report);
        Assert.DoesNotContain("terminal line 0", report);
        Assert.True(report.Length < 25_000, $"Report was {report.Length} characters.");
    }

    [Fact]
    public void MissingPlanRecoveryAndValidationStillProducesUsefulReportWithoutMutatingRawEvidence()
    {
        const string raw = "first raw line\nlast raw line";
        string report = new FailureDiagnosticReportBuilder().Build(new FailureDiagnosticReportContext(
            "Encode", "source.mkv", "", 1, "FFmpeg process failure", null, raw));

        Assert.Contains("Plan: Not available.", report);
        Assert.Contains("Authoritative execution outcome: Not available.", report);
        Assert.Contains("last raw line", report);
        Assert.Equal("first raw line\nlast raw line", raw);
    }

    [Fact]
    public void RecoveryAndFinalizationUseAuthoritativeOutcomeValues()
    {
        string report = new FailureDiagnosticReportBuilder().Build(new FailureDiagnosticReportContext(
            "Encode", "source.mkv", "output.mkv", 1, "Validation", null, "raw",
            CreatePlan(), CreateRecoveryOutcome()));

        Assert.Contains("Recovery attempt 1/1: VideoDecode/Tolerant; process=Succeeded; media=Rejected; result=Failed", report);
        Assert.Contains("Validation            : Failed; frame deficit", report);
        Assert.Contains("Output disposition    : Rejected or not accepted", report);
    }

    [Fact]
    public void CompanionRawArtifactPreservesBoundedRawTextWithoutNormalization()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MediaFlux-ReportTests", Guid.NewGuid().ToString("N"));
        const string raw = "[h264 @ 000001d6df9649c0] Invalid NAL unit size (0 > 26098).\nterminal line";
        try
        {
            FailureDiagnosticReportArtifact artifact = Assert.IsType<FailureDiagnosticReportArtifact>(
                ErrorLogService.TryWriteFailureDiagnosticArtifacts("unused", "curated", raw, directory));
            Assert.Equal("curated", File.ReadAllText(artifact.ReportPath));
            Assert.Equal(raw, File.ReadAllText(artifact.RawEvidencePath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MixedSourceAndTerminalDiskEvidenceDoesNotClaimAConfirmedTerminalCause()
    {
        var collector = new FfmpegDiagnosticCollector();
        collector.Observe("[h264 @ 000001d6df9649c0] Invalid NAL unit size (0 > 26098).", FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe("[h264 @ 000001d6df9649c0] Error splitting the input into NAL units.", FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe("Error submitting packet to decoder: Invalid data found when processing input", FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe("Error writing trailer: No space left on device", FfmpegDiagnosticComponent.Ffmpeg);

        string report = new FailureDiagnosticReportBuilder().Build(new FailureDiagnosticReportContext(
            "Encode", "source.mkv", "output.mkv", 1, "FFmpeg process failure", collector.Complete(), "raw"));

        Assert.Contains("prevents assigning the terminal cause", report);
        Assert.Contains("Terminal-cause note", report);
    }

    [Fact]
    public void HardwareEvidenceHasPriorityOverUncorroboratedDecoderWarnings()
    {
        var collector = new FfmpegDiagnosticCollector();
        collector.Observe("Error splitting the input into NAL units.", FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe("[h264 @ 000001d6df9649c0] Missing picture in access unit with size 477", FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe("[h264 @ 000001d6df9649c0] Cannot load cuvid: CUDA_ERROR_UNKNOWN", FfmpegDiagnosticComponent.Ffmpeg);

        FfmpegDiagnosticClassification classification = collector.Complete().Classification;
        Assert.Equal(FfmpegDiagnosticCategory.HardwareAcceleration, classification.PrimaryCategory);
        Assert.NotEqual(FfmpegDiagnosticConfidence.High, classification.Confidence);
    }

    [Fact]
    public void RealWorldH264PatternRemainsSourceOrientedAfterDownstreamEncoderAbort()
    {
        var collector = new FfmpegDiagnosticCollector();
        collector.Observe("stream 1, missing mandatory atoms, broken header", FfmpegDiagnosticComponent.Ffmpeg);
        for (int i = 0; i < 3000; i++)
        {
            collector.Observe($"[h264 @ {i + 10000000:x8}] Invalid NAL unit size (0 > {67 + i}).", FfmpegDiagnosticComponent.Ffmpeg);
            collector.Observe($"[h264 @ {i + 20000000:x8}] missing picture in access unit with size {71 + i}", FfmpegDiagnosticComponent.Ffmpeg);
        }
        collector.Observe("Error splitting the input into NAL units", FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe("Error submitting packet to decoder: Invalid data found when processing input", FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe("[enc:hevc_nvenc] Could not open encoder before EOF", FfmpegDiagnosticComponent.Ffmpeg);

        FfmpegDiagnosticSummary summary = collector.Complete();
        FfmpegDiagnosticFamilySummary missing = Assert.Single(summary.Families, x => x.Family == "Missing picture in access unit");
        Assert.Equal(3000, missing.Occurrences);
        Assert.Equal((71L, 3070L), missing.ValueRanges["access-unit-size"]);
        Assert.Equal(0, summary.DroppedNewFamilies);
        Assert.Equal(FfmpegDiagnosticCategory.SourceIntegrity, summary.Classification.PrimaryCategory);
        Assert.Equal(FfmpegDiagnosticConfidence.High, summary.Classification.Confidence);
        Assert.Contains(summary.Families, x => x.Family == "Downstream encoder abort" && x.Category == FfmpegDiagnosticCategory.EncoderInitialization);
    }

    [Fact]
    public void SingleAttemptReportRetainsCommandExitAndBoundedStderrContext()
    {
        string longLine = new('x', 1500);
        var attempt = new FfmpegAttemptDiagnostic(
            1, "Primary encode", "-i source.mkv -c:v hevc_nvenc", 1,
            "first stderr\n" + longLine, null, IsTerminal: true);

        string report = new FailureDiagnosticReportBuilder().Build(new FailureDiagnosticReportContext(
            "Encode", "source.mkv", "output.mkv", 1, "FFmpeg process failure", null,
            attempt.StandardError, Attempts: new[] { attempt }));

        Assert.Contains("Attempt 1: Primary encode [terminal]", report);
        Assert.Contains("Command", report);
        Assert.Contains("-i source.mkv -c:v hevc_nvenc", report);
        Assert.Contains("Exit", report);
        Assert.Contains(": 1", report);
        Assert.Contains("first stderr", report);
        Assert.Contains("[line truncated in curated report]", report);
        Assert.DoesNotContain(longLine, report);
    }

    [Fact]
    public void FailedPrimaryAndFallbackRemainDistinctAndOrdered()
    {
        var attempts = new[]
        {
            new FfmpegAttemptDiagnostic(1, "Primary encode", "primary-command", 1, "primary stderr", null),
            new FfmpegAttemptDiagnostic(2, "Software decode fallback", "fallback-command", 1, "fallback stderr", null, IsTerminal: true)
        };

        string report = new FailureDiagnosticReportBuilder().Build(new FailureDiagnosticReportContext(
            "Encode", "source.mkv", "output.mkv", 1, "FFmpeg process failure", null,
            "===== FFmpeg attempt 1 =====\nprimary stderr\n===== FFmpeg attempt 2 =====\nfallback stderr",
            Attempts: attempts));

        int primary = report.IndexOf("Attempt 1: Primary encode", StringComparison.Ordinal);
        int fallback = report.IndexOf("Attempt 2: Software decode fallback [terminal]", StringComparison.Ordinal);
        Assert.True(primary >= 0 && fallback > primary);
        Assert.Contains("primary-command", report);
        Assert.Contains("fallback-command", report);
        Assert.Contains("primary stderr", report);
        Assert.Contains("fallback stderr", report);
    }

    [Fact]
    public void SuccessfulFallbackCanBeMarkedAsTerminalAttempt()
    {
        var attempts = new[]
        {
            new FfmpegAttemptDiagnostic(1, "Primary encode", "primary-command", 1, "primary stderr", null),
            new FfmpegAttemptDiagnostic(2, "Software decode fallback", "fallback-command", 0, "fallback completed", null, IsTerminal: true)
        };

        string report = new FailureDiagnosticReportBuilder().Build(new FailureDiagnosticReportContext(
            "Encode", "source.mkv", "output.mkv", 0, "Validation", null,
            "fallback completed", Attempts: attempts));

        Assert.Contains("Attempt 1: Primary encode", report);
        Assert.Contains("Attempt 2: Software decode fallback [terminal]", report);
        Assert.Contains("Exit", report);
        Assert.Contains(": 0", report);
        Assert.Contains("fallback completed", report);
    }

    private static EncodingPlan CreatePlan() => new()
    {
        IsAvailable = true,
        Source = new EncodingPlanSource("h264", 1920, 1080, 23.976, 7200),
        Video = new EncodingPlanVideo("Reencode", "hevc", "hevc_nvenc", 1920, 1080, 1920, 1080, "yuv420p"),
        Container = new EncodingPlanContainer(OutputContainerSelection.Matroska, OutputContainer.Matroska, "Requested MKV"),
        Hardware = new EncodingPlanHardware(true, "nvenc", true),
        Audio = new[] { new EncodingPlanStream(1, "audio", "aac", StreamCompatibilityAction.Copy, null, "Compatible") }
    };

    private static EncodingExecutionOutcome CreateRecoveryOutcome() => new(
        Guid.NewGuid(), Array.Empty<EncodingPreflightOutcome>(),
        new[] { new EncodingRecoveryOutcome(EncodingRecoveryKind.VideoDecode, EncodingRecoveryFailureClass.SourceVideoCorruption, EncodingRecoveryMode.Strict, EncodingRecoveryMode.Tolerant, 1, 1, EncodingRecoveryResult.Failed, "decode persisted", EncodingRecoveryProcessResult.Succeeded, EncodingRecoveryDisposition.Rejected) },
        new EncodingValidationOutcome(EncodingLifecycleStatus.Failed, EncodingLifecycleStatus.Failed, EncodingLifecycleStatus.Failed, EncodingLifecycleStatus.Passed, EncodingLifecycleStatus.Passed, EncodingLifecycleStatus.Passed, "stage.mkv", "FrameDeficit", "frame deficit"),
        new EncodingFinalizationOutcome(EncodingLifecycleStatus.Failed, false, "output.mkv", EncodingSourceDisposition.Retained, "Retained", "Validation", "frame deficit"),
        EncodingTerminalResult.ValidationFailed);
}
