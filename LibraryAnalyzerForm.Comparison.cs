using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private async Task OpenMemberComparisonAsync(
        string title,
        IReadOnlyList<VisualSimilarityMemberRecord> members,
        long? keeperFileId)
    {
        VisualSimilarityMemberRecord[] eligible = ResolveEligibleComparisonMembers(members, File.Exists);
        if (eligible.Length != 2) return;
        if (_reviewOptions.ComparisonLauncher != null)
        {
            await _reviewOptions.ComparisonLauncher(title, eligible.Select(member => member.FullPath).ToArray());
            return;
        }

        using var dialog = new MediaFluxForm
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(1100, 680),
            MinimumSize = new Size(900, 560)
        };
        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(10),
            Text = "Side-by-side comparison uses the existing Library Analyzer midpoint previews. Double-click a preview or use Play video for full playback."
        };
        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(8)
        };
        using var cancellation = new CancellationTokenSource();
        foreach (VisualSimilarityMemberRecord member in eligible)
        {
            var card = CreateVisualReviewCard(
                member,
                decisionsAllowed: false,
                selectedKeeperFileId: keeperFileId,
                suggestedKeeperFileId: null,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask);
            foreach (Button button in Descendants<Button>(card.Panel).Where(button =>
                         button.Text.Contains("keeper", StringComparison.OrdinalIgnoreCase) ||
                         button.Text.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
                         button.Text is "Protect" or "Unprotect"))
                button.Visible = false;
            body.Controls.Add(card.Panel);
            _ = LoadVisualReviewThumbnailAsync(card.Picture, card.Status, member, cancellation.Token);
        }
        var close = new Button { Text = "Close", Dock = DockStyle.Bottom, Height = 38, DialogResult = DialogResult.OK };
        dialog.Controls.Add(body);
        dialog.Controls.Add(close);
        dialog.Controls.Add(header);
        dialog.FormClosed += (_, _) =>
        {
            cancellation.Cancel();
            DisposeVisualReviewImages(body);
        };
        dialog.ShowDialog(this);
    }

    internal static VisualSimilarityMemberRecord[] ResolveEligibleComparisonMembers(
        IReadOnlyList<VisualSimilarityMemberRecord> members,
        Func<string, bool> fileExists) => members
        .Where(member => member.Availability == IndexedFileAvailability.Present && fileExists(member.FullPath))
        .DistinctBy(member => member.FileId)
        .Take(2)
        .ToArray();

    private static VisualSimilarityMemberRecord ToVisualMember(ExactDuplicateMemberRecord member) => new(
        member.GroupId, member.FileId, member.FullPath, member.LocationPath, member.SizeBytes, member.LastWriteUtc,
        member.Availability, member.VideoCodec, member.Width, member.Height, member.TotalBitRate, member.DurationSeconds,
        member.IsProtected, member.IsSuggestedKeeper, member.IsManualKeeper, false, "");

    private static VisualSimilarityMemberRecord ToVisualMember(VisualFamilyMemberRecord member) => new(
        member.FamilyId, member.FileId, member.FullPath, member.LocationPath, member.SizeBytes, member.LastWriteUtc,
        member.Availability, member.VideoCodec, member.Width, member.Height, member.TotalBitRate, member.DurationSeconds,
        member.IsProtected, member.IsSuggestedKeeper, member.IsManualKeeper, member.IsHdr, member.AudioSummary,
        member.FrameRate);
}
