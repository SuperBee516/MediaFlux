namespace MediaFlux.Services.EncodingV2;

/// <summary>Creates an ordinary V2 core with one explicit, per-item certification seam.</summary>
public static class V2QueueCertificationCoreFactory
{
    public static V2EncodingCore Create(
        IV2SourceMetadataProvider metadata,
        V2EncodePlanner planner,
        IV2ProductionExecutor production,
        IV2CandidateValidator validator,
        IV2OutputPromoter promoter,
        string ffmpegPath,
        V2QueueFailureInjection? injection,
        Action injectionTriggered)
    {
        ArgumentNullException.ThrowIfNull(injectionTriggered);
        IV2SourceDeletionService sourceDeletion = new V2SafeSourceDeletionService();

        if (injection is not null)
        {
            switch (injection.Point)
            {
                case V2QueueFailurePoint.BeforeProduction:
                case V2QueueFailurePoint.ProductionExecution:
                    production = new InjectedProductionExecutor(injection.Point, injectionTriggered);
                    break;
                case V2QueueFailurePoint.Validation:
                    validator = new InjectedValidator(injectionTriggered);
                    break;
                case V2QueueFailurePoint.Promotion:
                    promoter = new InjectedPromoter(injectionTriggered);
                    break;
                case V2QueueFailurePoint.SourceDeletion:
                    sourceDeletion = new InjectedSourceDeletion(injectionTriggered);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(injection), injection.Point, "Unsupported certification failure point.");
            }
        }

        return new V2EncodingCore(metadata, planner, production, validator, promoter, ffmpegPath, sourceDeletion);
    }

    private sealed class InjectedProductionExecutor(
        V2QueueFailurePoint point,
        Action triggered) : IV2ProductionExecutor
    {
        public Task<V2ProcessOutcome> ExecuteAsync(FrozenEncodePlan plan, Action<TimeSpan>? processStarted = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            triggered();
            int exitCode = point == V2QueueFailurePoint.BeforeProduction ? 86 : 87;
            return Task.FromResult(new V2ProcessOutcome(exitCode, false, TimeSpan.Zero, TimeSpan.Zero,
                $"V2 queue certification injected {point} failure; no FFmpeg process was launched."));
        }
    }

    private sealed class InjectedValidator(Action triggered) : IV2CandidateValidator
    {
        public Task<V2ValidationOutcome> ValidateAsync(FrozenEncodePlan plan, V2MediaFacts source,
            string candidatePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            triggered();
            return Task.FromResult(new V2ValidationOutcome(false,
                "V2 queue certification injected validation failure; candidate was not promoted.",
                ErrorCode: "CertificationInjectedValidationFailure"));
        }
    }

    private sealed class InjectedPromoter(Action triggered) : IV2OutputPromoter
    {
        public void Promote(string stagingPath, string outputPath)
        {
            triggered();
            throw new IOException("V2 queue certification injected promotion failure; candidate was not promoted.");
        }
    }

    private sealed class InjectedSourceDeletion(Action triggered) : IV2SourceDeletionService
    {
        public bool TryDelete(FrozenEncodePlan plan, V2ValidationOutcome validation,
            CancellationToken cancellationToken, out string message)
        {
            cancellationToken.ThrowIfCancellationRequested();
            triggered();
            message = "V2 queue certification injected source-deletion failure; valid output and source were retained.";
            return false;
        }
    }
}
