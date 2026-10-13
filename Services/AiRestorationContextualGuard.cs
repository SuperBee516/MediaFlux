using MediaFlux.Models;

namespace MediaFlux.Services;

public enum AiSetupRecoveryAction { Install, Locate, Cancel }
public enum AiContextualGuardOutcome { Allow, PromptSetup, ExplainRuntimeFailure }
public sealed record AiContextualGuardDecision(AiContextualGuardOutcome Outcome, string Message, string? TechnicalReason = null);

/// <summary>Small decision boundary for user-triggered AI enablement; provider semantics stay in AiBackendManager.</summary>
public static class AiRestorationContextualGuard
{
    public static AiContextualGuardDecision Evaluate(
        AiRestorationMode previousMode,
        AiRestorationMode requestedMode,
        AiBackendSelection selection,
        IReadOnlyList<AiBackendMetadata> metadata,
        ManagedAiInstallationState managedState)
    {
        if (previousMode != AiRestorationMode.Off || requestedMode == AiRestorationMode.Off)
            return new(AiContextualGuardOutcome.Allow, "AI restoration selection unchanged.");

        AiBackendMetadata ncnn = metadata.FirstOrDefault(item => item.Id.Equals("ncnn-vulkan", StringComparison.OrdinalIgnoreCase))
            ?? new("ncnn-vulkan", "NCNN Vulkan", "Unavailable", false, false, "Real-ESRGAN is unavailable.", false, false, false, false, false, Array.Empty<string>());
        AiBackendMetadata tensorRt = metadata.FirstOrDefault(item => item.Id.Equals("nvidia-tensorrt", StringComparison.OrdinalIgnoreCase))
            ?? new("nvidia-tensorrt", "NVIDIA TensorRT", "Unavailable", false, false, "TensorRT is unavailable.", false, false, false, false, false, Array.Empty<string>());

        if (selection == AiBackendSelection.NvidiaTensorRt || selection is AiBackendSelection.DirectMl or AiBackendSelection.Cpu)
            return new(AiContextualGuardOutcome.Allow, "The explicitly selected provider is not Real-ESRGAN.");
        bool ncnnRequired = selection == AiBackendSelection.NcnnVulkan || (selection == AiBackendSelection.Auto && !tensorRt.IsReady);
        if (!ncnnRequired || ncnn.IsReady)
            return new(AiContextualGuardOutcome.Allow, "A usable selected provider is available.");
        if (managedState == ManagedAiInstallationState.Installed)
            return new(AiContextualGuardOutcome.ExplainRuntimeFailure, RuntimeMessage(ncnn.Reason), ncnn.Reason);
        return new(AiContextualGuardOutcome.PromptSetup, "Real-ESRGAN is required for AI Restoration but is not currently installed or configured.", ncnn.Reason);
    }

    private static string RuntimeMessage(string? reason)
    {
        if (reason?.Contains("vulkan", StringComparison.OrdinalIgnoreCase) == true) return "Real-ESRGAN is installed, but Vulkan is unavailable on this system.";
        if (reason?.Contains("model", StringComparison.OrdinalIgnoreCase) == true) return "Real-ESRGAN was found, but no compatible AI models are available.";
        return "Real-ESRGAN was found but could not be started successfully.";
    }
}
