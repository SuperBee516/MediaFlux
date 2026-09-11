using System.IO;
using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed record EncodeFailureAnalysisContext(
    Exception Exception,
    string DiagnosticText,
    string CurrentStage,
    bool IsCanceled,
    string SourcePath,
    string OutputPath,
    string EncoderId,
    string EncoderDisplayName,
    string VideoCodec,
    string EncoderPreset,
    bool TenBit,
    string OutputContainer,
    string Restoration);

/// <summary>
/// Converts existing encode state and captured FFmpeg diagnostics into a compact,
/// read-only explanation. It never changes an encode decision or starts recovery.
/// </summary>
public static class EncodeFailureAnalysisService
{
    public static EncodeFailureAnalysis? Analyze(EncodeFailureAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IsCanceled || context.Exception is OperationCanceledException)
            return null;

        string diagnostic = Combine(context.DiagnosticText, context.Exception.ToString());
        string stage = NormalizeStage(context.CurrentStage);
        EncodeFailurePlanContext plan = new(
            EmptyFallback(context.EncoderDisplayName, "Unknown encoder"),
            EmptyFallback(context.VideoCodec, "Unknown codec"),
            EmptyFallback(context.EncoderPreset, "Default"),
            context.TenBit ? "10-bit" : "8-bit",
            EmptyFallback(context.OutputContainer, "Unknown container"),
            EmptyFallback(context.Restoration, "Off"));

        if (IsSourceException(context.Exception) ||
            MatchesSource(diagnostic, context.SourcePath))
        {
            return Create(
                EncodeFailureCategory.Input,
                StageFor(EncodeFailureCategory.Input, stage),
                "The source could not be opened or read.",
                "The input file may be missing, inaccessible, unsupported, or corrupt.",
                "Verify that the input still exists and is readable, then inspect the source for corruption.",
                diagnostic,
                EncodeFailureConfidence.High,
                plan);
        }

        FfmpegStorageFailure storage = FfmpegStorageFailureClassifier.Classify(
            diagnostic,
            context.OutputPath);
        if (storage.IsReliable)
        {
            return Create(
                EncodeFailureCategory.Output,
                StageFor(EncodeFailureCategory.Output, stage),
                "The output could not be written or finalized.",
                $"MediaFlux detected that {storage.Describe()}.",
                RecommendedOutputAction(storage.Kind),
                Evidence(storage.MatchedEvidence, diagnostic),
                EncodeFailureConfidence.High,
                plan);
        }

        FfmpegNvencFailure nvenc = FfmpegNvencFailureClassifier.Classify(diagnostic);
        if (nvenc.IsReliable && IsVideoEncoderContext(context) && !IsAudioDiagnostic(diagnostic))
        {
            string action = nvenc.Kind == FfmpegNvencFailureKind.Unavailable
                ? "Verify NVIDIA encoder availability or select a software video encoder."
                : nvenc.Kind == FfmpegNvencFailureKind.UnsupportedConfiguration
                    ? "Review the selected codec, profile, pixel format, and preset for NVENC compatibility."
                    : "Check NVIDIA driver/GPU health and competing encoder sessions, then review Diagnostics.";
            return Create(
                EncodeFailureCategory.VideoEncoder,
                "Video encoder initialization",
                "The requested video encoder could not initialize.",
                nvenc.Describe(),
                action,
                Evidence(nvenc.MatchedEvidence, diagnostic),
                EncodeFailureConfidence.High,
                plan);
        }

        if (SourceAudioDecodePreflightService.IsReliableAudioDecodeFailure(diagnostic))
        {
            return Create(
                EncodeFailureCategory.Audio,
                "Input decoding",
                "A source audio stream could not be decoded reliably.",
                "FFmpeg reported corruption while decoding a source audio stream.",
                "Retry with Intelligent audio recovery or transcode that audio stream instead of copying it.",
                Evidence(Array.Empty<string>(), diagnostic),
                EncodeFailureConfidence.High,
                plan);
        }

        FfmpegSourceTruncation truncation = FfmpegSourceTruncationClassifier.Classify(diagnostic);
        FfmpegSourceDecodeCorruption corruption = FfmpegSourceDecodeCorruptionClassifier.Classify(diagnostic);
        if (truncation.IsReliable || corruption.IsReliable ||
            Matches(diagnostic, "Invalid data found when processing input", "moov atom not found", "could not find codec parameters", "Error while decoding stream"))
        {
            return Create(
                EncodeFailureCategory.Decoder,
                "Input decoding",
                "The source could not be decoded reliably.",
                "The source may contain corrupt, incomplete, or unsupported stream data.",
                "Review the source media and its detailed diagnostics before retrying.",
                Evidence(
                    truncation.MatchedEvidence.Concat(corruption.MatchedEvidence),
                    diagnostic),
                EncodeFailureConfidence.High,
                plan);
        }

