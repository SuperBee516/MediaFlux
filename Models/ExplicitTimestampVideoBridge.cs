namespace MediaFlux.Models;

public enum ExplicitTimestampBridgeAvailability { Available, Unavailable }
public sealed record ExplicitTimestampBridgeCapability(ExplicitTimestampBridgeAvailability Availability, string Reason, string? NativeLibraryDirectory = null);
public sealed record ExplicitTimestampVideoRequest(AiTimestampManifest Manifest, IReadOnlyList<string> ProcessedFrames, string OutputPath);
