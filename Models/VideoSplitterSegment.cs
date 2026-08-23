namespace MediaFlux.Models;

/// <summary>
/// A non-destructive range selected in the Video Splitter. Export ownership is
/// intentionally deferred to Phase 3.
/// </summary>
public sealed record VideoSplitterSegment(int Number, double StartSeconds, double EndSeconds, string OutputFileName)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

public static class VideoSplitterSegmentRules
{
    public static bool TryValidate(double startSeconds, double endSeconds, double sourceDurationSeconds, out string error)
    {
        if (sourceDurationSeconds <= 0) { error = "Load a readable source video first."; return false; }
        if (double.IsNaN(startSeconds) || double.IsNaN(endSeconds) || double.IsInfinity(startSeconds) || double.IsInfinity(endSeconds)) { error = "Timestamps must be valid numbers."; return false; }
        if (startSeconds < 0 || endSeconds > sourceDurationSeconds) { error = "Segment timestamps must stay within the source duration."; return false; }
        if (startSeconds >= endSeconds) { error = "Segment start must be earlier than its end."; return false; }
        error = "";
        return true;
    }

    public static string CreateOutputFileName(string sourcePath, int number)
    {
        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        string extension = Path.GetExtension(sourcePath);
        return $"{stem}-Part{number:00}{(string.IsNullOrWhiteSpace(extension) ? ".mp4" : extension)}";
    }

    public static string CreateUniqueOutputFileName(string sourcePath, int startingNumber, string? outputFolder, IEnumerable<string>? reservedNames = null)
    {
        var reserved = new HashSet<string>(reservedNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        for (int number = Math.Max(1, startingNumber); ; number++)
        {
            string candidate = CreateOutputFileName(sourcePath, number);
            if (reserved.Contains(candidate)) continue;
            if (!string.IsNullOrWhiteSpace(outputFolder) && File.Exists(Path.Combine(outputFolder, candidate))) continue;
            return candidate;
        }
    }

    public static string CreateUnusedFileName(string preferredName, string outputFolder, IEnumerable<string>? reservedNames = null)
    {
        var reserved = new HashSet<string>(reservedNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        string extension = Path.GetExtension(preferredName);
        string stem = Path.GetFileNameWithoutExtension(preferredName);
        for (int suffix = 1; ; suffix++)
        {
            string candidate = suffix == 1 ? preferredName : $"{stem} ({suffix}){extension}";
            if (reserved.Contains(candidate) || File.Exists(Path.Combine(outputFolder, candidate))) continue;
            return candidate;
        }
    }
}
