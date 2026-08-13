using MediaFlux.Models;

namespace MediaFlux.Services
{
    public sealed class SourceDeletionResult
    {
        public bool Deleted { get; init; }
        public string Message { get; init; } = "";
    }

    public static class SourceDeletionService
    {
        public static SourceDeletionResult DeleteAfterFinalization(
            string sourcePath,
            EncodingInputSource input,
            bool deleteRequested,
            EncodingService.EncodeResult result)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(result);
            if (!deleteRequested)
                return Retained("Source retained because deletion was not requested.");
            if (!input.AllowSourceDeletion)
                return Retained("Source retained because this input type disables source deletion.");
            if (!result.Success || !result.FinalizationSucceeded)
            {
                return Retained(
                    "Source retained because output validation and finalization did not complete.");
            }
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return Retained("Source deletion was requested, but the source no longer exists.");
            if (string.IsNullOrWhiteSpace(result.OutputPath) ||
                !File.Exists(result.OutputPath))
            {
                return Retained(
                    "Source retained because the verified final output no longer exists.");
            }

            try
            {
                long finalLength = new FileInfo(result.OutputPath).Length;
                if (finalLength <= 0 ||
                    result.FinalOutputSizeBytes is > 0 &&
                    finalLength != result.FinalOutputSizeBytes.Value)
                {
                    return Retained(
                        "Source retained because the final output changed after verification.");
                }

                File.Delete(sourcePath);
                return File.Exists(sourcePath)
                    ? Retained("Source deletion was requested, but the source still exists.")
                    : new SourceDeletionResult
                    {
                        Deleted = true,
                        Message =
                            "Source deleted after validated output promotion and final verification."
                    };
            }
            catch (Exception ex)
            {
                return Retained($"Source retained because deletion failed: {ex.Message}");
            }
        }

        private static SourceDeletionResult Retained(string message) => new()
        {
            Deleted = false,
            Message = message
        };
    }
}
