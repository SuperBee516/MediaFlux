using MediaFlux.Models;
using MediaFlux.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace MediaFlux;

public partial class MainForm
{
    private Label? _queueInspectorSummaryTitle;
    private Label? _queueInspectorPath;
    private Label? _queueInspectorStatus;
    private Label? _queueInspectorRecommendation;
    private Label? _queueInspectorConfidence;
    private Label? _queueInspectorRationale;
    private Label? _queueInspectorSourceSize;
    private Label? _queueInspectorEstimate;
    private Label? _queueInspectorSavings;
    private Label? _queueInspectorProvenance;
    private Label? _queueInspectorOutput;
    private Label? _queueInspectorProgress;
    private Label? _queueInspectorEta;
    private Label? _queueInspectorDuplicateState;
    private TextBox? _queueInspectorMedia;
    private TextBox? _queueInspectorDiagnostics;
    private Button? _queueInspectorCopyDiagnostic;
    private string? _queueInspectorSelectionKey;
    private string? _queueInspectorPresentationKey;

    private Control CreateQueueInspectorTabs()
    {
        _encodeInfoTabs = new TabControl
        {
            Name = "queueInspectorTabs",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            TabStop = true,
            AccessibleName = "Queue item inspector",
            AccessibleDescription = "Summary, plan and analysis, cached media details, and diagnostics for selected queue items."
        };

        _encodeInfoTabs.TabPages.Add(CreateScrollableInfoTab("Summary", CreateQueueInspectorSummary()));

        Control planAndAnalysis = CreateInspectorStack(
            CreateEncodingPlanGroup(),
            CreateEncodePreviewGroup(),
            CreateEncodingOptionsDetails(),
            CreateRestorationGroup());
        _encodeInfoTabs.TabPages.Add(CreateScrollableInfoTab("Plan & Analysis", planAndAnalysis));

        Control media = CreateInspectorStack(
            CreateQueueInspectorMediaGroup(),
            CreateStreamsGroup(),
            grpDuplicateFinder);
        _encodeInfoTabs.TabPages.Add(CreateScrollableInfoTab("Media", media));

        Control diagnostics = CreateInspectorStack(
            CreateFailureAnalysisGroup(),
            CreateEncodingDiagnosticsGroup(),
            CreateEncodingStatisticsGroup(),
            CreateQueueInspectorDiagnosticReportGroup());
        _encodeInfoTabs.TabPages.Add(CreateScrollableInfoTab("Diagnostics & Logs", diagnostics));

        foreach (TabPage page in _encodeInfoTabs.TabPages)
        {
            page.AccessibleName = page.Text;
            page.AccessibleDescription = $"{page.Text} for the selected encoding queue item.";
        }

        _encodeInfoTabs.SelectedIndexChanged += (_, __) =>
        {
            if (_encodeInfoTabs.SelectedTab is TabPage selectedTab && !_applyingEncodeDropdownSettings)
            {
                _config.EncodeDetailsTab = selectedTab.Text;
                _config.Save(_configPath);
            }

            if (_encodeInfoTabs.SelectedTab?.Text == "Diagnostics & Logs")
            {
                RefreshEncodingDiagnostics();
                RefreshEncodingStatistics();
                RefreshQueueInspectorDiagnostics();
            }
            else if (_encodeInfoTabs.SelectedTab?.Text == "Media")
            {
                // This projection is cache-only. Switching tabs never starts FFprobe.
                RefreshQueueInspectorMediaForSelection();
            }
        };

        dgvEncodeQueue.CellValueChanged += QueueInspector_CellValueChanged;
        dgvEncodeQueue.RowsRemoved += (_, __) => QueueInspector_SelectionMayHaveChanged();
        RefreshQueueInspectorFromSelection();
        return _encodeInfoTabs;
    }

