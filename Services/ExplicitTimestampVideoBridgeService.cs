using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>
/// Contract for the future native bridge that will submit one decoded/processed frame with its
/// authoritative AVFrame PTS. The existing ffmpeg.exe CLI cannot accept a sequence of image
/// frames with caller-provided per-frame PTS, so this service intentionally has no CLI fallback.
/// </summary>
public sealed class ExplicitTimestampVideoBridgeService
{
    private readonly string? _nativeLibraryDirectory;
    public ExplicitTimestampVideoBridgeService(string? nativeLibraryDirectory = null) => _nativeLibraryDirectory = nativeLibraryDirectory;

    public ExplicitTimestampBridgeCapability GetCapability()
    {
        if (string.IsNullOrWhiteSpace(_nativeLibraryDirectory) || !Directory.Exists(_nativeLibraryDirectory))
            return new(ExplicitTimestampBridgeAvailability.Unavailable, "A MediaFlux timestamp bridge backed by shared FFmpeg libraries is not installed.");
        string[] required = { "avcodec", "avformat", "avutil", "swscale" };
        bool complete = required.All(name => Directory.EnumerateFiles(_nativeLibraryDirectory, name + "*.dll").Any());
        return complete
            ? new(ExplicitTimestampBridgeAvailability.Available, "Shared FFmpeg libraries are present; a native AVFrame-PTS bridge may be initialized by a future integration.", _nativeLibraryDirectory)
            : new(ExplicitTimestampBridgeAvailability.Unavailable, "The configured directory does not contain the required shared FFmpeg libraries for explicit AVFrame PTS submission.", _nativeLibraryDirectory);
    }

    public Task<string> ProduceAsync(ExplicitTimestampVideoRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AiTimestampValidationResult validation = AiTimestampManifestService.Validate(request.Manifest, request.ProcessedFrames.Select(Path.GetFileName).ToArray()!);
        if (!validation.IsValid) throw new AiRestorationValidationException(validation.Reason);
        ExplicitTimestampBridgeCapability capability = GetCapability();
        if (capability.Availability != ExplicitTimestampBridgeAvailability.Available)
            throw new AiRestorationValidationException("Timestamp-preserving VFR prototype is unavailable: " + capability.Reason);
        throw new AiRestorationValidationException("Timestamp-preserving VFR prototype has no native bridge implementation yet; no approximate CLI fallback is permitted.");
    }
}
