using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Chooses one bounded, deterministic frame batch size for an AI restoration operation.</summary>
public sealed class AiChunkPlanner
{
    public const int MinimumFramesPerChunk = 60;
    public const int MaximumFramesPerChunk = 720;

    public AiChunkPlan Plan(AiChunkPlannerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        int pixels = checked(Math.Max(1, input.SourceWidth) * Math.Max(1, input.SourceHeight));
        int planned = pixels <= 640 * 480 ? 720
            : pixels <= 1280 * 720 ? 360
            : pixels <= 1920 * 1080 ? 180
            : pixels >= 3840 * 2160 ? 60
            : 120;
        var reasons = new List<string> { ResolutionReason(pixels) };

        if (input.AiScale == AiRestorationScale.X4) { planned /= 2; reasons.Add("4x scale"); }
        else if (input.AiScale == AiRestorationScale.X3) { planned = planned * 2 / 3; reasons.Add("3x scale"); }

        if (input.DedicatedGpuVramBytes is not long vram)
        {
            planned = Math.Min(planned, 180);
            reasons.Add("GPU VRAM unavailable");
        }
        else if (vram < 4L * 1024 * 1024 * 1024)
        {
            planned = Math.Min(planned, 90);
            reasons.Add("low GPU VRAM");
        }
        else if (vram < 8L * 1024 * 1024 * 1024)
        {
            planned = Math.Min(planned, 180);
            reasons.Add("moderate GPU VRAM");
        }
        else if (vram >= 12L * 1024 * 1024 * 1024)
            reasons.Add("high GPU VRAM");

        int storageLimit = input.TemporaryStorageEstimate.MaximumSafeChunkFrames;
        if (storageLimit >= MinimumFramesPerChunk && storageLimit < planned)
        {
            planned = storageLimit;
            reasons.Add("temporary storage limit");
        }
        else if (storageLimit < MinimumFramesPerChunk)
            reasons.Add("temporary storage below minimum; existing preflight will reject it");

        planned = Math.Clamp(planned, MinimumFramesPerChunk, MaximumFramesPerChunk);
        return new AiChunkPlan(planned, string.Join("; ", reasons));
    }

    private static string ResolutionReason(int pixels) => pixels <= 640 * 480 ? "SD source"
        : pixels <= 1280 * 720 ? "720p-class source"
        : pixels <= 1920 * 1080 ? "1080p-class source"
        : pixels >= 3840 * 2160 ? "4K-or-larger source"
        : "above-1080p source";
}

public sealed record AiChunkPlannerInput(
    int SourceWidth,
    int SourceHeight,
    AiRestorationScale AiScale,
    long? DedicatedGpuVramBytes,
    AiTemporaryStorageEstimate TemporaryStorageEstimate,
    string BackendIdentity);

public sealed record AiChunkPlan(int FrameCount, string DecisionReason);
