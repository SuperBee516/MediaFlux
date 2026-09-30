using MediaFlux.Models;
using MediaFlux.Services.Encoders;

namespace MediaFlux.Services;

/// <summary>
/// Plain effective settings used to construct one production execution snapshot.
/// A saved-job caller can populate this model from EncodeJobSettings and Config
/// without creating or reading WinForms controls.
/// </summary>
public sealed record EncodeExecutionSnapshotSettings
{
    public required string OutputFolder { get; init; }
    public required string CompressionProfile { get; init; }
    public required string EncoderId { get; init; }
    public required VideoCodecFamily CodecFamily { get; init; }
    public required string VideoFormatText { get; init; }
    public required string EncoderPreset { get; init; }
    public int? NumericQualityValue { get; init; }
    public required bool AutomaticQuality { get; init; }
    public required QualityTarget QualityTarget { get; init; }
    public required bool TenBit { get; init; }
    public required string? AudioChannelSelection { get; init; }
    public required bool LimitGpuEncodingQueueToOneJob { get; init; }
    public required bool IncludeConcurrentEncoderSessions { get; init; }
    public required bool AutoTargetSize { get; init; }
    public required string TargetSizeText { get; init; }
    public required EncodingService.ScaleMode ScaleMode { get; init; }
    public required bool EnableOutputSuffix { get; init; }
    public required bool EnableCodecSuffix { get; init; }
    public required string OutputSuffix { get; init; }
    public required VideoRestorationSettings Restoration { get; init; }
    public required StorageSavingsOptions StorageSavings { get; init; }
    public required OutputContainerSelection OutputContainer { get; init; }
    public required ContainerCompatibilityPolicy CompatibilityPolicy { get; init; }
    public required bool DeleteSourceAfterCompression { get; init; }
    /// <summary>Only set for the existing MainForm fallback snapshot path.</summary>
    public string? EncoderDisplayTextOverride { get; init; }

    /// <summary>
    /// Creates the same explicit settings model from a complete saved job and its
    /// current global config. Incomplete legacy jobs require an effective fallback
    /// from their existing caller because MainForm's soft-apply leaves some controls
    /// unchanged when persisted values are absent.
    /// </summary>
    public static EncodeExecutionSnapshotSettings FromSavedJob(
        EncodeJobSettings job,
        Config config)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(job.EncoderId) ||
            string.IsNullOrWhiteSpace(job.CompressionProfile) ||
            string.IsNullOrWhiteSpace(job.VideoFormat) ||
            string.IsNullOrWhiteSpace(job.Resolution))
        {
            throw new InvalidDataException(
                "This saved job lacks settings which MainForm currently inherits from effective controls. " +
                "Supply those effective values explicitly before building an execution snapshot.");
        }

        ContainerCompatibilityPolicy compatibilityPolicy =
            Enum.TryParse(config.ContainerCompatibilityPolicy, true, out ContainerCompatibilityPolicy parsedPolicy) &&
            Enum.IsDefined(parsedPolicy)
                ? parsedPolicy
                : ContainerCompatibilityPolicy.Intelligent;
        QualityTarget qualityTarget =
            Enum.TryParse(job.QualityTarget, true, out QualityTarget parsedTarget) &&
            Enum.IsDefined(parsedTarget)
                ? parsedTarget
                : QualityTarget.Balanced;
        return new EncodeExecutionSnapshotSettings
        {
            OutputFolder = job.OutputFolder,
            CompressionProfile = job.CompressionProfile,
            EncoderId = job.EncoderId,
            CodecFamily = VideoEncoderCompatibility.ParseCodecFamily(job.VideoCodec),
            VideoFormatText = job.VideoFormat,
            EncoderPreset = job.EncoderPreset,
            NumericQualityValue = job.QualityValue,
            AutomaticQuality = string.Equals(job.QualityMode, "Automatic", StringComparison.OrdinalIgnoreCase),
            QualityTarget = qualityTarget,
            TenBit = job.TenBit,
            AudioChannelSelection = job.AudioChannels,
            LimitGpuEncodingQueueToOneJob = config.LimitGpuEncodingQueueToOneJob,
            IncludeConcurrentEncoderSessions = true,
            AutoTargetSize = job.AutoTargetSize,
            TargetSizeText = job.TargetSize,
            ScaleMode = EncodeExecutionSnapshotBuilder.ParseScaleMode(job.Resolution),
            EnableOutputSuffix = job.EnableOutputSuffix,
            EnableCodecSuffix = job.EnableCodecSuffix,
            OutputSuffix = job.OutputSuffix,
            Restoration = job.Restoration?.Clone() ?? new VideoRestorationSettings(),
            StorageSavings = config.StorageSavings?.CloneNormalized() ?? new StorageSavingsOptions(),
            OutputContainer = OutputContainerPolicy.ParseSelection(job.OutputContainer),
            CompatibilityPolicy = compatibilityPolicy,
            DeleteSourceAfterCompression = job.DeleteSourceAfterCompression
        };
    }
}

