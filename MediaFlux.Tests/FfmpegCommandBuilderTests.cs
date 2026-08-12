using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;
using Xunit;

namespace MediaFlux.Tests;

public sealed class FfmpegCommandBuilderTests
{
    [Fact]
    public void NvencHevcQualityCommandMatchesExistingBehavior()
    {
        FfmpegCommandRequest request = CreateRequest(
            "hevc_nvenc",
            useGpu: true,
            preset: "p6");

        string arguments = CreateBuilder().Build(request);

        Assert.Equal(
            "-y -hwaccel cuda -hwaccel_output_format cuda " +
            "-i \"C:\\Media\\source.mkv\" " +
            "-map 0:v:0 -map 0:a? -map 0:s? -map 0:d? " +
            "-map_metadata 0 -map_chapters 0 " +
            "-c:s copy -c:v hevc_nvenc -rc vbr -cq 24 -preset p6 " +
            "-tune hq -rc-lookahead 32 -spatial_aq 1 -temporal_aq 1 " +
            "-aq-strength 12 -surfaces 48 -multipass fullres " +
            "-bf 4 -b_ref_mode middle -refs 4 -c:a copy " +
            "-movflags +faststart \"C:\\Output\\source.mp4\"",
            arguments);
    }

    [Fact]
    public void ConcurrentNvencCommandUsesExistingReducedTuning()
    {
        FfmpegCommandRequest request = CreateRequest(
            "h264_nvenc",
            useGpu: true,
            preset: "p5",
            concurrentEncoderSessions: true);

        string arguments = CreateBuilder().Build(request);

        Assert.Contains(
            "-rc vbr -cq 22 -preset p5 -tune hq " +
            "-rc-lookahead 12 -spatial_aq 1 -temporal_aq 1 " +
            "-aq-strength 8 -surfaces 24 " +
            "-bf 3 -b_ref_mode middle -refs 3 ",
            arguments);
        Assert.DoesNotContain("-multipass fullres", arguments);
    }

    [Fact]
    public void NvencHevcTenBitUsesGpuResidentHighBitDepthWhenSupported()
    {
        FfmpegCommandRequest request = CreateRequest(
            "hevc_nvenc",
            useGpu: true,
            tenBit: true,
            nvencHighBitDepthOutputSupported: true);

        string arguments = CreateBuilder().Build(request);

        Assert.Contains(
            "-hwaccel cuda -hwaccel_output_format cuda ",
            arguments);
        Assert.Contains(
            "-c:v hevc_nvenc -profile:v main10 -highbitdepth 1 ",
            arguments);
        Assert.DoesNotContain("format=p010le", arguments);
        Assert.DoesNotContain("-pix_fmt p010le", arguments);
    }

    [Fact]
    public void NvencHevcTenBitScalingStaysOnCudaWhenSupported()
    {
        FfmpegCommandRequest request = CreateRequest(
            "hevc_nvenc",
            useGpu: true,
            tenBit: true,
            nvencHighBitDepthOutputSupported: true,
            scaleMode: EncodingService.ScaleMode.To1080p);

        string arguments = CreateBuilder().Build(request);

        Assert.Contains(
            "-vf scale_cuda=-2:1080:interp_algo=lanczos ",
            arguments);
        Assert.Contains("-highbitdepth 1 ", arguments);
        Assert.DoesNotContain("scale=-2:1080:flags=lanczos", arguments);
    }

    [Fact]
    public void NvencHevcTenBitRetainsCompatibilityPathWhenUnsupported()
    {
        FfmpegCommandRequest request = CreateRequest(
            "hevc_nvenc",
            useGpu: true,
            tenBit: true);

        string arguments = CreateBuilder().Build(request);

        Assert.StartsWith("-y -hwaccel cuda -i ", arguments);
        Assert.Contains("-vf format=p010le ", arguments);
        Assert.Contains(
            "-profile:v main10 -pix_fmt p010le ",
            arguments);
        Assert.DoesNotContain("-highbitdepth", arguments);
    }

    [Fact]
    public void QsvHevcQualityCommandMatchesExistingBehavior()
    {
        FfmpegCommandRequest request = CreateRequest(
            "hevc_qsv",
            useGpu: true);

        string arguments = CreateBuilder().Build(request);

        Assert.Contains("-y -hwaccel qsv ", arguments);
        Assert.Contains(
            "-c:v hevc_qsv -rc_mode icq -global_quality 19 " +
            "-preset slow -mbbrc 1 ",
            arguments);
        Assert.DoesNotContain("cuda", arguments);
    }

