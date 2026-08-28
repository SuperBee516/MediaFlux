using System.Globalization;
using System.Text.RegularExpressions;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Projects reviewed commercial segments into the proven splitter export contract.
/// This deliberately contains no FFmpeg work; VideoSplitterExportService remains the sole exporter.</summary>
public static class CommercialSegmentExportPlanner
{
    public const string DefaultNamingPattern = "{source}_Commercial_{index:00}";

    public static CommercialSegmentExportPlan CreatePlan(
        string sourcePath,
        IEnumerable<CommercialReviewSegment> reviewSegments,
        IEnumerable<int>? selectedSegmentNumbers,
        string? namingPattern)
    {
        var errors = new List<string>();
        string sourceName = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(sourceName)) sourceName = "Source";
        string extension = Path.GetExtension(sourcePath);
        HashSet<int>? selected = selectedSegmentNumbers == null ? null : selectedSegmentNumbers.ToHashSet();
        CommercialReviewSegment[] chosen = reviewSegments.Where(segment => selected == null || selected.Contains(segment.Number)).ToArray();
        if (chosen.Length == 0) errors.Add("Select at least one segment to export.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var segments = new List<VideoSplitterSegment>();
        foreach (CommercialReviewSegment segment in chosen)
        {
            if (!VideoSplitterSegmentRules.TryValidate(segment.StartSeconds, segment.EndSeconds, double.MaxValue, out string rangeError))
            {
                errors.Add($"Segment {segment.Number}: {rangeError}");
                continue;
            }

            string? patternError = null;
            string? name = segment.IsOutputNameCustom
                ? segment.OutputName
                : ExpandPattern(namingPattern, sourceName, segment.Number, extension, out patternError);
            if (patternError != null) { errors.Add(patternError); continue; }
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileName(name) != name)
            {
                errors.Add($"Segment {segment.Number} has an invalid output filename.");
                continue;
            }
            if (!names.Add(name)) { errors.Add($"Duplicate output filename: {name}"); continue; }
            segments.Add(new VideoSplitterSegment(segment.Number, segment.StartSeconds, segment.EndSeconds, name));
        }
        return new CommercialSegmentExportPlan(segments, errors);
    }

    private static string? ExpandPattern(string? value, string sourceName, int index, string extension, out string? error)
    {
        error = null;
        string pattern = string.IsNullOrWhiteSpace(value) ? DefaultNamingPattern : value.Trim();
        string expanded = pattern.Replace("{source}", sourceName, StringComparison.OrdinalIgnoreCase);
        try
        {
            expanded = Regex.Replace(expanded, @"\{index(?::(?<format>[^}]+))?\}", match =>
            {
                string format = match.Groups["format"].Success ? match.Groups["format"].Value : "00";
                return index.ToString(format, CultureInfo.InvariantCulture);
            }, RegexOptions.IgnoreCase);
        }
        catch (FormatException)
        {
            error = "The filename pattern has an invalid index format. Use {index:00}, for example.";
            return null;
        }
        if (expanded.Contains('{') || expanded.Contains('}'))
        {
            error = "The filename pattern may use only {source} and {index:00}.";
            return null;
        }
        return string.IsNullOrEmpty(Path.GetExtension(expanded)) ? expanded + extension : expanded;
    }
}

public sealed record CommercialSegmentExportPlan(IReadOnlyList<VideoSplitterSegment> Segments, IReadOnlyList<string> Errors);
