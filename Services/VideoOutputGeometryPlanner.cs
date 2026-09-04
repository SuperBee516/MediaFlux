using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>
/// Resolves the exact coded-video geometry before FFmpeg is started. This is
/// intentionally separate from the restoration plan: it applies the output
/// pixel-format and encoder alignment constraints to that requested result.
/// </summary>
public static class VideoOutputGeometryPlanner
{
    public static VideoOutputGeometryPlan Resolve(
        int sourceWidth,
        int sourceHeight,
        VideoOutputResolutionPlan requested,
        VideoEncoderSelection encoder,
        bool tenBit)
    {
        // MediaFlux's H.264/HEVC/AV1 pipelines use 4:2:0 output formats
        // (NV12/P010 or yuv420p/yuv420p10le), all of which require even coded
        // dimensions. Keep this explicit instead of accepting an arbitrary
        // one-pixel difference during validation.
        string pixelFormat = encoder.EncoderId.Equals(VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase) ||
                             encoder.EncoderId.Equals(VideoEncoderIds.Qsv, StringComparison.OrdinalIgnoreCase)
            ? tenBit ? "p010le" : "nv12"
            : tenBit ? "yuv420p10le" : "yuv420p";
        int divisibility = RequiresEvenDimensions(pixelFormat) ? 2 : 1;
        int width = NormalizeUp(requested.Width, divisibility);
        int height = NormalizeUp(requested.Height, divisibility);
        bool normalized = width != requested.Width || height != requested.Height;
        bool requiresScale = normalized || width != sourceWidth || height != sourceHeight;
        string reason = normalized
            ? $"{requested.Reason}; normalized for {pixelFormat} {divisibility}:1 chroma alignment"
            : requested.Reason;

        return new VideoOutputGeometryPlan(
            sourceWidth,
            sourceHeight,
            requested.Width,
            requested.Height,
            width,
            height,
            requiresScale ? $"{width}:{height}" : "",
            pixelFormat,
            divisibility,
            reason);
    }

    private static bool RequiresEvenDimensions(string pixelFormat) =>
        pixelFormat.StartsWith("yuv420", StringComparison.OrdinalIgnoreCase) ||
        pixelFormat.Equals("nv12", StringComparison.OrdinalIgnoreCase) ||
        pixelFormat.Equals("p010le", StringComparison.OrdinalIgnoreCase);

    private static int NormalizeUp(int value, int divisibility) =>
        divisibility <= 1 || value % divisibility == 0
            ? value
            : checked(value + divisibility - value % divisibility);
}

public sealed record VideoOutputGeometryPlan(
    int SourceWidth,
    int SourceHeight,
    int RequestedWidth,
    int RequestedHeight,
    int Width,
    int Height,
    string ScaleExpression,
    string PixelFormat,
    int RequiredDivisibility,
    string Reason)
{
    public bool WasNormalized => Width != RequestedWidth || Height != RequestedHeight;
    public bool RequiresExplicitScale => !string.IsNullOrEmpty(ScaleExpression);
    public string ScaleFilter => RequiresExplicitScale
        ? $"scale={ScaleExpression}:flags=lanczos"
        : "";
    public string Describe() => $"{Width}x{Height} ({Reason})";
}
