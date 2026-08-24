using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;

namespace MediaFlux;

public partial class MainForm
{
    private EncodeJobService _encodeJobService = null!;
    private List<EncodeJob> _encodeJobs = new();
    private System.Windows.Forms.Timer? _jobSchedulerTimer;

    private void InitializeJobManager()
    {
        _encodeJobService = new EncodeJobService(AppPaths.EncodeJobsFile);
        _encodeJobs = _encodeJobService.Load();
        foreach (var job in _encodeJobs) EncodeJobService.RefreshStatus(job);
        SaveJobs();
        _jobSchedulerTimer = new System.Windows.Forms.Timer { Interval = 30_000, Enabled = true };
        _jobSchedulerTimer.Tick += async (_, __) => await RunDueJobsAsync();
    }

    private void SaveJobs()
    {
        _encodeJobService.Save(_encodeJobs);
        UpdateTrayStatus();
    }

    private async Task RunDueJobsAsync()
    {
        if (_encodingActive) return;
        var due = EncodeJobService.Due(_encodeJobs, DateTime.Now).FirstOrDefault();
        if (due != null) await RunJobAsync(due, scheduled: true);
    }

    private void ShowJobManager()
    {
        using var form = new JobManagerForm(() => _encodeJobs, HandleJobManagerAction);
        form.ShowDialog(this);
    }

