using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiConfigurationUiPresentationTests
{
    [Fact]
    public void AutoResolvesToReadyNcnnAndShowsItSeparately()
    {
        AiBackendMetadata ncnn = Backend("ncnn-vulkan", "NCNN Vulkan", "Unknown", ready: true);
        AiBackendMetadata tensorRt = Backend("nvidia-tensorrt", "NVIDIA TensorRT", "Unavailable", ready: false, reason: "CUDA runtime libraries are missing.");

        AiBackendMetadata resolved = AiConfigurationUiPresentation.ResolveProvider(AiBackendSelection.Auto, new[] { ncnn, tensorRt });
        AiConfigurationProviderSummary summary = AiConfigurationUiPresentation.BuildSummary(AiBackendSelection.Auto, resolved, 1, AiRestorationScale.X2);

        Assert.Equal("NCNN Vulkan", resolved.DisplayName);
        Assert.Equal("NCNN Vulkan", summary.ActiveProvider);
        Assert.Equal("NCNN Vulkan", summary.ResolvedProvider);
        Assert.Equal("✓ Ready", summary.Status);
        Assert.Equal("Unavailable", summary.Version);
        Assert.Contains("Resolved Provider: NCNN Vulkan", summary.ToDisplayText());
    }

    [Fact]
    public void VersionUnavailableDoesNotMakeReadyProviderUnhealthy()
    {
        AiConfigurationProviderSummary summary = AiConfigurationUiPresentation.BuildSummary(AiBackendSelection.NcnnVulkan, Backend("ncnn-vulkan", "NCNN Vulkan", "Unknown", ready: true), 1, AiRestorationScale.X4);

        Assert.Equal("✓ Ready", summary.Status);
        Assert.Equal("Unavailable", summary.Version);
    }

    [Fact]
    public void ReadyProviderReportsMissingCompatibleModelWithoutChangingItsVersion()
    {
        AiConfigurationProviderSummary summary = AiConfigurationUiPresentation.BuildSummary(AiBackendSelection.NcnnVulkan, Backend("ncnn-vulkan", "NCNN Vulkan", "1.0", ready: true), 0, null);

        Assert.Equal("✖ Compatible model not found", summary.Status);
        Assert.Equal("1.0", summary.Version);
    }

    [Theory]
    [InlineData("CUDA runtime libraries are missing.", "✖ CUDA Runtime Missing")]
    [InlineData("Required TensorRT runtime libraries are missing.", "✖ TensorRT Runtime Missing")]
    [InlineData("No complete supported AI models were found.", "✖ Compatible model not found")]
    public void UnavailableProviderStatusIsSpecific(string reason, string expected)
    {
        AiConfigurationProviderSummary summary = AiConfigurationUiPresentation.BuildSummary(AiBackendSelection.NvidiaTensorRt, Backend("nvidia-tensorrt", "NVIDIA TensorRT", "Unavailable", ready: false, reason: reason), 0, null);

        Assert.Equal(expected, summary.Status);
        Assert.Equal("Unavailable", summary.Models);
    }

    [Fact]
    public void CompatibleScalesAndModelsExcludeImpossibleSelectionsAndUseNearestScale()
    {
        AiRestorationModel x2 = Model("anime-x2", AiRestorationMode.Animation, AiRestorationScale.X2);
        AiRestorationModel x3 = Model("anime-x3", AiRestorationMode.Animation, AiRestorationScale.X3);
        AiRestorationModel x4 = Model("general-x4", AiRestorationMode.General, AiRestorationScale.X4);

        IReadOnlyList<AiRestorationScale> animationScales = AiConfigurationUiPresentation.CompatibleScales(AiRestorationMode.Animation, new[] { x2, x3, x4 });
        AiRestorationScale? nearest = AiConfigurationUiPresentation.SelectNearestScale(AiRestorationScale.X4, animationScales);
        IReadOnlyList<AiRestorationModel> models = AiConfigurationUiPresentation.CompatibleModels(AiRestorationMode.Animation, nearest!.Value, new[] { x2, x3, x4 });

        Assert.Equal(new[] { AiRestorationScale.X2, AiRestorationScale.X3 }, animationScales);
        Assert.Equal(AiRestorationScale.X3, nearest);
        Assert.Single(models);
        Assert.Equal("anime-x3", models[0].Id);
    }

    [Fact]
    public void AutoPrefersReadyTensorRtWhenItBecomesAvailable()
    {
        AiBackendMetadata resolved = AiConfigurationUiPresentation.ResolveProvider(AiBackendSelection.Auto, new[]
        {
            Backend("ncnn-vulkan", "NCNN Vulkan", "1", ready: true),
            Backend("nvidia-tensorrt", "NVIDIA TensorRT", "10", ready: true)
        });

        Assert.Equal("NVIDIA TensorRT", resolved.DisplayName);
    }

    private static AiBackendMetadata Backend(string id, string name, string version, bool ready, string? reason = null) => new(id, name, version, ready, ready, reason, true, true, true, true, true, Array.Empty<string>());
    private static AiRestorationModel Model(string id, AiRestorationMode mode, AiRestorationScale scale) => new(id, id, mode, new[] { scale }, "models", "model.param", "model.bin", "ncnn-vulkan");
}
