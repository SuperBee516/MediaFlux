using System.Collections.Concurrent;
using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed record AiTemporaryStorageEstimate(long EstimatedBytes, long AvailableBytes, bool IsClearlyInsufficient, bool IsBorderline, long BaseEstimatedBytes, long ChunkEstimatedBytesPerFrame,
    long EstimatedPeakExtractedBytes = 0, long EstimatedPeakRestoredBytes = 0, long EstimatedIntermediateBytes = 0, long ActiveWorkingFilesBytes = 0, long SafetyMarginBytes = 0, long RawWorkingSetBytesPerFrame = 0)
{
    public long PeakWorkingSetBytes => SaturatingAdd(SaturatingAdd(EstimatedPeakExtractedBytes, EstimatedPeakRestoredBytes), SaturatingAdd(EstimatedIntermediateBytes, ActiveWorkingFilesBytes));
    public int MaximumSafeChunkFrames => RawWorkingSetBytesPerFrame > 0
        ? (int)Math.Clamp((AvailableBytes * 100L / AiProductionHardeningService.SafetyMarginPercent - ActiveWorkingFilesBytes) / RawWorkingSetBytesPerFrame, 0, AiChunkPlanner.MaximumFramesPerChunk)
        : ChunkEstimatedBytesPerFrame <= 0 ? AiChunkPlanner.MaximumFramesPerChunk
        : (int)Math.Clamp((AvailableBytes - BaseEstimatedBytes) / ChunkEstimatedBytesPerFrame, 0, AiChunkPlanner.MaximumFramesPerChunk);
    public string Describe() => $"Estimated peak AI temporary storage: {Format(EstimatedBytes)}; Available: {Format(AvailableBytes)}.";
    private static string Format(long bytes) => $"{bytes / 1024d / 1024d / 1024d:0.0} GB";
    private static long SaturatingAdd(long value, long addition) => addition > 0 && value > long.MaxValue - addition ? long.MaxValue : value + Math.Max(0, addition);
}

/// <summary>Centralized production preflight and conservative MediaFlux-owned staging maintenance.</summary>
public static class AiProductionHardeningService
{
    public const int SafetyMarginPercent = 135;
    private const long ActiveWorkingFilesReserveBytes = 1L * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, byte> ActiveRoots = new(StringComparer.OrdinalIgnoreCase);
    public static AiTemporaryStorageEstimate Estimate(int width, int height, int frames, AiRestorationScale scale, string stagingRoot, int chunkFrames, long? availableBytesOverride = null)
    {
        _ = frames; // Completed chunks delete their frame working directories; duration is not part of the peak working set.
        long sourcePixels = checked((long)Math.Max(64, width) * Math.Max(64, height));
        long restoredPixels = checked(sourcePixels * (int)scale * (int)scale);
        int boundedChunkFrames = Math.Clamp(chunkFrames, AiChunkPlanner.MinimumFramesPerChunk, AiChunkPlanner.MaximumFramesPerChunk);
        long extractedPerFrame = checked(sourcePixels * 4);
        long restoredPerFrame = checked(restoredPixels * 4);
        long intermediatePerFrame = checked(restoredPixels * 3); // FFV1 chunk video, before lossless compression.
        long rawPerFrame = checked(extractedPerFrame + restoredPerFrame + intermediatePerFrame);
        long extracted = checked(extractedPerFrame * boundedChunkFrames);
        long restored = checked(restoredPerFrame * boundedChunkFrames);
        long intermediate = checked(intermediatePerFrame * boundedChunkFrames);
        long peakWorkingSet = checked(extracted + restored + intermediate + ActiveWorkingFilesReserveBytes);
        long estimate = CeilingPercent(peakWorkingSet, SafetyMarginPercent);
        long safetyMargin = estimate - peakWorkingSet;
        long available = availableBytesOverride ?? new DriveInfo(Path.GetPathRoot(Path.GetFullPath(stagingRoot))!).AvailableFreeSpace;
        long perFrameWithMargin = CeilingPercent(rawPerFrame, SafetyMarginPercent);
        return new(estimate, available, available < estimate, available < CeilingPercent(estimate, 120),
            ActiveWorkingFilesReserveBytes, perFrameWithMargin, extracted, restored, intermediate, ActiveWorkingFilesReserveBytes, safetyMargin, rawPerFrame);
    }
    private static long CeilingPercent(long bytes, int percent) => checked((bytes * percent + 99) / 100);
    public static void EnsureSpace(AiTemporaryStorageEstimate estimate, bool runtime = false)
    { if (estimate.IsClearlyInsufficient) throw new AiRestorationValidationException((runtime ? "AI processing stopped: " : "AI preflight failed: ") + "insufficient temporary storage. " + estimate.Describe()); }
    public static string ClassifyBackendFailure(string detail) => detail.Contains("vulkan", StringComparison.OrdinalIgnoreCase) || detail.Contains("gpu", StringComparison.OrdinalIgnoreCase) ? "AI backend failed to initialize the selected Vulkan GPU/device." : detail.Contains("memory", StringComparison.OrdinalIgnoreCase) || detail.Contains("alloc", StringComparison.OrdinalIgnoreCase) ? "AI backend likely ran out of GPU resources." : "AI backend process failed; review MediaFlux diagnostics.";
    public static void Register(string root) => ActiveRoots.TryAdd(root, 0);
    public static void Unregister(string root) => ActiveRoots.TryRemove(root, out _);
    public static bool IsActive(string root) => ActiveRoots.ContainsKey(Path.GetFullPath(root));
    public static int CleanupOrphans(string stagingRoot, Action<string>? log = null, TimeSpan? age = null)
    {
        if (!Directory.Exists(stagingRoot)) return 0; int removed = 0; DateTime cutoff = DateTime.UtcNow - (age ?? TimeSpan.FromDays(2));
        foreach (string path in Directory.EnumerateDirectories(stagingRoot, "ai-intermediate-*")) try { if (!ActiveRoots.ContainsKey(path) && Directory.GetLastWriteTimeUtc(path) < cutoff) { Directory.Delete(path, true); removed++; log?.Invoke($"[AI Restoration] Removed abandoned staging directory: {path}"); } } catch { }
        return removed;
    }
}
