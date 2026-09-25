using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaFlux.Services;

public enum FfmpegDiagnosticComponent { Ffmpeg, Ffprobe, Other }
public enum FfmpegDiagnosticCategory { SourceDecode, SourceIntegrity, TimestampTimeline, AudioDecode, Subtitle, Muxing, EncoderInitialization, HardwareAcceleration, DiskIo, PermissionAccess, OutputValidation, Unknown }
public enum FfmpegDiagnosticSeverity { Info, Warning, Error }
public enum FfmpegDiagnosticConfidence { Unknown, Low, Moderate, High }

public sealed record FfmpegDiagnosticEvent(
    long Order, DateTimeOffset CapturedAt, FfmpegDiagnosticComponent Component,
    string RawMessage, string Fingerprint, string Family, FfmpegDiagnosticCategory Category,
    FfmpegDiagnosticSeverity Severity, double? SourceTimestampSeconds,
    IReadOnlyDictionary<string, long> Values);

public sealed record FfmpegDiagnosticFamilySummary(
    string Fingerprint, string Family, FfmpegDiagnosticCategory Category,
    FfmpegDiagnosticSeverity Severity, long Occurrences, DateTimeOffset FirstOccurrence,
    DateTimeOffset LastOccurrence, IReadOnlyList<string> RepresentativeRawMessages,
    IReadOnlyDictionary<string, (long Minimum, long Maximum)> ValueRanges, bool SamplesTruncated,
    long FirstOrder = 0);

public sealed record FfmpegDiagnosticClassification(
    FfmpegDiagnosticCategory PrimaryCategory, string ProbableCause,
    FfmpegDiagnosticConfidence Confidence, IReadOnlyList<string> SupportingFamilies);

public sealed record FfmpegDiagnosticSummary(
    IReadOnlyList<FfmpegDiagnosticFamilySummary> Families,
    FfmpegDiagnosticClassification Classification, long TotalEvents,
    long DroppedNewFamilies, bool AnalysisHadErrors);

