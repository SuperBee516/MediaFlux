using System.Globalization;

namespace MediaFlux.Services;

/// <summary>Shared asynchronous FFmpeg still-frame extraction for review surfaces.</summary>
public sealed class VideoFramePreviewService
{
    private readonly string _ffmpegPath;
    private readonly IMediaToolProcessRunner _runner;
    private readonly string _cacheDirectory;

    public VideoFramePreviewService(string applicationDirectory, string? configuredFfmpegPath = null, IMediaToolProcessRunner? runner = null, string? cacheDirectory = null)
    {
        _ffmpegPath = FfmpegToolResolver.Resolve(applicationDirectory, configuredFfmpegPath).FfmpegPath;
        _runner = runner ?? new MediaToolProcessRunner();
        _cacheDirectory = cacheDirectory ?? AppPaths.FramePreviewsDirectory;
    }

    public async Task<Image?> ExtractAsync(string sourcePath, double seconds, double sourceDurationSeconds, double sourceFrameRate = 0, int width = 320, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_ffmpegPath) || !File.Exists(sourcePath) || sourceDurationSeconds <= 0) return null;
        Directory.CreateDirectory(_cacheDirectory);
        string imagePath = Path.Combine(_cacheDirectory, $"{Guid.NewGuid():N}.jpg");
        double frameInterval = sourceFrameRate > 0 ? 1d / sourceFrameRate : 1d / 30d;
        double previewSeconds = Math.Clamp(seconds, 0, Math.Max(0, sourceDurationSeconds - frameInterval));
        try
        {
            MediaToolProcessResult result = await _runner.RunAsync(new MediaToolProcessRequest
            {
                FileName = _ffmpegPath,
                Arguments = new[] { "-hide_banner", "-loglevel", "error", "-ss", previewSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", sourcePath, "-frames:v", "1", "-vf", $"scale={Math.Max(64, width)}:-2", "-q:v", "3", "-y", imagePath },
                Timeout = TimeSpan.FromSeconds(30)
            }, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(imagePath)) return null;
            using Image image = Image.FromFile(imagePath);
            return new Bitmap(image);
        }
        finally { try { if (File.Exists(imagePath)) File.Delete(imagePath); } catch { } }
    }
}
