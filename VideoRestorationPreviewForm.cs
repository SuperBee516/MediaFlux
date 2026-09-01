using System.Diagnostics;
using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux;

internal enum RestorationPreviewMode { SideBySide, OriginalOnly, RestoredOnly }

/// <summary>Non-destructive, request-gated restoration comparison surface.</summary>
internal sealed class VideoRestorationPreviewForm : MediaFluxForm
{
    private const int TimelineMaximum = 10_000;
    private readonly VideoRestorationPreviewService _service;
    private readonly VideoRestorationPreviewSelection _selection;
    private readonly string _sourcePath, _externalPlayerPath;
    private readonly TimeSpan _duration;
    private readonly EncodingService.ScaleMode _encodeScale;
    private readonly Action<VideoRestorationSettings> _apply;
    private readonly Func<CancellationToken, Task<VideoRestorationPreviewAnalysisUpdate?>>? _analyze;
    private readonly List<TimeSpan> _samples;
    private readonly PictureBox _original = FrameBox(), _restored = FrameBox();
    private readonly SplitContainer _comparison = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6 };
    private readonly TrackBar _timeline = new() { Minimum = 0, Maximum = TimelineMaximum, TickFrequency = 1000, Dock = DockStyle.Fill, AccessibleName = "Preview timeline" };
    private readonly ComboBox _previewSelection = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, AccessibleName = "Preview restoration selection" };
    private readonly ComboBox _displayMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, AccessibleName = "Comparison display mode" };
    private readonly Button _refresh = new() { Text = "Generate / Refresh Preview", AutoSize = true };
    private readonly Button _clip = new() { Text = "Preview Clip (5 sec)", AutoSize = true };
    private readonly Button _compareAi = new() { Text = "Compare AI Configurations...", AutoSize = true };
    private readonly Button _comparisonSession = new() { Text = "Comparison Session...", AutoSize = true };
    private readonly Button _analyzeButton = new() { Text = "Analyze / Recommend", AutoSize = true };
    private readonly Button _applyButton = new() { Text = "Apply to Encode Settings", AutoSize = true };
    private readonly Label _timestamp = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(980, 0) };
    private readonly Label _summary = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(980, 0) };
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _seekDebounce = new() { Interval = 350 };
    private CancellationTokenSource? _operationCts;
    private VideoRestorationStillPreview? _current;
    private readonly VideoRestorationPreviewOperationGate _requestGate = new();
    private TimeSpan _selectedPosition;
    private int _sampleIndex;
    private bool _suppressTimeline;
    private TemporalQualityResult? _temporalQuality;
    private VideoRestorationAnalysisResult? _analysis;

    public VideoRestorationPreviewForm(string sourcePath, TimeSpan duration, EncodingService.ScaleMode encodeScale, VideoRestorationPreviewService service, VideoRestorationPreviewSelection selection, VideoRestorationAnalysisResult? analysis, string externalPlayerPath, Action<VideoRestorationSettings> apply, Func<CancellationToken, Task<VideoRestorationPreviewAnalysisUpdate?>>? analyze = null)
    {
        _sourcePath = sourcePath; _duration = duration; _encodeScale = encodeScale; _service = service; _selection = selection; _externalPlayerPath = externalPlayerPath; _apply = apply; _analyze = analyze;
        _samples = VideoRestorationPreviewService.BuildRepresentativePositions(duration).ToList(); _selectedPosition = _samples.FirstOrDefault();
        Text = "Preview Video Restoration"; StartPosition = FormStartPosition.CenterParent; MinimumSize = new Size(900, 680); Size = new Size(1180, 820); ShowInTaskbar = false;
        _analysis = analysis; _summary.Text = BuildSummary(analysis); BuildUi();
        Shown += async (_, __) => await StartStillAsync();
        FormClosing += (_, __) => CancelCurrentOperation();
        FormClosed += (_, __) => { _seekDebounce.Dispose(); _operationCts?.Dispose(); _current?.Dispose(); };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 7, BackColor = Color.FromArgb(246, 248, 251) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { Text = "Original  |  Restored", Font = new Font(Font, FontStyle.Bold), AutoSize = true }, 0, 0);
        root.Controls.Add(new Label { Text = Path.GetFileName(_sourcePath), AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 6) }, 0, 1);
        root.Controls.Add(BuildControls(), 0, 2);
        root.Controls.Add(BuildTimeline(), 0, 3);
        root.Controls.Add(_summary, 0, 4);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.White, Padding = new Padding(10) }; body.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        ConfigurePane(_comparison.Panel1, "Original", _original); ConfigurePane(_comparison.Panel2, "Restored", _restored); body.Controls.Add(_comparison, 0, 0);
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) }; footer.Controls.Add(_timestamp); footer.Controls.Add(_status); body.Controls.Add(footer, 0, 1); root.Controls.Add(body, 0, 5);
        root.Controls.Add(new Label { Text = "Preview selections never change encoding until Apply to Encode Settings is chosen. Frames use the same effective restoration pipeline as encoding.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 8, 0, 0) }, 0, 6);
        Controls.Add(root); SetTimeline(_selectedPosition); UpdateState("Ready");
    }

    private Control BuildControls()
    {
        var groups = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        var navigation = Group("Navigation"); AddButton(navigation, "‹ Previous", (_, __) => Navigate(-1), "Move to the previous representative source position."); AddButton(navigation, "Next ›", (_, __) => Navigate(1), "Move to the next representative source position."); AddButton(navigation, "Random", (_, __) => { if (_samples.Count > 0) { _sampleIndex = Random.Shared.Next(_samples.Count); SelectPosition(_samples[_sampleIndex], immediate: true); } }, "Choose a representative source position at random.");
        var preview = Group("Restoration Preview"); _previewSelection.Items.AddRange(new object[] { "No Restoration", "Current Settings", "Recommended" }); _previewSelection.DrawMode = DrawMode.OwnerDrawFixed; _previewSelection.DrawItem += DrawPreviewSelection; _previewSelection.SelectedIndex = (int)_selection.Mode; _previewSelection.SelectedIndexChanged += (_, __) => SelectPreviewMode(); preview.Controls.Add(new Label { Text = "Preview Restoration:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) }); preview.Controls.Add(_previewSelection); preview.Controls.Add(_refresh); _refresh.Click += async (_, __) => await StartStillAsync(); _tips.SetToolTip(_previewSelection, "Recommended is unavailable until Analyze / Recommend has completed. All choices are preview-only."); _tips.SetToolTip(_refresh, "Regenerate the selected original/restored frame pair.");
        var analysis = Group("Analysis"); _analyzeButton.Enabled = _analyze != null; _analyzeButton.Click += async (_, __) => await AnalyzeAsync(); analysis.Controls.Add(_analyzeButton); _clip.Click += async (_, __) => await StartMotionAsync(); analysis.Controls.Add(_clip); _compareAi.Click += async (_, __) => await CompareAiAsync(); analysis.Controls.Add(_compareAi); _comparisonSession.Click += (_, __) => ShowSession(); analysis.Controls.Add(_comparisonSession); _tips.SetToolTip(_analyzeButton, "Analyze the selected source and make a conservative recommendation."); _tips.SetToolTip(_clip, "Build and open a synchronized five-second side-by-side MP4 preview."); _tips.SetToolTip(_compareAi, "Choose two or three locally available AI configurations at this exact source window.");
        var actions = Group("Actions"); _displayMode.Items.AddRange(Enum.GetValues<RestorationPreviewMode>().Cast<object>().ToArray()); _displayMode.SelectedItem = RestorationPreviewMode.SideBySide; _displayMode.SelectedIndexChanged += (_, __) => SetDisplayMode((RestorationPreviewMode)_displayMode.SelectedItem!); actions.Controls.Add(_displayMode); actions.Controls.Add(_applyButton); _applyButton.Click += (_, __) => ApplyPreviewSettings(); _tips.SetToolTip(_applyButton, "Explicitly copy the previewed restoration settings into the encode configuration.");
        groups.Controls.Add(navigation); groups.Controls.Add(preview); groups.Controls.Add(analysis); groups.Controls.Add(actions); return groups;
    }

    private Control BuildTimeline()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 0, 0, 8), BackColor = Color.White, Padding = new Padding(8) }; panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = "Seek timeline", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 7, 10, 0) }, 0, 0); panel.Controls.Add(_timeline, 1, 0); panel.Controls.Add(_timestamp, 2, 0);
        var markers = new Label { Text = "Representative samples: " + string.Join("  •  ", _samples.Select(Format)), AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 2) }; panel.Controls.Add(markers, 0, 1); panel.SetColumnSpan(markers, 3);
        _timeline.ValueChanged += (_, __) => TimelineChanged();
        _timeline.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _timeline.Value = Math.Clamp((int)Math.Round(e.X / (double)Math.Max(1, _timeline.Width) * TimelineMaximum), 0, TimelineMaximum); _timeline.Focus(); } };
        _seekDebounce.Tick += async (_, __) => { _seekDebounce.Stop(); await StartStillAsync(); };
        return panel;
    }

    private async Task StartStillAsync()
    {
        long request = BeginOperation(out CancellationTokenSource source); CancellationToken token = source.Token; SetBusy(true); UpdateState("Generating restored frame...");
        try
        {
            var preview = await _service.GenerateStillAsync(new VideoRestorationPreviewRequest(_sourcePath, _duration, _selectedPosition, _selection.PreviewSettings, _encodeScale), token);
            if (!IsCurrent(request)) { preview.Dispose(); return; }
            _current?.Dispose(); _current = preview; _original.Image = preview.Original; _restored.Image = preview.Restored; SetTimeline(preview.Position); UpdateState("Preview ready" + (string.IsNullOrWhiteSpace(preview.FilterChain) ? " (No Restoration)." : "."));
        }
        catch (OperationCanceledException) { if (IsCurrent(request)) UpdateState("Canceled"); }
        catch (Exception ex) { if (IsCurrent(request)) { UpdateState("Preview failed: " + Concise(ex)); ErrorLogService.Append(AppPaths.UserDataDirectory, "Restoration preview failed", _sourcePath, exception: ex); } }
        finally { EndOperation(request, source); }
    }

    private async Task StartMotionAsync()
    {
        long request = BeginOperation(out CancellationTokenSource source); CancellationToken token = source.Token; SetBusy(true); UpdateState("Generating 5-second preview...");
        try
        {
            VideoRestorationMotionPreview clip = await _service.GenerateMotionAsync(new VideoRestorationPreviewRequest(_sourcePath, _duration, _selectedPosition, _selection.PreviewSettings, _encodeScale), TimeSpan.FromSeconds(5), token);
            if (!IsCurrent(request)) return;
            _temporalQuality = clip.TemporalQuality; if (_temporalQuality != null) { _summary.Text = BuildSummary(null) + "\r\n" + _temporalQuality.Summary; _tips.SetToolTip(_summary, _temporalQuality.Reason); }
            OpenVideo(clip.ComparisonPath); UpdateState("Preview ready; opened synchronized 5-second comparison." + (_temporalQuality?.IsAdvisory == true ? " Review the temporal stability advisory." : ""));
        }
        catch (OperationCanceledException) { if (IsCurrent(request)) UpdateState("Canceled"); }
        catch (Exception ex) { if (IsCurrent(request)) { UpdateState("Preview failed: " + Concise(ex)); ErrorLogService.Append(AppPaths.UserDataDirectory, "Restoration motion preview failed", _sourcePath, exception: ex); } }
        finally { EndOperation(request, source); }
    }

    private async Task CompareAiAsync()
    {
        if (_selection.PreviewSettings.AiMode == AiRestorationMode.Off) { UpdateState("Enable AI Restoration in Advanced Settings before comparing configurations."); return; }
        long request = BeginOperation(out CancellationTokenSource source); CancellationToken token = source.Token; SetBusy(true); UpdateState("Preparing AI comparison...");
        try
        {
            var backend = new AiRestorationBackendService(AppPaths.InstallDirectory, log: message => ErrorLogService.Append(AppPaths.UserDataDirectory, message));
            AiRestorationCapabilities capabilities = await backend.GetCapabilitiesAsync(_selection.PreviewSettings, token); if (!capabilities.IsAvailable) throw new AiRestorationValidationException(capabilities.Error ?? "AI backend unavailable.");
            List<VideoRestorationSettings> defaults = BuildDefaultCandidates(capabilities.Models); IReadOnlyList<VideoRestorationSettings>? candidates = PickCandidates(capabilities.Models, defaults);
            if (candidates == null) { UpdateState("AI comparison canceled."); return; }
            var progress = new Progress<string>(message => { if (IsCurrent(request)) UpdateState(message); });
            AiConfigurationComparisonResult result = await new AiConfigurationComparisonService(_service).CompareAsync(_sourcePath, _duration, _selectedPosition, _encodeScale, candidates, progress, token);
            if (!IsCurrent(request)) return; ShowComparison(result);
            if (_analysis != null) { VideoRestorationRecommendation recommendation = VideoRestorationRecommendationService.Recommend(_analysis, _analysis.AnimationHint == true, aiContext: new AiRecommendationContext(capabilities.Models, result.Items)); _selection.SetRecommendation(recommendation); _previewSelection.Invalidate(); }
            UpdateState("AI comparison complete. Visual review remains authoritative.");
        }
        catch (OperationCanceledException) { if (IsCurrent(request)) UpdateState("Canceled"); }
        catch (Exception ex) { if (IsCurrent(request)) { UpdateState("AI comparison failed: " + Concise(ex)); ErrorLogService.Append(AppPaths.UserDataDirectory, "AI configuration comparison failed", _sourcePath, exception: ex); } }
        finally { EndOperation(request, source); }
    }

    private List<VideoRestorationSettings> BuildDefaultCandidates(IReadOnlyList<AiRestorationModel> models)
    {
        var candidates = new List<VideoRestorationSettings> { _selection.PreviewSettings.Clone() };
        VideoRestorationRecommendation? recommendation = _selection.Recommendation;
        if (recommendation is { Settings.AiMode: not AiRestorationMode.Off }) candidates.Add(recommendation.Settings.Clone());
        foreach (AiRestorationModel model in models.Where(model => model.Category == _selection.PreviewSettings.AiMode)) { var candidate = _selection.PreviewSettings.Clone(); candidate.AiModelId = model.Id; candidate.AiScale = model.SupportedScales.Contains(candidate.AiScale) ? candidate.AiScale : model.SupportedScales[0]; candidates.Add(candidate); }
        return AiConfigurationComparisonService.ValidateCuratedCandidates(candidates, models).ToList();
    }

    private IReadOnlyList<VideoRestorationSettings>? PickCandidates(IReadOnlyList<AiRestorationModel> models, IReadOnlyList<VideoRestorationSettings> defaults)
    {
        using var dialog = new MediaFluxForm { Text = "Choose AI Configurations", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(660, 300), MinimizeBox = false, MaximizeBox = false };
        var options = models.Where(model => model.Category == _selection.PreviewSettings.AiMode).SelectMany(model => model.SupportedScales.Select(scale => { var settings = _selection.PreviewSettings.Clone(); settings.AiModelId = model.Id; settings.AiScale = scale; return settings; })).DistinctBy(settings => $"{settings.AiModelId}|{settings.AiScale}").ToList();
        var checks = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true }; foreach (VideoRestorationSettings option in options) { string marker = VideoRestorationPreviewSelection.Equivalent(option, _selection.PreviewSettings) ? "Current" : _selection.Recommendation != null && VideoRestorationPreviewSelection.Equivalent(option, _selection.Recommendation.Settings) ? "Recommended" : AiConfigurationComparisonService.SessionReport.Any(entry => entry.Item.Settings.AiModelId == option.AiModelId && entry.Item.Settings.AiScale == option.AiScale) ? "Previously tested" : "Local"; checks.Items.Add(new CandidateChoice(option, $"{option.AiModelId} · {(int)option.AiScale}x · {option.AiMode} ({marker})"), defaults.Any(candidate => VideoRestorationPreviewSelection.Equivalent(candidate, option))); }
        var compare = new Button { Text = "Compare", DialogResult = DialogResult.OK, AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true }; var note = new Label { Text = "Choose 2–3 compatible local configurations. This does not change encode settings.", AutoSize = true, ForeColor = SystemColors.GrayText, Dock = DockStyle.Top, Padding = new Padding(8) }; var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) }; buttons.Controls.Add(cancel); buttons.Controls.Add(compare); dialog.Controls.Add(checks); dialog.Controls.Add(note); dialog.Controls.Add(buttons);
        dialog.FormClosing += (_, e) => { if (dialog.DialogResult != DialogResult.OK) return; if (checks.CheckedItems.Count is < 2 or > 3) { MessageBox.Show(dialog, "Select two or three configurations.", dialog.Text, MessageBoxButtons.OK, MessageBoxIcon.Information); e.Cancel = true; } };
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        return AiConfigurationComparisonService.ValidateCuratedCandidates(checks.CheckedItems.Cast<CandidateChoice>().Select(choice => choice.Settings), models);
    }

    private void ShowSession()
    {
        AiComparisonSessionEntry[] entries = AiConfigurationComparisonService.SessionReport.Where(entry => string.Equals(Path.GetFullPath(entry.SourcePath), Path.GetFullPath(_sourcePath), StringComparison.OrdinalIgnoreCase)).ToArray();
        using var dialog = new MediaFluxForm { Text = "Comparison Session", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(760, 310), MinimizeBox = false, MaximizeBox = false };
        var list = new ListBox { Dock = DockStyle.Fill, DataSource = entries.Select(entry => new SessionChoice(entry, $"{Format(entry.Start)} / {entry.Duration.TotalSeconds:0}s · {entry.Item.Settings.AiModelId} · {(int)entry.Item.Settings.AiScale}x · {entry.Item.TemporalQuality?.Classification} · {entry.Item.Rank} · {entry.RecordedAt.LocalDateTime:t}")).ToList(), DisplayMember = nameof(SessionChoice.Text) };
        var use = new Button { Text = "Use This Configuration", AutoSize = true }; var play = new Button { Text = "Play Preview", AutoSize = true }; var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true }; use.Click += (_, __) => { if (list.SelectedItem is SessionChoice choice) { _selection.UsePreviewSettings(choice.Entry.Item.Settings); _previewSelection.SelectedIndex = (int)RestorationPreviewSelectionMode.CurrentSettings; dialog.DialogResult = DialogResult.OK; } }; play.Click += (_, __) => { if (list.SelectedItem is SessionChoice choice) { if (File.Exists(choice.Entry.Item.Clip.ComparisonPath)) OpenVideo(choice.Entry.Item.Clip.ComparisonPath); else MessageBox.Show(dialog, "The cached preview is no longer available.", dialog.Text, MessageBoxButtons.OK, MessageBoxIcon.Information); } }; var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) }; buttons.Controls.Add(close); buttons.Controls.Add(use); buttons.Controls.Add(play); dialog.Controls.Add(list); dialog.Controls.Add(buttons); if (entries.Length == 0) list.Items.Add("No comparison results for this source in the current session."); else list.SelectedIndex = 0; if (dialog.ShowDialog(this) == DialogResult.OK) _ = StartStillAsync();
    }
    private sealed record CandidateChoice(VideoRestorationSettings Settings, string Text);
    private sealed record SessionChoice(AiComparisonSessionEntry Entry, string Text);

    private void ShowComparison(AiConfigurationComparisonResult result)
    {
        using var dialog = new MediaFluxForm { Text = "Compare AI Configurations", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(760, 310), MinimizeBox = false, MaximizeBox = false };
        var list = new ListBox { Dock = DockStyle.Fill, DisplayMember = nameof(AiConfigurationComparisonItem.Summary), DataSource = result.Items.ToList() };
        Button use = new() { Text = "Use This Configuration", AutoSize = true }; Button play = new() { Text = "Play Selected", AutoSize = true }; Button close = new() { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
        use.Click += (_, __) => { if (list.SelectedItem is AiConfigurationComparisonItem item) { _selection.UsePreviewSettings(item.Settings); _previewSelection.SelectedIndex = (int)RestorationPreviewSelectionMode.CurrentSettings; dialog.DialogResult = DialogResult.OK; } };
        play.Click += (_, __) => { if (list.SelectedItem is AiConfigurationComparisonItem item) OpenVideo(item.Clip.ComparisonPath); };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) }; buttons.Controls.Add(close); buttons.Controls.Add(use); buttons.Controls.Add(play); dialog.Controls.Add(list); dialog.Controls.Add(buttons); list.SelectedIndex = 0;
        if (dialog.ShowDialog(this) == DialogResult.OK) _ = StartStillAsync();
    }

    private async Task AnalyzeAsync()
    {
        if (_analyze == null) return; long request = BeginOperation(out CancellationTokenSource source); CancellationToken token = source.Token; SetBusy(true); UpdateState("Analyzing source...");
        try
        {
            VideoRestorationPreviewAnalysisUpdate? update = await _analyze(token); if (!IsCurrent(request)) return;
            if (update == null) { UpdateState("No conservative restoration recommendation is available."); return; }
            _selection.SetRecommendation(update.Recommendation); _previewSelection.Invalidate(); _summary.Text = BuildSummary(update.Analysis) + $"   Recommended: {update.Recommendation.Settings.Preset}"; UpdateState("Analysis complete. Recommended preview is now available.");
        }
        catch (OperationCanceledException) { if (IsCurrent(request)) UpdateState("Canceled"); }
        catch (Exception ex) { if (IsCurrent(request)) UpdateState("Analysis failed: " + Concise(ex)); }
        finally { EndOperation(request, source); }
    }

    private void Navigate(int offset) { if (_samples.Count == 0) return; _sampleIndex = (_sampleIndex + offset + _samples.Count) % _samples.Count; SelectPosition(_samples[_sampleIndex], immediate: true); }
    private void TimelineChanged() { if (_suppressTimeline) return; _selectedPosition = TimelinePosition(); UpdateTimestamp(); _seekDebounce.Stop(); _seekDebounce.Start(); UpdateState("Seek position selected; preview will refresh when seeking stops."); }
    private void SelectPosition(TimeSpan position, bool immediate) { _selectedPosition = position; SetTimeline(position); if (immediate) _ = StartStillAsync(); else { _seekDebounce.Stop(); _seekDebounce.Start(); } }
    private void SelectPreviewMode() { var mode = (RestorationPreviewSelectionMode)_previewSelection.SelectedIndex; if (!_selection.SelectMode(mode)) { _previewSelection.SelectedIndex = (int)_selection.Mode; UpdateState("Recommended preview is unavailable. Run Analyze / Recommend first."); return; } _ = StartStillAsync(); }
    private void DrawPreviewSelection(object? sender, DrawItemEventArgs e) { if (e.Index < 0) return; bool unavailable = e.Index == (int)RestorationPreviewSelectionMode.Recommended && _selection.Recommendation == null; e.DrawBackground(); TextRenderer.DrawText(e.Graphics, _previewSelection.Items[e.Index]?.ToString(), e.Font, e.Bounds, unavailable ? SystemColors.GrayText : e.ForeColor, TextFormatFlags.VerticalCenter); if (!unavailable) e.DrawFocusRectangle(); }
    private void SetDisplayMode(RestorationPreviewMode mode) { _comparison.Panel1Collapsed = mode == RestorationPreviewMode.RestoredOnly; _comparison.Panel2Collapsed = mode == RestorationPreviewMode.OriginalOnly; }
    private void ApplyPreviewSettings()
    {
        if (_temporalQuality?.Classification == TemporalStability.SevereInstability && MessageBox.Show(this, _temporalQuality.Summary + "\r\n\r\n" + _temporalQuality.Reason + "\r\n\r\nApply these AI settings anyway?", "Temporal Stability Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (_temporalQuality?.Classification == TemporalStability.ModerateInstability) MessageBox.Show(this, _temporalQuality.Summary + "\r\n\r\n" + _temporalQuality.Reason, "Temporal Stability Advisory", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _apply(_selection.ApplyToEncodeSettings()); _applyButton.Enabled = false; UpdateState("Previewed settings were applied to encode settings.");
    }
    private long BeginOperation(out CancellationTokenSource source) { _seekDebounce.Stop(); source = new CancellationTokenSource(); CancellationTokenSource? previous = Interlocked.Exchange(ref _operationCts, source); previous?.Cancel(); return _requestGate.Begin(); }
    private void EndOperation(long request, CancellationTokenSource source) { if (IsCurrent(request)) { Interlocked.CompareExchange(ref _operationCts, null, source); SetBusy(false); } source.Dispose(); }
    private bool IsCurrent(long request) => _requestGate.IsCurrent(request) && !IsDisposed && !Disposing;
    private void CancelCurrentOperation() { _seekDebounce.Stop(); _requestGate.Invalidate(); Interlocked.Exchange(ref _operationCts, null)?.Cancel(); }
    private void SetBusy(bool busy) { _refresh.Enabled = !busy; _clip.Enabled = !busy; _compareAi.Enabled = !busy; _comparisonSession.Enabled = !busy; _analyzeButton.Enabled = !busy && _analyze != null; _applyButton.Enabled = !busy && _selection.DiffersFromEncode; }
    private void SetTimeline(TimeSpan position) { _suppressTimeline = true; _timeline.Value = Math.Clamp((int)Math.Round(position.TotalSeconds / Math.Max(.001, _duration.TotalSeconds) * TimelineMaximum), 0, TimelineMaximum); _suppressTimeline = false; UpdateTimestamp(); }
    private TimeSpan TimelinePosition() => TimeSpan.FromSeconds(_duration.TotalSeconds * _timeline.Value / TimelineMaximum);
    private void UpdateTimestamp() { _timestamp.Text = $"{Format(_selectedPosition)} / {Format(_duration)}" + (_encodeScale != EncodingService.ScaleMode.None || _selection.PreviewSettings.Resize != VideoRestorationResize.Original ? "  •  effective output resolution" : ""); }
    private void UpdateState(string value) => _status.Text = value;
    private static FlowLayoutPanel Group(string title) { var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(6), Margin = new Padding(0, 0, 8, 6) }; panel.Controls.Add(new Label { Text = title + ":", AutoSize = true, Font = new Font("Segoe UI", 8F, FontStyle.Bold), Margin = new Padding(0, 7, 6, 0) }); return panel; }
    private Button AddButton(Control parent, string text, EventHandler handler, string tooltip) { var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 5, 0) }; button.Click += handler; _tips.SetToolTip(button, tooltip); parent.Controls.Add(button); return button; }
    private static void ConfigurePane(Control pane, string label, PictureBox image) { var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 }; panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.Controls.Add(new Label { Text = label, AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 4) }, 0, 0); panel.Controls.Add(image, 0, 1); pane.Controls.Add(panel); }
    private static PictureBox FrameBox() => new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(20, 24, 30), Margin = new Padding(4) };
    private void OpenVideo(string path) { Process.Start(new ProcessStartInfo { FileName = !string.IsNullOrWhiteSpace(_externalPlayerPath) && File.Exists(_externalPlayerPath) ? _externalPlayerPath : path, Arguments = !string.IsNullOrWhiteSpace(_externalPlayerPath) && File.Exists(_externalPlayerPath) ? $"\"{path}\"" : string.Empty, UseShellExecute = true }); }
    private static string BuildSummary(VideoRestorationAnalysisResult? analysis) => analysis == null ? "Analysis has not been run. Previewing current restoration settings." : $"Noise: {analysis.Noise}   Banding: {analysis.Banding}   Blocking: {analysis.Blocking}   Scan: {analysis.ScanType}   Hint: {(analysis.AnimationHint == true ? "Animation" : "None")}";
    private static string Concise(Exception ex) => ex is FileNotFoundException ? "source is unavailable." : ex.Message.Length > 160 ? ex.Message[..160] + "…" : ex.Message;
    private static string Format(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"hh\:mm\:ss") : time.ToString(@"mm\:ss");
}
