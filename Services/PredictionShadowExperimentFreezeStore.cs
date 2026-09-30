using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>
/// Immutable schema-1 JSON freezes in UserData/data/research-experiment-freezes.
/// Construction/loading never create files. Only Create writes; journals/settings are never accessed.
/// </summary>
public sealed class PredictionShadowExperimentFreezeStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly string _directory;

    public PredictionShadowExperimentFreezeStore() : this(AppPaths.UserDataDirectory) { }

    public PredictionShadowExperimentFreezeStore(string userDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        _directory = Path.Combine(Path.GetFullPath(userDataDirectory), "data", "research-experiment-freezes");
    }

    /// <summary>Hashing the literal, case-sensitive ID avoids filename collisions and path traversal.</summary>
    public string GetPath(string experimentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(experimentId))).ToLowerInvariant();
        return Path.Combine(_directory, key + ".json");
    }

    public PredictionShadowExperimentFreeze? Load(string experimentId)
    {
        string path = GetPath(experimentId);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        PredictionShadowExperimentFreeze freeze = Deserialize(bytes);
        PredictionShadowExperimentFreezeValidation.Validate(freeze);
        if (!string.Equals(freeze.ExperimentId, experimentId, StringComparison.Ordinal))
            throw new InvalidDataException("Freeze identity does not match its storage key.");
        return freeze;
    }

    /// <summary>Missing returns false; corrupt/unsupported existing freezes fail closed rather than appearing absent.</summary>
    public bool IsFrozen(string experimentId) => Load(experimentId) is not null;

    public PredictionShadowExperimentFreeze Create(PredictionShadowExperimentFreeze freeze)
    {
        PredictionShadowExperimentFreezeValidation.Validate(freeze);
        byte[] bytes = Serialize(freeze);
        // Detach from caller-owned collections and validate exactly the snapshot being published.
        PredictionShadowExperimentFreeze snapshot = Deserialize(bytes);
        PredictionShadowExperimentFreezeValidation.Validate(snapshot);
        string destination = GetPath(snapshot.ExperimentId);
        var existing = Load(snapshot.ExperimentId);
        if (existing is not null) return IdenticalOrConflict(existing, bytes);

        Directory.CreateDirectory(_directory);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory atomic publication. Never overwrite, including concurrent creators.
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination))
            {
                return IdenticalOrConflict(Load(snapshot.ExperimentId) ?? throw new IOException("Freeze disappeared during publication."), bytes);
            }
            return Load(snapshot.ExperimentId) ?? throw new IOException("Published freeze could not be loaded.");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Explicit opt-in check for initial frozen Targets. Does not change legacy assignment behavior,
    /// authorize reserve activation, create assignments, or emit Frozen/Outcome events.
    /// The caller supplies the measured family key using the existing source-family service.
    /// </summary>
    public void ValidateTargetAssignment(PredictionShadowExperimentAssignmentBinding binding, string sourceFamilyKey)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.Assignment?.IsValid() != true) throw new InvalidDataException("Invalid assignment.");
        var assignment = binding.Assignment;
        var freeze = Load(assignment.ExperimentId) ?? throw new InvalidDataException("The referenced experiment has no valid freeze.");
        var target = freeze.Targets.SingleOrDefault(t => t.Slot == assignment.Slot);
        if (target is null || assignment.Attempt != freeze.ReplacementPolicy.InitialAttempt || assignment.Role != target.Role || assignment.Stratum != target.Stratum)
            throw new InvalidDataException("Assignment slot, attempt, stratum, or role does not match the frozen Target.");
        var source = target.Source;
        if (!string.Equals(binding.SourcePath, source.SourcePath, StringComparison.OrdinalIgnoreCase) ||
            binding.SourceLengthBytes != source.SourceLengthBytes || binding.SourceLastWriteTimeUtcTicks != source.SourceLastWriteTimeUtcTicks ||
            !string.Equals(sourceFamilyKey, source.FamilyKey, StringComparison.OrdinalIgnoreCase) ||
            !PredictionShadowExperimentAssignmentPersistence.MatchesSource(binding, source.SourcePath))
            throw new InvalidDataException("Assignment source binding/family does not match the frozen Target or current file.");
    }

    /// <summary>
    /// Validates a durable assignment before execution against its frozen Target or
    /// explicitly activated same-stratum reserve, including the current source file
    /// identity. Returns the frozen source-family key for plan-time verification.
    /// </summary>
    public string ValidateExecutionAssignment(
        PredictionShadowExperimentAssignmentBinding? binding,
        string? executionSourcePath,
        string? observedSourceFamilyKey = null)
    {
        if (binding?.Assignment?.IsValid() != true)
            throw new InvalidDataException("The persisted experiment assignment is missing or invalid.");
        if (string.IsNullOrWhiteSpace(executionSourcePath))
            throw new InvalidDataException("The assigned execution has no current source path.");

        PredictionShadowExperimentAssignment assignment = binding.Assignment;
        PredictionShadowExperimentFreeze freeze = Load(assignment.ExperimentId) ??
            throw new InvalidDataException($"No immutable freeze exists for assigned experiment '{assignment.ExperimentId}'.");
        PredictionShadowFreezeTarget? target = freeze.Targets.SingleOrDefault(item => item.Slot == assignment.Slot);
        if (target is null || assignment.Stratum != target.Stratum)
            throw new InvalidDataException("The assignment slot or stratum does not match a frozen Target.");

        PredictionShadowFreezeSource expectedSource;
        PredictionShadowFreezeReplacementPolicy policy = freeze.ReplacementPolicy;
        if (assignment.Role == PredictionShadowExperimentRole.Target)
        {
            if (assignment.Attempt != policy.InitialAttempt || target.Role != assignment.Role)
                throw new InvalidDataException("The assigned Target attempt or role does not match the freeze.");
            expectedSource = target.Source;
        }
        else if (assignment.Role == PredictionShadowExperimentRole.Replacement)
        {
            if (assignment.Attempt != policy.ReplacementAttempt ||
                assignment.Role != policy.ReplacementRole || !policy.SameStratumRequired ||
                !policy.PreserveExperimentSlotAndStratum || policy.MaximumReplacementsPerSlot < 1)
                throw new InvalidDataException("The replacement attempt or role is not permitted by the freeze.");

            PredictionShadowFreezeSource[] matchingReserves = freeze.Reserves
                .Where(reserve => reserve.Stratum == assignment.Stratum &&
                    SamePath(reserve.Source.SourcePath, binding.SourcePath) &&
                    reserve.Source.SourceLengthBytes == binding.SourceLengthBytes &&
                    reserve.Source.SourceLastWriteTimeUtcTicks == binding.SourceLastWriteTimeUtcTicks)
                .Select(reserve => reserve.Source)
                .ToArray();
            if (matchingReserves.Length != 1)
                throw new InvalidDataException("The replacement source is not a unique reserve in the frozen stratum.");
            expectedSource = matchingReserves[0];
        }
        else
        {
            throw new InvalidDataException("The assignment role is not supported by the frozen protocol.");
        }

        if (!SamePath(binding.SourcePath, expectedSource.SourcePath))
            throw new InvalidDataException("The persisted assignment source path conflicts with its frozen roster member.");
        if (!SamePath(executionSourcePath, binding.SourcePath))
            throw new InvalidDataException("The current execution source path no longer matches the persisted assignment binding.");
        if (binding.SourceLengthBytes != expectedSource.SourceLengthBytes)
            throw new InvalidDataException("The persisted assignment length conflicts with the frozen roster member.");
        if (binding.SourceLastWriteTimeUtcTicks != expectedSource.SourceLastWriteTimeUtcTicks)
            throw new InvalidDataException("The persisted assignment last-write time conflicts with the frozen roster member.");

        FileInfo current;
        try
        {
            current = new FileInfo(Path.GetFullPath(executionSourcePath));
            if (!current.Exists)
                throw new FileNotFoundException("The assigned source file is missing.", executionSourcePath);
            current.Refresh();
            if (!current.Exists)
                throw new FileNotFoundException("The assigned source file is missing.", executionSourcePath);
            if (current.Length != binding.SourceLengthBytes)
                throw new InvalidDataException("The assigned source length changed after assignment.");
            if (current.LastWriteTimeUtc.Ticks != binding.SourceLastWriteTimeUtcTicks)
                throw new InvalidDataException("The assigned source last-write time changed after assignment.");
        }
        catch (FileNotFoundException)
        {
            throw;
        }
          catch (DirectoryNotFoundException)
          {
              throw;
          }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidDataException($"The assigned source identity could not be verified: {ex.Message}", ex);
        }

        if (!string.IsNullOrWhiteSpace(observedSourceFamilyKey) &&
            !string.Equals(observedSourceFamilyKey, expectedSource.FamilyKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The execution plan source-family key conflicts with the immutable freeze.");

        return expectedSource.FamilyKey;
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static PredictionShadowExperimentFreeze IdenticalOrConflict(PredictionShadowExperimentFreeze existing, byte[] proposed)
    {
        if (!Serialize(existing).AsSpan().SequenceEqual(proposed))
            throw new InvalidDataException("ExperimentId is already frozen with conflicting content; overwriting is prohibited.");
        return existing;
    }

    private static byte[] Serialize(PredictionShadowExperimentFreeze freeze) => JsonSerializer.SerializeToUtf8Bytes(freeze with
    {
        GitCommit = freeze.GitCommit.ToLowerInvariant(),
        Strata = freeze.Strata.OrderBy(s => s.Stratum).ToArray(),
        Reserves = freeze.Reserves.OrderBy(r => r.Stratum).ThenBy(r => r.ReserveOrder).ToArray(),
        Exclusions = freeze.Exclusions.OrderBy(e => e.Source.SourcePath, StringComparer.Ordinal)
            .ThenBy(e => e.Source.SourceLengthBytes).ThenBy(e => e.Source.SourceLastWriteTimeUtcTicks)
            .ThenBy(e => e.Source.FamilyKey, StringComparer.Ordinal).ThenBy(e => e.Source.CandidateId)
            .ThenBy(e => e.Reason, StringComparer.Ordinal).ToArray(),
        JournalSnapshot = freeze.JournalSnapshot.OrderBy(j => j.JournalType).ThenBy(j => j.Generation).ThenBy(j => j.SourcePath, StringComparer.Ordinal)
            .Select(j => j with { Sha256 = j.Sha256.ToLowerInvariant() }).ToArray(),
        ReplacementPolicy = freeze.ReplacementPolicy with
        {
            ValidReasons = freeze.ReplacementPolicy.ValidReasons.OrderBy(r => r, StringComparer.Ordinal).ToArray(),
            ProhibitedOutcomeBasedReasons = freeze.ReplacementPolicy.ProhibitedOutcomeBasedReasons.OrderBy(r => r, StringComparer.Ordinal).ToArray()
        }
    }, Json);

    private static PredictionShadowExperimentFreeze Deserialize(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            RejectDuplicateProperties(document.RootElement);
            return JsonSerializer.Deserialize<PredictionShadowExperimentFreeze>(bytes, Json) ?? throw new InvalidDataException("Empty freeze artifact.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid freeze JSON.", ex); }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("Duplicate freeze JSON property: " + property.Name);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }
}