/// <summary>Conservatively turns one raw FFmpeg/FFprobe line into comparison metadata. Raw text is never changed.</summary>
public sealed partial class FfmpegDiagnosticNormalizer
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)] private static partial Regex Whitespace();
    [GeneratedRegex(@"\s*@\s*[0-9a-f]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Address();
    [GeneratedRegex(@"Invalid NAL unit size \((?<actual>-?\d+)\s*>\s*(?<expected>-?\d+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex InvalidNal();
    [GeneratedRegex(@"missing picture in access unit with size\s+(?<size>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex MissingPicture();
    [GeneratedRegex(@"\b(?:pts|dts|time)=\s*(?<value>-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Timestamp();

    public FfmpegDiagnosticEvent Normalize(string rawMessage, FfmpegDiagnosticComponent component, long order, DateTimeOffset capturedAt)
    {
        string raw = rawMessage ?? string.Empty;
        string comparable = Whitespace().Replace(Address().Replace(raw, " @ <address>"), " ").Trim();
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        Match nal = InvalidNal().Match(comparable);
        if (nal.Success)
        {
            values["invalid-size"] = long.Parse(nal.Groups["actual"].Value, CultureInfo.InvariantCulture);
            values["expected-size"] = long.Parse(nal.Groups["expected"].Value, CultureInfo.InvariantCulture);
            comparable = InvalidNal().Replace(comparable, "Invalid NAL unit size (<n> > <n>)");
            return Event("Invalid NAL unit size", FfmpegDiagnosticCategory.SourceIntegrity, FfmpegDiagnosticSeverity.Error, comparable, values);
        }
        Match missingPicture = MissingPicture().Match(comparable);
        if (missingPicture.Success)
        {
            values["access-unit-size"] = long.Parse(missingPicture.Groups["size"].Value, CultureInfo.InvariantCulture);
            comparable = MissingPicture().Replace(comparable, "Missing picture in access unit with size <n>");
            return Event("Missing picture in access unit", FfmpegDiagnosticCategory.SourceIntegrity, FfmpegDiagnosticSeverity.Warning, comparable, values);
        }

        (string family, FfmpegDiagnosticCategory category, FfmpegDiagnosticSeverity severity) = comparable switch
        {
            var x when x.Contains("No space left", StringComparison.OrdinalIgnoreCase) => ("Output write failure", FfmpegDiagnosticCategory.DiskIo, FfmpegDiagnosticSeverity.Error),
            var x when x.Contains("Starting second pass: moving the moov atom", StringComparison.OrdinalIgnoreCase) => ("MP4 faststart relocation", FfmpegDiagnosticCategory.Muxing, FfmpegDiagnosticSeverity.Info),
            var x when IsAffirmativeMuxingFailure(x) => ("Muxing failure", FfmpegDiagnosticCategory.Muxing, FfmpegDiagnosticSeverity.Error),
            var x when x.Contains("Error splitting the input into NAL units", StringComparison.OrdinalIgnoreCase) => ("Error splitting input into NAL units", FfmpegDiagnosticCategory.SourceDecode, FfmpegDiagnosticSeverity.Error),
            var x when x.Contains("missing mandatory atoms", StringComparison.OrdinalIgnoreCase) || x.Contains("broken header", StringComparison.OrdinalIgnoreCase) => ("Broken or incomplete container header", FfmpegDiagnosticCategory.SourceIntegrity, FfmpegDiagnosticSeverity.Error),
            var x when x.Contains("Could not open encoder before EOF", StringComparison.OrdinalIgnoreCase) => ("Downstream encoder abort", FfmpegDiagnosticCategory.EncoderInitialization, FfmpegDiagnosticSeverity.Warning),
            var x when x.Contains("Error submitting packet to decoder", StringComparison.OrdinalIgnoreCase) => ("Decoder packet submission failure", FfmpegDiagnosticCategory.SourceDecode, FfmpegDiagnosticSeverity.Error),
            var x when x.Contains("Error processing packet in decoder", StringComparison.OrdinalIgnoreCase) => ("Decoder packet processing failure", FfmpegDiagnosticCategory.SourceDecode, FfmpegDiagnosticSeverity.Error),
            var x when x.Contains("Invalid data found when processing input", StringComparison.OrdinalIgnoreCase) => ("Invalid input data", FfmpegDiagnosticCategory.SourceDecode, FfmpegDiagnosticSeverity.Error),
            var x when x.Contains("corrupt", StringComparison.OrdinalIgnoreCase) && x.Contains("packet", StringComparison.OrdinalIgnoreCase) => ("Corrupt packet", FfmpegDiagnosticCategory.SourceIntegrity, FfmpegDiagnosticSeverity.Warning),
            var x when x.Contains("Non-monotonous DTS", StringComparison.OrdinalIgnoreCase) => ("Non-monotonic DTS", FfmpegDiagnosticCategory.TimestampTimeline, FfmpegDiagnosticSeverity.Warning),
            var x when x.Contains("timestamp", StringComparison.OrdinalIgnoreCase) || x.Contains(" DTS", StringComparison.OrdinalIgnoreCase) || x.Contains(" PTS", StringComparison.OrdinalIgnoreCase) => ("Timestamp/timeline failure", FfmpegDiagnosticCategory.TimestampTimeline, FfmpegDiagnosticSeverity.Warning),
            var x when IsSubtitleFailure(x) => ("Subtitle processing failure", FfmpegDiagnosticCategory.Subtitle, FfmpegDiagnosticSeverity.Error),
            var x when IsAffirmativeHardwareFailure(x) => ("Hardware acceleration failure", FfmpegDiagnosticCategory.HardwareAcceleration, FfmpegDiagnosticSeverity.Error),
            var x when x.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || x.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) => ("Permission/access failure", FfmpegDiagnosticCategory.PermissionAccess, FfmpegDiagnosticSeverity.Error),
            var x when x.Contains("Error writing", StringComparison.OrdinalIgnoreCase) => ("Output write failure", FfmpegDiagnosticCategory.DiskIo, FfmpegDiagnosticSeverity.Error),
            var x when x.Contains("Error initializing output stream", StringComparison.OrdinalIgnoreCase) || x.Contains("Error while opening encoder", StringComparison.OrdinalIgnoreCase) => ("Encoder initialization failure", FfmpegDiagnosticCategory.EncoderInitialization, FfmpegDiagnosticSeverity.Error),
            var x when x.Contains("audio", StringComparison.OrdinalIgnoreCase) && x.Contains("decod", StringComparison.OrdinalIgnoreCase) => ("Audio decode failure", FfmpegDiagnosticCategory.AudioDecode, FfmpegDiagnosticSeverity.Error),
            _ => ("Unknown", FfmpegDiagnosticCategory.Unknown, FfmpegDiagnosticSeverity.Warning)
        };
        return Event(family, category, severity,
            category == FfmpegDiagnosticCategory.Unknown ? "Unknown" : comparable, values);

        FfmpegDiagnosticEvent Event(string family, FfmpegDiagnosticCategory category, FfmpegDiagnosticSeverity severity, string fingerprint, Dictionary<string, long> extracted)
        {
            double? timestamp = null;
            Match match = Timestamp().Match(raw);
            if (match.Success && double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed)) timestamp = parsed;
            return new FfmpegDiagnosticEvent(order, capturedAt, component, raw, fingerprint, family, category, severity, timestamp, new ReadOnlyDictionary<string, long>(extracted));
        }
    }

    public static bool IsInformationalBanner(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) &&
        (raw.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase) ||
         raw.StartsWith("built with ", StringComparison.OrdinalIgnoreCase) ||
         raw.StartsWith("configuration:", StringComparison.OrdinalIgnoreCase) ||
         raw.StartsWith("libav", StringComparison.OrdinalIgnoreCase));

    private static bool IsSubtitleFailure(string value)
    {
        if (!value.Contains("subtitle", StringComparison.OrdinalIgnoreCase))
            return false;

        return value.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("malformed", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("corrupt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAffirmativeMuxingFailure(string value) =>
        value.Contains("Error initializing the muxer", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Could not write header", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Error writing header", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Error writing trailer", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Could not write trailer", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Error muxing", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("av_interleaved_write_frame", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("muxing failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsAffirmativeHardwareFailure(string value) =>
        value.Contains("CUDA_ERROR", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("no capable CUDA device", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("no NVENC capable devices", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("NVENC driver", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("NVENC session", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("device lost", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("device unavailable", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("failed to initialize CUDA", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("cannot load cuvid", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Thread-safe, bounded diagnostic aggregation. It is deliberately observational.</summary>
public sealed class FfmpegDiagnosticAggregator
{
    private const int MaxFamilies = 128, MaxSamplesPerFamily = 6;
    private readonly object _sync = new(); private readonly Dictionary<string, Family> _families = new(StringComparer.Ordinal);
    private long _total, _dropped; private bool _analysisHadErrors;
    public void Add(FfmpegDiagnosticEvent item)
    {
        lock (_sync)
        {
            _total++;
            if (!_families.TryGetValue(item.Fingerprint, out Family? family))
            {
                if (_families.Count >= MaxFamilies) { _dropped++; return; }
                family = new Family(item); _families.Add(item.Fingerprint, family);
            }
            family.Add(item);
        }
    }
    public void NoteAnalysisError() { lock (_sync) _analysisHadErrors = true; }
    public FfmpegDiagnosticSummary CreateSummary()
    {
        lock (_sync)
        {
            var families = _families.Values.Select(x => x.Summary()).OrderByDescending(x => x.Occurrences).ToArray();
            return new FfmpegDiagnosticSummary(families, FfmpegDiagnosticClassifier.Classify(families), _total, _dropped, _analysisHadErrors);
        }
    }
    private sealed class Family
    {
        private readonly FfmpegDiagnosticEvent _first; private readonly List<string> _samples = new(); private readonly Dictionary<string, (long Min, long Max)> _ranges = new(); private long _count; private DateTimeOffset _last; private bool _truncated;
        public Family(FfmpegDiagnosticEvent item) { _first = item; _last = item.CapturedAt; }
        public void Add(FfmpegDiagnosticEvent item) { _count++; _last = item.CapturedAt; foreach (var v in item.Values) _ranges[v.Key] = _ranges.TryGetValue(v.Key, out var range) ? (Math.Min(range.Min, v.Value), Math.Max(range.Max, v.Value)) : (v.Value, v.Value); if (_samples.Count < 3) _samples.Add(item.RawMessage); else { if (_samples.Count < MaxSamplesPerFamily) _samples.Add(item.RawMessage); else { _samples.RemoveAt(3); _samples.Add(item.RawMessage); _truncated = true; } } }
        public FfmpegDiagnosticFamilySummary Summary() => new(_first.Fingerprint, _first.Family, _first.Category, _first.Severity, _count, _first.CapturedAt, _last, _samples.ToArray(), new ReadOnlyDictionary<string, (long Minimum, long Maximum)>(_ranges.ToDictionary(x => x.Key, x => (x.Value.Min, x.Value.Max))), _truncated, _first.Order);
    }
}

public static class FfmpegDiagnosticClassifier
{
    public static FfmpegDiagnosticClassification Classify(IReadOnlyList<FfmpegDiagnosticFamilySummary> families)
    {
        FfmpegDiagnosticFamilySummary? firstMuxFailure = families.Where(x => x.Category == FfmpegDiagnosticCategory.Muxing && x.Severity == FfmpegDiagnosticSeverity.Error).OrderBy(x => x.FirstOrder).FirstOrDefault();
        FfmpegDiagnosticFamilySummary[] causalSource = families.Where(x =>
            (x.Category is FfmpegDiagnosticCategory.SourceDecode or FfmpegDiagnosticCategory.SourceIntegrity) &&
            (firstMuxFailure is null || x.FirstOrder <= firstMuxFailure.FirstOrder)).ToArray();
        string[] source = causalSource.Select(x => x.Family).Distinct().ToArray();
        bool nal = source.Contains("Invalid NAL unit size"), split = source.Contains("Error splitting input into NAL units"), packet = source.Contains("Decoder packet submission failure") || source.Contains("Invalid input data");
        bool corruptVideoPacket = causalSource.Any(x => x.Family == "Corrupt packet" &&
            x.RepresentativeRawMessages.Any(message =>
                message.Contains("stream = 0", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("stream 0", StringComparison.OrdinalIgnoreCase)));
        bool missingPicture = source.Contains("Missing picture in access unit");
        bool container = source.Contains("Broken or incomplete container header");
        FfmpegDiagnosticFamilySummary? firstSource = causalSource.OrderBy(x => x.FirstOrder).FirstOrDefault();
        bool sourcePrecedesMux = firstSource is not null;
        if (sourcePrecedesMux && nal && split && (packet || container)) return new(FfmpegDiagnosticCategory.SourceIntegrity, "Malformed or corrupt H.264 bitstream data is probable.", FfmpegDiagnosticConfidence.High, source);
        if (sourcePrecedesMux && nal && missingPicture && corruptVideoPacket)
            return new(FfmpegDiagnosticCategory.SourceIntegrity, "Corrupt video packets and malformed H.264 access units are probable.", FfmpegDiagnosticConfidence.High, source);
        FfmpegDiagnosticFamilySummary? hardware = families.FirstOrDefault(x => x.Category == FfmpegDiagnosticCategory.HardwareAcceleration);
        if (hardware is not null) return new(FfmpegDiagnosticCategory.HardwareAcceleration, "Hardware-acceleration or encoder-device failure is probable.", FfmpegDiagnosticConfidence.Moderate, new[] { hardware.Family });
        if (source.Length >= 2 && sourcePrecedesMux)
            return new(FfmpegDiagnosticCategory.SourceDecode, "Source decode or bitstream-integrity failure is probable.", FfmpegDiagnosticConfidence.Moderate, source);
        FfmpegDiagnosticFamilySummary? faststart = families.FirstOrDefault(x => x.Family == "MP4 faststart relocation");
        if (faststart is not null && firstMuxFailure is not null &&
            faststart.FirstOrder <= firstMuxFailure.FirstOrder &&
            (firstSource is null || firstSource.FirstOrder > firstMuxFailure.FirstOrder))
            return new(FfmpegDiagnosticCategory.Muxing, "MP4 faststart relocation or trailer finalization failed after media processing.", FfmpegDiagnosticConfidence.High,
                [faststart.Family, firstMuxFailure.Family]);
        FfmpegDiagnosticFamilySummary? primary = families.Where(x => x.Severity == FfmpegDiagnosticSeverity.Error).OrderByDescending(x => x.Occurrences).FirstOrDefault(x => x.Category != FfmpegDiagnosticCategory.Unknown);
        return primary == null ? new(FfmpegDiagnosticCategory.Unknown, "No confident diagnostic interpretation is available.", FfmpegDiagnosticConfidence.Unknown, Array.Empty<string>()) : new(primary.Category, "A single diagnostic family was observed; cause remains uncertain.", FfmpegDiagnosticConfidence.Low, new[] { primary.Family });
    }
}

public sealed class FfmpegDiagnosticCollector
{
    private readonly FfmpegDiagnosticNormalizer _normalizer = new(); private readonly FfmpegDiagnosticAggregator _aggregator = new(); private long _order;
    public void Observe(string rawMessage, FfmpegDiagnosticComponent component)
    {
        if (FfmpegDiagnosticNormalizer.IsInformationalBanner(rawMessage)) return;
        try { _aggregator.Add(_normalizer.Normalize(rawMessage, component, Interlocked.Increment(ref _order), DateTimeOffset.UtcNow)); }
        catch { _aggregator.NoteAnalysisError(); }
    }
    public FfmpegDiagnosticSummary Complete() { try { return _aggregator.CreateSummary(); } catch { _aggregator.NoteAnalysisError(); return new FfmpegDiagnosticSummary(Array.Empty<FfmpegDiagnosticFamilySummary>(), new(FfmpegDiagnosticCategory.Unknown, "Diagnostic analysis was unavailable.", FfmpegDiagnosticConfidence.Unknown, Array.Empty<string>()), 0, 0, true); } }
}
