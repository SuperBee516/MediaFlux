using System.Security.Cryptography;
using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>
/// Captures prospective Ratio/Direct evidence and later attaches terminal
/// outcomes. Prediction admission remains delegated to the existing comparator.
/// </summary>
public sealed class NvencQualityModePredictionShadowService
{
    private readonly PredictionShadowObservationJournal _journal;
    private readonly PredictionShadowComplexitySamplingService _sampler;
    private readonly Action<string>? _diagnostic;
    private readonly Func<DateTime> _utcNow;

    public NvencQualityModePredictionShadowService(
        PredictionShadowObservationJournal journal,
        PredictionShadowComplexitySamplingService sampler,
        Action<string>? diagnostic = null,
        Func<DateTime>? utcNow = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _diagnostic = diagnostic;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<PredictionShadowFrozenObservation?> CaptureAsync(
        EncodingPlanSnapshot snapshot,
        string sourcePath,
        string settingsSignature,
        IEnumerable<EncodingStatisticsRecord> finalizedHistory,
        string mediaFluxVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(finalizedHistory);
        cancellationToken.ThrowIfCancellationRequested();
        EncodingPlan plan = snapshot.Plan;
        SourceAdaptiveShadowCalibration? decision = plan.SourceAdaptiveShadow;
        if (!IsEligible(plan, decision, sourcePath, settingsSignature))
            return null;

        DateTime evidenceCutoffUtc = _utcNow().ToUniversalTime();
        double durationSeconds = plan.Source!.DurationSeconds!.Value;
        double sourceVideoKbps = decision!.SourceVideoBitrateKbps!.Value;
        string familyKey = NvencQualityModeVideoBitratePredictionService.GetSourceFamilyKey(
            decision, durationSeconds, sourcePath);
        var request = new NvencQualityModePredictionRequest(
            familyKey,
            decision.SourceCodec,
            decision.OutputCodec,
            settingsSignature,
            decision.FinalExecutionCq!.Value,
            sourceVideoKbps,
            decision.PlannedWidth!.Value,
            decision.PlannedHeight!.Value,
            decision.PlannedFps!.Value,
            decision.MaterialTransformationActive);

        // The service receives only finalized observations completed before this
        // cutoff. The peer/admission implementation itself remains shared.
        EncodingStatisticsRecord[] priorHistory = finalizedHistory
            .Where(record => record.EndUtc != default && record.EndUtc.ToUniversalTime() < evidenceCutoffUtc)
            .ToArray();
        QualityModePairedPrediction pair =
            NvencQualityModeVideoBitratePredictionService.PredictBoth(request, priorHistory);

        PredictionShadowSamplingObservation complexity;
        try
        {
            complexity = await _sampler.AnalyzeAsync(sourcePath, durationSeconds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            complexity = new PredictionShadowSamplingObservation
            {
                SamplerVersion = PredictionShadowComplexitySamplingService.SamplerVersion,
                Status = PredictionShadowSamplingStatus.Unavailable,
                FailureReason = OneLine(ex.Message)
            };
            Diagnose($"[PredictionShadow] Complexity sampling unavailable: {OneLine(ex.Message)}");
        }
        cancellationToken.ThrowIfCancellationRequested();

        double? sourcePps = plan.Source.Width is > 0 && plan.Source.Height is > 0 && plan.Source.FrameRate is > 0
            ? (double)plan.Source.Width.Value * plan.Source.Height.Value * plan.Source.FrameRate.Value
            : null;
        double? sourceBpp = sourcePps is > 0 ? sourceVideoKbps * 1000d / sourcePps.Value : null;
        string observationId = snapshot.PlanId.ToString("N");
        var observation = new PredictionShadowFrozenObservation
        {
            SchemaVersion = 2,
            ObservationId = observationId,
            PlanId = snapshot.PlanId,
            SourceFamilyKey = familyKey,
            SourcePathSha256 = HashPath(sourcePath),
            PredictionEvidenceCutoffUtc = evidenceCutoffUtc,
            FrozenUtc = _utcNow().ToUniversalTime(),
            MediaFluxVersion = mediaFluxVersion,
            SourceCodec = decision.SourceCodec,
            SourceWidth = plan.Source.Width,
            SourceHeight = plan.Source.Height,
            SourceFps = plan.Source.FrameRate,
            SourceVideoBitrateKbps = sourceVideoKbps,
            SourceBitsPerPixel = sourceBpp,
            SourcePixelsPerSecond = sourcePps,
            SourceBytes = decision.SourceTotalBytes,
            OutputCodec = decision.OutputCodec,
            EncoderId = decision.EncoderId,
            Preset = decision.Preset,
            Cq = decision.FinalExecutionCq,
            SettingsSignature = settingsSignature,
            ComparatorWidth = decision.PlannedWidth,
            ComparatorHeight = decision.PlannedHeight,
            ComparatorFps = decision.PlannedFps,
            Ratio = ToForecast(pair.Ratio),
            Direct = ToForecast(pair.Direct),
            AdmittedPeerSourceFamilyKeys = pair.AdmittedPeerSourceFamilyKeys.ToArray(),
            RatioAndDirectSharePeers = pair.AdmittedPeerSourceFamilyKeys.Count == pair.Ratio.IndependentSourceCount &&
                pair.Ratio.IndependentSourceCount == pair.Direct.IndependentSourceCount,
            Complexity = complexity
        };

        try
        {
            bool journalIsComplete = _journal.TryReadEventsForTemporalComparison(
                out IReadOnlyList<PredictionShadowJournalEvent> journalEvents);
            observation = observation with
            {
                TemporalNeighbor = new PredictionShadowTemporalNeighborComparator()
                    .Compare(observation, journalIsComplete, journalEvents)
            };
        }
        catch (Exception ex)
        {
            Diagnose($"[PredictionShadow] Temporal comparator abstained: {OneLine(ex.Message)}");
            observation = observation with
            {
                TemporalNeighbor = PredictionShadowTemporalNeighborComparator.CreateAbstention(
                    "TemporalComparatorFailure", complexity.TemporalFrameDifference)
            };
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_journal.AppendFrozen(observation))
                Diagnose($"[PredictionShadow] Frozen observation {observationId} was already present.");
            else
                Diagnose(
                    $"[PredictionShadow] Frozen observation {observationId}; peers={pair.AdmittedPeerSourceFamilyKeys.Count}; " +
                    $"sampling={complexity.Status}; elapsed={complexity.WallClockMilliseconds:0}ms; " +
                    $"temporal={observation.TemporalNeighbor?.Ratio.Reason ?? "unavailable"}.");
            return observation;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw;
            Diagnose($"[PredictionShadow] Research persistence unavailable: {OneLine(ex.Message)}");
            return null;
        }
    }

    public bool RecordOutcome(
        string observationId,
        string state,
        string terminalResult,
        string validationState,
        string finalizationState,
        bool recoveredSuccessful,
        double? actualOutputVideoBitrateKbps,
        long? outputBytes,
        DateTime recordedUtc)
    {
        if (!_journal.TryGetFrozen(observationId, out PredictionShadowFrozenObservation? frozen) || frozen == null)
            return false;

        long? sizeChangeBytes = frozen.SourceBytes is > 0 && outputBytes is >= 0
            ? outputBytes.Value - frozen.SourceBytes.Value
            : null;
        double? outputSourceRateRatio = SafeRatio(actualOutputVideoBitrateKbps, frozen.SourceVideoBitrateKbps);
        double? sizeChangePercent = frozen.SourceBytes is > 0 && outputBytes is >= 0
            ? (outputBytes.Value - frozen.SourceBytes.Value) * 100d / frozen.SourceBytes.Value
            : null;
        double? validTemporalActual = !recoveredSuccessful &&
            string.Equals(state, "Completed", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(terminalResult, "Completed", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(validationState, "Passed", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(finalizationState, "Passed", StringComparison.OrdinalIgnoreCase)
                ? actualOutputVideoBitrateKbps
                : null;
        var outcome = new PredictionShadowOutcome
        {
            State = state,
            TerminalResult = terminalResult,
            ValidationState = validationState,
            FinalizationState = finalizationState,
            RecoveredSuccessful = recoveredSuccessful,
            ActualOutputVideoBitrateKbps = actualOutputVideoBitrateKbps,
            SourceBytes = frozen.SourceBytes,
            OutputBytes = outputBytes,
            OutputToSourceVideoBitrateRatio = outputSourceRateRatio,
            SizeChangeBytes = sizeChangeBytes,
            SizeChangePercent = sizeChangePercent,
            RatioSignedErrorKbps = SignedErrorKbps(frozen.Ratio.PredictedVideoBitrateKbps, actualOutputVideoBitrateKbps),
            RatioSignedErrorPercent = SignedErrorPercent(frozen.Ratio.PredictedVideoBitrateKbps, actualOutputVideoBitrateKbps),
            RatioAbsoluteErrorPercent = AbsoluteErrorPercent(frozen.Ratio.PredictedVideoBitrateKbps, actualOutputVideoBitrateKbps),
            DirectSignedErrorKbps = SignedErrorKbps(frozen.Direct.PredictedVideoBitrateKbps, actualOutputVideoBitrateKbps),
            DirectSignedErrorPercent = SignedErrorPercent(frozen.Direct.PredictedVideoBitrateKbps, actualOutputVideoBitrateKbps),
            DirectAbsoluteErrorPercent = AbsoluteErrorPercent(frozen.Direct.PredictedVideoBitrateKbps, actualOutputVideoBitrateKbps),
            TemporalNeighborRatioSignedErrorKbps = SignedErrorKbps(
                frozen.TemporalNeighbor?.Ratio.PredictedVideoBitrateKbps, validTemporalActual),
            TemporalNeighborRatioSignedErrorPercent = SignedErrorPercent(
                frozen.TemporalNeighbor?.Ratio.PredictedVideoBitrateKbps, validTemporalActual),
            TemporalNeighborRatioAbsoluteErrorPercent = AbsoluteErrorPercent(
                frozen.TemporalNeighbor?.Ratio.PredictedVideoBitrateKbps, validTemporalActual),
            TemporalNeighborDirectSignedErrorKbps = SignedErrorKbps(
                frozen.TemporalNeighbor?.Direct.PredictedVideoBitrateKbps, validTemporalActual),
            TemporalNeighborDirectSignedErrorPercent = SignedErrorPercent(
                frozen.TemporalNeighbor?.Direct.PredictedVideoBitrateKbps, validTemporalActual),
            TemporalNeighborDirectAbsoluteErrorPercent = AbsoluteErrorPercent(
                frozen.TemporalNeighbor?.Direct.PredictedVideoBitrateKbps, validTemporalActual)
        };

        try
        {
            return _journal.AppendOutcome(observationId, outcome, recordedUtc.ToUniversalTime());
        }
        catch (Exception ex)
        {
            Diagnose($"[PredictionShadow] Outcome persistence unavailable: {OneLine(ex.Message)}");
            return false;
        }
    }

    private static bool IsEligible(
        EncodingPlan plan,
        SourceAdaptiveShadowCalibration? decision,
        string sourcePath,
        string settingsSignature) =>
        plan.IsAvailable && plan.Validation?.SampleComparison != true &&
        !string.IsNullOrWhiteSpace(sourcePath) && !string.IsNullOrWhiteSpace(settingsSignature) &&
        decision is { IsPrimaryCalibrationCandidate: true, MaterialTransformationActive: false,
            FinalExecutionCq: >= 0 and <= 51, SourceVideoBitrateKbps: > 0,
            PlannedWidth: > 0, PlannedHeight: > 0, PlannedFps: > 0 } &&
        decision.SourceCodec.Equals("h264", StringComparison.OrdinalIgnoreCase) &&
        decision.OutputCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase) &&
        decision.EncoderId.Equals(VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase) &&
        plan.Source?.DurationSeconds is > 0 && double.IsFinite(plan.Source.DurationSeconds.Value);

    private static PredictionShadowForecast ToForecast(NvencQualityModePredictionResult result) => new()
    {
        PredictedVideoBitrateKbps = result.PredictedVideoBitrateKbps,
        Confidence = result.Confidence.ToString(),
        Reason = result.Reason.ToString(),
        IndependentPeerCount = result.IndependentSourceCount,
        ObservedLowVideoBitrateKbps = result.ObservedLowVideoBitrateKbps,
        ObservedHighVideoBitrateKbps = result.ObservedHighVideoBitrateKbps
    };

    private static string HashPath(string path)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar));
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        catch
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.Trim())));
        }
    }

    private static double? SafeRatio(double? numerator, double? denominator) =>
        numerator is > 0 && denominator is > 0 && double.IsFinite(numerator.Value) && double.IsFinite(denominator.Value)
            ? numerator.Value / denominator.Value
            : null;

    private static double? SignedErrorKbps(double? predicted, double? actual) =>
        predicted is > 0 && actual is > 0 && double.IsFinite(predicted.Value) && double.IsFinite(actual.Value)
            ? predicted.Value - actual.Value
            : null;

    private static double? SignedErrorPercent(double? predicted, double? actual) =>
        predicted is > 0 && actual is > 0 && double.IsFinite(predicted.Value) && double.IsFinite(actual.Value)
            ? (predicted.Value - actual.Value) / actual.Value * 100d
            : null;

    private static double? AbsoluteErrorPercent(double? predicted, double? actual) =>
        SignedErrorPercent(predicted, actual) is double error ? Math.Abs(error) : null;

    private void Diagnose(string message)
    {
        try { _diagnostic?.Invoke(message); }
        catch { /* Optional research diagnostics cannot become an encode dependency. */ }
    }

    private static string OneLine(string value)
    {
        string line = (value ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "unknown error";
        return line.Length <= 240 ? line : line[..240] + "…";
    }
}
