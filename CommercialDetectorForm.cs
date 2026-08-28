using System.Diagnostics;
using System.Globalization;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;

namespace MediaFlux;

internal enum CommercialDetectorViewState { NoSource, Loading, Ready, Analyzing, Cancelled, Completed, Error }

internal sealed record CommercialDetectorControlState(bool CanBrowse, bool CanAnalyze, bool CanCancel, bool CanChangeSettings);
internal sealed record CommercialBoundaryMoveRequest(Guid BoundaryId, double TimestampSeconds);

internal static class CommercialDetectorStateRules
{
    internal static CommercialDetectorControlState For(CommercialDetectorViewState state) => state switch
    {
        CommercialDetectorViewState.Loading => new(false, false, true, false),
        CommercialDetectorViewState.Analyzing => new(false, false, true, false),
        CommercialDetectorViewState.Ready or CommercialDetectorViewState.Cancelled or CommercialDetectorViewState.Completed => new(true, true, false, true),
        CommercialDetectorViewState.Error => new(true, true, false, true),
        _ => new(true, false, false, true)
    };
}

internal sealed class CommercialDetectorForm : MediaFluxForm
{
    private readonly Config _config;
    private readonly string _configPath;
    private readonly FfprobeService _probe;
    private readonly VideoFramePreviewService _framePreview;
    private readonly CommercialAnalysisStore _analysisStore;
    private readonly CommercialReviewState _review = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _framePreviewCts;
    private CancellationTokenSource? _exportCts;
    private readonly WindowsMediaPlayerHost _playerHost = new() { Dock = DockStyle.Fill };
    private readonly CommercialDetectionTimelineControl _timeline = new() { Name = "CommercialDetectionTimeline", Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _playbackTimer = new() { Interval = 100 };
    private readonly PreviewSeekCoordinator _previewSeek = new();
    private readonly ToolTip _toolTip = new();
    private readonly TextBox _sourcePath = new() { Name = "CommercialSourcePath", Dock = DockStyle.Fill, ReadOnly = true };
    private readonly Button _browse = new() { Name = "CommercialBrowseButton", Text = "Browse…", AutoSize = true };
    private readonly Label _mediaInfo = ValueLabel("Choose or drop a source video to begin.");
    private readonly ComboBox _preset = new() { Name = "CommercialPreset", DropDownStyle = ComboBoxStyle.DropDownList, Width = 145 };
    private readonly Button _analyze = new() { Name = "CommercialAnalyzeButton", Text = "Analyze Video", AutoSize = true };
    private readonly Button _cancel = new() { Name = "CommercialCancelButton", Text = "Cancel", AutoSize = true };
    private readonly Button _advancedToggle = new() { Name = "CommercialAdvancedToggle", AutoSize = true };
    private readonly Panel _advancedHost = new() { Name = "CommercialAdvancedSettings", Dock = DockStyle.Fill, AutoSize = true };
    private readonly CheckBox _blackEnabled = Check("Black / Fade");
    private readonly NumericUpDown _blackDuration = Number(.01M, 10, .15M, 2, .05M);
    private readonly NumericUpDown _blackThreshold = Number(1, 100, 10, 0, 1);
    private readonly CheckBox _silenceEnabled = Check("Silence");
    private readonly NumericUpDown _silenceDuration = Number(.01M, 30, .20M, 2, .05M);
    private readonly NumericUpDown _silenceDb = Number(-100, 0, -35, 0, 1);
    private readonly CheckBox _sceneEnabled = Check("Scene Change");
    private readonly NumericUpDown _sceneThreshold = Number(.01M, .99M, .45M, 2, .01M);
    private readonly NumericUpDown _correlationMs = Number(0, 5000, 500, 0, 50);
    private readonly NumericUpDown _minimumSegment = Number(1, 600, 15, 0, 1);
    private readonly CheckBox _preferCommonLengths = Check("Prefer 15 / 30 / 60 / 90 sec lengths");
    private readonly NumericUpDown _minimumConfidence = Number(0, 100, 45, 0, 1);
    private readonly CheckBox _includeLowConfidence = Check("Include low-confidence boundaries");
    private readonly Button _resetPreset = new() { Name = "CommercialResetPreset", Text = "Reset to Preset Defaults", AutoSize = true };
    private readonly Button _playPause = new() { Name = "CommercialPlayPause", Text = "Play", AutoSize = true };
    private readonly Button _seekBack = new() { Name = "CommercialSeekBack", Text = "◀ 5s", AutoSize = true };
    private readonly Button _seekForward = new() { Name = "CommercialSeekForward", Text = "5s ▶", AutoSize = true };
    private readonly Label _time = ValueLabel("0:00.000 / 0:00.000");
    private readonly Button _previousBoundary = new() { Name = "CommercialPreviousBoundary", Text = "Previous Boundary", AutoSize = true };
    private readonly Button _nextBoundary = new() { Name = "CommercialNextBoundary", Text = "Next Boundary", AutoSize = true };
    private readonly Button _addBoundary = new() { Name = "CommercialAddBoundary", Text = "Add Boundary at NOW", AutoSize = true };
    private readonly Button _removeBoundary = new() { Name = "CommercialRemoveBoundary", Text = "Remove Boundary", AutoSize = true };
    private readonly Button _resetBoundary = new() { Name = "CommercialResetBoundary", Text = "Reset to Detected Position", AutoSize = true };
    private readonly Button _playAcrossBoundary = new() { Name = "CommercialPlayAcrossBoundary", Text = "Play Across Boundary", AutoSize = true };
    private readonly Button _zoomOut = new() { Name = "CommercialZoomOut", Text = "−", Width = 34 };
    private readonly Button _zoomIn = new() { Name = "CommercialZoomIn", Text = "+", Width = 34 };
    private readonly Button _zoomFit = new() { Name = "CommercialZoomFit", Text = "Fit", AutoSize = true };
    private readonly HScrollBar _timelineScroll = new() { Name = "CommercialTimelineScroll", Dock = DockStyle.Fill, Minimum = 0, Maximum = 1099, LargeChange = 100, Enabled = false };
    private readonly Label _boundaryDetails = ValueLabel("Select a boundary to inspect its evidence.");
    private readonly PictureBox _beforeFrame = PreviewBox("CommercialBeforeFrame");
    private readonly PictureBox _afterFrame = PreviewBox("CommercialAfterFrame");
    private readonly Label _beforeLabel = ValueLabel("BEFORE  −0.250 sec");
    private readonly Label _afterLabel = ValueLabel("AFTER  +0.250 sec");
    private readonly DataGridView _segments = new() { Name = "CommercialSegmentsGrid" };
    private readonly Label _summary = ValueLabel("No analysis results yet.");
    private readonly ProgressBar _progress = new() { Name = "CommercialProgress", Minimum = 0, Maximum = 100, Dock = DockStyle.Fill };
    private readonly Label _status = ValueLabel("Choose a source video.");
    private readonly TextBox _outputDirectory = new() { Name = "CommercialOutputDirectory", Dock = DockStyle.Fill, Enabled = false };
    private readonly Button _browseOutput = new() { Name = "CommercialBrowseOutput", Text = "Browse…", AutoSize = true, Enabled = false };
    private readonly ComboBox _exportMode = new() { Name = "CommercialExportMode", DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false, Width = 180 };
    private readonly TextBox _namingPattern = new() { Name = "CommercialNamingPattern", Text = "{source}_Commercial_{index:00}", Width = 190, Enabled = false };
    private readonly Button _exportSelected = new() { Name = "CommercialExportSelected", Text = "Export Selected", AutoSize = true, Enabled = false };
    private readonly Button _exportAll = new() { Name = "CommercialExportAll", Text = "Export All", AutoSize = true, Enabled = false };
    private readonly Button _exportCancel = new() { Name = "CommercialExportCancel", Text = "Cancel Export", AutoSize = true, Enabled = false };
    private readonly Button _openOutputFolder = new() { Name = "CommercialOpenOutputFolder", Text = "Open Output Folder", AutoSize = true, Enabled = false };
    private readonly Label _exportDetails = ValueLabel("Analyze the source to enable export.");
    private readonly SplitContainer _previewSplit = Split("CommercialPreviewSplitter", Orientation.Vertical);
    private readonly SplitContainer _workspaceSplit = Split("CommercialWorkspaceSplitter", Orientation.Horizontal);
    private CommercialDetectionPreset _basePreset = CommercialDetectionPreset.Standard;
    private CommercialDetectorViewState _viewState;
    private bool _applyingSettings;
    private bool _isPlaying;
    private bool _synchronizingSelection;
    private double _duration;
    private double _sourceFrameRate;
    private double? _playAcrossEnd;
    private Guid? _selectedBoundaryId;
    private readonly Stopwatch _immediateSeekThrottle = Stopwatch.StartNew();
    private readonly Stopwatch _exportStopwatch = new();
    private readonly Dictionary<int, Image> _frameCache = new();
    private string? _loadedSource;
    private bool _hasAnalysisResult;
    private bool _streamCopyNoticeShown;

    internal CommercialDetectorForm(Config config, string configPath)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
        _probe = new FfprobeService(AppPaths.InstallDirectory, config.FfprobePath);
        _framePreview = new VideoFramePreviewService(AppPaths.InstallDirectory, config.FfmpegPath);
        _analysisStore = new CommercialAnalysisStore();
        _blackDuration.Name = "CommercialBlackDuration";
        _blackThreshold.Name = "CommercialBlackThreshold";
        _silenceDuration.Name = "CommercialSilenceDuration";
        _silenceDb.Name = "CommercialSilenceThreshold";
        _sceneThreshold.Name = "CommercialSceneThreshold";
        _correlationMs.Name = "CommercialCorrelationTolerance";
        _minimumSegment.Name = "CommercialMinimumSegment";
        _minimumConfidence.Name = "CommercialMinimumConfidence";
        Text = "Commercial Detector & Splitter";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1180, 900);
        Size = RestoreSize();
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(246, 248, 251);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        AllowDrop = true;

