using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>
/// Pins a damaged, known stereo stream to its source channel order during
/// tolerant decode. A corrupt AAC frame can advertise an impossible channel
/// layout and make FFmpeg's automatic rematrix filter abort the whole encode.
/// </summary>
internal static class TolerantAudioRecoveryFilterPolicy
{
    internal const string StereoPassThroughFilter = "pan=stereo|c0=c0|c1=c1";

    public static IReadOnlyDictionary<int, string> BuildOverrides(
        MediaProbeResult sourceProbe,
        OutputContainerDecision recoveredContainerDecision,
        int damagedAudioStreamIndex,
        int? requestedAudioChannels)
    {
        ArgumentNullException.ThrowIfNull(sourceProbe);
        ArgumentNullException.ThrowIfNull(recoveredContainerDecision);

        // Do not override a user's explicit mono or surround output request.
        if (requestedAudioChannels is > 0 and not 2)
            return Empty;

        MediaProbeStreamInfo? sourceStream = sourceProbe.Streams.FirstOrDefault(stream =>
            stream.Index == damagedAudioStreamIndex &&
            stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase));
        if (sourceStream?.Channels != 2 ||
            !sourceStream.ChannelLayout.Equals("stereo", StringComparison.OrdinalIgnoreCase))
            return Empty;

        int outputAudioIndex = 0;
        foreach (StreamCompatibilityPlan plan in recoveredContainerDecision.StreamPlans.Where(plan =>
                     plan.StreamType.Equals("audio", StringComparison.OrdinalIgnoreCase) &&
                     plan.Action is StreamCompatibilityAction.Copy or StreamCompatibilityAction.Transcode))
        {
            if (plan.StreamIndex == damagedAudioStreamIndex &&
                plan.Action == StreamCompatibilityAction.Transcode)
            {
                return new Dictionary<int, string>
                {
                    [outputAudioIndex] = StereoPassThroughFilter
                };
            }

            outputAudioIndex++;
        }

        return Empty;
    }

    private static IReadOnlyDictionary<int, string> Empty { get; } =
        new Dictionary<int, string>();
}
