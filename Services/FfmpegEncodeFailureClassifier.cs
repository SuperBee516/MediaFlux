using System.Globalization;
using System.Text.RegularExpressions;
using MediaFlux.Models;

namespace MediaFlux.Services;

internal sealed record FfmpegAudioTimestampFailure(
    int? OutputAudioStreamIndex,
    long? PreviousDts,
    long? CurrentDts,
    string MatchedEvidence)
{
    public string DescribeDiagnosis()
    {
        string stream = OutputAudioStreamIndex is { } index
            ? $" audio output stream {index}"
            : " audio";
        string timestamps = PreviousDts is { } previous && CurrentDts is { } current
            ? $" (previous DTS {previous.ToString(CultureInfo.InvariantCulture)}, current DTS {current.ToString(CultureInfo.InvariantCulture)})"
            : "";
        return "Audio timestamp incompatibility: the MP4 muxer rejected copied" + stream +
            " because packet DTS values were non-monotonic" + timestamps +
            ". This indicates an input audio packet-timeline incompatibility with stream-copy-to-MP4; it does not establish source corruption. " +
            "The failed attempt did not automatically transcode the copied audio stream.";
    }
}

internal static class FfmpegAudioTimestampFailureClassifier
{
    private static readonly Regex CopiedAudioMuxFailure = new(
        @"\[aost#\d+:(?<stream>\d+)/copy(?:\s+@\s+[0-9a-f]{8,})?\]\s*Non-monotonic DTS;\s*previous:\s*(?<previous>-?\d+),\s*current:\s*(?<current>-?\d+);\s*Error submitting (?:a )?packet to the muxer",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static FfmpegAudioTimestampFailure? Classify(string? standardError, OutputContainer outputContainer)
    {
        if (outputContainer != OutputContainer.Mp4 || string.IsNullOrWhiteSpace(standardError))
            return null;

        Match match = CopiedAudioMuxFailure.Match(standardError);
        if (!match.Success)
            return null;

        return new FfmpegAudioTimestampFailure(
            int.TryParse(match.Groups["stream"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int streamIndex)
                ? streamIndex
                : null,
            long.TryParse(match.Groups["previous"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long previousDts)
                ? previousDts
                : null,
            long.TryParse(match.Groups["current"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long currentDts)
                ? currentDts
                : null,
            match.Value);
    }
}

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
        // Generic mux errors (including non-monotonic DTS / invalid argument)
        // do not establish a destination write failure on their own.
        if (TryFind(lines, "Input/output error", "ERROR_IO_DEVICE", out string[] io) &&
            (referencesOutput || lines.Any(line =>
                line.Contains("Error writing trailer", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Error muxing a packet", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("av_interleaved_write_frame", StringComparison.OrdinalIgnoreCase))))
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
    DriverIncompatible,
    UnsupportedConfiguration,
    RuntimeFailure
}

internal sealed record FfmpegNvencFailure(FfmpegNvencFailureKind Kind, IReadOnlyList<string> MatchedEvidence)
{
    public bool IsReliable => Kind != FfmpegNvencFailureKind.None;

    public string Describe() => Kind switch
    {
        FfmpegNvencFailureKind.Unavailable => "the requested NVENC encoder is unavailable; verify the NVIDIA driver and FFmpeg build",
        FfmpegNvencFailureKind.DriverIncompatible => "the installed NVIDIA driver does not support the NVENC API required by this FFmpeg build",
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
        if (Contains(standardError, "does not support the required nvenc api version", "minimum required Nvidia driver"))
            return new(FfmpegNvencFailureKind.DriverIncompatible, Evidence(standardError));
        if (Contains(standardError, "Unknown encoder", "Cannot load libnvidia-encode", "No NVENC capable devices found"))
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
