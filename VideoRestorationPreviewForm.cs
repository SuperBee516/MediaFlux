using System.Diagnostics;
using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux;

internal enum RestorationPreviewMode { SideBySide, OriginalOnly, RestoredOnly }

/// <summary>Dedicated, non-destructive visual comparison surface for restoration settings.</summary>
internal sealed class VideoRestorationPreviewForm : MediaFluxForm
{
    private readonly VideoRestorationPreviewService _service;
    private readonly VideoRestorationPreviewSelection _selection;
    private readonly string _sourcePath;
    private readonly TimeSpan _duration;
    private readonly EncodingService.ScaleMode _encodeScale;
    private readonly string _externalPlayerPath;
    private readonly Action<VideoRestorationSettings> _apply;
    private readonly Func<CancellationToken, Task<VideoRestorationRecommendation?>>? _analyze;
    private readonly List<TimeSpan> _positions;
    private readonly PictureBox _original = FrameBox();
    private readonly PictureBox _restored = FrameBox();
    private readonly SplitContainer _comparison = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6 };
    private readonly Label _timestamp = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(900, 0) };
    private readonly Label _summary = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(900, 0) };
    private readonly Button _applyButton = new() { Text = "Apply to Encode Settings", AutoSize = true };
    private Button? _recommendationButton;
    private CancellationTokenSource? _generationCts;
    private VideoRestorationStillPreview? _current;
    private int _positionIndex;

    public VideoRestorationPreviewForm(string sourcePath, TimeSpan duration, EncodingService.ScaleMode encodeScale, VideoRestorationPreviewService service, VideoRestorationPreviewSelection selection, VideoRestorationAnalysisResult? analysis, string externalPlayerPath, Action<VideoRestorationSettings> apply, Func<CancellationToken, Task<VideoRestorationRecommendation?>>? analyze = null)
    {
        _sourcePath = sourcePath; _duration = duration; _encodeScale = encodeScale; _service = service; _selection = selection; _externalPlayerPath = externalPlayerPath; _apply = apply; _analyze = analyze;
        _positions = VideoRestorationPreviewService.BuildRepresentativePositions(duration).ToList();
        Text = "Preview Video Restoration"; StartPosition = FormStartPosition.CenterParent; MinimumSize = new Size(900, 620); Size = new Size(1180, 760); ShowInTaskbar = false;
        _summary.Text = BuildSummary(analysis);
        BuildUi();
        Shown += async (_, __) => await GenerateAsync();
        FormClosing += (_, __) => _generationCts?.Cancel();
        FormClosed += (_, __) => { _generationCts?.Dispose(); _current?.Dispose(); };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5, BackColor = Color.FromArgb(246, 248, 251) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { Text = "Original  |  Restored", Font = new Font(Font, FontStyle.Bold), AutoSize = true }, 0, 0);
        root.Controls.Add(new Label { Text = Path.GetFileName(_sourcePath), AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 6) }, 0, 1);
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        AddButton(toolbar, "‹ Previous Sample", (_, __) => { _positionIndex = (_positionIndex - 1 + _positions.Count) % _positions.Count; _ = GenerateAsync(); });
        AddButton(toolbar, "Next Sample ›", (_, __) => { _positionIndex = (_positionIndex + 1) % _positions.Count; _ = GenerateAsync(); });
        AddButton(toolbar, "Random Sample", (_, __) => { _positionIndex = Random.Shared.Next(_positions.Count); _ = GenerateAsync(); });
        AddButton(toolbar, "Preview Off", (_, __) => { _selection.PreviewOff(); _ = GenerateAsync(); });
        AddButton(toolbar, "Preview Current", (_, __) => { _selection.PreviewCurrent(); _ = GenerateAsync(); });
        _recommendationButton = AddButton(toolbar, "Preview Recommendation", (_, __) => { if (_selection.PreviewRecommendation()) _ = GenerateAsync(); }); _recommendationButton.Enabled = _selection.Recommendation != null;
        AddButton(toolbar, "Preview Clip (5 sec)", async (_, __) => await GenerateMotionAsync());
        if (_analyze != null) AddButton(toolbar, "Analyze / Recommend", async (_, __) => await AnalyzeAsync());
        var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 }; mode.Items.AddRange(Enum.GetValues<RestorationPreviewMode>().Cast<object>().ToArray()); mode.SelectedItem = RestorationPreviewMode.SideBySide; mode.SelectedIndexChanged += (_, __) => SetMode((RestorationPreviewMode)mode.SelectedItem!); toolbar.Controls.Add(mode);
        toolbar.Controls.Add(_applyButton); _applyButton.Click += (_, __) => { _apply(_selection.ApplyToEncodeSettings()); _applyButton.Enabled = false; _status.Text = "Previewed settings were applied to encode settings."; };
        root.Controls.Add(toolbar, 0, 2);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.White, Padding = new Padding(10) }; body.RowStyles.Add(new RowStyle(SizeType.AutoSize)); body.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.Controls.Add(_summary, 0, 0);
        ConfigurePane(_comparison.Panel1, "Original", _original); ConfigurePane(_comparison.Panel2, "Restored", _restored); body.Controls.Add(_comparison, 0, 1);
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) }; footer.Controls.Add(_timestamp); footer.Controls.Add(_status); body.Controls.Add(footer, 0, 2);
        root.Controls.Add(body, 0, 3);
        root.Controls.Add(new Label { Text = "Previews use accurate seeks and the same effective restoration pipeline as encoding. Preview selections do not change encode settings until you apply them.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 8, 0, 0) }, 0, 4);
        Controls.Add(root);
    }

    private async Task GenerateAsync()
    {
        if (_positions.Count == 0) { _status.Text = "The source is too short for a representative restoration preview."; return; }
        _generationCts?.Cancel(); _generationCts?.Dispose(); _generationCts = new CancellationTokenSource(); CancellationToken token = _generationCts.Token;
        try
        {
            _status.Text = "Generating synchronized restoration frame…"; _applyButton.Enabled = false;
            var request = new VideoRestorationPreviewRequest(_sourcePath, _duration, _positions[_positionIndex], _selection.PreviewSettings, _encodeScale);
            VideoRestorationStillPreview next = await _service.GenerateStillAsync(request, token);
            if (token.IsCancellationRequested) { next.Dispose(); return; }
            _current?.Dispose(); _current = next; _original.Image = next.Original; _restored.Image = next.Restored;
            _timestamp.Text = $"Timestamp: {Format(next.Position)}" + (next.ResolutionChanged || _encodeScale != EncodingService.ScaleMode.None ? "  •  Preview shows the effective output resolution" : "");
            _status.Text = string.IsNullOrWhiteSpace(next.FilterChain) ? "Restoration preview: Off" : "Restoration preview ready.";
            _applyButton.Enabled = _selection.DiffersFromEncode;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "Preview unavailable: " + ex.Message; ErrorLogService.Append(AppPaths.UserDataDirectory, "Restoration preview failed", _sourcePath, exception: ex); }
    }

    private async Task GenerateMotionAsync()
    {
        try
        {
            _status.Text = "Generating synchronized motion preview…";
            _generationCts?.Cancel(); _generationCts?.Dispose(); _generationCts = new CancellationTokenSource();
            var request = new VideoRestorationPreviewRequest(_sourcePath, _duration, _positions[Math.Clamp(_positionIndex, 0, _positions.Count - 1)], _selection.PreviewSettings, _encodeScale);
            VideoRestorationMotionPreview clip = await _service.GenerateMotionAsync(request, TimeSpan.FromSeconds(5), _generationCts.Token);
            OpenVideo(clip.ComparisonPath); _status.Text = $"Opened synchronized 5-second preview from {Format(clip.Start)}.";
        }
        catch (Exception ex) { _status.Text = "Motion preview unavailable: " + ex.Message; ErrorLogService.Append(AppPaths.UserDataDirectory, "Restoration motion preview failed", _sourcePath, exception: ex); }
    }

    private async Task AnalyzeAsync()
    {
        if (_analyze == null) return;
        try { _status.Text = "Analyzing source…"; VideoRestorationRecommendation? result = await _analyze(CancellationToken.None); _selection.SetRecommendation(result); _recommendationButton!.Enabled = result != null; if (result == null) { _status.Text = "No conservative restoration recommendation is available."; return; } _status.Text = "Recommendation is ready. Choose Preview Recommendation to compare it without changing encode settings."; }
        catch (Exception ex) { _status.Text = "Analysis unavailable: " + ex.Message; }
    }

    private void SetMode(RestorationPreviewMode mode) { _comparison.Panel1Collapsed = mode == RestorationPreviewMode.RestoredOnly; _comparison.Panel2Collapsed = mode == RestorationPreviewMode.OriginalOnly; }
    private static void ConfigurePane(Control pane, string label, PictureBox image) { var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 }; panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.Controls.Add(new Label { Text = label, AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 4) }, 0, 0); panel.Controls.Add(image, 0, 1); pane.Controls.Add(panel); }
    private static PictureBox FrameBox() => new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(20, 24, 30), Margin = new Padding(4) };
    private static Button AddButton(Control parent, string text, EventHandler handler) { var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 6, 4) }; button.Click += handler; parent.Controls.Add(button); return button; }
    private void OpenVideo(string path) { Process.Start(new ProcessStartInfo { FileName = !string.IsNullOrWhiteSpace(_externalPlayerPath) && File.Exists(_externalPlayerPath) ? _externalPlayerPath : path, Arguments = !string.IsNullOrWhiteSpace(_externalPlayerPath) && File.Exists(_externalPlayerPath) ? $"\"{path}\"" : string.Empty, UseShellExecute = true }); }
    private static string BuildSummary(VideoRestorationAnalysisResult? analysis) => analysis == null ? "Analysis has not been run. Previewing current restoration settings." : $"Noise: {analysis.Noise}   Banding: {analysis.Banding}   Blocking: {analysis.Blocking}   Scan: {analysis.ScanType}   Hint: {(analysis.AnimationHint == true ? "Animation" : "None")}";
    private static string Format(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
}
