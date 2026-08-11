using MediaFlux.Models;

namespace MediaFlux.Services.LibraryCatalog;

public sealed record ExactDuplicateKeeperChoice(ExactDuplicateMemberRecord Keeper, string Reason);

public static class ExactDuplicateKeeperPolicy
{
    public static ExactDuplicateKeeperChoice Select(
        IReadOnlyList<ExactDuplicateMemberRecord> members,
        DuplicateKeeperPreferences? preferences = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0) throw new ArgumentException("A duplicate group has no members.", nameof(members));
        DuplicateKeeperPreferences normalized = preferences?.Clone() ?? new DuplicateKeeperPreferences();
        normalized.Normalize();

        ExactDuplicateMemberRecord? manual = members.FirstOrDefault(member => member.IsManualKeeper);
        if (manual != null) return new ExactDuplicateKeeperChoice(manual, "User-selected keeper");

        var ranked = members
            .OrderByDescending(member => member.IsProtected)
            .ThenBy(member => PreferredLocationRank(member.FullPath, normalized.ExactPreferredLocations))
            .ThenBy(member => FileNamePenalty(member.FullPath))
            .ThenBy(member => FolderDepth(member.FullPath))
            .ThenBy(member => member.CreationUtc ?? DateTime.MaxValue)
            .ThenBy(member => member.LastWriteUtc)
            .ThenBy(member => member.FullPath.Length)
            .ThenBy(member => member.PathKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.FileId)
            .First();

        string reason = ranked.IsProtected ? "Protected copy"
            : PreferredLocationRank(ranked.FullPath, normalized.ExactPreferredLocations) < int.MaxValue
                ? "Preferred location, then clean/short path and oldest dates"
                : "Clean/short path, folder depth, oldest creation and modification dates";
        return new ExactDuplicateKeeperChoice(ranked, reason);
    }

    private static int PreferredLocationRank(string path, IReadOnlyList<string> roots)
    {
        for (int index = 0; index < roots.Count; index++)
        {
            string root = roots[index].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return int.MaxValue;
    }

    private static int FolderDepth(string path) => path.Count(character => character is '\\' or '/');

    private static int FileNamePenalty(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int penalty = name.Length;
        string lower = name.ToLowerInvariant();
        foreach (string marker in new[] { "copy", "duplicate", "backup", "old", "(1)", "_1", "-1" })
            if (lower.Contains(marker, StringComparison.Ordinal)) penalty += 100;
        return penalty;
    }
}

public static class ExactDuplicateSelectionPolicy
{
    public static IReadOnlyList<long> SelectAllExceptKeeper(
        IReadOnlyList<ExactDuplicateMemberRecord> members,
        long keeperFileId) => members.Where(member => member.FileId != keeperFileId).Select(member => member.FileId).ToArray();
}
