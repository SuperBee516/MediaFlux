using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>
/// Presentation-only provider and model-selection state for the AI restoration settings form.
/// It deliberately does not participate in provider selection or restoration execution.
/// </summary>
public sealed record AiConfigurationProviderSummary(
    string ActiveProvider,
    string? ResolvedProvider,
    string Status,
    string Version,
    string Models,
    string Scale)
{
    public string ToDisplayText() => string.Join(Environment.NewLine,
        new[]
        {
            $"Active Provider: {ActiveProvider}",
            ResolvedProvider is null ? null : $"Resolved Provider: {ResolvedProvider}",
            $"Status: {Status}",
            $"Models: {Models}",
            $"Scale: {Scale}",
            $"Version: {Version}"
        }.Where(line => line is not null));
}

/// <summary>Formats backend discovery for the UI and constrains model choices to valid combinations.</summary>
public static class AiConfigurationUiPresentation
{
    public static AiBackendMetadata ResolveProvider(AiBackendSelection selection, IReadOnlyList<AiBackendMetadata> backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        AiBackendMetadata? ncnn = backends.FirstOrDefault(backend => backend.Id.Equals("ncnn-vulkan", StringComparison.OrdinalIgnoreCase));
        AiBackendMetadata? tensorRt = backends.FirstOrDefault(backend => backend.Id.Equals("nvidia-tensorrt", StringComparison.OrdinalIgnoreCase));
        return selection switch
        {
            AiBackendSelection.NcnnVulkan => ncnn ?? Unavailable("NCNN Vulkan"),
            AiBackendSelection.NvidiaTensorRt => tensorRt ?? Unavailable("NVIDIA TensorRT"),
            AiBackendSelection.DirectMl => Unavailable("DirectML") with { Reason = "DirectML inference is not implemented in this MediaFlux phase." },
            AiBackendSelection.Cpu => Unavailable("CPU") with { Reason = "CPU inference is not implemented in this MediaFlux phase." },
            _ => tensorRt?.IsReady == true ? tensorRt : ncnn ?? Unavailable("NCNN Vulkan")
        };
    }

    public static IReadOnlyList<AiRestorationScale> CompatibleScales(AiRestorationMode mode, IReadOnlyList<AiRestorationModel> models) =>
        models.Where(model => model.Category == mode)
            .SelectMany(model => model.SupportedScales)
            .Distinct()
            .OrderBy(scale => (int)scale)
            .ToArray();

    public static AiRestorationScale? SelectNearestScale(AiRestorationScale current, IReadOnlyList<AiRestorationScale> compatibleScales) =>
        compatibleScales.Count == 0
            ? null
            : compatibleScales.OrderBy(scale => Math.Abs((int)scale - (int)current)).ThenBy(scale => (int)scale).First();

    public static IReadOnlyList<AiRestorationModel> CompatibleModels(AiRestorationMode mode, AiRestorationScale scale, IReadOnlyList<AiRestorationModel> models) =>
        models.Where(model => model.Category == mode && model.SupportedScales.Contains(scale)).ToArray();

    public static AiConfigurationProviderSummary BuildSummary(AiBackendSelection selection, AiBackendMetadata provider, int compatibleModelCount, AiRestorationScale? scale)
    {
        bool ready = provider.IsReady;
        string models = !ready
            ? "Unavailable"
            : compatibleModelCount == 1 ? "1 compatible model detected" : $"{compatibleModelCount} compatible models detected";
        return new(
            provider.DisplayName,
            selection == AiBackendSelection.Auto ? provider.DisplayName : null,
            DescribeStatus(provider, compatibleModelCount, scale),
            NormalizeVersion(provider.Version),
            models,
            scale is null ? "Unavailable" : $"{(int)scale}×");
    }

    private static string DescribeStatus(AiBackendMetadata provider, int compatibleModelCount, AiRestorationScale? scale)
    {
        if (provider.IsReady)
            return compatibleModelCount == 0 && scale is null ? "✖ Compatible model not found" : "✓ Ready";
        string reason = provider.Reason ?? "Provider unavailable.";
        if (reason.Contains("CUDA runtime", StringComparison.OrdinalIgnoreCase)) return "✖ CUDA Runtime Missing";
        if (reason.Contains("TensorRT runtime", StringComparison.OrdinalIgnoreCase)) return "✖ TensorRT Runtime Missing";
        if (reason.Contains("model", StringComparison.OrdinalIgnoreCase)) return "✖ Compatible model not found";
        return "✖ " + reason.Trim().TrimEnd('.');
    }

    private static string NormalizeVersion(string? version) => string.IsNullOrWhiteSpace(version) || version.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? "Unavailable" : version;
    private static AiBackendMetadata Unavailable(string displayName) => new("", displayName, "Unavailable", false, false, "Provider unavailable.", false, false, false, false, false, Array.Empty<string>());
}
