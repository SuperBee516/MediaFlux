using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiRestorationPhase3Tests
{
    [Fact]
    public void MissingNcnnWithAutoAndNoAlternativePromptsSetup()
    {
        AiContextualGuardDecision decision = AiRestorationContextualGuard.Evaluate(AiRestorationMode.Off, AiRestorationMode.Animation, AiBackendSelection.Auto, new[] { Backend("ncnn-vulkan", false), Backend("nvidia-tensorrt", false) }, ManagedAiInstallationState.NotInstalled);
        Assert.Equal(AiContextualGuardOutcome.PromptSetup, decision.Outcome); Assert.Contains("required", decision.Message);
    }

    [Fact]
    public void ExplicitTensorRtDoesNotPromptForRealEsrgan()
    {
        AiContextualGuardDecision decision = AiRestorationContextualGuard.Evaluate(AiRestorationMode.Off, AiRestorationMode.General, AiBackendSelection.NvidiaTensorRt, new[] { Backend("ncnn-vulkan", false), Backend("nvidia-tensorrt", false) }, ManagedAiInstallationState.NotInstalled);
        Assert.Equal(AiContextualGuardOutcome.Allow, decision.Outcome);
    }

    [Fact]
    public void InstalledButVulkanUnavailableExplainsRuntimeFailure()
    {
        AiContextualGuardDecision decision = AiRestorationContextualGuard.Evaluate(AiRestorationMode.Off, AiRestorationMode.Animation, AiBackendSelection.NcnnVulkan, new[] { Backend("ncnn-vulkan", false, "Vulkan unavailable") }, ManagedAiInstallationState.Installed);
        Assert.Equal(AiContextualGuardOutcome.ExplainRuntimeFailure, decision.Outcome); Assert.Contains("Vulkan", decision.Message);
    }

    [Fact]
    public async Task ExecutionGuardBlocksBeforeUnavailableBackendRuns()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFluxPhase3", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var settings = new VideoRestorationSettings { AiMode = AiRestorationMode.Animation, AiBackendPath = Path.Combine(root, "missing.exe") };
            var backend = new AiRestorationBackendService(root);
            AiRestorationValidationException exception = await Assert.ThrowsAsync<AiRestorationValidationException>(() => AiRestorationExecutionGuard.CreateSessionAsync(backend, settings));
            Assert.Contains("could not start", exception.Message);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static AiBackendMetadata Backend(string id, bool ready, string? reason = null) => new(id, id, "Unavailable", false, ready, reason, false, false, false, false, false, Array.Empty<string>());
}
