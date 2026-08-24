using MediaFlux.Models;
using MediaFlux.Services;

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
            case "Edit Job": case "Edit Encode Settings": case "Change Schedule":
                using (var editor = new JobEditorForm(job)) if (editor.ShowDialog(this) == DialogResult.OK) SaveJobs();
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
        AutoTargetSize = chkAutoTargetSize.Checked, TargetSize = txtTargetSize.Text
    };

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
        if (chkTenBit != null) chkTenBit.Checked = settings.TenBit;
        SelectComboText(comboCompressionProfile, settings.CompressionProfile);
        if (comboAudioChannels != null) SelectComboText(comboAudioChannels, settings.AudioChannels);
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
