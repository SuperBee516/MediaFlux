using System.Globalization;
using MediaFlux.Models;

namespace MediaFlux.Services;
public sealed record CorrelatedFrameExtractionRequest(string SourcePath, string OutputDirectory, string SourceTimeBase, string PreAiFilterChain, TimeSpan? Start, int FrameCount);
public sealed record CorrelatedFrameExtractionDiagnostics(int ImageCount, IReadOnlyList<int> TimingRecordIndexes, IReadOnlyList<int> MissingTimingIndexes, IReadOnlyList<int> UnexpectedTimingIndexes)
{
    public string Describe() => $"images={ImageCount}, timingRecords={TimingRecordIndexes.Count}, missingTimingIndexes=[{string.Join(',', MissingTimingIndexes)}], unexpectedTimingIndexes=[{string.Join(',', UnexpectedTimingIndexes)}]";
}
public sealed record CorrelatedFrameExtractionResult(IReadOnlyList<string> Frames, IReadOnlyList<FfmpegShowInfoFrame> TimingRecords, AiTimestampManifest? Manifest, CorrelatedFrameExtractionDiagnostics? Diagnostics = null);
/// <summary>Single-pass FFmpeg image extraction and showinfo correlation. Not wired to VFR AI processing yet.</summary>
public sealed class CorrelatedFrameExtractionService
{
    private readonly string _ffmpegPath; private readonly IMediaToolProcessRunner _runner;
    public CorrelatedFrameExtractionService(string ffmpegPath, IMediaToolProcessRunner? runner = null) { _ffmpegPath = ffmpegPath; _runner = runner ?? new MediaToolProcessRunner(); }
    public async Task<CorrelatedFrameExtractionResult> ExtractAsync(CorrelatedFrameExtractionRequest request, CancellationToken token = default)
    {
        if (request.FrameCount < 1) throw new ArgumentException("A bounded requested frame count is required."); Directory.CreateDirectory(request.OutputDirectory); var parser = new FfmpegShowInfoParser(); string pattern = Path.Combine(request.OutputDirectory, "frame-%08d.png"); string filter = string.IsNullOrWhiteSpace(request.PreAiFilterChain) ? "showinfo" : request.PreAiFilterChain + ",showinfo";
        var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "info", "-y" }; if (request.Start is { } start) { args.Add("-ss"); args.Add(start.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture)); } args.AddRange(new[] { "-i", request.SourcePath, "-vf", filter, "-frames:v", (request.FrameCount + 1).ToString(CultureInfo.InvariantCulture), "-start_number", "0", pattern });
        MediaToolProcessResult result = await _runner.RunAsync(new MediaToolProcessRequest { FileName = _ffmpegPath, Arguments = args, Timeout = TimeSpan.FromMinutes(2), StandardErrorLineCallback = parser.Consume }, token).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.TimedOut) throw new AiRestorationValidationException("Correlated frame extraction failed; see MediaFlux diagnostics.");
        parser = new FfmpegShowInfoParser(); foreach (string line in result.StandardError.Split('\n')) parser.Consume(line);
        string[] all = Directory.EnumerateFiles(request.OutputDirectory, "frame-*.png").OrderBy(x => x, StringComparer.Ordinal).ToArray(); CorrelatedFrameExtractionDiagnostics diagnostics = Diagnose(all.Length, parser.Frames.Select(frame => frame.Index)); if (all.Length < request.FrameCount + 1 || parser.Frames.Count < request.FrameCount + 1) throw new AiRestorationValidationException($"Correlated extraction could not prove the final-frame duration using a lookahead frame (required={request.FrameCount + 1}; {diagnostics.Describe()}).");
        string[] frames = all.Take(request.FrameCount).ToArray(); FfmpegShowInfoFrame[] records = parser.Frames.Take(request.FrameCount + 1).ToArray(); Validate(frames, records);
        var entries = records.Take(request.FrameCount).Select((record, index) => new AiFrameTimingEntry(index, record.Pts, record.PtsTime, records[index + 1].PtsTime - record.PtsTime, request.SourceTimeBase, Path.GetFileName(frames[index]), Path.GetFileName(frames[index]))).ToArray(); var manifest = new AiTimestampManifest(request.SourceTimeBase, entries); AiTimestampValidationResult valid = AiTimestampManifestService.Validate(manifest); if (!valid.IsValid) throw new AiRestorationValidationException(valid.Reason); try { File.Delete(all[^1]); } catch { } return new(frames, records, manifest, diagnostics);
    }
    internal static void Validate(IReadOnlyList<string> frames, IReadOnlyList<FfmpegShowInfoFrame> records) { if (frames.Count + 1 != records.Count || frames.Select((path, index) => Path.GetFileName(path) == $"frame-{index:D8}.png").Any(ok => !ok) || records.Select((record, index) => record.Index == index).Any(ok => !ok)) throw new AiRestorationValidationException("Extracted image files and showinfo records do not have a strict one-to-one order."); }
    internal static CorrelatedFrameExtractionDiagnostics Diagnose(int imageCount, IEnumerable<int> timingIndexes)
    {
        int[] indexes = timingIndexes.Distinct().OrderBy(index => index).ToArray();
        int[] missing = Enumerable.Range(0, imageCount).Except(indexes).ToArray();
        int[] unexpected = indexes.Where(index => index < 0 || index >= imageCount).ToArray();
        return new(imageCount, indexes, missing, unexpected);
    }
}
