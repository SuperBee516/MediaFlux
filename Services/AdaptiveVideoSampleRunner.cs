using System.Diagnostics;
using System.Globalization;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Owns bounded direct-source video samples, processes and temporary artifacts.</summary>
internal sealed class AdaptiveVideoSampleRunner : IAdaptiveVideoSampleRunner
{
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly IMediaProbeService _probe;
    private readonly IMediaToolProcessRunner _packetRunner;
    private readonly Func<AdaptiveQualitySelectionRequest, int, RepresentativeSample, string, string> _arguments;
    private readonly Func<string, string, CancellationToken, Task>? _runOverride;
    private readonly string _root;

    public AdaptiveVideoSampleRunner(string ffmpeg, string ffprobe, IMediaProbeService probe,
        Func<AdaptiveQualitySelectionRequest, int, RepresentativeSample, string, string> arguments,
        IMediaToolProcessRunner? packetRunner = null,
        Func<string, string, CancellationToken, Task>? runOverride = null, string? temporaryRoot = null)
    {
        _ffmpeg = ffmpeg;
        _ffprobe = ffprobe;
        _probe = probe;
        _arguments = arguments;
        _packetRunner = packetRunner ?? new MediaToolProcessRunner();
        _runOverride = runOverride;
        _root = temporaryRoot ?? Path.Combine(Path.GetTempPath(), "MediaFlux", "AdaptiveSamples");
    }

    public async Task<RepresentativeSampleEvidence> MeasureAsync(AdaptiveQualitySelectionRequest request, int quality,
        RepresentativeSample sample, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "video" + (request.Video.Container == OutputContainer.Matroska ? ".mkv" : ".mp4"));
        try
        {
            string arguments = _arguments(request, quality, sample, output);
            if (_runOverride is not null) await _runOverride(output, arguments, cancellationToken).ConfigureAwait(false);
            else await RunProcessAsync(_ffmpeg, arguments, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            MediaProbeResult probe = await _probe.ProbeAsync(output, cancellationToken).ConfigureAwait(false);
            MediaProbeStreamInfo? video = probe.Streams.SingleOrDefault(s => s.CodecType == "video");
            if (!probe.Success || video is null || probe.Streams.Any(s => s.CodecType != "video") ||
                video.Width != request.Video.Geometry.Width || video.Height != request.Video.Geometry.Height ||
                !video.CodecName.Equals("hevc", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(video.PixelFormat) ||
                !video.PixelFormat.Equals(request.Video.TenBit ? "yuv420p10le" : "yuv420p", StringComparison.OrdinalIgnoreCase) ||
                !PreservesKnownColor(request.Video.SourceColorRange, video.ColorRange) ||
                !PreservesKnownColor(request.Video.SourceColorSpace, video.ColorSpace) ||
                !PreservesKnownColor(request.Video.SourceColorTransfer, video.ColorTransfer) ||
                !PreservesKnownColor(request.Video.SourceColorPrimaries, video.ColorPrimaries) ||
                (video.DurationSeconds ?? probe.DurationSeconds) is not > 0)
                throw new InvalidDataException("Adaptive video sample did not preserve planned geometry, bit depth, known color signaling or usable duration.");
            MediaToolProcessResult packets = await _packetRunner.RunAsync(new MediaToolProcessRequest
            {
                FileName = _ffprobe,
                Arguments = ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=size", "-of", "csv=p=0", output],
                Timeout = Timeout.InfiniteTimeSpan,
                DiagnosticComponent = FfmpegDiagnosticComponent.Ffprobe
            }, cancellationToken).ConfigureAwait(false);
            if (packets.ExitCode != 0 || packets.TimedOut || packets.StandardOutput.Contains("[Additional"))
                throw new InvalidDataException("Adaptive sample packet-byte measurement failed or was truncated.");
            long bytes = 0;
            foreach (string line in packets.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string size = line.Split(',')[0].Trim();
                if (size.Length == 0) continue; // optional packet side-data separator
                if (!long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out long packetBytes) || packetBytes <= 0)
                    throw new InvalidDataException("Adaptive sample contains invalid video packet sizes.");
                bytes = checked(bytes + packetBytes);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes <= 0) throw new InvalidDataException("Adaptive sample contains no measured video packets.");
            return new(sample, bytes, video.DurationSeconds ?? probe.DurationSeconds!.Value);
        }
        finally
        {
            // All runner calls complete (including kill/drain) before ownership ends.
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool PreservesKnownColor(string source, string produced) =>
        string.IsNullOrWhiteSpace(source) || source.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
        source.Equals("unspecified", StringComparison.OrdinalIgnoreCase) || source.Equals(produced, StringComparison.OrdinalIgnoreCase);

    internal static async Task RunProcessAsync(string executable, string arguments, CancellationToken cancellationToken,
        Action<int>? processStarted = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = executable, Arguments = arguments, UseShellExecute = false,
            RedirectStandardError = true, RedirectStandardOutput = true,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        } };
        process.Start();
        // FFmpeg emits no progress records here; loglevel error bounds diagnostics.
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        try
        {
            processStarted?.Invoke(process.Id);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Adaptive sample FFmpeg failed ({process.ExitCode}): {await stderr.ConfigureAwait(false)}");
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            throw;
        }
    }
}
