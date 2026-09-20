namespace MediaFlux.Services;

public sealed record FfmpegLocateResult(bool Succeeded, string? FfmpegPath, string? FfprobePath, FfmpegManagedRuntimeValidation? Runtime, string Detail);

public static class FfmpegSetupService
{
    public static async Task<FfmpegLocateResult> LocateAndValidateAsync(string ffmpegPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ffmpegPath) || !string.Equals(Path.GetFileName(ffmpegPath), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            return new(false, null, null, null, "Select an existing ffmpeg.exe file.");

        string? directory = Path.GetDirectoryName(Path.GetFullPath(ffmpegPath));
        string? ffprobe = directory is null ? null : Path.Combine(directory, "ffprobe.exe");
        if (ffprobe is null || !File.Exists(ffprobe))
            return new(false, ffmpegPath, null, null, "The selected installation does not contain ffprobe.exe beside ffmpeg.exe.");

        FfmpegManagedRuntimeValidation runtime = await FfmpegManagedRuntimeValidator.ValidateAsync(ffmpegPath, ffprobe, cancellationToken).ConfigureAwait(true);
        if (!runtime.FfmpegSucceeded || !runtime.FfprobeSucceeded)
            return new(false, ffmpegPath, ffprobe, runtime, "MediaFlux could not start both FFmpeg tools. Choose a complete, compatible installation.");
        return new(true, Path.GetFullPath(ffmpegPath), Path.GetFullPath(ffprobe), runtime, "FFmpeg and FFprobe are ready.");
    }

    public static void InvalidateCaches() => FfmpegEncoderCapabilityService.ClearCache();
}