    private Control CreateQueueInspectorSummary()
    {
        var root = new TableLayoutPanel
        {
            Name = "queueInspectorSummary",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(4),
            Margin = Padding.Empty,
            AccessibleName = "Selected item summary"
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _queueInspectorSummaryTitle = new Label
        {
            Name = "queueInspectorSummaryTitle",
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Text = "Select a queue item to inspect it.",
            Margin = new Padding(2, 2, 2, 8),
            AccessibleName = "Selected queue item"
        };
        root.Controls.Add(_queueInspectorSummaryTitle, 0, 0);

        _queueInspectorPath = CreateInspectorValueLabel("queueInspectorPath", "No item selected.");
        _queueInspectorPath.AutoEllipsis = true;
        _queueInspectorPath.MaximumSize = new Size(1800, 0);
        _queueInspectorPath.TabStop = true;
        _queueInspectorPath.AccessibleName = "Source path";
        root.Controls.Add(_queueInspectorPath, 0, 1);

        var values = new TableLayoutPanel
        {
            Name = "queueInspectorSummaryValues",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 7,
            Margin = new Padding(0, 4, 0, 0),
            Padding = Padding.Empty
        };
        values.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        values.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        for (int row = 0; row < 7; row++)
            values.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _queueInspectorStatus = AddInspectorSummaryValue(values, "Status", "queueInspectorStatus", 0, 0);
        _queueInspectorRecommendation = AddInspectorSummaryValue(values, "Recommendation", "queueInspectorRecommendation", 1, 0);
        _queueInspectorConfidence = AddInspectorSummaryValue(values, "Confidence", "queueInspectorConfidence", 0, 1);
        _queueInspectorSourceSize = AddInspectorSummaryValue(values, "Source size", "queueInspectorSourceSize", 1, 1);
        _queueInspectorEstimate = AddInspectorSummaryValue(values, "Estimated output", "queueInspectorEstimate", 0, 2);
        _queueInspectorSavings = AddInspectorSummaryValue(values, "Estimated savings", "queueInspectorSavings", 1, 2);
        _queueInspectorProvenance = AddInspectorSummaryValue(values, "Estimate provenance", "queueInspectorProvenance", 0, 3);
        _queueInspectorOutput = AddInspectorSummaryValue(values, "Configured / planned output", "queueInspectorOutput", 1, 3);
        _queueInspectorProgress = AddInspectorSummaryValue(values, "Progress", "queueInspectorProgress", 0, 4);
        _queueInspectorEta = AddInspectorSummaryValue(values, "ETA", "queueInspectorEta", 1, 4);
        _queueInspectorDuplicateState = AddInspectorSummaryValue(values, "Duplicate state", "queueInspectorDuplicateState", 0, 5);
        _queueInspectorRationale = AddInspectorSummaryValue(values, "Recommendation rationale", "queueInspectorRationale", 0, 6);
        values.SetColumnSpan(_queueInspectorRationale.Parent!, 2);
        root.Controls.Add(values, 0, 2);
        Control queueTotals = CreateQueueSummaryGroup();
        queueTotals.Dock = DockStyle.Top;
        root.Controls.Add(queueTotals, 0, 3);
        return root;
    }

    private static Label CreateInspectorValueLabel(string name, string text) => new()
    {
        Name = name,
        AutoSize = true,
        MaximumSize = new Size(1500, 0),
        Text = text,
        Margin = new Padding(3, 2, 8, 5),
        ForeColor = SystemColors.ControlText
    };

    private static Label AddInspectorSummaryValue(
        TableLayoutPanel table,
        string caption,
        string name,
        int column,
        int row)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(7, 4, 7, 3),
            Margin = new Padding(3),
            BackColor = SystemColors.Window
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = caption.ToUpperInvariant(),
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 2)
        }, 0, 0);
        Label value = CreateInspectorValueLabel(name, "—");
        value.Margin = Padding.Empty;
        value.MaximumSize = new Size(900, 0);
        value.AccessibleName = caption;
        panel.Controls.Add(value, 0, 1);
        table.Controls.Add(panel, column, row);
        return value;
    }

    private Control CreateQueueInspectorMediaGroup()
    {
        var group = new GroupBox
        {
            Text = "Cached source media",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10),
            Margin = Padding.Empty
        };
        _queueInspectorMedia = new TextBox
        {
            Name = "queueInspectorMediaDetails",
            Dock = DockStyle.Top,
            AutoSize = true,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            MinimumSize = new Size(0, 150),
            Font = new Font("Segoe UI", 9F),
            AccessibleName = "Cached source media details",
            AccessibleDescription = "Media metadata already cached by MediaFlux; this view does not probe the source."
        };
        group.Controls.Add(_queueInspectorMedia);
        return group;
    }

    private Control CreateQueueInspectorDiagnosticReportGroup()
    {
        var group = new GroupBox
        {
            Text = "Selected job diagnostics and log",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10),
            Margin = new Padding(0, 6, 0, 0)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _queueInspectorDiagnostics = new TextBox
        {
            Name = "queueInspectorDiagnosticText",
            Dock = DockStyle.Top,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            Height = 180,
            Font = new Font("Consolas", 9F),
            AccessibleName = "Selected job technical diagnostics",
            AccessibleDescription = "Recovery, failure classification, and curated diagnostic report for the selected queue item."
        };
        layout.Controls.Add(_queueInspectorDiagnostics, 0, 0);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 4, 0, 0)
        };
        _queueInspectorCopyDiagnostic = new Button
        {
            Name = "queueInspectorCopyDiagnostic",
            Text = "Copy Diagnostic",
            AutoSize = true,
            AccessibleName = "Copy selected job diagnostic"
        };
        _queueInspectorCopyDiagnostic.Click += (_, __) =>
        {
            if (string.IsNullOrWhiteSpace(_queueInspectorDiagnostics?.Text))
                return;
            try { Clipboard.SetText(_queueInspectorDiagnostics.Text); }
            catch (Exception ex) { toolStripStatusLabel1.Text = $"Could not copy diagnostic: {ex.Message}"; }
        };
        var openLog = new Button
        {
            Name = "queueInspectorOpenErrorLog",
            Text = "View / Clear Application Logs…",
            AutoSize = true,
            AccessibleName = "Open application log viewer",
            AccessibleDescription = "Opens the existing viewer with Copy All and Clear Log actions."
        };
        openLog.Click += ViewErrorLogToolStripMenuItem_Click;
        actions.Controls.Add(_queueInspectorCopyDiagnostic);
        actions.Controls.Add(openLog);
        layout.Controls.Add(actions, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private static Control CreateInspectorStack(params Control[] controls)
    {
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = controls.Length,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int index = 0; index < controls.Length; index++)
        {
            controls[index].Dock = DockStyle.Top;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(controls[index], 0, index);
        }
        return stack;
    }

    private void RefreshQueueInspectorFromSelection()
    {
        if (_encodeInfoTabs == null || _queueInspectorSummaryTitle == null)
            return;

        DataGridViewRow[] rows = GetQueueInspectorSelectedRows();
        string selectionKey = BuildQueueInspectorSelectionKey(rows);
        string presentationKey = BuildQueueInspectorPresentationKey(rows);
        if (string.Equals(selectionKey, _queueInspectorSelectionKey, StringComparison.Ordinal) &&
            string.Equals(presentationKey, _queueInspectorPresentationKey, StringComparison.Ordinal))
        {
            return;
        }

        _queueInspectorSelectionKey = selectionKey;
        _queueInspectorPresentationKey = presentationKey;
        RefreshQueueInspectorSummary(rows);

        RefreshQueueInspectorMedia(rows);
        RefreshQueueInspectorDiagnostics(rows);
    }

    private void RefreshQueueInspectorForRow(DataGridViewRow row, bool summaryOnly = false)
    {
        if (_queueInspectorSummaryTitle == null || row == null || row.IsNewRow ||
            !IsQueueInspectorItemSelected(row))
            return;

        DataGridViewRow[] rows = GetQueueInspectorSelectedRows();
        RefreshQueueInspectorSummary(rows);
        if (summaryOnly)
            return;

        _queueInspectorPresentationKey = BuildQueueInspectorPresentationKey(rows);
        RefreshQueueInspectorMedia(rows);
        RefreshQueueInspectorDiagnostics(rows);
        UpdateFailureAnalysis(rows);
    }

    private void QueueInspector_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= dgvEncodeQueue.Rows.Count || e.ColumnIndex < 0 ||
            e.ColumnIndex >= dgvEncodeQueue.Columns.Count)
            return;

        DataGridViewRow row = dgvEncodeQueue.Rows[e.RowIndex];
        if (!IsQueueInspectorItemSelected(row))
            return;

        string column = dgvEncodeQueue.Columns[e.ColumnIndex].Name;
        RefreshQueueInspectorForRow(row, summaryOnly: column is "colProgress" or "colETA");
        if ((column is "colProgress" or "colETA") &&
            _encodeInfoTabs?.SelectedTab?.Text == "Diagnostics & Logs")
        {
            RefreshQueueInspectorDiagnostics();
        }
    }

    private void QueueInspector_SelectionMayHaveChanged()
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        try { BeginInvoke(new Action(RefreshQueueInspectorFromSelection)); }
        catch (InvalidOperationException) { }
    }

    private void RefreshQueueInspectorMediaForSelection() =>
        RefreshQueueInspectorMedia(GetQueueInspectorSelectedRows());

    private void RefreshQueueInspectorMediaForRow(DataGridViewRow row)
    {
        if (IsQueueInspectorItemSelected(row))
            RefreshQueueInspectorMedia(GetQueueInspectorSelectedRows());
    }

    private void RefreshQueueInspectorSummary(DataGridViewRow[] rows)
    {
        if (_queueInspectorSummaryTitle == null)
            return;

        if (rows.Length == 0)
        {
            SetInspectorText(_queueInspectorSummaryTitle, "Select a queue item to inspect it.");
            SetInspectorText(_queueInspectorPath, "No item selected.");
            SetInspectorText(_queueInspectorStatus, "No item selected");
            SetInspectorText(_queueInspectorRecommendation, "—");
            SetInspectorText(_queueInspectorConfidence, "—");
            SetInspectorText(_queueInspectorRationale, "—");
            SetInspectorText(_queueInspectorSourceSize, "—");
            SetInspectorText(_queueInspectorEstimate, "—");
            SetInspectorText(_queueInspectorSavings, "—");
            SetInspectorText(_queueInspectorProvenance, "—");
            SetInspectorText(_queueInspectorOutput, "—");
            SetInspectorText(_queueInspectorProgress, "—");
            SetInspectorText(_queueInspectorEta, "—");
            SetInspectorText(_queueInspectorDuplicateState, "—");
            return;
        }

        if (rows.Length > 1)
        {
            double sourceMb = rows.Sum(GetInspectorSourceSizeMb);
            double estimatedMb = rows.Sum(GetInspectorEstimatedSizeMb);
            int running = rows.Count(IsQueueRowActivelyEncoding);
            SetInspectorText(_queueInspectorSummaryTitle, $"{rows.Length:N0} queue items selected");
            SetInspectorText(_queueInspectorPath, "Multiple sources selected.");
            SetInspectorText(_queueInspectorStatus, $"{running:N0} actively encoding");
            SetInspectorText(_queueInspectorRecommendation, "Varies by item");
            SetInspectorText(_queueInspectorConfidence, "Varies by item");
            SetInspectorText(_queueInspectorRationale, "Select one item to inspect its recommendation rationale.");
            SetInspectorText(_queueInspectorSourceSize, sourceMb > 0 ? FormatSize(sourceMb) : "Not yet available");
            SetInspectorText(_queueInspectorEstimate, estimatedMb > 0 ? FormatSize(estimatedMb) : "Waiting for estimates");
            SetInspectorText(_queueInspectorSavings, sourceMb > 0 && estimatedMb > 0 ? FormatSavings(sourceMb, estimatedMb) : "Not yet available");
            SetInspectorText(_queueInspectorProvenance, "Per-item provenance varies");
            SetInspectorText(_queueInspectorOutput, "Varies by item");
            SetInspectorText(_queueInspectorProgress, running > 0 ? $"{running:N0} active item(s)" : "—");
            SetInspectorText(_queueInspectorEta, "—");
            SetInspectorText(_queueInspectorDuplicateState, $"{rows.Count(row => row.Tag is RowMeta meta && meta.ExcludedFromEncodeAsDuplicate):N0} excluded as duplicates");
            return;
        }

        DataGridViewRow row = rows[0];
        RowMeta meta = EnsureRowMeta(row);
        string path = GetFullPathFromRow(row) ?? meta.Path;
        double sourceSizeMb = GetInspectorSourceSizeMb(row);
        double estimateMb = GetInspectorEstimatedSizeMb(row);
        string status = row.Cells["colStatus"]?.Value?.ToString() ?? "Queued";
        QueueAnalysisPresentation analysis = GetQueueAnalysisPresentation(row, meta);
        string output = GetQueueWorkspacePlannedOutput(row, meta).Text;
        string recommendation = analysis.Recommendation ?? (_config.SmartRecommendationsEnabled ? "Analyzing or unavailable" : "Disabled");
        string rationale = analysis.Reasons.Count > 0
            ? string.Join(Environment.NewLine, analysis.Reasons)
            : meta.EncodeRecommendation?.PrimaryReason ?? "No recommendation rationale is available yet.";
        string provenance = GetQueueAnalysisCalibration(meta.SizePredictionCalibration ?? meta.IntelligencePlan?.SizePredictionCalibration)
            ?? FirstMeaningfulLine(meta.EstimateDiagnostic)
            ?? "Estimate source is not yet available.";
        bool active = IsQueueRowActivelyEncoding(row);
        string progress = row.Cells["colProgress"]?.Value?.ToString() ?? "";
        string eta = row.Cells["colETA"]?.Value?.ToString() ?? "";
        string duplicateState = meta.ExcludedFromEncodeAsDuplicate
            ? "Excluded from encoding as an exact duplicate"
            : meta.DuplicateGroupId is int groupId
                ? $"Duplicate candidate · group {groupId} · {meta.DuplicateConfidence} confidence"
                : "No duplicate match recorded";

        SetInspectorText(_queueInspectorSummaryTitle, Path.GetFileName(path));
        SetInspectorText(_queueInspectorPath, path);
        SetInspectorText(_queueInspectorStatus, status);
        SetInspectorText(_queueInspectorRecommendation, recommendation);
        SetInspectorText(_queueInspectorConfidence, analysis.Confidence ?? "—");
        SetInspectorText(_queueInspectorRationale, rationale);
        SetInspectorText(_queueInspectorSourceSize, sourceSizeMb > 0 ? FormatSize(sourceSizeMb) : "Not yet available");
        SetInspectorText(_queueInspectorEstimate, estimateMb > 0 ? FormatSize(estimateMb) : EstimateDisplayText(row));
        SetInspectorText(_queueInspectorSavings, sourceSizeMb > 0 && estimateMb > 0 ? FormatSavings(sourceSizeMb, estimateMb) : analysis.SavingsValue ?? "Not yet available");
        SetInspectorText(_queueInspectorProvenance, provenance);
        SetInspectorText(_queueInspectorOutput, string.IsNullOrWhiteSpace(output) ? "Configured output pending" : output);
        SetInspectorText(_queueInspectorProgress, active && !string.IsNullOrWhiteSpace(progress) ? progress : "—");
        SetInspectorText(_queueInspectorEta, active && !string.IsNullOrWhiteSpace(eta) ? eta : "—");
        SetInspectorText(_queueInspectorDuplicateState, duplicateState);
        if (_queueInspectorPath != null)
            _uiToolTip.SetToolTip(_queueInspectorPath, path);
        if (_queueInspectorRationale != null)
            _uiToolTip.SetToolTip(_queueInspectorRationale, rationale);
        if (_queueInspectorOutput != null)
            _uiToolTip.SetToolTip(_queueInspectorOutput, GetQueueWorkspacePlannedOutput(row, meta).ToolTip);
    }

    private void RefreshQueueInspectorMedia(DataGridViewRow[] rows)
    {
        if (_queueInspectorMedia == null)
            return;

        string text = rows.Length switch
        {
            0 => "Select one queue item to view media metadata already available to MediaFlux.",
            > 1 => "Media stream details are shown for one selected item at a time. Select one item to avoid stale detail.",
            _ => BuildQueueInspectorMediaText(rows[0])
        };
        if (!string.Equals(_queueInspectorMedia.Text, text, StringComparison.Ordinal))
            _queueInspectorMedia.Text = text;
    }

    private string BuildQueueInspectorMediaText(DataGridViewRow row)
    {
        RowMeta meta = EnsureRowMeta(row);
        string path = GetFullPathFromRow(row) ?? meta.Path;
        MediaInfoService.MediaInfo? cached = null;
        if (_mediaInfoService != null && !string.IsNullOrWhiteSpace(path) &&
            _mediaInfoService.TryGetCachedInfo(path, out MediaInfoService.MediaInfo cachedInfo))
        {
            cached = cachedInfo;
        }

        EncodingPlan? plan = meta.IntelligencePlan;
        EncodingPlanSource? source = plan?.Source;
        string container = !string.IsNullOrWhiteSpace(cached?.FormatName)
            ? cached!.FormatName!
            : $"Not cached (file extension {Path.GetExtension(path)})";
        string codec = FirstNonEmpty(meta.VideoCodec, cached?.VideoCodec, source?.Codec) ?? "Not cached";
        string resolution = FirstNonEmpty(NormalizeInspectorResolution(meta.Resolution),
            cached?.Width is int width && cached.Height is int height ? $"{width}×{height}" : null,
            source?.Width is int sourceWidth && source.Height is int sourceHeight ? $"{sourceWidth}×{sourceHeight}" : null) ?? "Not cached";
        double? fps = meta.Fps > 0 ? meta.Fps : cached?.Fps ?? source?.FrameRate;
        double? duration = meta.DurationSec > 0 ? meta.DurationSec : cached?.DurationSeconds ?? source?.DurationSeconds;
        long? sizeBytes = source?.SizeBytes;
        if (sizeBytes is not > 0 && File.Exists(path))
        {
            try { sizeBytes = new FileInfo(path).Length; }
            catch { }
        }

        var text = new StringBuilder();
        string sourceSizeText = sizeBytes is > 0
            ? FormatSize(sizeBytes.Value / (1024d * 1024d))
            : "Not cached";
        text.AppendLine($"Source: {path}");
        text.AppendLine($"Container: {container}");
        text.AppendLine($"Video: {codec} · {resolution} · {FormatOptionalRate(fps)} fps");
        text.AppendLine($"Duration: {FormatOptionalDuration(duration)} · Size: {sourceSizeText}");
        text.AppendLine($"Video bitrate: {FormatOptionalBitrate(cached?.BitrateKbps ?? source?.BitrateKbps)} · Total bitrate: {FormatOptionalBitrate(cached?.TotalBitrateKbps)}");

        if (plan?.Audio.Count > 0)
        {
            text.AppendLine("Audio streams (frozen preflight plan):");
            foreach (EncodingPlanStream stream in plan.Audio)
                text.AppendLine($"  #{stream.StreamIndex}: {stream.Codec} · {stream.Action}{FormatTargetCodec(stream.TargetCodec)}{FormatChannels(stream.Channels)} · {stream.Reason}");
        }
        else
        {
            text.AppendLine($"Audio streams: {cached?.AudioStreamCount.ToString() ?? "Not cached"} · combined bitrate {FormatOptionalBitrate(cached?.AudioBitrateKbps)}");
        }

        if (plan?.Subtitles.Count > 0)
        {
            text.AppendLine("Subtitle streams (frozen preflight plan):");
            foreach (EncodingPlanStream stream in plan.Subtitles)
                text.AppendLine($"  #{stream.StreamIndex}: {stream.Codec} · {stream.Action}{FormatTargetCodec(stream.TargetCodec)} · {stream.Reason}");
        }
        else
        {
            text.AppendLine($"Subtitle streams: {cached?.SubtitleStreamCount.ToString() ?? "Not cached"} · combined bitrate {FormatOptionalBitrate(cached?.SubtitleBitrateKbps)}");
        }

        if (cached != null)
            text.AppendLine($"Other streams: {cached.DataStreamCount} data · {cached.AttachmentStreamCount} attachments");
        if (meta.DuplicateGroupId is int groupId)
            text.AppendLine($"Duplicate group: {groupId} · {meta.DuplicateConfidence} ({meta.DuplicateConfidenceScore}) · {meta.DuplicateRecommendation}");
        else
            text.AppendLine(meta.ExcludedFromEncodeAsDuplicate ? "Duplicate state: excluded" : "Duplicate state: no match recorded");

        if (plan == null && cached == null)
            text.AppendLine("Detailed source metadata is not cached yet. Selecting this tab will not start a source probe.");
        else if (plan == null)
            text.AppendLine("Per-stream codec, language, and disposition details are not part of the current aggregate MediaInfo cache.");
        return text.ToString().TrimEnd();
    }

    private void RefreshQueueInspectorDiagnostics() =>
        RefreshQueueInspectorDiagnostics(GetQueueInspectorSelectedRows());

    private void RefreshQueueInspectorDiagnostics(DataGridViewRow[] rows)
    {
        if (_queueInspectorDiagnostics == null)
            return;

        string text;
        if (rows.Length == 0)
        {
            text = "Select a queue item to view its failure, recovery, and diagnostic details.\r\n\r\nUse View / Clear Application Logs to inspect central error logs and raw FFmpeg evidence.";
        }
        else if (rows.Length > 1)
        {
            text = $"{rows.Length:N0} items selected. Technical diagnostics are shown for one item at a time.";
        }
        else
        {
            RowMeta? meta = rows[0].Tag as RowMeta;
            string status = rows[0].Cells["colStatus"]?.Value?.ToString() ?? "Queued";
            var report = new StringBuilder();
            report.AppendLine($"Status: {status}");
            if (meta != null)
            {
                report.AppendLine($"Queue item: {meta.QueueItemId:N}");
                report.AppendLine($"Current stage: {meta.CurrentProcessingStage}");
                if (meta.IntelligencePlan != null)
                    report.AppendLine($"Frozen Encoding Plan: {meta.IntelligencePlan.PlanId:N}");
                if (!string.IsNullOrWhiteSpace(meta.StatisticsOperationId))
                    report.AppendLine($"Statistics operation: {meta.StatisticsOperationId}");
            }
            if (meta?.FailureAnalysis is EncodeFailureAnalysis failure)
            {
                report.AppendLine();
                report.AppendLine("Failure classification");
                report.AppendLine($"Stage: {failure.FailureStage}");
                report.AppendLine($"Summary: {failure.Summary}");
                report.AppendLine($"Likely cause: {failure.LikelyCause}");
                report.AppendLine($"Recommended action: {failure.RecommendedAction}");
                report.AppendLine($"Technical detail: {failure.TechnicalDetail}");
            }
            if (meta?.IntelligenceOutcome is EncodingExecutionOutcome outcome)
            {
                report.AppendLine();
                report.AppendLine("Recovery and final execution outcome");
                report.AppendLine(EncodingPlanService.DescribeRecovery(outcome));
                report.AppendLine(EncodingPlanService.DescribeLifecycle(outcome));
            }
            if (!string.IsNullOrWhiteSpace(meta?.CuratedFailureDiagnosticReport))
            {
                report.AppendLine();
                report.AppendLine("Curated failure diagnostic report");
                report.AppendLine(meta.CuratedFailureDiagnosticReport);
            }
            string? executionLog = meta?.GetInspectorLogTail();
            if (!string.IsNullOrWhiteSpace(executionLog))
            {
                report.AppendLine();
                report.AppendLine("Recent FFmpeg / execution log (bounded tail)");
                report.AppendLine(executionLog);
            }
            if (meta?.FailureAnalysis == null && meta?.IntelligenceOutcome == null &&
                string.IsNullOrWhiteSpace(meta?.CuratedFailureDiagnosticReport) &&
                string.IsNullOrWhiteSpace(executionLog))
            {
                report.AppendLine();
                report.Append("No failure or recovery diagnostic has been recorded for this item.");
            }
            text = report.ToString().TrimEnd();
        }

        if (!string.Equals(_queueInspectorDiagnostics.Text, text, StringComparison.Ordinal))
            _queueInspectorDiagnostics.Text = text;
        if (_queueInspectorCopyDiagnostic != null)
            _queueInspectorCopyDiagnostic.Enabled = !string.IsNullOrWhiteSpace(text);
    }

    private DataGridViewRow[] GetQueueInspectorSelectedRows() =>
        dgvEncodeQueue.SelectedRows.Cast<DataGridViewRow>()
            .Where(row => !row.IsNewRow)
            .ToArray();

    private bool IsQueueInspectorItemSelected(DataGridViewRow row)
    {
        RowMeta target = EnsureRowMeta(row);
        string targetPath = NormalizeInspectorPath(GetFullPathFromRow(row) ?? target.Path);
        return GetQueueInspectorSelectedRows().Any(selected =>
        {
            RowMeta selectedMeta = EnsureRowMeta(selected);
            return selectedMeta.QueueItemId == target.QueueItemId &&
                   string.Equals(
                       NormalizeInspectorPath(GetFullPathFromRow(selected) ?? selectedMeta.Path),
                       targetPath,
                       StringComparison.OrdinalIgnoreCase);
        });
    }

    private string BuildQueueInspectorSelectionKey(IEnumerable<DataGridViewRow> rows) =>
        string.Join("|", rows
            .Select(row =>
            {
                RowMeta meta = EnsureRowMeta(row);
                return $"{meta.QueueItemId:N}:{NormalizeInspectorPath(GetFullPathFromRow(row) ?? meta.Path)}";
            })
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase));

    private string BuildQueueInspectorPresentationKey(IEnumerable<DataGridViewRow> rows) =>
        string.Join("|", rows.Select(row =>
        {
            RowMeta? meta = row.Tag as RowMeta;
            return string.Join("~",
                meta?.QueueItemId.ToString("N") ?? "",
                row.Cells["colStatus"]?.Value?.ToString() ?? "",
                row.Cells["colEstimatedSize"]?.Value?.ToString() ?? "",
                meta?.SrcMb.ToString("R") ?? "",
                meta?.EstimateDiagnostic ?? "",
                meta?.EncodeRecommendation?.ToString() ?? "",
                meta?.SizePredictionCalibration?.ToString() ?? "",
                meta?.IntelligencePlan?.PlanId.ToString("N") ?? "",
                meta?.IntelligenceOutcome?.TerminalResult.ToString() ?? "",
                meta?.CurrentProcessingStage ?? "",
                meta?.FailureAnalysis?.Summary ?? "",
                meta?.CuratedFailureDiagnosticReport ?? "");
        }));

    private double GetInspectorSourceSizeMb(DataGridViewRow row)
    {
        if (row.Tag is RowMeta meta && meta.SrcMb > 0)
            return meta.SrcMb;
        return ParseSizeToMb(row.Cells["colSize"]?.Value?.ToString());
    }

    private double GetInspectorEstimatedSizeMb(DataGridViewRow row)
    {
        string? path = GetFullPathFromRow(row);
        if (!string.IsNullOrWhiteSpace(path) && _estimatedSizeMap.TryGetValue(path, out double value) && value > 0)
            return value;
        return ParseSizeToMb(row.Cells["colEstimatedSize"]?.Value?.ToString());
    }

    private static string EstimateDisplayText(DataGridViewRow row)
    {
        string text = row.Cells["colEstimatedSize"]?.Value?.ToString() ?? "";
        return string.IsNullOrWhiteSpace(text) ? "Waiting for estimate" : text;
    }

    private static string FormatSavings(double sourceMb, double outputMb)
    {
        double delta = sourceMb - outputMb;
        double percent = sourceMb > 0 ? delta / sourceMb * 100d : 0;
        return delta >= 0
            ? $"{FormatSize(delta)} ({percent:0.#}%)"
            : $"{FormatSize(Math.Abs(delta))} increase ({Math.Abs(percent):0.#}%)";
    }

    private static string? FirstMeaningfulLine(string? text) =>
        text?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? NormalizeInspectorResolution(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Replace('x', '×').Replace('X', '×');

    private static string FormatOptionalRate(double? value) => value is > 0 ? $"{value:0.###}" : "Not cached";
    private static string FormatOptionalBitrate(double? value) => value is > 0 ? $"{value:0} kbps" : "Not cached";
    private static string FormatOptionalBitrate(int? value) => value is > 0 ? $"{value:0} kbps" : "Not cached";
    private static string FormatOptionalDuration(double? seconds) => seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value).ToString(@"hh\:mm\:ss") : "Not cached";
    private static string FormatTargetCodec(string? codec) => string.IsNullOrWhiteSpace(codec) ? "" : $" → {codec}";
    private static string FormatChannels(int? channels) => channels is > 0 ? $" · {channels} channels" : "";

    private static string NormalizeInspectorPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.Trim(); }
    }

    private static void SetInspectorText(Label? label, string value)
    {
        if (label != null && !string.Equals(label.Text, value, StringComparison.Ordinal))
            label.Text = value;
    }

    private static string NormalizeRememberedQueueInspectorTab(string? savedTab) => savedTab?.Trim().ToLowerInvariant() switch
    {
        "plan & analysis" or "encoding plan" or "encoding options" or "restoration" => "Plan & Analysis",
        "media" or "streams" or "duplicates" => "Media",
        "diagnostics & logs" or "diagnostics" or "statistics" => "Diagnostics & Logs",
        _ => "Summary"
    };
}