    [Theory]
    [InlineData("libx264", "-c:v libx264 -crf 23 -preset slow ")]
    [InlineData("libx265", "-c:v libx265 -crf 24 -preset slow ")]
    [InlineData("libsvtav1", "-c:v libsvtav1 -crf 30 -preset 6 ")]
    public void SoftwareQualityCommandsMatchExistingBehavior(
        string ffmpegCodec,
        string expectedVideoArguments)
    {
        FfmpegCommandRequest request = CreateRequest(
            ffmpegCodec,
            useGpu: false);

        string arguments = CreateBuilder().Build(request);

        Assert.Contains(expectedVideoArguments, arguments);
        Assert.DoesNotContain("-hwaccel", arguments);
    }

    [Theory]
    [InlineData("libx264")]
    [InlineData("libx265")]
    [InlineData("libsvtav1")]
    public void SoftwareEncodersNeverReceiveCudaArguments(
        string ffmpegCodec)
    {
        FfmpegCommandRequest request = CreateRequest(
            ffmpegCodec,
            useGpu: true);

        string arguments = CreateBuilder().Build(request);

        Assert.DoesNotContain("cuda", arguments);
        Assert.DoesNotContain("_nvenc", arguments);
        Assert.DoesNotContain("-hwaccel", arguments);
    }

    public static TheoryData<string, VideoCodecFamily, string, bool>
        SupportedEncoderMatrix => new()
        {
            {
                VideoEncoderIds.Nvenc,
                VideoCodecFamily.H264,
                "h264_nvenc",
                true
            },
            {
                VideoEncoderIds.Nvenc,
                VideoCodecFamily.Hevc,
                "hevc_nvenc",
                true
            },
            {
                VideoEncoderIds.Nvenc,
                VideoCodecFamily.Av1,
                "av1_nvenc",
                true
            },
            {
                VideoEncoderIds.Qsv,
                VideoCodecFamily.H264,
                "h264_qsv",
                true
            },
            {
                VideoEncoderIds.Qsv,
                VideoCodecFamily.Hevc,
                "hevc_qsv",
                true
            },
            {
                VideoEncoderIds.Qsv,
                VideoCodecFamily.Av1,
                "av1_qsv",
                true
            },
            {
                VideoEncoderIds.Libx264,
                VideoCodecFamily.H264,
                "libx264",
                false
            },
            {
                VideoEncoderIds.Libx265,
                VideoCodecFamily.Hevc,
                "libx265",
                false
            },
            {
                VideoEncoderIds.SvtAv1,
                VideoCodecFamily.Av1,
                "libsvtav1",
                false
            }
        };

    [Theory]
    [MemberData(nameof(SupportedEncoderMatrix))]
    public void EverySupportedEncoderCodecPairBuildsAValidBackendCommand(
        string encoderId,
        VideoCodecFamily codecFamily,
        string expectedCodec,
        bool isHardware)
    {
        ResolvedVideoEncoder resolved =
            EncoderRegistry.Default.Resolve(encoderId, codecFamily);
        FfmpegCommandRequest request = CreateRequest(
            resolved.Selection,
            useGpu: true);

        string arguments = CreateBuilder().Build(request);

        Assert.Contains($"-c:v {expectedCodec} ", arguments);
        Assert.Contains("-map_metadata 0 -map_chapters 0 ", arguments);
        Assert.Equal(
            isHardware,
            arguments.Contains("-hwaccel", StringComparison.Ordinal));

        foreach (string otherCodec in SupportedEncoderMatrix
                     .Select(row => (string)row[2])
                     .Where(codec => !codec.Equals(
                         expectedCodec,
                         StringComparison.Ordinal)))
        {
            Assert.DoesNotContain($"-c:v {otherCodec} ", arguments);
        }
    }

    [Fact]
    public void Libx265TenBitScalingMatchesExistingEnginePath()
    {
        FfmpegCommandRequest request = CreateRequest(
            "libx265",
            useGpu: false,
            tenBit: true,
            scaleMode: EncodingService.ScaleMode.To1080p);

        string arguments = CreateBuilder().Build(request);

        Assert.Contains(
            "-vf scale=-2:1080:flags=lanczos,format=yuv420p10le " +
            "-c:v libx265 -profile:v main10 -pix_fmt yuv420p10le " +
            "-crf 24 -preset slow ",
            arguments);
    }

