using System.IO.Compression;
using System.Security.Cryptography;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class ManagedAiProvisioningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxManagedAiTests", Guid.NewGuid().ToString("N"));
    public ManagedAiProvisioningTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } AiRestorationBackendService.InvalidateCache(); }

    [Fact]
    public void ProductionManifestPinsTheOfficialAssetAndMediaFluxChecksum()
    {
        Assert.Equal("https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip", ManagedAiComponents.RealEsrgan.DownloadUri.AbsoluteUri);
        Assert.Matches("^[0-9A-Fa-f]{64}$", ManagedAiComponents.RealEsrgan.ExpectedSha256 ?? string.Empty);
        Assert.Equal("realesrgan-ncnn-vulkan.exe", ManagedAiComponents.RealEsrgan.ExecutableRelativePath);
        Assert.Equal("models", ManagedAiComponents.RealEsrgan.ModelsRelativePath);
    }

    [Fact]
    public void ExplicitExecutableTakesPrecedenceOverManagedAndLegacy()
    {
        string explicitPath = Touch(Path.Combine(_root, "chosen.exe"));
        Touch(Path.Combine(_root, "Programs", "RealESRGAN", "1", "realesrgan-ncnn-vulkan.exe"));
        Touch(Path.Combine(_root, "Programs", "realesrgan-ncnn-vulkan.exe"));
        AiBackendResolution result = new AiBackendPathResolver(_root).Resolve(explicitPath, null);
        Assert.Equal(Path.GetFullPath(explicitPath), result.ExecutablePath);
    }

    [Fact]
    public void ManagedInstallationPrecedesLegacyProgramsAndModelsFollowExecutable()
    {
        string managed = Path.Combine(_root, "Programs", "RealESRGAN", ManagedAiComponents.RealEsrgan.Version);
        string exe = Touch(Path.Combine(managed, "realesrgan-ncnn-vulkan.exe")); Directory.CreateDirectory(Path.Combine(managed, "models"));
        Touch(Path.Combine(_root, "Programs", "realesrgan-ncnn-vulkan.exe"));
        AiBackendResolution result = new AiBackendPathResolver(_root).Resolve(null, null);
        Assert.Equal(Path.GetFullPath(exe), result.ExecutablePath); Assert.Equal(Path.Combine(managed, "models"), result.ModelsDirectory); Assert.True(result.IsManaged);
    }

    [Fact]
    public void DeletedConfiguredExecutableDoesNotPairManagedExecutableWithStaleManualModels()
    {
        string managed = Path.Combine(_root, "Programs", "RealESRGAN", ManagedAiComponents.RealEsrgan.Version); string managedExe = Touch(Path.Combine(managed, "realesrgan-ncnn-vulkan.exe")); Directory.CreateDirectory(Path.Combine(managed, "models"));
        string staleModels = Path.Combine(_root, "external-models"); Directory.CreateDirectory(staleModels);
        AiBackendResolution result = new AiBackendPathResolver(_root).Resolve(Path.Combine(_root, "deleted.exe"), staleModels);
        Assert.Equal(Path.GetFullPath(managedExe), result.ExecutablePath); Assert.Equal(Path.Combine(managed, "models"), result.ModelsDirectory);
    }

    [Fact]
    public void LegacyApplicationAndProgramsDiscoveryStillWorks()
    {
        string exe = Touch(Path.Combine(_root, "Programs", "realesrgan-ncnn-vulkan.exe"));
        AiBackendResolution result = new AiBackendPathResolver(_root).Resolve(null, null);
        Assert.Equal(Path.GetFullPath(exe), result.ExecutablePath, ignoreCase: true); Assert.False(result.IsManaged);
    }

    [Fact]
    public void InvalidExplicitModelsDirectoryFallsBackToResolvedExecutableModels()
    {
        string exe = Touch(Path.Combine(_root, "realesrgan-ncnn-vulkan.exe")); string models = Path.Combine(_root, "models"); Directory.CreateDirectory(models);
        AiBackendResolution result = new AiBackendPathResolver(_root).Resolve(exe, Path.Combine(_root, "missing-models"));
        Assert.Equal(models, result.ModelsDirectory);
    }

    [Fact] public void MissingManagedInstallationIsNotInstalled() => Assert.Equal(ManagedAiInstallationState.NotInstalled, new AiBackendPathResolver(_root).InspectManagedInstallation().State);

    [Fact]
    public void ManagedStateDistinguishesInstalledAndIncomplete()
    {
        string managed = Path.Combine(_root, "Programs", "RealESRGAN", ManagedAiComponents.RealEsrgan.Version); Directory.CreateDirectory(managed);
        Assert.Equal(ManagedAiInstallationState.Incomplete, new AiBackendPathResolver(_root).InspectManagedInstallation().State);
        Touch(Path.Combine(managed, "realesrgan-ncnn-vulkan.exe")); Directory.CreateDirectory(Path.Combine(managed, "models"));
        Assert.Equal(ManagedAiInstallationState.Installed, new AiBackendPathResolver(_root).InspectManagedInstallation().State);
    }

    [Fact]
    public async Task HashMismatchRejectsWithoutCreatingManagedInstallation()
    {
        string archive = CreateArchive(includeExecutable: true, unsafeEntry: false); var descriptor = Descriptor(HashOf(archive, "00"));
        AiProvisioningResult result = await new ManagedAiComponentInstaller(descriptor, Path.Combine(_root, "Programs", "RealESRGAN"), new FixtureDownloader(archive)).InstallAsync();
        Assert.False(result.Succeeded); Assert.Contains("SHA-256", result.Detail);
        Assert.False(Directory.Exists(Path.Combine(_root, "Programs", "RealESRGAN", "test")));
    }

    [Fact]
    public async Task ProductionDescriptorUsesPinnedHashBeforeDownloadAttempt()
    {
        AiProvisioningResult result = await new ManagedAiComponentInstaller(ManagedAiComponents.RealEsrgan, Path.Combine(_root, "Programs", "RealESRGAN"), new FixtureDownloader(Path.Combine(_root, "missing.zip"))).InstallAsync();
        Assert.False(result.Succeeded); Assert.DoesNotContain("No authoritative SHA-256", result.Detail); Assert.Contains("Could not find file", result.Detail);
    }

    [Fact]
    public async Task UnsafeArchiveEntryIsRejected()
    {
        string archive = CreateArchive(includeExecutable: true, unsafeEntry: true); var descriptor = Descriptor(HashOf(archive));
        AiProvisioningResult result = await new ManagedAiComponentInstaller(descriptor, Path.Combine(_root, "Programs", "RealESRGAN"), new FixtureDownloader(archive)).InstallAsync();
        Assert.False(result.Succeeded); Assert.Contains("traversal", result.Detail);
    }

    [Fact]
    public async Task FailedReplacementLeavesExistingValidInstallation()
    {
        string managedRoot = Path.Combine(_root, "Programs", "RealESRGAN"), existing = Path.Combine(managedRoot, "test"); Touch(Path.Combine(existing, "realesrgan-ncnn-vulkan.exe")); Directory.CreateDirectory(Path.Combine(existing, "models"));
        string archive = CreateArchive(includeExecutable: false, unsafeEntry: false); var descriptor = Descriptor(HashOf(archive));
        AiProvisioningResult result = await new ManagedAiComponentInstaller(descriptor, managedRoot, new FixtureDownloader(archive)).InstallAsync();
        Assert.False(result.Succeeded); Assert.Contains("structurally incomplete", result.Detail);
        Assert.True(File.Exists(Path.Combine(existing, "realesrgan-ncnn-vulkan.exe"))); Assert.True(Directory.Exists(Path.Combine(existing, "models")));
    }

    [Fact]
    public async Task SuccessfulInstallIsImmediatelyDiscoverable()
    {
        string archive = CreateArchive(includeExecutable: true, unsafeEntry: false); var descriptor = Descriptor(HashOf(archive)); string managedRoot = Path.Combine(_root, "Programs", "RealESRGAN");
        AiProvisioningResult result = await new ManagedAiComponentInstaller(descriptor, managedRoot, new FixtureDownloader(archive)).InstallAsync();
        AiBackendResolution resolved = new AiBackendPathResolver(_root, descriptor).Resolve(null, null);
        Assert.True(result.Succeeded); Assert.Equal(Path.Combine(result.ManagedDirectory, "realesrgan-ncnn-vulkan.exe"), resolved.ExecutablePath); Assert.Equal(ManagedAiInstallationState.Installed, new AiBackendPathResolver(_root, descriptor).InspectManagedInstallation().State);
    }

    private string CreateArchive(bool includeExecutable, bool unsafeEntry)
    {
        string path = Path.Combine(_root, Guid.NewGuid() + ".zip"); using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            if (includeExecutable) using (StreamWriter writer = new(zip.CreateEntry("realesrgan-ncnn-vulkan.exe").Open())) writer.Write("exe");
            zip.CreateEntry("models/");
            if (unsafeEntry) using (StreamWriter writer = new(zip.CreateEntry("../../escape.txt").Open())) writer.Write("bad");
        } return path;
    }
    private ManagedAiComponentDescriptor Descriptor(string hash) => ManagedAiComponents.RealEsrgan with { Version = "test", PackageIdentity = "fixture.zip", ExpectedSha256 = hash };
    private static string HashOf(string path, string? overrideHash = null) => overrideHash ?? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Touch(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "x"); return path; }

    private sealed class FixtureDownloader(string source) : IManagedAiPackageDownloader
    {
        public async Task DownloadAsync(Uri _, string destination, IProgress<AiProvisioningProgress>? progress, CancellationToken cancellationToken) { await using FileStream input = File.OpenRead(source); await using FileStream output = new(destination, FileMode.CreateNew); await input.CopyToAsync(output, cancellationToken); }
    }
}
