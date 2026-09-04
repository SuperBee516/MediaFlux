namespace MediaFlux.Models
{
    public enum OutputContainerSelection
    {
        Auto = 0,
        Matroska = 1,
        Mp4 = 2
    }

    public enum OutputContainer
    {
        Matroska = 1,
        Mp4 = 2
    }

    public enum ContainerCompatibilityPolicy { Intelligent, AlwaysAsk, Strict }
    public enum StreamCompatibilityAction { Copy, Transcode, Omit, Unsupported }

    public sealed record StreamCompatibilityPlan(
        int StreamIndex, string StreamType, string Codec, StreamCompatibilityAction Action,
        string Reason, string? TargetCodec = null, string RequestedAction = "copy",
        string? Language = null, string? Title = null,
        IReadOnlyDictionary<string, bool>? Dispositions = null)
    {
        public bool IsDispositionSet(string name) =>
            Dispositions?.TryGetValue(name, out bool value) == true && value;
    }

    public sealed class OutputContainerDecision
    {
        public OutputContainerSelection Requested { get; init; }
        public OutputContainer Resolved { get; init; }
        public string Reason { get; init; } = "";
        public IReadOnlyList<string> CompatibilityWarnings { get; init; } =
            Array.Empty<string>();
        public bool CopySubtitles { get; init; }
        public bool CopyDataStreams { get; init; }
        public bool CopyAttachments { get; init; }
        public IReadOnlyList<StreamCompatibilityPlan> StreamPlans { get; init; } = Array.Empty<StreamCompatibilityPlan>();
        public bool ConvertSubtitlesToMovText => Resolved == OutputContainer.Mp4 && StreamPlans.Any(plan =>
            plan.StreamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase) &&
            plan.Action == StreamCompatibilityAction.Transcode &&
            plan.TargetCodec == "mov_text");
        public bool TranscodeAudioToAac => StreamPlans.Any(plan =>
            plan.StreamType.Equals("audio", StringComparison.OrdinalIgnoreCase) &&
            plan.Action == StreamCompatibilityAction.Transcode &&
            plan.TargetCodec == "aac");
        public bool HasUnsupportedMeaningfulStreams => StreamPlans.Any(plan =>
            plan.Action == StreamCompatibilityAction.Unsupported &&
            (plan.StreamType.Equals("audio", StringComparison.OrdinalIgnoreCase) || plan.StreamType.Equals("video", StringComparison.OrdinalIgnoreCase) || plan.StreamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase)));

        public string Extension => Resolved == OutputContainer.Matroska ? ".mkv" : ".mp4";
        public string MuxerName => Resolved == OutputContainer.Matroska ? "matroska" : "mp4";
        public bool RequiresConfirmation =>
            Requested == OutputContainerSelection.Mp4 && CompatibilityWarnings.Count > 0;
    }
}
