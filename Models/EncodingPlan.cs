namespace MediaFlux.Models;

public sealed record EncodingPlanItem(string Label, string Value, string? Reason = null);

public sealed record EncodingPlanSection(
    string Title,
    IReadOnlyList<EncodingPlanItem> Items);

public sealed class EncodingPlan
{
    public bool IsAvailable { get; init; }
    public string UnavailableReason { get; init; } = "";
    public IReadOnlyList<EncodingPlanSection> Sections { get; init; } =
        Array.Empty<EncodingPlanSection>();

    public static EncodingPlan Unavailable(string reason) => new()
    {
        IsAvailable = false,
        UnavailableReason = reason
    };
}
