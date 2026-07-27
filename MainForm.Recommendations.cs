using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux
{
    public partial class MainForm
    {
        private enum RecommendationStartChoice
        {
            EncodeAll,
            CandidatesOnly,
            Cancel
        }

        private void SetRecommendationAnalyzing(DataGridViewRow row)
        {
            if (!dgvEncodeQueue.Columns.Contains("colEncodeRecommendation"))
                return;

            RowMeta meta = EnsureRowMeta(row);
            meta.EncodeRecommendation = null;
            meta.BaselineEncodeRecommendation = null;
            meta.DeepAnalysis = null;
            var cell = row.Cells["colEncodeRecommendation"];
            cell.Value = _config.SmartRecommendationsEnabled ? "Analyzing…" : "";
            cell.ToolTipText = _config.SmartRecommendationsEnabled
                ? "Recommendation will be calculated from this file and the current encoding settings."
                : "";
            ApplyRecommendationCellStyle(cell, null);
        }

        private void SetRecommendationUnavailable(
            DataGridViewRow row,
            string reason)
        {
            if (!dgvEncodeQueue.Columns.Contains("colEncodeRecommendation"))
                return;

            var recommendation = new SmartEncodeRecommendation
            {
                Kind = SmartEncodeRecommendationKind.Unavailable,
                Confidence = SmartEncodeConfidence.Low,
                PrimaryReason = reason,
                Reasons = new[] { reason }
            };
            ApplySmartRecommendation(row, recommendation);
        }

        private void ApplySmartRecommendation(
            DataGridViewRow row,
            SmartEncodeRecommendation? recommendation,
            bool updateBaseline = true)
        {
            if (row == null ||
                row.IsNewRow ||
                row.DataGridView != dgvEncodeQueue ||
                !dgvEncodeQueue.Columns.Contains("colEncodeRecommendation"))
            {
                return;
            }

            RowMeta meta = EnsureRowMeta(row);
            if (updateBaseline)
            {
                meta.BaselineEncodeRecommendation = recommendation;
                meta.DeepAnalysis = null;
                if (recommendation != null &&
                    meta.ContentHint is
                        SmartEncodeContentHint.Animation or
                        SmartEncodeContentHint.ScreenContent)
                {
                    recommendation = new SmartEncodeDecisionService()
                        .RefineWithDeepAnalysis(
                            recommendation,
                            new DeepMediaAnalysisResult
                            {
                                InterlaceStatus = SampledInterlaceStatus.Unavailable
                            },
                            meta.ContentHint,
                            intendedOutputMb: 0);
                }
            }
            meta.EncodeRecommendation = recommendation;
            var cell = row.Cells["colEncodeRecommendation"];

            if (!_config.SmartRecommendationsEnabled)
            {
                cell.Value = "";
                cell.ToolTipText = "";
                ApplyRecommendationCellStyle(cell, null);
                if (row.Selected || dgvEncodeQueue.CurrentRow == row)
                    UpdateEncodePreview();
                return;
            }

            cell.Value = recommendation?.DisplayName ?? "Unavailable";
            cell.ToolTipText = recommendation?.BuildTooltip() ??
                "Required media metadata could not be analyzed.";
            ApplyRecommendationCellStyle(cell, recommendation?.Kind);
            if (row.Selected || dgvEncodeQueue.CurrentRow == row)
                UpdateEncodePreview();
        }

        private static void ApplyRecommendationCellStyle(
            DataGridViewCell cell,
            SmartEncodeRecommendationKind? kind)
        {
            (Color back, Color fore) = kind switch
            {
                SmartEncodeRecommendationKind.StrongCandidate =>
                    (Color.FromArgb(187, 247, 208), Color.FromArgb(22, 101, 52)),
                SmartEncodeRecommendationKind.ModerateCandidate =>
                    (Color.FromArgb(219, 234, 254), Color.FromArgb(30, 64, 175)),
                SmartEncodeRecommendationKind.Skip =>
                    (Color.FromArgb(226, 232, 240), Color.FromArgb(51, 65, 85)),
                SmartEncodeRecommendationKind.Review =>
                    (Color.FromArgb(254, 243, 199), Color.FromArgb(146, 64, 14)),
                SmartEncodeRecommendationKind.RemuxOnly =>
                    (Color.FromArgb(224, 231, 255), Color.FromArgb(55, 48, 163)),
                SmartEncodeRecommendationKind.Unavailable =>
                    (Color.FromArgb(241, 245, 249), Color.FromArgb(100, 116, 139)),
                _ => (Color.Empty, Color.Empty)
            };

            cell.Style.BackColor = back;
            cell.Style.ForeColor = fore;
            cell.Style.SelectionBackColor = back.IsEmpty
                ? Color.FromArgb(37, 99, 235)
                : back;
            cell.Style.SelectionForeColor = fore.IsEmpty ? Color.White : fore;
        }

        private RecommendationStartChoice ReviewRecommendationsBeforeStart(
            IReadOnlyCollection<DataGridViewRow> requestedRows)
        {
            if (!_config.SmartRecommendationsEnabled ||
                !_config.WarnBeforeEncodingSkippedOrReviewItems)
            {
                return RecommendationStartChoice.EncodeAll;
            }

            var eligible = requestedRows
                .Where(row =>
                    row.Tag is not RowMeta
                    {
                        ExcludedFromEncodeAsDuplicate: true
                    } &&
                    row.Tag is not RowMeta
                    {
                        IsDvdEncode: true
                    })
                .ToList();
            int skip = eligible.Count(row =>
                (row.Tag as RowMeta)?.EncodeRecommendation?.Kind ==
                SmartEncodeRecommendationKind.Skip);
            int review = eligible.Count(row =>
                (row.Tag as RowMeta)?.EncodeRecommendation?.Kind ==
                SmartEncodeRecommendationKind.Review);
            int remuxOnly = eligible.Count(row =>
                (row.Tag as RowMeta)?.EncodeRecommendation?.Kind ==
                SmartEncodeRecommendationKind.RemuxOnly);
            int unavailable = eligible.Count(row =>
            {
                SmartEncodeRecommendation? recommendation =
                    (row.Tag as RowMeta)?.EncodeRecommendation;
                return recommendation == null ||
                       recommendation.Kind == SmartEncodeRecommendationKind.Unavailable;
            });

            if (skip == 0 && review == 0 && remuxOnly == 0 && unavailable == 0)
                return RecommendationStartChoice.EncodeAll;

            string message =
                "Smart Encode found files that may not be good candidates for the current settings.\r\n\r\n" +
                $"Skip: {skip:N0}\r\n" +
                $"Review: {review:N0}\r\n" +
                $"Remux only: {remuxOnly:N0}\r\n" +
                $"Analysis unavailable: {unavailable:N0}\r\n\r\n" +
                "Yes: encode only Strong and Moderate candidates\r\n" +
                "No: encode every requested file anyway\r\n" +
                "Cancel: return to the queue";

            DialogResult choice = MessageBox.Show(
                this,
                message,
                "Review Smart Encode Recommendations",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);

            return choice switch
            {
                DialogResult.Yes => RecommendationStartChoice.CandidatesOnly,
                DialogResult.No => RecommendationStartChoice.EncodeAll,
                _ => RecommendationStartChoice.Cancel
            };
        }

        private static bool IsSmartEncodeCandidate(DataGridViewRow row)
        {
            return (row.Tag as RowMeta)?.EncodeRecommendation?.IsCandidate == true;
        }

        private void ViewRecommendationDetails_Click(object? sender, EventArgs e)
        {
            if (dgvEncodeQueue.SelectedRows.Count == 0)
            {
                ShowStatusInfo("Select a file to view its recommendation.");
                return;
            }

            DataGridViewRow row = dgvEncodeQueue.SelectedRows[0];
            SmartEncodeRecommendation? recommendation =
                (row.Tag as RowMeta)?.EncodeRecommendation;
            if (recommendation == null)
            {
                ShowStatusInfo("A recommendation is not available for the selected file.");
                return;
            }

            RowMeta? meta = row.Tag as RowMeta;
            using var dialog = new MediaFluxForm
            {
                Text = "Smart Encode Recommendation",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(560, 360),
                MinimumSize = new Size(480, 300),
                Padding = new Padding(12)
            };

            var details = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Segoe UI", 10F),
                BackColor = SystemColors.Window,
                Text =
                    recommendation.BuildTooltip() +
                    (meta?.DeepAnalysis == null
                        ? string.Empty
                        : Environment.NewLine + Environment.NewLine +
                          meta.DeepAnalysis.BuildSummary())
            };
            var close = new Button
            {
                Text = "Close",
                Dock = DockStyle.Bottom,
                Height = 32,
                DialogResult = DialogResult.OK
            };
            dialog.Controls.Add(details);
            dialog.Controls.Add(close);
            dialog.AcceptButton = close;
            dialog.CancelButton = close;
            dialog.ShowDialog(this);
        }

        private static int RecommendationSortRank(object? value)
        {
            return value?.ToString() switch
            {
                "Strong candidate" => 0,
                "Moderate candidate" => 1,
                "Review" => 2,
                "Remux only" => 3,
                "Skip" => 4,
                "Analyzing…" => 5,
                _ => 6
            };
        }
    }
}
