namespace MediaFlux.Services
{
    public static class IncompleteEncodeOutputCleanupService
    {
        public static async Task<string> CleanupAsync(
            string sourcePath,
            string outputPath,
            bool cleanupEnabled,
            string outcome)
        {
            if (!cleanupEnabled)
                return "disabled in Settings.";

            if (string.IsNullOrWhiteSpace(outputPath))
                return "no output path was allocated.";

            string fullSourcePath;
            string fullOutputPath;
            try
            {
                fullSourcePath = Path.GetFullPath(sourcePath);
                fullOutputPath = Path.GetFullPath(outputPath);
            }
            catch (Exception ex)
            {
                return
                    $"not deleted because the attempt path was invalid ({ex.Message}).";
            }

            if (string.Equals(
                    fullSourcePath,
                    fullOutputPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "not deleted because the output path matched the source path.";
            }

            const int attempts = 3;
            Exception? lastError = null;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    if (!File.Exists(fullOutputPath))
                        return "no incomplete output file was present.";

                    File.Delete(fullOutputPath);
                    if (!File.Exists(fullOutputPath))
                        return $"deleted the {outcome} attempt output.";

                    lastError = new IOException(
                        "The file still exists after the delete request.");
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                if (attempt < attempts)
                    await Task.Delay(250 * attempt).ConfigureAwait(false);
            }

            return
                $"could not delete the {outcome} attempt output after " +
                $"{attempts} attempts ({lastError?.Message ?? "unknown error"}).";
        }
    }
}
