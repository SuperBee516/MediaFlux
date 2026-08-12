namespace MediaFlux.Models
{
    public enum SmartEncodeContentHint
    {
        Auto,
        LiveAction,
        Animation,
        ScreenContent
    }

    public enum SampledInterlaceStatus
    {
        Unavailable,
        Progressive,
        Mixed,
        Interlaced
    }

    public sealed class DeepMediaAnalysisResult
    {
        public double? ProjectedOutputMb { get; init; }
        public double? ProjectedOutputLowerMb { get; init; }
        public double? ProjectedOutputUpperMb { get; init; }
        public SmartEncodeConfidence ProjectionConfidence { get; init; } =
            SmartEncodeConfidence.Low;
        public int ProjectionSampleCount { get; init; }
        public double SampledMediaSeconds { get; init; }
        public bool UsedProjectionDurationFallback { get; init; }
        public double AverageBitrateKbps { get; init; }
        public double EncodeSpeed { get; init; }
        public TimeSpan EstimatedCompletion { get; init; }
        public SampledInterlaceStatus InterlaceStatus { get; init; }
        public int InterlacedFrames { get; init; }
        public int ProgressiveFrames { get; init; }
        public bool PossibleSyntheticContent { get; init; }
        public int VisualFramesAnalyzed { get; init; }
        public double MedianQuantizedColorCount { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

        public string BuildSummary()
        {
            var lines = new List<string> { "Deep analysis" };
            if (ProjectedOutputMb.HasValue)
            {
                lines.Add($"Sample-projected output: {ProjectedOutputMb.Value:0.#} MB");
                if (ProjectedOutputLowerMb is > 0 && ProjectedOutputUpperMb is > 0)
                {
                    lines.Add(
                        $"Expected range: {ProjectedOutputLowerMb.Value:0.#}–" +
                        $"{ProjectedOutputUpperMb.Value:0.#} MB " +
                        $"({ProjectionConfidence} confidence)");
                }
                lines.Add($"Observed sample bitrate: {AverageBitrateKbps:0} kbps");
                if (ProjectionSampleCount > 0 && SampledMediaSeconds > 0)
                {
                    lines.Add(
                        $"Coverage: {ProjectionSampleCount} sample(s), " +
                        $"{SampledMediaSeconds:0.#} encoded seconds");
                }
                if (EncodeSpeed > 0)
                    lines.Add($"Observed encode speed: {EncodeSpeed:0.##}x");
                if (UsedProjectionDurationFallback)
                    lines.Add("One or more sample durations were unavailable; the range was widened.");
            }

            lines.Add($"Sampled scan type: {InterlaceStatus}");
            if (InterlacedFrames + ProgressiveFrames > 0)
            {
                lines.Add(
                    $"Sampled frames: {InterlacedFrames:N0} interlaced, " +
                    $"{ProgressiveFrames:N0} progressive");
            }

            if (PossibleSyntheticContent)
                lines.Add("Visual samples may be animation or screen content.");

            foreach (string note in Notes.Where(note => !string.IsNullOrWhiteSpace(note)))
                lines.Add($"• {note}");

            return string.Join(Environment.NewLine, lines);
        }
    }
}
