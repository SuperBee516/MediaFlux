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
}
