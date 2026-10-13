using MediaFlux.Models;
using MediaFlux.Services.Encoders;

namespace MediaFlux.Services;

/// <summary>
/// Deterministic metadata-only target-rate planner for the restored Automatic
/// workflow. It deliberately makes no output-quality guarantee.
/// </summary>
public static class AutomaticTargetPlanner
{
    private const double BitsPerMegabyteKbpsSecond = 8388.608d;
    private const double BytesPerKbpsSecond = 125d;

    public static AutomaticTargetPlan Create(
        MediaProbeResult source,
        EncodingInputSource input,
        VideoEncoderSelection encoder,
        VideoOutputGeometryPlan? geometry,
        TimeSpan duration,
        QualityTarget qualityTarget,
        int? audioChannels,
        OutputContainerDecision container)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(container);

        AutomaticTargetPlan Unavailable(string reason) => new()
        {
            Disposition = AutomaticTargetDisposition.Unavailable,
            QualityTarget = qualityTarget,
            Reason = reason
        };

        MediaProbeStreamInfo? video = source.Streams.FirstOrDefault(stream =>
            stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
        if (!source.Success || video is null)
            return Unavailable("FFprobe did not provide a usable source video stream.");

        double seconds = duration > TimeSpan.Zero
            ? duration.TotalSeconds
            : input.KnownDurationSeconds is > 0
                ? input.KnownDurationSeconds.Value
                : source.DurationSeconds.GetValueOrDefault();
        if (!double.IsFinite(seconds) || seconds <= 0)
            return Unavailable("A positive source duration is required for target planning.");

        int width = video.Width.GetValueOrDefault();
        int height = video.Height.GetValueOrDefault();
        double fps = FirstPositive(video.FrameRate, video.AverageFrameRate, video.NominalFrameRate);
        if (width <= 0 || height <= 0 || !double.IsFinite(fps) || fps <= 0)
            return Unavailable("FFprobe did not provide valid source dimensions and frame rate.");
        if (geometry is not { Width: > 0, Height: > 0 })
            return Unavailable("The selected encoder could not resolve valid output geometry.");

        long sourceBytes = source.SizeBytes.GetValueOrDefault();
        if (sourceBytes <= 0 && input.Kind == EncodingInputKind.File)
        {
            try { sourceBytes = new FileInfo(input.SourcePath).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        if (sourceBytes <= 0)
            return Unavailable("Source size is unavailable; no output projection can be calculated.");

        double sourceTotalKbps = sourceBytes * 8d / 1000d / seconds;
        if (!double.IsFinite(sourceTotalKbps) || sourceTotalKbps <= 0)
            return Unavailable("Source size and duration do not produce a valid total bitrate.");

        MediaProbeStreamInfo[] audioStreams = source.Streams.Where(stream =>
            stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase)).ToArray();
        MediaProbeStreamInfo[] outputAudioStreams = PlannedStreams(container, input, "audio")
            .Where(plan => plan.Action is StreamCompatibilityAction.Copy or StreamCompatibilityAction.Transcode)
            .Select(plan => audioStreams.FirstOrDefault(stream => stream.Index == plan.StreamIndex))
            .Where(stream => stream is not null).Cast<MediaProbeStreamInfo>().ToArray();

        double measuredAudioKbps = audioStreams.Sum(stream =>
            stream.BitRate is > 0 ? stream.BitRate.Value / 1000d : 0d);
        int unknownSourceAudioCount = audioStreams.Count(stream => stream.BitRate is not > 0);
        if (unknownSourceAudioCount > 0 && input.KnownAudioBitrateKbps is > 0)
            measuredAudioKbps = Math.Max(measuredAudioKbps, input.KnownAudioBitrateKbps.Value);
        if (measuredAudioKbps <= 0 && audioStreams.Length > 0)
            measuredAudioKbps = audioStreams.Length * DefaultAudioBudget(audioChannels);

        double plannedAudioKbps = 0;
        foreach (var plan in PlannedStreams(container, input, "audio")
                     .Where(plan => plan.Action is StreamCompatibilityAction.Copy or StreamCompatibilityAction.Transcode))
        {
            MediaProbeStreamInfo? stream = audioStreams.FirstOrDefault(value => value.Index == plan.StreamIndex);
            if (plan.Action == StreamCompatibilityAction.Transcode)
            {
                plannedAudioKbps += DefaultAudioBudget(audioChannels);
            }
            else if (stream?.BitRate is > 0)
            {
                plannedAudioKbps += stream.BitRate.Value / 1000d;
            }
            else if (input.KnownAudioBitrateKbps is > 0 && outputAudioStreams.Length > 0)
            {
                plannedAudioKbps += input.KnownAudioBitrateKbps.Value / outputAudioStreams.Length;
            }
            else
            {
                plannedAudioKbps += DefaultAudioBudget(audioChannels);
            }
        }

        double sourceAncillaryKbps = 0;
        foreach (MediaProbeStreamInfo stream in source.Streams.Where(stream =>
                     stream.CodecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase) ||
                     stream.CodecType.Equals("data", StringComparison.OrdinalIgnoreCase)))
        {
            if (container.StreamPlans.Any(plan => plan.StreamIndex == stream.Index &&
                plan.Action is StreamCompatibilityAction.Copy or StreamCompatibilityAction.Transcode))
                sourceAncillaryKbps += StreamBitrateKbps(stream, 8d);
        }

        double plannedAncillaryKbps = 0;
        foreach (var plan in container.StreamPlans.Where(plan =>
                     (plan.StreamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase) ||
                      plan.StreamType.Equals("data", StringComparison.OrdinalIgnoreCase)) &&
                     plan.Action is StreamCompatibilityAction.Copy or StreamCompatibilityAction.Transcode))
        {
            MediaProbeStreamInfo? stream = source.Streams.FirstOrDefault(value => value.Index == plan.StreamIndex);
            plannedAncillaryKbps += stream is null ? 8d : StreamBitrateKbps(stream, 8d);
        }

