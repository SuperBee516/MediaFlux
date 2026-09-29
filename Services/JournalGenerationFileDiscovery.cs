namespace MediaFlux.Services;

/// <summary>Finds an active JSONL journal and numeric .oldN generations beside it.</summary>
internal static class JournalGenerationFileDiscovery
{
    public static IReadOnlyList<string> Discover(string activePath)
    {
        string fullPath = Path.GetFullPath(activePath);
        string directory = Path.GetDirectoryName(fullPath)!;
        string fileName = Path.GetFileName(fullPath);
        string stem = Path.GetFileNameWithoutExtension(fullPath);
        var candidates = new List<(string Path, int Generation)>();
        if (File.Exists(fullPath))
            candidates.Add((fullPath, 0));
        if (!Directory.Exists(directory))
            return candidates.Select(item => item.Path).ToArray();

        string[] prefixes = [fileName + ".old", stem + ".old"];
        foreach (string candidate in Directory.EnumerateFiles(directory))
        {
            string candidateName = Path.GetFileName(candidate);
            foreach (string prefix in prefixes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!candidateName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                string suffix = candidateName[prefix.Length..];
                if (suffix.Length > 0 && suffix.All(char.IsAsciiDigit) &&
                    int.TryParse(suffix, out int generation))
                    candidates.Add((Path.GetFullPath(candidate), generation));
                break;
            }
        }

        return candidates
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Generation)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Path)
            .ToArray();
    }
}