        BuildLayout();
        _preset.Items.AddRange(Enum.GetNames<CommercialDetectionPreset>());
        _exportMode.Items.AddRange(new object[] { "Fast / Lossless", "Accurate Cut" });
        _exportMode.SelectedIndex = 0;
        ApplyStoredPreferences();
        SetAdvancedExpanded(_config.CommercialDetectorAdvancedExpanded);
        WireEvents();
        SetState(CommercialDetectorViewState.NoSource, "Choose a source video.");
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(14), BackColor = BackColor };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildSourceCard(), 0, 1);
        root.Controls.Add(_advancedHost, 0, 2);
        root.Controls.Add(BuildProgressRow(), 0, 3);
        root.Controls.Add(_summary, 0, 4);
        root.Controls.Add(BuildWorkspace(), 0, 5);
        Controls.Add(root);
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 10) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "Commercial Detector & Splitter", AutoSize = true, Font = new Font(Font.FontFamily, 16F, FontStyle.Bold), ForeColor = Color.FromArgb(31, 41, 55) }, 0, 0);
        header.Controls.Add(new Label { Text = "Source  →  Analyze  →  Review  →  Export", AutoSize = true, ForeColor = Color.FromArgb(71, 85, 105), Margin = new Padding(8, 7, 0, 0) }, 1, 0);
        return header;
    }

    private Control BuildSourceCard()
    {
        TableLayoutPanel card = Card("Source & Analysis", 4);
        for (int index = 0; index < 4; index++) card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var sourceRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 7, 0, 2) };
        sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceRow.Controls.Add(new Label { Text = "Source", AutoSize = true, Margin = new Padding(0, 7, 8, 0) }, 0, 0); sourceRow.Controls.Add(_sourcePath, 1, 0); sourceRow.Controls.Add(_browse, 2, 0);
        card.Controls.Add(sourceRow, 0, 1);
        _mediaInfo.Dock = DockStyle.Fill; _mediaInfo.AutoEllipsis = true; card.Controls.Add(_mediaInfo, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 5, 0, 0) };
        actions.Controls.Add(new Label { Text = "Detection Preset", AutoSize = true, Margin = new Padding(0, 7, 5, 0) }); actions.Controls.Add(_preset); actions.Controls.Add(_analyze); actions.Controls.Add(_cancel); actions.Controls.Add(_advancedToggle);
        card.Controls.Add(actions, 0, 3);
        return card;
    }

    private void BuildAdvancedSettings()
    {
        var card = Card("Advanced Settings", 3); card.Margin = new Padding(0, 8, 0, 8);
        for (int index = 0; index < 3; index++) card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var groups = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 7, 0, 0) };
        for (int index = 0; index < 4; index++) groups.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        groups.Controls.Add(SettingsGroup("Black / Fade", _blackEnabled, Field("Minimum duration (sec)", _blackDuration), Field("Darkness threshold (%)", _blackThreshold)), 0, 0);
        groups.Controls.Add(SettingsGroup("Silence", _silenceEnabled, Field("Minimum duration (sec)", _silenceDuration), Field("Threshold (dB)", _silenceDb)), 1, 0);
        groups.Controls.Add(SettingsGroup("Scene", _sceneEnabled, Field("Sensitivity threshold", _sceneThreshold)), 2, 0);
        groups.Controls.Add(SettingsGroup("Correlation / Segments", Field("Correlation tolerance (ms)", _correlationMs), Field("Minimum segment (sec)", _minimumSegment), _preferCommonLengths, Field("Minimum confidence", _minimumConfidence), _includeLowConfidence), 3, 0);
        card.Controls.Add(groups, 0, 1);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 6, 0, 0) }; footer.Controls.Add(_resetPreset); card.Controls.Add(footer, 0, 2);
        _advancedHost.Controls.Add(card);
        _toolTip.SetToolTip(_blackDuration, "Shortest dark interval FFmpeg should report as a black/fade signal.");
        _toolTip.SetToolTip(_blackThreshold, "Pixels at or below this brightness percentage are treated as black.");
        _toolTip.SetToolTip(_silenceDuration, "Shortest quiet interval FFmpeg should report as a silence signal.");
        _toolTip.SetToolTip(_silenceDb, "Audio quieter than this level is treated as silence.");
        _toolTip.SetToolTip(_sceneThreshold, "Higher values report fewer, stronger scene changes.");
        _toolTip.SetToolTip(_correlationMs, "Nearby signals inside this window become one candidate boundary.");
        _toolTip.SetToolTip(_minimumSegment, "Reject boundaries that would create clips shorter than this duration.");
        _toolTip.SetToolTip(_preferCommonLengths, "Adds only a small confidence boost near common commercial durations; it is never required.");
        _toolTip.SetToolTip(_minimumConfidence, "Candidates below this 0–100 score are hidden.");
        _toolTip.SetToolTip(_includeLowConfidence, "Lower the filter to include tentative candidates for manual review.");
    }

    private Control BuildProgressRow()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 4) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _status.Dock = DockStyle.Fill; _status.AutoEllipsis = true; _status.Margin = new Padding(10, 2, 0, 0); panel.Controls.Add(_progress, 0, 0); panel.Controls.Add(_status, 1, 0); return panel;
    }

    private Control BuildWorkspace()
    {
        _previewSplit.Panel1.Padding = new Padding(0, 0, 5, 0); _previewSplit.Panel2.Padding = new Padding(5, 0, 0, 0);
        _previewSplit.Panel1.Controls.Add(BuildPreviewCard()); _previewSplit.Panel2.Controls.Add(BuildTimelineCard());
        _workspaceSplit.Panel1.Padding = new Padding(0, 0, 0, 5); _workspaceSplit.Panel2.Padding = new Padding(0, 5, 0, 0);
        _workspaceSplit.Panel1.Controls.Add(_previewSplit); _workspaceSplit.Panel2.Controls.Add(BuildReviewArea()); return _workspaceSplit;
    }

    private Control BuildPreviewCard()
    {
        TableLayoutPanel card = Card("Preview", 4); card.RowStyles.Add(new RowStyle(SizeType.AutoSize)); card.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); card.RowStyles.Add(new RowStyle(SizeType.AutoSize)); card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var preview = new Panel { Name = "CommercialMediaPreview", Dock = DockStyle.Fill, BackColor = Color.Black, Margin = new Padding(0, 7, 0, 6) }; preview.Controls.Add(_playerHost); card.Controls.Add(preview, 0, 1);
        _time.Font = new Font(Font, FontStyle.Bold); card.Controls.Add(_time, 0, 2);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 0) }; controls.Controls.Add(_playPause); controls.Controls.Add(_seekBack); controls.Controls.Add(_seekForward); card.Controls.Add(controls, 0, 3); return card;
    }

    private Control BuildTimelineCard()
    {
        TableLayoutPanel card = Card("Detected Boundaries", 7); card.RowStyles.Add(new RowStyle(SizeType.AutoSize)); card.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); card.RowStyles.Add(new RowStyle(SizeType.AutoSize)); card.RowStyles.Add(new RowStyle(SizeType.AutoSize)); card.RowStyles.Add(new RowStyle(SizeType.AutoSize)); card.RowStyles.Add(new RowStyle(SizeType.Absolute, 105)); card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _timeline.Margin = new Padding(0, 7, 0, 6); card.Controls.Add(_timeline, 0, 1);
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        navigation.Controls.Add(_previousBoundary); navigation.Controls.Add(_nextBoundary); navigation.Controls.Add(_addBoundary); navigation.Controls.Add(_removeBoundary); navigation.Controls.Add(_resetBoundary); navigation.Controls.Add(_playAcrossBoundary);
        navigation.Controls.Add(new Label { Text = "Zoom", AutoSize = true, Margin = new Padding(10, 7, 3, 0) }); navigation.Controls.Add(_zoomOut); navigation.Controls.Add(_zoomIn); navigation.Controls.Add(_zoomFit); card.Controls.Add(navigation, 0, 2);
        card.Controls.Add(_timelineScroll, 0, 3);
        _boundaryDetails.Dock = DockStyle.Fill; _boundaryDetails.AutoEllipsis = true; _boundaryDetails.MaximumSize = new Size(0, 54); card.Controls.Add(_boundaryDetails, 0, 4);
        var frames = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 4, 0, 0) }; frames.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); frames.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); frames.Controls.Add(_beforeFrame, 0, 0); frames.Controls.Add(_afterFrame, 1, 0); card.Controls.Add(frames, 0, 5);
        var frameLabels = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty }; frameLabels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); frameLabels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); frameLabels.Controls.Add(_beforeLabel, 0, 0); frameLabels.Controls.Add(_afterLabel, 1, 0); card.Controls.Add(frameLabels, 0, 6);
        _toolTip.SetToolTip(_timeline, "Drag NOW or a boundary to seek immediately. Mouse wheel zooms; Shift+wheel pans when zoomed.");
        return card;
    }

    private Control BuildReviewArea()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty }; panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        TableLayoutPanel gridCard = Card("Proposed Segments", 2); gridCard.RowStyles.Add(new RowStyle(SizeType.AutoSize)); gridCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); ConfigureSegmentsGrid(); gridCard.Controls.Add(_segments, 0, 1); panel.Controls.Add(gridCard, 0, 0); panel.Controls.Add(BuildExportCard(), 0, 1); return panel;
    }

    private Control BuildExportCard()
    {
        TableLayoutPanel card = Card("Export", 3); card.Margin = new Padding(0, 8, 0, 0);
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize)); card.RowStyles.Add(new RowStyle(SizeType.AutoSize)); card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        row.Controls.Add(new Label { Text = "Output directory", AutoSize = true, Margin = new Padding(0, 7, 4, 0) }); _outputDirectory.Width = 220; row.Controls.Add(_outputDirectory); row.Controls.Add(_browseOutput);
        row.Controls.Add(new Label { Text = "Mode", AutoSize = true, Margin = new Padding(10, 7, 4, 0) }); row.Controls.Add(_exportMode);
        row.Controls.Add(new Label { Text = "Naming", AutoSize = true, Margin = new Padding(10, 7, 4, 0) }); row.Controls.Add(_namingPattern); row.Controls.Add(_exportSelected); row.Controls.Add(_exportAll); row.Controls.Add(_exportCancel); row.Controls.Add(_openOutputFolder); card.Controls.Add(row, 0, 1);
        _exportDetails.Margin = new Padding(0, 5, 0, 0); card.Controls.Add(_exportDetails, 0, 2);
        _toolTip.SetToolTip(_namingPattern, "Use {source} and {index:00}. A manually edited Output Name in the grid takes precedence.");
        _toolTip.SetToolTip(_exportMode, "Fast / Lossless stream copy is quick but cuts can align to nearby source keyframes. Accurate re-encode is frame exact.");
        return card;
    }

    private void ConfigureSegmentsGrid()
    {
        _segments.Dock = DockStyle.Fill; _segments.AllowUserToAddRows = false; _segments.AllowUserToDeleteRows = false; _segments.AllowUserToResizeRows = false; _segments.ReadOnly = false; _segments.MultiSelect = true; _segments.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _segments.AutoGenerateColumns = false; _segments.BackgroundColor = Color.White; _segments.BorderStyle = BorderStyle.FixedSingle; _segments.RowHeadersVisible = false;
        _segments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "#", Width = 38 });
        _segments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Start", Width = 90 });
        _segments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "End", Width = 90 });
        _segments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Duration", Width = 90 });
        _segments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Confidence", Width = 90 });
        _segments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Detection / Reason", FillWeight = 160, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _segments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Output Name", FillWeight = 120, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        for (int index = 0; index < 6; index++) _segments.Columns[index].ReadOnly = true;
    }

    private void WireEvents()
    {
        _browse.Click += async (_, _) => await BrowseAsync();
        _analyze.Click += async (_, _) => await AnalyzeAsync();
        _cancel.Click += (_, _) => _operationCts?.Cancel();
        _browseOutput.Click += (_, _) => BrowseOutputFolder();
        _outputDirectory.TextChanged += (_, _) => UpdateExportControlStates();
        _exportSelected.Click += async (_, _) => await ExportAsync(selectedOnly: true);
        _exportAll.Click += async (_, _) => await ExportAsync(selectedOnly: false);
        _exportCancel.Click += (_, _) => _exportCts?.Cancel();
        _openOutputFolder.Click += (_, _) => OpenOutputFolder();
        _advancedToggle.Click += (_, _) => SetAdvancedExpanded(!_advancedHost.Visible);
        _resetPreset.Click += (_, _) => ApplyPreset(_basePreset);
        _preset.SelectedIndexChanged += (_, _) => { if (!_applyingSettings && Enum.TryParse(_preset.Text, out CommercialDetectionPreset selected) && selected != CommercialDetectionPreset.Custom) ApplyPreset(selected); };
        foreach (Control control in PresetControls())
        {
            if (control is NumericUpDown number) number.ValueChanged += (_, _) => SettingsChanged();
            else if (control is CheckBox check) check.CheckedChanged += (_, _) => SettingsChanged();
        }
        _blackEnabled.CheckedChanged += (_, _) => UpdateAdvancedEnabledStates(); _silenceEnabled.CheckedChanged += (_, _) => UpdateAdvancedEnabledStates(); _sceneEnabled.CheckedChanged += (_, _) => UpdateAdvancedEnabledStates();
        _includeLowConfidence.CheckedChanged += (_, _) => { if (!_applyingSettings) { _minimumConfidence.Value = _includeLowConfidence.Checked ? 25 : Math.Max(45, _minimumConfidence.Value); } };
        _timeline.PositionChanged += (_, seconds) => SeekTo(seconds, immediate: true);
        _timeline.BoundarySelected += (_, id) => SelectBoundary(id, synchronizeGrid: true, seek: true);
        _timeline.BoundaryMovePreview += (_, seconds) => SeekTo(seconds, immediate: true);
        _timeline.BoundaryMoved += (_, request) => MoveBoundary(request.BoundaryId, request.TimestampSeconds);
        _timeline.ViewportChanged += (_, fraction) => UpdateTimelineScroll(fraction);
        _timelineScroll.Scroll += (_, _) => _timeline.SetPanFraction(_timelineScroll.Value / 1000d);
        _segments.SelectionChanged += (_, _) => SeekSelectedSegment();
        _segments.CellEndEdit += Segments_CellEndEdit;
        _playPause.Click += (_, _) => TogglePlayback(); _seekBack.Click += (_, _) => SeekTo(CurrentPosition() - 5, immediate: true); _seekForward.Click += (_, _) => SeekTo(CurrentPosition() + 5, immediate: true);
        _previousBoundary.Click += (_, _) => SelectAdjacentBoundary(-1); _nextBoundary.Click += (_, _) => SelectAdjacentBoundary(1);
        _addBoundary.Click += (_, _) => AddBoundaryAt(CurrentPosition()); _removeBoundary.Click += (_, _) => RemoveSelectedBoundary(); _resetBoundary.Click += (_, _) => ResetSelectedBoundary(); _playAcrossBoundary.Click += (_, _) => PlayAcrossSelectedBoundary();
        _zoomOut.Click += (_, _) => _timeline.ZoomBy(.8, CurrentPosition()); _zoomIn.Click += (_, _) => _timeline.ZoomBy(1.25, CurrentPosition()); _zoomFit.Click += (_, _) => _timeline.ResetZoom();
        _playbackTimer.Tick += (_, _) => SynchronizePlayback();
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += async (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) await LoadSourceAsync(files[0]); };
        KeyDown += CommercialDetectorForm_KeyDown;
        ContextMenuStrip reviewMenu = BuildReviewContextMenu(); _timeline.ContextMenuStrip = reviewMenu; _segments.ContextMenuStrip = reviewMenu;
        FormClosing += CommercialDetectorForm_FormClosing;
    }

    private async Task BrowseAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.m4v;*.webm;*.ts;*.m2ts|All files|*.*", Title = "Choose source video" };
        if (dialog.ShowDialog(this) == DialogResult.OK) await LoadSourceAsync(dialog.FileName);
    }

    private void BrowseOutputFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose where commercial segments will be saved", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) { _outputDirectory.Text = dialog.SelectedPath; UpdateExportControlStates(); }
    }

    private bool AskToRestoreSavedAnalysis() => MessageBox.Show(this,
        "MediaFlux found a saved review for this exact video.\r\n\r\nYes restores its proposed boundaries and edits. No starts a fresh analysis.",
        "Restore previous commercial analysis?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private void RestoreSavedAnalysis(CommercialAnalysisSnapshot snapshot)
    {
        CommercialReviewBoundary[] boundaries = snapshot.Boundaries.Select(item => new CommercialReviewBoundary(
            Guid.NewGuid(), item.TimestampSeconds, item.OriginalDetectedTimestampSeconds, item.Confidence,
            Enum.TryParse(item.ConfidenceCategory, out CommercialDetectionConfidence confidence) ? confidence : CommercialDetectionConfidence.Low,
            item.Evidence, Enum.TryParse(item.Origin, out CommercialBoundaryOrigin origin) ? origin : CommercialBoundaryOrigin.Automatic)).ToArray();
        CommercialReviewSegment[] segments = snapshot.Segments.Select(item => new CommercialReviewSegment(item.Number, item.StartSeconds, item.EndSeconds, item.OutputName, item.IsOutputNameCustom)).ToArray();
        _review.Restore(_loadedSource!, _duration, boundaries, segments, snapshot.SuppressedAutomaticPositions);
        _hasAnalysisResult = true;
        ApplySettings(snapshot.Settings, Enum.TryParse(snapshot.DetectionPreset, out CommercialDetectionPreset preset) ? preset : CommercialDetectionPreset.Custom);
        RefreshReviewDisplay();
    }

    private void SaveAnalysis()
    {
        if (_loadedSource == null || !_hasAnalysisResult) return;
        try { _analysisStore.Save(_loadedSource, _duration, CurrentPreset(), ReadSettings(), _review); }
        catch (Exception ex) { ErrorLogService.Append(Application.StartupPath, "Save Commercial Detector analysis failed", _loadedSource, ex); }
    }

    private async Task ExportAsync(bool selectedOnly)
    {
        if (_exportCts != null || _loadedSource == null || !_hasAnalysisResult) return;
        int[] selectedNumbers = SelectedSegmentNumbers();
        IEnumerable<int>? selected = selectedOnly ? selectedNumbers : null;
        CommercialSegmentExportPlan plan = CommercialSegmentExportPlanner.CreatePlan(_loadedSource, _review.Segments, selected, _namingPattern.Text);
        if (plan.Errors.Count > 0) { _status.Text = plan.Errors[0]; _exportDetails.Text = string.Join("  ", plan.Errors); return; }

        string folder = _outputDirectory.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder)) folder = Path.GetDirectoryName(_loadedSource) ?? "";
        string fullFolder;
        try { fullFolder = Path.GetFullPath(folder); }
        catch (Exception ex) { _status.Text = "Choose a valid output folder."; _exportDetails.Text = ex.Message; return; }

        bool overwrite = false;
        VideoSplitterSegment[] segments = plan.Segments.ToArray();
        string[] existing = segments.Select(segment => Path.Combine(fullFolder, OutputPathService.SanitizeFileName(segment.OutputFileName))).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (existing.Length > 0)
        {
            OutputConflictChoice choice = OutputConflictDialog.Show(this, existing);
            if (choice == OutputConflictChoice.Cancel) return;
            if (choice == OutputConflictChoice.AutoRename)
            {
                segments = VideoSplitterForm.AutoRenameConflictingOutputs(segments, fullFolder);
                _exportDetails.Text = "Existing output conflicts will use unique names for this export.";
            }
            else overwrite = true;
        }

        VideoSplitterProcessingMode mode = _exportMode.SelectedIndex == 1 ? VideoSplitterProcessingMode.AccurateReencode : VideoSplitterProcessingMode.StreamCopy;
        if (mode == VideoSplitterProcessingMode.StreamCopy && !_streamCopyNoticeShown)
        {
            _streamCopyNoticeShown = true;
            MessageBox.Show(this, "Fast / Lossless stream copy is fast and preserves source streams, but start and end points can align to nearby source keyframes rather than the exact reviewed frame.", "Keyframe boundary note", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        var request = new VideoSplitterExportRequest
        {
            SourcePath = _loadedSource,
            OutputFolder = fullFolder,
            Segments = segments,
            SourceDurationSeconds = _duration,
            Mode = mode,
            OverwriteExistingOutput = overwrite,
            VideoEncoder = ResolveConfiguredEncoder(),
            EncoderPreset = _config.LastEncoderPreset,
            QualityValue = _config.LastQualityValue
        };
        IReadOnlyList<string> validation = VideoSplitterExportService.Validate(request);
        if (validation.Count > 0) { _status.Text = validation[0]; _exportDetails.Text = string.Join("  ", validation); return; }

        _exportCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _exportStopwatch.Restart(); _progress.Value = 0; SetExportInteractionState(active: true);
        _status.Text = $"Exporting {segments.Length} segment(s)…";
        try
        {
            var service = new VideoSplitterExportService(AppPaths.InstallDirectory, _config.FfmpegPath, _config.FfprobePath);
            var progress = new Progress<VideoSplitterExportProgress>(UpdateExportProgress);
            VideoSplitterExportResult result = await service.ExportAsync(request, progress, _exportCts.Token);
            HandleExportResult(result, fullFolder);
            RememberOutputFolder(fullFolder);
        }
        catch (OperationCanceledException) { _status.Text = "Export cancelled. Completed staged outputs were retained safely."; }
        catch (Exception ex) { ErrorLogService.Append(Application.StartupPath, "Commercial Detector export failed", _loadedSource, ex); _status.Text = "Export failed unexpectedly. See the Error Log for details."; _exportDetails.Text = ex.Message; }
        finally { _exportStopwatch.Stop(); _exportCts?.Dispose(); _exportCts = null; SetExportInteractionState(active: false); }
    }

    private string ResolveConfiguredEncoder()
    {
        try { return EncoderRegistry.Default.Resolve(_config.LastEncoderId, VideoEncoderCompatibility.ParseCodecFamily(_config.LastVideoCodec)).Selection.FfmpegCodec; }
        catch { return "libx264"; }
    }

    private void UpdateExportProgress(VideoSplitterExportProgress update)
    {
        if (IsDisposed) return;
        int percent = (int)Math.Clamp(update.Percent ?? 0, 0, 100); _progress.Value = percent;
        string speed = update.Speed is > 0 ? $" · {update.Speed:0.00}x" : "";
        _status.Text = $"Exporting segment {update.SegmentNumber} of {update.SegmentCount}: {update.OutputFileName}";
        _exportDetails.Text = $"{update.Status} · {percent}% · elapsed {_exportStopwatch.Elapsed:hh\\:mm\\:ss}{speed}";
    }

    private void HandleExportResult(VideoSplitterExportResult result, string folder)
    {
        if (result.Warnings.Count > 0) { _status.Text = result.Warnings[0]; _exportDetails.Text = string.Join("  ", result.Warnings); return; }
        VideoSplitterSegmentExportResult[] succeeded = result.Segments.Where(segment => segment.Success).ToArray();
        VideoSplitterSegmentExportResult[] failed = result.Segments.Where(segment => !segment.Success && !segment.WasCanceled).ToArray();
        foreach (VideoSplitterSegmentExportResult failure in failed)
            ErrorLogService.Append(Application.StartupPath, "Commercial Detector segment export failed", _loadedSource, details: $"Segment {failure.Segment.Number}: {failure.ErrorMessage}\r\n{failure.DiagnosticOutput}\r\n{failure.CleanupMessage}");
        if (result.Success) _progress.Value = 100;
        _status.Text = result.Success ? $"Completed {succeeded.Length} segment(s)." : result.WasCanceled ? $"Canceled. {succeeded.Length} completed output(s) were retained." : $"Completed {succeeded.Length}; failed {failed.Length}. See Error Log for failures.";
        _exportDetails.Text = $"{_status.Text} Output folder: {folder} · elapsed {_exportStopwatch.Elapsed:hh\\:mm\\:ss}";
    }

    private void OpenOutputFolder()
    {
        if (!Directory.Exists(_outputDirectory.Text)) return;
        try { Process.Start(new ProcessStartInfo { FileName = _outputDirectory.Text, UseShellExecute = true }); }
        catch (Exception ex) { ErrorLogService.Append(Application.StartupPath, "Open Commercial Detector output folder failed", _outputDirectory.Text, ex); }
    }

    private void RememberOutputFolder(string folder)
    {
        _outputDirectory.Text = folder;
        _config.LastOutputFolders.RemoveAll(item => item.Equals(folder, StringComparison.OrdinalIgnoreCase));
        _config.LastOutputFolders.Insert(0, folder);
        if (_config.LastOutputFolders.Count > _config.FolderHistoryLimit) _config.LastOutputFolders.RemoveRange(_config.FolderHistoryLimit, _config.LastOutputFolders.Count - _config.FolderHistoryLimit);
        CapturePreferences();
        try { _config.Save(_configPath); } catch (Exception ex) { ErrorLogService.Append(Application.StartupPath, "Save Commercial Detector output folder failed", exception: ex); }
    }

    private async Task LoadSourceAsync(string path)
    {
        if (_exportCts != null || _viewState is CommercialDetectorViewState.Loading or CommercialDetectorViewState.Analyzing) return;
        if (!File.Exists(path)) { SetState(CommercialDetectorViewState.Error, "The selected source file no longer exists."); return; }
        CancelOperation(); _operationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token); CancellationToken token = _operationCts.Token;
        ClearResults(); ClearFrameCache(); _timeline.ResetZoom(); _loadedSource = null; _hasAnalysisResult = false; _duration = 0; _sourceFrameRate = 0; _sourcePath.Text = path; _mediaInfo.Text = "Reading media information…"; SetState(CommercialDetectorViewState.Loading, "Reading media information…");
        try
        {
            MediaProbeResult result = await _probe.ProbeAsync(path, token);
            MediaProbeStreamInfo? video = result.Streams.FirstOrDefault(stream => stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase) && !(stream.Dispositions.TryGetValue("attached_pic", out bool attached) && attached));
            if (!result.Success || result.DurationSeconds is not > 0 || video == null) { SetState(CommercialDetectorViewState.Error, string.IsNullOrWhiteSpace(result.ErrorMessage) ? "This file does not contain a readable video stream." : result.ErrorMessage); return; }
            _loadedSource = path; _duration = result.DurationSeconds.Value; _sourceFrameRate = video.FrameRate ?? 0; _review.Initialize(path, _duration, Array.Empty<CommercialBoundary>()); _timeline.SetSource(_duration, _review.Boundaries); UpdateTime(0);
            if (string.IsNullOrWhiteSpace(_outputDirectory.Text)) _outputDirectory.Text = _config.LastOutputFolders.FirstOrDefault() ?? Path.GetDirectoryName(path) ?? "";
            MediaProbeStreamInfo? audio = result.Streams.FirstOrDefault(stream => stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase));
            _mediaInfo.Text = $"Duration: {VideoSplitterForm.FormatTime(_duration)}    Resolution: {video.Width ?? 0}×{video.Height ?? 0}    Video: {DisplayCodec(video.CodecName)}    Audio: {DisplayCodec(audio?.CodecName)}";
            LoadPlayer(path);
            CommercialAnalysisLookup saved = _analysisStore.Find(path, _duration);
            if (saved.Match == CommercialAnalysisMatch.Exact && saved.Snapshot != null && AskToRestoreSavedAnalysis())
            {
                RestoreSavedAnalysis(saved.Snapshot);
                SetState(CommercialDetectorViewState.Completed, $"Restored { _review.Boundaries.Count } proposed boundaries and {_review.Segments.Count} segments.");
            }
            else
            {
                if (saved.Match == CommercialAnalysisMatch.Stale) { _analysisStore.Remove(path); SetState(CommercialDetectorViewState.Ready, "A previous analysis did not match this version of the video. Analyze again to create current boundaries."); }
                else SetState(CommercialDetectorViewState.Ready, "Ready to analyze. The source remains unchanged.");
            }
        }
        catch (OperationCanceledException) { if (!_lifetimeCts.IsCancellationRequested) SetState(CommercialDetectorViewState.Cancelled, "Source loading cancelled."); }
        catch (Exception ex) { ErrorLogService.Append(Application.StartupPath, "Commercial Detector source load failed", path, ex); SetState(CommercialDetectorViewState.Error, "MediaFlux could not read this source. See the Error Log for details."); }
        finally { DisposeOperation(); }
    }

    private async Task AnalyzeAsync()
    {
        if (_exportCts != null || _viewState == CommercialDetectorViewState.Analyzing || _loadedSource == null) return;
        CommercialDetectionSettings settings = ReadSettings();
        ReanalysisChoice reanalysis = ReanalysisChoice.Everything;
        if (_review.HasManualChanges)
        {
            reanalysis = ReanalysisChoiceDialog.ShowChoice(this);
            if (reanalysis == ReanalysisChoice.Cancel) return;
        }
        CancelOperation(); _operationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token); CancellationToken token = _operationCts.Token;
        SetState(CommercialDetectorViewState.Analyzing, "Reading media information…");
        try
        {
            var detector = new CommercialDetectionService(AppPaths.InstallDirectory, _config.FfmpegPath, _config.FfprobePath, message => Debug.WriteLine(message));
            var progress = new Progress<CommercialDetectionProgress>(update => { _progress.Value = (int)Math.Clamp(update.Percent ?? 0, 0, 100); _status.Text = StageText(update.Stage); });
            CommercialDetectionResult result = await detector.AnalyzeAsync(_loadedSource, settings, progress, token);
            if (!result.Success) { SetState(CommercialDetectorViewState.Error, result.Warnings.FirstOrDefault() ?? "Analysis failed."); return; }
            PopulateResults(result, reanalysis == ReanalysisChoice.KeepManual, settings.CorrelationToleranceSeconds);
            string warning = result.Warnings.Count == 0 ? "" : $" {string.Join(" ", result.Warnings)}";
            if (_review.Boundaries.Count == 0) SetState(CommercialDetectorViewState.Completed, "No boundaries were detected. Try Aggressive mode or adjust Advanced Settings." + warning);
            else SetState(CommercialDetectorViewState.Completed, $"Analysis complete: {_review.Boundaries.Count} boundaries and {_review.Segments.Count} segments." + warning);
        }
        catch (OperationCanceledException) { if (!_lifetimeCts.IsCancellationRequested) SetState(CommercialDetectorViewState.Cancelled, "Analysis cancelled. The source was not modified."); }
        catch (Exception ex) { ErrorLogService.Append(Application.StartupPath, "Commercial Detector analysis failed", _loadedSource, ex); SetState(CommercialDetectorViewState.Error, "Analysis could not be completed. See the Error Log for details."); }
        finally { DisposeOperation(); }
    }

    private void PopulateResults(CommercialDetectionResult result, bool keepManual, double duplicateToleranceSeconds)
    {
        if (keepManual) _review.ApplyReanalysis(result.Boundaries, keepManualBoundaries: true, duplicateToleranceSeconds);
        else _review.Initialize(result.SourcePath, result.SourceDurationSeconds, result.Boundaries);
        _hasAnalysisResult = true;
        RefreshReviewDisplay();
        SaveAnalysis();
    }

    private void RefreshReviewDisplay(int preferredSegmentIndex = -1)
    {
        _synchronizingSelection = true;
        try
        {
            if (_selectedBoundaryId.HasValue && !_review.Boundaries.Any(boundary => boundary.Id == _selectedBoundaryId.Value)) _selectedBoundaryId = null;
            _timeline.SetSource(_duration, _review.Boundaries, _selectedBoundaryId, preservePosition: true);
            _segments.Rows.Clear();
            foreach (CommercialReviewSegment segment in _review.Segments)
            {
                CommercialReviewBoundary? boundary = BoundaryAt(segment.StartSeconds);
                string confidence = boundary == null ? "—" : boundary.Origin == CommercialBoundaryOrigin.Manual ? "Manual" : $"{boundary.ConfidenceCategory} ({boundary.Confidence}%)";
                string reason = boundary == null ? "Source start" : boundary.Origin == CommercialBoundaryOrigin.Manual ? "Manual boundary" : string.Join(" + ", boundary.Evidence.Select(evidence => SignalName(evidence.Kind)).Distinct());
                int row = _segments.Rows.Add(segment.Number, VideoSplitterForm.FormatTime(segment.StartSeconds), VideoSplitterForm.FormatTime(segment.EndSeconds), VideoSplitterForm.FormatTime(segment.DurationSeconds), confidence, reason, segment.OutputName);
                _segments.Rows[row].Tag = segment;
            }
            int high = _review.Boundaries.Count(boundary => boundary.Origin != CommercialBoundaryOrigin.Manual && boundary.ConfidenceCategory == CommercialDetectionConfidence.High); int medium = _review.Boundaries.Count(boundary => boundary.Origin != CommercialBoundaryOrigin.Manual && boundary.ConfidenceCategory == CommercialDetectionConfidence.Medium); int low = _review.Boundaries.Count(boundary => boundary.Origin != CommercialBoundaryOrigin.Manual && boundary.ConfidenceCategory == CommercialDetectionConfidence.Low); int manual = _review.Boundaries.Count(boundary => boundary.Origin == CommercialBoundaryOrigin.Manual);
            _summary.Text = $"Boundaries: {_review.Boundaries.Count}    Segments: {_review.Segments.Count}    Confidence — High: {high}  Medium: {medium}  Low: {low}    Manual: {manual}";
            int target = preferredSegmentIndex;
            if (_selectedBoundaryId.HasValue)
            {
                CommercialReviewBoundary? selected = SelectedBoundary();
                if (selected != null) target = _review.Segments.ToList().FindIndex(segment => Math.Abs(segment.StartSeconds - selected.TimestampSeconds) < .001);
            }
            if (_segments.Rows.Count > 0 && target >= 0) { target = Math.Clamp(target, 0, _segments.Rows.Count - 1); _segments.ClearSelection(); _segments.Rows[target].Selected = true; _segments.CurrentCell = _segments.Rows[target].Cells[0]; }
        }
        finally { _synchronizingSelection = false; }
        UpdateBoundaryDetails(); UpdateReviewControlStates(); UpdateExportControlStates();
    }

    private void SelectBoundary(Guid? id, bool synchronizeGrid, bool seek)
    {
        _selectedBoundaryId = id; _timeline.SelectBoundary(id); CommercialReviewBoundary? boundary = SelectedBoundary();
        if (boundary != null)
        {
            if (seek) SeekTo(boundary.TimestampSeconds, immediate: true);
            if (synchronizeGrid)
            {
                int index = _review.Segments.ToList().FindIndex(segment => Math.Abs(segment.StartSeconds - boundary.TimestampSeconds) < .001);
                if (index >= 0 && index < _segments.Rows.Count)
                {
                    _synchronizingSelection = true; try { _segments.ClearSelection(); _segments.Rows[index].Selected = true; _segments.CurrentCell = _segments.Rows[index].Cells[0]; } finally { _synchronizingSelection = false; }
                }
            }
        }
        UpdateBoundaryDetails(); UpdateReviewControlStates(); _ = RefreshSelectedBoundaryFramesAsync();
    }

    private void SelectAdjacentBoundary(int direction)
    {
        if (_review.Boundaries.Count == 0) return; int index;
        if (_selectedBoundaryId.HasValue) index = _review.Boundaries.ToList().FindIndex(boundary => boundary.Id == _selectedBoundaryId.Value) + direction;
        else if (direction < 0) index = _review.Boundaries.ToList().FindLastIndex(boundary => boundary.TimestampSeconds < CurrentPosition());
        else index = _review.Boundaries.ToList().FindIndex(boundary => boundary.TimestampSeconds > CurrentPosition());
        if (index >= 0 && index < _review.Boundaries.Count) SelectBoundary(_review.Boundaries[index].Id, synchronizeGrid: true, seek: true);
    }

    private void AddBoundaryAt(double timestamp)
    {
        if (!_review.TryAddBoundary(timestamp, out CommercialReviewBoundary? added)) { _status.Text = "A boundary must be inside the source and distinct from existing boundaries."; return; }
        _selectedBoundaryId = added!.Id; RefreshReviewDisplay(); SelectBoundary(added.Id, synchronizeGrid: true, seek: true); SaveAnalysis(); _status.Text = $"Manual boundary added at {VideoSplitterForm.FormatTime(timestamp)}.";
    }

    private void RemoveSelectedBoundary()
    {
        CommercialReviewBoundary? selected = SelectedBoundary(); if (selected == null) return; int previousIndex = _review.Boundaries.ToList().FindIndex(boundary => boundary.Id == selected.Id);
        if (!_review.TryRemoveBoundary(selected.Id)) return; _selectedBoundaryId = null; RefreshReviewDisplay(Math.Max(0, previousIndex));
        if (_review.Boundaries.Count > 0) SelectBoundary(_review.Boundaries[Math.Min(previousIndex, _review.Boundaries.Count - 1)].Id, synchronizeGrid: true, seek: false);
        SaveAnalysis(); _status.Text = "Boundary removed; segments were regenerated.";
    }

    private void MoveBoundary(Guid id, double timestamp)
    {
        if (!_review.TryMoveBoundary(id, timestamp))
        {
            CommercialReviewBoundary? original = _review.Boundaries.FirstOrDefault(boundary => boundary.Id == id);
            _timeline.SetSource(_duration, _review.Boundaries, id, preservePosition: true);
            if (original != null) SeekTo(original.TimestampSeconds, immediate: true);
            _status.Text = "Boundary move rejected because it would be outside the source or duplicate another boundary.";
            return;
        }
        _selectedBoundaryId = id; RefreshReviewDisplay(); SelectBoundary(id, synchronizeGrid: true, seek: true); SaveAnalysis(); _status.Text = $"Boundary moved to {VideoSplitterForm.FormatTime(timestamp)}.";
    }

    private void ResetSelectedBoundary()
    {
        CommercialReviewBoundary? selected = SelectedBoundary(); if (selected == null || !_review.TryResetBoundary(selected.Id)) return;
        RefreshReviewDisplay(); SelectBoundary(selected.Id, synchronizeGrid: true, seek: true); SaveAnalysis(); _status.Text = "Boundary reset to its detected position.";
    }

    private void MergePrevious()
    {
        int index = SelectedSegmentIndex(); if (!_review.TryMergePrevious(index)) return; _selectedBoundaryId = null; RefreshReviewDisplay(Math.Max(0, index - 1)); SaveAnalysis(); _status.Text = "Segments merged.";
    }

    private void MergeNext()
    {
        int index = SelectedSegmentIndex(); if (!_review.TryMergeNext(index)) return; _selectedBoundaryId = null; RefreshReviewDisplay(index); SaveAnalysis(); _status.Text = "Segments merged.";
    }

    private void SplitSelectedSegmentAtNow()
    {
        int index = SelectedSegmentIndex(); if (!_review.TrySplitSegment(index, CurrentPosition())) { _status.Text = "Move NOW inside the selected segment before splitting."; return; }
        CommercialReviewBoundary? added = _review.Boundaries.OrderBy(boundary => Math.Abs(boundary.TimestampSeconds - CurrentPosition())).FirstOrDefault(); _selectedBoundaryId = added?.Id; RefreshReviewDisplay(index + 1); if (added != null) SelectBoundary(added.Id, true, false); SaveAnalysis(); _status.Text = "Selected segment split at NOW.";
    }

    private void Segments_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 6) return; string proposed = Convert.ToString(_segments.Rows[e.RowIndex].Cells[e.ColumnIndex].Value)?.Trim() ?? "";
        if (!_review.TrySetOutputName(e.RowIndex, proposed)) { _status.Text = "Output name cannot be empty."; RefreshReviewDisplay(e.RowIndex); return; }
        RefreshReviewDisplay(e.RowIndex); SaveAnalysis(); _status.Text = "Custom output name saved for this review session.";
    }

    private void UpdateBoundaryDetails()
    {
        CommercialReviewBoundary? boundary = SelectedBoundary();
        if (boundary == null) { _boundaryDetails.Text = "Select a boundary to inspect its evidence."; SetPreviewImage(_beforeFrame, null); SetPreviewImage(_afterFrame, null); return; }
        string origin = boundary.Origin switch { CommercialBoundaryOrigin.Automatic => "Automatic", CommercialBoundaryOrigin.AutomaticMoved => $"Automatic, moved from {VideoSplitterForm.FormatTime(boundary.OriginalDetectedTimestampSeconds ?? boundary.TimestampSeconds)}", _ => "Manual" };
        string confidence = boundary.Origin == CommercialBoundaryOrigin.Manual ? "Manual — no detector confidence" : $"{boundary.Confidence}% ({boundary.ConfidenceCategory})";
        string evidence = boundary.Evidence.Count == 0 ? "Manual boundary" : string.Join("; ", boundary.Evidence.Select(DescribeEvidence));
        _boundaryDetails.Text = $"{VideoSplitterForm.FormatTime(boundary.TimestampSeconds)}    {confidence}    {origin}\r\n{evidence}";
    }

    private async Task RefreshSelectedBoundaryFramesAsync()
    {
        CommercialReviewBoundary? boundary = SelectedBoundary(); if (boundary == null || _loadedSource == null) return;
        _framePreviewCts?.Cancel(); _framePreviewCts?.Dispose(); _framePreviewCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token); CancellationToken token = _framePreviewCts.Token; Guid requestedId = boundary.Id;
        (double before, double after) = ResolveBoundaryPreviewTimes(boundary.TimestampSeconds, _duration);
        _beforeLabel.Text = $"BEFORE  {VideoSplitterForm.FormatTime(before)}  (loading…)"; _afterLabel.Text = $"AFTER  {VideoSplitterForm.FormatTime(after)}  (loading…)";
        try
        {
            Image?[] images = await Task.WhenAll(GetCachedFrameAsync(before, token), GetCachedFrameAsync(after, token));
            if (token.IsCancellationRequested || _selectedBoundaryId != requestedId) { foreach (Image? image in images) image?.Dispose(); return; }
            SetPreviewImage(_beforeFrame, images[0]); SetPreviewImage(_afterFrame, images[1]); _beforeLabel.Text = $"BEFORE  {VideoSplitterForm.FormatTime(before)}"; _afterLabel.Text = $"AFTER  {VideoSplitterForm.FormatTime(after)}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) { ErrorLogService.Append(Application.StartupPath, "Commercial Detector boundary frames failed", _loadedSource, ex); _beforeLabel.Text = "BEFORE frame unavailable"; _afterLabel.Text = "AFTER frame unavailable"; } }
    }

    private async Task<Image?> GetCachedFrameAsync(double seconds, CancellationToken token)
    {
        int key = (int)Math.Round(seconds * 1000); if (_frameCache.TryGetValue(key, out Image? cached)) return new Bitmap(cached);
        Image? extracted = await _framePreview.ExtractAsync(_loadedSource!, seconds, _duration, _sourceFrameRate, 320, token); if (extracted == null) return null;
        if (_frameCache.Count >= 48) { int oldest = _frameCache.Keys.First(); _frameCache.Remove(oldest, out Image? removed); removed?.Dispose(); }
        _frameCache[key] = new Bitmap(extracted); return extracted;
    }

    private void PlayAcrossSelectedBoundary()
    {
        CommercialReviewBoundary? boundary = SelectedBoundary(); if (boundary == null) return; double start = Math.Max(0, boundary.TimestampSeconds - 3); _playAcrossEnd = Math.Min(_duration, boundary.TimestampSeconds + 3); SeekTo(start, immediate: true);
        try { dynamic player = _playerHost.Player; player.controls.currentPosition = start; _previewSeek.Complete(); player.controls.play(); SetPlaying(true); _playbackTimer.Start(); _status.Text = $"Playing across boundary from {VideoSplitterForm.FormatTime(start)} to {VideoSplitterForm.FormatTime(_playAcrossEnd.Value)}."; } catch { _playAcrossEnd = null; _status.Text = "The preview player could not play this boundary."; }
    }

    private ContextMenuStrip BuildReviewContextMenu()
    {
        var menu = new ContextMenuStrip(); ToolStripItem preview = menu.Items.Add("Preview"); ToolStripItem playAcross = menu.Items.Add("Play Across Boundary"); menu.Items.Add(new ToolStripSeparator()); ToolStripItem add = menu.Items.Add("Add Boundary Here"); ToolStripItem remove = menu.Items.Add("Remove Boundary"); ToolStripItem reset = menu.Items.Add("Reset to Detected Position"); menu.Items.Add(new ToolStripSeparator()); ToolStripItem mergePrevious = menu.Items.Add("Merge Previous"); ToolStripItem mergeNext = menu.Items.Add("Merge Next"); ToolStripItem split = menu.Items.Add("Split Selected Segment at NOW");
        preview.Click += (_, _) => { CommercialReviewBoundary? boundary = SelectedBoundary(); SeekTo(boundary?.TimestampSeconds ?? (_segments.CurrentRow?.Tag as CommercialReviewSegment)?.StartSeconds ?? CurrentPosition(), true); };
        playAcross.Click += (_, _) => PlayAcrossSelectedBoundary(); add.Click += (_, _) => AddBoundaryAt(menu.SourceControl == _timeline ? _timeline.ContextTimestampSeconds : CurrentPosition()); remove.Click += (_, _) => RemoveSelectedBoundary(); reset.Click += (_, _) => ResetSelectedBoundary(); mergePrevious.Click += (_, _) => MergePrevious(); mergeNext.Click += (_, _) => MergeNext(); split.Click += (_, _) => SplitSelectedSegmentAtNow();
        menu.Opening += (_, _) => { int segment = SelectedSegmentIndex(); CommercialReviewBoundary? boundary = SelectedBoundary(); preview.Enabled = _loadedSource != null; playAcross.Enabled = boundary != null; add.Enabled = _loadedSource != null && _viewState != CommercialDetectorViewState.Analyzing; remove.Enabled = boundary != null; reset.Enabled = boundary?.Origin == CommercialBoundaryOrigin.AutomaticMoved; mergePrevious.Enabled = segment > 0; mergeNext.Enabled = segment >= 0 && segment < _review.Segments.Count - 1; split.Enabled = segment >= 0 && segment < _review.Segments.Count && CurrentPosition() > _review.Segments[segment].StartSeconds + .001 && CurrentPosition() < _review.Segments[segment].EndSeconds - .001; };
        _segments.CellMouseDown += (_, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0) { _segments.ClearSelection(); _segments.Rows[e.RowIndex].Selected = true; _segments.CurrentCell = _segments.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)]; } };
        return menu;
    }

    private void CommercialDetectorForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ActiveControl is TextBox or NumericUpDown || _segments.IsCurrentCellInEditMode) return;
        if (e.KeyCode == Keys.Space) { TogglePlayback(); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Left && e.Control) { SelectAdjacentBoundary(-1); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Right && e.Control) { SelectAdjacentBoundary(1); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Left) SeekTo(CurrentPosition() - 5, true);
        else if (e.KeyCode == Keys.Right) SeekTo(CurrentPosition() + 5, true);
        else if (e.KeyCode == Keys.Delete) RemoveSelectedBoundary();
        else if (e.KeyCode == Keys.B) AddBoundaryAt(CurrentPosition());
    }

    private void UpdateTimelineScroll(double fraction) { _timelineScroll.Enabled = _timeline.IsZoomed; _timelineScroll.Value = Math.Clamp((int)Math.Round(fraction * 1000), _timelineScroll.Minimum, _timelineScroll.Maximum - _timelineScroll.LargeChange + 1); }
    private CommercialReviewBoundary? SelectedBoundary() => _selectedBoundaryId.HasValue ? _review.Boundaries.FirstOrDefault(boundary => boundary.Id == _selectedBoundaryId.Value) : null;
    private CommercialReviewBoundary? BoundaryAt(double timestamp) => _review.Boundaries.FirstOrDefault(boundary => Math.Abs(boundary.TimestampSeconds - timestamp) < .001);
    private int SelectedSegmentIndex() => _segments.CurrentRow?.Tag is CommercialReviewSegment segment ? _review.Segments.ToList().FindIndex(item => item.Number == segment.Number) : -1;
    private int[] SelectedSegmentNumbers() => _segments.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Tag).OfType<CommercialReviewSegment>().Select(segment => segment.Number).Distinct().OrderBy(number => number).ToArray();
    private static string DescribeEvidence(DetectionEvidence evidence) => evidence.StartSeconds.HasValue && evidence.EndSeconds.HasValue ? $"{SignalName(evidence.Kind)} {evidence.StartSeconds:0.###}–{evidence.EndSeconds:0.###}s" : $"{SignalName(evidence.Kind)} at {evidence.TimestampSeconds:0.###}s";
    internal static (double Before, double After) ResolveBoundaryPreviewTimes(double boundarySeconds, double durationSeconds) => (Math.Clamp(boundarySeconds - .25, 0, Math.Max(0, durationSeconds)), Math.Clamp(boundarySeconds + .25, 0, Math.Max(0, durationSeconds)));
    private static void SetPreviewImage(PictureBox box, Image? image) { Image? old = box.Image; box.Image = image; old?.Dispose(); }

    private void LoadPlayer(string path)
    {
        try { dynamic player = _playerHost.Player; player.settings.autoStart = false; player.uiMode = "none"; player.stretchToFit = false; player.enableContextMenu = false; player.URL = path; _previewSeek.Request(0); _playbackTimer.Start(); }
        catch (Exception ex) { ErrorLogService.Append(Application.StartupPath, "Commercial Detector preview load failed", path, ex); _status.Text = "Source loaded, but preview is unavailable."; }
    }

    private void TogglePlayback()
    {
        if (_loadedSource == null) return;
        try { _playAcrossEnd = null; dynamic player = _playerHost.Player; if (_isPlaying) player.controls.pause(); else player.controls.play(); SetPlaying(!_isPlaying); _playbackTimer.Start(); }
        catch { SetPlaying(false); _status.Text = "The preview player is unavailable for this file."; }
    }

    private void SeekTo(double seconds, bool immediate)
    {
        if (_loadedSource == null) return; seconds = Math.Clamp(seconds, 0, _duration); _previewSeek.Request(seconds); _timeline.PositionSeconds = seconds; UpdateTime(seconds);
        if (immediate && _immediateSeekThrottle.ElapsedMilliseconds >= 55)
        {
            try { ((dynamic)_playerHost.Player).controls.currentPosition = seconds; _previewSeek.Complete(); _immediateSeekThrottle.Restart(); }
            catch { /* Keep the pending seek; the playback timer applies it when WMP becomes ready. */ }
        }
    }

    private void SynchronizePlayback()
    {
        if (_loadedSource == null) return;
        try
        {
            dynamic player = _playerHost.Player; int state = Convert.ToInt32(player.playState, CultureInfo.InvariantCulture);
            if (_previewSeek.TryGet(out double pending) && PreviewSeekCoordinator.CanSeek(state)) { player.controls.currentPosition = pending; _previewSeek.Complete(); }
            double position = Convert.ToDouble(player.controls.currentPosition, CultureInfo.InvariantCulture); _timeline.PositionSeconds = position; UpdateTime(position); SetPlaying(state == 3);
            if (_playAcrossEnd.HasValue && position >= _playAcrossEnd.Value) { player.controls.pause(); player.controls.currentPosition = _playAcrossEnd.Value; _timeline.PositionSeconds = _playAcrossEnd.Value; UpdateTime(_playAcrossEnd.Value); _playAcrossEnd = null; SetPlaying(false); _status.Text = "Boundary preview finished."; }
        }
        catch { SetPlaying(false); }
    }

    private void SeekSelectedSegment()
    {
        if (_synchronizingSelection || _segments.CurrentRow?.Tag is not CommercialReviewSegment segment) return;
        CommercialReviewBoundary? boundary = BoundaryAt(segment.StartSeconds); _selectedBoundaryId = boundary?.Id; _timeline.SelectBoundary(_selectedBoundaryId); SeekTo(segment.StartSeconds, immediate: true); UpdateBoundaryDetails(); UpdateReviewControlStates(); _ = RefreshSelectedBoundaryFramesAsync();
    }

    private double CurrentPosition() => _timeline.PositionSeconds;
    private void UpdateTime(double position) => _time.Text = $"{VideoSplitterForm.FormatTime(position)} / {VideoSplitterForm.FormatTime(_duration)}";
    private void SetPlaying(bool playing) { _isPlaying = playing; _playPause.Text = playing ? "Pause" : "Play"; }

    private CommercialDetectionSettings ReadSettings() => new()
    {
        BlackDetectionEnabled = _blackEnabled.Checked, MinimumBlackDurationSeconds = (double)_blackDuration.Value, BlackPixelThreshold = (double)_blackThreshold.Value / 100d,
        SilenceDetectionEnabled = _silenceEnabled.Checked, MinimumSilenceDurationSeconds = (double)_silenceDuration.Value, SilenceThresholdDb = (double)_silenceDb.Value,
        SceneDetectionEnabled = _sceneEnabled.Checked, SceneThreshold = (double)_sceneThreshold.Value,
        CorrelationToleranceSeconds = (double)_correlationMs.Value / 1000d, MinimumSegmentDurationSeconds = (double)_minimumSegment.Value,
        PreferCommonCommercialLengths = _preferCommonLengths.Checked, MinimumBoundaryConfidence = (int)_minimumConfidence.Value,
        MinimumSceneOnlyConfidence = Math.Max((int)_minimumConfidence.Value, _basePreset switch { CommercialDetectionPreset.Conservative => 70, CommercialDetectionPreset.Aggressive => 25, _ => 55 })
    };

    private CommercialDetectionPreset CurrentPreset() => Enum.TryParse(_preset.Text, out CommercialDetectionPreset preset) ? preset : CommercialDetectionPreset.Custom;

    private void ApplyStoredPreferences()
    {
        CommercialDetectorPreferences preferences = _config.CommercialDetectorPreferences;
        _exportMode.SelectedIndex = Math.Clamp(preferences.ExportModeIndex, 0, Math.Max(0, _exportMode.Items.Count - 1));
        _namingPattern.Text = string.IsNullOrWhiteSpace(preferences.FilenameTemplate) ? CommercialSegmentExportDefaults.FilenameTemplate : preferences.FilenameTemplate;
        if (Enum.TryParse(preferences.DetectionPreset, out CommercialDetectionPreset preset) && preset != CommercialDetectionPreset.Custom)
            ApplyPreset(preset);
        else ApplySettings(preferences.Settings ?? CommercialDetectionSettings.Standard, CommercialDetectionPreset.Custom);
    }

    private void ApplySettings(CommercialDetectionSettings settings, CommercialDetectionPreset preset)
    {
        _applyingSettings = true;
        try
        {
            _basePreset = preset == CommercialDetectionPreset.Custom ? CommercialDetectionPreset.Standard : preset;
            _preset.SelectedItem = preset.ToString();
            _blackEnabled.Checked = settings.BlackDetectionEnabled; _blackDuration.Value = (decimal)settings.MinimumBlackDurationSeconds; _blackThreshold.Value = (decimal)(settings.BlackPixelThreshold * 100);
            _silenceEnabled.Checked = settings.SilenceDetectionEnabled; _silenceDuration.Value = (decimal)settings.MinimumSilenceDurationSeconds; _silenceDb.Value = (decimal)settings.SilenceThresholdDb;
            _sceneEnabled.Checked = settings.SceneDetectionEnabled; _sceneThreshold.Value = (decimal)settings.SceneThreshold; _correlationMs.Value = (decimal)(settings.CorrelationToleranceSeconds * 1000);
            _minimumSegment.Value = (decimal)settings.MinimumSegmentDurationSeconds; _preferCommonLengths.Checked = settings.PreferCommonCommercialLengths; _minimumConfidence.Value = settings.MinimumBoundaryConfidence; _includeLowConfidence.Checked = settings.MinimumBoundaryConfidence < 45;
            UpdateAdvancedEnabledStates();
        }
        finally { _applyingSettings = false; }
    }

    private void ApplyPreset(CommercialDetectionPreset preset)
    {
        if (preset == CommercialDetectionPreset.Custom) return;
        ApplySettings(CommercialDetectionSettings.FromPreset(preset), preset);
    }

    private void SettingsChanged()
    {
        if (_applyingSettings) return; _applyingSettings = true; try { _preset.SelectedItem = CommercialDetectionPreset.Custom.ToString(); } finally { _applyingSettings = false; } UpdateAdvancedEnabledStates();
    }

    private IEnumerable<Control> PresetControls() => new Control[] { _blackEnabled, _blackDuration, _blackThreshold, _silenceEnabled, _silenceDuration, _silenceDb, _sceneEnabled, _sceneThreshold, _correlationMs, _minimumSegment, _preferCommonLengths, _minimumConfidence, _includeLowConfidence };
    private void UpdateAdvancedEnabledStates() { _blackDuration.Enabled = _blackThreshold.Enabled = _blackEnabled.Checked; _silenceDuration.Enabled = _silenceDb.Enabled = _silenceEnabled.Checked; _sceneThreshold.Enabled = _sceneEnabled.Checked; }

    private void SetAdvancedExpanded(bool expanded)
    {
        if (_advancedHost.Controls.Count == 0) BuildAdvancedSettings(); _advancedHost.Visible = expanded; _advancedToggle.Text = expanded ? "Hide Advanced Settings ▲" : "Advanced Settings ▼"; _config.CommercialDetectorAdvancedExpanded = expanded;
    }

    private void CapturePreferences()
    {
        _config.CommercialDetectorPreferences = new CommercialDetectorPreferences
        {
            DetectionPreset = CurrentPreset().ToString(),
            Settings = ReadSettings(),
            ExportModeIndex = Math.Max(0, _exportMode.SelectedIndex),
            FilenameTemplate = string.IsNullOrWhiteSpace(_namingPattern.Text) ? CommercialSegmentExportDefaults.FilenameTemplate : _namingPattern.Text.Trim()
        };
    }

    private void SetState(CommercialDetectorViewState state, string status)
    {
        _viewState = state; CommercialDetectorControlState controls = CommercialDetectorStateRules.For(state); bool reviewEnabled = _loadedSource != null && state is not CommercialDetectorViewState.Loading and not CommercialDetectorViewState.Analyzing; _browse.Enabled = controls.CanBrowse; _analyze.Enabled = controls.CanAnalyze && _loadedSource != null; _cancel.Enabled = controls.CanCancel; _preset.Enabled = controls.CanChangeSettings; _advancedToggle.Enabled = controls.CanChangeSettings; _advancedHost.Enabled = controls.CanChangeSettings; _playPause.Enabled = _seekBack.Enabled = _seekForward.Enabled = reviewEnabled; _timeline.Enabled = _segments.Enabled = reviewEnabled; _status.Text = status;
        if (state is not CommercialDetectorViewState.Loading and not CommercialDetectorViewState.Analyzing) _progress.Value = state == CommercialDetectorViewState.Completed ? 100 : 0;
        UpdateReviewControlStates(); UpdateExportControlStates();
    }

    private void UpdateReviewControlStates()
    {
        bool editable = _exportCts == null && _loadedSource != null && _viewState is not CommercialDetectorViewState.Loading and not CommercialDetectorViewState.Analyzing; CommercialReviewBoundary? selected = SelectedBoundary();
        _previousBoundary.Enabled = _nextBoundary.Enabled = editable && _review.Boundaries.Count > 0; _addBoundary.Enabled = editable; _removeBoundary.Enabled = editable && selected != null; _resetBoundary.Enabled = editable && selected?.Origin == CommercialBoundaryOrigin.AutomaticMoved; _playAcrossBoundary.Enabled = editable && selected != null; _zoomIn.Enabled = _zoomOut.Enabled = _zoomFit.Enabled = _loadedSource != null;
    }

    private void UpdateExportControlStates()
    {
        bool ready = _loadedSource != null && _hasAnalysisResult && _viewState is not CommercialDetectorViewState.Loading and not CommercialDetectorViewState.Analyzing && _exportCts == null;
        _outputDirectory.Enabled = _browseOutput.Enabled = _exportMode.Enabled = _namingPattern.Enabled = ready;
        _exportAll.Enabled = ready && _review.Segments.Count > 0;
        _exportSelected.Enabled = ready && SelectedSegmentNumbers().Length > 0;
        _exportCancel.Enabled = _exportCts != null;
        _openOutputFolder.Enabled = Directory.Exists(_outputDirectory.Text);
    }

    private void SetExportInteractionState(bool active)
    {
        if (!active)
        {
            CommercialDetectorControlState controls = CommercialDetectorStateRules.For(_viewState);
            bool reviewEnabled = _loadedSource != null && _viewState is not CommercialDetectorViewState.Loading and not CommercialDetectorViewState.Analyzing;
            _browse.Enabled = controls.CanBrowse; _analyze.Enabled = controls.CanAnalyze && _loadedSource != null; _preset.Enabled = controls.CanChangeSettings; _advancedToggle.Enabled = controls.CanChangeSettings; _advancedHost.Enabled = controls.CanChangeSettings;
            _playPause.Enabled = _seekBack.Enabled = _seekForward.Enabled = reviewEnabled; _timeline.Enabled = _segments.Enabled = reviewEnabled;
            UpdateReviewControlStates(); UpdateExportControlStates();
            return;
        }
        _browse.Enabled = _analyze.Enabled = _preset.Enabled = _advancedToggle.Enabled = _advancedHost.Enabled = false;
        _playPause.Enabled = _seekBack.Enabled = _seekForward.Enabled = _timeline.Enabled = _segments.Enabled = false;
        UpdateReviewControlStates(); UpdateExportControlStates();
    }

    private void ClearResults(bool keepTimelineSource = false) { _selectedBoundaryId = null; _segments.Rows.Clear(); _summary.Text = "No analysis results yet."; _timeline.SetSource(keepTimelineSource ? _duration : 0, Array.Empty<CommercialReviewBoundary>()); UpdateBoundaryDetails(); UpdateExportControlStates(); }
    private void ClearFrameCache() { _framePreviewCts?.Cancel(); _framePreviewCts?.Dispose(); _framePreviewCts = null; foreach (Image image in _frameCache.Values) image.Dispose(); _frameCache.Clear(); SetPreviewImage(_beforeFrame, null); SetPreviewImage(_afterFrame, null); }
    private void CancelOperation() { try { _operationCts?.Cancel(); } catch { } DisposeOperation(); }
    private void DisposeOperation() { _operationCts?.Dispose(); _operationCts = null; }
    private static string StageText(CommercialDetectionStage stage) => stage switch { CommercialDetectionStage.ProbingSource => "Reading media information…", CommercialDetectionStage.DetectingBlack => "Detecting black/fade…", CommercialDetectionStage.DetectingSilence => "Detecting silence…", CommercialDetectionStage.DetectingScenes => "Detecting scene changes…", CommercialDetectionStage.CorrelatingCandidates => "Correlating boundaries…", CommercialDetectionStage.GeneratingSegments => "Building segments…", _ => "Analysis complete." };

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e); if (_config.CommercialDetectorWindowX != int.MinValue && _config.CommercialDetectorWindowY != int.MinValue) Location = new Point(_config.CommercialDetectorWindowX, _config.CommercialDetectorWindowY);
        ConfigureSplitter(_previewSplit, 380, 420, _config.CommercialDetectorPreviewSplitterDistance, Math.Max(380, _previewSplit.Width / 2)); ConfigureSplitter(_workspaceSplit, 330, 170, _config.CommercialDetectorWorkspaceSplitterDistance, Math.Max(330, _workspaceSplit.Height / 2));
        try { _playerHost.CreateControl(); dynamic player = _playerHost.Player; player.uiMode = "none"; player.stretchToFit = false; player.enableContextMenu = false; }
        catch (Exception ex) { ErrorLogService.Append(Application.StartupPath, "Initialize Commercial Detector preview failed", exception: ex); _status.Text = "Preview is unavailable on this computer; analysis remains available."; }
    }

    private void CommercialDetectorForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exportCts != null)
        {
            _exportCts.Cancel();
            e.Cancel = true;
            _status.Text = "Canceling active export and cleaning up staged files…";
            return;
        }
        _lifetimeCts.Cancel(); CancelOperation(); _framePreviewCts?.Cancel(); _playbackTimer.Stop(); try { ((dynamic)_playerHost.Player).close(); } catch { }
        if (WindowState == FormWindowState.Normal) { _config.CommercialDetectorWindowX = Left; _config.CommercialDetectorWindowY = Top; _config.CommercialDetectorWindowWidth = Width; _config.CommercialDetectorWindowHeight = Height; }
        _config.CommercialDetectorPreviewSplitterDistance = _previewSplit.SplitterDistance; _config.CommercialDetectorWorkspaceSplitterDistance = _workspaceSplit.SplitterDistance;
        CapturePreferences();
        try { _config.Save(_configPath); } catch (Exception ex) { ErrorLogService.Append(Application.StartupPath, "Save Commercial Detector window state failed", exception: ex); }
    }

    protected override void Dispose(bool disposing) { if (disposing) { ClearFrameCache(); _lifetimeCts.Dispose(); _operationCts?.Dispose(); _exportCts?.Dispose(); _playbackTimer.Dispose(); _toolTip.Dispose(); } base.Dispose(disposing); }
    private Size RestoreSize() => _config.CommercialDetectorWindowWidth >= MinimumSize.Width && _config.CommercialDetectorWindowHeight >= MinimumSize.Height ? new(_config.CommercialDetectorWindowWidth, _config.CommercialDetectorWindowHeight) : new(1320, 960);
    private static void ConfigureSplitter(SplitContainer splitter, int panel1Min, int panel2Min, int persisted, int preferred) { int available = (splitter.Orientation == Orientation.Vertical ? splitter.ClientSize.Width : splitter.ClientSize.Height) - splitter.SplitterWidth; if (available < panel1Min + panel2Min) return; splitter.Panel1MinSize = panel1Min; splitter.Panel2MinSize = panel2Min; splitter.SplitterDistance = Math.Clamp(persisted > 0 ? persisted : preferred, panel1Min, available - panel2Min); }
    private static string DisplayCodec(string? codec) => string.IsNullOrWhiteSpace(codec) ? "None" : codec.ToUpperInvariant();
    private static string SignalName(DetectionSignalKind kind) => kind switch { DetectionSignalKind.Black => "Black / Fade", DetectionSignalKind.Silence => "Silence", _ => "Scene Change" };
    private static Label ValueLabel(string text) => new() { Text = text, AutoSize = true, ForeColor = Color.FromArgb(55, 65, 81) };
    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Checked = true };
    private static NumericUpDown Number(decimal minimum, decimal maximum, decimal value, int decimals, decimal increment) => new() { Minimum = minimum, Maximum = maximum, Value = value, DecimalPlaces = decimals, Increment = increment, Width = 82 };
    private static PictureBox PreviewBox(string name) => new() { Name = name, Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(30, 41, 59), Margin = new Padding(2) };
    private static SplitContainer Split(string name, Orientation orientation) => new() { Name = name, Dock = DockStyle.Fill, Orientation = orientation, SplitterWidth = 6, BackColor = Color.FromArgb(203, 213, 225) };
    private TableLayoutPanel Card(string title, int rows) { var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows, BackColor = Color.White, Padding = new Padding(10), Margin = Padding.Empty }; card.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = Color.FromArgb(55, 65, 81) }, 0, 0); return card; }
    private static Control Field(string label, Control control) { var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) }; row.Controls.Add(new Label { Text = label, AutoSize = true, Width = 145, Margin = new Padding(0, 6, 4, 0) }); row.Controls.Add(control); return row; }
    private static Control SettingsGroup(string title, params Control[] controls) { var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8), Margin = new Padding(3), BackColor = Color.FromArgb(248, 250, 252) }; panel.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) }); panel.Controls.AddRange(controls); return panel; }
}