        bool audioFailure = IsAudioDiagnostic(diagnostic);
        if (audioFailure)
        {
            return Create(
                EncodeFailureCategory.Audio,
                "Audio conversion",
                "The audio stream could not be encoded or converted.",
                "The selected audio configuration is unavailable or incompatible with the output container.",
                "Review the audio conversion settings and the output container compatibility details.",
                Evidence(Array.Empty<string>(), diagnostic),
                EncodeFailureConfidence.Moderate,
                plan);
        }

        if (Matches(diagnostic,
                "Unknown encoder",
                "Encoder .* not found",
                "No encoder for codec",
                "Error initializing output stream 0:0",
                "Error while opening encoder",
                "Could not open encoder"))
        {
            return Create(
                EncodeFailureCategory.VideoEncoder,
                "Video encoder",
                "The selected video encoder or its configuration was rejected.",
                "FFmpeg reported that the requested video encoder is unavailable or incompatible with the selected output settings.",
                "Review the selected encoder, codec, profile, and pixel format; use a software encoder only if the requested hardware path is unavailable.",
                Evidence(Array.Empty<string>(), diagnostic),
                EncodeFailureConfidence.Moderate,
                plan);
        }

        if (Matches(diagnostic,
                "Subtitle codec is not supported",
                "Subtitle .* not supported",
                "Could not find tag for codec .* in stream",
                "Subtitle conversion failed"))
        {
            return Create(
                EncodeFailureCategory.Subtitle,
                "Subtitle conversion",
                "A subtitle stream could not be copied or converted.",
                "The subtitle codec is incompatible with the selected output container or conversion path.",
                "Review the subtitle compatibility details and choose a compatible output container or subtitle handling option.",
                Evidence(Array.Empty<string>(), diagnostic),
                EncodeFailureConfidence.Moderate,
                plan);
        }

        if (IsRestorationContext(context, diagnostic))
        {
            return Create(
                EncodeFailureCategory.Restoration,
                "Restoration / preprocessing",
                "The restoration or preprocessing stage did not complete.",
                "MediaFlux has evidence that the failure occurred during restoration or preprocessing, but the available diagnostics do not prove a more specific cause.",
                "Review the restoration and preprocessing diagnostics before retrying; no settings were changed automatically.",
                Evidence(Array.Empty<string>(), diagnostic),
                EncodeFailureConfidence.Moderate,
                plan);
        }

        if (Matches(diagnostic,
                "Error writing trailer",
                "Error muxing a packet",
                "Could not write header",
                "Error initializing output stream",
                "Invalid argument"))
        {
            return Create(
                EncodeFailureCategory.Output,
                StageFor(EncodeFailureCategory.Output, stage),
                "FFmpeg could not create or finalize the output container.",
                "The output format or stream combination was rejected during muxing.",
                "Review the Encoding Plan and detailed diagnostics for an output-container or stream-compatibility issue.",
                Evidence(Array.Empty<string>(), diagnostic),
                EncodeFailureConfidence.Moderate,
                plan);
        }

