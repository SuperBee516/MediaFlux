namespace MediaFlux.Models;

/// <summary>
/// Formats already-published queue analysis for the grid tooltip and Details view.
/// It intentionally does not evaluate recommendations or estimate output sizes.
/// </summary>
public sealed class QueueAnalysisPresentation
{
    private QueueAnalysisPresentation(
        string status,
        string? recommendation,
        string? confidence,
        string? estimatedResult,
        string? savingsLabel,
        string? savingsValue,
        EncodingQualityResolution? quality,
        IReadOnlyList<string> reasons)
    {
        Status = status;
        Recommendation = recommendation;
        Confidence = confidence;
        EstimatedResult = estimatedResult;
        SavingsLabel = savingsLabel;
        SavingsValue = savingsValue;
        Quality = quality;
        Reasons = reasons;
    }

    public string Status { get; }
    public string? Recommendation { get; }
    public string? Confidence { get; }
    public string? EstimatedResult { get; }
    public string? SavingsLabel { get; }
    public string? SavingsValue { get; }
    public EncodingQualityResolution? Quality { get; }
    public IReadOnlyList<string> Reasons { get; }
    public bool IsAvailable => Recommendation != null || Quality != null;

    public QueueAnalysisPresentation WithSizeEvidence(EmpiricalPreviewResult? preview, EncodingSizePredictionCalibration? calibration)
    {
        bool fallback = calibration?.EstimateModelId == "GenericBppV1";
        string? estimate = EstimatedResult;
        if (fallback && estimate is not null) estimate = "~" + estimate + " (low confidence)";
        if (preview is { LikelyDisposition: AdaptiveSelectionDisposition.Skipped } && estimate is not null)
            estimate += $" at {preview.Mechanism.ToString().ToUpperInvariant()}{preview.DisplayQuality} · likely skip";
        return new(Status, Recommendation, fallback ? "Low" : Confidence, estimate,
            SavingsLabel, SavingsValue, Quality, preview is null ? Reasons : Reasons.Append(preview.Description).ToArray());
    }

    public static QueueAnalysisPresentation Create(
        SmartEncodeRecommendation? recommendation,
        double sourceMb = 0,
        double estimatedOutputMb = 0,
        EncodingQualityResolution? quality = null)
    {
        if (recommendation == null)
        {
            return new QueueAnalysisPresentation(
                "Queue analysis has not been performed for this file.",
                null, null, null, null, null, quality, Array.Empty<string>());
        }

        string? estimatedResult = sourceMb > 0 && estimatedOutputMb > 0
            ? $"{FormatSize(sourceMb)} → {FormatSize(estimatedOutputMb)}"
            : null;
        string? savingsLabel = null;
        string? savingsValue = null;
        if (recommendation.EstimatedSavingsPercent is double savingsPercent &&
            recommendation.EstimatedSavingsMb is double savingsMb)
        {
            bool increase = savingsPercent < 0 || savingsMb < 0;
            savingsLabel = increase ? "Estimated increase" : "Estimated savings";
            savingsValue = increase
                ? $"{Math.Abs(savingsPercent):0.#}% (+{Math.Abs(savingsMb):0.#} MB)"
                : $"{Math.Max(0, savingsPercent):0.#}% ({Math.Max(0, savingsMb):0.#} MB)";
        }

        return new QueueAnalysisPresentation(
            string.Empty,
            recommendation.DisplayName,
            recommendation.Confidence.ToString(),
            estimatedResult,
            savingsLabel,
            savingsValue,
            quality,
            recommendation.Reasons
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public static QueueAnalysisPresentation CreateAdaptivePending(
        double sourceMb = 0,
        double estimatedOutputMb = 0,
        EncodingQualityResolution? quality = null)
    {
        string? estimatedResult = sourceMb > 0 && estimatedOutputMb > 0
            ? $"{FormatSize(sourceMb)} → {FormatSize(estimatedOutputMb)}"
            : null;
        return new QueueAnalysisPresentation(
            string.Empty,
            "Adaptive pending",
            null,
            estimatedResult,
            null,
            null,
            quality,
            ["The final Source Adaptive quality decision has not been selected yet, so this advisory recommendation is pending."]);
    }

    public static QueueAnalysisPresentation Create(EncodingRecommendation? recommendation)
    {
        if (recommendation == null)
            return Create((SmartEncodeRecommendation?)null);
        string? savings = recommendation.ExpectedSavingsPercent is double percent && recommendation.ExpectedSavingsMb is double mb
            ? (percent < 0 ? $"{Math.Abs(percent):0.#}% increase" : $"{Math.Max(0, percent):0.#}% ({Math.Max(0, mb):0.#} MB)")
            : null;
        return new QueueAnalysisPresentation(
            string.Empty,
            recommendation.DisplayName,
            recommendation.Confidence.ToString(),
            recommendation.EstimatedOutputSizeMb is > 0 ? FormatSize(recommendation.EstimatedOutputSizeMb.Value) : null,
            savings == null ? null : "Expected savings",
            savings,
            null,
            new[] { recommendation.PrimaryReason }.Concat(recommendation.SupportingReasons).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public string BuildTooltip()
    {
        if (!IsAvailable)
            return Status;

        var lines = new List<string>();
        if (Recommendation != null)
            lines.Add(Recommendation);
        if (Quality != null)
            lines.AddRange(EncodingQualityPresentation.CreateItems(Quality)
                .Select(item => $"{item.Label}: {item.Value}"));
        if (SavingsLabel != null && SavingsValue != null)
            lines.Add($"{SavingsLabel}: {SavingsValue}");
        if (Confidence != null)
            lines.Add($"Confidence: {Confidence}");
        lines.AddRange(Reasons.Select(reason => $"• {reason}"));
        lines.AddRange(EncodingQualityPresentation.CreateReasons(Quality).Select(reason => $"• {reason}"));
        return string.Join(Environment.NewLine, lines);
    }

    public string BuildQueueTooltip()
    {
        if (!IsAvailable)
            return Status;

        var lines = new List<string>();
        if (Recommendation != null)
            lines.Add(Recommendation);
        if (SavingsLabel != null && SavingsValue != null)
            lines.Add($"{SavingsLabel}: {SavingsValue}");
        if (Reasons.Count > 0)
            lines.Add(Reasons[0]);
        return string.Join(Environment.NewLine, lines);
    }

    public static string BuildEstimatedOutputTooltip(
        double? sourceMb,
        double? estimatedOutputMb,
        string? quality,
        string? encoder)
    {
        if (estimatedOutputMb is not > 0)
            return "Estimated output is not available yet.";

        var lines = new List<string> { $"Estimated output: {FormatSize(estimatedOutputMb.Value)}" };
        if (sourceMb is > 0)
        {
            double savingsPercent = (sourceMb.Value - estimatedOutputMb.Value) / sourceMb.Value * 100d;
            lines.Add(savingsPercent >= 0
                ? $"Estimated savings: {savingsPercent:0.#}%"
                : $"Estimated increase: {Math.Abs(savingsPercent):0.#}%");
        }
        if (!string.IsNullOrWhiteSpace(quality))
            lines.Add($"Quality: {quality.Trim()}");
        if (!string.IsNullOrWhiteSpace(encoder))
            lines.Add($"Encoder: {encoder.Trim()}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatSize(double megabytes) =>
        megabytes >= 1024 ? $"{megabytes / 1024d:0.##} GB" : $"{megabytes:0.#} MB";
}
