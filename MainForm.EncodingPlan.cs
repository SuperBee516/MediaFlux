using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;

namespace MediaFlux;

public partial class MainForm
{
    private Label? _encodingPlanStatusLabel;
    private TableLayoutPanel? _encodingPlanTable;
    private Label? _queueAnalysisStatusLabel;
    private TableLayoutPanel? _queueAnalysisTable;
    private string? _renderedQueueAnalysisItemKey;
    private string? _renderedQueueAnalysisTooltip;
    private string? _renderedQueueAnalysisCalibration;
    private string? _renderedIntelligenceItemKey;
    private EncodingIntelligencePresentation.PresentationKey? _renderedIntelligenceKey;
    private Guid? _renderedPlanId;
    private Control? _encodingPlanGroup;
    private readonly Dictionary<string, Label> _dynamicIntelligenceValues = new(StringComparer.Ordinal);
    private Control CreateEncodingPlanGroup()
    {
        var group = new CompositedEncodingPlanGroupBox
        {
            Text = "Encoding Plan",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(12, 8, 12, 10),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Margin = Padding.Empty,
            BackColor = Color.White
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 6,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.White
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int index = 0; index < 6; index++)
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        content.Controls.Add(CreateEncodingPlanHeader("QUEUE ANALYSIS"), 0, 0);

        _queueAnalysisStatusLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 4),
            Text = "Select a queue item to view its queue analysis."
        };
        content.Controls.Add(_queueAnalysisStatusLabel, 0, 1);

        _queueAnalysisTable = CreateEncodingPlanTable();
        content.Controls.Add(_queueAnalysisTable, 0, 2);

        content.Controls.Add(CreateEncodingPlanHeader("ENCODING PLAN"), 0, 3);

        _encodingPlanStatusLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 8),
            Text = "Select a queue item to view Encoding Intelligence."
        };
        content.Controls.Add(_encodingPlanStatusLabel, 0, 4);

        _encodingPlanTable = CreateEncodingPlanTable();
        content.Controls.Add(_encodingPlanTable, 0, 5);

        _encodingPlanGroup = group;
        group.Controls.Add(content);
        return group;
    }

    // This group owns the dynamically populated analysis and plan tables.
    // Compositing its child windows prevents partially created rows from being
    // exposed while one selected-item presentation is being installed.
    private sealed class CompositedEncodingPlanGroupBox : GroupBox
    {
        private const int WsExComposited = 0x02000000;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WsExComposited;
                return parameters;
            }
        }
    }

    private static Label CreateEncodingPlanHeader(string text) => new()
    {
        AutoSize = true,
        Text = text,
        ForeColor = Color.FromArgb(31, 88, 166),
        Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold),
        Margin = new Padding(0, 1, 0, 3)
    };

    private static TableLayoutPanel CreateEncodingPlanTable()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 0,
            Margin = new Padding(0, 0, 0, 7),
            Padding = Padding.Empty,
            BackColor = Color.White
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        return table;
    }

    private void ScheduleEncodingPlanRefresh()
    {
        if (_encodingPlanTable == null || _queueAnalysisTable == null || IsDisposed)
            return;

        using (BeginPlanAndAnalysisLayoutUpdate(_encodingPlanGroup))
            ScheduleEncodingPlanRefreshCore();
    }

    private void ScheduleEncodingPlanRefreshCore()
    {
        DataGridViewRow[] rows = dgvEncodeQueue.SelectedRows
            .Cast<DataGridViewRow>()
            .Where(row => !row.IsNewRow)
            .ToArray();
        if (rows.Length != 1)
        {
            RenderQueueAnalysis(null, null, rows.Length == 0
                ? "Select a queue item to view its queue analysis."
                : "Select one queue item at a time to view queue analysis.");
            RenderEncodingPlanStatus(rows.Length == 0
                ? "Select a queue item to view Encoding Intelligence."
                : "Select one queue item at a time to view Encoding Intelligence.");
            return;
        }

        RowMeta meta = EnsureRowMeta(rows[0]);
        RenderQueueAnalysis(rows[0], meta, null);
        RequestQualityPreview(rows[0], meta);
        if (meta.IntelligencePlan == null)
        {
            RenderEncodingPlanStatus("Encoding Intelligence will appear when the existing encode preflight publishes its plan.");
            return;
        }
        RenderEncodingPlan(meta.IntelligencePlan, meta.IntelligenceOutcome, rows[0]);
    }

    private void RenderQueueAnalysis(DataGridViewRow? row, RowMeta? meta, string? emptyStatus)
    {
        if (_queueAnalysisStatusLabel == null || _queueAnalysisTable == null)
            return;

        if (row == null || meta == null)
        {
            string emptyText = emptyStatus ?? "Select a queue item to view its queue analysis.";
            if (_renderedQueueAnalysisItemKey == null &&
                string.Equals(_queueAnalysisStatusLabel.Text, emptyText, StringComparison.Ordinal) &&
                _queueAnalysisTable.Controls.Count == 0)
                return;

            _queueAnalysisStatusLabel.Text = emptyText;
            _renderedQueueAnalysisItemKey = null;
            _renderedQueueAnalysisTooltip = null;
            _renderedQueueAnalysisCalibration = null;
            ClearEncodingPlanRows(_queueAnalysisTable);
            return;
        }

        QueueAnalysisPresentation presentation = GetQueueAnalysisPresentation(row, meta);
        string tooltip = presentation.BuildTooltip();
        string? calibration = GetQueueAnalysisCalibration(meta.SizePredictionCalibration);
        string itemKey = GetEncodingPlanItemKey(row, meta);
        if (string.Equals(_renderedQueueAnalysisItemKey, itemKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_renderedQueueAnalysisTooltip, tooltip, StringComparison.Ordinal) &&
            string.Equals(_renderedQueueAnalysisCalibration, calibration, StringComparison.Ordinal))
            return;

        _renderedQueueAnalysisItemKey = itemKey;
        _renderedQueueAnalysisTooltip = tooltip;
        _renderedQueueAnalysisCalibration = calibration;
        string status = presentation.IsAvailable ? string.Empty : presentation.Status;
        if (!string.Equals(_queueAnalysisStatusLabel.Text, status, StringComparison.Ordinal))
            _queueAnalysisStatusLabel.Text = status;
        ClearEncodingPlanRows(_queueAnalysisTable);
        if (!presentation.IsAvailable)
            return;

        if (presentation.Recommendation != null)
            AddQueueAnalysisItem("Recommendation", presentation.Recommendation);
        if (presentation.Confidence != null)
            AddQueueAnalysisItem("Confidence", presentation.Confidence);
        if (presentation.EstimatedResult != null)
            AddQueueAnalysisItem("Estimated result", presentation.EstimatedResult);
        if (calibration != null)
            AddQueueAnalysisItem("Size calibration", calibration);
        if (presentation.Quality != null)
        {
            foreach (EncodingPlanItem item in EncodingQualityPresentation.CreateItems(presentation.Quality))
                AddQueueAnalysisItem(item.Label, item.Value);
        }
        if (presentation.SavingsLabel != null && presentation.SavingsValue != null)
            AddQueueAnalysisItem(presentation.SavingsLabel, presentation.SavingsValue);
        if (presentation.Reasons.Count > 0)
            AddQueueAnalysisSection("Why", presentation.Reasons);
    }

    private QueueAnalysisPresentation GetQueueAnalysisPresentation(
        DataGridViewRow row,
        RowMeta meta)
    {
        string path = GetFullPathFromRow(row) ?? meta.Path;
        double sourceMb = meta.SrcMb;
        if (sourceMb <= 0)
            _queueSourceSizeMap.TryGetValue(path, out sourceMb);
        _estimatedSizeMap.TryGetValue(path, out double estimatedOutputMb);
        if (meta.IntelligencePlan?.Recommendation is EncodingRecommendation planRecommendation)
            return QueueAnalysisPresentation.Create(planRecommendation);
        return QueueAnalysisPresentation.Create(meta.EncodeRecommendation, sourceMb, estimatedOutputMb, meta.IntelligencePlan?.Quality ?? meta.QualityPreview);
    }

    private static string? GetQueueAnalysisCalibration(EncodingSizePredictionCalibration? calibration)
    {
        if (calibration == null)
            return null;

        string prediction = calibration.Applied
            ? $"{calibration.CalibratedPredictionMb:0.##} MB (base {calibration.BasePredictionMb:0.##} MB)"
            : calibration.Decision == EncodingCalibrationDecision.ShadowEvaluationOnly
                ? $"Shadow candidate {calibration.HypotheticalCalibratedPredictionMb:0.##} MB; displaying base {calibration.BasePredictionMb:0.##} MB"
                : $"{calibration.Decision}; displaying base {calibration.BasePredictionMb:0.##} MB";
        string policyId = AdaptivePredictionPolicies.NormalizePolicyId(calibration.PolicyId);
        if (policyId == AdaptivePredictionPolicies.LegacyPolicyId)
            policyId = "Legacy / Unversioned";
        string learning = calibration.LearningStrength > 0
            ? $"; learning strength {calibration.LearningStrength:0.##}"
            : string.Empty;
        return $"{prediction}; {policyId}; {calibration.EffectivenessState}{learning}";
    }

    private void InvalidateEncodingPlansForConfigurationChange()
    {
        Interlocked.Increment(ref _qualityPreviewGeneration);
        foreach (DataGridViewRow row in dgvEncodeQueue?.Rows.Cast<DataGridViewRow>() ?? Enumerable.Empty<DataGridViewRow>())
        {
            if (row.Tag is RowMeta meta)
            {
                meta.IntelligencePlan = null;
                meta.IntelligenceOutcome = null;
                meta.QualityPreview = null;
            }
        }
    }

    private void RequestQualityPreview(DataGridViewRow row, RowMeta meta)
    {
        if (!IsAutomaticQualitySelected() || meta.IntelligencePlan != null ||
            meta.HasCustomSettings || string.IsNullOrWhiteSpace(meta.Path) ||
            meta.QualityPreview != null)
            return;

        int generation = _qualityPreviewGeneration;
        string path = meta.Path;
        QualityTarget target = GetSelectedQualityTarget();
        VideoEncoderSelection encoder = GetSelectedVideoEncoderSelection();
        EncodingService.ScaleMode scaleMode = GetSelectedScaleMode();
        bool tenBit = chkTenBit?.Checked == true;
        double? targetMb = EncodingTargetSizeResolver.ResolveAutomaticQualityTargetMb(
            automaticQuality: true,
            autoTargetSize: chkAutoTargetSize.Checked,
            configuredManualTarget: txtTargetSize.Text);

        if (!TryBeginQualityPreviewRequest(meta, generation))
            return;

        _ = Task.Run(async () =>
        {
            bool requestReleasedOnUi = false;
            try
            {
                if (!File.Exists(path))
                    return;

                var probe = await new FfprobeService(AppPaths.InstallDirectory, _config.FfprobePath)
                    .ProbeAsync(path).ConfigureAwait(false);
                if (!probe.Success)
                    return;
                MediaProbeStreamInfo? video = probe.Streams.FirstOrDefault(stream =>
                    stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
                VideoOutputGeometryPlan? geometry = null;
                if (video?.Width is > 0 && video.Height is > 0)
                {
                    VideoOutputResolutionPlan resolution = VideoRestorationPipeline.ResolveFinalOutputResolution(
                        video.Width.Value, video.Height.Value, _config.VideoRestoration, scaleMode);
                    geometry = VideoOutputGeometryPlanner.Resolve(
                        video.Width.Value, video.Height.Value, resolution, encoder, tenBit);
                }
                EncodingQualityResolution quality = new EncodingQualityPolicyService().Resolve(
                    new EncodingQualityPolicyRequest(
                        EncodingQualityIntent.Automatic(target), probe, encoder,
                        geometry, scaleMode, targetMb));
                if (IsDisposed || !IsHandleCreated)
                    return;

                UiInvoke(() =>
                {
                    try
                    {
                        if (generation != _qualityPreviewGeneration || IsDisposed ||
                            !ReferenceEquals(dgvEncodeQueue.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault(), row) ||
                            !string.Equals(meta.Path, path, StringComparison.OrdinalIgnoreCase) ||
                            meta.IntelligencePlan != null)
                        {
                            return;
                        }
                        using (BeginPlanAndAnalysisLayoutUpdate(_encodingPlanGroup))
                        {
                            meta.QualityPreview = quality;
                            RenderQueueAnalysis(row, meta, null);
                        }
                    }
                    finally
                    {
                        EndQualityPreviewRequest(meta, generation);
                    }
                });
                requestReleasedOnUi = true;
            }
            catch
            {
                // Queue preview is advisory; encode-time preflight remains authoritative.
            }
            finally
            {
                if (!requestReleasedOnUi)
                    EndQualityPreviewRequest(meta, generation);
            }
        });
    }

    private static bool TryBeginQualityPreviewRequest(RowMeta meta, int generation)
    {
        while (true)
        {
            int current = Volatile.Read(ref meta.QualityPreviewRequestGeneration);
            if (current == generation)
                return false;
            if (Interlocked.CompareExchange(
                    ref meta.QualityPreviewRequestGeneration,
                    generation,
                    current) == current)
            {
                return true;
            }
        }
    }

    private static void EndQualityPreviewRequest(RowMeta meta, int generation) =>
        Interlocked.CompareExchange(
            ref meta.QualityPreviewRequestGeneration,
            int.MinValue,
            generation);

    private void AddQueueAnalysisSection(string title, IReadOnlyList<string> reasons)
    {
        if (_queueAnalysisTable == null)
            return;

        int headerRow = _queueAnalysisTable.RowCount++;
        _queueAnalysisTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _queueAnalysisTable.Controls.Add(CreateEncodingPlanHeader(title.ToUpperInvariant()), 0, headerRow);
        foreach (string reason in reasons)
            AddQueueAnalysisItem("•", reason);
    }

    private void AddQueueAnalysisItem(string label, string value)
    {
        if (_queueAnalysisTable == null)
            return;

        int row = _queueAnalysisTable.RowCount++;
        _queueAnalysisTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 3),
            Padding = new Padding(7, 3, 7, 3),
            BackColor = Color.FromArgb(248, 249, 251)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, label == "•" ? 18F : 112F));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        var caption = CreateInfoCaption(label == "•" ? label : label.ToUpperInvariant());
        caption.Anchor = AnchorStyles.Left;
        panel.Controls.Add(caption, 0, 0);
        var text = CreateInfoValue(value, bold: label != "•");
        text.AutoSize = true;
        text.MaximumSize = new Size(760, 0);
        panel.Controls.Add(text, 1, 0);
        _queueAnalysisTable.Controls.Add(panel, 0, row);
    }

    private void RenderEncodingPlanStatus(string text)
    {
        if (_renderedIntelligenceItemKey == null && _renderedPlanId == null &&
            string.Equals(_encodingPlanStatusLabel?.Text, text, StringComparison.Ordinal))
            return;
        if (_encodingPlanStatusLabel != null)
            _encodingPlanStatusLabel.Text = text;
        _renderedIntelligenceItemKey = null;
        _renderedIntelligenceKey = null;
        _renderedPlanId = null;
        _dynamicIntelligenceValues.Clear();
        ClearEncodingPlanRows(_encodingPlanTable);
    }

    private void RenderEncodingPlanUnavailable(string text)
    {
        RenderEncodingPlanStatus(text);
    }

    private void RefreshCurrentEncodingIntelligence(DataGridViewRow row, RowMeta meta)
    {
        if (IsDisposed || meta.IntelligencePlan == null ||
            !dgvEncodeQueue.SelectedRows.Cast<DataGridViewRow>().Contains(row))
            return;
        RenderEncodingPlan(meta.IntelligencePlan, meta.IntelligenceOutcome, row);
        RefreshQueueInspectorForRow(row);
    }

    private void RenderEncodingPlan(EncodingPlan plan, EncodingExecutionOutcome? outcome = null, DataGridViewRow? row = null)
    {
        if (_encodingPlanStatusLabel == null || _encodingPlanTable == null)
            return;

        EncodingIntelligencePresentation.PresentationKey key =
            EncodingIntelligencePresentation.GetKey(plan, outcome);
        string itemKey = row == null ? string.Empty : GetEncodingPlanItemKey(row, EnsureRowMeta(row));
        if (string.Equals(_renderedIntelligenceItemKey, itemKey, StringComparison.OrdinalIgnoreCase) &&
            _renderedPlanId == plan.PlanId &&
            _renderedIntelligenceKey == key)
            return;

        if (string.Equals(_renderedIntelligenceItemKey, itemKey, StringComparison.OrdinalIgnoreCase) &&
            _renderedPlanId == plan.PlanId)
        {
            UpdateDynamicEncodingIntelligence(plan, outcome);
            _renderedIntelligenceKey = key;
            return;
        }

        _renderedIntelligenceItemKey = itemKey;
        _renderedIntelligenceKey = key;
        _renderedPlanId = plan.PlanId;
        _dynamicIntelligenceValues.Clear();

        string status = plan.IsAvailable
            ? "Planned settings are authoritative. Historical estimates are advisory and never change the encode."
            : plan.UnavailableReason;
        if (!string.Equals(_encodingPlanStatusLabel.Text, status, StringComparison.Ordinal))
            _encodingPlanStatusLabel.Text = status;
        ClearEncodingPlanRows(_encodingPlanTable);

        if (!plan.IsAvailable)
            return;

        EncodingIntelligencePresentation.Model presentation = EncodingIntelligencePresentation.Create(plan, outcome);
        AddEncodingPlanSection("Encoding intelligence", presentation.Summary);
        if (plan.Recommendation is { } recommendation)
        {
            AddEncodingPlanSection("Recommendation", new[]
            {
                new EncodingPlanItem("Decision", recommendation.DisplayName),
                new EncodingPlanItem("Reason", recommendation.PrimaryReason),
                new EncodingPlanItem("Confidence", recommendation.Confidence.ToString()),
                new EncodingPlanItem("Quality risk", recommendation.QualityRisk.ToString()),
                recommendation.ExpectedSavingsPercent is double percent
                    ? new EncodingPlanItem("Expected savings", $"{percent:0.#}%")
                    : null
            }.Where(item => item != null).Cast<EncodingPlanItem>().ToArray());
        }
        if (presentation.Reasons.Count > 0)
            AddEncodingPlanSection("Why this plan?", presentation.Reasons);
        if (presentation.Technical.Count > 0)
            AddEncodingPlanSection("Technical details", presentation.Technical);

        foreach (EncodingPlanSection section in plan.Sections)
            AddEncodingPlanSection(section.Title, section.Items);

        if (presentation.Summary.Count == 0 && plan.Sections.Count == 0)
        {
            int fallbackRow = _encodingPlanTable.RowCount++;
            _encodingPlanTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _encodingPlanTable.Controls.Add(
                new Label
                {
                    AutoSize = true,
                    ForeColor = SystemColors.GrayText,
                    Text = "No additional resolved processing or stream changes are available for this item."
                },
                0,
                fallbackRow);
        }
    }

    private void UpdateDynamicEncodingIntelligence(EncodingPlan plan, EncodingExecutionOutcome? outcome)
    {
        EncodingIntelligencePresentation.Model presentation = EncodingIntelligencePresentation.Create(plan, outcome);
        foreach (EncodingPlanItem item in presentation.Summary.Concat(presentation.Technical))
        {
            if (_dynamicIntelligenceValues.TryGetValue(item.Label, out Label? label) &&
                !string.Equals(label.Text, item.Value, StringComparison.Ordinal))
                label.Text = item.Value;
        }
    }

    private void AddEncodingPlanSection(string title, IReadOnlyList<EncodingPlanItem> items)
    {
        if (_encodingPlanTable == null || items.Count == 0)
            return;

        int sectionRow = _encodingPlanTable.RowCount++;
        _encodingPlanTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var header = CreateInfoCaption(title.ToUpperInvariant());
        header.ForeColor = Color.FromArgb(31, 88, 166);
        header.Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold);
        header.Margin = new Padding(0, sectionRow == 0 ? 1 : 8, 0, 3);
        _encodingPlanTable.Controls.Add(header, 0, sectionRow);
        foreach (EncodingPlanItem item in items)
            AddEncodingPlanItem(item);
    }

    private void AddEncodingPlanItem(EncodingPlanItem item)
    {
        if (_encodingPlanTable == null)
            return;

        int row = _encodingPlanTable.RowCount++;
        _encodingPlanTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = string.IsNullOrWhiteSpace(item.Reason) ? 1 : 2,
            Margin = new Padding(0, 0, 0, 3),
            Padding = new Padding(7, 3, 7, 3),
            BackColor = Color.FromArgb(248, 249, 251)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112F));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (!string.IsNullOrWhiteSpace(item.Reason))
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = CreateInfoCaption(item.Label.ToUpperInvariant());
        label.Anchor = AnchorStyles.Left;
        panel.Controls.Add(label, 0, 0);

        var value = CreateInfoValue(item.Value, bold: true);
        value.AutoSize = true;
        value.MaximumSize = new Size(760, 0);
        panel.Controls.Add(value, 1, 0);
        if (item.Label is "Recovery" or "Lifecycle" or "Terminal result" or "Validation" or "Finalization")
            _dynamicIntelligenceValues[item.Label] = value;

        if (!string.IsNullOrWhiteSpace(item.Reason))
        {
            var reason = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(760, 0),
                ForeColor = SystemColors.GrayText,
                Font = new Font("Segoe UI", 8F, FontStyle.Italic),
                Text = $"Reason: {item.Reason}",
                Margin = new Padding(0, 1, 0, 0)
            };
            panel.Controls.Add(reason, 1, 1);
        }

        _encodingPlanTable.Controls.Add(panel, 0, row);
    }

    private static void ClearEncodingPlanRows(TableLayoutPanel? table)
    {
        if (table == null)
            return;

        table.SuspendLayout();
        try
        {
            table.Controls.Clear();
            table.RowStyles.Clear();
            table.RowCount = 0;
        }
        finally
        {
            table.ResumeLayout(true);
        }
    }

    private string GetEncodingPlanItemKey(DataGridViewRow row, RowMeta meta)
    {
        string path = GetFullPathFromRow(row) ?? meta.Path;
        return string.IsNullOrWhiteSpace(path)
            ? $"row:{row.Index}"
            : path.Trim();
    }
}
