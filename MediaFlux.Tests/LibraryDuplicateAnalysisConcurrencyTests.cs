using MediaFlux.Services.LibraryCatalog;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LibraryDuplicateAnalysisConcurrencyTests
{
    [Fact]
    public async Task ExactAndVisualShareOneExclusiveAnalysisSlot()
    {
        using var gate = new LibraryDuplicateAnalysisGate();
        using LibraryDuplicateAnalysisGate.Lease exact = await gate.AcquireAsync(LibraryDuplicateAnalyzer.Exact, CancellationToken.None);
        Task<LibraryDuplicateAnalysisGate.Lease> visualTask = gate.AcquireAsync(LibraryDuplicateAnalyzer.Visual, CancellationToken.None);

        Assert.False(visualTask.IsCompleted);
        Assert.Equal(LibraryDuplicateAnalyzer.Exact, gate.ActiveAnalyzer);

        exact.Dispose();
        using LibraryDuplicateAnalysisGate.Lease visual = await visualTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(LibraryDuplicateAnalyzer.Visual, gate.ActiveAnalyzer);
    }

    [Fact]
    public async Task WaitingAnalyzerCanBeCanceledWithoutBlockingTheActiveAnalyzer()
    {
        using var gate = new LibraryDuplicateAnalysisGate();
        using LibraryDuplicateAnalysisGate.Lease exact = await gate.AcquireAsync(LibraryDuplicateAnalyzer.Exact, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task<LibraryDuplicateAnalysisGate.Lease> visualTask = gate.AcquireAsync(LibraryDuplicateAnalyzer.Visual, cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => visualTask);
        Assert.Equal(LibraryDuplicateAnalyzer.Exact, gate.ActiveAnalyzer);
    }

    [Fact]
    public async Task ZeroWorkReleaseAllowsTheNextAnalyzerToAcquire()
    {
        using var gate = new LibraryDuplicateAnalysisGate();
        using (LibraryDuplicateAnalysisGate.Lease exact = await gate.AcquireAsync(LibraryDuplicateAnalyzer.Exact, CancellationToken.None))
        {
            // The coordinators hold this lease for the complete analysis lifecycle,
            // including catalog preparation, zero-work stages, and terminal cleanup.
        }

        using LibraryDuplicateAnalysisGate.Lease visual = await gate.AcquireAsync(LibraryDuplicateAnalyzer.Visual, CancellationToken.None);
        Assert.Equal(LibraryDuplicateAnalyzer.Visual, gate.ActiveAnalyzer);
    }
}
