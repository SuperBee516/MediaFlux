using System.Numerics;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class StorageSavingsContractTests
{
    [Theory]
    [InlineData(1_000, 500, true)]
    [InlineData(1_000, 900, true)]
    [InlineData(1_000, 901, false)]
    [InlineData(101, 90, true)]
    [InlineData(101, 91, false)]
    public void SmallSourceUsesOnlyConservativeTenPercentBoundary(long source, long output, bool passes)
    {
        StorageSavingsContract contract = StorageSavingsContractService.Resolve(true, source);
        Assert.Equal(10, contract.MinimumSavingsPercent);
        Assert.Null(contract.MinimumSavingsBytes);
        StorageSavingsEvaluation result = StorageSavingsContractService.Evaluate(contract, output);
        Assert.Equal(passes ? StorageSavingsAcceptance.Accepted : StorageSavingsAcceptance.Rejected, result.Acceptance);
        Assert.Equal(source - output, result.ActualSavingsBytes);
    }

    [Theory]
    [InlineData(1_073_741_823, false)]
    [InlineData(1_073_741_824, true)]
    [InlineData(2_147_483_648, true)]
    [InlineData(long.MaxValue, true)]
    public void LargeSourceRecordsBothClausesAndExactMaximum(long source, bool absoluteApplies)
    {
        StorageSavingsContract contract = StorageSavingsContractService.Resolve(true, source);
        long required = (long)(((BigInteger)source + 9) / 10);
        Assert.Equal(source - required, contract.MaximumAcceptedOutputBytes);
        Assert.Equal(absoluteApplies ? 104_857_600L : (long?)null, contract.MinimumSavingsBytes);
        Assert.Equal(StorageSavingsAcceptance.Accepted,
            StorageSavingsContractService.Evaluate(contract, source - required).Acceptance);
        Assert.Equal(StorageSavingsAcceptance.Rejected,
            StorageSavingsContractService.Evaluate(contract, source - required + 1).Acceptance);
        if (absoluteApplies)
        {
            Assert.True(required > contract.MinimumSavingsBytes);
            Assert.Equal(StorageSavingsAcceptance.Rejected,
                StorageSavingsContractService.Evaluate(contract, source - 104_857_600).Acceptance);
        }
    }

    [Fact]
    public void AbsoluteRequirementCanDominateInTheGeneralResolver()
    {
        StorageSavingsContract contract = StorageSavingsContractService.ResolveRequirements(1000, 10, 200);
        Assert.Equal(800, contract.MaximumAcceptedOutputBytes);
        Assert.Equal(StorageSavingsAcceptance.Accepted, StorageSavingsContractService.Evaluate(contract, 800).Acceptance);
        Assert.Equal(StorageSavingsAcceptance.Rejected, StorageSavingsContractService.Evaluate(contract, 801).Acceptance);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void UnknownOrInvalidSourceFailsClosed(long? source)
    {
        StorageSavingsContract contract = StorageSavingsContractService.Resolve(true, source);
        Assert.True(contract.Applies);
        Assert.Null(contract.MaximumAcceptedOutputBytes);
        Assert.Equal(StorageSavingsAcceptance.Rejected, StorageSavingsContractService.Evaluate(contract, 1).Acceptance);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(101, null)]
    [InlineData(10, -1L)]
    public void InvalidRequirementsFailClosed(int percent, long? absolute)
    {
        var contract = StorageSavingsContractService.ResolveRequirements(1000, percent, absolute);
        Assert.Null(contract.MaximumAcceptedOutputBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(99)]
    [InlineData(100)]
    public void ArithmeticAtLongMaximumMatchesExactRationalArithmetic(int percent)
    {
        long source = long.MaxValue;
        var contract = StorageSavingsContractService.ResolveRequirements(source, percent);
        long required = (long)(((BigInteger)source * percent + 99) / 100);
        Assert.Equal(source - required, contract.MaximumAcceptedOutputBytes);
    }

    [Fact]
    public void DisabledContractPreservesNonApplicableBehavior()
    {
        var contract = StorageSavingsContractService.Resolve(false, null);
        Assert.False(contract.Applies);
        Assert.Equal(StorageSavingsAcceptance.NotApplicable,
            StorageSavingsContractService.Evaluate(contract, long.MaxValue).Acceptance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidCandidateCannotPass(long output) => Assert.Equal(StorageSavingsAcceptance.Rejected,
        StorageSavingsContractService.Evaluate(StorageSavingsContractService.Resolve(true, 1000), output).Acceptance);
}
