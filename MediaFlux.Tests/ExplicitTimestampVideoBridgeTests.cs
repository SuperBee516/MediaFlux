using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class ExplicitTimestampVideoBridgeTests
{
    [Fact]
    public void MissingSharedLibrariesFailsClosedWithoutCliFallback()
    {
        ExplicitTimestampBridgeCapability capability = new ExplicitTimestampVideoBridgeService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).GetCapability();
        Assert.Equal(ExplicitTimestampBridgeAvailability.Unavailable, capability.Availability);
        Assert.Contains("shared FFmpeg libraries", capability.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnavailableBridgeDoesNotCreateApproximateOutput()
    {
        string root = Path.Combine(Path.GetTempPath(), "mediaflux-explicit-pts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string frame = Path.Combine(root, "frame-00000000.png"); File.WriteAllBytes(frame, Array.Empty<byte>());
            var manifest = new AiTimestampManifest("1/1000", new[] { new AiFrameTimingEntry(0, 1000, 1, .04, "1/1000", Path.GetFileName(frame), Path.GetFileName(frame)) });
            string output = Path.Combine(root, "output.mkv");
            await Assert.ThrowsAsync<AiRestorationValidationException>(() => new ExplicitTimestampVideoBridgeService().ProduceAsync(new(manifest, new[] { frame }, output)));
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MismatchDiagnosticsIdentifyMissingTimingIdentityWithoutPairing()
    {
        CorrelatedFrameExtractionDiagnostics result = CorrelatedFrameExtractionService.Diagnose(5, new[] { 0, 1, 3 });
        Assert.Equal(new[] { 2, 4 }, result.MissingTimingIndexes);
        Assert.Empty(result.UnexpectedTimingIndexes);
    }
}
