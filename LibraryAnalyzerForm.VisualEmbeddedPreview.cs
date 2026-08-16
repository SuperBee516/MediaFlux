using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private readonly PictureBox _visualPreviewLeft = new() { Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom };
    private readonly PictureBox _visualPreviewRight = new() { Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label _visualPreviewLeftStatus = new() { Dock = DockStyle.Bottom, Height = 38, AutoEllipsis = true, Padding = new Padding(6, 5, 6, 0), ForeColor = SystemColors.GrayText };
    private readonly Label _visualPreviewRightStatus = new() { Dock = DockStyle.Bottom, Height = 38, AutoEllipsis = true, Padding = new Padding(6, 5, 6, 0), ForeColor = SystemColors.GrayText };
    private readonly Label _visualPreviewStatus = new() { Dock = DockStyle.Top, Height = 26, Padding = new Padding(6, 5, 6, 0), ForeColor = SystemColors.GrayText };
    private CancellationTokenSource? _visualEmbeddedPreviewCancellation;
    private int _visualEmbeddedPreviewVersion;

    private void BuildVisualComparisonPreview()
    {
        _visualComparisonPreview.Padding = new Padding(6);
        var title = new Label { Dock = DockStyle.Top, Height = 28, Padding = new Padding(2, 4, 2, 0), Text = "Comparison preview", Font = new Font(Font, FontStyle.Bold) };
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.Controls.Add(CreatePreviewCard(_visualPreviewLeft, _visualPreviewLeftStatus), 0, 0);
        cards.Controls.Add(CreatePreviewCard(_visualPreviewRight, _visualPreviewRightStatus), 1, 0);
        _visualComparisonPreview.Controls.Add(cards);
        _visualComparisonPreview.Controls.Add(_visualPreviewStatus);
        _visualComparisonPreview.Controls.Add(title);
        ApplyVisualComparisonPreviewLayout();
    }

    private static Panel CreatePreviewCard(PictureBox picture, Label status)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(4), BorderStyle = BorderStyle.FixedSingle };
        panel.Controls.Add(picture);
        panel.Controls.Add(status);
        return panel;
    }

    private async Task ToggleVisualComparisonPreviewAsync()
    {
        if (_lifecycleCleanupCompleted) return;
        MediaFlux.Models.LibraryAnalyzerUiState? state = _reviewOptions.UiState;
        if (state == null) return;
        state.ShowVisualComparisonPreview = _visualComparisonPreviewEnabled.Checked;
        _reviewOptions.UiStateChanged?.Invoke(state);
        ApplyVisualComparisonPreviewLayout();
        if (_visualComparisonPreviewEnabled.Checked)
            await UpdateVisualComparisonPreviewAsync(SelectedVisualGroup() is { } group
                ? await Task.Run(() => _runtime.VisualCatalog.GetVisualGroupMembers(group.GroupId))
                : Array.Empty<VisualSimilarityMemberRecord>());
    }

    private void ApplyVisualComparisonPreviewLayout()
    {
        bool enabled = _visualComparisonPreviewEnabled.Checked;
        _visualComparisonPreview.Visible = enabled;
        _visualDetailSplit.Panel2Collapsed = !enabled;
        if (!enabled) ClearVisualComparisonPreview();
    }

    private async Task UpdateVisualComparisonPreviewAsync(IReadOnlyList<VisualSimilarityMemberRecord> members)
    {
        _visualEmbeddedPreviewCancellation?.Cancel();
        _visualEmbeddedPreviewCancellation?.Dispose();
        _visualEmbeddedPreviewCancellation = null;
        int version = Interlocked.Increment(ref _visualEmbeddedPreviewVersion);
        if (!_visualComparisonPreviewEnabled.Checked || IsDisposed || Disposing)
        {
            ClearVisualComparisonPreview();
            return;
        }

        VisualSimilarityMemberRecord[] eligible = ResolveEligibleComparisonMembers(members, File.Exists);
        if (eligible.Length != 2)
        {
            ClearVisualComparisonPreview();
            _visualPreviewStatus.Text = members.Count == 0 ? "Select a visual match to preview it." : "Preview unavailable — one or more files are missing or unavailable.";
            return;
        }

        var cancellation = new CancellationTokenSource();
        _visualEmbeddedPreviewCancellation = cancellation;
        _visualPreviewStatus.Text = "Loading cached midpoint previews…";
        _visualPreviewLeftStatus.Text = Path.GetFileName(eligible[0].FullPath);
        _visualPreviewRightStatus.Text = Path.GetFileName(eligible[1].FullPath);
        try
        {
            await Task.Delay(180, cancellation.Token);
            if (version != Volatile.Read(ref _visualEmbeddedPreviewVersion) || cancellation.IsCancellationRequested) return;
            await Task.WhenAll(
                LoadVisualReviewThumbnailAsync(_visualPreviewLeft, _visualPreviewLeftStatus, eligible[0], cancellation.Token),
                LoadVisualReviewThumbnailAsync(_visualPreviewRight, _visualPreviewRightStatus, eligible[1], cancellation.Token));
            if (version == Volatile.Read(ref _visualEmbeddedPreviewVersion) && !cancellation.IsCancellationRequested)
                _visualPreviewStatus.Text = "Midpoint previews use the existing visual-review thumbnail cache.";
        }
        catch (OperationCanceledException)
        {
            // A newer selection superseded this lightweight preview request.
        }
    }

    private void ClearVisualComparisonPreview()
    {
        DisposePreviewImage(_visualPreviewLeft);
        DisposePreviewImage(_visualPreviewRight);
        _visualPreviewLeftStatus.Text = "";
        _visualPreviewRightStatus.Text = "";
    }

    private static void DisposePreviewImage(PictureBox picture)
    {
        Image? image = picture.Image;
        picture.Image = null;
        image?.Dispose();
    }
}
