using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Evaluates whether a frozen plan is worth executing. Advisory only.</summary>
public static class EncodingRecommendationService
{
    public static EncodingRecommendation Evaluate(EncodingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsAvailable)
            return Result(EncodingRecommendationKind.Review, "The encoding plan is unavailable.", [], EncodingHistoricalConfidence.None, null, null, null, EncodingRecommendationRisk.Unknown, []);

        double? sourceMb = plan.Source?.SizeBytes is > 0 ? plan.Source.SizeBytes.Value / 1048576d : null;
        EncodingHistoricalPrediction? history = plan.Estimates.HistoricalPrediction;
        double? outputMb = history?.PredictedOutputSizeMb is > 0 ? history.PredictedOutputSizeMb : plan.Estimates.EstimatedOutputSizeMb is > 0 ? plan.Estimates.EstimatedOutputSizeMb : null;
        double? savingsMb = sourceMb is > 0 && outputMb is > 0 ? sourceMb - outputMb : null;
        double? savingsPercent = savingsMb.HasValue && sourceMb is > 0 ? savingsMb.Value / sourceMb.Value * 100d : null;
        bool explicitIntent = plan.Estimates.EstimatedOutputSizeMb is > 0 ||
            (plan.Video is { } video && plan.Source is { } source &&
             (!string.Equals(video.Codec, source.Codec, StringComparison.OrdinalIgnoreCase) ||
              video.EffectiveWidth is > 0 && source.Width is > 0 && video.EffectiveWidth != source.Width ||
              video.EffectiveHeight is > 0 && source.Height is > 0 && video.EffectiveHeight != source.Height));

        EncodingRecommendationRisk risk = ResolveRisk(plan);
        var reasons = new List<string>();
        if (plan.Source?.BitrateKbps is > 0 && plan.Source.Width is > 0 && plan.Source.Height is > 0)
            reasons.Add($"Source: {plan.Source.Codec} {plan.Source.Width}×{plan.Source.Height} at {plan.Source.BitrateKbps.Value:0} kbps.");
        if (outputMb is > 0)
            reasons.Add($"Estimated output: {outputMb.Value:0.#} MB.");
        if (history?.IsAvailable == true)
            reasons.Add($"Historical estimate uses {history.SampleCount} comparable job(s) at {history.Confidence} confidence.");
        if (risk == EncodingRecommendationRisk.High)
            reasons.Insert(0, "Source bitrate is unusually low for the planned source resolution; further compression may reduce visible quality.");

        if (risk == EncodingRecommendationRisk.High)
            return Result(EncodingRecommendationKind.Review, reasons[0], reasons, Confidence(plan), outputMb, savingsMb, savingsPercent, risk, Facts(plan));
        if (outputMb is null || sourceMb is null)
            return Result(EncodingRecommendationKind.Review, "Insufficient reliable size evidence to decide safely.", reasons, Confidence(plan), outputMb, savingsMb, savingsPercent, risk, Facts(plan));
        if (savingsPercent < 0)
            return Result(EncodingRecommendationKind.Review, "Estimated output is larger than the source; review the explicit transformation before encoding.", reasons, Confidence(plan), outputMb, savingsMb, savingsPercent, risk, Facts(plan));
        if (explicitIntent && savingsPercent < 10)
            return Result(EncodingRecommendationKind.Review, "Explicit encoding intent is present, but the predicted storage benefit is small.", reasons, Confidence(plan), outputMb, savingsMb, savingsPercent, risk, Facts(plan));
        if (savingsPercent >= 15)
            return Result(EncodingRecommendationKind.Encode, $"Estimated {savingsPercent.Value:0.#}% storage reduction with {risk.ToString().ToLowerInvariant()} quality risk.", reasons, Confidence(plan), outputMb, savingsMb, savingsPercent, risk, Facts(plan));
        return Result(EncodingRecommendationKind.Skip, "Source is already efficiently encoded; predicted savings are negligible.", reasons, Confidence(plan), outputMb, savingsMb, savingsPercent, risk, Facts(plan));
    }

    private static EncodingRecommendation Result(EncodingRecommendationKind kind, string primary, IReadOnlyList<string> reasons, EncodingHistoricalConfidence confidence, double? output, double? mb, double? percent, EncodingRecommendationRisk risk, IReadOnlyList<EncodingPlanItem> facts) => new(kind, primary, reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), confidence, output, mb, percent, risk, facts);
    private static EncodingHistoricalConfidence Confidence(EncodingPlan plan) => plan.Estimates.HistoricalPrediction?.Confidence is { } c && c != EncodingHistoricalConfidence.None ? c : plan.Estimates.EstimatedOutputSizeMb is > 0 ? EncodingHistoricalConfidence.Medium : EncodingHistoricalConfidence.Low;
    private static EncodingRecommendationRisk ResolveRisk(EncodingPlan plan)
    {
        if (plan.Source?.BitrateKbps is not > 0 || plan.Source.Width is not > 0 || plan.Source.Height is not > 0 || plan.Source.FrameRate is not > 0) return EncodingRecommendationRisk.Unknown;
        double bpp = plan.Source.BitrateKbps.Value * 1000d / (plan.Source.Width.Value * (double)plan.Source.Height.Value * plan.Source.FrameRate.Value);
        return bpp < 0.035 ? EncodingRecommendationRisk.High : bpp < 0.06 ? EncodingRecommendationRisk.Medium : EncodingRecommendationRisk.Low;
    }
    private static IReadOnlyList<EncodingPlanItem> Facts(EncodingPlan plan) => new[]
    {
        new EncodingPlanItem("Source codec", plan.Source?.Codec ?? "Unknown"),
        new EncodingPlanItem("Planned codec", plan.Video?.Codec ?? "Unknown"),
        new EncodingPlanItem("Quality risk", ResolveRisk(plan).ToString())
    };
}
