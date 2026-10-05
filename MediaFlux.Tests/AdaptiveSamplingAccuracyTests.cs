using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AdaptiveSamplingAccuracyTests
{
    [Theory]
    [InlineData(76)]
    [InlineData(225)]
    [InlineData(2752.783367)]
    [InlineData(14400)]
    public void LongSourcesCoverNineTemporalRegionsWithBoundedWork(double seconds)
    {
        var positions = StorageSavingsSampleSelector.Positions(TimeSpan.FromSeconds(seconds));
        Assert.Equal(9, positions.Count);
        Assert.InRange(positions.Sum(p => p.Duration.TotalSeconds), 0, 225);
        for (int i = 0; i < positions.Count; i++)
        {
            double regionStart = seconds * i / 9;
            double regionEnd = seconds * (i + 1) / 9;
            Assert.InRange(positions[i].Start.TotalSeconds, regionStart - 0.000001, regionEnd);
            Assert.InRange((positions[i].Start + positions[i].Duration).TotalSeconds, regionStart, regionEnd + 0.000001);
        }
    }

    [Fact]
    public async Task ScenesBetweenOldThreePositionsPreventFalseClearlyMeets()
    {
        var request = AdaptiveStorageSavingsTests.Request() with { SourceDuration = TimeSpan.FromSeconds(900) };
        // Old beginning/middle/end all observed 500 B/s and clearly met.
        // Six previously unobserved regions carry 1500 B/s.
        var runner = new AdaptiveStorageSavingsTests.FakeSamples((_, i) => i is 0 or 4 or 8 ? 500 : 1500);
        var result = await new StorageSavingsSampleSelector(runner).SelectAsync(request, null, CancellationToken.None);
        Assert.Equal(AdaptiveSelectionDisposition.NotSuitable, result.Disposition);
        Assert.Equal(result.PreferredQuality, result.SelectedQuality);
        Assert.All(result.Candidates, c =>
        {
            Assert.Equal(AdaptiveSampleClassification.Borderline, c.Classification);
            Assert.True(c.ProjectedTypicalBytes > request.Contract.MaximumAcceptedOutputBytes);
            Assert.Equal(9, c.Samples.Count);
        });
        Assert.Contains("optimistic minimum", result.Reason);
    }

    [Fact]
    public void HeterogeneousRatesRetainMaximumVideoProjectionAndAncillaryBudgets()
    {
        var request = AdaptiveStorageSavingsTests.Request() with { Ancillary = new(12_000, 3_000, 4_000, 1234) };
        RepresentativeSampleEvidence[] samples = [
            new(new("Beginning", TimeSpan.Zero, TimeSpan.FromSeconds(25)), 25_000, 25),
            new(new("Middle", TimeSpan.FromSeconds(37.5), TimeSpan.FromSeconds(25)), 75_000, 25),
            new(new("End", TimeSpan.FromSeconds(75), TimeSpan.FromSeconds(25)), 50_000, 25)];
        var candidate = StorageSavingsSampleSelector.ProjectCandidate(request, 24, samples);
        Assert.Equal(101234, candidate.ProjectedLowerBytes);
        Assert.Equal(520234, candidate.ProjectedUpperBytes, 3);
        Assert.Equal(420234, candidate.ProjectedTypicalBytes!.Value, 3);
        Assert.Equal(200000, candidate.ContainerAllowanceBytes, 3);
    }

    [Theory]
    [InlineData(19, 28012485, 30582582, 30374502, 3081410700.353127, 3414180789.8241506)]
    [InlineData(20, 27566747, 30537481, 30393133, 3032378926.0298567, 3409169506.859234)]
    [InlineData(21, 27120881, 30023685, 30279874, 2983333071.5359173, 3385038155.747165)]
    public void RecordedRejectedJobRatesRemainBorderlineButCannotJustifyFullEncode(
        int quality, long beginning, long middle, long end, double lower, double upper)
    {
        var request = AdaptiveStorageSavingsTests.Request() with
        {
            Contract = StorageSavingsContractService.Resolve(true, 3458469647),
            SourceDuration = TimeSpan.FromSeconds(2752.783367),
            Ancillary = new(15914184.742547875, 0, 0, 0)
        };
        RepresentativeSampleEvidence[] samples = [
            new(new("Beginning", TimeSpan.Zero, TimeSpan.FromSeconds(25)), beginning, 25.025),
            new(new("Middle", TimeSpan.FromSeconds(1363.8916835), TimeSpan.FromSeconds(25)), middle, 25.025),
            new(new("End", TimeSpan.FromSeconds(2727.783367), TimeSpan.FromSeconds(25)), end, 24.991633)];
        var candidate = StorageSavingsSampleSelector.ProjectCandidate(request, quality, samples);
        Assert.Equal(lower, candidate.ProjectedLowerBytes, 3);
        Assert.Equal(upper, candidate.ProjectedUpperBytes, 3);
        Assert.Equal(AdaptiveSampleClassification.Borderline, candidate.Classification);
        Assert.False(StorageSavingsSampleSelector.IsEligibleBorderline(candidate, 3112622682));
        Assert.True(candidate.ProjectedTypicalBytes > 3112622682);
        Assert.Equal(StorageSavingsAcceptance.Rejected,
            StorageSavingsContractService.Evaluate(request.Contract, 3312117484).Acceptance);
    }

    [Fact]
    public async Task DiagnosticsPreserveMeasurementProjectionAndSelectionWithoutPaths()
    {
        var request = AdaptiveStorageSavingsTests.Request();
        var evidence = await new StorageSavingsSampleSelector(new AdaptiveStorageSavingsTests.FakeSamples((_, _) => 5000))
            .SelectAsync(request, null, CancellationToken.None);
        string diagnostics = string.Join("\n", StorageSavingsSampleSelector.DescribeDiagnostics(evidence, 999, 900000));
        Assert.Equal(100, evidence.SourceDurationSeconds);
        Assert.Contains("sourceSeconds=100.000000", diagnostics); // frozen measurement duration, not a stale queue value
        foreach (string field in new[] { "quality=24", "startSeconds=", "requestedSeconds=", "measuredSeconds=",
                     "videoPayloadBytes=", "bytesPerSecond=", "minRate=", "maxRate=", "lowerBytes=", "typicalBytes=",
                     "upperBytes=", "containerUpperBytes=", "maximumBytes=900000", "classification=ClearlyMeets", "disposition=Selected" })
            Assert.Contains(field, diagnostics);
        Assert.DoesNotContain(request.Input.SourcePath, diagnostics);
        Assert.Equal(12, diagnostics.Split('\n').Length);
    }
}
