using System.Collections.Concurrent;
using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed record AiTemporaryStorageEstimate(long EstimatedBytes, long AvailableBytes, bool IsClearlyInsufficient, bool IsBorderline)
{ public string Describe() => $"Estimated AI temporary storage: {Format(EstimatedBytes)}; Available: {Format(AvailableBytes)}."; private static string Format(long bytes) => $"{bytes / 1024d / 1024d / 1024d:0.0} GB"; }

/// <summary>Centralized production preflight and conservative MediaFlux-owned staging maintenance.</summary>
public static class AiProductionHardeningService
{
    private static readonly ConcurrentDictionary<string, byte> ActiveRoots = new(StringComparer.OrdinalIgnoreCase);
    public static AiTemporaryStorageEstimate Estimate(int width, int height, int frames, AiRestorationScale scale, string stagingRoot)
    {
        long pixels = checked((long)Math.Max(64, width) * Math.Max(64, height) * (int)scale * (int)scale);
        long chunk = checked(pixels * 4 * AiRestorationFrameProcessor.MaximumFramesPerChunk * 3);
        long intermediate = checked(pixels * 3 * Math.Max(1, frames));
        long estimate = checked((long)((chunk + intermediate) * 1.35));
        long available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(stagingRoot))!).AvailableFreeSpace;
        return new(estimate, available, available < estimate / 2, available < estimate);
    }
    public static void EnsureSpace(AiTemporaryStorageEstimate estimate, bool runtime = false)
    { if (estimate.IsClearlyInsufficient) throw new AiRestorationValidationException((runtime ? "AI processing stopped: " : "AI preflight failed: ") + "insufficient temporary storage. " + estimate.Describe()); }
    public static string ClassifyBackendFailure(string detail) => detail.Contains("vulkan", StringComparison.OrdinalIgnoreCase) || detail.Contains("gpu", StringComparison.OrdinalIgnoreCase) ? "AI backend failed to initialize the selected Vulkan GPU/device." : detail.Contains("memory", StringComparison.OrdinalIgnoreCase) || detail.Contains("alloc", StringComparison.OrdinalIgnoreCase) ? "AI backend likely ran out of GPU resources." : "AI backend process failed; review MediaFlux diagnostics.";
    public static void Register(string root) => ActiveRoots.TryAdd(root, 0);
    public static void Unregister(string root) => ActiveRoots.TryRemove(root, out _);
    public static int CleanupOrphans(string stagingRoot, Action<string>? log = null, TimeSpan? age = null)
    {
        if (!Directory.Exists(stagingRoot)) return 0; int removed = 0; DateTime cutoff = DateTime.UtcNow - (age ?? TimeSpan.FromDays(2));
        foreach (string path in Directory.EnumerateDirectories(stagingRoot, "ai-intermediate-*")) try { if (!ActiveRoots.ContainsKey(path) && Directory.GetLastWriteTimeUtc(path) < cutoff) { Directory.Delete(path, true); removed++; log?.Invoke($"[AI Restoration] Removed abandoned staging directory: {path}"); } } catch { }
        return removed;
    }
}