/// <summary>Optional DVD facts needed by the existing source-size estimator.</summary>
public sealed record EncodeExecutionDvdFacts(
    string OutputPath,
    long CombinedSizeBytes,
    double CombinedDurationSeconds,
    int? VideoWidth,
    int? VideoHeight,
    double? FrameRate,
    string? VideoCodec);

/// <summary>Source-stream measurements gathered by an existing metadata service.</summary>
public sealed record EncodeExecutionSourceMeasurements
{
    public double? AudioBitrateKbps { get; init; }
    public int AudioStreamCount { get; init; }
    public double? SubtitleBitrateKbps { get; init; }
    public int SubtitleStreamCount { get; init; }
    public double? EstimatedPlannedAudioBitrateKbps { get; init; }
    public int? Height { get; init; }
}

/// <summary>Explicit plain per-item state consumed by the execution snapshot builder.</summary>
public sealed record EncodeExecutionSnapshotItem
{
    public required string SourceFilePath { get; init; }
    public required string LogicalSourcePath { get; init; }
    /// <summary>Required only for a non-file input such as a DVD title.</summary>
    public EncodingInputSource? PreparedInput { get; init; }
    public EncodeExecutionSourceMeasurements? SourceMeasurements { get; init; }
    public string? CustomCompressionProfile { get; init; }
    public double? CustomTargetMb { get; init; }
    public double? EstimatedTargetMb { get; init; }
    public EncodingSizePredictionCalibration? SizePredictionCalibration { get; init; }
    public PredictionShadowExperimentAssignmentBinding? PredictionShadowExperimentAssignment { get; init; }
    public long? SourceSizeBytes { get; init; }
    public double? MediaDurationSeconds { get; init; }
    public LibraryPolicyQueueItem? LibraryPolicyIntent { get; init; }
    public EncodingPreset? PolicyEncodingPreset { get; init; }
    public EncodeExecutionDvdFacts? Dvd { get; init; }
}

/// <summary>Non-UI values which identify and annotate an execution snapshot.</summary>
public sealed record EncodeExecutionSnapshotIdentity
{
    public required string OperationId { get; init; }
    public required DateTime StatisticsStartUtc { get; init; }
    public required string MediaFluxVersion { get; init; }
    public required string StatisticsPath { get; init; }
    public required bool ContainerCompatibilityConfirmed { get; init; }
}

/// <summary>Snapshot plus the same non-execution projection facts used by queue logging.</summary>
public sealed record EncodeExecutionSnapshotBuildResult(
    EncodeExecutionSnapshot Snapshot,
    bool StorageSavingsApplies,
    bool UsesStorageQualityTarget,
    StorageSavingsOptions StorageSavings);

