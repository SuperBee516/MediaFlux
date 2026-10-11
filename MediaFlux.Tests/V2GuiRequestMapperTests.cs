using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.EncodingV2;
using MediaFlux.Services.EncodingV2Gui;
using Xunit;

namespace MediaFlux.Tests;

public sealed class V2GuiRequestMapperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-V2-GUI-map-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _ffmpeg;
    private readonly string _ffprobe;

    public V2GuiRequestMapperTests()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "source.mp4");
        _ffmpeg = Path.Combine(_root, "ffmpeg.exe");
        _ffprobe = Path.Combine(_root, "ffprobe.exe");
        File.WriteAllBytes(_source, [1, 2, 3, 4]);
        File.WriteAllText(_ffmpeg, "fixture");
        File.WriteAllText(_ffprobe, "fixture");
    }

    [Fact]
    public void FixedNvencQualityMapsDirectlyAndStorageSavingsCannotOverrideIt()
    {
        V2GuiEncodeIntent intent = Intent(SettingsWithStorageSavingsQuality(quality: 17));
        V2GuiPreparationResult result = V2GuiRequestMapper.TryCreate(intent, SourceFacts(1280, 720),
            _ffmpeg, _ffprobe, sourceProbeMilliseconds: 12);

        Assert.True(result.Supported, result.Reason);
        V2GuiPreparedEncode prepared = Assert.IsType<V2GuiPreparedEncode>(result.Prepared);
        Assert.Equal("hevc_nvenc", prepared.Request.Encoder);
        Assert.Equal("hevc", prepared.Request.Codec);
        Assert.Equal("cq", prepared.Request.QualityMode);
        Assert.Equal(29, prepared.Request.QualityValue);
        Assert.Equal("p4", prepared.Request.Preset);
        Assert.Equal("copy", prepared.Request.AudioMode);
        Assert.Equal("mp4", prepared.Request.Container);
        Assert.True(prepared.Request.DeleteSourceOnSuccess);
        Assert.Contains("-cq", prepared.PreflightPlan.Arguments);
        Assert.Contains("29", prepared.PreflightPlan.Arguments);
        Assert.DoesNotContain("17", prepared.PreflightPlan.Arguments);
        Assert.DoesNotContain("StoragePolicyRejected", prepared.PreflightPlan.Arguments);
    }

    [Fact]
    public void FixedSoftwareQualityMapsToCrfAndSourceGeometry()
    {
        EncodeExecutionSnapshotSettings settings = Settings() with
        {
            EncoderId = VideoEncoderIds.Libx265,
            CodecFamily = VideoCodecFamily.Hevc,
            VideoFormatText = "H.265 / HEVC (x265)",
            EncoderPreset = "slow",
            NumericQualityValue = 24,
            ScaleMode = EncodingService.ScaleMode.None
        };

        V2GuiPreparationResult result = V2GuiRequestMapper.TryCreate(Intent(settings), SourceFacts(1280, 720),
            _ffmpeg, _ffprobe, 1);

        Assert.True(result.Supported, result.Reason);
        Assert.Equal("crf", result.Prepared!.Request.QualityMode);
        Assert.Equal(24, result.Prepared.Request.QualityValue);
        Assert.Equal("source", result.Prepared.Request.GeometryMode);
        Assert.Equal(1280, result.Prepared.PreflightPlan.Width);
        Assert.Equal(720, result.Prepared.PreflightPlan.Height);
    }

    [Fact]
    public void DeleteSourceOffRemainsOffInTheFrozenV2Plan()
    {
        V2GuiPreparationResult result = V2GuiRequestMapper.TryCreate(
            Intent(Settings() with { DeleteSourceAfterCompression = false }), SourceFacts(1280, 720),
            _ffmpeg, _ffprobe, 1);

        Assert.True(result.Supported, result.Reason);
        Assert.False(result.Prepared!.Request.DeleteSourceOnSuccess);
        Assert.False(result.Prepared.PreflightPlan.DeleteSourceOnSuccess);
    }

    [Fact]
    public void DownscaleRoutesOnlyWhenItMatchesTheExistingGeometryPlan()
    {
        EncodeExecutionSnapshotSettings settings = Settings() with { ScaleMode = EncodingService.ScaleMode.To720p };
        V2GuiPreparationResult downscale = V2GuiRequestMapper.TryCreate(Intent(settings), SourceFacts(1920, 1080),
            _ffmpeg, _ffprobe, 1);
        V2GuiPreparationResult upscale = V2GuiRequestMapper.TryCreate(Intent(settings), SourceFacts(640, 360),
            _ffmpeg, _ffprobe, 1);

        Assert.True(downscale.Supported, downscale.Reason);
        Assert.Equal("fitBox", downscale.Prepared!.Request.GeometryMode);
        Assert.Equal((1280, 720), (downscale.Prepared.PreflightPlan.Width, downscale.Prepared.PreflightPlan.Height));
        Assert.False(upscale.Supported);
        Assert.Contains("geometry", upscale.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourceAdaptiveTargetSizeCustomAndUnsupportedEncoderStayOnV1AsWholeJobs()
    {
        Assert.Contains("Source Adaptive", Decline(Settings() with { AutomaticQuality = true }));
        Assert.Contains("Target-size", Decline(Settings() with { AutoTargetSize = true }));
        Assert.Contains("custom", Decline(Settings(), hasCustomSettings: true), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("supports only", Decline(Settings() with { EncoderId = VideoEncoderIds.Qsv }), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HeadlessSavedJobRouterUsesSharedV2BoundaryAndNeverFallsThroughAfterV2RunFailure()
    {
        V2GuiEncodeIntent fixedIntent = Intent(Settings());
        V2GuiPreparationResult preparedResult = V2GuiRequestMapper.TryCreate(
            fixedIntent, SourceFacts(1280, 720), _ffmpeg, _ffprobe, 1);
        V2GuiPreparedEncode prepared = Assert.IsType<V2GuiPreparedEncode>(preparedResult.Prepared);
        var adapter = new FakeV2Adapter(preparedResult)
        {
            RunException = new InvalidOperationException("synthetic V2 production failure")
        };
        var router = new HeadlessSavedJobV2Router(adapter);

        HeadlessSavedJobV2RouteResult fixedRoute = await router.ExecuteAsync(
            fixedIntent, null, null, CancellationToken.None);

        Assert.Same(prepared, fixedRoute.Prepared);
        Assert.Null(fixedRoute.Run);
        Assert.Equal("synthetic V2 production failure", fixedRoute.ExecutionException!.Message);
        Assert.Equal(1, adapter.PrepareCount);
        Assert.Equal(1, adapter.RunCount);

        V2GuiEncodeIntent adaptiveIntent = Intent(Settings() with { AutomaticQuality = true });
        var adaptiveAdapter = new FakeV2Adapter(V2GuiPreparationResult.Unsupported("unused"));
        HeadlessSavedJobV2RouteResult adaptiveRoute = await new HeadlessSavedJobV2Router(adaptiveAdapter)
            .ExecuteAsync(adaptiveIntent, null, null, CancellationToken.None);

        Assert.Null(adaptiveRoute.Prepared);
        Assert.Contains("Source Adaptive", adaptiveRoute.FallbackReason);
        Assert.Equal(0, adaptiveAdapter.PrepareCount);
        Assert.Equal(0, adaptiveAdapter.RunCount);
    }

    [Fact]
    public void MixedAudioActionsRemainOnV1InsteadOfPartiallyMappingStreams()
    {
        V2GuiPreparationResult result = V2GuiRequestMapper.TryCreate(Intent(Settings()), SourceFacts(1280, 720,
            audioCodecs: ["aac", "flac"]), _ffmpeg, _ffprobe, 1);

        Assert.False(result.Supported);
        Assert.Contains("Mixed", result.Reason);
    }

    [Fact]
    public void UniformAacTranscodeAndSupportedSubtitleCopyMapToV2()
    {
        V2GuiPreparationResult result = V2GuiRequestMapper.TryCreate(Intent(Settings()),
            SourceFacts(1280, 720, audioCodecs: ["flac"], subtitleCodecs: ["mov_text"]),
            _ffmpeg, _ffprobe, 1);

        Assert.True(result.Supported, result.Reason);
        Assert.Equal("aac", result.Prepared!.Request.AudioMode);
        Assert.Equal(192, result.Prepared.Request.AudioBitrateKbps);
        Assert.Equal("copy", result.Prepared.Request.SubtitleMode);
    }

    [Fact]
    public void MatroskaAttachmentPreservationRemainsOnV1()
    {
        V2GuiPreparationResult result = V2GuiRequestMapper.TryCreate(Intent(Settings() with
        {
            OutputContainer = OutputContainerSelection.Matroska
        }), SourceFacts(1280, 720, attachments: true), _ffmpeg, _ffprobe, 1);

        Assert.False(result.Supported);
        Assert.Contains("attachment", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DataStreamsDeclineTheWholeJobToV1()
    {
        V2GuiPreparationResult result = V2GuiRequestMapper.TryCreate(Intent(Settings()),
            SourceFacts(1280, 720, dataStream: true), _ffmpeg, _ffprobe, 1);

        Assert.False(result.Supported);
        Assert.Contains("Data streams", result.Reason);
    }

    [Fact]
    public void AlternateVideoAndCoverArtStreamsDeclineTheWholeJobToV1()
    {
        V2GuiPreparationResult alternate = V2GuiRequestMapper.TryCreate(Intent(Settings()),
            SourceFacts(1280, 720, extraVideoStreams: 1), _ffmpeg, _ffprobe, 1);
        V2GuiPreparationResult coverArt = V2GuiRequestMapper.TryCreate(Intent(Settings()),
            SourceFacts(1280, 720, attachedPicture: true), _ffmpeg, _ffprobe, 1);

        Assert.False(alternate.Supported);
        Assert.False(coverArt.Supported);
        Assert.Contains("one ordinary video", alternate.Reason);
        Assert.Contains("cover-art", coverArt.Reason);
    }

    private string Decline(EncodeExecutionSnapshotSettings settings, bool hasCustomSettings = false)
    {
        var intent = Intent(settings) with { HasCustomSettings = hasCustomSettings };
        return V2GuiRequestMapper.GetIntentUnsupportedReason(intent) ?? "supported";
    }

    private V2GuiEncodeIntent Intent(EncodeExecutionSnapshotSettings settings) => new()
    {
        SourcePath = _source,
        JobId = Guid.NewGuid().ToString("N"),
        Settings = settings
    };

    private EncodeExecutionSnapshotSettings Settings() => new()
    {
        OutputFolder = _root,
        CompressionProfile = "Medium Quality (Default)",
        EncoderId = VideoEncoderIds.Nvenc,
        CodecFamily = VideoCodecFamily.Hevc,
        VideoFormatText = "H.265 / HEVC (x265)",
        EncoderPreset = "p4",
        NumericQualityValue = 29,
        AutomaticQuality = false,
        QualityTarget = QualityTarget.Balanced,
        TenBit = false,
        AudioChannelSelection = "Keep source layout",
        LimitGpuEncodingQueueToOneJob = true,
        IncludeConcurrentEncoderSessions = false,
        AutoTargetSize = false,
        TargetSizeText = "",
        ScaleMode = EncodingService.ScaleMode.None,
        EnableOutputSuffix = false,
        EnableCodecSuffix = false,
        OutputSuffix = "",
        Restoration = new VideoRestorationSettings(),
        StorageSavings = new StorageSavingsOptions(),
        AdaptivePreAttemptResearchCapture = new(),
        OutputContainer = OutputContainerSelection.Mp4,
        CompatibilityPolicy = ContainerCompatibilityPolicy.Intelligent,
        DeleteSourceAfterCompression = true
    };

    private EncodeExecutionSnapshotSettings SettingsWithStorageSavingsQuality(int quality) => Settings() with
    {
        StorageSavings = new StorageSavingsOptions
        {
            Enabled = true,
            TargetMode = StorageSavingsOptions.QualityTarget,
            QualityValue = quality
        }
    };

    private V2MediaFacts SourceFacts(int width, int height, IReadOnlyList<string>? audioCodecs = null,
        IReadOnlyList<string>? subtitleCodecs = null, bool attachments = false, bool dataStream = false,
        int extraVideoStreams = 0, bool attachedPicture = false)
    {
        MediaProbeStreamInfo[] streams = [new()
        {
            Index = 0, CodecType = "video", CodecName = "h264", Width = width, Height = height,
            PixelFormat = "yuv420p", AverageFrameRate = 30,
            Dispositions = attachedPicture ? new Dictionary<string, bool> { ["attached_pic"] = true } : new Dictionary<string, bool>()
        }];
        streams = streams.Concat(Enumerable.Range(0, extraVideoStreams).Select(index => new MediaProbeStreamInfo
        {
            Index = streams.Length + index, CodecType = "video", CodecName = "h264", Width = width, Height = height,
            PixelFormat = "yuv420p", AverageFrameRate = 30
        })).ToArray();
        streams = streams.Concat((audioCodecs ?? ["aac"]).Select((codec, index) => new MediaProbeStreamInfo
        {
            Index = index + 1, CodecType = "audio", CodecName = codec, Channels = 2, ChannelLayout = "stereo"
        })).ToArray();
        streams = streams.Concat((subtitleCodecs ?? []).Select((codec, index) => new MediaProbeStreamInfo
        {
            Index = streams.Length + index, CodecType = "subtitle", CodecName = codec
        })).ToArray();
        if (attachments)
            streams = streams.Append(new MediaProbeStreamInfo { Index = streams.Length, CodecType = "attachment", CodecName = "ttf" }).ToArray();
        if (dataStream)
            streams = streams.Append(new MediaProbeStreamInfo { Index = streams.Length, CodecType = "data", CodecName = "bin_data" }).ToArray();
        var file = new FileInfo(_source);
        return new V2MediaFacts
        {
            SourcePath = Path.GetFullPath(_source),
            Probe = new MediaProbeResult
            {
                Success = true,
                FormatName = "mov,mp4,m4a,3gp,3g2,mj2",
                DurationSeconds = 10,
                Streams = streams
            },
            SourceLength = file.Length,
            SourceLastWriteUtcTicks = file.LastWriteTimeUtc.Ticks,
            PrimaryVideoCodec = "h264",
            SourceWidth = width,
            SourceHeight = height,
            AudioCodecs = audioCodecs ?? ["aac"],
            SubtitleCodecs = subtitleCodecs ?? []
        };
    }

    private sealed class FakeV2Adapter(V2GuiPreparationResult preparation) : IV2GuiProductionAdapter
    {
        public int PrepareCount { get; private set; }
        public int RunCount { get; private set; }
        public Exception? RunException { get; init; }

        public Task<V2GuiPreparationResult> PrepareAsync(V2GuiEncodeIntent intent, CancellationToken cancellationToken)
        {
            PrepareCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(preparation);
        }

        public Task<V2GuiQueueItemResult> RunAsync(
            V2GuiPreparedEncode prepared,
            Action<V2EncodingQueueEvent>? eventObserver,
            CancellationToken cancellationToken)
        {
            RunCount++;
            if (RunException is not null) throw RunException;
            throw new NotSupportedException("This test only exercises failure containment.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
