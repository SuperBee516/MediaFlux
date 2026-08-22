using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class VideoSplitterExportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-SplitterExport", Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _ffmpeg;

    public VideoSplitterExportServiceTests()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "source.mp4");
        _ffmpeg = Path.Combine(_root, "ffmpeg.exe");
        File.WriteAllText(_source, "source");
        File.WriteAllText(_ffmpeg, "tool");
    }

    [Fact]
    public void StreamCopyCommandMapsStreamsAndUsesCopy()
    {
        IReadOnlyList<string> args = VideoSplitterExportService.BuildStreamCopyArguments("in.mkv", "out.mkv", 12.5, 8);
        Assert.Contains("-c", args); Assert.Contains("copy", args);
        Assert.Contains("0:v?", args); Assert.Contains("0:a?", args); Assert.Contains("0:s?", args);
        Assert.Equal("12.5", args[args.ToList().IndexOf("-ss") + 1]);
    }

    [Fact]
    public void AccurateCutCommandPlacesSeekAfterInputAndUsesConfiguredEncoder()
    {
        IReadOnlyList<string> args = VideoSplitterExportService.BuildAccurateReencodeArguments("in.mp4", "out.mp4", 3, 4, "h264_nvenc", "p5", 22);
        Assert.True(args.ToList().IndexOf("-i") < args.ToList().IndexOf("-ss"));
        Assert.Contains("h264_nvenc", args); Assert.Contains("-cq", args); Assert.DoesNotContain("-crf", args);
        Assert.Contains("aac", args);
    }

    [Fact]
    public void ValidationRejectsDuplicatesAndSourceReplacement()
    {
        var request = Request(_root, new[]
        {
            new VideoSplitterSegment(1, 0, 2, Path.GetFileName(_source)),
            new VideoSplitterSegment(2, 3, 5, Path.GetFileName(_source))
        });
        IReadOnlyList<string> errors = VideoSplitterExportService.Validate(request);
        Assert.Contains(errors, error => error.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("source", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FailureRemovesStagingFileAndDoesNotCreateFinalOutput()
    {
        var runner = new ScriptedRunner((request, _) =>
        {
            File.WriteAllText(request.Arguments.Last(), "partial");
            return Task.FromResult(new MediaToolProcessResult { ExitCode = 1, StandardError = "synthetic failure" });
        });
        var service = new VideoSplitterExportService(_ffmpeg, runner, new SuccessfulProbe());
        VideoSplitterExportResult result = await service.ExportAsync(Request(_root, new[] { new VideoSplitterSegment(1, 0, 2, "clip.mp4") }));
        VideoSplitterSegmentExportResult segment = Assert.Single(result.Segments);
        Assert.False(segment.Success); Assert.False(File.Exists(Path.Combine(_root, "clip.mp4")));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.partial*"));
    }

    [Fact]
    public async Task AccurateCutPromotesValidatedStagingOutput()
    {
        var runner = new ScriptedRunner((request, _) =>
        {
            Assert.Contains("-crf", request.Arguments);
            File.WriteAllText(request.Arguments.Last(), "encoded");
            return Task.FromResult(new MediaToolProcessResult { ExitCode = 0 });
        });
        var service = new VideoSplitterExportService(_ffmpeg, runner, new SuccessfulProbe());
        VideoSplitterExportRequest request = Request(_root, new[] { new VideoSplitterSegment(1, 0, 2, "accurate.mp4") }, VideoSplitterProcessingMode.AccurateReencode);
        VideoSplitterExportResult result = await service.ExportAsync(request);
        Assert.True(result.Success); Assert.True(File.Exists(Path.Combine(_root, "accurate.mp4")));
    }

    [Fact]
    public async Task CancellationRemovesStagingFileAndKeepsPriorSuccess()
    {
        int calls = 0;
        var runner = new ScriptedRunner((request, token) =>
        {
            File.WriteAllText(request.Arguments.Last(), "partial");
            calls++;
            if (calls == 2) throw new OperationCanceledException(token);
            return Task.FromResult(new MediaToolProcessResult { ExitCode = 0 });
        });
        var service = new VideoSplitterExportService(_ffmpeg, runner, new SuccessfulProbe());
        VideoSplitterExportResult result = await service.ExportAsync(Request(_root, new[] { new VideoSplitterSegment(1, 0, 2, "one.mp4"), new VideoSplitterSegment(2, 3, 5, "two.mp4") }));
        Assert.True(result.WasCanceled); Assert.True(result.Segments[0].Success); Assert.True(File.Exists(Path.Combine(_root, "one.mp4")));
        Assert.True(result.Segments[1].WasCanceled); Assert.Empty(Directory.EnumerateFiles(_root, "*.partial*"));
    }

    private VideoSplitterExportRequest Request(string folder, IReadOnlyList<VideoSplitterSegment> segments, VideoSplitterProcessingMode mode = VideoSplitterProcessingMode.StreamCopy) => new() { SourcePath = _source, OutputFolder = folder, Segments = segments, SourceDurationSeconds = 10, Mode = mode };
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class SuccessfulProbe : IMediaProbeService
    {
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new MediaProbeResult { Success = true, DurationSeconds = 2, Streams = new[] { new MediaProbeStreamInfo { CodecType = "video", CodecName = "h264" } } });
    }
    private sealed class ScriptedRunner(Func<MediaToolProcessRequest, CancellationToken, Task<MediaToolProcessResult>> run) : IMediaToolProcessRunner
    {
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default) => run(request, cancellationToken);
    }
}
