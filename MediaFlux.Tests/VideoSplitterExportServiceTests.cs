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
        Assert.Contains("0:V?", args); Assert.Contains("0:a?", args); Assert.Contains("0:s?", args);
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
    public void AccurateCutMapsMjpegAttachedArtworkByCopyAndOnlyEncodesPlayableVideo()
    {
        MediaProbeResult source = SourceWithArtwork();
        VideoSplitterExportService.StreamMappingPlan mapping = VideoSplitterExportService.CreateStreamMappingPlan(source, "out.mp4");

        IReadOnlyList<string> args = VideoSplitterExportService.BuildAccurateReencodeArguments("in.mp4", "out.mp4", 3, 4, "hevc_nvenc", "p5", 22, mapping);

        Assert.Equal(new[] { 0 }, mapping.PlayableVideoStreamIndexes);
        Assert.Equal(new[] { 2 }, mapping.AttachedPictureStreamIndexes);
        Assert.Contains("0:0", args);
        Assert.Contains("0:2", args);
        Assert.Contains("hevc_nvenc", args);
        Assert.Contains("-c:v:1", args);
        Assert.Equal("copy", args[args.ToList().IndexOf("-c:v:1") + 1]);
        Assert.Contains("-disposition:v:1", args);
    }

    [Fact]
    public void OrdinaryMp4MapsOnlyItsPrimaryVideo()
    {
        var source = new MediaProbeResult
        {
            Success = true,
            Streams = new[]
            {
                new MediaProbeStreamInfo { Index = 0, CodecType = "video", CodecName = "h264" },
                new MediaProbeStreamInfo { Index = 1, CodecType = "audio", CodecName = "aac" }
            }
        };

        VideoSplitterExportService.StreamMappingPlan mapping = VideoSplitterExportService.CreateStreamMappingPlan(source, "out.mp4");
        IReadOnlyList<string> args = VideoSplitterExportService.BuildAccurateReencodeArguments("in.mp4", "out.mp4", 3, 4, "libx264", "medium", 22, mapping);

        Assert.Equal(new[] { 0 }, mapping.PlayableVideoStreamIndexes);
        Assert.Empty(mapping.AttachedPictureStreamIndexes);
        Assert.Contains("0:0", args);
        Assert.DoesNotContain("-c:v:1", args);
    }

    [Fact]
    public void StreamCopyMapsCompatibleAttachedArtworkWithoutUsingVideoEncoder()
    {
        VideoSplitterExportService.StreamMappingPlan mapping = VideoSplitterExportService.CreateStreamMappingPlan(SourceWithArtwork(), "out.mp4");

        IReadOnlyList<string> args = VideoSplitterExportService.BuildStreamCopyArguments("in.mp4", "out.mp4", 0, 2, mapping);

        Assert.Contains("0:0", args);
        Assert.Contains("0:2", args);
        Assert.Contains("copy", args);
        Assert.DoesNotContain("-c:v", args);
    }

    [Fact]
    public void StreamCopyOmitsAttachedArtworkForAnIncompatibleContainer()
    {
        VideoSplitterExportService.StreamMappingPlan mapping = VideoSplitterExportService.CreateStreamMappingPlan(SourceWithArtwork(), "out.avi");
        IReadOnlyList<string> args = VideoSplitterExportService.BuildStreamCopyArguments("in.mp4", "out.avi", 0, 2, mapping);

        Assert.Empty(mapping.AttachedPictureStreamIndexes);
        Assert.Contains("0:0", args);
        Assert.DoesNotContain("0:2", args);
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
    public async Task AccurateCutWithAttachedArtworkProducesValidatedOutput()
    {
        var runner = new ScriptedRunner((request, _) =>
        {
            Assert.Contains("0:0", request.Arguments);
            Assert.Contains("0:2", request.Arguments);
            Assert.Equal("copy", request.Arguments[request.Arguments.ToList().IndexOf("-c:v:1") + 1]);
            File.WriteAllText(request.Arguments.Last(), "encoded");
            return Task.FromResult(new MediaToolProcessResult { ExitCode = 0 });
        });
        var service = new VideoSplitterExportService(_ffmpeg, runner, new SourceAndOutputProbe(_source, SourceWithArtwork()));

        VideoSplitterExportResult result = await service.ExportAsync(Request(
            _root,
            new[] { new VideoSplitterSegment(1, 0, 2, "artwork.mp4") },
            VideoSplitterProcessingMode.AccurateReencode));

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_root, "artwork.mp4")));
    }

    [Fact]
    public async Task ExplicitOverwriteReplacesExistingValidatedOutput()
    {
        string output = Path.Combine(_root, "replace.mp4");
        File.WriteAllText(output, "old");
        var runner = new ScriptedRunner((request, _) =>
        {
            File.WriteAllText(request.Arguments.Last(), "new");
            return Task.FromResult(new MediaToolProcessResult { ExitCode = 0 });
        });
        var service = new VideoSplitterExportService(_ffmpeg, runner, new SuccessfulProbe());

        VideoSplitterExportResult result = await service.ExportAsync(Request(
            _root,
            new[] { new VideoSplitterSegment(1, 0, 2, "replace.mp4") },
            overwrite: true));

        Assert.True(result.Success);
        Assert.Equal("new", File.ReadAllText(output));
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

    private VideoSplitterExportRequest Request(string folder, IReadOnlyList<VideoSplitterSegment> segments, VideoSplitterProcessingMode mode = VideoSplitterProcessingMode.StreamCopy, bool overwrite = false) => new() { SourcePath = _source, OutputFolder = folder, Segments = segments, SourceDurationSeconds = 10, Mode = mode, OverwriteExistingOutput = overwrite };
    private static MediaProbeResult SourceWithArtwork() => new()
    {
        Success = true,
        DurationSeconds = 10,
        Streams = new[]
        {
            new MediaProbeStreamInfo { Index = 0, CodecType = "video", CodecName = "h264" },
            new MediaProbeStreamInfo { Index = 1, CodecType = "audio", CodecName = "aac" },
            new MediaProbeStreamInfo
            {
                Index = 2,
                CodecType = "video",
                CodecName = "mjpeg",
                Dispositions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["attached_pic"] = true }
            }
        }
    };
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class SuccessfulProbe : IMediaProbeService
    {
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new MediaProbeResult { Success = true, DurationSeconds = 2, Streams = new[] { new MediaProbeStreamInfo { CodecType = "video", CodecName = "h264" } } });
    }
    private sealed class SourceAndOutputProbe(string sourcePath, MediaProbeResult sourceProbe) : IMediaProbeService
    {
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(path.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)
                ? sourceProbe
                : new MediaProbeResult
                {
                    Success = true,
                    DurationSeconds = 2,
                    Streams = new[]
                    {
                        new MediaProbeStreamInfo { Index = 0, CodecType = "video", CodecName = "hevc" },
                        new MediaProbeStreamInfo { Index = 1, CodecType = "audio", CodecName = "aac" },
                        new MediaProbeStreamInfo
                        {
                            Index = 2,
                            CodecType = "video",
                            CodecName = "mjpeg",
                            Dispositions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["attached_pic"] = true }
                        }
                    }
                });
    }
    private sealed class ScriptedRunner(Func<MediaToolProcessRequest, CancellationToken, Task<MediaToolProcessResult>> run) : IMediaToolProcessRunner
    {
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default) => run(request, cancellationToken);
    }
}
