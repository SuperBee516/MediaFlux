using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class RealEsrganSetupPresentationTests
{
    [Fact]
    public void ManagedReadyInstallationReportsRuntimeModelAndDeviceDetails()
    {
        RealEsrganSetupStatus status = RealEsrganSetupPresentation.Describe(Installation(ManagedAiInstallationState.Installed), Capabilities(available: true, vulkan: true, models: 5), "0.2.5.0");
        Assert.Equal("Ready", status.Status); Assert.Equal("5 detected", status.Models); Assert.Equal("Available", status.Vulkan); Assert.Equal("GPU 0", status.Device); Assert.False(status.CanInstall);
    }

    [Fact]
    public void IncompleteManagedInstallationIsActionable()
    {
        RealEsrganSetupStatus status = RealEsrganSetupPresentation.Describe(Installation(ManagedAiInstallationState.Incomplete), Capabilities(false, false, 0), null);
        Assert.Equal("Installation incomplete", status.Status); Assert.True(status.CanInstall);
    }

    [Fact]
    public void ManualReadyInstallationDoesNotRequireManagedPaths()
    {
        RealEsrganSetupStatus status = RealEsrganSetupPresentation.Describe(Installation(ManagedAiInstallationState.NotInstalled), Capabilities(true, true, 1), "1.0");
        Assert.Equal("Ready", status.Status); Assert.Contains("manually configured", status.Detail); Assert.True(status.CanInstall);
    }

    [Fact]
    public void ManagedRuntimeProblemsRemainDistinctFromStructuralInstallation()
    {
        RealEsrganSetupStatus models = RealEsrganSetupPresentation.Describe(Installation(ManagedAiInstallationState.Installed), Capabilities(true, true, 0), "1.0");
        RealEsrganSetupStatus vulkan = RealEsrganSetupPresentation.Describe(Installation(ManagedAiInstallationState.Installed), Capabilities(true, false, 1), "1.0");
        Assert.Equal("Models missing/incomplete", models.Status); Assert.Equal("Vulkan unavailable", vulkan.Status);
    }

    private static ManagedAiInstallationInspection Installation(ManagedAiInstallationState state) => new(state, "managed", "managed\\tool.exe", "managed\\models", "0.2.5.0", "package.zip", state == ManagedAiInstallationState.Installed, state == ManagedAiInstallationState.Installed, "detail");
    private static AiRestorationCapabilities Capabilities(bool available, bool vulkan, int models) => new(available, "ncnn-vulkan", "tool.exe", "identity", vulkan, new[] { "Auto", "GPU 0" }, Enumerable.Range(0, models).Select(index => new AiRestorationModel($"model-{index}", "Model", AiRestorationMode.Animation, new[] { AiRestorationScale.X2 }, "models", "a.param", "a.bin", "ncnn-vulkan")).ToArray(), available ? null : "unavailable");
}
