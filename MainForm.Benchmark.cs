using MediaFlux.Models;
using MediaFlux.Services;
using System.Diagnostics;

namespace MediaFlux;

public partial class MainForm
{
    private void BenchmarkEncodePerformance_Click(object? sender, EventArgs e)
    {
        if (!CanStartEncoderBenchmark(_encodingActive, _mediaRemuxCts != null, _sampleComparisonCts != null, out string conflict))
        {
            ShowStatusInfo(conflict);
            return;
        }
        if (!EnsureFfmpegToolsAvailable() || !EnsureSelectedVideoEncoderAvailable()) return;
        DataGridViewRow? row = dgvEncodeQueue.CurrentRow;
        if (row == null || row.IsNewRow || row.Tag is RowMeta { IsDvdEncode: true })
        {
            ShowStatusInfo("Select one regular media file in the encode queue before benchmarking.");
            return;
        }
        string sourcePath = row.Tag is RowMeta meta ? meta.Path : row.Tag as string ?? "";
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            ShowStatusInfo("The selected benchmark source is unavailable.");
            return;
        }
        double durationSeconds = (row.Tag as RowMeta)?.DurationSec ?? 0;
        if (durationSeconds <= 0) durationSeconds = _mediaInfoService.GetDurationSeconds(sourcePath);
        if (durationSeconds <= 0)
        {
            ShowStatusInfo("MediaFlux could not determine the selected video's duration.");
            return;
        }
        SampleComparisonSettings current = BuildSampleComparisonSettings(row, sourcePath, durationSeconds);
        MediaInfoService.MediaInfo media = _mediaInfoService.GetInfo(sourcePath);
        EncoderCapabilities capabilities = GetSelectedEncoderCapabilities();
        VideoEncoderSelection encoder = current.Encoder ?? GetSelectedVideoEncoderSelection();
        IReadOnlyList<EncoderPresetOption> presets = capabilities.Presets.Count > 0
            ? capabilities.Presets
            : new[] { new EncoderPresetOption(current.EncoderPreset, current.EncoderPreset) };
        IReadOnlyList<int> concurrency = capabilities.SupportsConcurrentJobs &&
                                         capabilities.Id.Equals(VideoEncoderIds.Nvenc, StringComparison.OrdinalIgnoreCase)
            ? new[] { 1, 2 }
            : new[] { 1 };
        var definition = new EncoderBenchmarkDefinition(
            sourcePath, TimeSpan.FromSeconds(durationSeconds), new FileInfo(sourcePath).Length,
            media.VideoCodec ?? "Unknown codec", media.FormatName ?? "Unknown container",
            media.Width is > 0 && media.Height is > 0 ? $"{media.Width}×{media.Height}" : "Unknown resolution",
            media.Fps,
            new EncoderBenchmarkSettings(
                encoder, capabilities.DisplayName, current.UseGpu, current.ProjectedTargetMb,
                current.ScaleMode, current.EncoderPreset, current.QualityValue, current.TenBit,
                current.AudioChannels, EncodingService.StreamMapMode.KeepAll,
                CopySubtitles: true, CopyDataStreams: true, CopyAttachments: true,
                GetSelectedOutputContainer(), ContainerCompatibilityConfirmed: true),
            presets, concurrency);
        var runner = new EncodingServiceBenchmarkJobRunner(
            AppPaths.InstallDirectory, _config.FfmpegPath, _config.FfprobePath,
            message => Debug.WriteLine(message));
        var service = new EncoderBenchmarkService(runner);
        using var dialog = new EncoderBenchmarkForm(definition, service);
        dialog.ShowDialog(this);
    }

    internal static bool CanStartEncoderBenchmark(
        bool encodingActive,
        bool remuxActive,
        bool sampleComparisonActive,
        out string reason)
    {
        reason = encodingActive
            ? "Finish or stop the active production encode before benchmarking; concurrent activity would distort the result."
            : remuxActive
                ? "Finish or cancel the active remux before benchmarking."
                : sampleComparisonActive
                    ? "Finish or cancel sample comparison before benchmarking."
                    : "";
        return reason.Length == 0;
    }
}
