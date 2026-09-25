using MediaFlux.Models;

namespace MediaFlux.Services;

internal static class EncodingRetryPolicy
{
    public static bool AllowsAutomaticRetry(EncodingTerminalResult? terminalResult) =>
        terminalResult != EncodingTerminalResult.SourceUnrecoverable;
}
