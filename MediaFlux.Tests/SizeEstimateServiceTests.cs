using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class SizeEstimateServiceTests
{
    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(false, 0, true)]
    [InlineData(false, 750, false)]
    public void ProfileEstimateMode_UsesProfileUnlessManualTargetIsPresent(
        bool autoRequested,
        double manualTargetMb,
        bool expected)
    {
        Assert.Equal(
            expected,
            SizeEstimateService.ShouldUseProfileEstimate(autoRequested, manualTargetMb));
    }

    [Fact]
    public void AutoEstimate_UsesEachFilesMetadataIndependently()
    {
        double detailed1080p = Estimate(
            srcMb: 1_000, durationSec: 3_600, width: 1920, height: 1080,
            fps: 23.976, bitrateKbps: 8_000, sourceCodec: "h264");
        double lowComplexity720p = Estimate(
            srcMb: 1_000, durationSec: 3_600, width: 1280, height: 720,
            fps: 29.97, bitrateKbps: 2_000, sourceCodec: "hevc");

        Assert.NotEqual(detailed1080p, lowComplexity720p);
        Assert.NotEqual(
            Math.Round(100 * (1 - detailed1080p / 1_000)),
            Math.Round(100 * (1 - lowComplexity720p / 1_000)));
    }

    [Fact]
    public void AutoEstimate_ChangesWithCurrentOutputSettings()
    {
        double h264High = Estimate(targetCodec: "libx264", profile: "High Quality");
        double hevcMedium = Estimate(targetCodec: "libx265", profile: "Medium Quality (Default)");
        double av1Low = Estimate(targetCodec: "libaom-av1", profile: "Low Quality (Smaller File)");

        Assert.True(h264High > hevcMedium);
        Assert.True(hevcMedium > av1Low);
    }

    [Fact]
    public void AutoEstimate_ChangesWhenOutputIsScaled()
    {
        double original = Estimate(width: 3840, height: 2160, targetHeight: null);
        double downscaled = Estimate(width: 3840, height: 2160, targetHeight: 1080);

        Assert.True(downscaled < original);
    }

    [Fact]
    public void AutoEstimate_ReturnsUnavailableWhenEssentialMetadataIsMissing()
    {
        double estimate = Estimate(durationSec: 0);

        Assert.Equal(0, estimate);
    }

    [Fact]
    public void NoCompression_DoesNotInventSavings()
    {
        double estimate = Estimate(srcMb: 750, profile: "No Compression");

        Assert.Equal(750, estimate);
    }

    [Fact]
    public void AutoEstimateReportsNoSavingsInsteadOfForcingReduction()
    {
        const double durationSeconds = 3_600;
        const int videoKbps = 1_500;
        const int audioKbps = 192;
        double sourceMb =
            (videoKbps + audioKbps) * durationSeconds / 8192d;

        SizeEstimateBreakdown estimate =
            SizeEstimateService.EstimateAutoTargetMbSmartDetailed(
                sourceMb,
                durationSeconds,
                width: 1920,
                height: 1080,
                fps: 24,
                sourceVideoBitrateKbps: videoKbps,
                sourceCodec: "av1",
                compressionProfile: "Medium Quality (Default)",
                targetCodec: "hevc_nvenc",
                quality: 22,
                targetHeight: null,
                sourceAudioBitrateKbps: audioKbps,
                sourceAudioStreamCount: 1);

        Assert.True(estimate.EstimatedOutputMb > sourceMb);
        Assert.Contains(
            $"output={estimate.EstimatedOutputMb:0.##} MB",
            estimate.Diagnostic);
    }

    [Fact]
    public void AutoEstimatePreservesMeasuredAudioInsteadOfCappingIt()
    {
        double ordinaryAudio = SizeEstimateService.EstimateAutoTargetMbSmart(
            srcMb: 1_200,
            durationSec: 3_600,
            width: 1920,
            height: 1080,
            fps: 30,
            sourceVideoBitrateKbps: 2_600,
            sourceCodec: "h264",
            compressionProfile: "Medium Quality (Default)",
            targetCodec: "libx265",
            quality: 22,
            targetHeight: null,
            sourceAudioBitrateKbps: 192,
            sourceAudioStreamCount: 1);
        double audioHeavy = SizeEstimateService.EstimateAutoTargetMbSmart(
            srcMb: 1_200,
            durationSec: 3_600,
            width: 1920,
            height: 1080,
            fps: 30,
            sourceVideoBitrateKbps: 2_600,
            sourceCodec: "h264",
            compressionProfile: "Medium Quality (Default)",
            targetCodec: "libx265",
            quality: 22,
            targetHeight: null,
            sourceAudioBitrateKbps: 1_200,
            sourceAudioStreamCount: 3);

        Assert.True(audioHeavy > ordinaryAudio);
    }

    [Fact]
    public void AudioConversionBudgetsEveryMappedAudioStream()
    {
        double oneStream = SizeEstimateService.EstimateAutoTargetMbSmart(
            srcMb: 1_200,
            durationSec: 3_600,
            width: 1920,
            height: 1080,
            fps: 30,
            sourceVideoBitrateKbps: 2_600,
            sourceCodec: "h264",
            compressionProfile: "Medium Quality (Default)",
            targetCodec: "libx265",
            quality: 22,
            targetHeight: null,
            sourceAudioBitrateKbps: 1_200,
            sourceAudioStreamCount: 1,
            targetAudioChannels: 2);
        double threeStreams = SizeEstimateService.EstimateAutoTargetMbSmart(
            srcMb: 1_200,
            durationSec: 3_600,
            width: 1920,
            height: 1080,
            fps: 30,
            sourceVideoBitrateKbps: 2_600,
            sourceCodec: "h264",
            compressionProfile: "Medium Quality (Default)",
            targetCodec: "libx265",
            quality: 22,
            targetHeight: null,
            sourceAudioBitrateKbps: 1_200,
            sourceAudioStreamCount: 3,
            targetAudioChannels: 2);

        Assert.True(threeStreams > oneStream);
    }

    [Fact]
    public void H264ToHevcRepresentativeSourceProjectsHistoricalSavingsRange()
    {
        const double durationSeconds = 3_600;
        const int videoKbps = 5_000;
        const int audioKbps = 192;
        double sourceMb =
            (videoKbps + audioKbps) * durationSeconds / 8192d;

        SizeEstimateBreakdown estimate =
            SizeEstimateService.EstimateAutoTargetMbSmartDetailed(
                sourceMb,
                durationSeconds,
                width: 1920,
                height: 1080,
                fps: 30,
                sourceVideoBitrateKbps: videoKbps,
                sourceCodec: "h264",
                compressionProfile: "Medium Quality (Default)",
                targetCodec: "hevc_nvenc",
                quality: 22,
                targetHeight: null,
                sourceAudioBitrateKbps: audioKbps,
                sourceAudioStreamCount: 1,
                sourceTotalBitrateKbps: videoKbps + audioKbps);

        double savingsPercent =
            (sourceMb - estimate.EstimatedOutputMb) / sourceMb * 100d;
        Assert.InRange(savingsPercent, 40, 60);
        Assert.Equal(videoKbps, estimate.SourceVideoBitrateKbps, precision: 0);
    }

    [Fact]
    public void MissingVideoStreamBitrateDoesNotDoubleCountMeasuredAudio()
    {
        const double durationSeconds = 3_600;
        const int derivedVideoKbps = 3_000;
        const int audioKbps = 900;
        const int containerKbps = 40;
        const int totalKbps = derivedVideoKbps + audioKbps + containerKbps;
        double sourceMb = totalKbps * durationSeconds / 8192d;

        SizeEstimateBreakdown missingVideoBitrate =
            SizeEstimateService.EstimateAutoTargetMbSmartDetailed(
                sourceMb,
                durationSeconds,
                width: 1920,
                height: 1080,
                fps: 24,
                sourceVideoBitrateKbps: 0,
                sourceCodec: "h264",
                compressionProfile: "Medium Quality (Default)",
                targetCodec: "hevc_nvenc",
                quality: 22,
                targetHeight: null,
                sourceAudioBitrateKbps: audioKbps,
                sourceAudioStreamCount: 2,
                sourceTotalBitrateKbps: totalKbps);

        Assert.InRange(
            missingVideoBitrate.SourceVideoBitrateKbps,
            2_950,
            3_050);
        Assert.False(missingVideoBitrate.UsedMeasuredVideoBitrate);
        Assert.Contains(
            "derived total minus mapped streams",
            missingVideoBitrate.Diagnostic);
    }

    [Fact]
    public void AudioHeavySourceRetainsCopiedAudioFloor()
    {
        const double durationSeconds = 3_600;
        const int videoKbps = 2_600;
        const int audioKbps = 1_200;
        double sourceMb =
            (videoKbps + audioKbps) * durationSeconds / 8192d;

        SizeEstimateBreakdown estimate =
            SizeEstimateService.EstimateAutoTargetMbSmartDetailed(
                sourceMb,
                durationSeconds,
                width: 1920,
                height: 1080,
                fps: 30,
                sourceVideoBitrateKbps: videoKbps,
                sourceCodec: "h264",
                compressionProfile: "Medium Quality (Default)",
                targetCodec: "hevc_nvenc",
                quality: 22,
                targetHeight: null,
                sourceAudioBitrateKbps: audioKbps,
                sourceAudioStreamCount: 3);

        Assert.Equal(audioKbps, estimate.PlannedAudioBitrateKbps, precision: 0);
        Assert.True(estimate.TargetTotalBitrateKbps >
                    estimate.TargetVideoBitrateKbps + 1_100);
    }

    [Fact]
    public void StreamBudgetCopiesSubtitlesButExcludesDataAndAttachments()
    {
        const double durationSeconds = 3_600;
        const int totalKbps = 4_000;
        double sourceMb = totalKbps * durationSeconds / 8192d;
        long attachmentBytes =
            (long)Math.Round(100d * 1000d * durationSeconds / 8d);

        SizeEstimateBreakdown estimate =
            SizeEstimateService.EstimateAutoTargetMbSmartDetailed(
                sourceMb,
                durationSeconds,
                width: 1920,
                height: 1080,
                fps: 24,
                sourceVideoBitrateKbps: 0,
                sourceCodec: "h264",
                compressionProfile: "Medium Quality (Default)",
                targetCodec: "hevc_nvenc",
                quality: 22,
                targetHeight: null,
                sourceAudioBitrateKbps: 200,
                sourceAudioStreamCount: 1,
                sourceTotalBitrateKbps: totalKbps,
                sourceSubtitleBitrateKbps: 20,
                sourceSubtitleStreamCount: 2,
                sourceDataBitrateKbps: 100,
                sourceDataStreamCount: 1,
                sourceAttachmentStreamCount: 1,
                sourceAttachmentSizeBytes: attachmentBytes);

        Assert.InRange(estimate.SourceVideoBitrateKbps, 3_500, 3_580);
        Assert.Equal(
            20,
            estimate.PlannedMappedAncillaryBitrateKbps,
            precision: 0);
        Assert.Contains("data excluded=1/100 kbps", estimate.Diagnostic);
        Assert.Contains("attachments excluded=1/", estimate.Diagnostic);
    }

    [Fact]
    public void StorageBitrateModeTargetsConfiguredShareOfSourceVideo()
    {
        var storage = new MediaFlux.Models.StorageSavingsOptions
        {
            Enabled = true,
            TargetMode =
                MediaFlux.Models.StorageSavingsOptions.SourceBitrateTarget,
            SourceVideoBitratePercent = 45
        };

        SizeEstimateBreakdown estimate =
            SizeEstimateService.EstimateAutoTargetMbSmartDetailed(
                srcMb: 2_300,
                durationSec: 3_600,
                width: 1920,
                height: 1080,
                fps: 30,
                sourceVideoBitrateKbps: 5_000,
                sourceCodec: "h264",
                compressionProfile: "Medium Quality (Default)",
                targetCodec: "hevc_nvenc",
                quality: 22,
                targetHeight: null,
                sourceAudioBitrateKbps: 192,
                sourceAudioStreamCount: 1,
                storageSavings: storage);

        Assert.Equal(2_250, estimate.TargetVideoBitrateKbps, precision: 0);
        Assert.Contains("storage bitrate target 45%", estimate.Diagnostic);
    }

    [Fact]
    public void StorageQualityModeUsesConfiguredCqProjection()
    {
        var storage = new MediaFlux.Models.StorageSavingsOptions
        {
            Enabled = true,
            TargetMode = MediaFlux.Models.StorageSavingsOptions.QualityTarget,
            QualityValue = 30
        };

        SizeEstimateBreakdown estimate =
            SizeEstimateService.EstimateAutoTargetMbSmartDetailed(
                srcMb: 2_300,
                durationSec: 3_600,
                width: 1920,
                height: 1080,
                fps: 30,
                sourceVideoBitrateKbps: 5_000,
                sourceCodec: "h264",
                compressionProfile: "Medium Quality (Default)",
                targetCodec: "libx265",
                quality: 22,
                targetHeight: null,
                sourceAudioBitrateKbps: 192,
                sourceAudioStreamCount: 1,
                storageSavings: storage);

        Assert.True(estimate.UsesStorageQualityTarget);
        Assert.Contains(
            "quality target 30 (CQ/CRF/ICQ)",
            estimate.Diagnostic);
    }

    private static double Estimate(
        double srcMb = 1_200,
        double durationSec = 3_600,
        int width = 1920,
        int height = 1080,
        double fps = 30,
        int bitrateKbps = 2_600,
        string sourceCodec = "h264",
        string profile = "Medium Quality (Default)",
        string targetCodec = "libx265",
        int quality = 22,
        int? targetHeight = null)
    {
        return SizeEstimateService.EstimateAutoTargetMbSmart(
            srcMb,
            durationSec,
            width,
            height,
            fps,
            bitrateKbps,
            sourceCodec,
            profile,
            targetCodec,
            quality,
            targetHeight);
    }
}
