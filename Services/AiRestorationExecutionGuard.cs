using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Final pre-processing boundary shared by preview and encode orchestration.</summary>
public static class AiRestorationExecutionGuard
{
    public static async Task<AiRestorationSession> CreateSessionAsync(IAiRestorationBackend backend, VideoRestorationSettings settings, CancellationToken cancellationToken = default, Action<string>? log = null)
    {
        try
        {
            return await backend.CreateSessionAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (AiRestorationValidationException ex)
        {
            log?.Invoke($"[AI Execution Guard] blocked processing before backend launch: {ex.Message}");
            throw new AiRestorationValidationException($"AI restoration could not start because the selected backend is unavailable. {ex.Message}");
        }
    }
}
