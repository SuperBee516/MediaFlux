namespace MediaFlux.Models;

public enum CommercialBoundaryOrigin { Automatic, Manual, AutomaticMoved }

public sealed record CommercialReviewBoundary(
    Guid Id,
    double TimestampSeconds,
    double? OriginalDetectedTimestampSeconds,
    int Confidence,
    CommercialDetectionConfidence ConfidenceCategory,
    IReadOnlyList<DetectionEvidence> Evidence,
    CommercialBoundaryOrigin Origin);

public sealed record CommercialReviewSegment(
    int Number,
    double StartSeconds,
    double EndSeconds,
    string OutputName,
    bool IsOutputNameCustom)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

/// <summary>Deterministic, UI-independent boundary editing and segment projection.</summary>
public sealed class CommercialReviewState
{
    private const double EdgeEpsilonSeconds = .001;
    private readonly List<CommercialReviewBoundary> _boundaries = new();
    private readonly List<CommercialReviewSegment> _segments = new();
    private readonly List<double> _suppressedAutomaticPositions = new();

    public double SourceDurationSeconds { get; private set; }
    public string SourceBaseName { get; private set; } = "Source";
    public string SourceExtension { get; private set; } = "";
    public IReadOnlyList<CommercialReviewBoundary> Boundaries => _boundaries;
    public IReadOnlyList<CommercialReviewSegment> Segments => _segments;
    public IReadOnlyList<double> SuppressedAutomaticPositions => _suppressedAutomaticPositions;
    public bool HasManualChanges => _boundaries.Any(boundary => boundary.Origin != CommercialBoundaryOrigin.Automatic) || _segments.Any(segment => segment.IsOutputNameCustom) || _suppressedAutomaticPositions.Count > 0;

