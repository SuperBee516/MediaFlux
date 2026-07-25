using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services
{
    public static class OutputPathService
    {
        private static readonly HashSet<string> ReservedWindowsNames = new(
            new[]
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            },
            StringComparer.OrdinalIgnoreCase);

        public static string SanitizeFileName(string? value, string fallback = "DVD Title")
        {
            string source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            var invalid = Path.GetInvalidFileNameChars().ToHashSet();
            var builder = new StringBuilder(source.Length);
            foreach (char character in source)
                builder.Append(invalid.Contains(character) || char.IsControl(character) ? '_' : character);

            string sanitized = CollapseRepeatedWhitespace(builder.ToString())
                .Trim()
                .TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(sanitized))
                sanitized = fallback;

            string baseName = Path.GetFileNameWithoutExtension(sanitized);
            if (ReservedWindowsNames.Contains(baseName))
                sanitized = "_" + sanitized;

            return sanitized;
        }

        public static string BuildDefaultDvdBaseName(
            DvdFolderAnalysisResult analysis,
            DvdTitleCandidate candidate,
            string? namingPattern = null)
        {
            ArgumentNullException.ThrowIfNull(analysis);
            ArgumentNullException.ThrowIfNull(candidate);

            string selected = Path.TrimEndingDirectorySeparator(analysis.SelectedFolderPath);
            string folderName;
            if (Path.GetFileName(selected).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
            {
                string? parent = Path.GetDirectoryName(selected);
                folderName = string.IsNullOrWhiteSpace(parent)
                    ? "DVD Title"
                    : Path.GetFileName(Path.TrimEndingDirectorySeparator(parent));
            }
            else
            {
                folderName = Path.GetFileName(selected);
            }

            string cleanName = SanitizeFileName(folderName);
            bool oneStrongCandidate =
                analysis.Candidates.Count == 1 &&
                ReferenceEquals(analysis.RecommendedCandidate, candidate);
            string titleSetSuffix = oneStrongCandidate
                ? ""
                : $" - {candidate.TitleSetId}";
            string pattern = string.IsNullOrWhiteSpace(namingPattern)
                ? "{MovieName}{TitleSetSuffix}"
                : namingPattern;
            string expanded = pattern
                .Replace("{MovieName}", cleanName, StringComparison.OrdinalIgnoreCase)
                .Replace("{TitleSet}", candidate.TitleSetId, StringComparison.OrdinalIgnoreCase)
                .Replace("{TitleSetSuffix}", titleSetSuffix, StringComparison.OrdinalIgnoreCase);
            return SanitizeFileName(expanded, cleanName + titleSetSuffix);
        }

        public static string BuildDefaultDvdOutputPath(
            DvdFolderAnalysisResult analysis,
            DvdTitleCandidate candidate,
            string outputFolder,
            string? namingPattern = null)
        {
            string folder = string.IsNullOrWhiteSpace(outputFolder)
                ? GetDefaultOutputFolder(analysis)
                : Path.GetFullPath(outputFolder);
            string baseName = BuildDefaultDvdBaseName(
                analysis,
                candidate,
                namingPattern);
            return GetCollisionSafePath(Path.Combine(folder, baseName + ".mkv"));
        }

        public static string EnsureMkvExtension(string path)
        {
            return EnsureExtension(path, ".mkv");
        }

        public static string EnsureMp4Extension(string path)
        {
            return EnsureExtension(path, ".mp4");
        }

        private static string EnsureExtension(string path, string extension)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";

            return Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.ChangeExtension(path, extension));
        }

        public static string GetCollisionSafePath(string requestedPath)
        {
            string fullPath = Path.GetFullPath(requestedPath);
            if (!File.Exists(fullPath))
                return fullPath;

            string? folder = Path.GetDirectoryName(fullPath);
            string baseName = Path.GetFileNameWithoutExtension(fullPath);
            string extension = Path.GetExtension(fullPath);
            for (int counter = 1; ; counter++)
            {
                string candidate = Path.Combine(
                    folder ?? Environment.CurrentDirectory,
                    $"{baseName} ({counter}){extension}");
                if (!File.Exists(candidate))
                    return candidate;
            }
        }

        public static string CreateStagingPath(string finalOutputPath)
        {
            string fullPath = Path.GetFullPath(finalOutputPath);
            string folder = Path.GetDirectoryName(fullPath) ??
                            throw new InvalidOperationException("The output folder could not be determined.");
            string baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(fullPath));
            return Path.Combine(
                folder,
                $".{baseName}.mediaflux-{Guid.NewGuid():N}.partial.mkv");
        }

        public static bool IsPathWithinDirectory(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
                return false;

            string fullPath = Path.GetFullPath(path);
            string fullDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(directory));
            return fullPath.StartsWith(
                fullDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string GetDefaultOutputFolder(DvdFolderAnalysisResult analysis)
        {
            string selected = Path.TrimEndingDirectorySeparator(analysis.SelectedFolderPath);
            if (Path.GetFileName(selected).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(selected) ?? selected;
            return selected;
        }

        private static string CollapseRepeatedWhitespace(string value)
        {
            var builder = new StringBuilder(value.Length);
            bool previousWhitespace = false;
            foreach (char character in value)
            {
                bool isWhitespace = char.IsWhiteSpace(character);
                if (isWhitespace && previousWhitespace)
                    continue;

                builder.Append(isWhitespace ? ' ' : character);
                previousWhitespace = isWhitespace;
            }

            return builder.ToString();
        }
    }
}
