using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux
{
    public partial class MainForm : MediaFluxForm
    {
        // =====================
        // Export/Import DTOs
        // =====================
        private sealed class QueueSnapshot
        {
            public string Version { get; set; } = "1.5";
            public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
            public QueueSettings Settings { get; set; } = new QueueSettings();
            public List<QueueItem> Items { get; set; } = new List<QueueItem>();
        }

        private sealed class QueueSettings
        {
            public bool AutoTargetSize { get; set; }
            public double? ManualTargetMb { get; set; }  // null when auto
            public string CompressionProfile { get; set; } = "";
            public string EncoderMode { get; set; } = "";
            public string EncoderId { get; set; } = "";
            public string VideoFormat { get; set; } = "";
            public string VideoCodec { get; set; } = "";
            public string EncoderPreset { get; set; } = "";
            public int? QualityValue { get; set; }
            public bool? TenBit { get; set; }
            public string AudioChannels { get; set; } = "";
            public string OutputFolder { get; set; } = "";       // cmbEncodeOutput.Text
            public string OutputContainer { get; set; } = nameof(OutputContainerSelection.Mp4);
        }

        private sealed class QueueItem
        {
            public string Path { get; set; } = "";
            public string? ContentHint { get; set; }
            public LibraryPolicyQueueItem? LibraryPolicyIntent { get; set; }
            public DvdQueueItem? Dvd { get; set; }
        }

        private sealed class DvdQueueItem
        {
            public string VideoTsFolder { get; set; } = "";
            public string TitleSetId { get; set; } = "";
            public string OutputPath { get; set; } = "";
            public List<int> SelectedAudioStreamIndexes { get; set; } = new();
            public List<int> SelectedSubtitleStreamIndexes { get; set; } = new();
        }

        // Centralized JSON options (pretty for readability)
        private static readonly JsonSerializerOptions _queueJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Build a settings snapshot from current UI
        private QueueSettings CaptureCurrentQueueSettings()
        {
            double? manualMb = null;
            if (!chkAutoTargetSize.Checked && double.TryParse(txtTargetSize.Text, out var mb) && mb > 0)
                manualMb = mb;

            return new QueueSettings
            {
                AutoTargetSize = chkAutoTargetSize.Checked,
                ManualTargetMb = manualMb,
                CompressionProfile = comboCompressionProfile.SelectedItem?.ToString() ?? "",
                EncoderMode = comboEncoderMode.SelectedItem?.ToString() ?? "",
                EncoderId = GetSelectedEncoderId(),
                VideoFormat = comboVideoFormat.Text,
                VideoCodec = GetSelectedVideoCodecFamily().ToString(),
                EncoderPreset = GetSelectedEncoderPreset(),
                QualityValue = nudAutoQuality == null
                    ? null
                    : (int)nudAutoQuality.Value,
                TenBit = chkTenBit?.Checked,
                AudioChannels = comboAudioChannels?.Text ?? "",
                OutputFolder = cmbEncodeOutput.Text ?? "",
                OutputContainer = GetSelectedOutputContainer().ToString()
            };
        }

        // Resolve full file path from a row (supports Tag = RowMeta or string)
        private string? GetPathFromRow(DataGridViewRow row)
        {
            if (row.Tag is RowMeta rm) return rm.Path;
            if (row.Tag is string s) return s;
            return null;
        }

        // FILE MENU: Export Queue…
        private void exportQueueToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            if (dgvEncodeQueue.Rows.Count == 0)
            {
                MessageBox.Show("There are no items in the Encode queue to export.",
                    "Export Queue", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var items = new List<QueueItem>();
            foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
            {
                var path = GetPathFromRow(row);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    DvdImportOptions? dvdOptions =
                        (row.Tag as RowMeta)?.DvdEncodeOptions;
                    items.Add(new QueueItem
                    {
                        Path = path,
                        ContentHint = (row.Tag as RowMeta)?.ContentHint is
                            SmartEncodeContentHint hint &&
                            hint != SmartEncodeContentHint.Auto
                                ? hint.ToString()
                                : null,
                        LibraryPolicyIntent = (row.Tag as RowMeta)?.LibraryPolicyIntent,
                        Dvd = dvdOptions == null
                            ? null
                            : new DvdQueueItem
                            {
                                VideoTsFolder = Path.GetDirectoryName(
                                    dvdOptions.Candidate.Segments[0].Path) ?? "",
                                TitleSetId = dvdOptions.Candidate.TitleSetId,
                                OutputPath = dvdOptions.OutputPath,
                                SelectedAudioStreamIndexes =
                                    dvdOptions.SelectedAudioStreamIndexes.ToList(),
                                SelectedSubtitleStreamIndexes =
                                    dvdOptions.SelectedSubtitleStreamIndexes.ToList()
                            }
                    });
                }
            }

            if (items.Count == 0)
            {
                MessageBox.Show("No valid items to export.", "Export Queue",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var sfd = new SaveFileDialog
            {
                Title = "Export Queue",
                Filter = "Encode Queue (*.cequeue)|*.cequeue|JSON (*.json)|*.json|All files (*.*)|*.*",
                FileName = $"queue_{DateTime.Now:yyyyMMdd_HHmm}.cequeue",
                OverwritePrompt = true
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;

            var snapshot = new QueueSnapshot
            {
                Settings = CaptureCurrentQueueSettings(),
                Items = items
            };

            try
            {
                var json = JsonSerializer.Serialize(snapshot, _queueJsonOptions);
                File.WriteAllText(sfd.FileName, json);
                toolStripStatusLabel1.Text = $"Exported {items.Count} item(s) to {Path.GetFileName(sfd.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed:\n{ex.Message}", "Export Queue",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // FILE MENU: Import Queue…        
        private async void importQueueToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Import Queue",
                Filter = "Encode Queue (*.cequeue;*.json)|*.cequeue;*.json|All files (*.*)|*.*",
                Multiselect = false
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var json = File.ReadAllText(ofd.FileName);
                var snapshot = JsonSerializer.Deserialize<QueueSnapshot>(json, _queueJsonOptions);
                if (snapshot == null)
                {
                    MessageBox.Show("Invalid or empty queue file.", "Import Queue",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                snapshot.Items ??= new List<QueueItem>();
                if (snapshot.Items.Any(item => item.Dvd != null) &&
                    !EnsureFfmpegToolsAvailable())
                {
                    return;
                }

                // Ask: Append or Replace?
                var choice = MessageBox.Show(
                    "Do you want to APPEND these items to your current queue?\n\n" +
                    "Click Yes to Append, No to Replace the current queue.",
                    "Import Queue",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (choice == DialogResult.Cancel) return;

                if (choice == DialogResult.No)
                {
                    if (_encodingActive)
                    {
                        MessageBox.Show(
                            this,
                            "The active Encode queue cannot be replaced while jobs are running. " +
                            "Choose Append or stop encoding first.",
                            "Import Queue",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                        return;
                    }

                    _estimateService.ResetAndCancel();
                    _rowsByPath.Clear();
                    _estimatedSizeMap.Clear();
                    _queueSourceSizeMap.Clear();
                    _queueTotalSourceMb = 0;
                    _queueTotalEstimatedMb = 0;
                    _queueFileCount = 0;
                    _queueTotalsDirty = false;
                    _suppressRowEvents = true;
                    try
                    {
                        dgvEncodeQueue.Rows.Clear();
                    }
                    finally
                    {
                        _suppressRowEvents = false;
                    }
                }

                int added = 0, missing = 0;
                foreach (var qi in snapshot.Items)
                {
                    if (qi.Dvd != null)
                    {
                        toolStripStatusLabel1.Text =
                            $"Restoring DVD title {qi.Dvd.TitleSetId}…";
                        DvdImportOptions? restored = await RestoreDvdQueueItemAsync(
                            qi.Dvd);
                        if (restored != null &&
                            QueueDvdEncode(restored, showMessages: false))
                        {
                            added++;
                        }
                        else
                        {
                            missing++;
                        }
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(qi.Path))
                        continue;

                    if (File.Exists(qi.Path))
                    {
                        if (AddEncodeItemIfNotPresent(qi.Path))
                            added++;

                        if (Enum.TryParse(
                                qi.ContentHint,
                                ignoreCase: true,
                                out SmartEncodeContentHint savedHint) &&
                            _rowsByPath.TryGetValue(qi.Path, out DataGridViewRow? importedRow))
                        {
                            RowMeta meta = EnsureRowMeta(importedRow);
                            meta.ContentHint = savedHint;
                            meta.LibraryPolicyIntent = qi.LibraryPolicyIntent;
                            if (qi.LibraryPolicyIntent != null)
                                meta.CustomCompressionProfile = "Medium Quality (Default)";
                            UpdateRowCustomFlag(importedRow);
                        }
                        else if (qi.LibraryPolicyIntent != null &&
                                 _rowsByPath.TryGetValue(qi.Path, out DataGridViewRow? policyRow))
                        {
                            RowMeta meta = EnsureRowMeta(policyRow);
                            meta.LibraryPolicyIntent = qi.LibraryPolicyIntent;
                            meta.CustomCompressionProfile = "Medium Quality (Default)";
                            UpdateRowCustomFlag(policyRow);
                        }
                    }
                    else
                    {
                        missing++;
                    }
                }

                // Optionally apply snapshot settings to UI (non-destructive)
                ApplySnapshotSettingsToUi(snapshot.Settings);

                toolStripStatusLabel1.Text =
                    $"Imported {added} item(s){(missing > 0 ? $", {missing} missing file(s) skipped" : "")} from {Path.GetFileName(ofd.FileName)}";

                // Auto-refresh estimates once imported
                SafeRefreshEstimates();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed:\n{ex.Message}", "Import Queue",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task<DvdImportOptions?> RestoreDvdQueueItemAsync(
            DvdQueueItem saved)
        {
            if (string.IsNullOrWhiteSpace(saved.VideoTsFolder) ||
                !Directory.Exists(saved.VideoTsFolder) ||
                string.IsNullOrWhiteSpace(saved.TitleSetId) ||
                string.IsNullOrWhiteSpace(saved.OutputPath))
            {
                return null;
            }

            try
            {
                var probeService = new FfprobeService(
                    AppPaths.InstallDirectory,
                    _config.FfprobePath);
                var analysisService = new DvdFolderAnalysisService(probeService);
                DvdFolderAnalysisResult analysis = await analysisService.AnalyzeAsync(
                    saved.VideoTsFolder);
                DvdTitleCandidate? candidate = analysis.Candidates.FirstOrDefault(
                    item => item.TitleSetId.Equals(
                        saved.TitleSetId,
                        StringComparison.OrdinalIgnoreCase));
                if (candidate?.IsValidForConversion != true)
                    return null;

                return new DvdImportOptions
                {
                    Candidate = candidate,
                    OutputMode = DvdOutputMode.EncodeUsingCurrentSettings,
                    OutputPath = saved.OutputPath,
                    SelectedAudioStreamIndexes =
                        saved.SelectedAudioStreamIndexes?.ToArray() ??
                        Array.Empty<int>(),
                    SelectedSubtitleStreamIndexes =
                        saved.SelectedSubtitleStreamIndexes?.ToArray() ??
                        Array.Empty<int>()
                };
            }
            catch (Exception ex)
            {
                ErrorLogService.Append(
                    AppPaths.InstallDirectory,
                    "DVD queue item restore failed",
                    saved.VideoTsFolder,
                    ex,
                    $"Title set: {saved.TitleSetId}{Environment.NewLine}" +
                    $"Output: {saved.OutputPath}");
                return null;
            }
        }
    }
}
