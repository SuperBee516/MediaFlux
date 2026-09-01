namespace MediaFlux.Services;

/// <summary>Produces a deliberately conservative ETA from observed batched AI throughput.</summary>
public static class AiRestorationProgressEstimator
{
    public const int MinimumCompletedFrames = 12;
    public static readonly TimeSpan MinimumObservation = TimeSpan.FromSeconds(3);

    public static TimeSpan? EstimateRemaining(int completedFrames, int totalFrames, TimeSpan elapsed)
    {
        if (completedFrames < MinimumCompletedFrames || totalFrames <= completedFrames || elapsed < MinimumObservation)
            return null;
        double framesPerSecond = completedFrames / elapsed.TotalSeconds;
        return framesPerSecond > 0 ? TimeSpan.FromSeconds((totalFrames - completedFrames) / framesPerSecond) : null;
    }
}
