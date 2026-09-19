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

    [Fact]
    public async Task ProcessRunnerObservesStderrWithoutChangingRawCapture()
    {
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        MediaToolProcessResult result = await new MediaToolProcessRunner().RunAsync(new MediaToolProcessRequest
        {
            FileName = shell,
            Arguments = new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::Error.WriteLine('[h264 @ 000001d6df9649c0] Invalid NAL unit size (0 > 26098).')" }
        });

        Assert.Contains("Invalid NAL unit size (0 > 26098).", result.StandardError);
        FfmpegDiagnosticFamilySummary family = Assert.Single(result.DiagnosticSummary!.Families);
        Assert.Equal("Invalid NAL unit size", family.Family);
    }

    [Fact]
    public async Task CapturedFailureCanFlowIntoBoundedReportAndPairedArtifacts()
    {
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        MediaToolProcessResult result = await new MediaToolProcessRunner().RunAsync(new MediaToolProcessRequest
        {
            FileName = shell,
            Arguments = new[] { "-NoProfile", "-NonInteractive", "-Command", "1..120 | % { [Console]::Error.WriteLine(\"[h264 @ 000001d6df9649c0] Invalid NAL unit size (0 > $($_ + 26000)).\") }; exit 17" }
        });

        Assert.Equal(17, result.ExitCode);
        Assert.NotNull(result.DiagnosticSummary);
        Assert.Equal(120, result.DiagnosticSummary!.TotalEvents);
        string report = new FailureDiagnosticReportBuilder().Build(new FailureDiagnosticReportContext(
            "Encode", "source.mkv", "output.mkv", result.ExitCode, "FFmpeg process failure",
            result.DiagnosticSummary, result.StandardError));
        string directory = Path.Combine(Path.GetTempPath(), "MediaFlux-E2EReportTests", Guid.NewGuid().ToString("N"));
        try
        {
            FailureDiagnosticReportArtifact artifacts = Assert.IsType<FailureDiagnosticReportArtifact>(
                ErrorLogService.TryWriteFailureDiagnosticArtifacts("unused", report, result.StandardError, directory));
            Assert.Contains("120 occurrence(s)", report);
            Assert.True(report.Length < 20_000);
            Assert.Equal(result.StandardError, File.ReadAllText(artifacts.RawEvidencePath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
