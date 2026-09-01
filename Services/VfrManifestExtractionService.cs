using MediaFlux.Models;

namespace MediaFlux.Services;
public sealed record VfrManifestExtractionRequest(string SourcePath, string StagingDirectory, string SourceTimeBase, SourceTimingAnalysis Timing, string PreAiFilterChain, TimeSpan? Start, int FrameCount);
public sealed class VfrManifestExtractionResult : IDisposable
{
    public VfrManifestExtractionResult(string stagingDirectory, CorrelatedFrameExtractionResult extraction, TimeSpan? requestedStart) { StagingDirectory = stagingDirectory; Extraction = extraction; RequestedStart = requestedStart; }
    public string StagingDirectory { get; } public CorrelatedFrameExtractionResult Extraction { get; } public TimeSpan? RequestedStart { get; }
    public AiTimestampManifest Manifest => Extraction.Manifest!; public IReadOnlyList<string> Frames => Extraction.Frames;
    public IReadOnlyList<AiFrameTimingEntry> Chunk(int offset) => Manifest.Frames.Skip(offset).Take(AiRestorationFrameProcessor.MaximumFramesPerChunk).ToArray();
    public void Dispose() { try { if (Directory.Exists(StagingDirectory) && Path.GetFileName(StagingDirectory).StartsWith("ai-vfr-extraction-", StringComparison.OrdinalIgnoreCase)) Directory.Delete(StagingDirectory, true); } catch { } }
}
/// <summary>Future-only manifest investigation utility. It is not an AI, preview, or encoding activation path.</summary>
public sealed class VfrManifestExtractionService
{
    private readonly CorrelatedFrameExtractionService _extractor; private readonly Action<string>? _log;
    public VfrManifestExtractionService(CorrelatedFrameExtractionService extractor, Action<string>? log = null) { _extractor = extractor; _log = log; }
    public async Task<VfrManifestExtractionResult> ExtractAsync(VfrManifestExtractionRequest request, CancellationToken token = default)
    {
        if (request.Timing.Classification != SourceTimingClassification.Vfr || request.Timing.AiEligibility != AiTimingEligibility.PotentialFutureTimestampAware) throw new AiRestorationValidationException("Timestamp-preserving extraction requires verified monotonic VFR timing evidence.");
        string root = Path.Combine(request.StagingDirectory, "ai-vfr-extraction-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            CorrelatedFrameExtractionResult extraction = await _extractor.ExtractAsync(new(request.SourcePath, root, request.SourceTimeBase, request.PreAiFilterChain, request.Start, request.FrameCount), token).ConfigureAwait(false);
            AiTimestampValidationResult valid = AiTimestampManifestService.Validate(extraction.Manifest!, extraction.Frames.Select(path => Path.GetFileName(path)!).ToArray()); if (!valid.IsValid) throw new AiRestorationValidationException(valid.Reason);
            _log?.Invoke($"[VFR Extraction] future-only diagnostic; range={request.Start}; frames={extraction.Frames.Count}; pts={extraction.Manifest!.Frames[0].PresentationSeconds:0.######}-{extraction.Manifest.Frames[^1].PresentationSeconds:0.######}; lookahead=verified.");
            return new(root, extraction, request.Start);
        }
        catch { try { Directory.Delete(root, true); } catch { } throw; }
    }
}