        double sourceAttachmentKbps = 0;
        double plannedAttachmentKbps = 0;
        if (container.CopyAttachments)
        {
            MediaProbeStreamInfo[] attachments = source.Streams.Where(stream =>
                stream.CodecType.Equals("attachment", StringComparison.OrdinalIgnoreCase)).ToArray();
            long attachmentBytes = attachments.Sum(stream => Math.Max(0, stream.ExtraDataSizeBytes.GetValueOrDefault()));
            sourceAttachmentKbps = attachmentBytes > 0
                ? attachmentBytes * 8d / 1000d / seconds
                : attachments.Length * 8d;
            plannedAttachmentKbps = sourceAttachmentKbps;
        }

        sourceAncillaryKbps += sourceAttachmentKbps;
        if (sourceAncillaryKbps <= 0 && input.KnownMappedAncillaryBitrateKbps > 0)
            sourceAncillaryKbps = input.KnownMappedAncillaryBitrateKbps;
        if (plannedAncillaryKbps <= 0 && input.KnownMappedAncillaryBitrateKbps > 0)
            plannedAncillaryKbps = input.KnownMappedAncillaryBitrateKbps;
        plannedAncillaryKbps += plannedAttachmentKbps;

        double sourceOverheadKbps = Math.Max(16d, sourceTotalKbps * 0.01d);
        double measuredVideoKbps = video.BitRate is > 0
            ? video.BitRate.Value / 1000d
            : sourceTotalKbps - measuredAudioKbps - sourceAncillaryKbps - sourceOverheadKbps;
        if (!double.IsFinite(measuredVideoKbps) || measuredVideoKbps <= 0)
            return Unavailable("Source video bitrate is missing and cannot be inferred from the remaining source bitrate.");
        if (measuredVideoKbps > sourceTotalKbps * 1.05d)
            return Unavailable("FFprobe video bitrate is inconsistent with source size and duration.");

