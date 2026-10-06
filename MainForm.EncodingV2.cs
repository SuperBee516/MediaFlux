using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.EncodingV2;
using MediaFlux.Services.EncodingV2Gui;
using System.Diagnostics;
using System.Text;

namespace MediaFlux;

public partial class MainForm
{
    private async Task CompleteV2GuiEncodeAsync(
        DataGridViewRow row,
        RowMeta meta,
        V2GuiPreparedEncode prepared,
        double durationSeconds,
        string displayName,
        long? sourceSizeBytes,
        DateTime jobStartUtc,
        StringBuilder jobLog,
        EncodeLifecycleDiagnostics lifecycle,
        CancellationToken cancellationToken)
    {
        void Log(string line)
        {
            lock (jobLog) jobLog.AppendLine(line);
            meta.AppendInspectorLogLine(line);
        }

        DateTime? v2ItemDispatchUtc = null;
        void Observe(V2EncodingQueueEvent queueEvent)
        {
            if (queueEvent.Type == V2EncodingQueueEventType.ItemStarted)
                v2ItemDispatchUtc = queueEvent.TimestampUtc;
            V2LifecycleEvent? lifecycleEvent = queueEvent.ItemEvent;
            if (lifecycleEvent is null)
            {
                if (queueEvent.Type == V2EncodingQueueEventType.ItemStarted)
                    ApplyV2UiState(row, displayName, "Queued", "0%", "--:--:--", "Queued for V2 production.");
                return;
            }

            if (lifecycleEvent.Stage.Equals("ffmpeg.process.started", StringComparison.Ordinal))
                lifecycle.Record(EncodeLifecycleEvent.FfmpegStart, lifecycleEvent.TimestampUtc);

            string status = lifecycleEvent.State switch
            {
                V2LifecycleState.Queued => "Queued",
                V2LifecycleState.Probing => "Probing",
                V2LifecycleState.Planned => "Queued",
                V2LifecycleState.Encoding => "Encoding",
                V2LifecycleState.Validating => "Validating",
                V2LifecycleState.Promoting => "Finalizing",
                V2LifecycleState.Completed => "Completed",
                V2LifecycleState.Canceled => "Canceled",
                V2LifecycleState.Failed => "Failed",
                _ => "Encoding"
            };
            string progressText = "";
            string etaText = "--:--:--";
            if (lifecycleEvent.FfmpegProgress is { } progress)
            {
                if (progress.PercentComplete is double percent && double.IsFinite(percent))
                    progressText = Math.Clamp(percent, 0, 100).ToString("0.#") + "%";
                if (durationSeconds > 0 && progress.OutputTimeSeconds is double outputSeconds &&
                    progress.SpeedMultiplier is double speed && speed > 0)
                {
                    double remaining = Math.Max(0, durationSeconds - outputSeconds) / speed;
                    etaText = TimeSpan.FromSeconds(Math.Min(remaining, TimeSpan.MaxValue.TotalSeconds)).ToString(@"hh\:mm\:ss");
                }
            }
            else if (lifecycleEvent.State is V2LifecycleState.Queued or V2LifecycleState.Probing or
                     V2LifecycleState.Planned or V2LifecycleState.Validating or V2LifecycleState.Promoting)
            {
                progressText = lifecycleEvent.State is V2LifecycleState.Queued or V2LifecycleState.Probing ? "0%" : "";
            }

            meta.CurrentProcessingStage = status;
            if (!lifecycleEvent.Stage.Equals("ffmpeg.progress.updated", StringComparison.Ordinal))
                Log($"[V2] {lifecycleEvent.Stage}: {lifecycleEvent.Message}");
            ApplyV2UiState(row, displayName, status, progressText, etaText, lifecycleEvent.Message);
        }

        V2GuiQueueItemResult run = await _v2GuiProductionAdapter.RunAsync(prepared, Observe, cancellationToken);
        V2ExecutionResult execution = run.Execution;
        FrozenEncodePlan plan = run.PreflightPlan;
        DateTime jobEndUtc = DateTime.UtcNow;
        lifecycle.Record(EncodeLifecycleEvent.QueueCompleted, jobEndUtc);
        EncodeLifecycleTiming lifecycleTiming = lifecycle.Snapshot(
            prepared.Request.SourcePath, prepared.Request.OutputPath,
            execution.OutputPromoted ? execution.OutputSizeBytes : null);
        double wallSeconds = Math.Max(0, (jobEndUtc - jobStartUtc).TotalSeconds);
        meta.StatisticsProcessingSeconds += wallSeconds;
        Log($"[MediaFlux] Engine result: V2; queue-state={run.Queue.State}; item-state={execution.State}; " +
            $"productionAttempts={execution.ProductionAttemptCount}; validated={execution.OutputValidated}; " +
            $"promoted={execution.OutputPromoted}; totalQueueMs={run.Queue.Timings.TotalMilliseconds:0.##}; " +
            $"encodeMs={execution.Timings.EncodeMilliseconds:0.##}; " +
            $"guiRequestToV2DispatchMs={FormatMillisecondsBetween(lifecycleTiming.QueueDispatchUtc, v2ItemDispatchUtc)}; " +
            $"v2DispatchToFfmpegStartMs={FormatMillisecondsBetween(v2ItemDispatchUtc, lifecycleTiming.FfmpegStartUtc)}; " +
            $"executeRequestToFfmpegStartMs={execution.Timings.ExecuteRequestToProcessStartMilliseconds:0.##}; " +
            $"previousCompletionToDispatchMs={FormatMilliseconds(lifecycleTiming.PreviousCompletionToDispatchSeconds)}.");
        foreach (string warning in execution.Warnings)
            Log("[V2] Warning: " + warning);
        if (execution.Failure is { } failure)
            Log($"[V2] Failure: {failure.Code}: {failure.Message}");

        long? durableOutputSize = execution.OutputPromoted ? execution.OutputSizeBytes : null;
        bool completed = execution.State == V2LifecycleState.Completed && execution.OutputValidated && execution.OutputPromoted;
        bool canceled = execution.State == V2LifecycleState.Canceled;
        EncodingTerminalResult terminal = completed
            ? EncodingTerminalResult.Completed
            : canceled
                ? EncodingTerminalResult.Canceled
                : execution.Failure?.Code.Contains("Validation", StringComparison.OrdinalIgnoreCase) == true
                    ? EncodingTerminalResult.ValidationFailed
                    : execution.Failure?.Code.Contains("Promotion", StringComparison.OrdinalIgnoreCase) == true
                        ? EncodingTerminalResult.FinalizationFailed
                        : EncodingTerminalResult.EncodeFailed;
        JobStatus historyStatus = completed ? JobStatus.Success : canceled ? JobStatus.Canceled : JobStatus.Failed;
        string deletionResult = !execution.SourceDeletionRequested
            ? "Source retained because deletion was not requested."
            : execution.SourceDeleted == true
                ? "Source deleted after validated output promotion."
                : execution.Warnings.LastOrDefault() ?? "Source retained because requested deletion did not complete.";
        string summary = completed
            ? execution.Warnings.Count == 0 ? "V2 output validated and promoted." : "V2 output validated and promoted with a warning."
            : canceled ? "V2 job canceled; source retained." : execution.Failure?.Message ?? "V2 job failed.";

        var historyRecord = new JobHistoryRecord
        {
            Id = meta.StatisticsOperationId,
            Type = JobType.Encode,
            Status = historyStatus,
            StartUtc = meta.StatisticsStartUtc == default ? jobStartUtc : meta.StatisticsStartUtc,
            EndUtc = jobEndUtc,
            SourcePath = prepared.Request.SourcePath,
            OutputPath = prepared.Request.OutputPath,
            EncoderMode = plan.Encoder,
            DurationSec = durationSeconds > 0 ? durationSeconds : null,
            Log = jobLog.ToString(),
            Notes = $"Engine=V2; {summary} {deletionResult}",
            SourceSizeBytes = sourceSizeBytes ?? prepared.SourceFacts.SourceLength,
            OutputSizeBytes = durableOutputSize,
            ErrorSummary = completed ? null : execution.Failure?.Message,
            FinalizationOutcome = completed ? "Promoted" : execution.State.ToString(),
            StagingPath = execution.StagingPath,
            SourceDeletionResult = deletionResult,
            RequestedOutputContainer = prepared.ContainerDecision.Requested.ToString(),
            ResolvedOutputContainer = plan.Container == "mkv" ? "Matroska" : "Mp4",
            ContainerDecisionReason = prepared.ContainerDecision.Reason,
            TerminalResult = terminal
        };
        try
        {
            lock (_historyLock) _historyService.AppendEncodingOutcome(historyRecord, null);
        }
        catch (Exception ex) { Debug.WriteLine("V2 history append failed: " + ex); }

        var statisticsRecord = new EncodingStatisticsRecord
        {
            Id = meta.StatisticsOperationId,
            StartUtc = historyRecord.StartUtc,
            EndUtc = jobEndUtc,
            Outcome = completed ? EncodingStatisticsOutcome.Success : canceled
                ? EncodingStatisticsOutcome.Cancelled
                : terminal == EncodingTerminalResult.ValidationFailed ? EncodingStatisticsOutcome.ValidationFailed
                : terminal == EncodingTerminalResult.FinalizationFailed ? EncodingStatisticsOutcome.PromotionFailed
                : EncodingStatisticsOutcome.Failed,
            SourcePath = prepared.Request.SourcePath,
            OutputPath = prepared.Request.OutputPath,
            Codec = plan.Codec,
            Encoder = plan.Encoder,
            SourceSizeBytes = sourceSizeBytes ?? prepared.SourceFacts.SourceLength,
            OutputSizeBytes = durableOutputSize,
            MediaDurationSeconds = durationSeconds > 0 ? durationSeconds : null,
            ProcessingSeconds = wallSeconds,
            ProductionEncodeCount = execution.ProductionAttemptCount,
            EncoderId = plan.Encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase)
                ? VideoEncoderIds.Nvenc : plan.Encoder,
            EncoderPreset = plan.Preset,
            OutputBitDepth = 8,
            ScalingApplied = plan.Width != prepared.SourceFacts.SourceWidth || plan.Height != prepared.SourceFacts.SourceHeight,
            TerminalResult = terminal.ToString(),
            Notes = "Engine=V2; fixed-quality production; Storage Savings was advisory and had no acceptance authority."
        };
        try
        {
            if (_encodingStatisticsService.AppendFinalized(statisticsRecord))
                Ui(RefreshEncodingStatistics);
        }
        catch (Exception ex) { Debug.WriteLine("V2 statistics append failed: " + ex); }

