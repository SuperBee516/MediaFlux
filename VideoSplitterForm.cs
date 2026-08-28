using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;
using System.Globalization;
using System.Diagnostics;

namespace MediaFlux;

/// <summary>
/// Phase 1 editing surface. It intentionally owns no export pipeline: the selected
/// range remains in memory and will become the input to the Phase 2 job model.
/// </summary>
internal sealed partial class VideoSplitterForm : MediaFluxForm
{
    private readonly Config _config;
    private readonly string _configPath;
    private readonly FfprobeService _probeService;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _loadCts;
    private readonly WindowsMediaPlayerHost _playerHost = new() { Dock = DockStyle.Fill };
    private readonly TimelineControl _timeline = new() { Dock = DockStyle.Fill, Height = 82 };
    private readonly System.Windows.Forms.Timer _playbackTimer = new() { Interval = 100 };
    private readonly Label _sourceLabel = CreateValueLabel("Open a video to begin.");
    private readonly Label _detailsLabel = CreateValueLabel("Source information will appear here.");
    private readonly Label _timeLabel = CreateValueLabel("0:00.000 / 0:00.000");
    private readonly Label _statusLabel = CreateValueLabel("Ready");
    private readonly Label _rangeStateLabel = CreatePreviewLabel("Set IN, then Set OUT to mark a segment.");
    private readonly TextBox _inText = new() { Width = 105 };
    private readonly TextBox _outText = new() { Width = 105 };
    private readonly TrackBar _volume = new() { Minimum = 0, Maximum = 100, Value = 80, TickStyle = TickStyle.None, Width = 100 };
    private readonly Button _playPause = new() { Text = "Play", AutoSize = true, Enabled = false };
    private readonly Button _seekBackButton = CreateUiButton("◀ 5s", "SeekBackButton");
    private readonly Button _seekForwardButton = CreateUiButton("5s ▶", "SeekForwardButton");
    private readonly Button _frameBackButton = CreateUiButton("‹ Fine", "FineSeekBackButton");
    private readonly Button _frameForwardButton = CreateUiButton("Fine ›", "FineSeekForwardButton");
    private readonly Button _muteButton = CreateUiButton("Mute", "MuteButton");
    private readonly Button _setInButton = CreateUiButton("Set IN", "SetInButton");
    private readonly Button _setOutButton = CreateUiButton("Set OUT", "SetOutButton");
    private readonly Button _addSegmentButton = CreateUiButton("Add Segment", "AddSegmentButton");
    private readonly Button _splitAtPositionButton = CreateUiButton("Split at Current Position", "SplitAtCurrentPositionButton");
    private readonly ComboBox _splitKeep = new() { Name = "SplitKeepSelector", DropDownStyle = ComboBoxStyle.DropDownList, Width = 104 };
    private readonly Button _updateSegmentButton = CreateUiButton("Update Segment", "UpdateSegmentButton");
    private readonly Button _removeSegmentButton = CreateUiButton("Remove", "RemoveSegmentButton");
    private readonly Button _clearSegmentsButton = CreateUiButton("Clear", "ClearSegmentsButton");
    private readonly Button _previewSelectionButton = CreateUiButton("Preview Selection", "PreviewSelectionButton");
    private readonly Button _browseOutputButton = CreateUiButton("Browse…", "SplitterBrowseOutputButton");
    private readonly DataGridView _segmentsGrid = new();
    private readonly List<VideoSplitterSegment> _segments = new();
    private readonly PictureBox _inPreview = CreatePreviewBox();
    private readonly PictureBox _outPreview = CreatePreviewBox();
    private readonly Label _inPreviewLabel = CreatePreviewLabel("IN preview: —");
    private readonly Label _outPreviewLabel = CreatePreviewLabel("OUT preview: —");
    private readonly Label _keyframeLabel = CreatePreviewLabel("Keyframe boundary information will appear after loading a video.");
    private readonly ComboBox _outputFolder = new() { Name = "SplitterOutputFolder", DropDownStyle = ComboBoxStyle.DropDown, Width = 300 };
    private readonly ComboBox _processingMode = new() { Name = "SplitterProcessingMode", DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly CheckBox _playOutput = new() { Name = "SplitterPlayOutput", Text = "Play output after completion", AutoSize = true };
    private readonly Button _exportButton = new() { Name = "SplitterExportButton", Text = "Process all segments", AutoSize = true };
    private readonly Button _cancelExportButton = new() { Name = "SplitterCancelExportButton", Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly Button _openOutputButton = new() { Name = "SplitterOpenOutputButton", Text = "Open Output Folder", AutoSize = true };
    private readonly ProgressBar _exportProgress = new() { Minimum = 0, Maximum = 100, Width = 190 };
    private readonly Label _exportDetails = CreatePreviewLabel("No processing is active.");
    private readonly System.Windows.Forms.Timer _previewDebounce = new() { Interval = 300 };
    private readonly ToolTip _toolTip = new();
    private CancellationTokenSource? _previewCts;
    private bool _previewSelectionOnly;
    private double _selectionPreviewEnd;
    private double _sourceFrameRate;
    private CancellationTokenSource? _exportCts;
    private readonly Stopwatch _exportStopwatch = new();
    private double _durationSeconds;
    private string? _sourcePath;
    private bool _updatingTimestampText;
    private bool _isPlaying;
    private bool _playbackRequested;
    private double _confirmedPlaybackPosition;
    private readonly PreviewSeekCoordinator _previewSeek = new();
    private SplitContainer? _mediaEditorSplit;
    private SplitContainer? _timelineDetailsSplit;
    private SplitContainer? _boundarySegmentsSplit;
    private SplitContainer? _segmentsOutputSplit;

    public VideoSplitterForm(Config config, string configPath)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
        _probeService = new FfprobeService(AppPaths.InstallDirectory, config.FfprobePath);

        Text = "Video Splitter / Trimmer";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1100, 760);
        Size = RestoreSize();
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(246, 248, 251);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        AllowDrop = true;

        BuildLayout();
        _processingMode.Items.AddRange(new object[] { "Fast / Lossless — Stream Copy", "Accurate Cut — Re-encode" });
        _processingMode.SelectedIndex = 0;
        _splitKeep.Items.AddRange(new object[] { "Both sides", "Before NOW", "After NOW" });
        _splitKeep.SelectedIndex = 0;
        foreach (string folder in _config.LastOutputFolders.Where(Directory.Exists)) _outputFolder.Items.Add(folder);
        _timeline.PositionChanged += (_, seconds) => SeekTo(seconds);
        _timeline.RangeChanged += (_, _) => { UpdateTimestampText(); ScheduleBoundaryPreviews(); };
        _playbackTimer.Tick += (_, _) => SynchronizePlaybackPosition();
        _previewDebounce.Tick += async (_, _) => { _previewDebounce.Stop(); await RefreshBoundaryPreviewsAsync(); };
        _playPause.Click += (_, _) => TogglePlayback();
        _seekBackButton.Click += (_, _) => SeekRelative(-5);
        _seekForwardButton.Click += (_, _) => SeekRelative(5);
        _frameBackButton.Click += (_, _) => SeekFrame(-1);
        _frameForwardButton.Click += (_, _) => SeekFrame(1);
        _muteButton.Click += (_, _) => ToggleMute();
        _setInButton.Click += (_, _) => SetIn(CurrentPlaybackPosition());
        _setOutButton.Click += (_, _) => SetOut(CurrentPlaybackPosition());
        _addSegmentButton.Click += (_, _) => AddSegment();
        _splitAtPositionButton.Click += (_, _) => SplitAtCurrentPosition();
        _updateSegmentButton.Click += (_, _) => UpdateSelectedSegment();
        _removeSegmentButton.Click += (_, _) => RemoveSelectedSegment();
        _clearSegmentsButton.Click += (_, _) => ClearSegments();
        _previewSelectionButton.Click += (_, _) => PreviewSelection();
        _browseOutputButton.Click += (_, _) => BrowseOutputFolder();
        _exportButton.Click += async (_, _) => await ExportAllAsync();
        _cancelExportButton.Click += (_, _) => _exportCts?.Cancel();
        _openOutputButton.Click += (_, _) => OpenOutputFolder();
        _volume.ValueChanged += (_, _) => ApplyVolume();
        _outputFolder.TextChanged += (_, _) => UpdateControlStates();
        _inText.Leave += (_, _) => ApplyTimestampText(_inText, isIn: true);
        _outText.Leave += (_, _) => ApplyTimestampText(_outText, isIn: false);
        _inText.KeyDown += TimestampText_KeyDown;
        _outText.KeyDown += TimestampText_KeyDown;
        DragEnter += VideoSplitterForm_DragEnter;
        DragDrop += VideoSplitterForm_DragDrop;
        KeyDown += VideoSplitterForm_KeyDown;
        FormClosing += VideoSplitterForm_FormClosing;
        UpdateControlStates();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(14), BackColor = BackColor };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildSourceInfo(), 0, 1);
        root.Controls.Add(BuildRemediatedWorkspace(), 0, 2);
        Controls.Add(root);
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 12) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "Video Splitter / Trimmer", AutoSize = true, Font = new Font(Font.FontFamily, 16F, FontStyle.Bold), ForeColor = Color.FromArgb(31, 41, 55) }, 0, 0);
        var browse = new Button { Text = "Open video…", AutoSize = true };
        browse.Click += async (_, _) => await BrowseAsync();
        header.Controls.Add(browse, 1, 0);
        return header;
    }

    private Control BuildSourceInfo()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, BackColor = Color.White, Padding = new Padding(12), Margin = new Padding(0, 0, 0, 12) };
        panel.Controls.Add(_sourceLabel, 0, 0);
        panel.Controls.Add(_detailsLabel, 0, 1);
        return panel;
    }

    private Control BuildRemediatedWorkspace()
    {
        _mediaEditorSplit = CreateLayoutSplitter("SplitterMediaEditor", Orientation.Vertical);
        _timelineDetailsSplit = CreateLayoutSplitter("SplitterTimelineDetails", Orientation.Horizontal);
        _boundarySegmentsSplit = CreateLayoutSplitter("SplitterBoundarySegments", Orientation.Vertical);
        _segmentsOutputSplit = CreateLayoutSplitter("SplitterSegmentsOutput", Orientation.Horizontal);

        _timelineDetailsSplit.Panel1.Padding = new Padding(0, 0, 0, 5);
        _timelineDetailsSplit.Panel1.Controls.Add(_mediaEditorSplit);
        _timelineDetailsSplit.Panel2.Padding = new Padding(0, 5, 0, 0);
        _timelineDetailsSplit.Panel2.Controls.Add(_boundarySegmentsSplit);

        _mediaEditorSplit.Panel1.Padding = new Padding(0, 0, 6, 0);
        _mediaEditorSplit.Panel1.Controls.Add(BuildMediaArea());
        _mediaEditorSplit.Panel2.Padding = new Padding(6, 0, 0, 0);
        _mediaEditorSplit.Panel2.Controls.Add(BuildTimelineArea());

        _boundarySegmentsSplit.Panel1.Padding = new Padding(0, 0, 5, 0);
        _boundarySegmentsSplit.Panel1.Controls.Add(BuildBoundaryArea());
        _boundarySegmentsSplit.Panel2.Padding = new Padding(5, 0, 0, 0);
        _boundarySegmentsSplit.Panel2.Controls.Add(_segmentsOutputSplit);

        _segmentsOutputSplit.Panel1.Padding = new Padding(0, 0, 0, 5);
        _segmentsOutputSplit.Panel1.Controls.Add(BuildSegmentsArea());
        _segmentsOutputSplit.Panel2.Padding = new Padding(0, 5, 0, 0);
        _segmentsOutputSplit.Panel2.Controls.Add(BuildOutputArea());

        _mediaEditorSplit.SplitterMoved += (_, _) => _config.VideoSplitterMediaEditorSplitterDistance = _mediaEditorSplit.SplitterDistance;
        _timelineDetailsSplit.SplitterMoved += (_, _) => _config.VideoSplitterTimelineDetailsSplitterDistance = _timelineDetailsSplit.SplitterDistance;
        _boundarySegmentsSplit.SplitterMoved += (_, _) => _config.VideoSplitterBoundarySegmentsSplitterDistance = _boundarySegmentsSplit.SplitterDistance;
        _segmentsOutputSplit.SplitterMoved += (_, _) => _config.VideoSplitterSegmentsOutputSplitterDistance = _segmentsOutputSplit.SplitterDistance;
        return _timelineDetailsSplit;
    }

    private Control BuildMediaArea()
    {
        TableLayoutPanel card = CreateCard("Media Preview", 4);
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var preview = new Panel { Name = "SplitterMediaPreview", Dock = DockStyle.Fill, BackColor = Color.Black, Margin = new Padding(0, 8, 0, 8) };
        preview.Controls.Add(_playerHost);
        card.Controls.Add(preview, 0, 1);
        _timeLabel.Font = new Font(Font, FontStyle.Bold);
        _timeLabel.Margin = new Padding(2, 2, 2, 4);
        card.Controls.Add(_timeLabel, 0, 2);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        controls.Controls.Add(_playPause);
        controls.Controls.Add(_seekBackButton);
        controls.Controls.Add(_seekForwardButton);
        controls.Controls.Add(_frameBackButton);
        controls.Controls.Add(_frameForwardButton);
        controls.Controls.Add(new Label { Text = "Volume", AutoSize = true, Margin = new Padding(10, 7, 2, 0) });
        controls.Controls.Add(_volume);
        controls.Controls.Add(_muteButton);
        card.Controls.Add(controls, 0, 3);
        _toolTip.SetToolTip(_frameBackButton, "Fine seek backward by one source frame-rate interval (Shift+Left). Decoding is not guaranteed frame-accurate.");
        _toolTip.SetToolTip(_frameForwardButton, "Fine seek forward by one source frame-rate interval (Shift+Right). Decoding is not guaranteed frame-accurate.");
        return card;
    }

    private Control BuildTimelineArea()
    {
        TableLayoutPanel card = CreateCard("Timeline & Mark Range", 4);
        card.Name = "SplitterTimelinePanel";
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _timeline.Name = "SplitterTimeline";
        _timeline.Margin = new Padding(0, 6, 0, 6);
        card.Controls.Add(_timeline, 0, 1);

        var marking = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        marking.Controls.Add(CreateFieldLabel("IN", Color.FromArgb(22, 101, 52)));
        marking.Controls.Add(_inText);
        marking.Controls.Add(_setInButton);
        marking.Controls.Add(CreateFieldLabel("OUT", Color.FromArgb(185, 28, 28)));
        marking.Controls.Add(_outText);
        marking.Controls.Add(_setOutButton);
        marking.Controls.Add(_addSegmentButton);
        marking.Controls.Add(_updateSegmentButton);
        card.Controls.Add(marking, 0, 2);

        var rangeRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 5, 0, 0) };
        rangeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rangeRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _rangeStateLabel.Dock = DockStyle.Fill;
        _rangeStateLabel.AutoEllipsis = true;
        rangeRow.Controls.Add(_rangeStateLabel, 0, 0);
        var splitActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        splitActions.Controls.Add(new Label { Text = "Keep:", AutoSize = true, Margin = new Padding(0, 7, 3, 0) });
        splitActions.Controls.Add(_splitKeep);
        splitActions.Controls.Add(_splitAtPositionButton);
        rangeRow.Controls.Add(splitActions, 1, 0);
        card.Controls.Add(rangeRow, 0, 3);
        _toolTip.SetToolTip(_setInButton, "Set the green IN marker to the current blue playhead (I).");
        _toolTip.SetToolTip(_setOutButton, "Set the red OUT marker to the current blue playhead (O).");
        _toolTip.SetToolTip(_timeline, "NOW (blue) is playback position; IN (green) and OUT (red) define the selected range.");
        _toolTip.SetToolTip(_splitAtPositionButton, "Add the requested side(s) divided at the current blue NOW playhead; existing segments are retained.");
        return card;
    }

    private Control BuildBoundaryArea()
    {
        TableLayoutPanel card = CreateCard("Boundary Previews", 4);
        card.Name = "SplitterBoundaryPanel";
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var previews = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 6, 0, 0) };
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        previews.Controls.Add(_inPreview, 0, 0);
        previews.Controls.Add(_outPreview, 1, 0);
        card.Controls.Add(previews, 0, 1);
        var labels = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        labels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        labels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        labels.Controls.Add(_inPreviewLabel, 0, 0);
        labels.Controls.Add(_outPreviewLabel, 1, 0);
        card.Controls.Add(labels, 0, 2);
        _keyframeLabel.AutoSize = false;
        _keyframeLabel.AutoEllipsis = true;
        _keyframeLabel.Dock = DockStyle.Fill;
        _keyframeLabel.Height = 36;
        card.Controls.Add(_keyframeLabel, 0, 3);
        return card;
    }

    private Control BuildSegmentsArea()
    {
        TableLayoutPanel card = CreateCard("Segments", 3);
        card.Name = "SplitterSegmentsPanel";
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        ConfigureSegmentsGrid();
        card.Controls.Add(_segmentsGrid, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        actions.Controls.Add(_removeSegmentButton);
        actions.Controls.Add(_clearSegmentsButton);
        actions.Controls.Add(_previewSelectionButton);
        card.Controls.Add(actions, 0, 2);
        return card;
    }

    private Control BuildOutputArea()
    {
        TableLayoutPanel panel = CreateCard("Output & Processing", 6);
        panel.Name = "SplitterExportPanel";
        for (int row = 0; row < 5; row++) panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var folder = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 7, 0, 2) };
        folder.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folder.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folder.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _outputFolder.Dock = DockStyle.Fill;
        folder.Controls.Add(new Label { Text = "Folder", AutoSize = true, Margin = new Padding(0, 7, 5, 0) }, 0, 0);
        folder.Controls.Add(_outputFolder, 1, 0);
        folder.Controls.Add(_browseOutputButton, 2, 0);
        panel.Controls.Add(folder, 0, 1);

        var mode = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 2, 0, 2) };
        mode.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        mode.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mode.Controls.Add(new Label { Text = "Mode", AutoSize = true, Margin = new Padding(0, 7, 8, 0) }, 0, 0);
        _processingMode.Dock = DockStyle.Fill;
        mode.Controls.Add(_processingMode, 1, 0);
        panel.Controls.Add(mode, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 2) };
        _playOutput.Margin = new Padding(0, 6, 12, 0);
        actions.Controls.Add(_playOutput);
        actions.Controls.Add(_exportButton);
        actions.Controls.Add(_cancelExportButton);
        actions.Controls.Add(_openOutputButton);
        panel.Controls.Add(actions, 0, 3);
        var progress = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 3, 0, 0) };
        progress.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        progress.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _exportProgress.Dock = DockStyle.Fill;
        _exportDetails.AutoSize = false;
        _exportDetails.AutoEllipsis = true;
        _exportDetails.Dock = DockStyle.Fill;
        progress.Controls.Add(_exportProgress, 0, 0);
        progress.Controls.Add(_exportDetails, 1, 0);
        panel.Controls.Add(progress, 0, 4);
        _statusLabel.ForeColor = Color.FromArgb(100, 116, 139);
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.AutoEllipsis = true;
        panel.Controls.Add(_statusLabel, 0, 5);
        return panel;
    }

    private TableLayoutPanel CreateCard(string title, int rows)
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows, BackColor = Color.White, Padding = new Padding(10), Margin = Padding.Empty };
        card.Controls.Add(new Label { Text = title, UseMnemonic = false, AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = Color.FromArgb(55, 65, 81), Margin = Padding.Empty }, 0, 0);
        return card;
    }

    private static SplitContainer CreateLayoutSplitter(string name, Orientation orientation) => new()
    {
        Name = name,
        Dock = DockStyle.Fill,
        Orientation = orientation,
        SplitterWidth = 6,
        BackColor = Color.FromArgb(203, 213, 225),
        BorderStyle = BorderStyle.None
    };

    private static Label CreateFieldLabel(string text, Color color) => new() { Text = text, AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), ForeColor = color, Margin = new Padding(8, 7, 3, 0) };

    private void ConfigureSegmentsGrid()
    {
        _segmentsGrid.Dock = DockStyle.Fill;
        _segmentsGrid.AllowUserToAddRows = false;
        _segmentsGrid.AllowUserToDeleteRows = false;
        _segmentsGrid.AllowUserToResizeRows = false;
        _segmentsGrid.ReadOnly = true;
        _segmentsGrid.MultiSelect = false;
        _segmentsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _segmentsGrid.AutoGenerateColumns = false;
        _segmentsGrid.BackgroundColor = Color.White;
        _segmentsGrid.BorderStyle = BorderStyle.FixedSingle;
        _segmentsGrid.RowHeadersVisible = false;
        _segmentsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "#", Width = 35 });
        _segmentsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Start", Width = 86 });
        _segmentsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "End", Width = 86 });
        _segmentsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Duration", Width = 86 });
        _segmentsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Output filename", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _segmentsGrid.SelectionChanged += (_, _) => RestoreSelectedSegment();
        _segmentsGrid.ReadOnly = false;
        for (int index = 0; index < 4; index++) _segmentsGrid.Columns[index].ReadOnly = true;
        _segmentsGrid.CellEndEdit += SegmentsGrid_CellEndEdit;
    }

    private async Task BrowseAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.m4v;*.webm;*.ts;*.m2ts|All files|*.*", Title = "Open video" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await LoadSourceAsync(dialog.FileName);
    }

    private async Task LoadSourceAsync(string path)
    {
        if (!File.Exists(path))
        {
            ShowLoadError("The selected file no longer exists.");
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        CancellationToken token = _loadCts.Token;
        _playbackTimer.Stop();
        SetPlaybackState(false);
        _sourcePath = null;
        _durationSeconds = 0;
        _sourceFrameRate = 0;
        _segments.Clear();
        RefreshSegmentsGrid(-1);
        _timeline.SetDuration(0);
        SetPreviewImage(_inPreview, null);
        SetPreviewImage(_outPreview, null);
        _inPreviewLabel.Text = "IN preview: —";
        _outPreviewLabel.Text = "OUT preview: —";
        _statusLabel.Text = "Analyzing source video…";
        _sourceLabel.Text = Path.GetFileName(path);
        _detailsLabel.Text = path;

        try
        {
            MediaProbeResult result = await _probeService.ProbeAsync(path, token);
            if (token.IsCancellationRequested) return;
            MediaProbeStreamInfo? video = result.Streams.FirstOrDefault(stream => string.Equals(stream.CodecType, "video", StringComparison.OrdinalIgnoreCase));
            if (!result.Success || video == null || result.DurationSeconds is not > 0)
            {
                ShowLoadError(string.IsNullOrWhiteSpace(result.ErrorMessage) ? "This file does not contain a readable video stream." : result.ErrorMessage);
                return;
            }

            _sourcePath = path;
            _durationSeconds = result.DurationSeconds.Value;
            _sourceFrameRate = video.FrameRate ?? 0;
            _confirmedPlaybackPosition = 0;
            _segments.Clear();
            RefreshSegmentsGrid(-1);
            if (string.IsNullOrWhiteSpace(_outputFolder.Text))
                _outputFolder.Text = _config.LastOutputFolders.FirstOrDefault(Directory.Exists) ?? Path.GetDirectoryName(path) ?? "";
            _timeline.SetDuration(_durationSeconds);
            UpdateSourceInfo(result, video, path);
            UpdateTimestampText();
            LoadIntoPlayer(path);
            _statusLabel.Text = "Ready. Seek with the blue playhead, then use Set IN and Set OUT to mark a range.";
            ScheduleBoundaryPreviews();
            UpdateControlStates();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorLogService.Append(Application.StartupPath, "Video Splitter source analysis failed", path, ex);
            ShowLoadError("MediaFlux could not analyze this video. See the Error Log for details.");
        }
    }

    private void LoadIntoPlayer(string path)
    {
        try
        {
            dynamic player = _playerHost.Player;
            player.settings.autoStart = false;
            player.uiMode = "none";
            player.stretchToFit = false;
            player.enableContextMenu = false;
            player.settings.volume = _volume.Value;
            player.settings.mute = false;
            player.URL = path;
            _playbackRequested = false;
            // WMP does not paint the first frame until it receives a seek after the
            // media becomes ready. Keep the request until that state is reached.
            _previewSeek.Request(_timeline.PositionSeconds);
            SetPlaybackState(false);
            _playbackTimer.Start();
        }
        catch (Exception ex)
        {
            ErrorLogService.Append(Application.StartupPath, "Video Splitter preview load failed", path, ex);
            _statusLabel.Text = "The video was analyzed, but the Windows preview player could not open it.";
        }
    }

    private void UpdateSourceInfo(MediaProbeResult result, MediaProbeStreamInfo video, string path)
    {
        MediaProbeStreamInfo? audio = result.Streams.FirstOrDefault(stream => string.Equals(stream.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
        string bitrate = result.BitRate is > 0 ? $"{result.BitRate.Value / 1000d:N0} kb/s" : "Unknown";
        long size = result.SizeBytes ?? new FileInfo(path).Length;
        _sourceLabel.Text = $"{Path.GetFileName(path)}  —  {path}";
        _detailsLabel.Text = $"Duration: {FormatTime(_durationSeconds)}    Resolution: {video.Width ?? 0}×{video.Height ?? 0}    FPS: {video.FrameRate?.ToString("0.###", CultureInfo.InvariantCulture) ?? "Unknown"}\r\n" +
            $"Video: {DisplayCodec(video.CodecName)}    Audio: {DisplayCodec(audio?.CodecName)}    Bitrate: {bitrate}    Size: {FormatSize(size)}";
    }

    private void TogglePlayback()
    {
        if (_sourcePath == null) return;
        try
        {
            dynamic player = _playerHost.Player;
            int state = Convert.ToInt32(player.playState, CultureInfo.InvariantCulture);
            if (_isPlaying || state == 3) PausePlayback(player); else StartPlayback(player);
        }
        catch (Exception ex)
        {
            ErrorLogService.Append(Application.StartupPath, "Video Splitter playback command failed", _sourcePath, ex);
            _statusLabel.Text = "The preview player is unavailable for this file.";
        }
    }

    private void StartPlayback(dynamic player)
    {
        player.controls.play();
        _playbackRequested = true;
        SetPlaybackState(true);
        _playbackTimer.Start();
    }

    private void PausePlayback(dynamic player)
    {
        player.controls.pause();
        _playbackRequested = false;
        SetPlaybackState(false);
    }

    private void SynchronizePlaybackPosition()
    {
        if (_sourcePath == null) return;
        try
        {
            dynamic player = _playerHost.Player;
            int state = Convert.ToInt32(player.playState, CultureInfo.InvariantCulture);
            if (state == 3) _playbackRequested = true;
            else if (state is 1 or 2 or 8 or 10) _playbackRequested = false;
            SetPlaybackState(state == 3 || (_playbackRequested && state is 6 or 9 or 11));
            double position = Convert.ToDouble(player.controls.currentPosition, CultureInfo.InvariantCulture);
            if (TryApplyPendingPreviewSeek(player, state, out double requestedPosition))
                position = requestedPosition;
            if ((_previewSelectionOnly && position >= _selectionPreviewEnd) || position >= _durationSeconds - 0.05)
            {
                try { player.controls.pause(); } catch { }
                _playbackRequested = false;
                if (_previewSelectionOnly)
                {
                    try { player.controls.currentPosition = _selectionPreviewEnd; } catch { }
                    position = _selectionPreviewEnd;
                    _statusLabel.Text = "Selection preview finished.";
                }
                _previewSelectionOnly = false;
                SetPlaybackState(false);
            }
            _timeline.PositionSeconds = Math.Clamp(position, 0, _durationSeconds);
            _confirmedPlaybackPosition = _timeline.PositionSeconds;
            UpdateTimestampText();
        }
        catch { SetPlaybackState(false); }
    }

    private void SeekTo(double seconds, bool applyImmediately = false)
    {
        if (_sourcePath == null) return;
        seconds = Math.Clamp(seconds, 0, _durationSeconds);
        // Timeline drags can produce many events. The 100 ms playback timer
        // coalesces them into WMP seeks while the timeline itself remains instant.
        _previewSeek.Request(seconds);
        _confirmedPlaybackPosition = seconds;
        _timeline.PositionSeconds = seconds;
        if (applyImmediately) TryApplyPendingPreviewSeekImmediately();
        UpdateTimestampText();
    }

    private void TryApplyPendingPreviewSeekImmediately()
    {
        if (!_previewSeek.TryGet(out double position)) return;
        try
        {
            ((dynamic)_playerHost.Player).controls.currentPosition = position;
            _previewSeek.Complete();
        }
        catch
        {
            _previewSeek.Complete();
            _statusLabel.Text = "The preview player could not seek to that position.";
        }
    }

    private bool TryApplyPendingPreviewSeek(dynamic player, int state, out double position)
    {
        position = 0;
        if (!_previewSeek.TryGet(out position) || !PreviewSeekCoordinator.CanSeek(state)) return false;
        try
        {
            player.controls.currentPosition = position;
            _previewSeek.Complete();
            _confirmedPlaybackPosition = position;
            return true;
        }
        catch
        {
            _previewSeek.Complete();
            _statusLabel.Text = "The preview player could not seek to that position.";
            return false;
        }
    }

    private void SeekRelative(double seconds) => SeekTo(_timeline.PositionSeconds + seconds);
    private double CurrentPlaybackPosition()
    {
        if (_sourcePath == null || !_isPlaying) return _timeline.PositionSeconds;
        try
        {
            double position = Math.Clamp(Convert.ToDouble(((dynamic)_playerHost.Player).controls.currentPosition, CultureInfo.InvariantCulture), 0, _durationSeconds);
            _timeline.PositionSeconds = position;
            _confirmedPlaybackPosition = position;
            UpdateTimestampText();
            return position;
        }
        catch { return _timeline.PositionSeconds; }
    }
    private void SeekFrame(int direction)
    {
        // Windows Media Player seeks by time rather than decoded frames. A frame
        // duration is useful for fine navigation, but is deliberately not labeled
        // as frame-accurate seeking.
        double fps = _sourceFrameRate is > 0 ? _sourceFrameRate : 30;
        SeekRelative(direction / fps);
        _statusLabel.Text = "Fine seek uses the source frame-rate interval; playback decode seeking is not guaranteed frame-accurate.";
    }
    private void SetIn(double seconds)
    {
        if (_sourcePath == null) return;
        _timeline.InSeconds = Math.Clamp(seconds, 0, _durationSeconds);
        UpdateTimestampText();
        _statusLabel.Text = "IN marker set at the current playhead.";
    }

    private void SetOut(double seconds)
    {
        if (_sourcePath == null) return;
        _timeline.OutSeconds = Math.Clamp(seconds, 0, _durationSeconds);
        UpdateTimestampText();
        _statusLabel.Text = "OUT marker set at the current playhead.";
    }
    private void ToggleMute()
    {
        try { dynamic player = _playerHost.Player; bool muted = !(bool)player.settings.mute; player.settings.mute = muted; _muteButton.Text = muted ? "Unmute" : "Mute"; }
        catch { }
    }
    private void ApplyVolume()
    {
        try { ((dynamic)_playerHost.Player).settings.volume = _volume.Value; } catch { }
    }

    private void UpdateTimestampText()
    {
        _updatingTimestampText = true;
        _timeLabel.Text = $"{FormatTime(_timeline.PositionSeconds)} / {FormatTime(_durationSeconds)}";
        _inText.Text = FormatTime(_timeline.InSeconds);
        _outText.Text = FormatTime(_timeline.OutSeconds);
        _updatingTimestampText = false;
        UpdateRangeState();
    }

    private void UpdateRangeState()
    {
        if (_sourcePath == null || _durationSeconds <= 0)
        {
            _rangeStateLabel.Text = "Set IN, then Set OUT to mark a segment.";
            _rangeStateLabel.ForeColor = Color.FromArgb(100, 116, 139);
            UpdateControlStates();
            return;
        }
        if (TryCurrentRange(out _))
        {
            _rangeStateLabel.Text = $"Range ready: IN {FormatTime(_timeline.InSeconds)} → OUT {FormatTime(_timeline.OutSeconds)} ({FormatTime(_timeline.OutSeconds - _timeline.InSeconds)}).";
            _rangeStateLabel.ForeColor = Color.FromArgb(22, 101, 52);
        }
        else
        {
            _rangeStateLabel.Text = "Range not ready: OUT must be later than IN. Set or edit the markers again.";
            _rangeStateLabel.ForeColor = Color.FromArgb(185, 28, 28);
        }
        UpdateControlStates();
    }

    private void UpdateControlStates()
    {
        bool loaded = _sourcePath != null && _durationSeconds > 0;
        bool validRange = loaded && TryCurrentRange(out _);
        bool selected = SelectedSegmentIndex() >= 0;
        bool processing = _exportCts != null;
        _playPause.Enabled = loaded && !processing;
        _seekBackButton.Enabled = loaded && !processing;
        _seekForwardButton.Enabled = loaded && !processing;
        _frameBackButton.Enabled = loaded && !processing;
        _frameForwardButton.Enabled = loaded && !processing;
        _muteButton.Enabled = loaded;
        _volume.Enabled = loaded;
        _inText.Enabled = loaded && !processing;
        _outText.Enabled = loaded && !processing;
        _setInButton.Enabled = loaded && !processing;
        _setOutButton.Enabled = loaded && !processing;
        _addSegmentButton.Enabled = validRange && !processing;
        _updateSegmentButton.Enabled = validRange && selected && !processing;
        _splitAtPositionButton.Enabled = loaded && _timeline.PositionSeconds > 0 && _timeline.PositionSeconds < _durationSeconds && !processing;
        _removeSegmentButton.Enabled = selected && !processing;
        _clearSegmentsButton.Enabled = _segments.Count > 0 && !processing;
        _previewSelectionButton.Enabled = validRange && !processing;
        _exportButton.Enabled = loaded && _segments.Count > 0 && !processing;
        _cancelExportButton.Enabled = processing;
        _browseOutputButton.Enabled = !processing;
        _processingMode.Enabled = !processing;
        _outputFolder.Enabled = !processing;
        _playOutput.Enabled = !processing;
        _openOutputButton.Enabled = !processing && Directory.Exists(_outputFolder.Text.Trim());
    }

    private void AddSegment()
    {
        if (!TryCurrentRange(out string error)) { ShowSegmentError(error); return; }
        int number = NextSegmentNumber();
        string name = VideoSplitterSegmentRules.CreateUniqueOutputFileName(_sourcePath!, number, CurrentOutputFolder(), _segments.Select(segment => segment.OutputFileName));
        _segments.Add(new VideoSplitterSegment(number, _timeline.InSeconds, _timeline.OutSeconds, name));
        RefreshSegmentsGrid(selectIndex: _segments.Count - 1);
        _statusLabel.Text = $"Added segment {number}. Review its output name and process it when ready.";
    }

    private void UpdateSelectedSegment()
    {
        int index = SelectedSegmentIndex();
        if (index < 0) { ShowSegmentError("Select a segment to update."); return; }
        if (!TryCurrentRange(out string error)) { ShowSegmentError(error); return; }
        VideoSplitterSegment current = _segments[index];
        _segments[index] = current with { StartSeconds = _timeline.InSeconds, EndSeconds = _timeline.OutSeconds };
        RefreshSegmentsGrid(index);
        _statusLabel.Text = $"Updated segment {current.Number}.";
    }

    private void RemoveSelectedSegment()
    {
        int index = SelectedSegmentIndex();
        if (index < 0) return;
        _segments.RemoveAt(index);
        RenumberSegments();
        RefreshSegmentsGrid(Math.Min(index, _segments.Count - 1));
    }

    private void ClearSegments()
    {
        _segments.Clear();
        RefreshSegmentsGrid(-1);
        _statusLabel.Text = "All segments cleared.";
    }

    private void SplitAtCurrentPosition()
    {
        if (_sourcePath == null) { ShowSegmentError("Load a source video before splitting it."); return; }
        double splitSeconds = _timeline.PositionSeconds;
        VideoSplitterSplitKeep keep = (VideoSplitterSplitKeep)_splitKeep.SelectedIndex;
        if (!TryCreateSplitSegments(_sourcePath, splitSeconds, _durationSeconds, keep, out VideoSplitterSegment[] segments, out string error))
        {
            ShowSegmentError(error);
            return;
        }
        int firstIndex = _segments.Count;
        int firstNumber = NextSegmentNumber();
        IEnumerable<string> reserved = _segments.Select(segment => segment.OutputFileName);
        for (int index = 0; index < segments.Length; index++)
        {
            int number = firstNumber + index;
            string name = VideoSplitterSegmentRules.CreateUniqueOutputFileName(_sourcePath, number, CurrentOutputFolder(), reserved);
            segments[index] = segments[index] with { Number = number, OutputFileName = name };
            reserved = reserved.Append(name);
        }
        _segments.AddRange(segments);
        _timeline.InSeconds = segments[0].StartSeconds;
        _timeline.OutSeconds = segments[0].EndSeconds;
        RefreshSegmentsGrid(firstIndex);
        SeekTo(splitSeconds);
        _statusLabel.Text = $"Added {segments.Length} split part{(segments.Length == 1 ? "" : "s")} at {FormatTime(splitSeconds)}. Existing segments were retained.";
    }

    internal static bool TryCreateSplitSegments(string sourcePath, double splitSeconds, double durationSeconds, out VideoSplitterSegment[] segments, out string error)
        => TryCreateSplitSegments(sourcePath, splitSeconds, durationSeconds, VideoSplitterSplitKeep.BothSides, out segments, out error);

    internal static bool TryCreateSplitSegments(string sourcePath, double splitSeconds, double durationSeconds, VideoSplitterSplitKeep keep, out VideoSplitterSegment[] segments, out string error)
    {
        segments = Array.Empty<VideoSplitterSegment>();
        if (durationSeconds <= 0) { error = "The source duration is not available."; return false; }
        const double minimumSegmentSeconds = 0.001;
        if (splitSeconds <= minimumSegmentSeconds || splitSeconds >= durationSeconds - minimumSegmentSeconds)
        {
            error = "Choose a split point after the first frame and before the end of the video.";
            return false;
        }
        segments = keep switch
        {
            VideoSplitterSplitKeep.BeforeNow => new[] { new VideoSplitterSegment(1, 0, splitSeconds, VideoSplitterSegmentRules.CreateOutputFileName(sourcePath, 1)) },
            VideoSplitterSplitKeep.AfterNow => new[] { new VideoSplitterSegment(1, splitSeconds, durationSeconds, VideoSplitterSegmentRules.CreateOutputFileName(sourcePath, 1)) },
            _ => new[]
            {
                new VideoSplitterSegment(1, 0, splitSeconds, VideoSplitterSegmentRules.CreateOutputFileName(sourcePath, 1)),
                new VideoSplitterSegment(2, splitSeconds, durationSeconds, VideoSplitterSegmentRules.CreateOutputFileName(sourcePath, 2))
            }
        };
        error = string.Empty;
        return true;
    }

    private void RestoreSelectedSegment()
    {
        int index = SelectedSegmentIndex();
        if (index < 0 || index >= _segments.Count) { UpdateControlStates(); return; }
        VideoSplitterSegment segment = _segments[index];
        _timeline.InSeconds = segment.StartSeconds;
        _timeline.OutSeconds = segment.EndSeconds;
        SeekTo(segment.StartSeconds);
        UpdateControlStates();
    }

    private void PreviewSelection()
    {
        if (!TryCurrentRange(out string error)) { ShowSegmentError(error); return; }
        _previewSelectionOnly = true;
        _selectionPreviewEnd = _timeline.OutSeconds;
        SeekTo(_timeline.InSeconds, applyImmediately: true);
        try { StartPlayback((dynamic)_playerHost.Player); }
        catch (Exception ex)
        {
            _previewSelectionOnly = false;
            ErrorLogService.Append(Application.StartupPath, "Video Splitter selection preview failed", _sourcePath, ex);
            _statusLabel.Text = "The preview player could not play this selection.";
            return;
        }
        _statusLabel.Text = $"Previewing selected range: {FormatTime(_timeline.InSeconds)}–{FormatTime(_timeline.OutSeconds)}.";
    }

    private bool TryCurrentRange(out string error) => VideoSplitterSegmentRules.TryValidate(_timeline.InSeconds, _timeline.OutSeconds, _durationSeconds, out error);
    private void ShowSegmentError(string error) { _statusLabel.Text = error; MessageBox.Show(this, error, "Video Splitter / Trimmer", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    private int SelectedSegmentIndex() => _segmentsGrid.SelectedRows.Count == 1 ? _segmentsGrid.SelectedRows[0].Index : -1;
    private int NextSegmentNumber() => _segments.Count == 0 ? 1 : _segments.Max(segment => segment.Number) + 1;
    private string? CurrentOutputFolder()
    {
        string folder = _outputFolder.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder)) folder = _sourcePath == null ? "" : Path.GetDirectoryName(_sourcePath) ?? "";
        try { return string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder); }
        catch { return null; }
    }
    private void RenumberSegments()
    {
        for (int index = 0; index < _segments.Count; index++)
        {
            VideoSplitterSegment segment = _segments[index];
            _segments[index] = segment with { Number = index + 1 };
        }
    }
    private void RefreshSegmentsGrid(int selectIndex)
    {
        _segmentsGrid.Rows.Clear();
        foreach (VideoSplitterSegment segment in _segments)
            _segmentsGrid.Rows.Add(segment.Number, FormatTime(segment.StartSeconds), FormatTime(segment.EndSeconds), FormatTime(segment.DurationSeconds), segment.OutputFileName);
        if (selectIndex >= 0 && selectIndex < _segmentsGrid.Rows.Count) _segmentsGrid.Rows[selectIndex].Selected = true;
        UpdateControlStates();
    }

    private void SegmentsGrid_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _segments.Count || e.ColumnIndex != 4) return;
        string name = Convert.ToString(_segmentsGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value) ?? "";
        string sanitized = OutputPathService.SanitizeFileName(name, _segments[e.RowIndex].OutputFileName);
        _segments[e.RowIndex] = _segments[e.RowIndex] with { OutputFileName = sanitized };
        RefreshSegmentsGrid(e.RowIndex);
    }

    private void BrowseOutputFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose where split videos will be saved", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) _outputFolder.Text = dialog.SelectedPath;
    }

    private async Task ExportAllAsync()
    {
        if (_sourcePath == null) { ShowSegmentError("Load a source video before processing segments."); return; }
        string folder = _outputFolder.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder)) folder = Path.GetDirectoryName(_sourcePath) ?? "";
        bool overwrite = false;
        string fullFolder;
        try { fullFolder = Path.GetFullPath(folder); }
        catch (Exception ex) { ShowSegmentError($"The output folder is invalid: {ex.Message}"); return; }
        string[] existing = _segments.Select(segment => Path.Combine(fullFolder, OutputPathService.SanitizeFileName(segment.OutputFileName))).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (existing.Length > 0)
        {
            OutputConflictChoice choice = OutputConflictDialog.Show(this, existing);
            if (choice == OutputConflictChoice.Cancel) return;
            if (choice == OutputConflictChoice.AutoRename)
            {
                int selectedIndex = SelectedSegmentIndex();
                VideoSplitterSegment[] renamed = AutoRenameConflictingOutputs(_segments, fullFolder);
                _segments.Clear();
                _segments.AddRange(renamed);
                RefreshSegmentsGrid(selectedIndex);
                _statusLabel.Text = "Existing output conflicts were automatically renamed.";
            }
            else overwrite = true;
        }
        string encoder = ResolveConfiguredEncoder();
        VideoSplitterProcessingMode mode = _processingMode.SelectedIndex == 1 ? VideoSplitterProcessingMode.AccurateReencode : VideoSplitterProcessingMode.StreamCopy;
        if (mode == VideoSplitterProcessingMode.StreamCopy &&
            (_keyframeLabel.Text.Contains("no keyframe", StringComparison.OrdinalIgnoreCase) ||
             _keyframeLabel.Text.Contains("not available", StringComparison.OrdinalIgnoreCase)))
        {
            if (MessageBox.Show(this, "FFprobe could not confirm keyframe alignment for one or more selected boundaries. Stream-copy output may start or end at a nearby keyframe rather than the exact requested frame.\r\n\r\nContinue with Fast / Lossless stream copy?", "Keyframe boundary warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
        }
        var request = new VideoSplitterExportRequest
        {
            SourcePath = _sourcePath,
            OutputFolder = fullFolder,
            Segments = _segments.ToArray(),
            SourceDurationSeconds = _durationSeconds,
            Mode = mode,
            OverwriteExistingOutput = overwrite,
            VideoEncoder = encoder,
            EncoderPreset = _config.LastEncoderPreset,
            QualityValue = _config.LastQualityValue
        };
        IReadOnlyList<string> errors = VideoSplitterExportService.Validate(request);
        if (errors.Count > 0) { ShowSegmentError(string.Join("\r\n", errors)); return; }

        _exportCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        SetExportUi(active: true); _exportStopwatch.Restart(); _exportProgress.Value = 0;
        try
        {
            var progress = new Progress<VideoSplitterExportProgress>(UpdateExportProgress);
            var service = new VideoSplitterExportService(AppPaths.InstallDirectory, _config.FfmpegPath, _config.FfprobePath);
            VideoSplitterExportResult result = await service.ExportAsync(request, progress, _exportCts.Token);
            HandleExportResult(result);
            RememberOutputFolder(fullFolder);
        }
        catch (Exception ex)
        {
            ErrorLogService.Append(Application.StartupPath, "Video Splitter export failed", _sourcePath, ex);
            _statusLabel.Text = "Export failed unexpectedly. See Error Log for details.";
        }
        finally { _exportStopwatch.Stop(); _exportCts.Dispose(); _exportCts = null; SetExportUi(active: false); }
    }

    private string ResolveConfiguredEncoder()
    {
        try
        {
            return EncoderRegistry.Default.Resolve(_config.LastEncoderId, VideoEncoderCompatibility.ParseCodecFamily(_config.LastVideoCodec)).Selection.FfmpegCodec;
        }
        catch { return "libx264"; }
    }

    internal static VideoSplitterSegment[] AutoRenameConflictingOutputs(IEnumerable<VideoSplitterSegment> segments, string outputFolder)
    {
        var result = new List<VideoSplitterSegment>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VideoSplitterSegment segment in segments)
        {
            string preferred = OutputPathService.SanitizeFileName(segment.OutputFileName, $"Source-Part{segment.Number:00}.mp4");
            string available = VideoSplitterSegmentRules.CreateUnusedFileName(preferred, outputFolder, reserved);
            reserved.Add(available);
            result.Add(segment with { OutputFileName = available });
        }
        return result.ToArray();
    }

    private void UpdateExportProgress(VideoSplitterExportProgress update)
    {
        if (IsDisposed) return;
        int percent = (int)Math.Clamp(update.Percent ?? 0, 0, 100);
        _exportProgress.Value = percent;
        string speed = update.Speed is > 0 ? $" · {update.Speed:0.00}x" : "";
        _exportDetails.Text = $"Segment {update.SegmentNumber}/{update.SegmentCount}: {update.OutputFileName} · {update.Status} · {percent}% · elapsed {_exportStopwatch.Elapsed:hh\\:mm\\:ss}{speed}";
    }

    private void HandleExportResult(VideoSplitterExportResult result)
    {
        if (result.Warnings.Count > 0) { ShowSegmentError(string.Join("\r\n", result.Warnings)); return; }
        VideoSplitterSegmentExportResult[] succeeded = result.Segments.Where(segment => segment.Success).ToArray();
        VideoSplitterSegmentExportResult[] failed = result.Segments.Where(segment => !segment.Success && !segment.WasCanceled).ToArray();
        foreach (VideoSplitterSegmentExportResult failure in failed)
            ErrorLogService.Append(Application.StartupPath, "Video Splitter segment export failed", _sourcePath, details: $"Segment {failure.Segment.Number}: {failure.ErrorMessage}\r\n{failure.DiagnosticOutput}\r\n{failure.CleanupMessage}");
        _exportProgress.Value = result.Success ? 100 : _exportProgress.Value;
        _statusLabel.Text = result.Success ? $"Completed {succeeded.Length} segment(s)." : result.WasCanceled ? $"Canceled. {succeeded.Length} completed output(s) were retained." : $"Completed {succeeded.Length}; failed {failed.Length}. See Error Log for failures.";
        _exportDetails.Text = $"{_statusLabel.Text} Elapsed {_exportStopwatch.Elapsed:hh\\:mm\\:ss}";
        if (result.Success && _playOutput.Checked && succeeded.Length > 0) PlayOutput(succeeded[0].OutputPath);
        if (succeeded.Length > 0) MessageBox.Show(this, $"{_statusLabel.Text}\r\n\r\nOutput folder:\r\n{Path.GetDirectoryName(succeeded[0].OutputPath)}", "Video Splitter / Trimmer", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SetExportUi(bool active) { UpdateControlStates(); }
    private void OpenOutputFolder() { if (Directory.Exists(_outputFolder.Text)) Process.Start(new ProcessStartInfo { FileName = _outputFolder.Text, UseShellExecute = true }); }
    private void PlayOutput(string path) { try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch (Exception ex) { ErrorLogService.Append(Application.StartupPath, "Play Video Splitter output failed", path, ex); } }
    private void RememberOutputFolder(string folder)
    {
        _config.LastOutputFolders.RemoveAll(item => item.Equals(folder, StringComparison.OrdinalIgnoreCase));
        _config.LastOutputFolders.Insert(0, folder);
        if (_config.LastOutputFolders.Count > _config.FolderHistoryLimit) _config.LastOutputFolders.RemoveRange(_config.FolderHistoryLimit, _config.LastOutputFolders.Count - _config.FolderHistoryLimit);
        _outputFolder.Items.Clear(); foreach (string item in _config.LastOutputFolders) _outputFolder.Items.Add(item); _outputFolder.Text = folder;
        try { _config.Save(_configPath); } catch (Exception ex) { ErrorLogService.Append(Application.StartupPath, "Save Video Splitter output folder failed", exception: ex); }
    }

    private void ScheduleBoundaryPreviews()
    {
        if (_sourcePath == null) return;
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private async Task RefreshBoundaryPreviewsAsync()
    {
        if (_sourcePath == null) return;
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        CancellationToken token = _previewCts.Token;
        string path = _sourcePath;
        double start = _timeline.InSeconds;
        double end = _timeline.OutSeconds;
        _inPreviewLabel.Text = $"IN preview: {FormatTime(start)} (loading…)";
        _outPreviewLabel.Text = $"OUT preview: {FormatTime(end)} (loading…)";
        try
        {
            var results = await Task.WhenAll(CreateBoundaryPreviewAsync(path, start, "in", token), CreateBoundaryPreviewAsync(path, end, "out", token));
            if (token.IsCancellationRequested) return;
            SetPreviewImage(_inPreview, results[0]);
            SetPreviewImage(_outPreview, results[1]);
            _inPreviewLabel.Text = $"IN preview: {FormatTime(start)}";
            _outPreviewLabel.Text = $"OUT preview: {FormatTime(end)}";
            await UpdateKeyframeAwarenessAsync(path, start, end, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorLogService.Append(Application.StartupPath, "Video Splitter boundary preview failed", path, ex);
            if (!token.IsCancellationRequested) _keyframeLabel.Text = "Boundary previews could not be generated. See Error Log for details.";
        }
    }

    private async Task<Image?> CreateBoundaryPreviewAsync(string sourcePath, double seconds, string boundary, CancellationToken token)
    {
        FfmpegToolPaths tools = FfmpegToolResolver.Resolve(AppPaths.InstallDirectory, _config.FfmpegPath, _config.FfprobePath);
        if (!tools.HasFfmpeg) return null;
        string directory = Path.Combine(AppPaths.DataDirectory, "video-splitter-previews");
        Directory.CreateDirectory(directory);
        string imagePath = Path.Combine(directory, $"{Guid.NewGuid():N}-{boundary}.jpg");
        double frameInterval = _sourceFrameRate > 0 ? 1d / _sourceFrameRate : 1d / 30d;
        double previewSeconds = Math.Clamp(seconds, 0, Math.Max(0, _durationSeconds - frameInterval));
        try
        {
            var result = await new MediaToolProcessRunner().RunAsync(new MediaToolProcessRequest
            {
                FileName = tools.FfmpegPath,
                Arguments = new[] { "-hide_banner", "-loglevel", "error", "-ss", previewSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", sourcePath, "-frames:v", "1", "-vf", "scale=320:-2", "-q:v", "3", "-y", imagePath },
                Timeout = TimeSpan.FromSeconds(30)
            }, token);
            if (result.ExitCode != 0 || !File.Exists(imagePath)) return null;
            using var image = Image.FromFile(imagePath);
            return new Bitmap(image);
        }
        finally { try { if (File.Exists(imagePath)) File.Delete(imagePath); } catch { } }
    }

    private async Task UpdateKeyframeAwarenessAsync(string sourcePath, double start, double end, CancellationToken token)
    {
        FfmpegToolPaths tools = FfmpegToolResolver.Resolve(AppPaths.InstallDirectory, _config.FfmpegPath, _config.FfprobePath);
        if (!tools.HasFfprobe) { _keyframeLabel.Text = "Keyframe check unavailable: FFprobe was not found."; return; }
        string startState = await GetKeyframeStateAsync(tools.FfprobePath, sourcePath, start, token);
        string endState = await GetKeyframeStateAsync(tools.FfprobePath, sourcePath, end, token);
        if (!token.IsCancellationRequested) _keyframeLabel.Text = $"Stream-copy boundary check — IN: {startState}; OUT: {endState}. Phase 3 should warn before lossless cuts that are not keyframe-aligned.";
    }

    private static async Task<string> GetKeyframeStateAsync(string ffprobePath, string sourcePath, double seconds, CancellationToken token)
    {
        var result = await new MediaToolProcessRunner().RunAsync(new MediaToolProcessRequest
        {
            FileName = ffprobePath,
            Arguments = new[] { "-v", "error", "-select_streams", "v:0", "-read_intervals", $"{seconds.ToString("0.###", CultureInfo.InvariantCulture)}%+0.25", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", sourcePath },
            Timeout = TimeSpan.FromSeconds(20)
        }, token);
        if (result.ExitCode != 0) return "not available";
        string? keyframe = result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(line => line.Contains('K'));
        if (keyframe == null) return "no keyframe reported near requested time";
        string timestamp = keyframe.Split(',')[0];
        return $"keyframe reported near {timestamp}s";
    }

    private static void SetPreviewImage(PictureBox box, Image? image)
    {
        Image? old = box.Image; box.Image = image; old?.Dispose();
    }

    private void ApplyTimestampText(TextBox box, bool isIn)
    {
        if (_updatingTimestampText || !TryParseTime(box.Text, out double seconds)) { UpdateTimestampText(); return; }
        if (isIn) SetIn(seconds); else SetOut(seconds);
    }

    private void TimestampText_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && sender is TextBox box) { ApplyTimestampText(box, box == _inText); e.SuppressKeyPress = true; }
    }
    private async void VideoSplitterForm_DragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) await LoadSourceAsync(files[0]);
    }
    private void VideoSplitterForm_DragEnter(object? sender, DragEventArgs e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    private void VideoSplitterForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ActiveControl is TextBox) return;
        if (e.KeyCode == Keys.Space) { TogglePlayback(); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.I) SetIn(CurrentPlaybackPosition());
        else if (e.KeyCode == Keys.O) SetOut(CurrentPlaybackPosition());
        else if (e.KeyCode == Keys.Left && e.Shift) SeekFrame(-1);
        else if (e.KeyCode == Keys.Right && e.Shift) SeekFrame(1);
        else if (e.KeyCode == Keys.Left) SeekRelative(-1);
        else if (e.KeyCode == Keys.Right) SeekRelative(1);
        else if (e.KeyCode == Keys.A && e.Control) AddSegment();
    }

    private void SetPlaybackState(bool playing) { _isPlaying = playing; _playPause.Text = playing ? "Pause" : "Play"; }
    private void ShowLoadError(string message)
    {
        _sourcePath = null;
        _durationSeconds = 0;
        _sourceFrameRate = 0;
        _confirmedPlaybackPosition = 0;
        _segments.Clear();
        _playbackTimer.Stop();
        _playbackRequested = false;
        SetPlaybackState(false);
        RefreshSegmentsGrid(-1);
        _timeline.SetDuration(0);
        SetPreviewImage(_inPreview, null);
        SetPreviewImage(_outPreview, null);
        _inPreviewLabel.Text = "IN preview: —";
        _outPreviewLabel.Text = "OUT preview: —";
        _detailsLabel.Text = message;
        _statusLabel.Text = "Unable to load video.";
        UpdateTimestampText();
    }
    private void VideoSplitterForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exportCts != null)
        {
            e.Cancel = true;
            _exportCts.Cancel();
            _statusLabel.Text = "Canceling the active export. Close the window after cleanup completes.";
            return;
        }
        _lifetimeCts.Cancel(); _previewCts?.Cancel(); _previewDebounce.Stop(); _playbackTimer.Stop(); SetPlaybackState(false); PersistBounds();
        try { ((dynamic)_playerHost.Player).close(); } catch { }
    }
    private void PersistBounds()
    {
        if (WindowState == FormWindowState.Normal)
        {
            _config.VideoSplitterWindowX = Left; _config.VideoSplitterWindowY = Top; _config.VideoSplitterWindowWidth = Width; _config.VideoSplitterWindowHeight = Height;
        }
        try { _config.Save(_configPath); } catch (Exception ex) { ErrorLogService.Append(Application.StartupPath, "Save Video Splitter window placement failed", exception: ex); }
    }
    private Size RestoreSize() => _config.VideoSplitterWindowWidth >= MinimumSize.Width && _config.VideoSplitterWindowHeight >= MinimumSize.Height ? new Size(_config.VideoSplitterWindowWidth, _config.VideoSplitterWindowHeight) : new Size(1280, 820);
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_config.VideoSplitterWindowX != int.MinValue && _config.VideoSplitterWindowY != int.MinValue) Location = new Point(_config.VideoSplitterWindowX, _config.VideoSplitterWindowY);
        ConfigureShownSplitter(_mediaEditorSplit, 300, 540, _config.VideoSplitterMediaEditorSplitterDistance, 390);
        ConfigureShownSplitter(_timelineDetailsSplit, 240, 360, _config.VideoSplitterTimelineDetailsSplitterDistance, 300);
        ConfigureShownSplitter(_boundarySegmentsSplit, 300, 360, _config.VideoSplitterBoundarySegmentsSplitterDistance, 320);
        ConfigureShownSplitter(_segmentsOutputSplit, 150, 230, _config.VideoSplitterSegmentsOutputSplitterDistance, 190);
        try
        {
            _playerHost.CreateControl();
            dynamic player = _playerHost.Player;
            player.uiMode = "none";
            player.stretchToFit = false;
            player.enableContextMenu = false;
        }
        catch (Exception ex) { ErrorLogService.Append(Application.StartupPath, "Initialize Video Splitter preview player failed", exception: ex); _statusLabel.Text = "Windows preview player is not available on this computer."; }
    }
    private static void ConfigureShownSplitter(SplitContainer? splitter, int panel1Min, int panel2Min, int persisted, int preferred)
    {
        if (splitter == null) return;
        int available = (splitter.Orientation == Orientation.Vertical ? splitter.ClientSize.Width : splitter.ClientSize.Height) - splitter.SplitterWidth;
        if (available < panel1Min + panel2Min) return;
        splitter.Panel1MinSize = panel1Min;
        splitter.Panel2MinSize = panel2Min;
        int maximum = available - panel2Min;
        splitter.SplitterDistance = Math.Clamp(persisted > 0 ? persisted : preferred, panel1Min, maximum);
    }
    protected override void Dispose(bool disposing) { if (disposing) { _loadCts?.Dispose(); _previewCts?.Dispose(); _lifetimeCts.Dispose(); _previewDebounce.Dispose(); _playbackTimer.Dispose(); _toolTip.Dispose(); _inPreview.Image?.Dispose(); _outPreview.Image?.Dispose(); } base.Dispose(disposing); }

    private static Button CreateUiButton(string text, string name) => new() { Text = text, Name = name, AccessibleName = text, AutoSize = true };
    private static Label CreateValueLabel(string text) => new() { Text = text, AutoSize = true, ForeColor = Color.FromArgb(55, 65, 81), MaximumSize = new Size(0, 42) };
    private static PictureBox CreatePreviewBox() => new() { Dock = DockStyle.Fill, Height = 95, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(30, 41, 59), Margin = new Padding(2, 6, 2, 2) };
    private static Label CreatePreviewLabel(string text) => new() { Text = text, AutoSize = true, ForeColor = Color.FromArgb(71, 85, 105), Margin = new Padding(2, 4, 2, 0) };
    private static string DisplayCodec(string? codec) => string.IsNullOrWhiteSpace(codec) ? "None" : codec.ToUpperInvariant();
    private static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024d / 1024d / 1024d:N2} GB" : $"{bytes / 1024d / 1024d:N1} MB";
    internal static string FormatTime(double seconds) { var value = TimeSpan.FromSeconds(Math.Max(0, seconds)); return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss\.fff") : value.ToString(@"m\:ss\.fff"); }
    internal static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan value) && value >= TimeSpan.Zero) { seconds = value.TotalSeconds; return true; }
        string[] parts = text.Trim().Split(':');
        if (parts.Length is 2 or 3 &&
            double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedSeconds) &&
            parsedSeconds >= 0 && parsedSeconds < 60 &&
            int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) && minutes >= 0)
        {
            int hours = 0;
            if (parts.Length == 3 &&
                (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours) || hours < 0))
            {
                return false;
            }

            seconds = hours * 3600d + minutes * 60d + parsedSeconds;
            return true;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds >= 0;
    }
}

