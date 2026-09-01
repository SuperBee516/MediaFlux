namespace MediaFlux.Models;

public sealed record AiFrameTimingEntry(int FrameIndex, long PresentationTimestamp, double PresentationSeconds, double DurationSeconds, string SourceTimeBase, string InputFileName, string OutputFileName);
public sealed record AiTimestampManifest(string SourceTimeBase, IReadOnlyList<AiFrameTimingEntry> Frames)
{
    public double DurationSeconds => Frames.Count == 0 ? 0 : Frames[^1].PresentationSeconds + Frames[^1].DurationSeconds - Frames[0].PresentationSeconds;
}
public sealed record AiTimestampValidationResult(bool IsValid, string Reason, double DurationDelta, bool IsMonotonic, bool HasMatchingFrameIdentity);
