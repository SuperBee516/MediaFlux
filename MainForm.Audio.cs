using Encode.Models;
using Encode.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Encode
{
    public partial class MainForm : Form
    {
        private void chkAudioDenoise_CheckedChanged(object? sender, EventArgs e)
        {
            bool wasEnabled = txtAudioDenoiseModel.Enabled;

            // Recompute based on current operation + checkbox state
            UpdateAudioUiState();

            bool nowEnabled = txtAudioDenoiseModel.Enabled;

            // If RNNoise just became enabled and no model is set, nudge the user once
            if (!wasEnabled &&
                nowEnabled &&
                string.IsNullOrWhiteSpace(txtAudioDenoiseModel.Text))
            {
                var result = MessageBox.Show(
                    this,
                    "RNNoise denoising is enabled but no model file is selected.\n\n" +
                    "Would you like to choose a model file now?",
                    "RNNoise model required",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    BrowseAudioDenoiseModel();
                }
            }
        }

        private void btnBrowseAudioDenoiseModel_Click(object? sender, EventArgs e)
        {
            BrowseAudioDenoiseModel();
        }

        private void BrowseAudioDenoiseModel()
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "RNNoise model (*.onnx;*.bin)|*.onnx;*.bin|All files (*.*)|*.*",
                Title = "Select RNNoise model file"
            };

            // Start in last-used folder if possible
            if (!string.IsNullOrWhiteSpace(txtAudioDenoiseModel.Text))
            {
                try
                {
                    var dir = Path.GetDirectoryName(txtAudioDenoiseModel.Text);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                        dlg.InitialDirectory = dir;
                }
                catch
                {
                    // ignore bad paths
                }
            }

            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                txtAudioDenoiseModel.Text = dlg.FileName;
            }
        }

        private void chkAudioNormalize_CheckedChanged(object? sender, EventArgs e)
        {
            UpdateAudioUiState();
        }

        private void UpdateAudioUiState()
        {
            var opText = comboAudioOperation.SelectedItem?.ToString() ?? "Extract (no re-encode)";
            bool isConvert = opText.StartsWith("Convert", StringComparison.OrdinalIgnoreCase);

            // RNNoise textbox + Browse only make sense for Convert
            bool denoiseEnabled = isConvert && chkAudioDenoise.Checked;
            txtAudioDenoiseModel.Enabled = denoiseEnabled;
            btnBrowseAudioDenoiseModel.Enabled = denoiseEnabled;

            // Loudnorm checkbox only in Convert mode
            chkAudioNormalize.Enabled = isConvert;
            if (!isConvert)
                chkAudioNormalize.Checked = false;

            // Mode combo only when both Convert and Normalize are active
            comboAudioNormalizeMode.Enabled = isConvert && chkAudioNormalize.Checked;
        }

        private void RemoveSelectedAudioRows_Click(object? sender, EventArgs e)
        {
            if (dgvAudioQueue.SelectedRows.Count == 0)
                return;

            foreach (DataGridViewRow row in dgvAudioQueue.SelectedRows)
            {
                if (!row.IsNewRow)
                    dgvAudioQueue.Rows.Remove(row);
            }

            int remaining = dgvAudioQueue.Rows.Cast<DataGridViewRow>()
                .Count(r => !r.IsNewRow);
            lblAudioStatus.Text = $"{remaining} file(s) queued for audio operations.";
        }

        private void ClearAudioQueue_Click(object? sender, EventArgs e)
        {
            if (dgvAudioQueue.Rows.Count == 0)
                return;

            var confirm = MessageBox.Show(
                "Clear all items from the audio queue?",
                "Confirm Clear",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes)
                return;

            dgvAudioQueue.Rows.Clear();
            lblAudioStatus.Text = "Audio queue cleared.";
        }

        private void btnBrowseAudioInput_Click(object? sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                SelectedPath = string.IsNullOrWhiteSpace(cmbAudioInputFolder.Text)
                    ? Application.StartupPath
                    : cmbAudioInputFolder.Text
            };

            if (dlg.ShowDialog() != DialogResult.OK)
                return;

            string picked = dlg.SelectedPath;
            cmbAudioInputFolder.Text = picked;

            // Reuse existing folder history lists
            AddToHistory(_config.LastInputFolders, picked);
            RefreshHistoryCombo(cmbAudioInputFolder, _config.LastInputFolders);
            _config.Save(_configPath);

            toolStripStatusLabel1.Text = "Preparing to scan for audio…";
            ScanAndPopulateAudioGrid(picked);
        }

        private void btnBrowseAudioOutput_Click(object? sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                SelectedPath = string.IsNullOrWhiteSpace(cmbAudioOutputFolder.Text)
                    ? cmbAudioInputFolder.Text
                    : cmbAudioOutputFolder.Text
            };

            if (dlg.ShowDialog() != DialogResult.OK)
                return;

            string picked = dlg.SelectedPath;
            cmbAudioOutputFolder.Text = picked;

            AddToHistory(_config.LastOutputFolders, picked);
            RefreshHistoryCombo(cmbAudioOutputFolder, _config.LastOutputFolders);
            _config.Save(_configPath);
        }

        private async void btnStartAudio_Click(object? sender, EventArgs e)
        {
            if (dgvAudioQueue.Rows.Count == 0)
            {
                MessageBox.Show("There are no files in the audio queue.", "Audio",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var outputFolder = cmbAudioOutputFolder.Text;
            if (string.IsNullOrWhiteSpace(outputFolder))
            {
                // fallback to input folder if no explicit output is set
                outputFolder = cmbAudioInputFolder.Text;
                if (string.IsNullOrWhiteSpace(outputFolder))
                {
                    MessageBox.Show("Please choose an output folder for audio files.", "Audio",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            // Validate RNNoise config if it will actually be used
            BuildAudioJobSettingsFromUi(out var operationForCheck, out _, out _);
            bool denoiseWillRun = (operationForCheck == "Convert") && chkAudioDenoise?.Checked == true;

            if (denoiseWillRun)
            {
                string modelPath = txtAudioDenoiseModel.Text.Trim();

                if (string.IsNullOrWhiteSpace(modelPath))
                {
                    MessageBox.Show(
                        this,
                        "RNNoise denoising is enabled but no model file is selected.",
                        "RNNoise model required",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);

                    txtAudioDenoiseModel.Focus();
                    return;
                }

                if (!File.Exists(modelPath))
                {
                    MessageBox.Show(
                        this,
                        "The RNNoise model file does not exist:\n\n" + modelPath,
                        "RNNoise model not found",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    txtAudioDenoiseModel.Focus();
                    txtAudioDenoiseModel.SelectAll();
                    return;
                }
            }

            if (!Directory.Exists(outputFolder))
            {
                Directory.CreateDirectory(outputFolder);
            }

            bool processSelectedOnly = dgvAudioQueue.SelectedRows.Count > 0;
            var jobs = BuildAudioJobsFromGrid(outputFolder, processSelectedOnly);
            if (jobs.Count == 0)
            {
                MessageBox.Show("No valid audio jobs were built from the queue.", "Audio",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            lblAudioStatus.Text = $"Running {jobs.Count} audio job(s)…";
            toolStripStatusLabel1.Text = "Audio processing in progress…";

            foreach (var job in jobs)
            {
                try
                {
                    // --- NEW: initialise shared metrics for this audio file ---
                    var dur = GetVideoDuration(job.InputPath); // works for audio too
                    _currentEncodeDuration = TimeSpan.Zero;
                    _currentEncodeTotalDuration = dur;
                    ResetEncodeMetrics();
                    StartJobTimer();

                    bool ok = job.Operation == "Extract"
                        ? await _audioService.ExtractAsync(job)
                        : await _audioService.ConvertAsync(job);

                    if (!ok)
                    {
                        lblAudioStatus.Text = $"Audio job failed for {Path.GetFileName(job.InputPath)}";
                        // we keep going; you can change this if you prefer hard fail
                    }
                }
                catch (Exception ex)
                {
                    lblAudioStatus.Text = $"Error: {ex.Message}";
                    // continue with remaining jobs
                }
            }

            ResetEncodeMetrics();
            lblAudioStatus.Text = "Audio jobs completed.";
            toolStripStatusLabel1.Text = "Audio processing complete.";
        }

        private void BuildAudioJobSettingsFromUi(out string operation, out string? extension, out LoudnormMode loudnormMode)
        {
            var opText = comboAudioOperation.SelectedItem?.ToString() ?? "Extract (no re-encode)";
            operation = opText.StartsWith("Convert", StringComparison.OrdinalIgnoreCase)
                ? "Convert"
                : "Extract";

            // Loudnorm: only meaningful for Convert
            if (operation != "Convert" || !chkAudioNormalize.Checked)
            {
                loudnormMode = LoudnormMode.None;
            }
            else
            {
                var modeText = comboAudioNormalizeMode.SelectedItem?.ToString()
                               ?? "Single-pass (fast)";
                loudnormMode = modeText.StartsWith("Two-pass", StringComparison.OrdinalIgnoreCase)
                    ? LoudnormMode.TwoPass
                    : LoudnormMode.SinglePass;
            }

            // For Extract, we always keep the source container.
            // AudioService will derive the extension from the input file.
            if (operation == "Extract")
            {
                extension = null;
                return;
            }

            // For Convert, the format combo actually matters.
            var fmt = comboAudioFormat.SelectedItem?.ToString()
                      ?? "AAC (m4a)";
            extension = null;

            switch (fmt)
            {
                case "AAC (m4a)":
                    extension = ".m4a";
                    break;
                case "MP3":
                    extension = ".mp3";
                    break;
                case "FLAC":
                    extension = ".flac";
                    break;
                case "Opus":
                    extension = ".opus";
                    break;
                case "WAV":
                    extension = ".wav";
                    break;
                case "AC3":
                    extension = ".ac3";
                    break;
                case "E-AC3":
                    extension = ".eac3";
                    break;
                case "Same as source (extract only)":
                default:
                    // For Convert, "Same as source" isn't meaningful; fall back to AAC.
                    extension = ".m4a";
                    break;
            }
        }

        private List<AudioJob> BuildAudioJobsFromGrid(string outputFolder, bool processSelectedOnly)
        {
            var jobs = new List<AudioJob>();
            BuildAudioJobSettingsFromUi(out var operation, out var extension, out var loudnormMode);

            // RNNoise is only meaningful for Convert
            bool denoiseAllowed = (operation == "Convert") && chkAudioDenoise?.Checked == true;
            string? modelPath = string.IsNullOrWhiteSpace(txtAudioDenoiseModel?.Text)
                ? null
                : txtAudioDenoiseModel.Text;

            var rows = processSelectedOnly
                ? dgvAudioQueue.SelectedRows.Cast<DataGridViewRow>()
                : dgvAudioQueue.Rows.Cast<DataGridViewRow>();

            foreach (DataGridViewRow row in rows)
            {
                if (row.IsNewRow) continue;

                var path = row.Cells["colAudioPath"].Value as string;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    continue;

                jobs.Add(new AudioJob
                {
                    InputPath = path,
                    OutputFolder = outputFolder,
                    Operation = operation,
                    OutputExtension = extension,
                    Loudnorm = loudnormMode,
                    DenoiseEnabled = denoiseAllowed,
                    DenoiseModelPath = denoiseAllowed ? modelPath : null,
                    Quality = GetAudioQualityFromUi()
                });
            }

            return jobs;
        }

        private AudioQuality GetAudioQualityFromUi()
        {
            // Guard for designer / weird states
            if (comboAudioQuality.SelectedIndex < 0)
                return AudioQuality.Auto;

            return comboAudioQuality.SelectedIndex switch
            {
                1 => AudioQuality.VeryLow,
                2 => AudioQuality.Low,
                3 => AudioQuality.Medium,
                4 => AudioQuality.High,
                5 => AudioQuality.VeryHigh,
                _ => AudioQuality.Auto
            };
        }

        private int? GetSelectedAudioChannels()
        {
            var text = comboAudioChannels?.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (text.StartsWith("Stereo", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("2.0"))
                return 2;

            if (text.StartsWith("5.1", StringComparison.OrdinalIgnoreCase))
                return 6;

            if (text.StartsWith("Keep", StringComparison.OrdinalIgnoreCase))
                return null;    // no -ac → keep source layout

            return null;
        }

        private void ScanAndPopulateAudioGrid(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;

            _activityIndicator?.StartActivity(UiActivity.FolderScan);
            try
            {
                using (UiBusy("Scanning for audio…"))
                {
                    dgvAudioQueue.Rows.Clear();

                    // Basic audio-related extensions; includes common video containers
                    // because we can extract audio from them.
                    string[] allowedExt =
                    {
                ".mp3", ".m4a", ".aac", ".flac", ".wav",
                ".ogg", ".opus", ".wma",
                ".ac3", ".eac3", ".dts", ".thd",
                ".mkv", ".mp4", ".m4v", ".mov",
                ".ts", ".m2ts", ".mka"
            };

                    var extSet = new HashSet<string>(allowedExt, StringComparer.OrdinalIgnoreCase);

                    var searchOpt = chkAudioIncludeSubfolders.Checked
                        ? SearchOption.AllDirectories
                        : SearchOption.TopDirectoryOnly;

                    foreach (var f in Directory.GetFiles(folder, "*.*", searchOpt))
                    {
                        if (!extSet.Contains(Path.GetExtension(f)))
                            continue;

                        AddAudioItemIfNotPresent(f);
                    }

                    int count = dgvAudioQueue.Rows.Cast<DataGridViewRow>()
                        .Count(r => !r.IsNewRow);
                    lblAudioStatus.Text = $"{count} file(s) queued for audio operations.";
                }
            }
            finally
            {
                _activityIndicator?.StopActivity(UiActivity.FolderScan);
            }
        }

        private bool AddAudioItemIfNotPresent(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            // Skip if already present by path
            foreach (DataGridViewRow row in dgvAudioQueue.Rows)
            {
                var existing = row.Cells["colAudioPath"].Value as string;
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            int idx = dgvAudioQueue.Rows.Add();
            var r = dgvAudioQueue.Rows[idx];
            r.Cells["colAudioName"].Value = Path.GetFileName(path);
            r.Cells["colAudioPath"].Value = path;

            return true;
        }

        private void dgvAudioQueue_DragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        private void dgvAudioQueue_DragDrop(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
                return;

            var files = ExpandFilesAndFolders(paths);
            var extSet = new HashSet<string>(DefaultAudioExts, StringComparer.OrdinalIgnoreCase);

            int added = 0;
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f);
                if (string.IsNullOrEmpty(ext)) continue;
                if (!extSet.Contains(ext)) continue;

                if (AddAudioItemIfNotPresent(f))
                    added++;
            }

            if (added > 0)
            {
                int count = dgvAudioQueue.Rows.Cast<DataGridViewRow>()
                    .Count(r => !r.IsNewRow);
                lblAudioStatus.Text = $"{count} file(s) queued for audio operations.";
                toolStripStatusLabel1.Text = $"Added {added} file(s) to audio queue.";
            }
        }

        private void StartSelectedAudio_Click(object? sender, EventArgs e)
        {
            if (dgvAudioQueue.SelectedRows.Count == 0)
            {
                MessageBox.Show("Select one or more audio items first.",
                    "Audio", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // btnStartAudio_Click now respects selection via BuildAudioJobsFromGrid
            btnStartAudio_Click(sender, e);
        }

        private void OpenAudioFileFolder_Click(object? sender, EventArgs e)
        {
            if (dgvAudioQueue.SelectedRows.Count == 0)
                return;

            var row = dgvAudioQueue.SelectedRows[0];
            var path = row.Cells["colAudioPath"].Value as string;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Process.Start("explorer.exe", dir);
            }
        }

        private void EditAudioMetadata_Click(object? sender, EventArgs e)
        {
            if (dgvAudioQueue.SelectedRows.Count != 1)
            {
                MessageBox.Show(
                    "Please select exactly one audio file to edit metadata.",
                    "Metadata",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var row = dgvAudioQueue.SelectedRows[0];
            var path = row.Cells["colAudioPath"].Value as string;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                MessageBox.Show(
                    "File not found on disk.",
                    "Metadata",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            using (var dlg = new AudioMetadataForm(path))
            {
                dlg.ShowDialog(this);
            }
        }

        private void comboAudioOperation_SelectedIndexChanged(object? sender, EventArgs e)
        {
            var opText = comboAudioOperation.SelectedItem?.ToString() ?? "Extract (no re-encode)";
            bool isConvert = opText.StartsWith("Convert", StringComparison.OrdinalIgnoreCase);

            if (!isConvert)
            {
                // Extract mode: format is "Same as source" and disabled
                comboAudioFormat.SelectedIndex = 0; // "Same as source (extract only)"
                comboAudioFormat.Enabled = false;
            }
            else
            {
                comboAudioFormat.Enabled = true;

                // If it was on "Same as source", default to a sane convert target like AAC
                if (comboAudioFormat.SelectedIndex <= 0)
                {
                    // assuming index 1 is AAC (m4a)
                    comboAudioFormat.SelectedIndex = 1;
                }
            }

            // Centralize RNNoise + normalize state here
            UpdateAudioUiState();
        }
    }
}
