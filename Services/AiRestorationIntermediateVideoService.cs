using System.Globalization;
using MediaFlux.Models;

namespace MediaFlux.Services;

public enum AiIntermediateStage { ExtractingFrames, AiProcessing, Reassembling, Validating }
public sealed record AiIntermediateProgress(AiIntermediateStage Stage, int Current, int Total, string Message);
public sealed record AiIntermediateVideoRequest(string SourcePath, double FrameRate, TimeSpan SourceDuration, VideoRestorationSettings Settings, VideoRestorationPipelinePlan Plan, TimeSpan? Start = null, TimeSpan? Duration = null, int SourceWidth = 0, int SourceHeight = 0);
public sealed record AiIntermediateVideoResult(string Path, TimeSpan Duration, int FrameCount, int Width, int Height, double FrameRate, string? StagingDirectory = null) : IDisposable
{ public void Dispose() { try { if (!string.IsNullOrWhiteSpace(StagingDirectory) && System.IO.Path.GetFileName(StagingDirectory).StartsWith("ai-intermediate-", StringComparison.OrdinalIgnoreCase) && Directory.Exists(StagingDirectory)) Directory.Delete(StagingDirectory, true); else if (File.Exists(Path)) File.Delete(Path); } catch { } } }

/// <summary>Creates a bounded, frame-based AI video intermediate. It deliberately owns no final encoder policy.</summary>
public sealed class AiRestorationIntermediateVideoService
{
    private readonly string _ffmpegPath, _ffprobePath, _stagingRoot;
    private readonly IMediaToolProcessRunner _runner;
    private readonly AiRestorationBackendService _backend;
    private readonly AiRestorationFrameProcessor _frames = new();
    public AiRestorationIntermediateVideoService(string ffmpegPath, string ffprobePath, string stagingRoot, AiRestorationBackendService backend, IMediaToolProcessRunner? runner = null)
    { _ffmpegPath = ffmpegPath; _ffprobePath = ffprobePath; _stagingRoot = stagingRoot; _backend = backend; _runner = runner ?? new MediaToolProcessRunner(); }

    public async Task<AiIntermediateVideoResult> CreateAsync(AiIntermediateVideoRequest request, IProgress<AiIntermediateProgress>? progress = null, CancellationToken token = default)
    {
        if (!File.Exists(request.SourcePath)) throw new FileNotFoundException("AI intermediate source is unavailable.", request.SourcePath);
        if (request.FrameRate is < 1 or > 240 || double.IsNaN(request.FrameRate)) throw new AiRestorationValidationException("AI intermediate processing requires a known constant frame rate between 1 and 240 fps; VFR sources are not supported yet.");
        if (!request.Plan.UsesAi) throw new AiRestorationValidationException("AI intermediate processing requires AI restoration to be enabled.");
        AiRestorationModel model = await _backend.ValidateSelectionAsync(request.Settings, token).ConfigureAwait(false);
        Directory.CreateDirectory(_stagingRoot); AiProductionHardeningService.CleanupOrphans(_stagingRoot); string root = Path.Combine(_stagingRoot, "ai-intermediate-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); AiProductionHardeningService.Register(root);
        try
        {
            TimeSpan duration = request.Duration ?? request.SourceDuration;
            if (duration <= TimeSpan.Zero) throw new AiRestorationValidationException("AI intermediate processing requires a known source duration.");
            int total = checked((int)Math.Round(duration.TotalSeconds * request.FrameRate, MidpointRounding.AwayFromZero));
            if (total <= 0) throw new AiRestorationValidationException("AI intermediate processing could not determine an expected frame count.");
            AiTemporaryStorageEstimate estimate = AiProductionHardeningService.Estimate(request.SourceWidth, request.SourceHeight, total, request.Settings.AiScale, _stagingRoot); AiProductionHardeningService.EnsureSpace(estimate); using (File.Create(Path.Combine(root, ".mediaflux-ai-staging"))) { }
            _ = model; // Model has been revalidated above; retain a single preflight boundary.
            var chunks = new List<string>();
            for (int offset = 0, chunkIndex = 0; offset < total; offset += AiRestorationFrameProcessor.MaximumFramesPerChunk, chunkIndex++)
            {
                int count = Math.Min(AiRestorationFrameProcessor.MaximumFramesPerChunk, total - offset);
                AiProductionHardeningService.EnsureSpace(AiProductionHardeningService.Estimate(request.SourceWidth, request.SourceHeight, count, request.Settings.AiScale, _stagingRoot), runtime: true);
                string chunk = Path.Combine(root, $"chunk-{chunkIndex:D5}"), input = Path.Combine(chunk, "input"), output = Path.Combine(chunk, "output"); Directory.CreateDirectory(input); Directory.CreateDirectory(output);
                progress?.Report(new(AiIntermediateStage.ExtractingFrames, offset, total, $"Extracting AI frames (chunk {chunkIndex + 1})"));
                await RunAsync(BuildExtractArguments(request, offset, count, input), "extract AI frames", token).ConfigureAwait(false);
                string[] extracted = ExpectedFrames(input, count); ValidateFrameSet(input, extracted);
                progress?.Report(new(AiIntermediateStage.AiProcessing, offset, total, $"AI restoring frames (chunk {chunkIndex + 1})"));
                await _frames.ProcessChunkAsync(extracted, output, (source, destination, ct) => _backend.ProcessFrameAsync(request.Settings, source, destination, ct), token).ConfigureAwait(false);
                string[] processed = ExpectedFrames(output, count); ValidateFrameSet(output, processed);
                string chunkVideo = Path.Combine(root, $"chunk-{chunkIndex:D5}.mkv");
                progress?.Report(new(AiIntermediateStage.Reassembling, offset + count, total, $"Reassembling AI frames (chunk {chunkIndex + 1})"));
                await RunAsync(BuildReassemblyArguments(processed[0], request.FrameRate, chunkVideo), "reassemble AI chunk", token).ConfigureAwait(false);
                chunks.Add(chunkVideo); Directory.Delete(chunk, true);
            }
            string list = Path.Combine(root, "chunks.ffconcat"); await File.WriteAllLinesAsync(list, chunks.Select(path => "file '" + path.Replace("'", "'\\''") + "'"), token).ConfigureAwait(false);
            string final = Path.Combine(root, "intermediate.mkv"), staging = final + ".staging";
            await RunAsync(new[] { "-y", "-f", "concat", "-safe", "0", "-i", list, "-map", "0:v:0", "-c", "copy", staging }, "join AI chunks", token).ConfigureAwait(false);
            AiIntermediateVideoResult result = await ValidateAsync(staging, duration, total, request.FrameRate, request.Settings.AiScale, token).ConfigureAwait(false);
            File.Move(staging, final, true); return result with { Path = final, StagingDirectory = root };
        }
        catch { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } throw; }
        finally { AiProductionHardeningService.Unregister(root); }
    }

