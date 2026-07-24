using System.Text;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class ErrorLogServiceTests
{
    [Fact]
    public void ReadTail_BoundsLargeLogsAndKeepsNewestEntries()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                path,
                string.Join(Environment.NewLine, Enumerable.Range(0, 2_000).Select(i => $"entry-{i:D4}")),
                Encoding.UTF8);

            string text = ErrorLogService.ReadTail(path, 4 * 1024, out bool truncated);

            Assert.True(truncated);
            Assert.DoesNotContain("entry-0000", text);
            Assert.Contains("entry-1999", text);
            Assert.True(Encoding.UTF8.GetByteCount(text) <= 4 * 1024);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadTail_ReturnsFriendlyTextWhenLogDoesNotExist()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");

        string text = ErrorLogService.ReadTail(path, 4 * 1024, out bool truncated);

        Assert.False(truncated);
        Assert.Contains("No error log", text);
    }
}
