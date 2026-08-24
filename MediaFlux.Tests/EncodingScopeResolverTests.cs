using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingScopeResolverTests
{
    [Fact]
    public void PartialSelection_RequiresChoice_AndSelectedChoiceKeepsOnlySelectedJobs()
    {
        var jobs = new[] { new object(), new object(), new object() };
        var scope = EncodingScopeResolver.Analyze(jobs, new[] { jobs[0] });

        Assert.True(scope.RequiresChoice);
        Assert.Same(jobs[0], Assert.Single(scope.Resolve(EncodingScopeChoice.Selected)!));
    }

    [Fact]
    public void SelectedScope_IsTheCollectionPresentedToSmartEncode()
    {
        var jobs = new[] { new object(), new object(), new object(), new object() };
        var scope = EncodingScopeResolver.Analyze(jobs, new[] { jobs[1], jobs[3] });

        IReadOnlyList<object> smartEncodeScope = scope.Resolve(EncodingScopeChoice.Selected)!;

        Assert.Equal(new[] { jobs[1], jobs[3] }, smartEncodeScope);
    }

    [Fact]
    public void NoOrFullEligibleSelection_UsesEntireQueueWithoutChoice()
    {
        var jobs = new[] { new object(), new object() };

        var noneSelected = EncodingScopeResolver.Analyze(jobs, Array.Empty<object>());
        var allSelected = EncodingScopeResolver.Analyze(jobs, jobs);

        Assert.False(noneSelected.RequiresChoice);
        Assert.False(allSelected.RequiresChoice);
        Assert.Equal(jobs, noneSelected.Resolve(EncodingScopeChoice.EntireQueue));
        Assert.Equal(jobs, allSelected.Resolve(EncodingScopeChoice.EntireQueue));
    }

    [Fact]
    public void IneligibleSelectedRows_AreNotCountedInScope()
    {
        var eligible = new[] { new object(), new object() };
        var ineligible = new object();
        var scope = EncodingScopeResolver.Analyze(eligible, new[] { eligible[0], ineligible });

        Assert.True(scope.RequiresChoice);
        Assert.Same(eligible[0], Assert.Single(scope.SelectedJobs));
        Assert.Equal(2, scope.EligibleJobs.Count);
    }
}