    private async void HandleJobManagerAction(EncodeJob job, string action)
    {
        switch (action)
        {
            case "Run Now": await RunJobAsync(job, scheduled: false); break;
            case "Edit Job": case "Change Schedule":
                using (var editor = new JobEditorForm(job)) if (editor.ShowDialog(this) == DialogResult.OK) SaveJobs();
                break;
            case "Edit Encode Settings":
                using (var editor = new EncodeJobSettingsForm(job.Name, job.Settings))
                {
                    if (editor.ShowDialog(this) == DialogResult.OK)
                    {
                        job.Settings = editor.EditedSettings;
                        job.ModifiedUtc = DateTime.UtcNow;
                        RefreshJobEstimates(job);
                        SaveJobs();
                    }
                }
                break;
            case "View Files": MessageBox.Show(this, string.Join(Environment.NewLine, job.Files.Select(file => file.SourcePath)), job.Name, MessageBoxButtons.OK, MessageBoxIcon.Information); break;
            case "Load into Main Queue": await LoadJobIntoMainQueueAsync(job); break;
            case "Enable / Disable": job.Enabled = !job.Enabled; EncodeJobService.RefreshStatus(job); job.ModifiedUtc = DateTime.UtcNow; SaveJobs(); break;
            case "Delete":
                if (MessageBox.Show(this, $"Delete saved job '{job.Name}'?", "Delete Job", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) { _encodeJobs.Remove(job); SaveJobs(); }
                break;
        }
    }

    private void SaveJobFromQueue(bool selectedOnly)
    {
        var rows = (selectedOnly ? dgvEncodeQueue.SelectedRows.Cast<DataGridViewRow>() : GetEncodeRowsInVisualOrder())
            .Where(row => !row.IsNewRow && row.Tag is not RowMeta { ExcludedFromEncodeAsDuplicate: true }).ToArray();
        if (rows.Length == 0) { ShowStatusInfo(selectedOnly ? "Select eligible files to save as a job." : "The queue has no eligible files to save."); return; }
        UpdateSizeTotals(force: true);
        var job = new EncodeJob { Name = $"Encode job {DateTime.Now:g}", Settings = CaptureJobSettings(), EstimatedOutputBytes = (long)(_queueTotalEstimatedMb * 1024 * 1024), EstimatedSavingsBytes = (long)(Math.Max(0, _queueTotalSourceMb - _queueTotalEstimatedMb) * 1024 * 1024) };
        foreach (var row in rows)
        {
            var meta = EnsureRowMeta(row);
            if (!string.IsNullOrWhiteSpace(meta.Path)) job.Files.Add(new EncodeJobFile { SourcePath = meta.Path, CustomCompressionProfile = meta.CustomCompressionProfile, CustomTargetMb = meta.CustomTargetMb });
        }
        using var editor = new JobEditorForm(job);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        _encodeJobs.Add(job); SaveJobs(); ShowStatusInfo($"Saved job '{job.Name}' with {job.Files.Count:N0} file(s).");
    }

    private EncodeJobSettings CaptureJobSettings() => new()
    {
        OutputFolder = cmbEncodeOutput.Text, CompressionProfile = comboCompressionProfile.Text,
        EncoderId = _config.LastEncoderId, VideoCodec = _config.LastVideoCodec, EncoderPreset = _config.LastEncoderPreset,
        OutputContainer = _config.LastOutputContainer, QualityValue = _config.LastQualityValue,
        TenBit = chkTenBit?.Checked == true, AudioChannels = comboAudioChannels?.Text ?? "", VideoFormat = comboVideoFormat.Text,
        AutoTargetSize = chkAutoTargetSize.Checked, TargetSize = txtTargetSize.Text, Resolution = comboResolution?.Text ?? "",
        DeleteSourceAfterCompression = chkDeleteSource.Checked, EnableOutputSuffix = _config.EnableOutputSuffix,
        EnableCodecSuffix = _config.EnableCodecSuffix, OutputSuffix = _config.OutputSuffix
    };

    private void RefreshJobEstimates(EncodeJob job)
    {
        // Reuse the same SizeEstimateService used by the main queue. A fixed
        // target is exact; profile-based estimates are refreshed per source.
        if (!job.Settings.AutoTargetSize && double.TryParse(job.Settings.TargetSize, out double targetMb) && targetMb > 0)
        {
            job.EstimatedOutputBytes = (long)(targetMb * 1024d * 1024d * job.Files.Count);
            long sourceBytes = job.Files.Where(file => File.Exists(file.SourcePath)).Sum(file => new FileInfo(file.SourcePath).Length);
            if (sourceBytes > 0) job.EstimatedSavingsBytes = Math.Max(0, sourceBytes - job.EstimatedOutputBytes);
            return;
        }

        try
        {
            VideoCodecFamily codec = VideoEncoderCompatibility.ParseCodecFamily(job.Settings.VideoCodec);
            string encoderId = VideoEncoderCompatibility.ResolveEncoderId(job.Settings.EncoderId, codec);
            VideoEncoderSelection encoder = EncoderRegistry.Default.Resolve(encoderId, codec).Selection;
            int? targetHeight = job.Settings.Resolution switch { "720p" => 720, "1080p" => 1080, "1440p" => 1440, "4K" => 2160, _ => null };
            int? audioChannels = job.Settings.AudioChannels.StartsWith("Stereo", StringComparison.OrdinalIgnoreCase) ? 2 : job.Settings.AudioChannels.StartsWith("5.1", StringComparison.OrdinalIgnoreCase) ? 6 : null;
            double estimateMb = job.Files.Where(file => File.Exists(file.SourcePath)).Sum(file => _sizeEstimateService.EstimateAutoTargetMbSmart(file.SourcePath, job.Settings.CompressionProfile, encoder, job.Settings.QualityValue, targetHeight, audioChannels));
            if (estimateMb > 0)
            {
                job.EstimatedOutputBytes = (long)(estimateMb * 1024d * 1024d);
                long sourceBytes = job.Files.Where(file => File.Exists(file.SourcePath)).Sum(file => new FileInfo(file.SourcePath).Length);
                if (sourceBytes > 0) job.EstimatedSavingsBytes = Math.Max(0, sourceBytes - job.EstimatedOutputBytes);
            }
        }
        catch
        {
            // Preserve the existing estimate when source metadata or a selected
            // encoder is unavailable; execution still performs normal validation.
        }
    }

    private async Task LoadJobIntoMainQueueAsync(EncodeJob job)
    {
        var existing = job.Files.Where(file => File.Exists(file.SourcePath)).ToArray();
        if (existing.Length != job.Files.Count) ShowStatusInfo($"{job.Files.Count - existing.Length} saved source file(s) are unavailable and were not loaded.");
        if (existing.Length == 0) return;
        await ImportEncodePathsAsync(existing.Select(file => file.SourcePath), false, false, replaceExisting: true, rememberRoots: false);
        foreach (DataGridViewRow row in GetEncodeRowsInVisualOrder())
        {
            var file = existing.FirstOrDefault(item => string.Equals(item.SourcePath, GetPathFromRow(row), StringComparison.OrdinalIgnoreCase));
            if (file == null) continue;
            var meta = EnsureRowMeta(row); meta.CustomCompressionProfile = file.CustomCompressionProfile; meta.CustomTargetMb = file.CustomTargetMb; UpdateRowCustomFlag(row);
        }
        ShowStatusInfo($"Loaded '{job.Name}' into the main queue. Saved job unchanged.");
    }

    private void ApplyJobSettings(EncodeJobSettings settings)
    {
        // Apply the saved primitives immediately before execution. The job holds
        // values, not references to Config or controls, so later normal changes
        // cannot affect an already-saved job.
        cmbEncodeOutput.Text = settings.OutputFolder;
        txtTargetSize.Text = settings.TargetSize;
        chkAutoTargetSize.Checked = settings.AutoTargetSize;
        chkDeleteSource.Checked = settings.DeleteSourceAfterCompression;
        if (chkTenBit != null) chkTenBit.Checked = settings.TenBit;
        SelectComboText(comboCompressionProfile, settings.CompressionProfile);
        if (comboAudioChannels != null) SelectComboText(comboAudioChannels, settings.AudioChannels);
        if (comboResolution != null) SelectComboText(comboResolution, settings.Resolution);
        _applyingEncodeDropdownSettings = true;
        try
        {
            SelectEncoderById(settings.EncoderId);
            SelectOutputContainer(settings.OutputContainer);
            RefreshVideoFormatItems(VideoEncoderCompatibility.ParseCodecFamily(settings.VideoCodec));
            RefreshEncoderPresetItems(settings.EncoderPreset);
            if (nudAutoQuality != null) nudAutoQuality.Value = Math.Clamp(settings.QualityValue, (int)nudAutoQuality.Minimum, (int)nudAutoQuality.Maximum);
        }
        finally { _applyingEncodeDropdownSettings = false; }
        _config.EnableOutputSuffix = settings.EnableOutputSuffix;
        _config.EnableCodecSuffix = settings.EnableCodecSuffix;
        _config.OutputSuffix = settings.OutputSuffix;
        UpdateEncoderUiState();
    }

    private async Task RunJobAsync(EncodeJob job, bool scheduled)
    {
        if (_encodingActive) { job.LastResult = "Deferred: MediaFlux is already encoding."; SaveJobs(); ShowStatusInfo(job.LastResult); return; }
        var sources = EncodeJobService.SplitAvailableFiles(job.Files);
        var missing = sources.Missing;
        var available = sources.Available;
        if (available.Count == 0) { job.Status = EncodeJobStatus.Failed; job.LastResult = "No saved source files are available."; job.LastRunUtc = DateTime.UtcNow; SaveJobs(); ShowStatusInfo(job.LastResult); return; }
        job.Status = EncodeJobStatus.Running; job.LastRunUtc = DateTime.UtcNow; job.LastResult = missing.Count == 0 ? "Running." : $"Running {available.Count} available file(s); {missing.Count} source file(s) unavailable."; SaveJobs();
        ApplyJobSettings(job.Settings);
        await LoadJobIntoMainQueueAsync(job);
        if (dgvEncodeQueue.Rows.Count == 0) { job.Status = EncodeJobStatus.Failed; job.LastResult = "Could not load any valid source files into the encode queue."; SaveJobs(); return; }
        await StartEncodeAsync(processAllOverride: true);
        job.Status = _cancelEncode ? EncodeJobStatus.Failed : _encodeFailedCount > 0 ? EncodeJobStatus.CompletedWithErrors : EncodeJobStatus.Completed;
        job.LastResult = job.Status == EncodeJobStatus.Completed ? "Completed." : job.Status == EncodeJobStatus.CompletedWithErrors ? $"Completed with {_encodeFailedCount} failed file(s)." : "Stopped or failed.";
        SaveJobs();
        if (scheduled) ShowStatusInfo($"Scheduled job '{job.Name}' {job.LastResult}");
    }
}