internal enum OutputConflictChoice { Overwrite, AutoRename, Cancel }

internal sealed class OutputConflictDialog : MediaFluxForm
{
    private OutputConflictChoice _choice = OutputConflictChoice.Cancel;

    private OutputConflictDialog(IReadOnlyList<string> paths)
    {
        Text = "Output files already exist";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(520, 280);
        Size = new Size(620, 340);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(246, 248, 251);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Text = $"{paths.Count} destination file(s) already exist. Choose how MediaFlux should handle them."
        }, 0, 0);
        root.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = string.Join(Environment.NewLine, paths.Select(Path.GetFileName)),
            Margin = new Padding(0, 12, 0, 12)
        }, 0, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        actions.Controls.Add(CreateChoiceButton("Cancel", OutputConflictChoice.Cancel));
        actions.Controls.Add(CreateChoiceButton("Auto-Rename", OutputConflictChoice.AutoRename));
        actions.Controls.Add(CreateChoiceButton("Overwrite", OutputConflictChoice.Overwrite));
        root.Controls.Add(actions, 0, 2);
        Controls.Add(root);
    }

    private Button CreateChoiceButton(string text, OutputConflictChoice choice)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        button.Click += (_, _) => { _choice = choice; DialogResult = DialogResult.OK; Close(); };
        return button;
    }

    public static OutputConflictChoice Show(IWin32Window owner, IReadOnlyList<string> paths)
    {
        using var dialog = new OutputConflictDialog(paths);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._choice : OutputConflictChoice.Cancel;
    }
}

