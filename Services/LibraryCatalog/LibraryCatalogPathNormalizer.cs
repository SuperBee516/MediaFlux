namespace MediaFlux.Services.LibraryCatalog
{
    internal static class LibraryCatalogPathNormalizer
    {
        public static (string Path, string Key) NormalizeFullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A catalog path is required.", nameof(path));

            string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
            string normalized = Path.GetFullPath(expanded)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string root = Path.GetPathRoot(normalized) ?? string.Empty;
            if (!string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
                normalized = Path.TrimEndingDirectorySeparator(normalized);

            return (normalized, normalized.ToUpperInvariant());
        }

        public static (string Path, string Key) NormalizeRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("A relative catalog path is required.", nameof(relativePath));

            string normalized = relativePath.Trim()
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized))
                throw new ArgumentException("The catalog membership path must be relative.", nameof(relativePath));

            string[] segments = normalized.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
                throw new ArgumentException("The catalog membership path must stay within its library location.", nameof(relativePath));

            normalized = string.Join(Path.DirectorySeparatorChar, segments);
            return (normalized, normalized.ToUpperInvariant());
        }
    }
}
