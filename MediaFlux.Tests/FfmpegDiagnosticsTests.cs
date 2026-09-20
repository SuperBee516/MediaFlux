using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class FfmpegDiagnosticsTests
{
    [Fact]
    public void VolatileAddressesAndNalSizesCollapseWhileRetainingRanges()
    {
        var collector = new FfmpegDiagnosticCollector();
        string first = "[h264 @ 000001d6df9649c0] Invalid NAL unit size (0 > 26098).";
        string second = "[h264 @ 000001d6df965340] Invalid NAL unit size (0 > 30228).";
        collector.Observe(first, FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe(second, FfmpegDiagnosticComponent.Ffmpeg);

        FfmpegDiagnosticFamilySummary family = Assert.Single(collector.Complete().Families);
        Assert.Equal("Invalid NAL unit size", family.Family);
        Assert.Equal(2, family.Occurrences);
        Assert.Equal((0L, 0L), family.ValueRanges["invalid-size"]);
        Assert.Equal((26098L, 30228L), family.ValueRanges["expected-size"]);
        Assert.Contains(first, family.RepresentativeRawMessages);
        Assert.Contains(second, family.RepresentativeRawMessages);
    }

    [Fact]
    public void RepetitionIsBoundedAndRawEvidenceRemainsUnchanged()
    {
        var collector = new FfmpegDiagnosticCollector();
        for (int i = 0; i < 2000; i++)
            collector.Observe($"[h264 @ {i:x8}] Invalid NAL unit size (0 > {26000 + i}).", FfmpegDiagnosticComponent.Ffmpeg);

        FfmpegDiagnosticFamilySummary family = Assert.Single(collector.Complete().Families);
        Assert.Equal(2000, family.Occurrences);
        Assert.True(family.SamplesTruncated);
        Assert.True(family.RepresentativeRawMessages.Count <= 6);
        Assert.Equal("[h264 @ 00000000] Invalid NAL unit size (0 > 26000).", family.RepresentativeRawMessages[0]);
    }

    [Fact]
    public void CorrelatedH264EvidenceIsConservativelyClassified()
    {
        var collector = new FfmpegDiagnosticCollector();
        collector.Observe("[h264 @ 000001d6df9649c0] Invalid NAL unit size (0 > 26098).", FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe("[h264 @ 000001d6df9649c0] Error splitting the input into NAL units.", FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe("[vist#0:0/h264 @ 000001d6e0114040] [dec:h264 @ 000001d6df96c7c0] Error submitting packet to decoder: Invalid data found when processing input", FfmpegDiagnosticComponent.Ffmpeg);

        FfmpegDiagnosticSummary summary = collector.Complete();
        Assert.Equal(FfmpegDiagnosticCategory.SourceIntegrity, summary.Classification.PrimaryCategory);
        Assert.Equal(FfmpegDiagnosticConfidence.High, summary.Classification.Confidence);
        Assert.Contains("Malformed or corrupt H.264 bitstream data", summary.Classification.ProbableCause);
    }

    [Fact]
    public void UnknownAndMalformedInputAreSafeAndNotMergedWithKnownFamilies()
    {
        var collector = new FfmpegDiagnosticCollector();
        const string unknown = "[strange @ xyz] unanticipated fish-shaped diagnostic";
        collector.Observe(unknown, FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe(null!, FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe("[h264 @ 12345678] Invalid NAL unit size (0 > 7).", FfmpegDiagnosticComponent.Ffmpeg);

        FfmpegDiagnosticSummary summary = collector.Complete();
        Assert.Contains(summary.Families, x => x.Family == "Unknown" && x.RepresentativeRawMessages.Contains(unknown));
        Assert.Contains(summary.Families, x => x.Family == "Invalid NAL unit size");
    }

    [Fact]
    public void FamilyLimitCountsOverflowWithoutFailingCollection()
    {
        var aggregator = new FfmpegDiagnosticAggregator();
        for (int i = 0; i < 140; i++)
            aggregator.Add(new FfmpegDiagnosticEvent(i, DateTimeOffset.UtcNow, FfmpegDiagnosticComponent.Ffmpeg,
                $"raw {i}", $"fingerprint-{i}", $"family-{i}", FfmpegDiagnosticCategory.Unknown,
                FfmpegDiagnosticSeverity.Warning, null, new Dictionary<string, long>()));

        FfmpegDiagnosticSummary summary = aggregator.CreateSummary();
        Assert.Equal(140, summary.TotalEvents);
        Assert.Equal(128, summary.Families.Count);
        Assert.Equal(12, summary.DroppedNewFamilies);
    }

    [Fact]
    public void BannerMetadataDoesNotBecomeUnknownFailureEvidence()
    {
        var collector = new FfmpegDiagnosticCollector();
        collector.Observe("ffmpeg version N-12345 built with gcc", FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe("built with gcc 14", FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe("libavutil      59. 39.100 / 59. 39.100", FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe("a genuinely unfamiliar decoder warning", FfmpegDiagnosticComponent.Ffmpeg);

        FfmpegDiagnosticSummary summary = collector.Complete();
        Assert.Equal(1, summary.TotalEvents);
        Assert.Equal("Unknown", Assert.Single(summary.Families).Family);
    }

    [Fact]
    public void AffirmativeCudaDeviceFailureRemainsHardwareClassification()
    {
        var collector = new FfmpegDiagnosticCollector();
        collector.Observe("Error splitting the input into NAL units", FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe("[h264 @ 000001d6df9649c0] missing picture in access unit with size 477", FfmpegDiagnosticComponent.Ffmpeg);
        collector.Observe("[hevc_nvenc @ 000001d6df9649c0] CUDA_ERROR_NO_DEVICE", FfmpegDiagnosticComponent.Ffmpeg);

        Assert.Equal(FfmpegDiagnosticCategory.HardwareAcceleration, collector.Complete().Classification.PrimaryCategory);
    }

    [Fact]
    public void NormalOutputStatisticsDoNotBecomeSubtitleFailure()
    {
        var collector = new FfmpegDiagnosticCollector();
        collector.Observe("[out#0/mp4 @ 12345678] video:70782KiB audio:5511KiB subtitle:0KiB other streams:0KiB global headers:0KiB muxing overhead: 1.2%", FfmpegDiagnosticComponent.Ffmpeg);

        FfmpegDiagnosticSummary summary = collector.Complete();
        Assert.DoesNotContain(summary.Families, family => family.Family == "Subtitle processing failure");
    }

    [Fact]
    public void AffirmativeSubtitleErrorRemainsSubtitleFailure()
    {
        var collector = new FfmpegDiagnosticCollector();
        collector.Observe("Error while converting subtitle stream: subtitle codec is not supported", FfmpegDiagnosticComponent.Ffmpeg);

        FfmpegDiagnosticSummary summary = collector.Complete();
        Assert.Contains(summary.Families, family => family.Family == "Subtitle processing failure");
    }
}
