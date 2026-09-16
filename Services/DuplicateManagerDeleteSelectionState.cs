namespace MediaFlux.Services;

/// <summary>
/// Owns Duplicate Manager review acknowledgement and explicit deletion choices independently of
/// transient grid rows and advisory keeper recommendations.
/// </summary>
public sealed class DuplicateManagerDeleteSelectionState
{
    private readonly Dictionary<string, bool> _explicitSelections = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _reviewedGroups = new();

    public bool IsReviewed(DuplicateGroup group) => _reviewedGroups.Contains(group.Id);

    public bool MarkReviewed(DuplicateGroup group) => _reviewedGroups.Add(group.Id);

    public bool CanSelectForDeletion(DuplicateGroup group, DuplicateItem item)
    {
        if (!IsManualDeletionCandidate(group, item))
            return false;

        return Resolve(group, item) || WouldRetainSurvivor(group, item.Path);
    }

    public bool Resolve(DuplicateGroup group, DuplicateItem item)
    {
        if (!IsManualDeletionCandidate(group, item))
            return false;

        if (_explicitSelections.TryGetValue(item.Path, out bool selected))
            return selected;

        return string.Equals(item.Recommendation, "Trash candidate", StringComparison.OrdinalIgnoreCase) &&
               group.Items.Any(other =>
                   !string.Equals(other.Path, item.Path, StringComparison.OrdinalIgnoreCase) &&
                   !IsSelectedByChoice(group, other));
    }

    public bool Record(DuplicateGroup group, DuplicateItem item, bool selected)
    {
        if (!IsManualDeletionCandidate(group, item))
        {
            _explicitSelections[item.Path] = false;
            return false;
        }

        if (selected && !WouldRetainSurvivor(group, item.Path))
            return Resolve(group, item);

        _explicitSelections[item.Path] = selected;
        return selected;
    }

    public void Clear()
    {
        _explicitSelections.Clear();
        _reviewedGroups.Clear();
    }

    private bool WouldRetainSurvivor(DuplicateGroup group, string pathToSelect)
    {
        return group.Items.Any(item =>
            !string.Equals(item.Path, pathToSelect, StringComparison.OrdinalIgnoreCase) &&
            !Resolve(group, item));
    }

    private bool IsSelectedByChoice(DuplicateGroup group, DuplicateItem item)
    {
        if (!IsManualDeletionCandidate(group, item))
            return false;

        return _explicitSelections.TryGetValue(item.Path, out bool selected)
            ? selected
            : string.Equals(item.Recommendation, "Trash candidate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsManualDeletionCandidate(DuplicateGroup group, DuplicateItem item)
    {
        return DuplicateCleanupPolicy.IsActionableGroup(group) &&
               !item.IsReferenceProtected &&
               !IsExplicitKeeper(item);
    }

    private static bool IsExplicitKeeper(DuplicateItem item) =>
        string.Equals(item.Recommendation, "Selected keeper", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(item.Recommendation, "Protected keeper", StringComparison.OrdinalIgnoreCase);
}
