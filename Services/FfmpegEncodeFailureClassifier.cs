namespace MediaFlux.Services;

internal enum FfmpegStorageFailureKind
{
    None,
    InsufficientSpace,
    PermissionDenied,
    DestinationUnavailable,
    WriteIoFailure
}

internal sealed record FfmpegStorageFailure(FfmpegStorageFailureKind Kind, IReadOnlyList<string> MatchedEvidence)
{
    public bool IsReliable => Kind != FfmpegStorageFailureKind.None;

    public string Describe() => Kind switch
    {
        FfmpegStorageFailureKind.InsufficientSpace => "the destination ran out of disk space",
        FfmpegStorageFailureKind.PermissionDenied => "MediaFlux was denied write access to the destination",
        FfmpegStorageFailureKind.DestinationUnavailable => "the destination or its staging directory became unavailable",
        FfmpegStorageFailureKind.WriteIoFailure => "the destination reported a write or I/O failure",
        _ => "no reliable storage failure evidence was found"
    };
}

internal static class FfmpegStorageFailureClassifier
{
    public static FfmpegStorageFailure Classify(string? standardError, string? outputPath = null)
    {
        if (string.IsNullOrWhiteSpace(standardError))
            return new(FfmpegStorageFailureKind.None, Array.Empty<string>());

        string[] lines = standardError.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        bool referencesOutput = !string.IsNullOrWhiteSpace(outputPath) &&
            lines.Any(line => line.Contains(Path.GetFileName(outputPath), StringComparison.OrdinalIgnoreCase));
        if (TryFind(lines, "No space left on device", "There is not enough space", "ERROR_DISK_FULL", out string[] space))
            return new(FfmpegStorageFailureKind.InsufficientSpace, space);
        if ((referencesOutput || lines.Any(line => line.Contains("output", StringComparison.OrdinalIgnoreCase))) &&
            TryFind(lines, "Permission denied", "Access is denied", "ERROR_ACCESS_DENIED", out string[] access))
            return new(FfmpegStorageFailureKind.PermissionDenied, access);

        if (referencesOutput && TryFind(lines, "No such file or directory", "The system cannot find the path specified", out string[] unavailable))
            return new(FfmpegStorageFailureKind.DestinationUnavailable, unavailable);
        if (TryFind(lines, "Error writing trailer", "Error muxing a packet", "av_interleaved_write_frame", out string[] io) ||
            (referencesOutput && TryFind(lines, "Input/output error", "ERROR_IO_DEVICE", out io)))
            return new(FfmpegStorageFailureKind.WriteIoFailure, io);

        return new(FfmpegStorageFailureKind.None, Array.Empty<string>());
    }

    public static FfmpegStorageFailure Classify(Exception exception, bool destinationOperation)
    {
        if (!destinationOperation)
            return new(FfmpegStorageFailureKind.None, Array.Empty<string>());
        if (exception is UnauthorizedAccessException || exception.Message.Contains("access", StringComparison.OrdinalIgnoreCase))
            return new(FfmpegStorageFailureKind.PermissionDenied, new[] { exception.Message });
        if (exception is DirectoryNotFoundException or FileNotFoundException)
            return new(FfmpegStorageFailureKind.DestinationUnavailable, new[] { exception.Message });
        if (exception.Message.Contains("space", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("disk full", StringComparison.OrdinalIgnoreCase))
            return new(FfmpegStorageFailureKind.InsufficientSpace, new[] { exception.Message });
        if (exception is IOException &&
            (exception.Message.Contains("I/O", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("device", StringComparison.OrdinalIgnoreCase)))
            return new(FfmpegStorageFailureKind.WriteIoFailure, new[] { exception.Message });
        return new(FfmpegStorageFailureKind.None, Array.Empty<string>());
    }

    private static bool TryFind(IEnumerable<string> lines, string first, string second, out string[] matches) =>
        TryFind(lines, new[] { first, second }, out matches);

    private static bool TryFind(IEnumerable<string> lines, string first, string second, string third, out string[] matches) =>
        TryFind(lines, new[] { first, second, third }, out matches);

    private static bool TryFind(IEnumerable<string> lines, string first, string second, string third, string fourth, out string[] matches) =>
        TryFind(lines, new[] { first, second, third, fourth }, out matches);

    private static bool TryFind(IEnumerable<string> lines, IEnumerable<string> signatures, out string[] matches)
    {
        matches = lines.Where(line => signatures.Any(signature => line.Contains(signature, StringComparison.OrdinalIgnoreCase))).ToArray();
        return matches.Length > 0;
    }
}

internal enum FfmpegNvencFailureKind
{
    None,
    Unavailable,
    UnsupportedConfiguration,
    RuntimeFailure
}

internal sealed record FfmpegNvencFailure(FfmpegNvencFailureKind Kind, IReadOnlyList<string> MatchedEvidence)
{
    public bool IsReliable => Kind != FfmpegNvencFailureKind.None;

    public string Describe() => Kind switch
    {
        FfmpegNvencFailureKind.Unavailable => "the requested NVENC encoder is unavailable; verify the NVIDIA driver and FFmpeg build",
        FfmpegNvencFailureKind.UnsupportedConfiguration => "the requested NVENC configuration is unsupported; adjust codec, profile, pixel format, or preset",
        FfmpegNvencFailureKind.RuntimeFailure => "the NVENC runtime failed; check GPU/driver health and competing encoder sessions",
        _ => "no reliable NVENC failure evidence was found"
    };
}

internal static class FfmpegNvencFailureClassifier
{
    public static FfmpegNvencFailure Classify(string? standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
            return new(FfmpegNvencFailureKind.None, Array.Empty<string>());
        if (Contains(standardError, "Unknown encoder", "Cannot load libnvidia-encode", "No NVENC capable devices found", "minimum required Nvidia driver"))
            return new(FfmpegNvencFailureKind.Unavailable, Evidence(standardError));
        if (Contains(standardError, "InitializeEncoder failed: invalid param", "NV_ENC_ERR_INVALID_PARAM", "unsupported preset"))
            return new(FfmpegNvencFailureKind.UnsupportedConfiguration, Evidence(standardError));
        if (Contains(standardError, "NV_ENC_ERR", "Nvenc unloaded", "NVENC Error"))
            return new(FfmpegNvencFailureKind.RuntimeFailure, Evidence(standardError));
        return new(FfmpegNvencFailureKind.None, Array.Empty<string>());
    }

    private static bool Contains(string text, params string[] signatures) =>
        signatures.Any(signature => text.Contains(signature, StringComparison.OrdinalIgnoreCase));

    private static string[] Evidence(string text) => text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("nvidia", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("NV_ENC", StringComparison.OrdinalIgnoreCase))
        .Take(4)
        .ToArray();
}