public enum VideoSplitterSplitKeep
{
    BothSides,
    BeforeNow,
    AfterNow
}

internal sealed class PreviewSeekCoordinator
{
    private double? _pendingPosition;

    public void Request(double position) => _pendingPosition = position;
    public bool TryGet(out double position)
    {
        position = _pendingPosition ?? 0;
        return _pendingPosition.HasValue;
    }
    public void Complete() => _pendingPosition = null;

    // WMP's ready, stopped, paused, and playing states all accept a preview seek.
    public static bool CanSeek(int playState) => playState is 1 or 2 or 3 or 10;
}

internal sealed class WindowsMediaPlayerHost : AxHost
{
    private const string WindowsMediaPlayerClsid = "6BF52A52-394A-11d3-B153-00C04F79FAA6";
    public WindowsMediaPlayerHost() : base(WindowsMediaPlayerClsid) { }
    public object Player => GetOcx();
}

internal sealed class TimelineControl : Control
{
    private const int HorizontalInset = 30;
    private const int MarkerHitRadius = 10;
    private enum DragTarget { None, Position, In, Out }
    private DragTarget _dragTarget;
    private double _duration;
    private double _position;
    private double _in;
    private double _out;
    public event EventHandler<double>? PositionChanged;
    public event EventHandler? RangeChanged;
    public TimelineControl() { DoubleBuffered = true; ResizeRedraw = true; BackColor = Color.White; MinimumSize = new Size(300, 82); Cursor = Cursors.Hand; }
    public double PositionSeconds { get => _position; set { _position = Math.Clamp(value, 0, _duration); Invalidate(); } }
    // Markers deliberately retain their independent positions. This lets a user
    // replace IN or OUT in either order; callers present an explicit invalid-range
    // state until IN precedes OUT instead of silently snapping to an old marker.
    public double InSeconds { get => _in; set { _in = Math.Clamp(value, 0, _duration); Invalidate(); RangeChanged?.Invoke(this, EventArgs.Empty); } }
    public double OutSeconds { get => _out; set { _out = Math.Clamp(value, 0, _duration); Invalidate(); RangeChanged?.Invoke(this, EventArgs.Empty); } }
    public void SetDuration(double seconds) { _duration = Math.Max(0, seconds); _position = 0; _in = 0; _out = _duration; Invalidate(); RangeChanged?.Invoke(this, EventArgs.Empty); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; Rectangle track = GetTrackRectangle();
        using var trackBrush = new SolidBrush(Color.FromArgb(226, 232, 240)); g.FillRectangle(trackBrush, track);
        if (_duration <= 0) { TextRenderer.DrawText(g, "Open or drop a video file to start trimming", Font, ClientRectangle, Color.FromArgb(100, 116, 139), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
        (double start, double visible) = Viewport();
        float left = ToX(_in, start, visible, track); float right = ToX(_out, start, visible, track);
        if (_in < _out)
        {
            using var rangeBrush = new SolidBrush(Color.FromArgb(147, 197, 253));
            g.FillRectangle(rangeBrush, left, track.Top, right - left, track.Height);
        }
        DrawMarker(g, left, track, Color.FromArgb(22, 101, 52), "IN");
        DrawMarker(g, right, track, Color.FromArgb(185, 28, 28), "OUT");
        DrawMarker(g, ToX(_position, start, visible, track), track, Color.FromArgb(37, 99, 235), "NOW");
        TextRenderer.DrawText(g, VideoSplitterForm.FormatTime(start), Font, new Point(track.Left, track.Bottom + 7), Color.FromArgb(100, 116, 139));
        string end = VideoSplitterForm.FormatTime(Math.Min(_duration, start + visible)); Size size = TextRenderer.MeasureText(end, Font); TextRenderer.DrawText(g, end, Font, new Point(track.Right - size.Width, track.Bottom + 7), Color.FromArgb(100, 116, 139));
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (_duration <= 0 || e.Button != MouseButtons.Left) return;
        (double start, double visible) = Viewport(); Rectangle track = GetTrackRectangle();
        float inX = ToX(_in, start, visible, track), outX = ToX(_out, start, visible, track);
        (DragTarget Target, double Distance) closestMarker = new[] { (DragTarget.In, Math.Abs(e.X - inX)), (DragTarget.Out, Math.Abs(e.X - outX)) }.OrderBy(item => item.Item2).First();
        _dragTarget = closestMarker.Distance <= MarkerHitRadius ? closestMarker.Target : DragTarget.Position;
        UpdateFromMouse(e.X, track, start, visible);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_duration <= 0) { Cursor = Cursors.Hand; return; }
        if (_dragTarget != DragTarget.None)
        {
            (double start, double visible) = Viewport();
            UpdateFromMouse(e.X, GetTrackRectangle(), start, visible);
            return;
        }
        Rectangle track = GetTrackRectangle();
        float inX = ToX(_in, 0, _duration, track), outX = ToX(_out, 0, _duration, track);
        Cursor = Math.Abs(e.X - inX) <= MarkerHitRadius || Math.Abs(e.X - outX) <= MarkerHitRadius ? Cursors.SizeWE : Cursors.Hand;
    }
    protected override void OnMouseUp(MouseEventArgs e) { _dragTarget = DragTarget.None; base.OnMouseUp(e); }
    private void UpdateFromMouse(int x, Rectangle track, double start, double visible)
    {
        double value = Math.Clamp(start + (x - track.Left) / (double)track.Width * visible, 0, _duration);
        if (_dragTarget == DragTarget.In) InSeconds = value; else if (_dragTarget == DragTarget.Out) OutSeconds = value; else { PositionSeconds = value; PositionChanged?.Invoke(this, value); }
    }
    private (double start, double visible) Viewport() => (0, _duration);
    private Rectangle GetTrackRectangle() => new(HorizontalInset, Math.Max(24, ClientSize.Height / 2 - 8), Math.Max(1, ClientSize.Width - HorizontalInset * 2), 16);
    internal Rectangle TrackRectangleForTesting => GetTrackRectangle();
    internal float XForSecondsForTesting(double value) => ToX(value, 0, _duration, GetTrackRectangle());
    internal double SecondsForXForTesting(int x)
    {
        Rectangle track = GetTrackRectangle();
        return Math.Clamp((x - track.Left) / (double)track.Width * _duration, 0, _duration);
    }
    private static float ToX(double value, double start, double visible, Rectangle track) => track.Left + (float)(Math.Clamp((value - start) / visible, 0, 1) * track.Width);
    private static void DrawMarker(Graphics g, float x, Rectangle track, Color color, string? label)
    {
        using var pen = new Pen(color, 2);
        g.DrawLine(pen, x, track.Top - 12, x, track.Bottom + 12);
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, new[] { new PointF(x - 5, track.Top - 12), new PointF(x + 5, track.Top - 12), new PointF(x, track.Top - 5) });
        if (!string.IsNullOrEmpty(label))
        {
            Size size = TextRenderer.MeasureText(label, SystemFonts.MessageBoxFont);
            int labelX = Math.Clamp((int)x - size.Width / 2, 0, Math.Max(0, track.Right + HorizontalInset - size.Width));
            TextRenderer.DrawText(g, label, SystemFonts.MessageBoxFont, new Point(labelX, 1), color);
        }
    }
}
