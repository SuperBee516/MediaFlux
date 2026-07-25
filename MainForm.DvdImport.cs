using System.Diagnostics;
using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux
{
    public partial class MainForm
    {
        private void InitializeDvdImportMenu()
        {
            var importDvdFolderItem = new ToolStripMenuItem("Import DVD Folder…")
            {
                ToolTipText = "Inspect an accessible VIDEO_TS folder and combine a selected DVD title."
            };
            importDvdFolderItem.Click += ImportDvdFolder_Click;
            fileToolStripMenuItem.DropDownItems.Insert(0, importDvdFolderItem);
            fileToolStripMenuItem.DropDownItems.Insert(1, new ToolStripSeparator());
        }

        private void ImportDvdFolder_Click(object? sender, EventArgs e)
        {
            if (!EnsureFfmpegToolsAvailable())
                return;

            string initialFolder = Directory.Exists(_config.LastDvdInputFolder)
                ? _config.LastDvdInputFolder
                : _config.LastInputFolders.FirstOrDefault(Directory.Exists) ??
                  Application.StartupPath;
            using var folderDialog = new FolderBrowserDialog
            {
                Description =
                    "Select a VIDEO_TS folder or the parent folder containing VIDEO_TS.",
                SelectedPath = initialFolder,
                ShowNewFolderButton = false
            };
            if (folderDialog.ShowDialog(this) != DialogResult.OK)
                return;

            _config.LastDvdInputFolder = folderDialog.SelectedPath;
            _config.Save(_configPath);
            AnalyzeAndImportDvdFolder(folderDialog.SelectedPath);
        }

        private void AnalyzeAndImportDvdFolder(string selectedFolder)
        {
            var probeService = new FfprobeService(
                AppPaths.InstallDirectory,
                _config.FfprobePath);
            var analysisService = new DvdFolderAnalysisService(probeService);
            using var analysisProgress = new DvdConversionProgressForm(
                "Analyzing DVD Structure",
                async (operationProgress, cancellationToken) =>
                {
                    var dvdProgress = new Progress<DvdAnalysisProgress>(update =>
                    {
                        double? percent = update.TotalSegments > 0
                            ? Math.Clamp(
                                update.CompletedSegments / (double)update.TotalSegments * 100d,
                                0,
                                100)
                            : null;
                        operationProgress.Report(new DvdOperationProgress
                        {
                            Status = string.IsNullOrWhiteSpace(update.Status)
                                ? "Analyzing DVD structure"
                                : update.Status,
                            Percent = percent
                        });
                    });
                    return await analysisService.AnalyzeAsync(
                        selectedFolder,
                        dvdProgress,
                        cancellationToken);
                });

            DialogResult analysisDialogResult = analysisProgress.ShowDialog(this);
            if (analysisDialogResult == DialogResult.Cancel || analysisProgress.WasCanceled)
            {
                toolStripStatusLabel1.Text = "DVD folder analysis canceled.";
                return;
            }
            if (analysisProgress.OperationException != null)
            {
                string logPath = ErrorLogService.Append(
                    AppPaths.InstallDirectory,
                    "DVD folder analysis failed",
                    selectedFolder,
                    analysisProgress.OperationException);
                MessageBox.Show(
                    this,
                    $"MediaFlux could not analyze the DVD folder.\r\n\r\n" +
                    $"{analysisProgress.OperationException.Message}\r\n\r\n" +
                    $"Details were written to:\r\n{logPath}",
                    "DVD Analysis Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            if (analysisProgress.OperationResult is not DvdFolderAnalysisResult analysis)
                return;
            if (!string.IsNullOrWhiteSpace(analysis.ErrorMessage))
            {
                MessageBox.Show(
                    this,
                    analysis.ErrorMessage,
                    "Import DVD Folder",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            if (analysis.Candidates.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "No valid DVD title sets were found.",
                    "Import DVD Folder",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            string initialOutputFolder = !string.IsNullOrWhiteSpace(_config.LastDvdOutputFolder)
                ? _config.LastDvdOutputFolder
                : !string.IsNullOrWhiteSpace(cmbEncodeOutput.Text)
                    ? cmbEncodeOutput.Text
                : _config.LastOutputFolders.FirstOrDefault(Directory.Exists) ?? "";
            using var selectionDialog = new DvdTitleSelectionForm(
                analysis,
                initialOutputFolder,
                _config.GetLastDvdOutputMode(),
                _config.DvdOutputNamingPattern);
            if (selectionDialog.ShowDialog(this) != DialogResult.OK ||
                selectionDialog.Options == null)
            {
                return;
            }

            DvdImportOptions options = selectionDialog.Options;
            _config.LastDvdInputFolder = selectedFolder;
            _config.LastDvdOutputFolder =
                Path.GetDirectoryName(options.OutputPath) ?? initialOutputFolder;
            _config.SetLastDvdOutputMode(options.OutputMode);
            _config.Save(_configPath);
            if (options.OutputMode == DvdOutputMode.EncodeUsingCurrentSettings)
            {
                QueueDvdEncode(options);
                return;
            }

            RunDvdRemux(options);
        }

        private void RunDvdRemux(DvdImportOptions options)
        {
            DateTime jobStartUtc = DateTime.UtcNow;
            var diagnosticLog = new System.Text.StringBuilder();
            var probeService = new FfprobeService(
                AppPaths.InstallDirectory,
                _config.FfprobePath);
            var validator = new DvdOutputValidationService(probeService);
            var manifestBuilder = new DvdConcatManifestBuilder(AppPaths.TempDirectory);
            var remuxService = new DvdRemuxService(
                AppPaths.InstallDirectory,
                _config.FfmpegPath,
                manifestBuilder,
                validator,
                line =>
                {
                    diagnosticLog.AppendLine(line);
                    Debug.WriteLine(line);
                });

            using var progressDialog = new DvdConversionProgressForm(
                "Combining DVD Title",
                async (progress, cancellationToken) =>
                    await remuxService.RemuxAsync(
                        options,
                        progress,
                        cancellationToken));
            DialogResult dialogResult = progressDialog.ShowDialog(this);
            if (dialogResult == DialogResult.Cancel || progressDialog.WasCanceled)
            {
                AppendDvdRemuxHistory(
                    options,
                    JobStatus.Canceled,
                    jobStartUtc,
                    options.OutputPath,
                    "Canceled by user. Temporary files were cleaned up; source deletion was disabled.",
                    diagnosticLog.ToString());
                toolStripStatusLabel1.Text =
                    "DVD remux canceled; incomplete output and temporary files were removed.";
                MessageBox.Show(
                    this,
                    "The operation was canceled. MediaFlux removed its incomplete output " +
                    "and temporary manifest. The DVD source folder was not changed.",
                    "DVD Remux Canceled",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            if (progressDialog.OperationException != null)
            {
                string logPath = ErrorLogService.Append(
                    AppPaths.InstallDirectory,
                    "DVD remux failed",
                    options.Candidate.Segments.FirstOrDefault()?.Path,
                    progressDialog.OperationException);
                AppendDvdRemuxHistory(
                    options,
                    JobStatus.Failed,
                    jobStartUtc,
                    options.OutputPath,
                    progressDialog.OperationException.Message,
                    diagnosticLog.ToString(),
                    logPath);
                MessageBox.Show(
                    this,
                    $"DVD remuxing failed. MediaFlux did not automatically re-encode the video." +
                    $"\r\n\r\n{progressDialog.OperationException.Message}" +
                    $"\r\n\r\nDetails were written to:\r\n{logPath}",
                    "DVD Remux Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            if (progressDialog.OperationResult is not DvdRemuxResult result)
                return;
            if (result.WasCanceled)
            {
                string cancellationNotes = result.CleanupSucceeded
                    ? "Canceled by user. Temporary files and incomplete output were removed; source deletion was disabled."
                    : $"Canceled by user. Cleanup needs attention: {result.CleanupMessage}";
                AppendDvdRemuxHistory(
                    options,
                    JobStatus.Canceled,
                    jobStartUtc,
                    options.OutputPath,
                    cancellationNotes,
                    BuildDvdRemuxDiagnosticLog(
                        options,
                        result,
                        diagnosticLog.ToString()));
                toolStripStatusLabel1.Text = result.CleanupSucceeded
                    ? "DVD remux canceled; incomplete output and temporary files were removed."
                    : "DVD remux canceled; temporary cleanup needs attention.";
                MessageBox.Show(
                    this,
                    result.CleanupSucceeded
                        ? "The operation was canceled. MediaFlux removed its incomplete output " +
                          "and temporary manifest. The DVD source folder was not changed."
                        : "The operation was canceled and the DVD source folder was not changed." +
                          $"\r\n\r\nCleanup warning: {result.CleanupMessage}",
                    "DVD Remux Canceled",
                    MessageBoxButtons.OK,
                    result.CleanupSucceeded
                        ? MessageBoxIcon.Information
                        : MessageBoxIcon.Warning);
                return;
            }
            if (!result.Success)
            {
                string logPath = ErrorLogService.Append(
                    AppPaths.InstallDirectory,
                    "DVD remux failed",
                    options.Candidate.Segments.FirstOrDefault()?.Path,
                    details:
                    $"Output     : {options.OutputPath}{Environment.NewLine}" +
                    $"Title Set  : {options.Candidate.TitleSetId}{Environment.NewLine}" +
                    $"Command    : {result.DiagnosticCommand}{Environment.NewLine}" +
                    $"Error      : {result.ErrorMessage}{Environment.NewLine}{Environment.NewLine}" +
                    result.DiagnosticOutput);
                string failureNotes = result.CleanupSucceeded
                    ? result.ErrorMessage
                    : $"{result.ErrorMessage} Cleanup warning: {result.CleanupMessage}";
                AppendDvdRemuxHistory(
                    options,
                    JobStatus.Failed,
                    jobStartUtc,
                    options.OutputPath,
                    failureNotes,
                    BuildDvdRemuxDiagnosticLog(
                        options,
                        result,
                        diagnosticLog.ToString()),
                    logPath);
                MessageBox.Show(
                    this,
                    $"{result.ErrorMessage}\r\n\r\nDetails were written to:\r\n{logPath}",
                    "DVD Remux Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                toolStripStatusLabel1.Text = "DVD remux failed; source files were not changed.";
                return;
            }

            AppendDvdRemuxHistory(
                options,
                JobStatus.Success,
                jobStartUtc,
                result.OutputPath,
                result.CleanupSucceeded
                    ? "Lossless remux completed and passed FFprobe validation."
                    : "Lossless remux completed and passed FFprobe validation, but temporary cleanup needs attention: " +
                      result.CleanupMessage,
                BuildDvdRemuxDiagnosticLog(
                    options,
                    result,
                    diagnosticLog.ToString()));
            toolStripStatusLabel1.Text = $"DVD title remuxed to {result.OutputPath}";
            MessageBox.Show(
                this,
                $"The DVD title was combined successfully and verified with FFprobe." +
                $"\r\n\r\n{result.OutputPath}" +
                (result.CleanupSucceeded
                    ? ""
                    : $"\r\n\r\nCleanup warning: {result.CleanupMessage}"),
                "DVD Remux Complete",
                MessageBoxButtons.OK,
                result.CleanupSucceeded
                    ? MessageBoxIcon.Information
                    : MessageBoxIcon.Warning);
        }

        private void AppendDvdRemuxHistory(
            DvdImportOptions options,
            JobStatus status,
            DateTime startUtc,
            string outputPath,
            string notes,
            string log,
            string logPath = "")
        {
            try
            {
                DvdTitleCandidate candidate = options.Candidate;
                long? outputSize = TryGetFileSizeBytes(outputPath);

                string sourceFolder = Path.GetDirectoryName(
                    candidate.Segments[0].Path) ?? "";
                if (!string.IsNullOrWhiteSpace(logPath))
                {
                    log = $"Central error log: {logPath}{Environment.NewLine}" + log;
                }

                lock (_historyLock)
                {
                    _historyService.Append(new JobHistoryRecord
                    {
                        Type = JobType.DvdRemux,
                        Status = status,
                        StartUtc = startUtc,
                        EndUtc = DateTime.UtcNow,
                        SourcePath = sourceFolder,
                        OutputPath = outputPath,
                        EncoderMode = "Lossless Remux (Stream Copy)",
                        DurationSec = candidate.CombinedDurationSeconds,
                        Log = log,
                        LogPath = logPath,
                        Notes = notes,
                        DvdTitleSet = candidate.TitleSetId,
                        DvdSegmentCount = candidate.Segments.Count,
                        DvdOutputMode = DvdOutputMode.LosslessRemuxToMkv.ToString(),
                        SourceSizeBytes = candidate.CombinedSizeBytes,
                        OutputSizeBytes = outputSize,
                        WasRecommendedDvdTitle = candidate.IsLikelyMainFeature,
                        ErrorSummary = status == JobStatus.Success ? null : notes
                    });
                }

                Ui(LoadHistoryGrid);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DVD remux history append failed: {ex}");
            }
        }

        private static string BuildDvdRemuxDiagnosticLog(
            DvdImportOptions options,
            DvdRemuxResult result,
            string serviceLog)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine($"Title set: {options.Candidate.TitleSetId}");
            builder.AppendLine($"Segments: {options.Candidate.Segments.Count}");
            builder.AppendLine(
                $"Source duration: {options.Candidate.CombinedDurationSeconds:0.###} seconds");
            builder.AppendLine($"Source bytes: {options.Candidate.CombinedSizeBytes}");
            builder.AppendLine(
                $"Recommended main feature: {options.Candidate.IsLikelyMainFeature}");
            builder.AppendLine(
                $"Selected audio streams: {string.Join(", ", options.SelectedAudioStreamIndexes)}");
            builder.AppendLine(
                $"Selected subtitle streams: {string.Join(", ", options.SelectedSubtitleStreamIndexes)}");
            builder.AppendLine("Source deletion: disabled");
            foreach (DvdSegmentInfo segment in options.Candidate.Segments)
                builder.AppendLine($"Segment {segment.SegmentNumber}: {segment.Path}");
            if (!string.IsNullOrWhiteSpace(result.DiagnosticCommand))
                builder.AppendLine($"FFmpeg command: {result.DiagnosticCommand}");
            if (!string.IsNullOrWhiteSpace(serviceLog))
                builder.AppendLine(serviceLog.TrimEnd());
            if (!string.IsNullOrWhiteSpace(result.DiagnosticOutput))
                builder.AppendLine(result.DiagnosticOutput.TrimEnd());
            builder.AppendLine(
                $"Cleanup: {(result.CleanupSucceeded ? "succeeded" : result.CleanupMessage)}");
            return builder.ToString();
        }

        private bool QueueDvdEncode(
            DvdImportOptions options,
            bool showMessages = true)
        {
            DvdTitleCandidate candidate = options.Candidate;
            string outputFolder = Path.GetDirectoryName(options.OutputPath) ?? "";
            string sourceFolder = candidate.Segments.Count > 0
                ? Path.GetDirectoryName(candidate.Segments[0].Path) ?? ""
                : "";
            if (OutputPathService.IsPathWithinDirectory(
                    options.OutputPath,
                    sourceFolder))
            {
                if (showMessages)
                {
                    MessageBox.Show(
                        this,
                        "The output cannot be written inside the source VIDEO_TS folder. " +
                        "Choose its parent folder or another destination.",
                        "DVD Output",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return false;
            }

            if (!ValidateOutputFolderAgainstWatchFolder(
                    outputFolder,
                    showMessage: showMessages))
                return false;

            if (candidate.Segments.Count == 0 ||
                candidate.Segments.Any(segment => !File.Exists(segment.Path)))
            {
                if (showMessages)
                {
                    MessageBox.Show(
                        this,
                        "One or more DVD segments are no longer available. Re-import the DVD folder.",
                        "DVD Encode",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return false;
            }

            string queuePath = candidate.Segments[0].Path;
            DataGridViewRow? existing = dgvEncodeQueue.Rows
                .Cast<DataGridViewRow>()
                .FirstOrDefault(row =>
                    row.Tag is RowMeta meta &&
                    meta.IsDvdEncode &&
                    string.Equals(meta.Path, queuePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        meta.DvdEncodeOptions?.Candidate.TitleSetId,
                        candidate.TitleSetId,
                        StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Selected = true;
                toolStripStatusLabel1.Text =
                    $"{candidate.TitleSetId} is already in the Encode queue.";
                return false;
            }
            if (dgvEncodeQueue.Rows
                .Cast<DataGridViewRow>()
                .Any(row =>
                    row.Tag is not RowMeta { IsDvdEncode: true } &&
                    string.Equals(
                        GetPathFromRow(row),
                        queuePath,
                        StringComparison.OrdinalIgnoreCase)))
            {
                if (showMessages)
                {
                    MessageBox.Show(
                        this,
                        "The first VOB segment is already queued as an individual file. " +
                        "Remove that row before adding the complete logical DVD title.",
                        "DVD Encode",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return false;
            }

            double sourceMb = candidate.CombinedSizeBytes / (1024d * 1024d);
            string outputBaseName = Path.GetFileNameWithoutExtension(options.OutputPath);
            var meta = new RowMeta
            {
                Path = queuePath,
                DurationSec = candidate.CombinedDurationSeconds,
                Resolution = candidate.VideoWidth.HasValue && candidate.VideoHeight.HasValue
                    ? $"{candidate.VideoWidth}x{candidate.VideoHeight}"
                    : "",
                VideoCodec = candidate.VideoCodec,
                Fps = candidate.FrameRate.HasValue
                    ? (int)Math.Round(candidate.FrameRate.Value)
                    : 0,
                SrcMb = sourceMb,
                DvdEncodeOptions = options
            };

            _suppressRowEvents = true;
            int rowIndex;
            try
            {
                rowIndex = dgvEncodeQueue.Rows.Add();
            }
            finally
            {
                _suppressRowEvents = false;
            }

            DataGridViewRow row = dgvEncodeQueue.Rows[rowIndex];
            row.Tag = meta;
            row.Cells["colName"].Value =
                $"{outputBaseName} ({candidate.TitleSetId}, DVD title)";
            row.Cells["colSize"].Value = FormatSize(sourceMb);
            row.Cells["colEstimatedSize"].Value = "";
            row.Cells["colCreated"].Value = candidate.Segments
                .Select(segment => File.GetCreationTime(segment.Path))
                .DefaultIfEmpty(DateTime.Now)
                .Min()
                .ToString("yyyy-MM-dd HH:mm");
            SetEncodeRowState(
                row,
                "Queued",
                "",
                "",
                "Logical DVD title queued for encoding with the current MediaFlux settings.");
            row.Cells["colETA"].Value = "";
            if (dgvEncodeQueue.Columns.Contains("colCustom"))
                row.Cells["colCustom"].Value = "";

            _rowsByPath[queuePath] = row;
            _queueSourceSizeMap[queuePath] = sourceMb;
            _queueTotalSourceMb += sourceMb;
            _queueFileCount++;
            _queueTotalsDirty = false;

            lock (_activeEncodeQueueLock)
            {
                if (_encodingActive &&
                    _activeEncodeQueue != null &&
                    !_activeEncodeQueue.Contains(row))
                {
                    _activeEncodeQueue.Add(row);
                }
            }

            RunEstimatePass();
            UpdateAnalyzeQueueButtonState();
            string queueStatus = _encodingActive
                ? "added to the active Encode queue"
                : "added to the Encode queue";
            toolStripStatusLabel1.Text =
                $"{candidate.TitleSetId} {queueStatus} as one logical DVD title.";
            return true;
        }
    }
}
