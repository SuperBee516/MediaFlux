using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private sealed record MemberComparisonSnapshot(
        VisualSimilarityMemberRecord[] Members,
        long? KeeperFileId,
        string HeaderText);

    private sealed record MemberComparisonWorkflow(
        Func<Task<MemberComparisonSnapshot?>> Reload,
        Func<int, Task<bool>> NavigateFamily,
        Func<int, bool> CanNavigateFamily,
        Func<long, Task> SetFamilyKeeper);

    private async Task OpenMemberComparisonAsync(
        string title,
        IReadOnlyList<VisualSimilarityMemberRecord> members,
        long? keeperFileId,
        MemberComparisonWorkflow? workflow = null)
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
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(6)
        };
        var close = new Button { Text = "Close", Width = 90, DialogResult = DialogResult.OK };
        var nextFamily = new Button { Text = "Next Family", Width = 110, Visible = workflow != null, Enabled = false };
        var previousFamily = new Button { Text = "Previous Family", Width = 125, Visible = workflow != null, Enabled = false };
        footer.Controls.Add(close);
        footer.Controls.Add(nextFamily);
        footer.Controls.Add(previousFamily);
        dialog.AcceptButton = close;
        MemberComparisonSnapshot current = new(eligible, keeperFileId,
            "Side-by-side comparison uses the existing Library Analyzer midpoint previews. Double-click a preview or use Play video for full playback.");
        using var availabilityTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        availabilityTimer.Tick += (_, _) =>
        {
            foreach (Button button in Descendants<Button>(body).Where(value => value.Name is "FamilyCompareSetLeftKeeper" or "FamilyCompareSetRightKeeper"))
            {
                VisualSimilarityMemberRecord? member = button.Tag as VisualSimilarityMemberRecord;
                button.Enabled = workflow != null && member != null && current.KeeperFileId != member.FileId &&
                                 member.Availability == IndexedFileAvailability.Present && File.Exists(member.FullPath);
            }
        };
        CancellationTokenSource currentPreviewCancellation = new();

        Task RenderCurrentAsync()
        {
            if (dialog.IsDisposed || dialog.Disposing) return Task.CompletedTask;
            // A fresh token source prevents previews from an earlier family overwriting this view.
            currentPreviewCancellation.Cancel();
            currentPreviewCancellation.Dispose();
            currentPreviewCancellation = new CancellationTokenSource();
            DisposeVisualReviewImages(body);
            body.Controls.Clear();
            header.Text = current.HeaderText;
            VisualSimilarityMemberRecord[] renderMembers = ResolveEligibleComparisonMembers(current.Members, File.Exists);
            if (renderMembers.Length != 2)
            {
                header.Text = "A compared file is unavailable. Use family navigation to continue.";
                previousFamily.Enabled = workflow != null && workflow.CanNavigateFamily(-1);
                nextFamily.Enabled = workflow != null && workflow.CanNavigateFamily(1);
                return Task.CompletedTask;
            }
            foreach (VisualSimilarityMemberRecord member in renderMembers)
            {
                async Task SetKeeperAsync()
                {
                    if (workflow == null || current.KeeperFileId == member.FileId || !File.Exists(member.FullPath)) return;
                    await workflow.SetFamilyKeeper(member.FileId);
                    MemberComparisonSnapshot? refreshed = await workflow.Reload();
                    if (refreshed != null) { current = refreshed; await RenderCurrentAsync(); }
                }

                var card = CreateVisualReviewCard(
                    member,
                    decisionsAllowed: workflow != null,
                    selectedKeeperFileId: current.KeeperFileId,
                    suggestedKeeperFileId: null,
                    SetKeeperAsync,
                    () => Task.CompletedTask,
                    () => Task.CompletedTask);
                foreach (Button button in Descendants<Button>(card.Panel).Where(button =>
                             button.Text.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
                             button.Text is "Protect" or "Unprotect"))
                    button.Visible = false;
                Button? keep = Descendants<Button>(card.Panel).FirstOrDefault(button =>
                    button.Text.Contains("keeper", StringComparison.OrdinalIgnoreCase) ||
                    button.Text.Contains("Keep", StringComparison.OrdinalIgnoreCase));
                if (keep != null)
                {
                    keep.Visible = workflow != null;
                    keep.Enabled = workflow != null && current.KeeperFileId != member.FileId && File.Exists(member.FullPath);
                    keep.Text = current.KeeperFileId == member.FileId ? "Current keeper" : "Set as keeper";
                    keep.Name = member.FileId == renderMembers[0].FileId ? "FamilyCompareSetLeftKeeper" : "FamilyCompareSetRightKeeper";
                    keep.Tag = member;
                }
                body.Controls.Add(card.Panel);
                _ = LoadVisualReviewThumbnailAsync(card.Picture, card.Status, member, currentPreviewCancellation.Token);
            }
            previousFamily.Enabled = workflow != null && workflow.CanNavigateFamily(-1);
            nextFamily.Enabled = workflow != null && workflow.CanNavigateFamily(1);
            return Task.CompletedTask;
        }
        // Navigation is bounded and the callback reports whether an eligible family was found.
        async Task NavigateFamilyAsync(int direction)
        {
            if (workflow == null) return;
            bool moved = await workflow.NavigateFamily(direction);
            if (!moved) { await RenderCurrentAsync(); return; }
            MemberComparisonSnapshot? refreshed = await workflow.Reload();
            if (refreshed != null) { current = refreshed; await RenderCurrentAsync(); }
        }
        previousFamily.Click += async (_, _) => await NavigateFamilyAsync(-1);
        nextFamily.Click += async (_, _) => await NavigateFamilyAsync(1);
        await RenderCurrentAsync();
        dialog.Controls.Add(body);
        if (workflow != null)
            dialog.Controls.Add(footer);
        else
        {
            close.Dock = DockStyle.Bottom;
            close.Height = 38;
            dialog.Controls.Add(close);
        }
        dialog.Controls.Add(header);
        dialog.FormClosed += (_, _) =>
        {
            availabilityTimer.Stop();
            currentPreviewCancellation.Cancel();
            currentPreviewCancellation.Dispose();
            DisposeVisualReviewImages(body);
        };
        dialog.Shown += (_, _) => availabilityTimer.Start();
        dialog.ShowDialog(this);
    }

    internal static VisualSimilarityMemberRecord[] ResolveEligibleComparisonMembers(
        IReadOnlyList<VisualSimilarityMemberRecord> members,
        Func<string, bool> fileExists) => members
        .Where(member => member.Availability == IndexedFileAvailability.Present && fileExists(member.FullPath))
        .DistinctBy(member => member.FileId)
        .Take(2)
        .ToArray();

    internal static int? ResolveAdjacentFamilyComparisonIndex(
        IReadOnlyList<long> eligibleFamilyIds,
        long currentFamilyId,
        int direction)
    {
        int current = -1;
        for (int index = 0; index < eligibleFamilyIds.Count; index++)
            if (eligibleFamilyIds[index] == currentFamilyId) { current = index; break; }
        if (current < 0 || direction == 0) return null;
        int adjacent = current + Math.Sign(direction);
        return adjacent >= 0 && adjacent < eligibleFamilyIds.Count ? adjacent : null;
    }

    internal static VisualFamilyMemberRecord? ResolveFamilyComparisonCandidate(
        IReadOnlyList<VisualFamilyMemberRecord> members,
        long keeperFileId,
        long? preferredMemberFileId,
        Func<string, bool> fileExists) =>
        members.FirstOrDefault(member => member.FileId == preferredMemberFileId && member.FileId != keeperFileId &&
                                         member.Availability == IndexedFileAvailability.Present && fileExists(member.FullPath))
        ?? members.FirstOrDefault(member => member.FileId != keeperFileId &&
                                            member.Availability == IndexedFileAvailability.Present && fileExists(member.FullPath));

    internal static string ResolveVisualFamilyMemberRole(VisualFamilyRecord family, VisualFamilyMemberRecord member)
    {
        bool effectiveKeeper = member.IsManualKeeper || (!family.ManualKeeperFileId.HasValue && member.IsSuggestedKeeper);
        return !effectiveKeeper ? "Candidate" : member.IsManualKeeper ? "Keeper (Manual)" : "Keeper (Automatic)";
    }

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
