using System.Diagnostics;
using System.Text.Json;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LiveEncoderWorkflowTests
{
    private const string FfmpegEnvironmentVariable =
        "MEDIAFLUX_LIVE_FFMPEG_PATH";
    private const string FfprobeEnvironmentVariable =
        "MEDIAFLUX_LIVE_FFPROBE_PATH";
    private const string NvencEnvironmentVariable =
        "MEDIAFLUX_LIVE_NVENC";

    [Fact]
    public async Task RepresentativeProjectionTracksFullQualityEncodeWhenEnabled()
    {
        ToolPaths? tools = GetLiveToolPaths();
        if (tools == null)
            return;

        string root = CreateWorkingFolder();
        try
        {
            string source = await CreateSourceAsync(tools, root);
            VideoEncoderSelection encoder = EncoderRegistry.Default.Resolve(
                VideoEncoderIds.Libx265,
                VideoCodecFamily.Hevc).Selection;
            var sampleService = new SampleComparisonService(
                Path.GetDirectoryName(tools.FfmpegPath)!,
                tools.FfmpegPath,
                tools.FfprobePath);
            SampleProjectionResult projection = await sampleService.GenerateProjectionAsync(
                source,
                TimeSpan.FromSeconds(3),
                new SampleComparisonSettings
                {
                    Encoder = encoder,
                    UseGpu = false,
                    EncoderPreset = "ultrafast",
                    QualityValue = 30,
                    ClipSeconds = 8
                },
                progress: null,
                CancellationToken.None);

            var encodingService = new EncodingService(
                Path.GetDirectoryName(tools.FfmpegPath)!,
                _ => { },
                logCallback: null,
                ffmpegPath: tools.FfmpegPath,
                ffprobePath: tools.FfprobePath);
            EncodingService.EncodeResult actual = await encodingService.EncodeWithResultAsync(
                new EncodingRequest
                {
                    Input = EncodingInputSource.FromFile(source),
                    OutputFolder = root,
                    Suffix = "_projection_actual",
                    Encoder = encoder,
                    UseGpu = false,
                    EncoderPreset = "ultrafast",
                    QualityValue = 30,
                    CopySubtitles = false
                });

            Assert.True(actual.Success);
            double actualMb = new FileInfo(actual.OutputPath).Length / (1024d * 1024d);
            double errorPercent = Math.Abs(projection.ProjectedFinalMb - actualMb) /
                                  actualMb * 100;
            Assert.InRange(errorPercent, 0, 10);
            Assert.Single(SampleComparisonService.BuildSamplePositions(
                TimeSpan.FromSeconds(3),
                requestedClipSeconds: 8));
            Assert.False(projection.UsedDurationFallback);
        }
        finally
        {
            DeleteWorkingFolder(root);
        }
    }

    [Fact]
    public async Task Libx265RunsQualityAndTargetSizeWorkflowsWhenEnabled()
    {
        ToolPaths? tools = GetLiveToolPaths();
        if (tools == null)
            return;

        string root = CreateWorkingFolder();
        try
        {
            string source = await CreateSourceAsync(tools, root);
            var log = new List<string>();
            var service = new EncodingService(
                Path.GetDirectoryName(tools.FfmpegPath)!,
                _ => { },
                log.Add,
                tools.FfmpegPath,
                tools.FfprobePath);
            VideoEncoderSelection encoder = EncoderRegistry.Default.Resolve(
                VideoEncoderIds.Libx265,
                VideoCodecFamily.Hevc).Selection;

            EncodingService.EncodeResult qualityResult =
                await service.EncodeWithResultAsync(new EncodingRequest
                {
                    Input = EncodingInputSource.FromFile(source),
                    OutputFolder = root,
                    Suffix = "_x265_quality",
                    Encoder = encoder,
                    UseGpu = false,
                    EncoderPreset = "ultrafast",
                    QualityValue = 30,
                    CopySubtitles = true
                });

            Assert.True(
                qualityResult.Success,
                BuildFailureMessage(qualityResult, log));
            Assert.True(qualityResult.FinalizationSucceeded);
            Assert.True(File.Exists(qualityResult.OutputPath));
            Assert.False(File.Exists(qualityResult.StagingPath));
            Assert.EndsWith(
                ".mp4.partial",
                qualityResult.StagingPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "decode-integrity",
                qualityResult.ValidationSummary,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("-c:v libx265 ", qualityResult.DiagnosticArguments);
            Assert.Contains("-crf 30 -preset ultrafast ",
                qualityResult.DiagnosticArguments);
            Assert.Contains("-c:a copy ", qualityResult.DiagnosticArguments);
            Assert.Contains("-map_metadata 0 -map_chapters 0 ",
                qualityResult.DiagnosticArguments);
            Assert.Contains("-sn ", qualityResult.DiagnosticArguments);
            Assert.DoesNotContain("-hwaccel", qualityResult.DiagnosticArguments);

            using JsonDocument qualityProbe = await ProbeAsync(
                tools,
                qualityResult.OutputPath);
            AssertVideoStream(
                qualityProbe,
                codec: "hevc",
                pixelFormat: "yuv420p",
                width: 320,
                height: 180);
            AssertAudioStream(qualityProbe, codec: "aac", channels: 1);
            AssertNoSubtitleStreams(qualityProbe);
            AssertTitleMetadata(qualityProbe);

            EncodingService.EncodeResult autoContainerResult =
                await service.EncodeWithResultAsync(new EncodingRequest
                {
                    Input = EncodingInputSource.FromFile(source),
                    OutputFolder = root,
                    Suffix = "_x265_auto_container",
                    Encoder = encoder,
                    UseGpu = false,
                    EncoderPreset = "ultrafast",
                    QualityValue = 30,
                    CopySubtitles = true,
                    OutputContainer = OutputContainerSelection.Auto
                });
            Assert.Equal(OutputContainer.Matroska, autoContainerResult.ResolvedOutputContainer);
            Assert.EndsWith(".mkv", autoContainerResult.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("-f matroska", autoContainerResult.DiagnosticArguments);
            using JsonDocument autoProbe = await ProbeAsync(tools, autoContainerResult.OutputPath);
            Assert.Single(
                GetStreams(autoProbe),
                item => GetString(item, "codec_type") == "subtitle");

            EncodingService.EncodeResult forcedMkvResult =
                await service.EncodeWithResultAsync(new EncodingRequest
                {
                    Input = EncodingInputSource.FromFile(source),
                    OutputFolder = root,
                    Suffix = "_x265_forced_mkv",
                    Encoder = encoder,
                    UseGpu = false,
                    EncoderPreset = "ultrafast",
                    QualityValue = 30,
                    CopySubtitles = true,
                    OutputContainer = OutputContainerSelection.Matroska
                });
            Assert.Equal(OutputContainer.Matroska, forcedMkvResult.ResolvedOutputContainer);
            Assert.EndsWith(".mkv", forcedMkvResult.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(forcedMkvResult.FinalizationSucceeded);

            log.Clear();
            EncodingService.EncodeResult targetResult =
                await service.EncodeWithResultAsync(new EncodingRequest
                {
                    Input = EncodingInputSource.FromFile(source),
                    OutputFolder = root,
                    Suffix = "_x265_target_10bit",
                    Encoder = encoder,
                    UseGpu = false,
                    TargetMb = 1,
                    ScaleMode = EncodingService.ScaleMode.To720p,
                    EncoderPreset = "ultrafast",
                    QualityValue = 30,
                    TenBit = true,
                    AudioChannels = 2,
                    CopySubtitles = true
                });

            Assert.True(
                targetResult.Success,
                BuildFailureMessage(targetResult, log));
            Assert.True(targetResult.FinalizationSucceeded);
            Assert.True(File.Exists(targetResult.OutputPath));
            Assert.False(File.Exists(targetResult.StagingPath));
            Assert.Contains("-c:v libx265 ", targetResult.DiagnosticArguments);
            Assert.Contains("-profile:v main10 -pix_fmt yuv420p10le ",
                targetResult.DiagnosticArguments);
            Assert.Contains("-b:v ", targetResult.DiagnosticArguments);
            Assert.Contains("-maxrate ", targetResult.DiagnosticArguments);
            Assert.Contains("-bufsize ", targetResult.DiagnosticArguments);
            Assert.Contains("-preset ultrafast ",
                targetResult.DiagnosticArguments);
            Assert.DoesNotContain("-crf ", targetResult.DiagnosticArguments);
            Assert.Contains("-c:a aac -b:a 192k -ac 2 ",
                targetResult.DiagnosticArguments);
            Assert.DoesNotContain("cuda", targetResult.DiagnosticArguments);

            using JsonDocument targetProbe = await ProbeAsync(
                tools,
                targetResult.OutputPath);
            AssertVideoStream(
                targetProbe,
                codec: "hevc",
                pixelFormat: "yuv420p10le",
                width: 1280,
                height: 720);
            AssertAudioStream(targetProbe, codec: "aac", channels: 2);
            AssertNoSubtitleStreams(targetProbe);
            AssertTitleMetadata(targetProbe);
        }
        finally
        {
            DeleteWorkingFolder(root);
        }
    }

    [Fact]
    public async Task NvencHevcRunsThroughSharedWorkflowWhenEnabled()
    {
        ToolPaths? tools = GetLiveToolPaths();
        if (tools == null ||
            !string.Equals(
                Environment.GetEnvironmentVariable(
                    NvencEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        string root = CreateWorkingFolder();
        try
        {
            string source = await CreateSourceAsync(tools, root);
            var log = new List<string>();
            var service = new EncodingService(
                Path.GetDirectoryName(tools.FfmpegPath)!,
                _ => { },
                log.Add,
                tools.FfmpegPath,
                tools.FfprobePath);
            VideoEncoderSelection encoder = EncoderRegistry.Default.Resolve(
                VideoEncoderIds.Nvenc,
                VideoCodecFamily.Hevc).Selection;

            EncodingService.EncodeResult result =
                await service.EncodeWithResultAsync(new EncodingRequest
                {
                    Input = EncodingInputSource.FromFile(source),
                    OutputFolder = root,
                    Suffix = "_nvenc_quality",
                    Encoder = encoder,
                    UseGpu = true,
                    EncoderPreset = "p1",
                    QualityValue = 30,
                    CopySubtitles = true
                });

            Assert.True(result.Success, BuildFailureMessage(result, log));
            Assert.True(File.Exists(result.OutputPath));
            Assert.Contains("-c:v hevc_nvenc ", result.DiagnosticArguments);
            Assert.Contains("-preset p1 ", result.DiagnosticArguments);
            Assert.Contains("-map_metadata 0 -map_chapters 0 ",
                result.DiagnosticArguments);

            using JsonDocument probe = await ProbeAsync(
                tools,
                result.OutputPath);
            AssertVideoStream(
                probe,
                codec: "hevc",
                pixelFormat: "yuv420p",
                width: 320,
                height: 180);
            AssertAudioStream(probe, codec: "aac", channels: 1);
            AssertNoSubtitleStreams(probe);
            AssertTitleMetadata(probe);

            EncodingService.EncodeResult tenBitResult =
                await service.EncodeWithResultAsync(new EncodingRequest
                {
                    Input = EncodingInputSource.FromFile(source),
                    OutputFolder = root,
                    Suffix = "_nvenc_ten_bit",
                    Encoder = encoder,
                    UseGpu = true,
                    EncoderPreset = "p1",
                    QualityValue = 30,
                    TenBit = true,
                    CopySubtitles = true
                });

            Assert.True(
                tenBitResult.Success,
                BuildFailureMessage(tenBitResult, log));
            bool supportsHighBitDepthOutput =
                FfmpegEncoderCapabilityService.SupportsEncoderOption(
                    tools.FfmpegPath,
                    "hevc_nvenc",
                    "highbitdepth");
            if (supportsHighBitDepthOutput)
            {
                Assert.Contains(
                    "-hwaccel_output_format cuda ",
                    tenBitResult.DiagnosticArguments);
                Assert.Contains(
                    "-highbitdepth 1 ",
                    tenBitResult.DiagnosticArguments);
            }
            else
            {
                Assert.Contains(
                    "-vf format=p010le ",
                    tenBitResult.DiagnosticArguments);
            }

            using JsonDocument tenBitProbe = await ProbeAsync(
                tools,
                tenBitResult.OutputPath);
            AssertVideoStream(
                tenBitProbe,
                codec: "hevc",
                pixelFormat: "yuv420p10le",
                width: 320,
                height: 180);
            Assert.Contains(
                log,
                line => line.Contains(
                    "Video pipeline:",
                    StringComparison.Ordinal));
        }
        finally
        {
            DeleteWorkingFolder(root);
        }
    }

    private static ToolPaths? GetLiveToolPaths()
    {
        string? ffmpegPath =
            Environment.GetEnvironmentVariable(FfmpegEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(ffmpegPath) ||
            !File.Exists(ffmpegPath))
        {
            return null;
        }

        string? ffprobePath =
            Environment.GetEnvironmentVariable(FfprobeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(ffprobePath))
        {
            ffprobePath = Path.Combine(
                Path.GetDirectoryName(ffmpegPath)!,
                "ffprobe.exe");
        }

        Assert.True(
            File.Exists(ffprobePath),
            $"Live FFprobe executable was not found at '{ffprobePath}'.");
        return new ToolPaths(ffmpegPath, ffprobePath);
    }

    private static string CreateWorkingFolder()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-LiveEncoderTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<string> CreateSourceAsync(
        ToolPaths tools,
        string root)
    {
        string subtitlePath = Path.Combine(root, "subtitle.srt");
        await File.WriteAllTextAsync(
            subtitlePath,
            """
            1
            00:00:00,250 --> 00:00:02,500
            MediaFlux Phase 5 live encoder smoke test
            """);

        string sourcePath = Path.Combine(root, "source.mkv");
        ProcessResult result = await RunProcessAsync(
            tools.FfmpegPath,
            [
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-f", "lavfi",
                "-i", "testsrc2=size=320x180:rate=24",
                "-f", "lavfi",
                "-i", "sine=frequency=1000:sample_rate=48000",
                "-f", "srt",
                "-i", subtitlePath,
                "-t", "3",
                "-map", "0:v:0",
                "-map", "1:a:0",
                "-map", "2:s:0",
                "-c:v", "libx264",
                "-preset", "ultrafast",
                "-crf", "24",
                "-pix_fmt", "yuv420p",
                "-c:a", "aac",
                "-b:a", "96k",
                "-c:s", "srt",
                "-metadata", "title=MediaFlux Phase 5 Live Smoke",
                sourcePath
            ]);

        Assert.True(
            result.ExitCode == 0,
            $"Live source generation failed.{Environment.NewLine}" +
            result.StandardError);
        Assert.True(File.Exists(sourcePath));
        return sourcePath;
    }

    private static async Task<JsonDocument> ProbeAsync(
        ToolPaths tools,
        string path)
    {
        ProcessResult result = await RunProcessAsync(
            tools.FfprobePath,
            [
                "-v", "error",
                "-show_entries",
                "stream=codec_type,codec_name,pix_fmt,width,height,channels:" +
                "format_tags=title",
                "-of", "json",
                path
            ]);

        Assert.True(
            result.ExitCode == 0,
            $"Live output probe failed.{Environment.NewLine}" +
            result.StandardError);
        return JsonDocument.Parse(result.StandardOutput);
    }

    private static void AssertVideoStream(
        JsonDocument probe,
        string codec,
        string pixelFormat,
        int width,
        int height)
    {
        JsonElement stream = GetStreams(probe)
            .Single(item => GetString(item, "codec_type") == "video");
        Assert.Equal(codec, GetString(stream, "codec_name"));
        Assert.Equal(pixelFormat, GetString(stream, "pix_fmt"));
        Assert.Equal(width, stream.GetProperty("width").GetInt32());
        Assert.Equal(height, stream.GetProperty("height").GetInt32());
    }

    private static void AssertAudioStream(
        JsonDocument probe,
        string codec,
        int channels)
    {
        JsonElement stream = GetStreams(probe)
            .Single(item => GetString(item, "codec_type") == "audio");
        Assert.Equal(codec, GetString(stream, "codec_name"));
        Assert.Equal(channels, stream.GetProperty("channels").GetInt32());
    }

    private static void AssertNoSubtitleStreams(JsonDocument probe)
    {
        Assert.DoesNotContain(
            GetStreams(probe),
            item => GetString(item, "codec_type") == "subtitle");
    }

    private static void AssertTitleMetadata(JsonDocument probe)
    {
        string title = probe.RootElement
            .GetProperty("format")
            .GetProperty("tags")
            .GetProperty("title")
            .GetString()!;
        Assert.Equal("MediaFlux Phase 5 Live Smoke", title);
    }

    private static JsonElement[] GetStreams(JsonDocument probe) =>
        probe.RootElement
            .GetProperty("streams")
            .EnumerateArray()
            .ToArray();

    private static string? GetString(
        JsonElement element,
        string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value)
            ? value.GetString()
            : null;

    private static string BuildFailureMessage(
        EncodingService.EncodeResult result,
        IReadOnlyCollection<string> log) =>
        $"Encoding failed with arguments:{Environment.NewLine}" +
        $"{result.DiagnosticArguments}{Environment.NewLine}" +
        string.Join(Environment.NewLine, log);

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private static void DeleteWorkingFolder(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Keep failed test artifacts available if FFmpeg still owns a handle.
        }
    }

    private sealed record ToolPaths(
        string FfmpegPath,
        string FfprobePath);

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