    [Theory]
    [InlineData("ultrafast")]
    [InlineData("superfast")]
    [InlineData("veryfast")]
    [InlineData("faster")]
    [InlineData("fast")]
    [InlineData("medium")]
    [InlineData("slow")]
    [InlineData("slower")]
    [InlineData("veryslow")]
    [InlineData("placebo")]
    public void Libx265UsesEverySupportedPreset(string preset)
    {
        FfmpegCommandRequest request = CreateRequest(
            "libx265",
            useGpu: false,
            preset: preset,
            qualityValue: 27);

        string arguments = CreateBuilder().Build(request);

        Assert.Contains(
            $"-c:v libx265 -crf 27 -preset {preset} ",
            arguments);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(18, 18)]
    [InlineData(80, 51)]
    public void Libx265CrfIsClampedBeforeCommandGeneration(
        int requestedCrf,
        int expectedCrf)
    {
        FfmpegCommandRequest request = CreateRequest(
            "libx265",
            useGpu: false,
            preset: "medium",
            qualityValue: requestedCrf);

        string arguments = CreateBuilder().Build(request);

        Assert.Contains(
            $"-c:v libx265 -crf {expectedCrf} -preset medium ",
            arguments);
    }

    [Fact]
    public void Libx265TargetSizeUsesBoundedBitrateAndSelectedPreset()
    {
        FfmpegCommandRequest request = CreateRequest(
            "libx265",
            useGpu: false,
            preset: "veryslow",
            targetMb: 100,
            knownDuration: TimeSpan.FromSeconds(100),
            knownAudioBitrateKbps: 160);

        string arguments = CreateBuilder().Build(request);

        Assert.Contains(
            "-c:v libx265 -b:v 7950k -maxrate 8586k " +
            "-bufsize 11130k -preset veryslow ",
            arguments);
        Assert.DoesNotContain("-crf", arguments);
    }

    [Fact]
    public void Libx265RetainsSharedAudioAndSubtitleHandling()
    {
        FfmpegCommandRequest request = CreateRequest(
            "libx265",
            useGpu: false,
            preset: "fast",
            qualityValue: 25,
            audioChannels: 6,
            copySubtitles: false,
            copyDataStreams: false);

        string arguments = CreateBuilder().Build(request);

        Assert.Contains("-map 0:v:0 -map 0:a? -dn ", arguments);
        Assert.Contains("-map_metadata 0 -map_chapters 0 -sn ", arguments);
        Assert.Contains("-c:v libx265 -crf 25 -preset fast ", arguments);
        Assert.Contains("-c:a aac -b:a 192k -ac 6 ", arguments);
        Assert.DoesNotContain("-c:s copy", arguments);
    }

    [Fact]
    public void Libx265RetainsSharedAudioCopyMetadataAndStreamMapping()
    {
        FfmpegCommandRequest request = CreateRequest(
            "libx265",
            useGpu: false,
            preset: "medium");

        string arguments = CreateBuilder().Build(request);

        Assert.Contains(
            "-map 0:v:0 -map 0:a? -map 0:s? -map 0:d? ",
            arguments);
        Assert.Contains("-map_metadata 0 -map_chapters 0 ", arguments);
        Assert.Contains("-c:s copy ", arguments);
        Assert.Contains("-c:a copy ", arguments);
    }

    [Fact]
    public void UnknownDurationRetainsQualityFallback()
    {
        var log = new List<string>();
        FfmpegCommandRequest request = CreateRequest(
            "libx265",
            useGpu: false,
            targetMb: 100,
            knownDuration: TimeSpan.Zero);
        var builder = new FfmpegCommandBuilder(
            EncoderRegistry.Default,
            _ => 160,
            log.Add);

        string arguments = builder.Build(request);

        Assert.Contains("-c:v libx265 -crf 24 -preset slow ", arguments);
        Assert.Contains(
            log,
            message => message.Contains(
                "using quality-based encoding instead",
                StringComparison.Ordinal));
    }

    [Fact]
    public void TargetSizeBudgetsConvertedAudioForEveryMappedStream()
    {
        var log = new List<string>();
        FfmpegCommandRequest request = CreateRequest(
            "libx265",
            useGpu: false,
            targetMb: 100,
            knownDuration: TimeSpan.FromSeconds(100),
            audioChannels: 2,
            knownAudioStreamCount: 3);
        var builder = new FfmpegCommandBuilder(
            EncoderRegistry.Default,
            _ => 160,
            log.Add);

        _ = builder.Build(request);

        Assert.Contains(
            log,
            message => message.Contains("audio=576 kbps", StringComparison.Ordinal));
    }

    [Fact]
    public void TargetSizeBudgetsMappedSubtitleAndDataStreams()
    {
        var log = new List<string>();
        FfmpegCommandRequest request = CreateRequest(
            "hevc_nvenc",
            useGpu: true,
            targetMb: 100,
            knownDuration: TimeSpan.FromSeconds(100),
            knownAudioBitrateKbps: 160,
            knownMappedAncillaryBitrateKbps: 40);
        var builder = new FfmpegCommandBuilder(
            EncoderRegistry.Default,
            _ => 160,
            log.Add);

        string arguments = builder.Build(request);

        Assert.Contains("-b:v 7910k", arguments);
        Assert.Contains(
            log,
            message => message.Contains(
                "subtitles/data=40 kbps",
                StringComparison.Ordinal));
    }

