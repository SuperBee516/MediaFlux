namespace MediaFlux.Models;

public enum EncodeFailureCategory
{
    Input,
    Decoder,
    VideoEncoder,
    Audio,
    Subtitle,
    Output,
    Restoration,
    Unknown
}

public enum EncodeFailureConfidence
{
    High,
    Moderate,
    Low
}

public sealed record EncodeFailurePlanContext(
    string Encoder,
    string VideoCodec,
    string EncoderPreset,
    string BitDepth,
    string OutputContainer,
    string Restoration);

/// <summary>Read-only, deterministic explanation of a failed encode attempt.</summary>
public sealed record EncodeFailureAnalysis(
    EncodeFailureCategory Category,
    string FailureStage,
    string Summary,
    string LikelyCause,
    string RecommendedAction,
    string TechnicalDetail,
    EncodeFailureConfidence Confidence,
    EncodeFailurePlanContext? PlanContext)
{
    public string CategoryLabel => Category switch
    {
        EncodeFailureCategory.Input => "Input / source",
        EncodeFailureCategory.Decoder => "Decoder",
        EncodeFailureCategory.VideoEncoder => "Video encoder",
        EncodeFailureCategory.Audio => "Audio",
        EncodeFailureCategory.Subtitle => "Subtitle",
        EncodeFailureCategory.Output => "Output / muxing",
        EncodeFailureCategory.Restoration => "Restoration / preprocessing",
        _ => "Unknown"
    };
}
