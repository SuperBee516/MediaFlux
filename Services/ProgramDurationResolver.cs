using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Chooses the program timeline instead of trusting a container-wide end timestamp.</summary>
public static class ProgramDurationResolver
{
    // A subtitle tail of a few frames is harmless; a larger disagreement is a
    // different timeline and must not drive video bitrate or truncation checks.
    private const double MaterialDifferenceSeconds = 2;

    public static ProgramDurationDecision Resolve(MediaProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        MediaProbeStreamInfo? video = probe.Streams.FirstOrDefault(s =>
            s.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
        double? videoDuration = video is null ? null : GetReliableDuration(video);
        if (video is null || videoDuration is not > 0)
            return new ProgramDurationDecision(probe.DurationSeconds, false,
                "No reliable primary video-stream duration was available; using FFprobe container duration.", null,
                FrameCountProvenance.Unavailable);

        double primaryVideoDuration = videoDuration.Value;
        bool nonVideoExtendsContainer = probe.DurationSeconds is > 0 && probe.Streams.Any(stream =>
            IsNonProgramStream(stream) && GetReliableDuration(stream) is double duration &&
            Math.Abs(duration - probe.DurationSeconds.Value) <= MaterialDifferenceSeconds);
        if (probe.DurationSeconds is > 0 && nonVideoExtendsContainer &&
            probe.DurationSeconds.Value - primaryVideoDuration > MaterialDifferenceSeconds)
        {
            return new ProgramDurationDecision(primaryVideoDuration, true,
                $"Container inflated by non-program stream: {probe.DurationSeconds:0.###}s versus primary video {primaryVideoDuration:0.###}s.", video,
                video.FrameCount is > 0 ? FrameCountProvenance.Measured : FrameCountProvenance.InferredFromDurationAndRate);
        }

        if (probe.DurationSeconds is > 0 &&
            Math.Abs(probe.DurationSeconds.Value - primaryVideoDuration) > MaterialDifferenceSeconds)
        {
            return new ProgramDurationDecision(probe.DurationSeconds, false,
                "Container duration disagrees with video, but no extending non-video program stream was identified.", video,
                video.FrameCount is > 0 ? FrameCountProvenance.Measured : FrameCountProvenance.InferredFromDurationAndRate);
        }

        return new ProgramDurationDecision(primaryVideoDuration, false,
            "Primary video-stream duration agrees with the container timeline.", video,
            video.FrameCount is > 0 ? FrameCountProvenance.Measured : FrameCountProvenance.InferredFromDurationAndRate);
    }

    public static double? GetReliableDuration(MediaProbeStreamInfo stream)
    {
        if (stream.DurationSeconds is > 0 && double.IsFinite(stream.DurationSeconds.Value))
            return stream.DurationSeconds.Value;
        if (stream.FrameCount is > 0 && stream.FrameRate is > 0 &&
            double.IsFinite(stream.FrameRate.Value))
            return stream.FrameCount.Value / stream.FrameRate.Value;
        return null;
    }

    private static bool IsNonProgramStream(MediaProbeStreamInfo stream) =>
        stream.CodecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase) ||
        stream.CodecType.Equals("data", StringComparison.OrdinalIgnoreCase) ||
        stream.CodecType.Equals("attachment", StringComparison.OrdinalIgnoreCase);
}

public sealed record ProgramDurationDecision(
    double? DurationSeconds,
    bool UsedVideoFallback,
    string Reason,
    MediaProbeStreamInfo? PrimaryVideo,
    FrameCountProvenance FrameCountProvenance = FrameCountProvenance.Unavailable);