        return Create(
            EncodeFailureCategory.Unknown,
            string.IsNullOrWhiteSpace(stage) ? "Encode processing" : stage,
            "The encode failed, but MediaFlux could not determine a reliable root cause.",
            "The available exception and FFmpeg diagnostics do not match a high-confidence failure category.",
            "Review the detailed Diagnostics and central error log for the complete failure context.",
            Evidence(Array.Empty<string>(), diagnostic),
            EncodeFailureConfidence.Low,
            plan);
    }

    private static EncodeFailureAnalysis Create(
        EncodeFailureCategory category,
        string stage,
        string summary,
        string cause,
        string action,
        string detail,
        EncodeFailureConfidence confidence,
        EncodeFailurePlanContext plan) =>
        new(category, stage, summary, cause, action, detail, confidence, plan);

    private static bool IsSourceException(Exception exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException ||
        (exception is UnauthorizedAccessException &&
         !exception.Message.Contains("output", StringComparison.OrdinalIgnoreCase));

    private static bool IsVideoEncoderContext(EncodeFailureAnalysisContext context) =>
        context.EncoderId.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ||
        context.EncoderId.Contains("qsv", StringComparison.OrdinalIgnoreCase) ||
        context.EncoderId.Contains("amf", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(context.VideoCodec) ||
        context.CurrentStage.Contains("encoder", StringComparison.OrdinalIgnoreCase);

    private static bool IsAudioDiagnostic(string diagnostic) =>
        Matches(diagnostic,
            "Audio encoder",
            "No audio encoder",
            "Error initializing output stream 0:1",
            "Could not open audio encoder",
            "Audio codec .* not supported",
            "Unknown encoder.*aac",
            "Unknown encoder.*mp3",
            "Unknown encoder.*ac3",
            "Unknown encoder.*opus",
            "Unknown encoder.*vorbis");

    private static bool IsRestorationContext(EncodeFailureAnalysisContext context, string diagnostic) =>
        context.CurrentStage.Contains("restor", StringComparison.OrdinalIgnoreCase) ||
        (context.Restoration is not "" and not "Off" &&
        Matches(diagnostic, "AI restoration", "AI intermediate", "restoration", "NCNN", "TensorRT"));

    private static string StageFor(EncodeFailureCategory category, string currentStage) =>
        category == EncodeFailureCategory.Output
            ? currentStage.Contains("final", StringComparison.OrdinalIgnoreCase) ||
              currentStage.Contains("verif", StringComparison.OrdinalIgnoreCase)
                ? currentStage
                : "Output writing / muxing"
            : category == EncodeFailureCategory.Input || category == EncodeFailureCategory.Decoder
                ? "Input decoding"
                : currentStage;

    private static string RecommendedOutputAction(FfmpegStorageFailureKind kind) => kind switch
    {
        FfmpegStorageFailureKind.InsufficientSpace => "Verify sufficient free disk space, then review the retained incomplete-output policy.",
        FfmpegStorageFailureKind.PermissionDenied => "Verify that the destination is writable and not protected by another process or policy.",
        FfmpegStorageFailureKind.DestinationUnavailable => "Verify that the destination folder still exists and is available.",
        _ => "Review the destination and detailed Diagnostics for a write or muxing problem."
    };

    private static bool MatchesSource(string diagnostic, string sourcePath)
    {
        bool sourceMentioned = !string.IsNullOrWhiteSpace(sourcePath) &&
            diagnostic.Contains(sourcePath, StringComparison.OrdinalIgnoreCase);
        return sourceMentioned && Matches(diagnostic,
            "No such file or directory",
            "Permission denied",
            "Access is denied",
            "Invalid data found when processing input",
            "could not inspect the source",
            "Error while decoding stream");
    }

    private static bool Matches(string text, params string[] signatures) =>
        signatures.Any(signature =>
            signature.Contains(".*", StringComparison.Ordinal)
                ? RegexLikeContains(text, signature)
                : text.Contains(signature, StringComparison.OrdinalIgnoreCase));

    private static bool RegexLikeContains(string text, string pattern)
    {
        string[] parts = pattern.Split(".*", StringSplitOptions.None);
        int index = 0;
        foreach (string part in parts)
        {
            index = text.IndexOf(part, index, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            index += part.Length;
        }
        return true;
    }

    private static string Evidence(IEnumerable<string> preferred, string diagnostic)
    {
        string[] matches = preferred
            .Concat(diagnostic.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => Matches(line,
                    "error", "failed", "denied", "invalid", "unsupported", "not found", "not enough space", "no such file", "corrupt", "truncated")))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        if (matches.Length == 0)
            matches = [diagnostic.Trim()];

        var builder = new StringBuilder();
        foreach (string line in matches)
        {
            if (builder.Length > 0) builder.Append(" | ");
            string remaining = line.Length > 320 ? line[..320] + "…" : line;
            if (builder.Length + remaining.Length > 900)
                break;
            builder.Append(remaining);
        }
        return builder.Length == 0 ? "No concise diagnostic excerpt was available." : builder.ToString();
    }

    private static string Combine(string diagnostic, string exception)
    {
        string value = string.Join(Environment.NewLine, new[] { diagnostic, exception }
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        return value.Length > 128_000 ? value[^128_000..] : value;
    }

    private static string NormalizeStage(string stage) =>
        string.IsNullOrWhiteSpace(stage) ? "Encode processing" : stage.Trim();

    private static string EmptyFallback(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
