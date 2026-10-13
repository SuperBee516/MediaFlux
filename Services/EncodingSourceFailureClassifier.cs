using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Conservative, source-focused classification of FFmpeg media failures.</summary>
internal static class EncodingSourceFailureClassifier
{
    public static EncodingSourceFailureClassification Classify(string? diagnostic)
    {
        string text = diagnostic ?? "";
        if (Contains(text, "Operation canceled", "Operation cancelled", "cancellation requested")) return new(EncodingSourceFailureType.Cancellation, false, Evidence(text, "cancellation"), "Cancellation is not source corruption.");
        if (FfmpegStorageFailureClassifier.Classify(text).IsReliable) return new(EncodingSourceFailureType.StorageOrIoFailure, false, Evidence(text, "storage"), "Destination storage or I/O failure is not source corruption.");
        if (FfmpegNvencFailureClassifier.Classify(text).IsReliable || FfmpegCudaNvdecFailureClassifier.Classify(text).IsCudaNvdecDeviceFailure) return new(EncodingSourceFailureType.GpuEncoderFailure, false, Evidence(text, "gpu"), "GPU/NVENC failure is not source corruption.");
        if (FfmpegSourceTruncationClassifier.Classify(text).IsReliable) return new(EncodingSourceFailureType.SourceTruncation, false, Evidence(text, "truncation"), "Source truncation is not eligible for repair.");
        if (Contains(text, "Unknown encoder", "Unsupported codec", "could not find tag for codec")) return new(EncodingSourceFailureType.UnsupportedCodec, false, Evidence(text, "unsupported-codec"), "Unsupported codec is not source corruption.");
        if (Contains(text, "Error muxing", "Error writing trailer", "Could not write header")) return new(EncodingSourceFailureType.OutputContainerFailure, false, Evidence(text, "mux"), "Mux/output-container failure is not source corruption.");
        if (Contains(text, "non-monotonous dts", "non-monotonic", "discontinuous presentation timestamps", "material presentation-timestamp gap")) return new(EncodingSourceFailureType.TimelineCorruption, true, Evidence(text, "timeline"), "Source timestamps are discontinuous or non-monotonic.");
        FfmpegSourceDecodeCorruption video = FfmpegSourceDecodeCorruptionClassifier.Classify(text);
        if (video.IsReliable) return new(EncodingSourceFailureType.VideoBitstreamCorruption, true, string.Join(" | ", video.MatchedEvidence), "Specific source video bitstream corruption was reported.");
        if (SourceAudioDecodePreflightService.IsReliableAudioDecodeFailure(text)) return new(EncodingSourceFailureType.AudioBitstreamCorruption, true, Evidence(text, "audio"), "Specific source audio decode corruption was reported.");
        if (Contains(text, "moov atom not found", "invalid as first byte of an ebml number", "could not find codec parameters")) return new(EncodingSourceFailureType.ContainerPacketCorruption, false, Evidence(text, "container"), "Container corruption requires an existing specialized recovery policy.");
        return new(EncodingSourceFailureType.UnknownMediaFailure, false, "", "The media failure could not be classified conservatively.");
    }

    private static bool Contains(string text, params string[] terms) => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    private static string Evidence(string text, string kind) => $"classified={kind}; diagnostic={text.Replace(Environment.NewLine, " ").Trim()}";
}
