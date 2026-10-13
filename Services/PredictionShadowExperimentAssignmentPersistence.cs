using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Creates and validates durable research assignment bindings to queue sources.</summary>
public static class PredictionShadowExperimentAssignmentPersistence
{
    public static PredictionShadowExperimentAssignment CreateReplacement(
        PredictionShadowExperimentAssignment previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (!previous.IsValid() || previous.Attempt == int.MaxValue)
            throw new ArgumentException("A valid assignment with an incrementable attempt is required.", nameof(previous));
        return previous with
        {
            Attempt = previous.Attempt + 1,
            Role = PredictionShadowExperimentRole.Replacement
        };
    }

    public static bool IsExplicitReplacement(
        PredictionShadowExperimentAssignment previous,
        PredictionShadowExperimentAssignment replacement) =>
        previous.IsValid() && replacement.IsValid() &&
        string.Equals(previous.ExperimentId, replacement.ExperimentId, StringComparison.Ordinal) &&
        previous.Slot == replacement.Slot && replacement.Attempt == previous.Attempt + 1 &&
        previous.Stratum == replacement.Stratum &&
        replacement.Role == PredictionShadowExperimentRole.Replacement;

    public static PredictionShadowExperimentAssignmentBinding? Capture(
        string? sourcePath, PredictionShadowExperimentAssignment? assignment)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || assignment?.IsValid() != true)
            return null;

        try
        {
            string fullPath = Path.GetFullPath(sourcePath);
            var source = new FileInfo(fullPath);
            if (!source.Exists)
                return null;
            return new PredictionShadowExperimentAssignmentBinding(
                assignment, fullPath, source.Length, source.LastWriteTimeUtc.Ticks);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool MatchesSource(
        PredictionShadowExperimentAssignmentBinding? binding, string? sourcePath)
    {
        if (binding?.Assignment.IsValid() != true ||
            string.IsNullOrWhiteSpace(sourcePath) ||
            binding.SourceLengthBytes < 0 || binding.SourceLastWriteTimeUtcTicks <= 0 ||
            string.IsNullOrWhiteSpace(binding.SourcePath))
            return false;

        try
        {
            string fullPath = Path.GetFullPath(sourcePath);
            if (!string.Equals(fullPath, Path.GetFullPath(binding.SourcePath), StringComparison.OrdinalIgnoreCase))
                return false;
            var source = new FileInfo(fullPath);
            return source.Exists && source.Length == binding.SourceLengthBytes &&
                source.LastWriteTimeUtc.Ticks == binding.SourceLastWriteTimeUtcTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public static string? GetSourceIdentityKey(string? sourcePath)
    {
        PredictionShadowExperimentAssignmentBinding? identity = Capture(
            sourcePath, new PredictionShadowExperimentAssignment("identity", 1, 1,
                PredictionShadowExperimentStratum.Low, PredictionShadowExperimentRole.Target));
        return identity is null ? null :
            $"{identity.SourcePath.ToUpperInvariant()}\0{identity.SourceLengthBytes}\0{identity.SourceLastWriteTimeUtcTicks}";
    }

    public static Dictionary<string, PredictionShadowExperimentAssignmentBinding>
        CaptureForQueueReconstruction(IEnumerable<(string SourcePath,
            PredictionShadowExperimentAssignmentBinding? Binding)> queuedItems)
    {
        ArgumentNullException.ThrowIfNull(queuedItems);
        var result = new Dictionary<string, PredictionShadowExperimentAssignmentBinding>(
            StringComparer.OrdinalIgnoreCase);
        foreach ((string sourcePath, PredictionShadowExperimentAssignmentBinding? binding) in queuedItems)
        {
            if (!MatchesSource(binding, sourcePath))
                continue;
            string? key = GetSourceIdentityKey(sourcePath);
            if (key != null)
                result.TryAdd(key, binding!);
        }
        return result;
    }

    public static PredictionShadowExperimentAssignmentBinding? RestoreForQueueReconstruction(
        IReadOnlyDictionary<string, PredictionShadowExperimentAssignmentBinding> assignments,
        string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        string? key = GetSourceIdentityKey(sourcePath);
        return key != null && assignments.TryGetValue(key, out var binding) &&
            MatchesSource(binding, sourcePath)
                ? binding
                : null;
    }
}
