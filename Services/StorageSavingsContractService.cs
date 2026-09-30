using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>The dedicated Phase 1 acceptance policy; never selects encoding settings.</summary>
public static class StorageSavingsContractService
{
    public const long LargeSourceBoundaryBytes = 1024L * 1024 * 1024;
    public const long LargeSourceMinimumSavingsBytes = 100L * 1024 * 1024;
    public const int RequiredSavingsPercent = 10;

    public static StorageSavingsContract Resolve(bool applies, long? sourceBytes) => !applies
        ? StorageSavingsContract.Disabled
        : ResolveRequirements(sourceBytes, RequiredSavingsPercent,
            sourceBytes >= LargeSourceBoundaryBytes ? LargeSourceMinimumSavingsBytes : null);

    public static StorageSavingsContract ResolveRequirements(
        long? sourceBytes, int minimumSavingsPercent, long? minimumSavingsBytes = null)
    {
        if (sourceBytes is not > 0 || minimumSavingsPercent is < 0 or > 100 || minimumSavingsBytes is < 0)
            return new(true, sourceBytes, minimumSavingsPercent, minimumSavingsBytes, null,
                "Storage acceptance cannot be resolved from an unknown/invalid source size or savings requirement.");

        // ceil(source * percent / 100), with no overflowing multiplication and no floating point.
        long percentageBytes = sourceBytes.Value / 100 * minimumSavingsPercent +
            (sourceBytes.Value % 100 * minimumSavingsPercent + 99) / 100;
        long requiredBytes = Math.Max(percentageBytes, minimumSavingsBytes ?? 0);
        long maximum = sourceBytes.Value - requiredBytes;
        return new(true, sourceBytes, minimumSavingsPercent, minimumSavingsBytes, maximum,
            $"Require at least {minimumSavingsPercent}% total-file savings" +
            (minimumSavingsBytes is long absolute ? $" and {absolute} bytes" : "") + ".");
    }

    /// <summary>Capture physical source bytes at snapshot creation; cached estimates are not authoritative.</summary>
    public static StorageSavingsContract Capture(bool applies, EncodingInputSource input)
    {
        if (!applies) return StorageSavingsContract.Disabled;
        try
        {
            IEnumerable<string> paths = input.Kind == EncodingInputKind.File
                ? new[] { input.SourcePath }
                : input.SourceFiles;
            long total = 0;
            foreach (string path in paths)
            {
                long size = new FileInfo(path).Length;
                if (size <= 0) return Resolve(true, null);
                total = checked(total + size);
            }
            return Resolve(true, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or NotSupportedException)
        {
            return Resolve(true, null) with { Reason = "Unable to verify source bytes: " + ex.Message };
        }
    }

    public static StorageSavingsEvaluation Evaluate(StorageSavingsContract contract, long actualOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(contract);
        long? savings = contract.SourceSizeBytes is > 0 && actualOutputBytes >= 0
            ? contract.SourceSizeBytes.Value - actualOutputBytes : null;
        double? percent = savings.HasValue ? savings.Value * 100d / contract.SourceSizeBytes!.Value : null;
        if (!contract.Applies)
            return new(contract, StorageSavingsAcceptance.NotApplicable, actualOutputBytes, savings, percent, contract.Reason);
        bool accepted = contract.SourceSizeBytes is > 0 && actualOutputBytes > 0 &&
            contract.MaximumAcceptedOutputBytes is long maximum && actualOutputBytes <= maximum;
        string reason = accepted ? "Validated candidate satisfies the storage-savings contract." :
            contract.MaximumAcceptedOutputBytes is null ? contract.Reason :
            $"Insufficient savings: validated candidate is {actualOutputBytes} bytes; maximum accepted output is {contract.MaximumAcceptedOutputBytes} bytes. " + contract.Reason;
        return new(contract, accepted ? StorageSavingsAcceptance.Accepted : StorageSavingsAcceptance.Rejected,
            actualOutputBytes, savings, percent, reason);
    }

    public static bool HasAcceptedEvidence(StorageSavingsContract contract, StorageSavingsEvaluation? evidence, long? finalBytes) =>
        evidence?.Acceptance != StorageSavingsAcceptance.Rejected &&
        (!contract.Applies || evidence is { Acceptance: StorageSavingsAcceptance.Accepted } &&
            evidence.Contract == contract && finalBytes == evidence.CandidateOutputBytes &&
            Evaluate(contract, evidence.CandidateOutputBytes).Acceptance == StorageSavingsAcceptance.Accepted);

    public static string Describe(StorageSavingsEvaluation evaluation) =>
        $"Storage acceptance: {evaluation.Acceptance}; source={evaluation.Contract.SourceSizeBytes} bytes; " +
        $"candidate={evaluation.CandidateOutputBytes} bytes; savings={evaluation.ActualSavingsBytes} bytes " +
        $"({evaluation.ActualSavingsPercent:0.######}%); required={evaluation.Contract.MinimumSavingsPercent}%" +
        (evaluation.Contract.MinimumSavingsBytes is long absolute ? $" and {absolute} bytes" : "") +
        $"; maximum output={evaluation.Contract.MaximumAcceptedOutputBytes} bytes. {evaluation.Reason}";
}
