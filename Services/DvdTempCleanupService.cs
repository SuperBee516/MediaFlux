namespace MediaFlux.Services
{
    public sealed class DvdTempCleanupResult
    {
        public int RemovedDirectoryCount { get; internal set; }
        public IReadOnlyList<string> Errors { get; internal set; } =
            Array.Empty<string>();
    }

    public static class DvdTempCleanupService
    {
        public static DvdTempCleanupResult CleanupStaleOperations(
            string tempRoot,
            TimeSpan minimumAge,
            DateTime? utcNow = null)
        {
            var result = new DvdTempCleanupResult();
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(tempRoot) ||
                minimumAge < TimeSpan.Zero ||
                !Directory.Exists(tempRoot))
            {
                return result;
            }

            string fullRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(tempRoot));
            string rootPrefix = fullRoot + Path.DirectorySeparatorChar;
            DateTime cutoff = (utcNow ?? DateTime.UtcNow) - minimumAge;

            foreach (string candidate in Directory.EnumerateDirectories(
                         fullRoot,
                         "dvd-*",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    string fullCandidate = Path.GetFullPath(candidate);
                    if (!fullCandidate.StartsWith(
                            rootPrefix,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var info = new DirectoryInfo(fullCandidate);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        info.LastWriteTimeUtc > cutoff)
                    {
                        continue;
                    }

                    Directory.Delete(fullCandidate, recursive: true);
                    result.RemovedDirectoryCount++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{candidate}: {ex.Message}");
                }
            }

            result.Errors = errors;
            return result;
        }
    }
}
