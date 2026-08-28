using System.Text.Json;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Bounded, source-validated persistence for commercial review work.
/// It stores only analysis and user review metadata; source media is never changed.</summary>
public sealed class CommercialAnalysisStore
{
    private const int MaximumEntries = 20;
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public CommercialAnalysisStore(string? path = null) => _path = path ?? AppPaths.CommercialDetectorAnalysisFile;

    public CommercialAnalysisLookup Find(string sourcePath, double durationSeconds)
    {
        CommercialAnalysisSnapshot? snapshot = Read().Entries
            .OrderByDescending(item => item.SavedUtc)
            .FirstOrDefault(item => SamePath(item.SourcePath, sourcePath));
        if (snapshot == null) return new CommercialAnalysisLookup(CommercialAnalysisMatch.None, null);
        if (!File.Exists(sourcePath)) return new CommercialAnalysisLookup(CommercialAnalysisMatch.Stale, snapshot);
        var info = new FileInfo(sourcePath);
        bool exact = snapshot.FileSizeBytes == info.Length &&
            snapshot.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks &&
            Math.Abs(snapshot.DurationSeconds - durationSeconds) < .01;
        return new CommercialAnalysisLookup(exact ? CommercialAnalysisMatch.Exact : CommercialAnalysisMatch.Stale, snapshot);
    }

    public void Save(string sourcePath, double durationSeconds, CommercialDetectionPreset preset, CommercialDetectionSettings settings, CommercialReviewState review)
    {
        if (!File.Exists(sourcePath)) return;
        var info = new FileInfo(sourcePath);
        var snapshot = new CommercialAnalysisSnapshot
        {
            SourcePath = Path.GetFullPath(sourcePath),
            FileSizeBytes = info.Length,
            LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
            DurationSeconds = durationSeconds,
            DetectionPreset = preset.ToString(),
            Settings = settings,
            SuppressedAutomaticPositions = review.SuppressedAutomaticPositions.ToList(),
            Boundaries = review.Boundaries.Select(boundary => new CommercialAnalysisBoundary
            {
                TimestampSeconds = boundary.TimestampSeconds,
                OriginalDetectedTimestampSeconds = boundary.OriginalDetectedTimestampSeconds,
                Confidence = boundary.Confidence,
                ConfidenceCategory = boundary.ConfidenceCategory.ToString(),
                Evidence = boundary.Evidence.ToArray(),
                Origin = boundary.Origin.ToString()
            }).ToList(),
            Segments = review.Segments.Select(segment => new CommercialAnalysisSegment
            {
                Number = segment.Number,
                StartSeconds = segment.StartSeconds,
                EndSeconds = segment.EndSeconds,
                OutputName = segment.OutputName,
                IsOutputNameCustom = segment.IsOutputNameCustom
            }).ToList(),
            SavedUtc = DateTimeOffset.UtcNow
        };
        CommercialAnalysisDocument document = Read();
        document.Entries.RemoveAll(item => SamePath(item.SourcePath, snapshot.SourcePath));
        document.Entries.Add(snapshot);
        document.Entries = document.Entries.OrderByDescending(item => item.SavedUtc).Take(MaximumEntries).ToList();
        Write(document);
    }

    public void Remove(string sourcePath)
    {
        CommercialAnalysisDocument document = Read();
        if (document.Entries.RemoveAll(item => SamePath(item.SourcePath, sourcePath)) > 0) Write(document);
    }

    private CommercialAnalysisDocument Read()
    {
        try
        {
            if (!File.Exists(_path)) return new CommercialAnalysisDocument();
            CommercialAnalysisDocument? document = JsonSerializer.Deserialize<CommercialAnalysisDocument>(File.ReadAllText(_path), _json);
            return document ?? new CommercialAnalysisDocument();
        }
        catch { return new CommercialAnalysisDocument(); }
    }

    private void Write(CommercialAnalysisDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, _json));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    private static bool SamePath(string left, string right)
    {
        try { return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return left.Equals(right, StringComparison.OrdinalIgnoreCase); }
    }
}

public enum CommercialAnalysisMatch { None, Exact, Stale }
public sealed record CommercialAnalysisLookup(CommercialAnalysisMatch Match, CommercialAnalysisSnapshot? Snapshot);
public sealed class CommercialAnalysisDocument { public List<CommercialAnalysisSnapshot> Entries { get; set; } = new(); }
public sealed class CommercialAnalysisSnapshot
{
    public string SourcePath { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public double DurationSeconds { get; set; }
    public string DetectionPreset { get; set; } = nameof(CommercialDetectionPreset.Standard);
    public CommercialDetectionSettings Settings { get; set; } = CommercialDetectionSettings.Standard;
    public List<double> SuppressedAutomaticPositions { get; set; } = new();
    public List<CommercialAnalysisBoundary> Boundaries { get; set; } = new();
    public List<CommercialAnalysisSegment> Segments { get; set; } = new();
    public DateTimeOffset SavedUtc { get; set; }
}
public sealed class CommercialAnalysisBoundary
{
    public double TimestampSeconds { get; set; }
    public double? OriginalDetectedTimestampSeconds { get; set; }
    public int Confidence { get; set; }
    public string ConfidenceCategory { get; set; } = nameof(CommercialDetectionConfidence.Low);
    public DetectionEvidence[] Evidence { get; set; } = Array.Empty<DetectionEvidence>();
    public string Origin { get; set; } = nameof(CommercialBoundaryOrigin.Automatic);
}
public sealed class CommercialAnalysisSegment
{
    public int Number { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public string OutputName { get; set; } = "";
    public bool IsOutputNameCustom { get; set; }
}
