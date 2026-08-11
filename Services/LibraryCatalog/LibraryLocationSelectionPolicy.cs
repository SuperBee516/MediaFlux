namespace MediaFlux.Services.LibraryCatalog;

public static class LibraryLocationSelectionPolicy
{
    public static IReadOnlyList<long> Resolve(
        IReadOnlyCollection<long> previousSelection,
        IReadOnlyCollection<long> availableLocationIds,
        long? preferredLocationId = null)
    {
        ArgumentNullException.ThrowIfNull(previousSelection);
        ArgumentNullException.ThrowIfNull(availableLocationIds);
        var available = availableLocationIds.ToHashSet();
        if (preferredLocationId.HasValue && available.Contains(preferredLocationId.Value)) return new[] { preferredLocationId.Value };
        long[] retained = previousSelection.Where(available.Contains).Distinct().ToArray();
        if (retained.Length > 0) return retained;
        return availableLocationIds.Take(1).ToArray();
    }
}
