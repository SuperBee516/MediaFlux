using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

public sealed class UiConfigPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MediaFlux-UiConfigTests",
        Guid.NewGuid().ToString("N"));

    public UiConfigPersistenceTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void SummaryPreviewHeightRoundTrips()
    {
        string path = Path.Combine(_root, "config.json");
        var config = new Config
        {
            EncodeInfoHeight = 412
        };

        config.Save(path);
        Config loaded = Config.Load(path);

        Assert.Equal(412, loaded.EncodeInfoHeight);
    }

    [Fact]
    public void OlderConfigUsesDefaultSummaryPreviewHeight()
    {
        string path = Path.Combine(_root, "legacy.json");
        File.WriteAllText(path, """{"EncodeInfoHeaderCollapsed":false}""");

        Config loaded = Config.Load(path);

        Assert.Equal(0, loaded.EncodeInfoHeight);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
