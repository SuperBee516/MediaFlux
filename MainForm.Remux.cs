using System.Diagnostics;
using System.Text;
using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux
{
    public partial class MainForm
    {
        private CancellationTokenSource? _mediaRemuxCts;

        private async void RemuxSelectedToMkv_Click(object? sender, EventArgs e)
        {
            if (_mediaRemuxCts != null)
            {
                _mediaRemuxCts.Cancel();
                return;
            }

            if (_encodingActive)
            {
                ShowStatusInfo("Finish or stop the active encode before remuxing queue files.");
                return;
            }
            if (_deepAnalysisCts != null || _sampleComparisonCts != null)
            {
                ShowStatusInfo("Finish or cancel the active analysis before remuxing queue files.");
                return;
            }
            if (!EnsureFfmpegToolsAvailable())
                return;

            var rows = dgvEncodeQueue.SelectedRows
                .Cast<DataGridViewRow>()
                .Where(row =>
                    !row.IsNewRow &&
                    row.Tag is not RowMeta { IsDvdEncode: true } &&
                    !string.IsNullOrWhiteSpace(GetPathFromRow(row)) &&
                    File.Exists(GetPathFromRow(row)!))
                .OrderBy(row => row.Index)
                .ToList();
            if (rows.Count == 0)
            {
                ShowStatusInfo("Select one or more normal video files to remux.");
                return;
            }

            string configuredOutputFolder = cmbEncodeOutput?.Text?.Trim() ?? "";
            if (!ValidateOutputFolderAgainstWatchFolder(
                    configuredOutputFolder,
                    showMessage: true))
            {
                return;
            }

            int recommended = rows.Count(row =>
                (row.Tag as RowMeta)?.EncodeRecommendation?.Kind ==
                SmartEncodeRecommendationKind.RemuxOnly);
            int other = rows.Count - recommended;
            string outputDescription = string.IsNullOrWhiteSpace(configuredOutputFolder)
                ? "beside each source file"
                : $"in:\r\n{configuredOutputFolder}";
            DialogResult confirmation = MessageBox.Show(
                this,
                $"Remux {rows.Count:N0} selected file(s) to MKV {outputDescription}?\r\n\r\n" +
                $"Recommended for remux: {recommended:N0}\r\n" +
                $"Other selected files: {other:N0}\r\n\r\n" +
                "This copies video, audio, subtitles, metadata, chapters, and attachments " +
                "without re-encoding. Output names are collision-safe. Source files are never deleted, " +
                "and MediaFlux will not fall back to encoding if a stream is incompatible.",
                "Remux Selected to MKV",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);
            if (confirmation != DialogResult.OK)
                return;

            _mediaRemuxCts = new CancellationTokenSource();
            CancellationToken token = _mediaRemuxCts.Token;
            btnStartEncode.Enabled = false;
            btnSampleComparison.Enabled = false;
            if (_analyzeQueueButton != null)
                _analyzeQueueButton.Enabled = false;
            SetQueueWorkCancelVisible(true);
            SetQueueProgress(0, rows.Count, visible: true);

            int succeeded = 0;
            int failed = 0;
            int canceled = 0;
            DataGridViewRow? currentRow = null;
            try
            {
                for (int index = 0; index < rows.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    DataGridViewRow row = rows[index];
                    currentRow = row;
                    string sourcePath = GetPathFromRow(row)!;
                    string outputFolder = string.IsNullOrWhiteSpace(configuredOutputFolder)
                        ? Path.GetDirectoryName(sourcePath) ?? ""
                        : configuredOutputFolder;
                    string requestedOutput = Path.Combine(
                        outputFolder,
                        Path.GetFileNameWithoutExtension(sourcePath) + "_remux.mkv");
                    string outputPath = OutputPathService.GetCollisionSafePath(
                        requestedOutput);
                    DateTime startUtc = DateTime.UtcNow;
                    var diagnosticLog = new StringBuilder();
                    var service = new MediaRemuxService(
                        AppPaths.InstallDirectory,
                        _config.FfmpegPath,
                        _config.FfprobePath,
                        line =>
                        {
                            diagnosticLog.AppendLine(line);
                            Debug.WriteLine(line);
                        });

                    SetEncodeRowState(
                        row,
                        "Encoding",
                        "0%",
                        "--:--:--",
                        "Remuxing streams to MKV without re-encoding.");
                    var remuxStopwatch = Stopwatch.StartNew();
                    var progress = new Progress<MediaRemuxProgress>(item =>
                    {
                        string progressText = item.Percent.HasValue
                            ? $"{item.Percent.Value:0}%"
                            : "";
                        string etaText = "--:--:--";
                        if (item.Percent is > 0 and < 100)
                        {
                            double totalSeconds =
                                remuxStopwatch.Elapsed.TotalSeconds /
                                (item.Percent.Value / 100d);
                            etaText = FormatRemaining(
                                TimeSpan.FromSeconds(
                                    Math.Max(
                                        0,
                                        totalSeconds -
                                        remuxStopwatch.Elapsed.TotalSeconds)));
                        }
                        SetEncodeRowState(
                            row,
                            "Encoding",
                            progressText,
                            etaText,
                            item.Status);
                        string status =
                            $"{Path.GetFileName(sourcePath)}: {item.Status} " +
                            $"({index + 1} of {rows.Count})";
                        lblEncodeStatus.Text = status;
                        toolStripStatusLabel1.Text = status;
                    });

                    MediaRemuxResult result = await service.RemuxAsync(
                        new MediaRemuxRequest
                        {
                            SourcePath = sourcePath,
                            OutputPath = outputPath
                        },
                        progress,
                        token);
                    remuxStopwatch.Stop();

                    string operationLog =
                        $"Command: {result.DiagnosticCommand}{Environment.NewLine}" +
                        diagnosticLog +
                        result.DiagnosticOutput;
                    if (result.WasCanceled)
                    {
                        canceled++;
                        AppendMediaRemuxHistory(
                            sourcePath,
                            outputPath,
                            startUtc,
                            JobStatus.Canceled,
                            result,
                            operationLog);
                        SetEncodeRowState(
                            row,
                            "Canceled",
                            "Canceled",
                            "",
                            result.ErrorMessage);
                        break;
                    }

                    if (!result.Success)
                    {
                        failed++;
                        string logPath = ErrorLogService.Append(
                            AppPaths.InstallDirectory,
                            "Media remux failed",
                            sourcePath,
                            details:
                            $"Output  : {outputPath}{Environment.NewLine}" +
                            $"Command : {result.DiagnosticCommand}{Environment.NewLine}" +
                            $"Error   : {result.ErrorMessage}{Environment.NewLine}" +
                            $"Cleanup : {result.CleanupMessage}{Environment.NewLine}{Environment.NewLine}" +
                            result.DiagnosticOutput);
                        AppendMediaRemuxHistory(
                            sourcePath,
                            outputPath,
                            startUtc,
                            JobStatus.Failed,
                            result,
                            $"Central error log: {logPath}{Environment.NewLine}" +
                            operationLog);
                        SetEncodeRowState(
                            row,
                            "Failed",
                            "Failed",
                            "",
                            result.ErrorMessage);
                    }
                    else
                    {
                        succeeded++;
                        AppendMediaRemuxHistory(
                            sourcePath,
                            result.OutputPath,
                            startUtc,
                            JobStatus.Success,
                            result,
                            operationLog);
                        RememberCompletedEncodePaths(
                            sourcePath,
                            result.OutputPath);
                        RemoveRowAndCleanup(row);
                    }

                    currentRow = null;
                    SetQueueProgress(index + 1, rows.Count, visible: true);
                }
            }
            catch (OperationCanceledException)
            {
                canceled++;
                if (currentRow?.DataGridView == dgvEncodeQueue)
                {
                    SetEncodeRowState(
                        currentRow,
                        "Canceled",
                        "Canceled",
                        "",
                        "Remux canceled. The source file was not changed.");
                }
            }
            finally
            {
                _mediaRemuxCts.Dispose();
                _mediaRemuxCts = null;
                SetQueueProgress(0, 0, visible: false);
                SetQueueWorkCancelVisible(false);
                btnStartEncode.Enabled = !_encodingActive;
                btnSampleComparison.Enabled = !_encodingActive;
                UpdateAnalyzeQueueButtonState();
                if (!_encodingActive)
                    lblEncodeStatus.Text = string.Empty;
                UpdateSizeTotals();
                UpdateSelectionSizeTotals();
                LoadHistoryGrid();
            }

            string summary =
                $"Remux finished: {succeeded:N0} succeeded, {failed:N0} failed" +
                (canceled > 0 ? $", {canceled:N0} canceled" : "") + ". " +
                "Source files were not deleted.";
            ShowStatusInfo(summary);
        }

        private void AppendMediaRemuxHistory(
            string sourcePath,
            string outputPath,
            DateTime startUtc,
            JobStatus status,
            MediaRemuxResult result,
            string log)
        {
            try
            {
                string notes = status == JobStatus.Success
                    ? "Lossless MKV stream copy completed and passed FFprobe validation. Source deletion disabled."
                    : result.ErrorMessage +
                      (result.CleanupSucceeded
                          ? " Incomplete output was removed. Source deletion disabled."
                          : $" Cleanup warning: {result.CleanupMessage} Source deletion disabled.");

                lock (_historyLock)
                {
                    _historyService.Append(new JobHistoryRecord
                    {
                        Type = JobType.Remux,
                        Status = status,
                        StartUtc = startUtc,
                        EndUtc = DateTime.UtcNow,
                        SourcePath = sourcePath,
                        OutputPath = outputPath,
                        EncoderMode = "Lossless MKV Remux (Stream Copy)",
                        DurationSec =
                            (dgvEncodeQueue.Rows.Cast<DataGridViewRow>()
                                .FirstOrDefault(row =>
                                    string.Equals(
                                        GetPathFromRow(row),
                                        sourcePath,
                                        StringComparison.OrdinalIgnoreCase))
                                ?.Tag as RowMeta)?.DurationSec,
                        Log = log,
                        Notes = notes,
                        SourceSizeBytes = TryGetFileSizeBytes(sourcePath),
                        OutputSizeBytes = TryGetFileSizeBytes(outputPath),
                        ErrorSummary = status == JobStatus.Success
                            ? null
                            : result.ErrorMessage
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Media remux history append failed: {ex}");
            }
        }

        private static string FormatRemaining(TimeSpan remaining)
        {
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;
            return remaining.ToString(@"hh\:mm\:ss");
        }
    }
}