        if (completed)
        {
            System.Threading.Interlocked.Increment(ref _encodeSucceededCount);
            if (durableOutputSize is > 0)
            {
                double finalMb = durableOutputSize.Value / (1024d * 1024d);
                _estimatedSizeMap[meta.Path] = finalMb;
                _queueTotalsDirty = true;
                UiInvoke(() =>
                {
                    if (row.DataGridView == dgvEncodeQueue)
                    {
                        row.Cells["colEstimatedSize"].Value = FormatSize(finalMb);
                        row.Cells["colEstimatedSize"].Tag = new Tuple<double, double>(meta.SrcMb, finalMb);
                        row.Cells["colEstimatedSize"].ToolTipText = "Actual validated final output size.";
                    }
                });
            }
            Interlocked.Exchange(ref _lastQueueCompletedTicks, jobEndUtc.Ticks);
            UiInvoke(() =>
            {
                if (row.DataGridView != dgvEncodeQueue) return;
                SetEncodeRowState(row, execution.Warnings.Count == 0 ? "Completed" : "Completed with warning",
                    "100%", "00:00:00", summary + " " + deletionResult);
                row.Cells["colProgress"].ToolTipText = summary + " " + deletionResult;
                lblEncodeStatus.Text = $"Completed: {displayName}. Continuing queue.";
                UpdateOperationProgressPresentation();
            });
            try
            {
                _mediaInfoService.Invalidate(prepared.Request.OutputPath);
                RememberCompletedEncodePaths(prepared.Request.SourcePath, prepared.Request.OutputPath);
                UiInvoke(() =>
                {
                    RemoveRowAndCleanup(row, allowCompletedEncodeJob: true);
                    RescanInputFolderAndMerge(recomputeEstimates: false);
                    SafeRefreshEstimates();
                    UpdateSizeTotals();
                    UpdateSelectionSizeTotals();
                    ClearEncodeInputFolderIfQueueEmptyAfterProcessing();
                });
            }
            catch (Exception ex) { Debug.WriteLine($"V2 post-encode queue cleanup failed for {prepared.Request.SourcePath}: {ex}"); }
        }
        else
        {
            if (!canceled) System.Threading.Interlocked.Increment(ref _encodeFailedCount);
            Interlocked.Exchange(ref _lastQueueCompletedTicks, jobEndUtc.Ticks);
            if (!canceled)
            {
                try
                {
                    ErrorLogService.Append(Application.StartupPath, "V2 encode job failed",
                        prepared.Request.SourcePath,
                        new InvalidOperationException(execution.Failure?.Message ?? "V2 execution failed."),
                        jobLog.ToString());
                }
                catch (Exception ex) { Debug.WriteLine("V2 error log append failed: " + ex); }
            }
            UiInvoke(() =>
            {
                if (row.DataGridView == dgvEncodeQueue)
                {
                    SetEncodeRowState(row, canceled ? "Canceled" : terminal == EncodingTerminalResult.ValidationFailed
                        ? "Validation Failed" : "Failed", canceled ? "Canceled" : "Failed", "", summary + " " + deletionResult);
                    row.Cells["colProgress"].ToolTipText = summary + " " + deletionResult;
                }
                lblEncodeStatus.Text = canceled
                    ? $"Canceled: {displayName}. Continuing queue."
                    : $"Failed: {displayName}. Continuing queue.";
                toolStripStatusLabel1.Text = canceled ? "V2 encode canceled; source retained." : "V2 encode failed; source retained where promotion did not complete.";
                UpdateOperationProgressPresentation();
            });
        }
    }

    private void ApplyV2UiState(DataGridViewRow row, string displayName, string status,
        string progress, string eta, string detail)
    {
        UiInvoke(() =>
        {
            if (row.DataGridView != dgvEncodeQueue) return;
            SetEncodeRowState(row, status, progress, eta, detail);
            lblEncodeStatus.Text = $"{status}: {displayName}";
            UpdateOperationProgressPresentation();
        });
    }

    private static string FormatMilliseconds(double? seconds) => seconds is double value
        ? (value * 1000).ToString("0.##")
        : "n/a";

    private static string FormatMillisecondsBetween(DateTime? startUtc, DateTime? endUtc) =>
        startUtc is DateTime start && endUtc is DateTime end && end >= start
            ? (end - start).TotalMilliseconds.ToString("0.##")
            : "n/a";
}
