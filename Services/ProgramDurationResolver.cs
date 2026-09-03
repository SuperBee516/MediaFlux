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
            s.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase) &&
            s.DurationSeconds is > 0);
        if (video?.DurationSeconds is not > 0)
            return new ProgramDurationDecision(probe.DurationSeconds, false,
                "No reliable primary video-stream duration was available; using FFprobe container duration.", null);

        double videoDuration = video.DurationSeconds.Value;
        bool nonVideoExtendsContainer = probe.DurationSeconds is > 0 && probe.Streams.Any(stream =>
            !stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase) &&
            stream.DurationSeconds is > 0 &&
            Math.Abs(stream.DurationSeconds.Value - probe.DurationSeconds.Value) <= MaterialDifferenceSeconds);
        if (probe.DurationSeconds is > 0 && nonVideoExtendsContainer &&
            probe.DurationSeconds.Value - videoDuration > MaterialDifferenceSeconds)
        {
            return new ProgramDurationDecision(videoDuration, true,
                $"Container duration {probe.DurationSeconds:0.###}s exceeds primary video stream {videoDuration:0.###}s by {probe.DurationSeconds.Value - videoDuration:0.###}s.", video);
        }

        if (probe.DurationSeconds is > 0 &&
            Math.Abs(probe.DurationSeconds.Value - videoDuration) > MaterialDifferenceSeconds)
        {
            return new ProgramDurationDecision(probe.DurationSeconds, false,
                "Container duration disagrees with video, but no extending non-video program stream was identified.", video);
        }

        return new ProgramDurationDecision(videoDuration, false,
            "Primary video-stream duration agrees with the container timeline.", video);
    }
}

public sealed record ProgramDurationDecision(
    double? DurationSeconds,
    bool UsedVideoFallback,
    string Reason,
    MediaProbeStreamInfo? PrimaryVideo);
