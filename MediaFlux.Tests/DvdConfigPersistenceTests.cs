using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

public sealed class DvdConfigPersistenceTests : IDisposable
{
    private readonly string _root;

    public DvdConfigPersistenceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-DvdConfigTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void OlderConfigDefaultsToLosslessRemux()
    {
        string path = Path.Combine(_root, "legacy.json");
        File.WriteAllText(path, """{"AutomaticallyBackupBeforeUpdates":false}""");

        Config config = Config.Load(path);

        Assert.Equal(
            DvdOutputMode.LosslessRemuxToMkv,
            config.GetLastDvdOutputMode());
        Assert.Equal("{MovieName}{TitleSetSuffix}", config.DvdOutputNamingPattern);
        Assert.Equal("", config.LastDvdInputFolder);
        Assert.Equal("", config.LastDvdOutputFolder);
    }

    [Fact]
    public void InvalidPersistedModeFallsBackToLosslessRemux()
    {
        string path = Path.Combine(_root, "invalid-mode.json");
        File.WriteAllText(path, """{"LastDvdOutputMode":"UnexpectedMode"}""");

        Config config = Config.Load(path);

        Assert.Equal(
            DvdOutputMode.LosslessRemuxToMkv,
            config.GetLastDvdOutputMode());
        Assert.Equal(
            nameof(DvdOutputMode.LosslessRemuxToMkv),
            config.LastDvdOutputMode);
    }

    [Fact]
    public void DvdPreferencesRoundTripWithUnicodeAndNetworkPaths()
    {
        string path = Path.Combine(_root, "config.json");
        var config = new Config
        {
            LastDvdInputFolder = @"\\server\media\Movie O'Brien 日本\VIDEO_TS",
            LastDvdOutputFolder = @"\\server\archive\Movies 日本",
            DvdOutputNamingPattern = "{MovieName} [{TitleSet}]"
        };
        config.SetLastDvdOutputMode(DvdOutputMode.EncodeUsingCurrentSettings);

        config.Save(path);
        Config loaded = Config.Load(path);

        Assert.Equal(config.LastDvdInputFolder, loaded.LastDvdInputFolder);
        Assert.Equal(config.LastDvdOutputFolder, loaded.LastDvdOutputFolder);
        Assert.Equal(
            DvdOutputMode.EncodeUsingCurrentSettings,
            loaded.GetLastDvdOutputMode());
        Assert.Equal(config.DvdOutputNamingPattern, loaded.DvdOutputNamingPattern);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
