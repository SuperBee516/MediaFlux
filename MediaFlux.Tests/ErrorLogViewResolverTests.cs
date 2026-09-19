using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class ErrorLogViewResolverTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MediaFlux-ErrorLogViews", Guid.NewGuid().ToString("N"));

    public ErrorLogViewResolverTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void LatestCuratedReportExcludesRawFilesAndPairsRawArtifact()
    {
        string reportA = Write("MediaFlux_Error_A.log", "A", DateTime.UtcNow.AddMinutes(-2));
        Write("MediaFlux_Error_A.raw.log", "raw A", DateTime.UtcNow.AddMinutes(-2));
        string reportB = Write("MediaFlux_Error_B.log", "B", DateTime.UtcNow.AddMinutes(-1));
        string rawB = Write("MediaFlux_Error_B.raw.log", "raw B", DateTime.UtcNow);

        Assert.Equal(reportB, ErrorLogService.FindLatestFailureDiagnosticReport(_directory));
        Assert.Equal(rawB, ErrorLogService.FindLatestRawFfmpegEvidence(_directory));
        Assert.NotEqual(reportA, ErrorLogService.FindLatestFailureDiagnosticReport(_directory));
    }

    [Fact]
    public void RawFallsBackToNewestRawWhenNoCuratedReportExists()
    {
        string raw = Write("MediaFlux_Error_Only.raw.log", "raw", DateTime.UtcNow);

        Assert.Null(ErrorLogService.FindLatestFailureDiagnosticReport(_directory));
        Assert.Equal(raw, ErrorLogService.FindLatestRawFfmpegEvidence(_directory));
    }

    [Fact]
    public void RawDoesNotFallbackWhenNewestCuratedReportHasNoPair()
    {
        Write("MediaFlux_Error_Report.log", "report", DateTime.UtcNow);
        Write("MediaFlux_Error_Older.raw.log", "raw", DateTime.UtcNow.AddMinutes(-1));

        Assert.Null(ErrorLogService.FindLatestRawFfmpegEvidence(_directory));
    }

    [Fact]
    public void MissingArtifactsResolveWithoutThrowing()
    {
        Assert.Null(ErrorLogService.FindLatestFailureDiagnosticReport(Path.Combine(_directory, "missing")));
        Assert.Null(ErrorLogService.FindLatestRawFfmpegEvidence(Path.Combine(_directory, "missing")));
    }

    [Fact]
    public void NewerReportIsSelectedAfterRefreshResolution()
    {
        string first = Write("MediaFlux_Error_First.log", "first", DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(first, ErrorLogService.FindLatestFailureDiagnosticReport(_directory));
        string second = Write("MediaFlux_Error_Second.log", "second", DateTime.UtcNow);
        Assert.Equal(second, ErrorLogService.FindLatestFailureDiagnosticReport(_directory));
    }

    [Fact]
    public void EqualTimestampsUseDescendingFilenameTieBreak()
    {
        DateTime stamp = DateTime.UtcNow;
        string lower = Write("MediaFlux_Error_A.log", "A", stamp);
        string higher = Write("MediaFlux_Error_B.log", "B", stamp);

        Assert.NotEqual(lower, higher);
        Assert.Equal(higher, ErrorLogService.FindLatestFailureDiagnosticReport(_directory));
    }

    [Fact]
    public void DeletingCuratedArtifactDeletesItsRawPairAndLeavesOtherFailures()
    {
        string reportA = Write("MediaFlux_Error_A.log", "A", DateTime.UtcNow.AddMinutes(-2));
        string rawA = Write("MediaFlux_Error_A.raw.log", "raw A", DateTime.UtcNow.AddMinutes(-2));
        string reportB = Write("MediaFlux_Error_B.log", "B", DateTime.UtcNow);
        string rawB = Write("MediaFlux_Error_B.raw.log", "raw B", DateTime.UtcNow);

        Assert.True(ErrorLogService.TryDeleteFailureDiagnosticPair(reportB, _directory, out string? error));
        Assert.Null(error);
        Assert.False(File.Exists(reportB));
        Assert.False(File.Exists(rawB));
        Assert.True(File.Exists(reportA));
        Assert.True(File.Exists(rawA));
        Assert.Equal(reportA, ErrorLogService.FindLatestFailureDiagnosticReport(_directory));
        Assert.Equal(rawA, ErrorLogService.FindLatestRawFfmpegEvidence(_directory));
    }

    [Fact]
    public void DeletingRawArtifactDeletesItsCuratedPair()
    {
        string report = Write("MediaFlux_Error_B.log", "B", DateTime.UtcNow);
        string raw = Write("MediaFlux_Error_B.raw.log", "raw B", DateTime.UtcNow);

        Assert.True(ErrorLogService.TryDeleteFailureDiagnosticPair(raw, _directory, out string? error));
        Assert.Null(error);
        Assert.False(File.Exists(report));
        Assert.False(File.Exists(raw));
    }

    [Fact]
    public void DeletingWithMissingPairMemberRemovesRemainingArtifact()
    {
        string raw = Write("MediaFlux_Error_B.raw.log", "raw B", DateTime.UtcNow);

        Assert.True(ErrorLogService.TryDeleteFailureDiagnosticPair(raw, _directory, out string? error));
        Assert.Null(error);
        Assert.False(File.Exists(raw));
    }

    [Fact]
    public void DeletionRejectsPathsOutsideConfiguredDirectory()
    {
        string outside = Path.Combine(Path.GetTempPath(), "MediaFlux_Error_Outside.log");
        File.WriteAllText(outside, "must remain");
        try
        {
            Assert.False(ErrorLogService.TryDeleteFailureDiagnosticPair(outside, _directory, out string? error));
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    private string Write(string name, string text, DateTime lastWriteUtc)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