    [Fact]
    public void EstimatorTargetTotalProducesSameFfmpegVideoBitrate()
    {
        const double durationSeconds = 3_600;
        const double targetVideoKbps = 2_500;
        const double audioKbps = 192;
        const double subtitleKbps = 20;
        double targetTotalKbps =
            SizeEstimateService.CalculateTargetTotalBitrateKbps(
                targetVideoKbps,
                audioKbps + subtitleKbps);
        double targetMb =
            targetTotalKbps * durationSeconds / 8192d;

        FfmpegCommandRequest request = CreateRequest(
            "hevc_nvenc",
            useGpu: true,
            targetMb: targetMb,
            knownDuration: TimeSpan.FromSeconds(durationSeconds),
            knownAudioBitrateKbps: audioKbps,
            knownMappedAncillaryBitrateKbps: subtitleKbps);

        string arguments = CreateBuilder().Build(request);

        Assert.Contains("-b:v 2500k", arguments);
    }

    private static FfmpegCommandBuilder CreateBuilder() =>
        new(EncoderRegistry.Default, _ => 160);

    private static FfmpegCommandRequest CreateRequest(
        string ffmpegCodec,
        bool useGpu,
        string? preset = null,
        int? qualityValue = null,
        bool tenBit = false,
        bool concurrentEncoderSessions = false,
        double? targetMb = null,
        TimeSpan? knownDuration = null,
        double? knownAudioBitrateKbps = null,
        int? audioChannels = null,
        bool copySubtitles = true,
        bool copyDataStreams = true,
        int knownAudioStreamCount = 0,
        double knownMappedAncillaryBitrateKbps = 0,
        EncodingService.ScaleMode scaleMode =
            EncodingService.ScaleMode.None,
        bool nvencHighBitDepthOutputSupported = false)
    {
        ResolvedVideoEncoder encoder =
            EncoderRegistry.Default.ResolveLegacyCodec(ffmpegCodec);

        return CreateRequest(
            encoder.Selection,
            useGpu,
            preset,
            qualityValue,
            tenBit,
            concurrentEncoderSessions,
            targetMb,
            knownDuration,
            knownAudioBitrateKbps,
            audioChannels,
            copySubtitles,
            copyDataStreams,
            knownAudioStreamCount,
            knownMappedAncillaryBitrateKbps,
            scaleMode,
            nvencHighBitDepthOutputSupported);
    }

    private static FfmpegCommandRequest CreateRequest(
        VideoEncoderSelection selection,
        bool useGpu,
        string? preset = null,
        int? qualityValue = null,
        bool tenBit = false,
        bool concurrentEncoderSessions = false,
        double? targetMb = null,
        TimeSpan? knownDuration = null,
        double? knownAudioBitrateKbps = null,
        int? audioChannels = null,
        bool copySubtitles = true,
        bool copyDataStreams = true,
        int knownAudioStreamCount = 0,
        double knownMappedAncillaryBitrateKbps = 0,
        EncodingService.ScaleMode scaleMode =
            EncodingService.ScaleMode.None,
        bool nvencHighBitDepthOutputSupported = false)
    {

        return new FfmpegCommandRequest
        {
            Input = new EncodingInputSource
            {
                Kind = EncodingInputKind.File,
                InputPath = "C:\\Media\\source.mkv",
                SourcePath = "C:\\Media\\source.mkv",
                OutputBaseName = "source",
                KnownAudioBitrateKbps = knownAudioBitrateKbps,
                KnownAudioStreamCount = knownAudioStreamCount,
                KnownMappedAncillaryBitrateKbps =
                    knownMappedAncillaryBitrateKbps
            },
            OutputPath = "C:\\Output\\source.mp4",
            Encoder = selection,
            UseGpu = useGpu,
            TargetMb = targetMb,
            ScaleMode = scaleMode,
            EncoderPreset = preset,
            QualityValue = qualityValue,
            TenBit = tenBit,
            AudioChannels = audioChannels,
            ConcurrentEncoderSessions = concurrentEncoderSessions,
            MapMode = EncodingService.StreamMapMode.KeepAll,
            CopySubtitles = copySubtitles,
            CopyDataStreams = copyDataStreams,
            ForceMp4CompatibleAudio = false,
            KnownDuration = knownDuration ?? TimeSpan.FromMinutes(10),
            NvencHighBitDepthOutputSupported =
                nvencHighBitDepthOutputSupported
        };
    }
}
