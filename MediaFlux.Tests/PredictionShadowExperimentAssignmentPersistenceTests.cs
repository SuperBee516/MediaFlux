using System.Text.Json;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class PredictionShadowExperimentAssignmentPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxResearchAssignmentTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void QueueExportItemRoundTripsAssignmentAndLegacyItemRemainsReadable()
    {
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, "source.mp4");
        File.WriteAllText(source, "stable source");
        PredictionShadowExperimentAssignmentBinding binding = Assert.IsType<PredictionShadowExperimentAssignmentBinding>(
            PredictionShadowExperimentAssignmentPersistence.Capture(source, Assignment()));
        var item = new EncodeQueueItemState
        {
            Path = source,
            PredictionShadowExperimentAssignment = binding
        };

        string json = JsonSerializer.Serialize(item);
        EncodeQueueItemState loaded = Assert.IsType<EncodeQueueItemState>(
            JsonSerializer.Deserialize<EncodeQueueItemState>(json));
        Assert.Equal(binding, loaded.PredictionShadowExperimentAssignment);
        Assert.Equal(Assignment(), loaded.PredictionShadowExperimentAssignment!.Assignment);

        EncodeQueueItemState legacy = Assert.IsType<EncodeQueueItemState>(
            JsonSerializer.Deserialize<EncodeQueueItemState>("{\"Path\":\"legacy.mp4\"}"));
        Assert.Equal("legacy.mp4", legacy.Path);
        Assert.Null(legacy.PredictionShadowExperimentAssignment);
    }

    [Fact]
    public void QueueReconstructionAndReorderBindByExactSourceIdentity()
    {
        Directory.CreateDirectory(_root);
        string a = WriteSource("a.mp4", "source-a");
        string b = WriteSource("b.mp4", "source-b");
        string unrelated = WriteSource("unrelated.mp4", "source-c");
        PredictionShadowExperimentAssignmentBinding aBinding = Capture(a, Assignment(slot: 1));
        PredictionShadowExperimentAssignmentBinding bBinding = Capture(b, Assignment(slot: 2));
        Dictionary<string, PredictionShadowExperimentAssignmentBinding> saved =
            PredictionShadowExperimentAssignmentPersistence.CaptureForQueueReconstruction(
                [(a, aBinding), (b, bBinding)]);

        // Recreated rows arrive in a different order. Same paths recover their own binding;
        // occupying the former row/index does not transfer an assignment.
        Assert.Equal(Assignment(slot: 2), PredictionShadowExperimentAssignmentPersistence
            .RestoreForQueueReconstruction(saved, b)!.Assignment);
        Assert.Equal(Assignment(slot: 1), PredictionShadowExperimentAssignmentPersistence
            .RestoreForQueueReconstruction(saved, a)!.Assignment);
        Assert.Null(PredictionShadowExperimentAssignmentPersistence
            .RestoreForQueueReconstruction(saved, unrelated));
    }

    [Fact]
    public void ChangedOrRenamedSourceCannotInheritAnOldAssignment()
    {
        Directory.CreateDirectory(_root);
        string source = WriteSource("source.mp4", "before");
        PredictionShadowExperimentAssignmentBinding binding = Capture(source, Assignment());
        File.WriteAllText(source, "different bytes with a different length");
        Assert.False(PredictionShadowExperimentAssignmentPersistence.MatchesSource(binding, source));

        string renamed = WriteSource("renamed.mp4", "different bytes with a different length");
        Assert.False(PredictionShadowExperimentAssignmentPersistence.MatchesSource(binding, renamed));
    }

    [Fact]
    public void ReplacementKeepsExperimentSlotAndStratumAndIncrementsAttempt()
    {
        PredictionShadowExperimentAssignment original = Assignment();
        PredictionShadowExperimentAssignment replacement =
            PredictionShadowExperimentAssignmentPersistence.CreateReplacement(original);

        Assert.Equal(original.ExperimentId, replacement.ExperimentId);
        Assert.Equal(original.Slot, replacement.Slot);
        Assert.Equal(original.Attempt + 1, replacement.Attempt);
        Assert.Equal(original.Stratum, replacement.Stratum);
        Assert.Equal(PredictionShadowExperimentRole.Replacement, replacement.Role);
        Assert.True(PredictionShadowExperimentAssignmentPersistence.IsExplicitReplacement(original, replacement));
        Assert.False(PredictionShadowExperimentAssignmentPersistence.IsExplicitReplacement(
            original, replacement with { Slot = original.Slot + 1 }));
        Assert.False(PredictionShadowExperimentAssignmentPersistence.IsExplicitReplacement(
            original, replacement with { Role = PredictionShadowExperimentRole.Target }));
    }

    private string WriteSource(string name, string content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static PredictionShadowExperimentAssignmentBinding Capture(
        string path, PredictionShadowExperimentAssignment assignment) =>
        Assert.IsType<PredictionShadowExperimentAssignmentBinding>(
            PredictionShadowExperimentAssignmentPersistence.Capture(path, assignment));

    private static PredictionShadowExperimentAssignment Assignment(int slot = 3) =>
        new("MF-3C3-G3-R1", slot, 1, PredictionShadowExperimentStratum.Control,
            PredictionShadowExperimentRole.Target);
}
