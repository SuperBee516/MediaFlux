using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;

namespace MediaFlux;

public partial class MainForm
{
    private Label? _encodingPlanStatusLabel;
    private TableLayoutPanel? _encodingPlanTable;
    private CancellationTokenSource? _encodingPlanCts;
    private int _encodingPlanRefreshGeneration;

    private sealed record EncodingPlanContext(
        string ProbePath,
        EncodingInputSource Input,
        VideoEncoderSelection Encoder,
        bool UseGpu,
        bool TenBit,
        int? AudioChannels,
        EncodingService.ScaleMode ScaleMode,
        OutputContainerSelection OutputContainer);

    private Control CreateEncodingPlanGroup()
    {
        var group = new GroupBox
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
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.White
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _encodingPlanStatusLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 8),
            Text = "Select one queue item to see its resolved pre-encode plan."
        };
        content.Controls.Add(_encodingPlanStatusLabel, 0, 0);

        _encodingPlanTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 0,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.White
        };
        _encodingPlanTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.Controls.Add(_encodingPlanTable, 0, 1);

        group.Controls.Add(content);
        return group;
    }

    private void ScheduleEncodingPlanRefresh()
    {
        if (_encodingPlanTable == null || IsDisposed)
            return;

        _encodingPlanCts?.Cancel();
        _encodingPlanCts = new CancellationTokenSource();
        int generation = Interlocked.Increment(ref _encodingPlanRefreshGeneration);
        _ = RefreshEncodingPlanAsync(generation, _encodingPlanCts.Token);
    }

    private async Task RefreshEncodingPlanAsync(
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(120, cancellationToken);
            if (IsDisposed || generation != _encodingPlanRefreshGeneration)
                return;

            DataGridViewRow[] rows = dgvEncodeQueue.SelectedRows
                .Cast<DataGridViewRow>()
                .Where(row => !row.IsNewRow)
                .ToArray();
            if (rows.Length != 1)
            {
                RenderEncodingPlanStatus(rows.Length == 0
                    ? "Select one queue item to see its resolved pre-encode plan."
                    : "Select one queue item at a time to see its resolved pre-encode plan.");
                return;
            }

            EncodingPlanContext? context = TryCaptureEncodingPlanContext(rows[0]);
            if (context == null)
                return;

            RenderEncodingPlanStatus("Resolving the selected source and current profile…");
            MediaProbeResult source = await new FfprobeService(
                    AppPaths.InstallDirectory,
                    _config.FfprobePath)
                .ProbeAsync(context.ProbePath, cancellationToken);

            if (cancellationToken.IsCancellationRequested ||
                IsDisposed ||
                generation != _encodingPlanRefreshGeneration)
            {
                return;
            }

            if (!source.Success)
            {
                RenderEncodingPlanUnavailable(
                    $"The plan is unavailable because FFprobe could not inspect the source: {source.ErrorMessage}");
                return;
            }

            EncodingPlan plan = EncodingPlanService.Resolve(
                new EncodingPlanService.Request(
                    source,
                    context.Input,
                    context.Encoder,
                    context.UseGpu,
                    context.TenBit,
                    context.AudioChannels,
                    context.ScaleMode,
                    _config.VideoRestoration.Clone(),
                    context.OutputContainer,
                    CompatibilityPolicy: GetContainerCompatibilityPolicy()));
            RenderEncodingPlan(plan);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsDisposed && generation == _encodingPlanRefreshGeneration)
            {
                RenderEncodingPlanUnavailable(
                    $"The plan is unavailable until the selected source can be resolved: {ex.Message}");
            }
        }
    }

    private EncodingPlanContext? TryCaptureEncodingPlanContext(DataGridViewRow row)
    {
        RowMeta meta = EnsureRowMeta(row);
        string? displayPath = GetFullPathFromRow(row) ?? meta.Path;
        if (string.IsNullOrWhiteSpace(displayPath))
        {
            RenderEncodingPlanUnavailable("The selected queue item has no source path.");
            return null;
        }

        EncodingInputSource input;
        string probePath;
        if (meta.IsDvdEncode && meta.DvdEncodeOptions != null)
        {
            input = new DvdEncodingInputFactory().Create(meta.DvdEncodeOptions);
            probePath = input.SourceFiles.FirstOrDefault() ?? input.SourcePath;
        }
        else
        {
            input = EncodingInputSource.FromFile(displayPath);
            probePath = displayPath;
        }

        ValidatedEncoderSettings validated;
        EncodingService.ScaleMode scaleMode;
        OutputContainerSelection outputContainer;
        if (meta.LibraryPolicyIntent is LibraryPolicyQueueItem policy)
        {
            EncodingPreset? preset = string.IsNullOrWhiteSpace(policy.EncodingPresetName)
                ? null
                : _presetService.LoadAll().FirstOrDefault(value =>
                    value.Name.Equals(policy.EncodingPresetName, StringComparison.OrdinalIgnoreCase));
            VideoCodecFamily codec = preset == null
                ? policy.ProposedCodec
                : VideoEncoderCompatibility.ParseCodecFamily(
                    string.IsNullOrWhiteSpace(preset.VideoCodec)
                        ? preset.VideoFormat
                        : preset.VideoCodec);
            string encoderId = preset == null
                ? policy.EncoderId
                : VideoEncoderCompatibility.ResolveEncoderId(
                    string.IsNullOrWhiteSpace(preset.EncoderId)
                        ? preset.EncoderMode
                        : preset.EncoderId,
                    codec);
            ResolvedVideoEncoder resolved = EncoderRegistry.Default.Resolve(encoderId, codec);
            validated = EncodingRequestValidator.ValidateAndNormalize(
                EncoderRegistry.Default,
                resolved.Selection,
                resolved.Provider.Capabilities.IsHardware,
                targetMb: null,
                preset?.EncoderPreset ?? policy.EncoderPreset,
                preset?.QualityValue ?? policy.QualityValue,
                preset?.TenBit ?? policy.PreferredBitDepth >= 10,
                GetSelectedAudioChannels(),
                concurrentEncoderSessions: false);
            scaleMode = PolicyScaleMode(policy);
            outputContainer = PolicyOutputContainer(policy);
        }
        else
        {
            validated = GetValidatedEncoderSettingsFromUi(includeConcurrentSessions: false);
            scaleMode = GetSelectedScaleMode();
            outputContainer = GetSelectedOutputContainer();
        }

        return new EncodingPlanContext(
            probePath,
            input,
            validated.Resolved.Selection,
            validated.UseGpu,
            validated.TenBit,
            GetSelectedAudioChannels(),
            scaleMode,
            outputContainer);
    }

    private void RenderEncodingPlanStatus(string text)
    {
        if (_encodingPlanStatusLabel != null)
            _encodingPlanStatusLabel.Text = text;
        ClearEncodingPlanRows();
    }

    private void RenderEncodingPlanUnavailable(string text)
    {
        RenderEncodingPlanStatus(text);
    }

    private void RenderEncodingPlan(EncodingPlan plan)
    {
        if (_encodingPlanStatusLabel == null || _encodingPlanTable == null)
            return;

        _encodingPlanStatusLabel.Text = plan.IsAvailable
            ? "Resolved from the selected source and current encode settings. Output size is omitted because no authoritative pre-encode value is available here."
            : plan.UnavailableReason;
        ClearEncodingPlanRows();

        if (!plan.IsAvailable)
            return;

        foreach (EncodingPlanSection section in plan.Sections)
        {
            int sectionRow = _encodingPlanTable.RowCount++;
            _encodingPlanTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var header = CreateInfoCaption(section.Title.ToUpperInvariant());
            header.ForeColor = Color.FromArgb(31, 88, 166);
            header.Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold);
            header.Margin = new Padding(0, sectionRow == 0 ? 1 : 8, 0, 3);
            _encodingPlanTable.Controls.Add(header, 0, sectionRow);

            foreach (EncodingPlanItem item in section.Items)
                AddEncodingPlanItem(item);
        }

        if (plan.Sections.Count == 0)
        {
            int row = _encodingPlanTable.RowCount++;
            _encodingPlanTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _encodingPlanTable.Controls.Add(
                new Label
                {
                    AutoSize = true,
                    ForeColor = SystemColors.GrayText,
                    Text = "No additional resolved processing or stream changes are available for this item."
                },
                0,
                row);
        }
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

    private void ClearEncodingPlanRows()
    {
        if (_encodingPlanTable == null)
            return;

        _encodingPlanTable.SuspendLayout();
        try
        {
            _encodingPlanTable.Controls.Clear();
            _encodingPlanTable.RowStyles.Clear();
            _encodingPlanTable.RowCount = 0;
        }
        finally
        {
            _encodingPlanTable.ResumeLayout(true);
        }
    }
}
