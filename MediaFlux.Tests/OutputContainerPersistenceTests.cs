using System.Text.Json;
using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

public sealed class OutputContainerPersistenceTests
{
    [Fact]
    public void LegacyConfigWithoutContainer_LoadsAsMp4()
    {
        string path = WriteJson("{\"LastVideoCodec\":\"Hevc\"}");
        try
        {
            Config config = Config.Load(path);
            Assert.Equal(nameof(OutputContainerSelection.Mp4), config.LastOutputContainer);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(OutputContainerSelection.Auto)]
    [InlineData(OutputContainerSelection.Matroska)]
    [InlineData(OutputContainerSelection.Mp4)]
    public void ConfigRoundTripsStableContainerName(OutputContainerSelection selection)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mediaflux-container-{Guid.NewGuid():N}.json");
        try
        {
            var config = new Config { LastOutputContainer = selection.ToString() };
            config.Save(path);
            Assert.Equal(selection.ToString(), Config.Load(path).LastOutputContainer);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void PresetMissingContainer_DefaultsToMp4()
    {
        EncodingPreset preset = JsonSerializer.Deserialize<EncodingPreset>("{\"Name\":\"Legacy\"}")!;
        Assert.Equal(nameof(OutputContainerSelection.Mp4), preset.OutputContainer);
    }

    private static string WriteJson(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mediaflux-container-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
