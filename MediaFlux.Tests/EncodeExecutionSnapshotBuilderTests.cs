using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodeExecutionSnapshotBuilderTests
{
    private static readonly DateTime StartedUtc = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OrdinaryAutomaticBalancedBuildsSourceAdaptiveIntentWithoutTargetSize()
    {
        EncodeExecutionSnapshot snapshot = Build(
            Settings() with
            {
                AutomaticQuality = true,
                QualityTarget = QualityTarget.Balanced,
                AutoTargetSize = true,
                TargetSizeText = "480"
            });

        Assert.Null(snapshot.PredictionShadowExperimentAssignment);
        Assert.Equal(EncodingQualityIntent.Automatic(QualityTarget.Balanced), snapshot.QualityIntent);
        Assert.Null(snapshot.TargetMb);
        Assert.Equal(VideoEncoderIds.Nvenc, snapshot.Encoder.EncoderId);
        Assert.Equal(VideoCodecFamily.Hevc, snapshot.Encoder.CodecFamily);
    }

    [Fact]
    public void ManualQualityRetainsNumericCqAndCrfIntent()
    {
        EncodeExecutionSnapshot cq = Build(Settings() with
        {
            AutomaticQuality = false,
            NumericQualityValue = 19,
            AutoTargetSize = true
        });
        EncodeExecutionSnapshot crf = Build(Settings() with
        {
            EncoderId = VideoEncoderIds.Libx264,
            CodecFamily = VideoCodecFamily.H264,
            VideoFormatText = "H.264 (x264)",
            NumericQualityValue = 23,
            AutomaticQuality = false
        });

        Assert.Null(cq.QualityIntent);
        Assert.Equal(19, cq.QualityValue);
        Assert.Equal(100, cq.TargetMb);
        Assert.Null(crf.QualityIntent);
        Assert.Equal(23, crf.QualityValue);
        Assert.Equal("libx264", crf.Codec);
    }

    [Fact]
    public void AutomaticQualityCanUseExplicitManualTargetSize()
    {
        EncodeExecutionSnapshot snapshot = Build(Settings() with
        {
            AutomaticQuality = true,
            QualityTarget = QualityTarget.Balanced,
            AutoTargetSize = false,
            TargetSizeText = "275"
        });

        Assert.Equal(275, snapshot.TargetMb);
        Assert.Equal(EncodingQualityIntent.Automatic(QualityTarget.Balanced), snapshot.QualityIntent);
    }

    [Fact]
    public void NvencHevcP5TenBitAndAutomaticConcurrencyAreCaptured()
    {
        EncodeExecutionSnapshot snapshot = Build(Settings() with
        {
            EncoderId = VideoEncoderIds.Nvenc,
            CodecFamily = VideoCodecFamily.Hevc,
            EncoderPreset = "p5",
            NumericQualityValue = 25,
            TenBit = true,
            LimitGpuEncodingQueueToOneJob = false
        });

        Assert.Equal(VideoEncoderIds.Nvenc, snapshot.Encoder.EncoderId);
        Assert.Equal(VideoCodecFamily.Hevc, snapshot.Encoder.CodecFamily);
        Assert.Equal("p5", snapshot.EncoderPreset);
        Assert.True(snapshot.TenBit);
        Assert.True(snapshot.UseGpu);
        Assert.True(snapshot.ConcurrentEncoderSessions);
    }

    [Theory]
    [InlineData(EncodingService.ScaleMode.None, 1080, 1080)]
    [InlineData(EncodingService.ScaleMode.To720p, 1080, 720)]
    [InlineData(EncodingService.ScaleMode.To720p, 480, 480)]
    [InlineData(EncodingService.ScaleMode.To4K, 1080, 1080)]
    public void ScaleModeAndOutputHeightPreserveExistingCeilingBehavior(
        EncodingService.ScaleMode mode,
        int sourceHeight,
        int expectedOutputHeight)
    {
        EncodeExecutionSnapshot snapshot = Build(
            Settings() with { ScaleMode = mode },
            Item() with { SourceMeasurements = new EncodeExecutionSourceMeasurements { Height = sourceHeight } });

        Assert.Equal(mode, snapshot.ScaleMode);
        Assert.Equal(expectedOutputHeight, snapshot.OutputHeight);
    }

    [Theory]
    [InlineData(VideoRestorationMode.Off, VideoRestorationPreset.Off)]
    [InlineData(VideoRestorationMode.Custom, VideoRestorationPreset.Custom)]
    public void RestorationSettingsAreCopiedForTheSnapshot(
        VideoRestorationMode mode,
        VideoRestorationPreset preset)
    {
        var restoration = new VideoRestorationSettings { Mode = mode, Preset = preset };
        EncodeExecutionSnapshot snapshot = Build(Settings() with { Restoration = restoration });

        Assert.Equal(mode, snapshot.Restoration.Mode);
        Assert.Equal(preset, snapshot.Restoration.Preset);
        Assert.NotSame(restoration, snapshot.Restoration);
    }

    [Fact]
    public void PerItemCustomTargetAndProfileOverrideGlobalSettings()
    {
        EncodeExecutionSnapshot snapshot = Build(
            Settings() with { CompressionProfile = "Medium Quality (Default)" },
            Item() with
            {
                CustomCompressionProfile = "No Compression",
                CustomTargetMb = 333
            });

        Assert.Equal(333, snapshot.TargetMb);
        Assert.Equal(22, snapshot.QualityValue);
    }

    [Fact]
    public void LibraryPolicyItemOverridesTheOrdinaryEncoderScaleAndContainerPolicy()
    {
        var policy = new LibraryPolicyQueueItem(
            "C:\\Media\\clip.mp4",
            "policy-1",
            "Policy",
            VideoCodecFamily.H264,
            VideoEncoderIds.Libx264,
            "slow",
            string.Empty,
            18,
            8,
            PreserveSourceResolution: false,
            MaximumOutputHeight: 720,
            PreserveHdr: false,
            TargetContainer: OutputContainerSelection.Matroska,
            ProjectedOutputBytes: 250L * 1024 * 1024,
            Confidence: LibraryPolicyConfidence.High);
        EncodeExecutionSnapshot snapshot = Build(
            Settings() with { AutomaticQuality = true, TenBit = true, EnableCodecSuffix = true },
            Item() with
            {
                LibraryPolicyIntent = policy,
                SourceMeasurements = new EncodeExecutionSourceMeasurements { Height = 1080 }
            });

        Assert.Equal(VideoEncoderIds.Libx264, snapshot.Encoder.EncoderId);
        Assert.Equal(VideoCodecFamily.H264, snapshot.Encoder.CodecFamily);
        Assert.Equal(18, snapshot.QualityValue);
        Assert.Null(snapshot.QualityIntent);
        Assert.False(snapshot.TenBit);
        Assert.False(snapshot.ConcurrentEncoderSessions);
        Assert.Equal(250, snapshot.TargetMb);
        Assert.Equal(EncodingService.ScaleMode.To720p, snapshot.ScaleMode);
        Assert.Equal(720, snapshot.OutputHeight);
        Assert.Equal(OutputContainerSelection.Matroska, snapshot.OutputContainer);
        Assert.Equal(" [x264]", snapshot.Suffix);

        var preset = new EncodingPreset
        {
            VideoCodec = nameof(VideoCodecFamily.Hevc),
            EncoderId = VideoEncoderIds.Nvenc,
            EncoderPreset = "p5",
            QualityValue = 20,
            TenBit = true,
            AutoTargetSize = false,
            ManualTargetMb = 175,
            OutputContainer = nameof(OutputContainerSelection.Matroska)
        };
        EncodeExecutionSnapshot fromPreset = Build(
            Settings(),
            Item() with { LibraryPolicyIntent = policy, PolicyEncodingPreset = preset });

        Assert.Equal(VideoEncoderIds.Nvenc, fromPreset.Encoder.EncoderId);
        Assert.Equal(VideoCodecFamily.Hevc, fromPreset.Encoder.CodecFamily);
        Assert.Equal("p5", fromPreset.EncoderPreset);
        Assert.Equal(20, fromPreset.QualityValue);
        Assert.True(fromPreset.TenBit);
        Assert.Equal(175, fromPreset.TargetMb);
        Assert.Equal(OutputContainerSelection.Matroska, fromPreset.OutputContainer);
    }

    [Fact]
    public void AssignmentBindingIsCarriedVerbatimWithoutHeuristicRepair()
    {
        var binding = new PredictionShadowExperimentAssignmentBinding(
            new PredictionShadowExperimentAssignment(
                "example-experiment",
                4,
                1,
                PredictionShadowExperimentStratum.Control,
                PredictionShadowExperimentRole.Target),
            "C:\\old-source.mp4",
            123,
            456);

        EncodeExecutionSnapshot snapshot = Build(
            Settings(),
            Item() with { PredictionShadowExperimentAssignment = binding });

        Assert.Same(binding, snapshot.PredictionShadowExperimentAssignment);
        Assert.Equal(binding, snapshot.PredictionShadowExperimentAssignment);
        Assert.NotEqual(snapshot.SourceFilePath, snapshot.PredictionShadowExperimentAssignment!.SourcePath);
    }

    [Fact]
    public void SourceMeasurementsCreateTheSameAudioSubtitleInputFacts()
    {
        EncodeExecutionSnapshot snapshot = Build(
            Settings(),
            Item() with
            {
                SourceMeasurements = new EncodeExecutionSourceMeasurements
                {
                    AudioBitrateKbps = 192,
                    AudioStreamCount = 2,
                    SubtitleBitrateKbps = 0,
                    SubtitleStreamCount = 3,
                    EstimatedPlannedAudioBitrateKbps = 128,
                    Height = 1080
                }
            });

        Assert.Equal("C:\\Media\\clip.mp4", snapshot.Input.InputPath);
        Assert.Equal(192, snapshot.Input.KnownAudioBitrateKbps);
        Assert.Equal(2, snapshot.Input.KnownAudioStreamCount);
        Assert.Equal(24, snapshot.Input.KnownMappedAncillaryBitrateKbps);
        Assert.Equal(1080, snapshot.SourceHeight);
    }

    [Fact]
    public void PlannedAudioBitrateIsUsedOnlyWhenMeasuredAudioBitrateIsUnavailable()
    {
        EncodeExecutionSnapshot snapshot = Build(
            Settings(),
            Item() with
            {
                SourceMeasurements = new EncodeExecutionSourceMeasurements
                {
                    AudioBitrateKbps = 0,
                    AudioStreamCount = 1,
                    EstimatedPlannedAudioBitrateKbps = 160
                }
            });

        Assert.Equal(160, snapshot.Input.KnownAudioBitrateKbps);
    }

    [Fact]
    public void NoCompressionRetainsSourceBitrateSafetyBumpAndHardwareNeutralQuality()
    {
        var builder = new EncodeExecutionSnapshotBuilder(sourceVideoBitrateKbps: _ => 1000);
        EncodeExecutionSnapshot snapshot = Build(
            Settings() with { CompressionProfile = "No Compression", NumericQualityValue = 31 },
            Item() with { MediaDurationSeconds = 60, EstimatedTargetMb = null },
            builder);

        Assert.Equal(19, snapshot.QualityValue);
        Assert.NotNull(snapshot.TargetMb);
        Assert.Equal(1000 * 1.15 * 60 / 8192d, snapshot.TargetMb.Value, 8);
    }

    [Fact]
    public void StorageSavingsQualityTargetKeepsQualityEncodingInsteadOfFixedSize()
    {
        var savings = new StorageSavingsOptions
        {
            Enabled = true,
            TargetMode = StorageSavingsOptions.QualityTarget,
            QualityValue = 17
        };
        EncodeExecutionSnapshotBuildResult result = BuildDetailed(
            Settings() with { AutomaticQuality = false, StorageSavings = savings },
            Item() with { EstimatedTargetMb = null });

        Assert.True(result.StorageSavingsApplies);
        Assert.True(result.UsesStorageQualityTarget);
        Assert.Equal(17, result.Snapshot.QualityValue);
        Assert.Null(result.Snapshot.TargetMb);
    }

    [Fact]
    public void SavedJobSettingsBuildTheSameSnapshotAsEquivalentEffectiveSettings()
    {
        var job = new EncodeJobSettings
        {
            OutputFolder = "C:\\Out",
            CompressionProfile = "Medium Quality (Default)",
            EncoderId = VideoEncoderIds.Nvenc,
            VideoCodec = nameof(VideoCodecFamily.Hevc),
            EncoderPreset = "p5",
            OutputContainer = nameof(OutputContainerSelection.Matroska),
            QualityValue = 22,
            QualityMode = "Automatic",
            QualityTarget = nameof(QualityTarget.Balanced),
            TenBit = true,
            AudioChannels = "Stereo (2.0)",
            VideoFormat = "H.265 / HEVC (x265)",
            AutoTargetSize = true,
            TargetSize = "275",
            Resolution = "720p",
            DeleteSourceAfterCompression = true,
            EnableOutputSuffix = true,
            EnableCodecSuffix = true,
            OutputSuffix = "Research",
            Restoration = new VideoRestorationSettings
            {
                Mode = VideoRestorationMode.Custom,
                Preset = VideoRestorationPreset.Custom
            }
        };
        var config = new Config
        {
            LimitGpuEncodingQueueToOneJob = false,
            ContainerCompatibilityPolicy = nameof(ContainerCompatibilityPolicy.Strict),
            StorageSavings = new StorageSavingsOptions { Enabled = true }
        };
        EncodeExecutionSnapshotSettings savedSettings = EncodeExecutionSnapshotSettings.FromSavedJob(job, config);
        EncodeExecutionSnapshotSettings effectiveSettings = Settings() with
        {
            OutputFolder = job.OutputFolder,
            CompressionProfile = job.CompressionProfile,
            EncoderId = job.EncoderId,
            CodecFamily = VideoCodecFamily.Hevc,
            VideoFormatText = job.VideoFormat,
            EncoderPreset = job.EncoderPreset,
            NumericQualityValue = job.QualityValue,
            AutomaticQuality = true,
            QualityTarget = QualityTarget.Balanced,
            TenBit = job.TenBit,
            AudioChannelSelection = job.AudioChannels,
            AutoTargetSize = job.AutoTargetSize,
            TargetSizeText = job.TargetSize,
            ScaleMode = EncodingService.ScaleMode.To720p,
            OutputContainer = OutputContainerSelection.Matroska,
            CompatibilityPolicy = ContainerCompatibilityPolicy.Strict,
            DeleteSourceAfterCompression = true,
            EnableOutputSuffix = true,
            EnableCodecSuffix = true,
            OutputSuffix = "Research",
            Restoration = job.Restoration,
            StorageSavings = config.StorageSavings
        };

        EncodeExecutionSnapshot fromJob = Build(savedSettings);
        EncodeExecutionSnapshot fromEffective = Build(effectiveSettings);
        AssertEquivalent(fromEffective, fromJob);
    }

    [Fact]
    public void IncompleteSavedJobRequiresExplicitEffectiveValues()
    {
        Assert.Throws<InvalidDataException>(() => EncodeExecutionSnapshotSettings.FromSavedJob(
            new EncodeJobSettings(),
            new Config()));
    }

    [Fact]
    public void OutputSuffixAndAudioSelectionUseSharedPlainValuePolicies()
    {
        Assert.Equal(" [HEVC] [Research]", EncodeExecutionSnapshotBuilder.BuildOutputSuffix(
            "H.265 / HEVC (x265)",
            enableOutputSuffix: true,
            enableCodecSuffix: true,
            "Research"));
        Assert.Equal(" Research", EncodeExecutionSnapshotBuilder.BuildOutputSuffix(
            "AV1", enableOutputSuffix: true, enableCodecSuffix: false, " Research "));
        Assert.Equal(2, EncodeExecutionSnapshotBuilder.ResolveAudioChannels("Stereo (2.0)"));
        Assert.Equal(6, EncodeExecutionSnapshotBuilder.ResolveAudioChannels("5.1"));
        Assert.Null(EncodeExecutionSnapshotBuilder.ResolveAudioChannels("Keep source layout"));
    }

    [Fact]
    public void OrdinaryUnassignedItemRemainsUnassignedAndQueueConcurrencyUsesSharedLimit()
    {
        EncodeExecutionSnapshot snapshot = Build(Settings(), Item());

        Assert.Null(snapshot.PredictionShadowExperimentAssignment);
        Assert.Equal(2, EncodeExecutionSnapshotBuilder.GetMaximumConcurrentEncodes(VideoEncoderIds.Nvenc, false));
        Assert.Equal(1, EncodeExecutionSnapshotBuilder.GetMaximumConcurrentEncodes(VideoEncoderIds.Nvenc, true));
        Assert.Equal(1, EncodeExecutionSnapshotBuilder.GetMaximumConcurrentEncodes(VideoEncoderIds.Libx265, false));
    }

    [Theory]
    [InlineData("eligible", true)]
    [InlineData("disabled", false)]
    [InlineData("custom-target", false)]
    [InlineData("custom-profile", false)]
    [InlineData("manual-target", false)]
    [InlineData("no-compression", false)]
    [InlineData("h264-output", false)]
    public void HardContractPreservesExistingPlanningApplicability(string scenario, bool applies)
    {
        var settings = Settings() with { StorageSavings = new StorageSavingsOptions { Enabled = scenario != "disabled" } };
        var item = Item();
        if (scenario == "custom-target") item = item with { CustomTargetMb = 25 };
        if (scenario == "custom-profile") item = item with { CustomCompressionProfile = "High Quality" };
        if (scenario == "manual-target") settings = settings with { AutoTargetSize = false, TargetSizeText = "25" };
        if (scenario == "no-compression") settings = settings with { CompressionProfile = "No Compression" };
        if (scenario == "h264-output") settings = settings with { EncoderId = VideoEncoderIds.Libx264, CodecFamily = VideoCodecFamily.H264 };
        var result = new EncodeExecutionSnapshotBuilder(sourceVideoBitrateKbps: _ => 1000).BuildWithDetails(Identity(), settings, item);
        Assert.Equal(applies, result.StorageSavingsApplies);
        Assert.Equal(applies, result.Snapshot.StorageSavingsContract.Applies);
    }

    [Fact]
    public void SourceBytesAreCapturedFromPhysicalFilesAndSharedWithSavedJobSnapshots()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-ContractSnapshot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "source.mkv");
            File.WriteAllBytes(path, new byte[1001]);
            var settings = Settings() with { StorageSavings = new StorageSavingsOptions { Enabled = true } };
            var item = Item() with { SourceFilePath = path, LogicalSourcePath = path, SourceSizeBytes = 9_000_000 };
            var snapshot = Build(settings, item);
            Assert.Equal(1001, snapshot.StorageSavingsContract.SourceSizeBytes);
            Assert.Equal(900, snapshot.StorageSavingsContract.MaximumAcceptedOutputBytes);
            Assert.Equal(22, snapshot.QualityValue);
            File.WriteAllBytes(path, new byte[2000]);
            Assert.Equal(1001, snapshot.StorageSavingsContract.SourceSizeBytes);

            var dvd = new EncodingInputSource
            {
                Kind = EncodingInputKind.DvdPhysicalConcat,
                SourcePath = root,
                SourceFiles = new[] { path },
                AllowSourceDeletion = false
            };
            Assert.Equal(2000, StorageSavingsContractService.Capture(true, dvd).SourceSizeBytes);
            Assert.Null(StorageSavingsContractService.Capture(true, EncodingInputSource.FromFile(Path.Combine(root, "missing"))).MaximumAcceptedOutputBytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static EncodeExecutionSnapshot Build(
        EncodeExecutionSnapshotSettings? settings = null,
        EncodeExecutionSnapshotItem? item = null,
        EncodeExecutionSnapshotBuilder? builder = null) =>
        (builder ?? new EncodeExecutionSnapshotBuilder()).Build(Identity(), settings ?? Settings(), item ?? Item());

    [Theory]
    [InlineData("automatic", true)]
    [InlineData("off", false)]
    [InlineData("manual", false)]
    [InlineData("target", false)]
    [InlineData("custom", false)]
    [InlineData("no-compression", false)]
    [InlineData("restoration", false)]
    [InlineData("qsv", false)]
    public void Phase2SnapshotEligibilityIsAdditionalToUnchangedPhase1Applicability(string scenario, bool expected)
    {
        var settings = Settings() with { AutomaticQuality = true, StorageSavings = new() { Enabled = true } };
        var item = Item();
        if (scenario == "off") settings = settings with { StorageSavings = new() { Enabled = false } };
        if (scenario == "manual") settings = settings with { AutomaticQuality = false };
        if (scenario == "target") settings = settings with { TargetSizeText = "100", AutoTargetSize = false };
        if (scenario == "custom") item = item with { CustomCompressionProfile = "Custom" };
        if (scenario == "no-compression") settings = settings with { CompressionProfile = "No Compression" };
        if (scenario == "restoration") settings = settings with { Restoration = new() { Mode = VideoRestorationMode.Custom, Preset = VideoRestorationPreset.VintageAnimationLight } };
        if (scenario == "qsv") settings = settings with { EncoderId = VideoEncoderIds.Qsv, EncoderPreset = "slow" };
        var snapshot = Build(settings, item);
        Assert.Equal(expected, snapshot.AdaptiveStorageSavingsEnabled);
        if (scenario is "manual" or "restoration" or "qsv") Assert.True(snapshot.StorageSavingsContract.Applies);
    }

    private static EncodeExecutionSnapshotBuildResult BuildDetailed(
        EncodeExecutionSnapshotSettings settings,
        EncodeExecutionSnapshotItem item) =>
        new EncodeExecutionSnapshotBuilder().BuildWithDetails(Identity(), settings, item);

    private static EncodeExecutionSnapshotIdentity Identity() => new()
    {
        OperationId = "operation-1",
        StatisticsStartUtc = StartedUtc,
        MediaFluxVersion = "1.7.3",
        StatisticsPath = "C:\\UserData\\encoding-statistics.jsonl",
        ContainerCompatibilityConfirmed = false
    };

    private static EncodeExecutionSnapshotSettings Settings() => new()
    {
        OutputFolder = "C:\\Out",
        CompressionProfile = "Medium Quality (Default)",
        EncoderId = VideoEncoderIds.Nvenc,
        CodecFamily = VideoCodecFamily.Hevc,
        VideoFormatText = "H.265 / HEVC (x265)",
        EncoderPreset = "p5",
        NumericQualityValue = 22,
        AutomaticQuality = false,
        QualityTarget = QualityTarget.Balanced,
        TenBit = false,
        AudioChannelSelection = "Keep source layout",
        LimitGpuEncodingQueueToOneJob = false,
        IncludeConcurrentEncoderSessions = true,
        AutoTargetSize = false,
        TargetSizeText = string.Empty,
        ScaleMode = EncodingService.ScaleMode.None,
        EnableOutputSuffix = false,
        EnableCodecSuffix = false,
        OutputSuffix = string.Empty,
        Restoration = new VideoRestorationSettings(),
        StorageSavings = new StorageSavingsOptions(),
        OutputContainer = OutputContainerSelection.Auto,
        CompatibilityPolicy = ContainerCompatibilityPolicy.Intelligent,
        DeleteSourceAfterCompression = false
    };

    private static EncodeExecutionSnapshotItem Item() => new()
    {
        SourceFilePath = "C:\\Media\\clip.mp4",
        LogicalSourcePath = "C:\\Media\\clip.mp4",
        MediaDurationSeconds = 60,
        SourceSizeBytes = 1_000_000,
        EstimatedTargetMb = 100
    };

    private static void AssertEquivalent(EncodeExecutionSnapshot expected, EncodeExecutionSnapshot actual)
    {
        Assert.Equal(expected.OperationId, actual.OperationId);
        Assert.Equal(expected.StatisticsStartUtc, actual.StatisticsStartUtc);
        Assert.Equal(expected.SourceFilePath, actual.SourceFilePath);
        Assert.Equal(expected.LogicalSourcePath, actual.LogicalSourcePath);
        Assert.Equal(expected.Input.Kind, actual.Input.Kind);
        Assert.Equal(expected.Input.InputPath, actual.Input.InputPath);
        Assert.Equal(expected.Input.SourcePath, actual.Input.SourcePath);
        Assert.Equal(expected.Input.OutputBaseName, actual.Input.OutputBaseName);
        Assert.Equal(expected.Input.KnownAudioBitrateKbps, actual.Input.KnownAudioBitrateKbps);
        Assert.Equal(expected.Input.KnownAudioStreamCount, actual.Input.KnownAudioStreamCount);
        Assert.Equal(expected.Input.KnownMappedAncillaryBitrateKbps, actual.Input.KnownMappedAncillaryBitrateKbps);
        Assert.Equal(expected.OutputFolder, actual.OutputFolder);
        Assert.Equal(expected.Suffix, actual.Suffix);
        Assert.Equal(expected.Encoder, actual.Encoder);
        Assert.Equal(expected.UseGpu, actual.UseGpu);
        Assert.Equal(expected.TargetMb, actual.TargetMb);
        Assert.Equal(expected.StorageSavingsContract, actual.StorageSavingsContract);
        Assert.Equal(expected.AdaptiveStorageSavingsEnabled, actual.AdaptiveStorageSavingsEnabled);
        Assert.Equal(expected.ScaleMode, actual.ScaleMode);
        Assert.Equal(expected.Restoration.Mode, actual.Restoration.Mode);
        Assert.Equal(expected.Restoration.Preset, actual.Restoration.Preset);
        Assert.Equal(expected.EncoderPreset, actual.EncoderPreset);
        Assert.Equal(expected.QualityValue, actual.QualityValue);
        Assert.Equal(expected.QualityIntent, actual.QualityIntent);
        Assert.Equal(expected.TenBit, actual.TenBit);
        Assert.Equal(expected.AudioChannels, actual.AudioChannels);
        Assert.Equal(expected.ConcurrentEncoderSessions, actual.ConcurrentEncoderSessions);
        Assert.Equal(expected.OutputContainer, actual.OutputContainer);
        Assert.Equal(expected.CompatibilityPolicy, actual.CompatibilityPolicy);
        Assert.Equal(expected.SourceSizeBytes, actual.SourceSizeBytes);
        Assert.Equal(expected.MediaDurationSeconds, actual.MediaDurationSeconds);
        Assert.Equal(expected.SourceHeight, actual.SourceHeight);
        Assert.Equal(expected.OutputHeight, actual.OutputHeight);
        Assert.Equal(expected.EncoderText, actual.EncoderText);
        Assert.Equal(expected.Codec, actual.Codec);
        Assert.Equal(expected.MediaFluxVersion, actual.MediaFluxVersion);
        Assert.Equal(expected.StatisticsPath, actual.StatisticsPath);
        Assert.Equal(expected.DeleteSourceAfterCompression, actual.DeleteSourceAfterCompression);
    }
}
