using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiConfigurationComparisonTests
{
    [Fact]
    public void ChoosingComparisonConfigurationChangesPreviewOnly()
    {
        var encode = new VideoRestorationSettings { AiMode = AiRestorationMode.Animation, AiModelId = "first", AiScale = AiRestorationScale.X2 };
        var selection = new VideoRestorationPreviewSelection(encode);
        selection.UsePreviewSettings(new VideoRestorationSettings { AiMode = AiRestorationMode.Animation, AiModelId = "second", AiScale = AiRestorationScale.X4 });
        Assert.Equal("first", selection.EncodeSettings.AiModelId); Assert.Equal("second", selection.PreviewSettings.AiModelId); Assert.True(selection.DiffersFromEncode);
    }

    [Fact]
    public void AiSettingsParticipateInPreviewEquality()
    {
        var left = new VideoRestorationSettings { AiMode = AiRestorationMode.Animation, AiModelId = "a", AiScale = AiRestorationScale.X2 };
        var right = left.Clone(); right.AiScale = AiRestorationScale.X4;
        Assert.False(VideoRestorationPreviewSelection.Equivalent(left, right));
    }
}