        double sourcePixelsPerSecond = (double)width * height * fps;
        double outputPixelsPerSecond = (double)geometry.Width * geometry.Height * fps;
        if (!double.IsFinite(sourcePixelsPerSecond) || sourcePixelsPerSecond <= 0 ||
            !double.IsFinite(outputPixelsPerSecond) || outputPixelsPerSecond <= 0)
            return Unavailable("Source or output geometry exceeds valid numeric bounds.");

        double measuredSourceBpp = measuredVideoKbps * 1000d / sourcePixelsPerSecond;
        double sourceCodecBpp = CodecReferenceBpp(video.CodecName);
        double complexity = Math.Clamp(Math.Sqrt(measuredSourceBpp / sourceCodecBpp), 0.55d, 1.65d);
        double targetCodecBpp = CodecReferenceBpp(encoder.FfmpegCodec);
        double qualityScale = QualityScale(qualityTarget);
        double targetVideoKbps = outputPixelsPerSecond * targetCodecBpp * complexity * qualityScale / 1000d;
        if (!double.IsFinite(targetVideoKbps) || targetVideoKbps <= 0)
            return Unavailable("The source-aware target bitrate calculation did not produce a valid positive rate.");

        double targetOutputBudgetKbps = sourceTotalKbps * qualityScale;
        double budgetOverheadKbps = Math.Max(16d, targetOutputBudgetKbps * 0.01d);
        double availableVideoBudgetKbps = targetOutputBudgetKbps - plannedAudioKbps -
            plannedAncillaryKbps - budgetOverheadKbps;
        double minimumVideoKbps = Math.Max(100d, outputPixelsPerSecond * 0.008d / 1000d);
        if (!double.IsFinite(availableVideoBudgetKbps) || availableVideoBudgetKbps < minimumVideoKbps)
            return Unavailable("Planned audio and ancillary streams consume too much of the selected output bitrate budget to leave a safe video rate.");
        targetVideoKbps = Math.Min(targetVideoKbps, availableVideoBudgetKbps);

        bool lowBitrateEfficientSource =
            (IsHevc(video.CodecName) || IsAv1(video.CodecName)) &&
            IsEfficientCodec(encoder.FfmpegCodec) &&
            geometry.Width == width && geometry.Height == height &&
            measuredSourceBpp < (IsAv1(video.CodecName) ? 0.025d : 0.035d);
        if (lowBitrateEfficientSource)
            targetVideoKbps = Math.Max(targetVideoKbps, measuredVideoKbps * 0.90d);

        if (targetVideoKbps < minimumVideoKbps)
            return Unavailable($"The calculated video target {targetVideoKbps:0.##} kbps is below the {minimumVideoKbps:0.##} kbps encoding floor.");

        double subtotalKbps = targetVideoKbps + plannedAudioKbps + plannedAncillaryKbps;
        double containerOverheadKbps = Math.Max(16d, subtotalKbps * 0.01d);
        double totalKbps = subtotalKbps + containerOverheadKbps;
        double outputBytes = totalKbps * seconds * BytesPerKbpsSecond;
        if (!double.IsFinite(totalKbps) || totalKbps <= 0 || !double.IsFinite(outputBytes) ||
            outputBytes < 1 || outputBytes > long.MaxValue)
            return Unavailable("The planned stream and container budget exceeds representable output-size bounds.");

        double maxRateKbps = Math.Round(targetVideoKbps * 1.08d, MidpointRounding.AwayFromZero);
        double bufferSizeKbps = Math.Round(targetVideoKbps * 1.40d, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(maxRateKbps) || !double.IsFinite(bufferSizeKbps) ||
            maxRateKbps < targetVideoKbps || bufferSizeKbps < targetVideoKbps)
            return Unavailable("The calculated maximum bitrate or VBV buffer is invalid.");

        long projectedBytes = checked((long)Math.Round(outputBytes, MidpointRounding.AwayFromZero));
        bool projectedUnfavorable = projectedBytes >= sourceBytes || totalKbps > targetOutputBudgetKbps;
        string reason = lowBitrateEfficientSource
            ? "Already-efficient low-bitrate HEVC/AV1 source: video reduction is limited to 10%; review the projected output before encoding."
            : projectedUnfavorable
                ? "The metadata projection is not smaller than the source; review or skip this encode."
                : $"Ready: {qualityTarget} maps to a source-aware target bitrate.";