    public void Initialize(string sourcePath, double durationSeconds, IEnumerable<CommercialBoundary> detectedBoundaries)
    {
        SourceDurationSeconds = Math.Max(0, durationSeconds);
        SourceBaseName = string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(sourcePath)) ? "Source" : Path.GetFileNameWithoutExtension(sourcePath);
        SourceExtension = Path.GetExtension(sourcePath);
        _boundaries.Clear();
        _suppressedAutomaticPositions.Clear();
        _boundaries.AddRange(ToAutomatic(detectedBoundaries));
        Normalize();
        RegenerateSegments(Array.Empty<CommercialReviewSegment>());
    }

    /// <summary>Restores a previously validated review snapshot while retaining the same normalization rules used for live edits.</summary>
    public void Restore(string sourcePath, double durationSeconds, IEnumerable<CommercialReviewBoundary> boundaries, IEnumerable<CommercialReviewSegment> segments, IEnumerable<double>? suppressedAutomaticPositions = null)
    {
        SourceDurationSeconds = Math.Max(0, durationSeconds);
        SourceBaseName = string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(sourcePath)) ? "Source" : Path.GetFileNameWithoutExtension(sourcePath);
        SourceExtension = Path.GetExtension(sourcePath);
        _boundaries.Clear(); _suppressedAutomaticPositions.Clear();
        if (suppressedAutomaticPositions != null) _suppressedAutomaticPositions.AddRange(suppressedAutomaticPositions.Where(IsInterior).Distinct());
        _boundaries.AddRange(boundaries.Where(boundary => IsInterior(boundary.TimestampSeconds)));
        Normalize(); RegenerateSegments(segments.ToArray());
    }

    public bool TryAddBoundary(double timestampSeconds, out CommercialReviewBoundary? added, double duplicateToleranceSeconds = .01)
    {
        added = null;
        if (!IsInterior(timestampSeconds) || HasNearbyBoundary(timestampSeconds, duplicateToleranceSeconds)) return false;
        added = new CommercialReviewBoundary(Guid.NewGuid(), timestampSeconds, null, 0, CommercialDetectionConfidence.Low, Array.Empty<DetectionEvidence>(), CommercialBoundaryOrigin.Manual);
        IReadOnlyList<CommercialReviewSegment> previous = _segments.ToArray(); _boundaries.Add(added); Normalize(); RegenerateSegments(previous); return true;
    }

    public bool TryRemoveBoundary(Guid id)
    {
        int index = _boundaries.FindIndex(boundary => boundary.Id == id); if (index < 0) return false;
        CommercialReviewBoundary removed = _boundaries[index];
        if (removed.Origin != CommercialBoundaryOrigin.Manual) _suppressedAutomaticPositions.Add(removed.OriginalDetectedTimestampSeconds ?? removed.TimestampSeconds);
        IReadOnlyList<CommercialReviewSegment> previous = _segments.ToArray(); _boundaries.RemoveAt(index); RegenerateSegments(previous); return true;
    }

    public bool TryMoveBoundary(Guid id, double timestampSeconds, double duplicateToleranceSeconds = .01)
    {
        int index = _boundaries.FindIndex(boundary => boundary.Id == id); if (index < 0 || !IsInterior(timestampSeconds) || HasNearbyBoundary(timestampSeconds, duplicateToleranceSeconds, id)) return false;
        CommercialReviewBoundary current = _boundaries[index];
        CommercialBoundaryOrigin origin = current.Origin == CommercialBoundaryOrigin.Automatic ? CommercialBoundaryOrigin.AutomaticMoved : current.Origin;
        IReadOnlyList<CommercialReviewSegment> previous = _segments.ToArray(); _boundaries[index] = current with { TimestampSeconds = timestampSeconds, Origin = origin }; Normalize(); RegenerateSegments(previous); return true;
    }

    public bool TryResetBoundary(Guid id, double duplicateToleranceSeconds = .01)
    {
        int index = _boundaries.FindIndex(boundary => boundary.Id == id); if (index < 0) return false;
        CommercialReviewBoundary current = _boundaries[index];
        if (current.Origin != CommercialBoundaryOrigin.AutomaticMoved || current.OriginalDetectedTimestampSeconds is not double original || !IsInterior(original) || HasNearbyBoundary(original, duplicateToleranceSeconds, id)) return false;
        IReadOnlyList<CommercialReviewSegment> previous = _segments.ToArray(); _boundaries[index] = current with { TimestampSeconds = original, Origin = CommercialBoundaryOrigin.Automatic }; Normalize(); RegenerateSegments(previous); return true;
    }

    public bool TrySplitSegment(int segmentIndex, double timestampSeconds) => segmentIndex >= 0 && segmentIndex < _segments.Count && timestampSeconds > _segments[segmentIndex].StartSeconds + EdgeEpsilonSeconds && timestampSeconds < _segments[segmentIndex].EndSeconds - EdgeEpsilonSeconds && TryAddBoundary(timestampSeconds, out _);
    public bool TryMergePrevious(int segmentIndex) => segmentIndex > 0 && segmentIndex < _segments.Count && TryRemoveAtTimestamp(_segments[segmentIndex].StartSeconds);
    public bool TryMergeNext(int segmentIndex) => segmentIndex >= 0 && segmentIndex < _segments.Count - 1 && TryRemoveAtTimestamp(_segments[segmentIndex].EndSeconds);

    public bool TrySetOutputName(int segmentIndex, string outputName)
    {
        string trimmed = outputName.Trim();
        if (segmentIndex < 0 || segmentIndex >= _segments.Count || string.IsNullOrWhiteSpace(trimmed) || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileName(trimmed) != trimmed || _segments.Where((_, index) => index != segmentIndex).Any(segment => segment.OutputName.Equals(trimmed, StringComparison.OrdinalIgnoreCase))) return false;
        _segments[segmentIndex] = _segments[segmentIndex] with { OutputName = trimmed, IsOutputNameCustom = true }; return true;
    }

    public void ApplyReanalysis(IEnumerable<CommercialBoundary> detectedBoundaries, bool keepManualBoundaries, double duplicateToleranceSeconds)
    {
        CommercialReviewBoundary[] retained = keepManualBoundaries ? _boundaries.Where(boundary => boundary.Origin != CommercialBoundaryOrigin.Automatic).ToArray() : Array.Empty<CommercialReviewBoundary>();
        IReadOnlyList<CommercialReviewSegment> previous = keepManualBoundaries ? _segments.ToArray() : Array.Empty<CommercialReviewSegment>();
        if (!keepManualBoundaries) _suppressedAutomaticPositions.Clear();
        _boundaries.Clear(); _boundaries.AddRange(retained);
        foreach (CommercialReviewBoundary automatic in ToAutomatic(detectedBoundaries))
            if (!HasNearbyBoundary(automatic.TimestampSeconds, Math.Max(0, duplicateToleranceSeconds)) && !_suppressedAutomaticPositions.Any(timestamp => Math.Abs(timestamp - automatic.TimestampSeconds) <= Math.Max(0, duplicateToleranceSeconds))) _boundaries.Add(automatic);
        Normalize(); RegenerateSegments(previous);
    }

    private IEnumerable<CommercialReviewBoundary> ToAutomatic(IEnumerable<CommercialBoundary> boundaries) => boundaries.Where(boundary => IsInterior(boundary.TimestampSeconds)).Select(boundary => new CommercialReviewBoundary(Guid.NewGuid(), boundary.TimestampSeconds, boundary.TimestampSeconds, boundary.Confidence, boundary.ConfidenceCategory, boundary.Evidence, CommercialBoundaryOrigin.Automatic));
    private bool TryRemoveAtTimestamp(double timestamp) { CommercialReviewBoundary? boundary = _boundaries.FirstOrDefault(item => Math.Abs(item.TimestampSeconds - timestamp) < EdgeEpsilonSeconds); return boundary != null && TryRemoveBoundary(boundary.Id); }
    private bool IsInterior(double timestamp) => double.IsFinite(timestamp) && timestamp > EdgeEpsilonSeconds && timestamp < SourceDurationSeconds - EdgeEpsilonSeconds;
    private bool HasNearbyBoundary(double timestamp, double tolerance, Guid? except = null) => _boundaries.Any(boundary => boundary.Id != except && Math.Abs(boundary.TimestampSeconds - timestamp) <= tolerance);
    private void Normalize()
    {
        _boundaries.Sort((left, right) => left.TimestampSeconds.CompareTo(right.TimestampSeconds));
        for (int index = _boundaries.Count - 1; index > 0; index--) if (Math.Abs(_boundaries[index].TimestampSeconds - _boundaries[index - 1].TimestampSeconds) < EdgeEpsilonSeconds) _boundaries.RemoveAt(index);
    }

    private void RegenerateSegments(IReadOnlyList<CommercialReviewSegment> previous)
    {
        var ranges = new List<(double Start, double End)>(); double start = 0;
        foreach (CommercialReviewBoundary boundary in _boundaries) { if (boundary.TimestampSeconds > start) ranges.Add((start, boundary.TimestampSeconds)); start = boundary.TimestampSeconds; }
        if (SourceDurationSeconds > start) ranges.Add((start, SourceDurationSeconds));
        var customByRange = new Dictionary<int, CommercialReviewSegment>();
        foreach (CommercialReviewSegment custom in previous.Where(segment => segment.IsOutputNameCustom).OrderBy(segment => segment.Number))
        {
            var best = ranges.Select((range, index) => new { Index = index, Overlap = Math.Max(0, Math.Min(range.End, custom.EndSeconds) - Math.Max(range.Start, custom.StartSeconds)) })
                .Where(item => item.Overlap > 0 && !customByRange.ContainsKey(item.Index)).OrderByDescending(item => item.Overlap).ThenBy(item => item.Index).FirstOrDefault();
            if (best != null) customByRange[best.Index] = custom;
        }
        _segments.Clear();
        for (int index = 0; index < ranges.Count; index++)
        {
            (double rangeStart, double rangeEnd) = ranges[index];
            customByRange.TryGetValue(index, out CommercialReviewSegment? mapped);
            _segments.Add(new CommercialReviewSegment(index + 1, rangeStart, rangeEnd, mapped?.OutputName ?? GeneratedName(index + 1), mapped != null));
        }
    }

    private string GeneratedName(int number) => $"{SourceBaseName}_Commercial_{number:00}{SourceExtension}";
}
