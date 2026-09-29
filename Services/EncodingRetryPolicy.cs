using MediaFlux.Models;

namespace MediaFlux.Services;

internal static class EncodingRetryPolicy
{
    public static bool AllowsAutomaticRetry(EncodingTerminalResult? terminalResult) =>
        terminalResult != EncodingTerminalResult.SourceUnrecoverable;

    public static bool AllowsAutomaticRetry(
        EncodingTerminalResult? terminalResult,
        bool hasResearchExperimentAssignment) =>
        !hasResearchExperimentAssignment && AllowsAutomaticRetry(terminalResult);
}
