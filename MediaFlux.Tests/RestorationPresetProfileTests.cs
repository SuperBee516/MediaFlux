using System.Text.Json;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class RestorationPresetProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxRestorationProfiles", Guid.NewGuid().ToString("N"));
    public RestorationPresetProfileTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void BuiltInPresetsPopulateExpectedExistingSettings()
    {
        VideoRestorationSettings cartoon = BuiltInRestorationPresetService.Apply(BuiltInRestorationPreset.ClassicCartoon);
        VideoRestorationSettings dvd = BuiltInRestorationPresetService.Apply(BuiltInRestorationPreset.DvdUpscale);
        VideoRestorationSettings vhs = BuiltInRestorationPresetService.Apply(BuiltInRestorationPreset.VhsCleanup);
        VideoRestorationSettings film = BuiltInRestorationPresetService.Apply(BuiltInRestorationPreset.FilmPreservation);
        VideoRestorationSettings heavy = BuiltInRestorationPresetService.Apply(BuiltInRestorationPreset.HeavyRestoration);

        Assert.Equal(VideoRestorationMode.Custom, cartoon.Mode); Assert.Equal(VideoRestorationPreset.Custom, cartoon.Preset);
        Assert.Equal(AiRestorationMode.Animation, cartoon.AiMode); Assert.Equal(AiRestorationScale.X2, cartoon.AiScale); Assert.Equal(VideoRestorationStrength.Light, cartoon.Denoise); Assert.Equal(VideoRestorationStrength.Off, cartoon.Deband);
        Assert.Equal(AiRestorationMode.General, dvd.AiMode); Assert.Equal(AiRestorationScale.X2, dvd.AiScale); Assert.Equal(VideoRestorationStrength.Light, dvd.Deblock);
        Assert.Equal(AiRestorationMode.Off, vhs.AiMode); Assert.Equal(VideoRestorationStrength.Strong, vhs.Denoise); Assert.Equal(VideoRestorationDeinterlace.AutoSafe, vhs.Deinterlace);
        Assert.Equal(AiRestorationMode.Off, film.AiMode); Assert.Equal(VideoRestorationStrength.Off, film.Denoise); Assert.Equal(VideoRestorationStrength.Off, film.Sharpen);
        Assert.Equal(VideoRestorationStrength.Strong, heavy.Denoise); Assert.Equal(VideoRestorationStrength.Strong, heavy.Deblock); Assert.Equal(VideoRestorationStrength.Strong, heavy.Deband);
    }

    [Fact]
    public void AiGeneralPresetRetainsTheCurrentGeneralScale()
    {
        var current = new VideoRestorationSettings { AiMode = AiRestorationMode.General, AiModelId = "general-model", AiScale = AiRestorationScale.X3 };
        VideoRestorationSettings settings = BuiltInRestorationPresetService.Apply(BuiltInRestorationPreset.AiGeneralEnhancement, current);
        Assert.Equal(AiRestorationMode.General, settings.AiMode); Assert.Equal("general-model", settings.AiModelId); Assert.Equal(AiRestorationScale.X3, settings.AiScale);
    }

    [Fact]
    public void ProfilesRoundTripAndRemainIndependentOfLaterChanges()
    {
        var service = new RestorationProfileService(_root);
        var settings = new VideoRestorationSettings { Mode = VideoRestorationMode.Custom, Preset = VideoRestorationPreset.Custom, Denoise = VideoRestorationStrength.Medium, Deblock = VideoRestorationStrength.Light, Deband = VideoRestorationStrength.Strong, Sharpen = VideoRestorationStrength.Light, Deinterlace = VideoRestorationDeinterlace.AutoSafe, AiMode = AiRestorationMode.Animation, AiModelId = "anime", AiScale = AiRestorationScale.X2, AiDevice = "GPU 1" };
        service.Save("Cartoon Cleanup", settings);
        settings.Denoise = VideoRestorationStrength.Off; settings.AiModelId = "changed";

        RestorationProfileDocument profile = Assert.Single(service.LoadAll());
        Assert.Equal(RestorationProfileService.CurrentVersion, profile.Version); Assert.Equal("Cartoon Cleanup", profile.Name);
        Assert.Equal(VideoRestorationStrength.Medium, profile.Settings.Denoise); Assert.Equal(VideoRestorationStrength.Light, profile.Settings.Deblock); Assert.Equal(VideoRestorationStrength.Strong, profile.Settings.Deband); Assert.Equal(VideoRestorationStrength.Light, profile.Settings.Sharpen); Assert.Equal(VideoRestorationDeinterlace.AutoSafe, profile.Settings.Deinterlace); Assert.Equal("anime", profile.Settings.AiModelId); Assert.Equal(AiRestorationScale.X2, profile.Settings.AiScale); Assert.Equal(VideoRestorationMode.Custom, profile.Settings.Mode);
    }

    [Fact]
    public void ProfilesUseOneReadableVersionedJsonFileAndMigrateVersionZero()
    {
        var service = new RestorationProfileService(_root);
        service.Save("Readable", new VideoRestorationSettings { Mode = VideoRestorationMode.Custom, AiMode = AiRestorationMode.General });
        string path = Assert.Single(Directory.EnumerateFiles(_root, "*.json"));
        string json = File.ReadAllText(path);
        Assert.Contains("\"Version\": 1", json); Assert.Contains("\"AiMode\": \"General\"", json);

        File.WriteAllText(Path.Combine(_root, "legacy.json"), JsonSerializer.Serialize(new RestorationProfileDocument(0, "Legacy", new VideoRestorationSettings { Mode = VideoRestorationMode.Custom })));
        Assert.Equal(RestorationProfileService.CurrentVersion, Assert.Single(service.LoadAll(), profile => profile.Name == "Legacy").Version);
    }

    [Fact]
    public void FutureProfileVersionsAreIgnoredWithoutAffectingExistingProfiles()
    {
        var service = new RestorationProfileService(_root);
        service.Save("Current", new VideoRestorationSettings { Mode = VideoRestorationMode.Custom });
        File.WriteAllText(Path.Combine(_root, "future.json"), "{ \"Version\": 99, \"Name\": \"Future\", \"Settings\": {} }");
        Assert.Equal("Current", Assert.Single(service.LoadAll()).Name);
    }

    [Fact]
    public void ProfileRenameAndDeleteOperateOnTheSelectedProfile()
    {
        var service = new RestorationProfileService(_root);
        service.Save("Original", new VideoRestorationSettings { Mode = VideoRestorationMode.Auto });
        service.Rename("Original", "Renamed");
        Assert.Equal("Renamed", Assert.Single(service.LoadAll()).Name);
        service.Delete("Renamed");
        Assert.Empty(service.LoadAll());
    }

    [Fact]
    public void LoadedCustomProfileUsesTheExistingPreviewAndEncodeResolutionPath()
    {
        VideoRestorationSettings profile = BuiltInRestorationPresetService.Apply(BuiltInRestorationPreset.LightCleanup);
        VideoRestorationSettings resolved = VideoRestorationModeResolver.Resolve(profile);
        Assert.Equal(profile.Denoise, resolved.Denoise); Assert.Equal(VideoRestorationMode.Custom, resolved.Mode);
    }
}
