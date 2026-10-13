namespace MediaFlux.Services;

/// <summary>Presentation-only mapping that combines structural installation and runtime validation.</summary>
public sealed record RealEsrganSetupStatus(string Status, string Detail, string Version, string Models, string Vulkan, string Device, bool CanInstall);

public static class RealEsrganSetupPresentation
{
    public static RealEsrganSetupStatus Describe(ManagedAiInstallationInspection installation, AiRestorationCapabilities capabilities, string? runtimeVersion)
    {
        string version = installation.State == ManagedAiInstallationState.NotInstalled ? "Unavailable" : "v" + installation.Version;
        bool manualRuntime = installation.State != ManagedAiInstallationState.Installed && capabilities.IsAvailable;
        if (manualRuntime && capabilities.Models.Count > 0 && capabilities.VulkanAvailable)
        {
            string reportedVersion = string.IsNullOrWhiteSpace(runtimeVersion) || runtimeVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? "Detected" : runtimeVersion;
            return new("Ready", "Using the manually configured Real-ESRGAN installation.", reportedVersion, $"{capabilities.Models.Count} detected", "Available", Device(capabilities), true);
        }
        if (installation.State != ManagedAiInstallationState.Installed)
        {
            string status = installation.State switch
            {
                ManagedAiInstallationState.NotInstalled => "Not installed",
                ManagedAiInstallationState.Incomplete => "Installation incomplete",
                _ => "Installation invalid"
            };
            return new(status, installation.Detail, version, "Unavailable", "Unavailable", "Unavailable", true);
        }

        if (!capabilities.IsAvailable)
            return new("Runtime validation failed", capabilities.Error ?? "The managed executable could not be inspected.", version, "Unavailable", "Unavailable", "Unavailable", true);
        if (capabilities.Models.Count == 0)
            return new("Models missing/incomplete", capabilities.Error ?? "No complete supported models were found.", version, "0 detected", capabilities.VulkanAvailable ? "Available" : "Unavailable", Device(capabilities), true);
        if (!capabilities.VulkanAvailable)
            return new("Vulkan unavailable", "The executable did not report a usable Vulkan runtime.", version, $"{capabilities.Models.Count} detected", "Unavailable", Device(capabilities), true);

        return new("Ready", "Ready to use with the NCNN / Vulkan provider.", version, $"{capabilities.Models.Count} detected", "Available", Device(capabilities), false);
    }

    private static string Device(AiRestorationCapabilities capabilities) => capabilities.Devices.FirstOrDefault(device => !device.Equals("Auto", StringComparison.OrdinalIgnoreCase)) ?? "Auto";
}
