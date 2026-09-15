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

    private static string FormatSize(double megabytes) =>
        megabytes >= 1024 ? $"{megabytes / 1024d:0.##} GB" : $"{megabytes:0.#} MB";
}
