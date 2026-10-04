using MediaFlux.Models;

namespace MediaFlux.Services;

internal sealed record VideoSamplingProfile(VideoSamplePurpose Purpose, int RegionSeconds)
{
    internal static VideoSamplingProfile Production { get; } = new(VideoSamplePurpose.ProductionAcceptance, 25);
    internal static VideoSamplingProfile Preview { get; } = new(VideoSamplePurpose.PreviewEstimation, 5);

    internal IReadOnlyList<RepresentativeSample> Positions(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        if (duration <= TimeSpan.FromSeconds(RegionSeconds))
            return Array.AsReadOnly(new[] { new RepresentativeSample("Full short video", TimeSpan.Zero, duration) });
        int count = duration > TimeSpan.FromSeconds(RegionSeconds * 3) ? 9 : 3;
        long clipTicks = Math.Min(TimeSpan.FromSeconds(RegionSeconds).Ticks, duration.Ticks / count);
        var positions = new List<RepresentativeSample>();
        for (int i = 0; i < count; i++)
        {
            long start = i == 0 ? 0 : i == count - 1 ? duration.Ticks - clipTicks
                : (long)(duration.Ticks * ((i + 0.5) / count)) - clipTicks / 2;
            string label = i == 0 ? "Beginning" : i == count / 2 ? "Middle"
                : i == count - 1 ? "End" : $"Temporal region {i + 1}/{count}";
            positions.Add(new(label, TimeSpan.FromTicks(start), TimeSpan.FromTicks(clipTicks)));
        }
        return positions.AsReadOnly();
    }
}
