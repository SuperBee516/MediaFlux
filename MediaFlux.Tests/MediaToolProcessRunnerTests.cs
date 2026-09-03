using System.Text;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class MediaToolProcessRunnerTests
{
    [Theory]
    [InlineData("Life, Larry and the Pursuit of Unhappiness ｜ Episode 4 Preview ｜ HBO Max [HEVC].mp4")]
    [InlineData("Miyazaki 日本語 résumé Привет 😀.mp4")]
    public void ProcessStartInfoPreservesUnicodeArgumentsAndDecodesToolOutputAsUtf8(string fileName)
    {
        string source = Path.Combine(Path.GetTempPath(), fileName);
        var request = new MediaToolProcessRequest
        {
            FileName = "ffmpeg.exe",
            Arguments = new[] { "-i", source }
        };

        var startInfo = MediaToolProcessRunner.CreateStartInfo(request);

        Assert.Equal(source, startInfo.ArgumentList[1]);
        Assert.Equal(Encoding.UTF8.WebName, startInfo.StandardOutputEncoding?.WebName);
        Assert.Equal(Encoding.UTF8.WebName, startInfo.StandardErrorEncoding?.WebName);
    }

    [Fact]
    public async Task ProcessRunnerPassesEveryUnicodeCodeUnitToTheLaunchedProcess()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFluxProcessRunnerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string script = Path.Combine(root, "report-arguments.ps1");
        string source = Path.Combine(root, "Life ｜ 日本語 Привет résumé 😀.mp4");
        await File.WriteAllTextAsync(script, "param([string]$Value)\r\n[string]::Join(',', @($Value.ToCharArray() | ForEach-Object { [int][char]$_ }))\r\n", Encoding.UTF8);

        try
        {
            string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            Assert.True(File.Exists(shell), "Windows PowerShell is required for this Windows-only process-boundary test.");
            MediaToolProcessLaunchInfo? launch = null;

            MediaToolProcessResult result = await new MediaToolProcessRunner().RunAsync(new MediaToolProcessRequest
            {
                FileName = shell,
                Arguments = new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, source },
                ProcessStartedCallback = value => launch = value
            });

            string expected = string.Join(',', source.Select(character => ((int)character).ToString()));
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(expected, result.StandardOutput.Trim());
            Assert.NotNull(launch);
            Assert.Equal(source, launch.ArgumentList[^1]);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
