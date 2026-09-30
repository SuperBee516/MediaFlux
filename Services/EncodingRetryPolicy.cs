using MediaFlux.Models;

namespace MediaFlux.Services;

internal static class EncodingRetryPolicy
{
    public static bool AllowsAutomaticRetry(EncodingTerminalResult? terminalResult) =>
        terminalResult is not (EncodingTerminalResult.SourceUnrecoverable or EncodingTerminalResult.StoragePolicyRejected);

    public static bool AllowsAutomaticRetry(
        EncodingTerminalResult? terminalResult,
        bool hasResearchExperimentAssignment) =>
        !hasResearchExperimentAssignment && AllowsAutomaticRetry(terminalResult);
}