internal enum ReanalysisChoice { KeepManual, Everything, Cancel }

internal sealed class ReanalysisChoiceDialog : MediaFluxForm
{
    private ReanalysisChoice _choice = ReanalysisChoice.Cancel;
    private ReanalysisChoiceDialog()
    {
        Text = "Re-analyze commercial boundaries"; StartPosition = FormStartPosition.CenterParent; MinimumSize = new Size(560, 240); Size = new Size(640, 275); AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 9F); BackColor = Color.FromArgb(246, 248, 251);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18) }; root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { Text = "This review contains moved/manual boundaries or custom output names. Choose how the new detection results should be applied.", AutoSize = true, MaximumSize = new Size(590, 0) }, 0, 0);
        root.Controls.Add(new Label { Text = "Keep Manual Boundaries replaces prior untouched automatic detections, retains manual work, avoids nearby duplicates, and maps custom names to the closest overlapping segments.", AutoSize = true, MaximumSize = new Size(590, 0), ForeColor = Color.FromArgb(71, 85, 105), Margin = new Padding(0, 14, 0, 14) }, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false }; actions.Controls.Add(ChoiceButton("Cancel", ReanalysisChoice.Cancel)); actions.Controls.Add(ChoiceButton("Re-analyze Everything", ReanalysisChoice.Everything)); actions.Controls.Add(ChoiceButton("Re-analyze & Keep Manual Boundaries", ReanalysisChoice.KeepManual)); root.Controls.Add(actions, 0, 2); Controls.Add(root);
    }
    private Button ChoiceButton(string text, ReanalysisChoice choice) { var button = new Button { Text = text, AutoSize = true, Margin = new Padding(8, 0, 0, 0) }; button.Click += (_, _) => { _choice = choice; DialogResult = DialogResult.OK; Close(); }; return button; }
    internal static ReanalysisChoice ShowChoice(IWin32Window owner) { using var dialog = new ReanalysisChoiceDialog(); return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._choice : ReanalysisChoice.Cancel; }
}

