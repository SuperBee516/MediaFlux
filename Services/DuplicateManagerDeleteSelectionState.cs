namespace MediaFlux.Services;

/// <summary>
/// Keeps explicit Duplicate Manager checkbox edits separate from the transient grid rows.
/// Rule-generated trash-candidate selections remain the default when no edit exists.
/// </summary>
public sealed class DuplicateManagerDeleteSelectionState
{
    private readonly Dictionary<string, bool> _explicitSelections = new(StringComparer.OrdinalIgnoreCase);

    public bool Resolve(DuplicateGroup group, DuplicateItem item)
    {
        if (!DuplicateCleanupPolicy.CanCleanupItem(group, item))
            return false;

        return _explicitSelections.TryGetValue(item.Path, out bool selected)
            ? selected
            : true;
    }

    public bool Record(DuplicateGroup group, DuplicateItem item, bool selected)
    {
        bool effectiveSelection = DuplicateCleanupPolicy.CanCleanupItem(group, item) && selected;
        _explicitSelections[item.Path] = effectiveSelection;
        return effectiveSelection;
    }

    public void Clear() => _explicitSelections.Clear();
}