    internal static string[] ExpectedFrames(string directory, int count) => Enumerable.Range(0, count).Select(index => Path.Combine(directory, $"frame-{index:D8}.png")).ToArray();
    internal static void ValidateFrameSet(string directory, IReadOnlyList<string> expected)
    { var actual = Directory.EnumerateFiles(directory, "frame-*.png").OrderBy(path => path, StringComparer.Ordinal).ToArray(); if (!actual.SequenceEqual(expected, StringComparer.Ordinal) || actual.Any(path => new FileInfo(path).Length < 64)) throw new AiRestorationValidationException("AI frame set is missing, incomplete, or contains unexpected frames."); }
    private static IReadOnlyList<string> BuildExtractArguments(AiIntermediateVideoRequest r, int offset, int count, string dir)
    { double seconds = (r.Start ?? TimeSpan.Zero).TotalSeconds + offset / r.FrameRate; return new[] { "-y", "-ss", seconds.ToString("0.######", CultureInfo.InvariantCulture), "-i", r.SourcePath, "-frames:v", count.ToString(), "-vf", string.IsNullOrWhiteSpace(r.Plan.PreAiFilterChain) ? "fps=" + r.FrameRate.ToString("0.######", CultureInfo.InvariantCulture) : r.Plan.PreAiFilterChain + ",fps=" + r.FrameRate.ToString("0.######", CultureInfo.InvariantCulture), "-start_number", "0", Path.Combine(dir, "frame-%08d.png") }; }
    private static IReadOnlyList<string> BuildReassemblyArguments(string first, double fps, string output) => new[] { "-y", "-framerate", fps.ToString("0.######", CultureInfo.InvariantCulture), "-start_number", "0", "-i", Path.Combine(Path.GetDirectoryName(first)!, "frame-%08d.png"), "-map", "0:v:0", "-c:v", "ffv1", "-level", "3", output };
    private async Task RunAsync(IReadOnlyList<string> args, string action, CancellationToken token) { MediaToolProcessResult r = await _runner.RunAsync(new MediaToolProcessRequest { FileName = _ffmpegPath, Arguments = args, Timeout = TimeSpan.FromMinutes(5) }, token).ConfigureAwait(false); if (r.ExitCode != 0 || r.TimedOut) throw new AiRestorationValidationException($"AI intermediate {action} failed."); }
    private async Task<AiIntermediateVideoResult> ValidateAsync(string path, TimeSpan expectedDuration, int expectedFrames, double fps, AiRestorationScale scale, CancellationToken token) { MediaToolProcessResult r = await _runner.RunAsync(new MediaToolProcessRequest { FileName = _ffprobePath, Arguments = new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,nb_frames,r_frame_rate:format=duration", "-of", "default=noprint_wrappers=1", path } }, token).ConfigureAwait(false); var map = r.StandardOutput.Split('\n').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase); if (r.ExitCode != 0 || !map.TryGetValue("width", out var w) || !map.TryGetValue("height", out var h) || !int.TryParse(w, out int width) || !int.TryParse(h, out int height)) throw new AiRestorationValidationException("AI intermediate validation failed: no readable video stream."); double duration = map.TryGetValue("duration", out var d) && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0; if (Math.Abs(duration - expectedDuration.TotalSeconds) > Math.Max(1, 2 / fps)) throw new AiRestorationValidationException($"AI intermediate validation failed: duration mismatch (expected {expectedDuration.TotalSeconds:0.###}, actual {duration:0.###})."); if (map.TryGetValue("nb_frames", out string? frames) && long.TryParse(frames, out long actualFrames) && Math.Abs(actualFrames - expectedFrames) > 1) throw new AiRestorationValidationException($"AI intermediate validation failed: frame-count mismatch (expected {expectedFrames}, actual {actualFrames})."); return new(path, TimeSpan.FromSeconds(duration), expectedFrames, width, height, fps); }
}
