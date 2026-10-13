namespace MediaFlux.Services;

internal static class EncodingOptionsLayoutState
{
    internal static bool ShouldApply(bool? appliedStacked, bool desiredStacked) =>
        appliedStacked is null || appliedStacked.Value != desiredStacked;
}