/// <summary>
/// The one shared production policy boundary for converting effective settings and
/// an explicit queue item into the immutable snapshot consumed by the orchestrator.
/// This type has no UI or MainForm dependencies.
/// </summary>
public sealed class EncodeExecutionSnapshotBuilder
{
    private const double MegabytesPerMiB = 1024d * 1024d;
    private readonly SizeEstimateService? _sizeEstimateService;
    private readonly Func<string, int?>? _sourceVideoBitrateKbps;

    public EncodeExecutionSnapshotBuilder(
        SizeEstimateService? sizeEstimateService = null,
        Func<string, int?>? sourceVideoBitrateKbps = null)
    {
        _sizeEstimateService = sizeEstimateService;
        _sourceVideoBitrateKbps = sourceVideoBitrateKbps;
    }

    public EncodeExecutionSnapshot Build(
        EncodeExecutionSnapshotIdentity identity,
        EncodeExecutionSnapshotSettings settings,
        EncodeExecutionSnapshotItem item) =>
        BuildWithDetails(identity, settings, item).Snapshot;

    public EncodeExecutionSnapshotBuildResult BuildWithDetails(
        EncodeExecutionSnapshotIdentity identity,
        EncodeExecutionSnapshotSettings settings,
        EncodeExecutionSnapshotItem item)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.SourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.LogicalSourcePath);
        ResolvedVideoEncoder resolved = ResolveEncoder(settings, item, out string formatText);
        EncodingInputSource input = BuildInput(item);
        bool isPolicyItem = item.LibraryPolicyIntent is not null;
        int? audioChannels = ResolveAudioChannels(settings.AudioChannelSelection);
        int requestedQuality = ResolveRequestedQuality(settings, resolved, item);
        bool useGpu = resolved.Provider.Capabilities.IsHardware;
        bool concurrentSessions = !isPolicyItem && settings.IncludeConcurrentEncoderSessions &&
            GetMaximumConcurrentEncodes(resolved.Selection.EncoderId, settings.LimitGpuEncodingQueueToOneJob) > 1;
        string? encoderPreset = ResolveEncoderPreset(settings, item);
        ValidatedEncoderSettings validated = EncodingRequestValidator.ValidateAndNormalize(
            EncoderRegistry.Default,
            resolved.Selection,
            useGpu,
            targetMb: null,
            encoderPreset,
            requestedQuality,
            ResolveTenBit(settings, item),
            audioChannels,
            concurrentSessions);

        string codec = validated.Resolved.Selection.FfmpegCodec;
        string profile = !string.IsNullOrWhiteSpace(item.CustomCompressionProfile)
            ? item.CustomCompressionProfile
            : settings.CompressionProfile;
        bool hasCustomProfile = !string.IsNullOrWhiteSpace(item.CustomCompressionProfile);
        bool hasCustomTarget = item.CustomTargetMb.HasValue;
        bool automaticQuality = !isPolicyItem && settings.AutomaticQuality;
        string targetText = hasCustomProfile ? string.Empty : settings.TargetSizeText;
        bool autoTargetSize = !hasCustomProfile && settings.AutoTargetSize;
        double? configuredManualTargetMb = EncodingTargetSizeResolver.ResolveConfiguredManualTargetMb(
            autoTargetSize,
            targetText);
        bool hasManualTarget = configuredManualTargetMb is > 0;
        StorageSavingsOptions storageSavings = settings.StorageSavings.CloneNormalized();
        bool storageSavingsApplies = storageSavings.Enabled &&
            SizeEstimateService.IsHevcCodec(codec) &&
            !hasCustomTarget &&
            !hasCustomProfile &&
            !hasManualTarget &&
            !profile.Equals("No Compression", StringComparison.OrdinalIgnoreCase);
        bool useStorageQualityTarget = storageSavingsApplies &&
            storageSavings.UsesQualityTarget &&
            !settings.AutomaticQuality;

        int estimateQuality = validated.QualityValue;
        if (useStorageQualityTarget)
            estimateQuality = storageSavings.QualityValue;

        EncodingQualityIntent? qualityIntent = isPolicyItem
            ? null
            : settings.AutomaticQuality
                ? EncodingQualityIntent.Automatic(settings.QualityTarget)
                : null;

        EncodingService.ScaleMode scaleMode = isPolicyItem
            ? PolicyScaleMode(item.LibraryPolicyIntent!)
            : settings.ScaleMode;
        int? targetHeight = isPolicyItem
            ? item.LibraryPolicyIntent!.PreserveSourceResolution
                ? null
                : item.LibraryPolicyIntent.MaximumOutputHeight
            : EstimateTargetHeight(scaleMode);
        OutputContainerSelection outputContainer = ResolveOutputContainer(
            settings.OutputContainer,
            item.LibraryPolicyIntent,
            item.PolicyEncodingPreset);
        double? targetMb = ResolveTargetMb(
            settings,
            item,
            validated,
            profile,
            automaticQuality,
            autoTargetSize,
            targetText,
            configuredManualTargetMb,
            hasManualTarget,
            useStorageQualityTarget,
            storageSavingsApplies,
            storageSavings,
            estimateQuality,
            targetHeight,
            audioChannels);

        string encoderText = settings.EncoderDisplayTextOverride ??
            validated.Resolved.Provider.Capabilities.DisplayName;
        int? sourceHeight = item.Dvd?.VideoHeight ?? item.SourceMeasurements?.Height;
        int? outputHeight = RuntimeOutputHeight(sourceHeight, scaleMode);
        string suffix = BuildOutputSuffix(
            isPolicyItem ? formatText : settings.VideoFormatText,
            settings.EnableOutputSuffix,
            settings.EnableCodecSuffix,
            settings.OutputSuffix);
        string outputFolder = item.Dvd is null
            ? settings.OutputFolder
            : Path.GetDirectoryName(item.Dvd.OutputPath) ?? string.Empty;

        var snapshot = new EncodeExecutionSnapshot
        {
            OperationId = identity.OperationId,
            StatisticsStartUtc = identity.StatisticsStartUtc,
            SourceFilePath = item.SourceFilePath,
            LogicalSourcePath = item.LogicalSourcePath,
            Input = input,
            OutputFolder = outputFolder,
            Suffix = suffix,
            Encoder = validated.Resolved.Selection,
            UseGpu = validated.UseGpu,
            TargetMb = targetMb,
            SizePredictionCalibration = item.SizePredictionCalibration,
            ScaleMode = scaleMode,
            Restoration = settings.Restoration?.Clone() ?? new VideoRestorationSettings(),
            EncoderPreset = validated.Preset,
            QualityValue = estimateQuality,
            QualityIntent = qualityIntent,
            TenBit = validated.TenBit,
            AudioChannels = audioChannels,
            ConcurrentEncoderSessions = validated.ConcurrentEncoderSessions,
            OutputContainer = outputContainer,
            ContainerCompatibilityConfirmed = identity.ContainerCompatibilityConfirmed,
            CompatibilityPolicy = settings.CompatibilityPolicy,
            PredictionShadowExperimentAssignment = item.PredictionShadowExperimentAssignment,
            SourceSizeBytes = item.SourceSizeBytes,
            MediaDurationSeconds = item.MediaDurationSeconds is > 0 ? item.MediaDurationSeconds : null,
            SourceHeight = sourceHeight,
            OutputHeight = outputHeight,
            EncoderText = encoderText,
            Codec = codec,
            MediaFluxVersion = identity.MediaFluxVersion,
            StatisticsPath = identity.StatisticsPath,
            DeleteSourceAfterCompression = settings.DeleteSourceAfterCompression
        };
        return new EncodeExecutionSnapshotBuildResult(
            snapshot,
            storageSavingsApplies,
            useStorageQualityTarget,
            storageSavings.CloneNormalized());
    }

    /// <summary>The existing scheduling limit, shared with snapshot concurrency policy.</summary>
    public static int GetMaximumConcurrentEncodes(string encoderId, bool limitGpuQueueToOneJob)
    {
        if (!encoderId.Equals(VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase) ||
            limitGpuQueueToOneJob)
            return 1;
        return 2;
    }

    private static ResolvedVideoEncoder ResolveEncoder(
        EncodeExecutionSnapshotSettings settings,
        EncodeExecutionSnapshotItem item,
        out string formatText)
    {
        if (item.LibraryPolicyIntent is not { } policy)
        {
            formatText = settings.VideoFormatText;
            return EncoderRegistry.Default.Resolve(settings.EncoderId, settings.CodecFamily);
        }

        EncodingPreset? preset = item.PolicyEncodingPreset;
        VideoCodecFamily codecFamily = preset is null
            ? policy.ProposedCodec
            : VideoEncoderCompatibility.ParseCodecFamily(
                string.IsNullOrWhiteSpace(preset.VideoCodec) ? preset.VideoFormat : preset.VideoCodec);
        string encoderId = preset is null
            ? policy.EncoderId
            : VideoEncoderCompatibility.ResolveEncoderId(
                string.IsNullOrWhiteSpace(preset.EncoderId) ? preset.EncoderMode : preset.EncoderId,
                codecFamily);
        formatText = CodecDisplayName(codecFamily);
        return EncoderRegistry.Default.Resolve(encoderId, codecFamily);
    }

    private static EncodingInputSource BuildInput(EncodeExecutionSnapshotItem item)
    {
        if (item.Dvd is not null)
            return item.PreparedInput ?? throw new ArgumentException(
                "A prepared DVD input is required for a DVD execution item.", nameof(item));

        EncodeExecutionSourceMeasurements? source = item.SourceMeasurements;
        double? audioBitrate = source?.AudioBitrateKbps is > 0
            ? source.AudioBitrateKbps
            : source?.EstimatedPlannedAudioBitrateKbps is > 0
                ? source.EstimatedPlannedAudioBitrateKbps
                : null;
        double subtitleBitrate = source?.SubtitleBitrateKbps ?? 0;
        double ancillaryBitrate = subtitleBitrate +
            (subtitleBitrate > 0 ? 0 : (source?.SubtitleStreamCount ?? 0) * 8d);
        return EncodingInputSource.FromFile(
            item.SourceFilePath,
            audioBitrate,
            source?.AudioStreamCount ?? 0,
            ancillaryBitrate);
    }

    private static string? ResolveEncoderPreset(EncodeExecutionSnapshotSettings settings, EncodeExecutionSnapshotItem item) =>
        item.LibraryPolicyIntent is { } policy
            ? item.PolicyEncodingPreset?.EncoderPreset ?? policy.EncoderPreset
            : settings.EncoderPreset;

    private static int ResolveRequestedQuality(
        EncodeExecutionSnapshotSettings settings,
        ResolvedVideoEncoder resolved,
        EncodeExecutionSnapshotItem item)
    {
        if (item.LibraryPolicyIntent is { } policy)
            return item.PolicyEncodingPreset?.QualityValue ?? policy.QualityValue;

        if (settings.CompressionProfile.Equals("No Compression", StringComparison.OrdinalIgnoreCase))
            return resolved.Provider.Capabilities.IsHardware ? 19 : 22;
        if (settings.NumericQualityValue.HasValue)
            return settings.NumericQualityValue.Value;
        if (resolved.Provider.Capabilities.IsHardware)
            return 19;
        string codec = resolved.Selection.FfmpegCodec;
        return codec.Contains("265", StringComparison.OrdinalIgnoreCase) ||
               codec.Contains("av1", StringComparison.OrdinalIgnoreCase)
            ? 24
            : 22;
    }

    private static bool ResolveTenBit(
        EncodeExecutionSnapshotSettings settings,
        EncodeExecutionSnapshotItem item) =>
        item.LibraryPolicyIntent is { } policy
            ? item.PolicyEncodingPreset?.TenBit ?? policy.PreferredBitDepth >= 10
            : settings.TenBit;

    private double? ResolveTargetMb(
        EncodeExecutionSnapshotSettings settings,
        EncodeExecutionSnapshotItem item,
        ValidatedEncoderSettings validated,
        string profile,
        bool automaticQuality,
        bool autoTargetSize,
        string targetText,
        double? configuredManualTargetMb,
        bool hasManualTarget,
        bool useStorageQualityTarget,
        bool storageSavingsApplies,
        StorageSavingsOptions storageSavings,
        int estimateQuality,
        int? targetHeight,
        int? audioChannels)
    {
        if (item.LibraryPolicyIntent is { } policy)
        {
            return item.PolicyEncodingPreset is { AutoTargetSize: false, ManualTargetMb: > 0 } preset
                ? preset.ManualTargetMb
                : policy.ProjectedOutputBytes is > 0
                    ? policy.ProjectedOutputBytes.Value / MegabytesPerMiB
                    : null;
        }

        if (item.CustomTargetMb.HasValue)
            return item.CustomTargetMb;

        if (!automaticQuality && profile.Equals("No Compression", StringComparison.OrdinalIgnoreCase))
        {
            if (item.Dvd is { } dvd)
                return dvd.CombinedSizeBytes / MegabytesPerMiB;

            if (_sourceVideoBitrateKbps is null)
                throw new InvalidOperationException(
                    "A source video bitrate provider is required for the No Compression target-size policy.");
            int? sourceKbps = _sourceVideoBitrateKbps(item.SourceFilePath);
            return sourceKbps.HasValue && item.MediaDurationSeconds is > 0
                ? (sourceKbps.Value * 1.15 * item.MediaDurationSeconds.Value) / 8192d
                : null;
        }

        if (automaticQuality)
        {
            return EncodingTargetSizeResolver.ResolveAutomaticQualityTargetMb(
                automaticQuality,
                autoTargetSize,
                targetText);
        }

        if (hasManualTarget)
            return configuredManualTargetMb;
        if (useStorageQualityTarget)
            return null;
        if (item.EstimatedTargetMb is > 0)
            return item.EstimatedTargetMb;

        double fallbackEstimate;
        if (item.Dvd is { } dvdFacts)
        {
            double sourceMb = dvdFacts.CombinedSizeBytes / MegabytesPerMiB;
            fallbackEstimate = SizeEstimateService.EstimateAutoTargetMbSmart(
                sourceMb,
                dvdFacts.CombinedDurationSeconds,
                dvdFacts.VideoWidth ?? 0,
                dvdFacts.VideoHeight ?? 0,
                dvdFacts.FrameRate ?? 0,
                sourceVideoBitrateKbps: 0,
                dvdFacts.VideoCodec,
                profile,
                validated.Resolved.Selection,
                estimateQuality,
                targetHeight);
        }
        else
        {
            if (_sizeEstimateService is null)
                throw new InvalidOperationException(
                    "A SizeEstimateService is required when no queued estimate is available.");
            StorageSavingsOptions? appliedSavings = storageSavingsApplies
                ? storageSavings
                : null;
            fallbackEstimate = _sizeEstimateService.EstimateAutoTargetMbSmart(
                item.SourceFilePath,
                profile,
                validated.Resolved.Selection,
                estimateQuality,
                targetHeight,
                audioChannels,
                appliedSavings);
        }

        return fallbackEstimate > 0 ? fallbackEstimate : null;
    }

    public static int? ResolveAudioChannels(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection)) return null;
        if (selection.StartsWith("Stereo", StringComparison.OrdinalIgnoreCase) || selection.Contains("2.0"))
            return 2;
        if (selection.StartsWith("5.1", StringComparison.OrdinalIgnoreCase))
            return 6;
        if (selection.StartsWith("Keep", StringComparison.OrdinalIgnoreCase))
            return null;
        return null;
    }

    public static EncodingService.ScaleMode ParseScaleMode(string? resolution)
    {
        if (string.Equals(resolution?.Trim(), "720p", StringComparison.OrdinalIgnoreCase))
            return EncodingService.ScaleMode.To720p;
        if (string.Equals(resolution?.Trim(), "1080p", StringComparison.OrdinalIgnoreCase))
            return EncodingService.ScaleMode.To1080p;
        if (string.Equals(resolution?.Trim(), "1440p", StringComparison.OrdinalIgnoreCase))
            return EncodingService.ScaleMode.To1440p;
        if (string.Equals(resolution?.Trim(), "4K", StringComparison.OrdinalIgnoreCase))
            return EncodingService.ScaleMode.To4K;
        return EncodingService.ScaleMode.None;
    }

    private static int? EstimateTargetHeight(EncodingService.ScaleMode scaleMode) => scaleMode switch
    {
        EncodingService.ScaleMode.To720p => 720,
        EncodingService.ScaleMode.To1080p => 1080,
        EncodingService.ScaleMode.To1440p => 1440,
        EncodingService.ScaleMode.To4K => 2160,
        _ => null
    };

    private static int? RuntimeOutputHeight(int? sourceHeight, EncodingService.ScaleMode scaleMode)
    {
        if (sourceHeight is not > 0) return null;
        int requested = scaleMode switch
        {
            EncodingService.ScaleMode.To720p => 720,
            EncodingService.ScaleMode.To1080p => 1080,
            EncodingService.ScaleMode.To1440p => 1440,
            EncodingService.ScaleMode.To4K => 2160,
            _ => sourceHeight.Value
        };
        return Math.Min(sourceHeight.Value, requested);
    }

    private static EncodingService.ScaleMode PolicyScaleMode(LibraryPolicyQueueItem item)
    {
        if (item.PreserveSourceResolution || !item.MaximumOutputHeight.HasValue)
            return EncodingService.ScaleMode.None;
        return item.MaximumOutputHeight.Value switch
        {
            <= 720 => EncodingService.ScaleMode.To720p,
            <= 1080 => EncodingService.ScaleMode.To1080p,
            <= 1440 => EncodingService.ScaleMode.To1440p,
            _ => EncodingService.ScaleMode.To4K
        };
    }

    public static OutputContainerSelection ResolveOutputContainer(
        OutputContainerSelection ordinaryRunSelection,
        LibraryPolicyQueueItem? policy,
        EncodingPreset? preset)
    {
        if (policy is null)
            return ordinaryRunSelection;
        if (preset is not null &&
            Enum.TryParse(preset.OutputContainer, true, out OutputContainerSelection selection))
            return selection;
        return policy.TargetContainer;
    }

    public static string BuildOutputSuffix(
        string formatText,
        bool enableOutputSuffix,
        bool enableCodecSuffix,
        string outputSuffix)
    {
        var parts = new List<string>();
        if (enableCodecSuffix)
        {
            string codecLabel = formatText.StartsWith("H.264", StringComparison.Ordinal)
                ? "x264"
                : formatText.StartsWith("H.265", StringComparison.Ordinal)
                    ? "HEVC"
                    : formatText.StartsWith("AV1", StringComparison.Ordinal)
                        ? "AV1"
                        : formatText.Trim();
            if (!string.IsNullOrWhiteSpace(codecLabel))
                parts.Add($"[{codecLabel}]");
        }
        if (enableOutputSuffix)
        {
            string suffix = outputSuffix?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(suffix))
            {
                if (enableCodecSuffix) parts.Add($"[{suffix}]");
                else parts.Add(suffix);
            }
        }
        return parts.Count == 0 ? string.Empty : $" {string.Join(" ", parts)}";
    }

    private static string CodecDisplayName(VideoCodecFamily family) => family switch
    {
        VideoCodecFamily.H264 => "H.264 (x264)",
        VideoCodecFamily.Hevc => "H.265 / HEVC (x265)",
        VideoCodecFamily.Av1 => "AV1",
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };
}
