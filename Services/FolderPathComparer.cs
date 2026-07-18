using System;
using System.IO;

namespace MediaFlux.Services
{
    internal static class FolderPathComparer
    {
        public static bool OutputConflictsWithWatchFolder(
            string? outputFolder,
            string? watchFolder,
            bool watchIncludesSubfolders)
        {
            string? output = Normalize(outputFolder);
            string? watch = Normalize(watchFolder);
            if (output == null || watch == null)
                return false;

            if (string.Equals(output, watch, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!watchIncludesSubfolders)
                return false;

            string watchPrefix = watch.EndsWith(Path.DirectorySeparatorChar)
                ? watch
                : watch + Path.DirectorySeparatorChar;
            return output.StartsWith(watchPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string? Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
            }
            catch
            {
                return null;
            }
        }
    }
}