internal sealed class CommercialDetectionTimelineControl : Control
{
    private const int Inset = 32;
    private const int MarkerHitRadius = 9;
    private double _duration;
    private double _position;
    private double _zoom = 1;
    private double _viewStart;
    private double? _hoverSeconds;
    private Guid? _selectedBoundaryId;
    private Guid? _dragBoundaryId;
    private double? _dragBoundarySeconds;
    private bool _draggingNow;
    private IReadOnlyList<CommercialReviewBoundary> _boundaries = Array.Empty<CommercialReviewBoundary>();
    internal event EventHandler<double>? PositionChanged;
    internal event EventHandler<Guid?>? BoundarySelected;
    internal event EventHandler<double>? BoundaryMovePreview;
    internal event EventHandler<CommercialBoundaryMoveRequest>? BoundaryMoved;
    internal event EventHandler<double>? ViewportChanged;
    internal double PositionSeconds { get => _position; set { _position = Math.Clamp(value, 0, _duration); Invalidate(); } }
    internal IReadOnlyList<CommercialReviewBoundary> Boundaries => _boundaries;
    internal bool IsZoomed => _zoom > 1.001;
    internal double ContextTimestampSeconds { get; private set; }
    internal CommercialDetectionTimelineControl() { DoubleBuffered = true; ResizeRedraw = true; BackColor = Color.White; MinimumSize = new Size(300, 100); Cursor = Cursors.Hand; TabStop = true; }
    internal void SetSource(double duration, IReadOnlyList<CommercialReviewBoundary> boundaries, Guid? selectedBoundaryId = null, bool preservePosition = false)
    {
        _duration = Math.Max(0, duration); if (!preservePosition) _position = 0; else _position = Math.Clamp(_position, 0, _duration); _boundaries = boundaries ?? Array.Empty<CommercialReviewBoundary>(); _selectedBoundaryId = selectedBoundaryId; ClampViewport(); Invalidate();
    }
    internal void SelectBoundary(Guid? id) { _selectedBoundaryId = id; Invalidate(); }
    internal void ZoomBy(double factor, double anchorSeconds)
    {
        if (_duration <= 0) return; double oldVisible = VisibleDuration; double anchor = Math.Clamp(anchorSeconds, _viewStart, _viewStart + oldVisible); double ratio = oldVisible <= 0 ? .5 : (anchor - _viewStart) / oldVisible; _zoom = Math.Clamp(_zoom * factor, 1, 40); double nextVisible = VisibleDuration; _viewStart = anchor - ratio * nextVisible; ClampViewport(); RaiseViewportChanged(); Invalidate();
    }
    internal void ResetZoom() { _zoom = 1; _viewStart = 0; RaiseViewportChanged(); Invalidate(); }
    internal void SetPanFraction(double fraction) { _viewStart = Math.Clamp(fraction, 0, 1) * Math.Max(0, _duration - VisibleDuration); ClampViewport(); RaiseViewportChanged(); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); Rectangle track = Track(); using var brush = new SolidBrush(Color.FromArgb(226, 232, 240)); e.Graphics.FillRectangle(brush, track);
        if (_duration <= 0) { TextRenderer.DrawText(e.Graphics, "Analyze a source to display proposed boundaries", Font, ClientRectangle, Color.FromArgb(100, 116, 139), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
        foreach (CommercialReviewBoundary boundary in _boundaries.Where(boundary => DisplayTimestamp(boundary) >= _viewStart && DisplayTimestamp(boundary) <= _viewStart + VisibleDuration))
        {
            bool selected = boundary.Id == _selectedBoundaryId; Color color = boundary.Origin == CommercialBoundaryOrigin.Manual ? Color.FromArgb(124, 58, 237) : boundary.ConfidenceCategory switch { CommercialDetectionConfidence.High => Color.FromArgb(22, 163, 74), CommercialDetectionConfidence.Medium => Color.FromArgb(217, 119, 6), _ => Color.FromArgb(220, 38, 38) };
            DrawMarker(e.Graphics, X(DisplayTimestamp(boundary), track), track, color, selected ? "SELECTED" : null, selected ? 4 : 2);
        }
        DrawMarker(e.Graphics, X(_position, track), track, Color.FromArgb(37, 99, 235), "NOW");
        string startText = (_viewStart <= .001 ? "START  " : "") + VideoSplitterForm.FormatTime(_viewStart); TextRenderer.DrawText(e.Graphics, startText, Font, new Point(track.Left, track.Bottom + 8), Color.FromArgb(100, 116, 139)); double viewEnd = Math.Min(_duration, _viewStart + VisibleDuration); string end = VideoSplitterForm.FormatTime(viewEnd) + (viewEnd >= _duration - .001 ? "  END" : ""); Size size = TextRenderer.MeasureText(end, Font); TextRenderer.DrawText(e.Graphics, end, Font, new Point(track.Right - size.Width, track.Bottom + 8), Color.FromArgb(100, 116, 139));
        if (_hoverSeconds.HasValue) { string hover = VideoSplitterForm.FormatTime(_hoverSeconds.Value); Size hoverSize = TextRenderer.MeasureText(hover, Font); int x = Math.Clamp((int)X(_hoverSeconds.Value, track) - hoverSize.Width / 2, 0, Math.Max(0, Width - hoverSize.Width)); TextRenderer.DrawText(e.Graphics, hover, Font, new Point(x, track.Top - 32), Color.FromArgb(31, 41, 55), Color.FromArgb(241, 245, 249)); }
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (_duration <= 0) return; Focus(); Rectangle track = Track(); double seconds = Seconds(e.X, track); ContextTimestampSeconds = seconds; CommercialReviewBoundary? hit = HitBoundary(e.X, track);
        if (e.Button == MouseButtons.Right) { if (hit != null) { _selectedBoundaryId = hit.Id; BoundarySelected?.Invoke(this, hit.Id); Invalidate(); } return; }
        if (e.Button != MouseButtons.Left) return;
        if (hit != null) { _selectedBoundaryId = hit.Id; _dragBoundaryId = hit.Id; _dragBoundarySeconds = hit.TimestampSeconds; BoundarySelected?.Invoke(this, hit.Id); BoundaryMovePreview?.Invoke(this, hit.TimestampSeconds); }
        else { _draggingNow = true; PositionSeconds = seconds; PositionChanged?.Invoke(this, seconds); }
        Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if (_duration <= 0) return; Rectangle track = Track(); double seconds = Seconds(e.X, track); _hoverSeconds = seconds;
        if (_dragBoundaryId.HasValue) { _dragBoundarySeconds = Math.Clamp(seconds, .001, Math.Max(.001, _duration - .001)); _position = _dragBoundarySeconds.Value; BoundaryMovePreview?.Invoke(this, _dragBoundarySeconds.Value); }
        else if (_draggingNow) { _position = seconds; PositionChanged?.Invoke(this, seconds); }
        else Cursor = HitBoundary(e.X, track) != null ? Cursors.SizeWE : Cursors.Hand;
        Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragBoundaryId.HasValue && _dragBoundarySeconds.HasValue) BoundaryMoved?.Invoke(this, new CommercialBoundaryMoveRequest(_dragBoundaryId.Value, _dragBoundarySeconds.Value)); _dragBoundaryId = null; _dragBoundarySeconds = null; _draggingNow = false; base.OnMouseUp(e);
    }
    protected override void OnMouseLeave(EventArgs e) { _hoverSeconds = null; if (!_dragBoundaryId.HasValue && !_draggingNow) Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_duration <= 0) return; double anchor = Seconds(e.X, Track()); if ((ModifierKeys & Keys.Shift) == Keys.Shift && IsZoomed) { _viewStart -= Math.Sign(e.Delta) * VisibleDuration * .10; ClampViewport(); RaiseViewportChanged(); Invalidate(); } else ZoomBy(e.Delta > 0 ? 1.25 : .8, anchor); base.OnMouseWheel(e);
    }
    private Rectangle Track() => new(Inset, Math.Max(30, ClientSize.Height / 2 - 8), Math.Max(1, ClientSize.Width - Inset * 2), 16);
    private double VisibleDuration => _duration <= 0 ? 0 : _duration / _zoom;
    private float X(double seconds, Rectangle track) => track.Left + (float)(Math.Clamp((seconds - _viewStart) / Math.Max(.001, VisibleDuration), 0, 1) * track.Width);
    private double Seconds(int x, Rectangle track) => Math.Clamp(_viewStart + (x - track.Left) / (double)track.Width * VisibleDuration, 0, _duration);
    private double DisplayTimestamp(CommercialReviewBoundary boundary) => _dragBoundaryId == boundary.Id && _dragBoundarySeconds.HasValue ? _dragBoundarySeconds.Value : boundary.TimestampSeconds;
    private CommercialReviewBoundary? HitBoundary(int x, Rectangle track) => _boundaries.Where(boundary => boundary.TimestampSeconds >= _viewStart && boundary.TimestampSeconds <= _viewStart + VisibleDuration).Select(boundary => new { Boundary = boundary, Distance = Math.Abs(x - X(boundary.TimestampSeconds, track)) }).Where(item => item.Distance <= MarkerHitRadius).OrderBy(item => item.Distance).Select(item => item.Boundary).FirstOrDefault();
    private void ClampViewport() => _viewStart = Math.Clamp(_viewStart, 0, Math.Max(0, _duration - VisibleDuration));
    private void RaiseViewportChanged() { double maximum = Math.Max(0, _duration - VisibleDuration); ViewportChanged?.Invoke(this, maximum <= 0 ? 0 : _viewStart / maximum); }
    internal double ViewportStartForTesting => _viewStart;
    internal double ViewportDurationForTesting => VisibleDuration;
    internal float XForTimestampForTesting(double seconds) => X(seconds, Track());
    internal double TimestampForXForTesting(int x) => Seconds(x, Track());
    private static void DrawMarker(Graphics graphics, float x, Rectangle track, Color color, string? label, int width = 2) { using var pen = new Pen(color, width); graphics.DrawLine(pen, x, track.Top - 13, x, track.Bottom + 12); using var brush = new SolidBrush(color); graphics.FillPolygon(brush, new[] { new PointF(x - 5, track.Top - 13), new PointF(x + 5, track.Top - 13), new PointF(x, track.Top - 6) }); if (label != null) TextRenderer.DrawText(graphics, label, SystemFonts.MessageBoxFont, new Point(Math.Max(0, (int)x - 30), 2), color); }
}
