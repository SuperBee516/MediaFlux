namespace MediaFlux.Services;

/// <summary>
/// Bounded, deterministic coordinator for frame-based backends. The caller supplies a
/// bounded chunk (rather than an entire feature-length source); every frame is completed in
/// chronological order and incomplete output is rejected before reassembly can begin.
/// </summary>
public sealed class AiRestorationFrameProcessor
{
    public const int MaximumFramesPerChunk = 180;

    public async Task ProcessChunkAsync(
        IReadOnlyList<string> orderedInputFrames,
        string outputDirectory,
        Func<string, string, CancellationToken, Task> processFrameAsync,
        CancellationToken cancellationToken = default)
    {
        if (orderedInputFrames.Count == 0) throw new ArgumentException("AI restoration needs at least one frame.", nameof(orderedInputFrames));
        if (orderedInputFrames.Count > MaximumFramesPerChunk) throw new ArgumentException($"AI restoration chunks are limited to {MaximumFramesPerChunk} frames.", nameof(orderedInputFrames));
        if (!Path.IsPathFullyQualified(outputDirectory)) throw new ArgumentException("AI restoration needs an absolute staging directory.", nameof(outputDirectory));
        if (orderedInputFrames.Any(path => !Path.IsPathFullyQualified(path) || !File.Exists(path))) throw new FileNotFoundException("An AI restoration input frame is missing.");
        Directory.CreateDirectory(outputDirectory);
        try
        {
            for (int index = 0; index < orderedInputFrames.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string extension = Path.GetExtension(orderedInputFrames[index]);
                string output = Path.Combine(outputDirectory, $"frame-{index:D8}{extension}");
                await processFrameAsync(orderedInputFrames[index], output, cancellationToken).ConfigureAwait(false);
                if (!File.Exists(output) || new FileInfo(output).Length < 64)
                    throw new InvalidOperationException($"AI restoration produced an incomplete frame at index {index}.");
            }
        }
        catch
        {
            try { foreach (string output in Directory.EnumerateFiles(outputDirectory, "frame-*")) File.Delete(output); } catch { }
            throw;
        }
    }
}
