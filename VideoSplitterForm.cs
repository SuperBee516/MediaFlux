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
    private SplitContainer? _previewEditorSplit;

    public VideoSplitterForm(Config config, string configPath)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
        _probeService = new FfprobeService(AppPaths.InstallDirectory, config.FfprobePath);

        Text = "Video Splitter / Trimmer";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(940, 780);
        Size = RestoreSize();
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(246, 248, 251);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        AllowDrop = true;

        BuildLayout();
        _processingMode.Items.AddRange(new object[] { "Fast / Lossless — Stream Copy", "Accurate Cut — Re-encode" });
        _processingMode.SelectedIndex = 0;
        foreach (string folder in _config.LastOutputFolders.Where(Directory.Exists)) _outputFolder.Items.Add(folder);
        _timeline.PositionChanged += (_, seconds) => SeekTo(seconds);
        _timeline.RangeChanged += (_, _) => { UpdateTimestampText(); ScheduleBoundaryPreviews(); };
        _playbackTimer.Tick += (_, _) => SynchronizePlaybackPosition();
        _previewDebounce.Tick += async (_, _) => { _previewDebounce.Stop(); await RefreshBoundaryPreviewsAsync(); };
        _playPause.Click += (_, _) => TogglePlayback();
        _exportButton.Click += async (_, _) => await ExportAllAsync();
        _cancelExportButton.Click += (_, _) => _exportCts?.Cancel();
        _openOutputButton.Click += (_, _) => OpenOutputFolder();
        _volume.ValueChanged += (_, _) => ApplyVolume();
        _inText.Leave += (_, _) => ApplyTimestampText(_inText, isIn: true);
        _outText.Leave += (_, _) => ApplyTimestampText(_outText, isIn: false);
        _inText.KeyDown += TimestampText_KeyDown;
        _outText.KeyDown += TimestampText_KeyDown;
        DragEnter += VideoSplitterForm_DragEnter;
        DragDrop += VideoSplitterForm_DragDrop;
        KeyDown += VideoSplitterForm_KeyDown;
        FormClosing += VideoSplitterForm_FormClosing;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(18), BackColor = BackColor };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildSourceInfo(), 0, 1);
        root.Controls.Add(BuildWorkspace(), 0, 2);
        root.Controls.Add(BuildStatusBar(), 0, 3);
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

    private Control BuildPreview()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Margin = new Padding(0, 0, 0, 8), MinimumSize = new Size(400, 150) };
        panel.Controls.Add(_playerHost);
        return panel;
    }

    private Control BuildWorkspace()
    {
        var split = new SplitContainer
        {
            Name = "SplitterPreviewEditorSplit",
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            BackColor = BackColor,
            Margin = new Padding(0, 0, 0, 8)
        };
        split.Panel1.Padding = new Padding(0, 0, 0, 8);
        split.Panel1.Controls.Add(BuildPreview());
        split.Panel2.Controls.Add(BuildTimeline());
        split.Resize += (_, _) => { if (split.Panel1MinSize > 0) ApplyPreviewSplitterDistance(); };
        split.SplitterMoved += (_, _) => _config.VideoSplitterPreviewSplitterDistance = split.SplitterDistance;
        _previewEditorSplit = split;
        return split;
    }

    private void ApplyPreviewSplitterDistance()
    {
        if (_previewEditorSplit == null || _previewEditorSplit.ClientSize.Height <= 0) return;
        int maximum = _previewEditorSplit.ClientSize.Height - _previewEditorSplit.Panel2MinSize - _previewEditorSplit.SplitterWidth;
        if (maximum < _previewEditorSplit.Panel1MinSize) return;
        int preferred = _config.VideoSplitterPreviewSplitterDistance > 0
            ? _config.VideoSplitterPreviewSplitterDistance
            : Math.Min(220, maximum);
        _previewEditorSplit.SplitterDistance = Math.Clamp(preferred, _previewEditorSplit.Panel1MinSize, maximum);
    }

    private Control BuildTimeline()
    {
        var scrollHost = new Panel { Name = "SplitterEditingSurface", Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0, 0, 0, 8) };
        var container = new TableLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Location = Point.Empty, ColumnCount = 1, RowCount = 5 };
        container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        container.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        container.Controls.Add(new Label { Text = "Timeline", AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = Color.FromArgb(55, 65, 81) }, 0, 0);
        container.Controls.Add(_timeline, 0, 1);

        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 5, 0, 0), Padding = new Padding(0, 0, 4, 0) };
        controls.Controls.Add(_playPause);
        controls.Controls.Add(CreateButton("◀ 5s", () => SeekRelative(-5)));
        controls.Controls.Add(CreateButton("5s ▶", () => SeekRelative(5)));
        controls.Controls.Add(CreateButton("‹ Frame", () => SeekFrame(-1)));
        controls.Controls.Add(CreateButton("Frame ›", () => SeekFrame(1)));
        controls.Controls.Add(_timeLabel);
        controls.Controls.Add(new Label { Text = "IN", AutoSize = true, Margin = new Padding(16, 7, 3, 0) });
        controls.Controls.Add(_inText);
        Button setIn = CreateButton("Set IN", () => SetIn(CurrentPlaybackPosition()));
        _toolTip.SetToolTip(setIn, "Mark the current blue playhead as the start of the next segment (I).");
        controls.Controls.Add(setIn);
        controls.Controls.Add(new Label { Text = "OUT", AutoSize = true, Margin = new Padding(10, 7, 3, 0) });
        controls.Controls.Add(_outText);
        Button setOut = CreateButton("Set OUT", () => SetOut(CurrentPlaybackPosition()));
        _toolTip.SetToolTip(setOut, "Mark the current blue playhead as the end of the next segment (O).");
        controls.Controls.Add(setOut);
        controls.Controls.Add(new Label { Text = "Volume", AutoSize = true, Margin = new Padding(16, 7, 3, 0) });
        controls.Controls.Add(_volume);
        controls.Controls.Add(CreateButton("Mute", ToggleMute));
        container.Controls.Add(controls, 0, 2);
        _rangeStateLabel.Margin = new Padding(0, 5, 0, 0);
        container.Controls.Add(_rangeStateLabel, 0, 3);
        container.Controls.Add(BuildEditingDetails(), 0, 4);
        scrollHost.Controls.Add(container);
        void FitEditingContentWidth() => container.Width = Math.Max(1, scrollHost.ClientSize.Width - (scrollHost.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
        scrollHost.Resize += (_, _) => FitEditingContentWidth();
        FitEditingContentWidth();
        return scrollHost;
    }

    private Control BuildStatusBar()
    {
        var panel = new TableLayoutPanel { Name = "SplitterExportPanel", Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 3, BackColor = Color.FromArgb(248, 250, 252), Padding = new Padding(8), Margin = new Padding(0, 5, 0, 0) };
        var outputs = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 4, 0) };
        outputs.Controls.Add(new Label { Text = "Output folder", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        outputs.Controls.Add(_outputFolder);
        outputs.Controls.Add(CreateButton("Browse…", BrowseOutputFolder));
        outputs.Controls.Add(new Label { Text = "Mode", AutoSize = true, Margin = new Padding(12, 7, 4, 0) });
        outputs.Controls.Add(_processingMode);
        outputs.Controls.Add(_playOutput);
        outputs.Controls.Add(_exportButton);
        outputs.Controls.Add(_cancelExportButton);
        outputs.Controls.Add(_openOutputButton);
        panel.Controls.Add(outputs, 0, 0);
        var progress = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 5, 0, 0) };
        progress.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 196));
        progress.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _exportDetails.AutoSize = false;
        _exportDetails.AutoEllipsis = true;
        _exportDetails.Dock = DockStyle.Fill;
        _exportDetails.TextAlign = ContentAlignment.MiddleLeft;
        progress.Controls.Add(_exportProgress, 0, 0);
        progress.Controls.Add(_exportDetails, 1, 0);
        panel.Controls.Add(progress, 0, 1);
        _statusLabel.ForeColor = Color.FromArgb(100, 116, 139); _statusLabel.Dock = DockStyle.Fill; _statusLabel.Margin = new Padding(0, 4, 0, 0);
        panel.Controls.Add(_statusLabel, 0, 2);
        return panel;
    }

    private Control BuildEditingDetails()
    {
        var outer = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 10, 0, 0), Padding = new Padding(0, 0, 4, 0) };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));

        var segments = new TableLayoutPanel { Name = "SplitterSegmentsPanel", Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 3, BackColor = Color.White, Padding = new Padding(10), Margin = new Padding(0, 0, 8, 0) };
        segments.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        segments.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        segments.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        segments.Controls.Add(new Label { Text = "Segments", AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = Color.FromArgb(55, 65, 81) }, 0, 0);
        ConfigureSegmentsGrid();
        segments.Controls.Add(_segmentsGrid, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, Height = 60, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        actions.Controls.Add(CreateNamedButton("Add Segment", AddSegment, "AddSegmentButton"));
        actions.Controls.Add(CreateNamedButton("Split at Current Position", SplitAtCurrentPosition, "SplitAtCurrentPositionButton"));
        actions.Controls.Add(CreateNamedButton("Update Segment", UpdateSelectedSegment, "UpdateSegmentButton"));
        actions.Controls.Add(CreateNamedButton("Remove", RemoveSelectedSegment, "RemoveSegmentButton"));
        actions.Controls.Add(CreateNamedButton("Clear", ClearSegments, "ClearSegmentsButton"));
        actions.Controls.Add(CreateNamedButton("Preview Selection", PreviewSelection, "PreviewSelectionButton"));
        segments.Controls.Add(actions, 0, 2);

        var boundaries = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 4, BackColor = Color.White, Padding = new Padding(10) };
        boundaries.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        boundaries.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        boundaries.Controls.Add(new Label { Text = "Boundary previews", AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = Color.FromArgb(55, 65, 81) }, 0, 0);
        boundaries.SetColumnSpan(boundaries.GetControlFromPosition(0, 0)!, 2);
        boundaries.Controls.Add(_inPreview, 0, 1);
        boundaries.Controls.Add(_outPreview, 1, 1);
        boundaries.Controls.Add(_inPreviewLabel, 0, 2);
        boundaries.Controls.Add(_outPreviewLabel, 1, 2);
        boundaries.Controls.Add(_keyframeLabel, 0, 3);
        boundaries.SetColumnSpan(_keyframeLabel, 2);
        outer.Controls.Add(segments, 0, 0);
        outer.Controls.Add(boundaries, 1, 0);
        return outer;
    }

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
        SetPlaybackState(false);
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
            _segments.Clear();
            RefreshSegmentsGrid(-1);
            if (string.IsNullOrWhiteSpace(_outputFolder.Text))
                _outputFolder.Text = _config.LastOutputFolders.FirstOrDefault(Directory.Exists) ?? Path.GetDirectoryName(path) ?? "";
            _timeline.SetDuration(_durationSeconds);
            UpdateSourceInfo(result, video, path);
            UpdateTimestampText();
            LoadIntoPlayer(path);
            _playPause.Enabled = true;
            _statusLabel.Text = "Ready. Drag the blue playhead or the IN/OUT markers to choose a range.";
            ScheduleBoundaryPreviews();
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
            player.URL = path;
            player.stretchToFit = false;
            player.settings.volume = _volume.Value;
            player.settings.mute = false;
            player.Ctlcontrols.pause();
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
            if (_isPlaying) player.Ctlcontrols.pause(); else player.Ctlcontrols.play();
            SetPlaybackState(!_isPlaying);
        }
        catch (Exception ex)
        {
            ErrorLogService.Append(Application.StartupPath, "Video Splitter playback command failed", _sourcePath, ex);
            _statusLabel.Text = "The preview player is unavailable for this file.";
        }
    }

    private void SynchronizePlaybackPosition()
    {
        if (_sourcePath == null || !_isPlaying) return;
        try
        {
            dynamic player = _playerHost.Player;
            double position = Convert.ToDouble(player.Ctlcontrols.currentPosition, CultureInfo.InvariantCulture);
            if ((_previewSelectionOnly && position >= _selectionPreviewEnd) || position >= _durationSeconds - 0.05)
            {
                if (_previewSelectionOnly)
                {
                    try { player.Ctlcontrols.currentPosition = _timeline.InSeconds; } catch { }
                    _statusLabel.Text = "Selection preview finished.";
                }
                _previewSelectionOnly = false;
                SetPlaybackState(false);
            }
            _timeline.PositionSeconds = Math.Clamp(position, 0, _durationSeconds);
            UpdateTimestampText();
        }
        catch { SetPlaybackState(false); }
    }

    private void SeekTo(double seconds)
    {
        if (_sourcePath == null) return;
        seconds = Math.Clamp(seconds, 0, _durationSeconds);
        try { ((dynamic)_playerHost.Player).Ctlcontrols.currentPosition = seconds; } catch { }
        _timeline.PositionSeconds = seconds;
        UpdateTimestampText();
    }

    private void SeekRelative(double seconds) => SeekTo(_timeline.PositionSeconds + seconds);
    private double CurrentPlaybackPosition()
    {
        if (_sourcePath == null || !_isPlaying) return _timeline.PositionSeconds;
        try
        {
            double position = Math.Clamp(Convert.ToDouble(((dynamic)_playerHost.Player).Ctlcontrols.currentPosition, CultureInfo.InvariantCulture), 0, _durationSeconds);
            _timeline.PositionSeconds = position;
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
        try { dynamic player = _playerHost.Player; player.settings.mute = !(bool)player.settings.mute; }
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
    }

    private void AddSegment()
    {
        if (!TryCurrentRange(out string error)) { ShowSegmentError(error); return; }
        int number = _segments.Count + 1;
        _segments.Add(new VideoSplitterSegment(number, _timeline.InSeconds, _timeline.OutSeconds, VideoSplitterSegmentRules.CreateOutputFileName(_sourcePath!, number)));
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
        if (!TryCreateSplitSegments(_sourcePath, _timeline.PositionSeconds, _durationSeconds, out VideoSplitterSegment[] segments, out string error))
        {
            ShowSegmentError(error);
            return;
        }
        _segments.Clear();
        _segments.AddRange(segments);
        _timeline.InSeconds = segments[0].StartSeconds;
        _timeline.OutSeconds = segments[0].EndSeconds;
        RefreshSegmentsGrid(0);
        SeekTo(_timeline.PositionSeconds);
        _statusLabel.Text = $"Created two planned segments at {FormatTime(_timeline.PositionSeconds)}. Review them before processing.";
    }

    internal static bool TryCreateSplitSegments(string sourcePath, double splitSeconds, double durationSeconds, out VideoSplitterSegment[] segments, out string error)
    {
        segments = Array.Empty<VideoSplitterSegment>();
        if (durationSeconds <= 0) { error = "The source duration is not available."; return false; }
        if (splitSeconds <= 0 || splitSeconds >= durationSeconds)
        {
            error = "Choose a split point after the first frame and before the end of the video.";
            return false;
        }
        segments = new[]
        {
            new VideoSplitterSegment(1, 0, splitSeconds, VideoSplitterSegmentRules.CreateOutputFileName(sourcePath, 1)),
            new VideoSplitterSegment(2, splitSeconds, durationSeconds, VideoSplitterSegmentRules.CreateOutputFileName(sourcePath, 2))
        };
        error = string.Empty;
        return true;
    }

    private void RestoreSelectedSegment()
    {
        int index = SelectedSegmentIndex();
        if (index < 0 || index >= _segments.Count) return;
        VideoSplitterSegment segment = _segments[index];
        _timeline.InSeconds = segment.StartSeconds;
        _timeline.OutSeconds = segment.EndSeconds;
        SeekTo(segment.StartSeconds);
    }

    private void PreviewSelection()
    {
        if (!TryCurrentRange(out string error)) { ShowSegmentError(error); return; }
        _previewSelectionOnly = true;
        _selectionPreviewEnd = _timeline.OutSeconds;
        SeekTo(_timeline.InSeconds);
        if (!_isPlaying) TogglePlayback();
        _statusLabel.Text = $"Previewing selected range: {FormatTime(_timeline.InSeconds)}–{FormatTime(_timeline.OutSeconds)}.";
    }

    private bool TryCurrentRange(out string error) => VideoSplitterSegmentRules.TryValidate(_timeline.InSeconds, _timeline.OutSeconds, _durationSeconds, out error);
    private void ShowSegmentError(string error) { _statusLabel.Text = error; MessageBox.Show(this, error, "Video Splitter / Trimmer", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    private int SelectedSegmentIndex() => _segmentsGrid.SelectedRows.Count == 1 ? _segmentsGrid.SelectedRows[0].Index : -1;
    private void RenumberSegments()
    {
        for (int index = 0; index < _segments.Count; index++)
        {
            VideoSplitterSegment segment = _segments[index];
            _segments[index] = segment with { Number = index + 1, OutputFileName = VideoSplitterSegmentRules.CreateOutputFileName(_sourcePath ?? "segment.mp4", index + 1) };
        }
    }
    private void RefreshSegmentsGrid(int selectIndex)
    {
        _segmentsGrid.Rows.Clear();
        foreach (VideoSplitterSegment segment in _segments)
            _segmentsGrid.Rows.Add(segment.Number, FormatTime(segment.StartSeconds), FormatTime(segment.EndSeconds), FormatTime(segment.DurationSeconds), segment.OutputFileName);
        if (selectIndex >= 0 && selectIndex < _segmentsGrid.Rows.Count) _segmentsGrid.Rows[selectIndex].Selected = true;
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
        string[] existing = _segments.Select(segment => Path.Combine(fullFolder, OutputPathService.SanitizeFileName(segment.OutputFileName))).Where(File.Exists).ToArray();
        if (existing.Length > 0)
        {
            DialogResult answer = MessageBox.Show(this, $"{existing.Length} output file(s) already exist. Overwrite them?\r\n\r\n{string.Join("\r\n", existing.Take(5).Select(Path.GetFileName))}", "Confirm overwrite", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
            overwrite = true;
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

    private void SetExportUi(bool active) { _exportButton.Enabled = !active; _cancelExportButton.Enabled = active; _processingMode.Enabled = !active; _outputFolder.Enabled = !active; }
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
        try
        {
            var result = await new MediaToolProcessRunner().RunAsync(new MediaToolProcessRequest
            {
                FileName = tools.FfmpegPath,
                Arguments = new[] { "-hide_banner", "-loglevel", "error", "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", sourcePath, "-frames:v", "1", "-vf", "scale=320:-2", "-q:v", "3", "-y", imagePath },
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

    private void SetPlaybackState(bool playing) { _isPlaying = playing; _playPause.Text = playing ? "Pause" : "Play"; if (playing) _playbackTimer.Start(); else _playbackTimer.Stop(); }
    private void ShowLoadError(string message) { _sourcePath = null; _durationSeconds = 0; _sourceFrameRate = 0; _segments.Clear(); RefreshSegmentsGrid(-1); _timeline.SetDuration(0); _playPause.Enabled = false; _detailsLabel.Text = message; _statusLabel.Text = "Unable to load video."; UpdateTimestampText(); }
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
    private Size RestoreSize() => _config.VideoSplitterWindowWidth >= MinimumSize.Width && _config.VideoSplitterWindowHeight >= MinimumSize.Height ? new Size(_config.VideoSplitterWindowWidth, _config.VideoSplitterWindowHeight) : new Size(1120, 760);
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_config.VideoSplitterWindowX != int.MinValue && _config.VideoSplitterWindowY != int.MinValue) Location = new Point(_config.VideoSplitterWindowX, _config.VideoSplitterWindowY);
        if (_previewEditorSplit != null)
        {
            // SplitContainer validates panel minimums immediately. Defer them until
            // the shown form has its real client height, including at high DPI.
            _previewEditorSplit.Panel1MinSize = 150;
            _previewEditorSplit.Panel2MinSize = 180;
            ApplyPreviewSplitterDistance();
        }
        try { _playerHost.CreateControl(); } catch (Exception ex) { ErrorLogService.Append(Application.StartupPath, "Initialize Video Splitter preview player failed", exception: ex); _statusLabel.Text = "Windows preview player is not available on this computer."; }
    }
    protected override void Dispose(bool disposing) { if (disposing) { _loadCts?.Dispose(); _previewCts?.Dispose(); _lifetimeCts.Dispose(); _previewDebounce.Dispose(); _playbackTimer.Dispose(); _toolTip.Dispose(); _inPreview.Image?.Dispose(); _outPreview.Image?.Dispose(); } base.Dispose(disposing); }

    private static Button CreateButton(string text, Action action) { var button = new Button { Text = text, AutoSize = true }; button.Click += (_, _) => action(); return button; }
    private static Button CreateNamedButton(string text, Action action, string name)
    {
        Button button = CreateButton(text, action);
        button.Name = name;
        button.AccessibleName = text;
        return button;
    }
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

internal sealed class WindowsMediaPlayerHost : AxHost
{
    private const string WindowsMediaPlayerClsid = "6BF52A52-394A-11d3-B153-00C04F79FAA6";
    public WindowsMediaPlayerHost() : base(WindowsMediaPlayerClsid) { }
    public object Player => GetOcx();
}

internal sealed class TimelineControl : Control
{
    private enum DragTarget { None, Position, In, Out }
    private DragTarget _dragTarget;
    private double _duration;
    private double _position;
    private double _in;
    private double _out;
    public event EventHandler<double>? PositionChanged;
    public event EventHandler? RangeChanged;
    public TimelineControl() { DoubleBuffered = true; BackColor = Color.White; MinimumSize = new Size(300, 70); Cursor = Cursors.Hand; }
    public double PositionSeconds { get => _position; set { _position = Math.Clamp(value, 0, _duration); Invalidate(); } }
    // Markers deliberately retain their independent positions. This lets a user
    // replace IN or OUT in either order; callers present an explicit invalid-range
    // state until IN precedes OUT instead of silently snapping to an old marker.
    public double InSeconds { get => _in; set { _in = Math.Clamp(value, 0, _duration); Invalidate(); RangeChanged?.Invoke(this, EventArgs.Empty); } }
    public double OutSeconds { get => _out; set { _out = Math.Clamp(value, 0, _duration); Invalidate(); RangeChanged?.Invoke(this, EventArgs.Empty); } }
    public void SetDuration(double seconds) { _duration = Math.Max(0, seconds); _position = 0; _in = 0; _out = _duration; Invalidate(); RangeChanged?.Invoke(this, EventArgs.Empty); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; Rectangle track = new(18, Height / 2 - 8, Math.Max(1, Width - 36), 16);
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
        DrawMarker(g, ToX(_position, start, visible, track), track, Color.FromArgb(37, 99, 235), null);
        TextRenderer.DrawText(g, VideoSplitterForm.FormatTime(start), Font, new Point(track.Left, track.Bottom + 7), Color.FromArgb(100, 116, 139));
        string end = VideoSplitterForm.FormatTime(Math.Min(_duration, start + visible)); Size size = TextRenderer.MeasureText(end, Font); TextRenderer.DrawText(g, end, Font, new Point(track.Right - size.Width, track.Bottom + 7), Color.FromArgb(100, 116, 139));
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (_duration <= 0 || e.Button != MouseButtons.Left) return;
        (double start, double visible) = Viewport(); Rectangle track = new(18, Height / 2 - 8, Math.Max(1, Width - 36), 16);
        float inX = ToX(_in, start, visible, track), outX = ToX(_out, start, visible, track), posX = ToX(_position, start, visible, track);
        _dragTarget = new[] { (DragTarget.In, Math.Abs(e.X - inX)), (DragTarget.Out, Math.Abs(e.X - outX)), (DragTarget.Position, Math.Abs(e.X - posX)) }.OrderBy(item => item.Item2).First().Item1;
        UpdateFromMouse(e.X, track, start, visible);
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_dragTarget != DragTarget.None) { (double start, double visible) = Viewport(); UpdateFromMouse(e.X, new Rectangle(18, Height / 2 - 8, Math.Max(1, Width - 36), 16), start, visible); } }
    protected override void OnMouseUp(MouseEventArgs e) { _dragTarget = DragTarget.None; base.OnMouseUp(e); }
    private void UpdateFromMouse(int x, Rectangle track, double start, double visible)
    {
        double value = Math.Clamp(start + (x - track.Left) / (double)track.Width * visible, 0, _duration);
        if (_dragTarget == DragTarget.In) InSeconds = value; else if (_dragTarget == DragTarget.Out) OutSeconds = value; else { PositionSeconds = value; PositionChanged?.Invoke(this, value); }
    }
    private (double start, double visible) Viewport() => (0, _duration);
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
            TextRenderer.DrawText(g, label, SystemFonts.MessageBoxFont, new Point(Math.Max(0, (int)x - size.Width / 2), 1), color);
        }
    }
}
