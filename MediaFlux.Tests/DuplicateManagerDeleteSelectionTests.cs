using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class DuplicateManagerDeleteSelectionTests
{
    [Fact]
    public void EligibleNonKeeperDefaultsToSuggestedSelectionAndManualCheckPersists()
    {
        DuplicateGroup group = Group(1);
        DuplicateItem candidate = Item("candidate.mkv", "Trash candidate");
        var state = new DuplicateManagerDeleteSelectionState();

        Assert.True(state.Resolve(group, candidate));
        Assert.True(state.Record(group, candidate, true));
        Assert.True(state.Resolve(group, candidate));
    }

    [Fact]
    public void ManualUncheckSurvivesStateResolutionRefresh()
    {
        DuplicateGroup group = Group(1);
        DuplicateItem candidate = Item("candidate.mkv", "Trash candidate");
        var state = new DuplicateManagerDeleteSelectionState();

        Assert.False(state.Record(group, candidate, false));
        Assert.False(state.Resolve(group, candidate));
        Assert.False(state.Resolve(group, candidate));
    }

    [Fact]
    public void KeeperCannotBeSelectedEvenWhenAStaleCheckedValueIsRecorded()
    {
        DuplicateGroup group = Group(1);
        DuplicateItem keeper = Item("keeper.mkv", "Selected keeper");
        var state = new DuplicateManagerDeleteSelectionState();

        Assert.False(state.Record(group, keeper, true));
        Assert.False(state.Resolve(group, keeper));
        Assert.False(DuplicateCleanupPolicy.CanCleanupItem(group, keeper));
    }

    [Fact]
    public void GroupsKeepIndependentManualSelections()
    {
        DuplicateItem first = Item("first.mkv", "Trash candidate");
        DuplicateItem second = Item("second.mkv", "Trash candidate");
        var state = new DuplicateManagerDeleteSelectionState();

        Assert.False(state.Record(Group(1), first, false));
        Assert.True(state.Record(Group(2), second, true));
        Assert.False(state.Resolve(Group(1), first));
        Assert.True(state.Resolve(Group(2), second));
    }

    [Fact]
    public void NormalGridRerenderUsesTheExplicitSelectionInsteadOfRecomputingIt()
    {
        DuplicateGroup group = Group(1);
        DuplicateItem candidate = Item("candidate.mkv", "Trash candidate");
        var state = new DuplicateManagerDeleteSelectionState();
        state.Record(group, candidate, false);

        // AddDuplicateManagerGridRow calls Resolve each time it rebuilds a row.
        Assert.False(state.Resolve(group, candidate));
    }

    [Fact]
    public void SuggestedSelectionStillDefaultsCheckedButProtectedAndReviewRowsDoNot()
    {
        var state = new DuplicateManagerDeleteSelectionState();
        Assert.True(state.Resolve(Group(1), Item("candidate.mkv", "Trash candidate")));
        Assert.False(state.Resolve(Group(2), Item("protected.mkv", "Trash candidate", protectedReference: true)));
        Assert.False(state.Resolve(Group(3, "Review only"), Item("review.mkv", "Trash candidate")));
    }

    [Fact]
    public void ChangingKeeperForcesTheNewKeeperFalseWithoutDiscardingOtherManualIntent()
    {
        var state = new DuplicateManagerDeleteSelectionState();
        DuplicateItem oldKeeper = Item("old-keeper.mkv", "Selected keeper");
        DuplicateItem newKeeper = Item("new-keeper.mkv", "Trash candidate");
        DuplicateGroup before = Group(1);
        DuplicateGroup after = Group(1);

        state.Record(before, newKeeper, true);
        Assert.True(state.Resolve(after, newKeeper));
        Assert.False(state.Record(after, oldKeeper, true));
        Assert.False(state.Resolve(after, oldKeeper));
        Assert.False(state.Resolve(after, Item("new-keeper.mkv", "Selected keeper")));
    }

    [Fact]
    public void FinalCheckedCandidatePolicyRejectsKeeperAndKeepsEligibleCheckboxSelection()
    {
        DuplicateGroup group = Group(1);
        DuplicateItem keeper = Item("keeper.mkv", "Selected keeper");
        DuplicateItem candidate = Item("candidate.mkv", "Trash candidate");
        var state = new DuplicateManagerDeleteSelectionState();
        state.Record(group, keeper, true);
        state.Record(group, candidate, true);

        Assert.False(state.Resolve(group, keeper));
        Assert.True(state.Resolve(group, candidate));
        Assert.False(DuplicateCleanupPolicy.CanCleanupItem(group, keeper));
        Assert.True(DuplicateCleanupPolicy.CanCleanupItem(group, candidate));
    }

    private static DuplicateGroup Group(int id, string confidence = "Exact") => new(
        id, confidence, 99, "test", "Exact hash", 0, 0, 0, 0,
        Array.Empty<DuplicateItem>());

    private static DuplicateItem Item(string path, string recommendation, bool protectedReference = false) => new(
        path, 100, "h264", 1920, 1080, 60, 1, DateTime.UtcNow, DateTime.UtcNow,
        protectedReference, "test", recommendation);
}
