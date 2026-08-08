namespace MediaFlux.Services
{
    public static class DuplicateCleanupPolicy
    {
        public static bool IsActionableGroup(DuplicateGroup group)
        {
            ArgumentNullException.ThrowIfNull(group);

            return string.Equals(group.ConfidenceLabel, "Exact", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(group.ConfidenceLabel, "Strong visual match", StringComparison.OrdinalIgnoreCase);
        }

        public static bool CanCleanupItem(DuplicateGroup group, DuplicateItem item)
        {
            ArgumentNullException.ThrowIfNull(group);
            ArgumentNullException.ThrowIfNull(item);

            return IsActionableGroup(group) &&
                   !item.IsReferenceProtected &&
                   string.Equals(item.Recommendation, "Trash candidate", StringComparison.OrdinalIgnoreCase);
        }
    }
}