        if ((plannedAudioKbps + plannedAncillaryKbps) >= totalKbps)
            return Unavailable("Mapped audio and ancillary streams consume the target output budget; no safe video bitrate remains.");

        return new AutomaticTargetPlan
        {
            Disposition = lowBitrateEfficientSource || projectedUnfavorable
                ? AutomaticTargetDisposition.Review
                : AutomaticTargetDisposition.Ready,
            QualityTarget = qualityTarget,
            SourceVideoBitrateKbps = measuredVideoKbps,
            TargetVideoBitrateKbps = targetVideoKbps,
            MaximumVideoBitrateKbps = maxRateKbps,
            VideoBufferSizeKbps = bufferSizeKbps,
            PlannedAudioBitrateKbps = plannedAudioKbps,
            PlannedMappedAncillaryBitrateKbps = plannedAncillaryKbps,
            ContainerOverheadBitrateKbps = containerOverheadKbps,
            TargetOutputBudgetKbps = targetOutputBudgetKbps,
            TargetTotalBitrateKbps = totalKbps,
            ProjectedOutputBytes = projectedBytes,
            Reason = reason
        };
    }

    public static double QualityScale(QualityTarget target) => target switch
    {
        QualityTarget.SmallerFile => 0.78d,
        QualityTarget.Efficient => 0.90d,
        QualityTarget.Balanced => 1.00d,
        QualityTarget.HighQuality => 1.12d,
        QualityTarget.MaximumQuality => 1.25d,
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private static IEnumerable<StreamCompatibilityPlan> PlannedStreams(
        OutputContainerDecision container,
        EncodingInputSource input,
        string streamType)
    {
        IEnumerable<StreamCompatibilityPlan> plans = container.StreamPlans.Where(plan =>
            plan.StreamType.Equals(streamType, StringComparison.OrdinalIgnoreCase));
        if (input.AudioStreamIndexes.Count > 0 && streamType.Equals("audio", StringComparison.OrdinalIgnoreCase))
            plans = plans.Where(plan => input.AudioStreamIndexes.Contains(plan.StreamIndex));
        if (input.SubtitleStreamIndexes.Count > 0 && streamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase))
            plans = plans.Where(plan => input.SubtitleStreamIndexes.Contains(plan.StreamIndex));
        return plans;
    }

    private static double DefaultAudioBudget(int? channels) => channels is >= 6 ? 384d : 192d;

    private static double StreamBitrateKbps(MediaProbeStreamInfo stream, double fallbackKbps) =>
        stream.BitRate is > 0 ? stream.BitRate.Value / 1000d : fallbackKbps;

    private static double FirstPositive(params double?[] candidates) =>
        candidates.FirstOrDefault(value => value is > 0 && double.IsFinite(value.Value)) ?? 0;

    private static bool IsHevc(string? codec) =>
        codec?.Contains("hevc", StringComparison.OrdinalIgnoreCase) == true ||
        codec?.Contains("h265", StringComparison.OrdinalIgnoreCase) == true ||
        codec?.Contains("x265", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsAv1(string? codec) =>
        codec?.Contains("av1", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsEfficientCodec(string? codec) => IsHevc(codec) || IsAv1(codec);

    private static double CodecReferenceBpp(string? codec)
    {
        if (IsAv1(codec)) return 0.045d;
        if (IsHevc(codec)) return 0.055d;
        if (codec?.Contains("h264", StringComparison.OrdinalIgnoreCase) == true ||
            codec?.Contains("avc", StringComparison.OrdinalIgnoreCase) == true ||
            codec?.Contains("x264", StringComparison.OrdinalIgnoreCase) == true)
            return 0.085d;
        if (codec?.Contains("mpeg2", StringComparison.OrdinalIgnoreCase) == true)
            return 0.14d;
        return 0.10d;
    }
}
