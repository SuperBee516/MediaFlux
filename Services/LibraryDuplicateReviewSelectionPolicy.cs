namespace MediaFlux.Services;

/// <summary>
/// Resolves where duplicate review should continue after a keeper decision refreshes the visible results.
/// </summary>
public static class LibraryDuplicateReviewSelectionPolicy
{
    /// <summary>
    /// Advances from the prior group when it remains visible. When a filter or reconciliation removes it,
    /// the row that took its former position is selected instead. The last visible row never wraps.
    /// </summary>
    public static int ResolveNextVisibleIndex(IReadOnlyList<long> visibleGroupIds, long priorGroupId, int priorIndex)
    {
        if (visibleGroupIds.Count == 0)
            return -1;

        int currentIndex = -1;
        for (int index = 0; index < visibleGroupIds.Count; index++)
        {
            if (visibleGroupIds[index] == priorGroupId)
            {
                currentIndex = index;
                break;
            }
        }

        return currentIndex >= 0
            ? Math.Min(currentIndex + 1, visibleGroupIds.Count - 1)
            : Math.Clamp(priorIndex, 0, visibleGroupIds.Count - 1);
    }
}
