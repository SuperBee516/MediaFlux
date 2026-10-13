using System.Diagnostics;
using MediaFlux.Services;

namespace MediaFlux;

internal sealed class CqSampleComparisonForm : MediaFluxForm
{
    private readonly string _externalPlayerPath;

    public CqSampleComparisonForm(
        string sourcePath,
        string settingsSummary,
        SampleComparisonCqSetResult result,
        string externalPlayerPath)
    {
        ArgumentNullException.ThrowIfNull(result);
        _externalPlayerPath = externalPlayerPath;
        Text = "CQ Sample Comparison";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(920, 600);
        Size = new Size(1120, 760);
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(246, 248, 251);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(18),
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(BuildHeader(sourcePath, settingsSummary), 0, 0);
        root.Controls.Add(BuildMetrics(result), 0, 1);
        root.Controls.Add(BuildSamples(result), 0, 2);
        root.Controls.Add(new Label
        {
            Text = "Play a sample to review the original and that CQ output synchronized side by side. MediaFlux does not calculate an objective quality score.",
            AutoSize = true,
            ForeColor = Color.FromArgb(90, 100, 114),
            Margin = new Padding(2, 8, 2, 0)
        }, 0, 3);
        Controls.Add(root);
    }

    private Control BuildHeader(string sourcePath, string settingsSummary)
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Margin = new Padding(0, 0, 0, 12)
        };
        panel.Controls.Add(new Label
        {
            Text = "NVENC HEVC constant-quality comparison",
            Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
            ForeColor = Color.FromArgb(31, 41, 55),
            AutoSize = true,
            Margin = Padding.Empty
        });
        panel.Controls.Add(new Label
        {
            Text = Path.GetFileName(sourcePath),
            Font = new Font(Font, FontStyle.Bold),
            AutoEllipsis = true,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 24,
            Margin = new Padding(0, 6, 0, 0)
        });
        panel.Controls.Add(new Label
        {
            Text = settingsSummary,
            ForeColor = Color.FromArgb(107, 114, 128),
            AutoEllipsis = true,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 24,
            Margin = Padding.Empty
        });
        return panel;
    }

    private Control BuildMetrics(SampleComparisonCqSetResult result)
    {
        var table = new DataGridView
        {
            Name = "cqComparisonMetrics",
            Dock = DockStyle.Top,
            Height = 150,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None
        };
        table.Columns.Add("quality", "CQ");
        table.Columns.Add("bitrate", "Measured total bitrate");
        table.Columns.Add("size", "Projected full-file size");
        table.Columns.Add("confidence", "Projection confidence");
        table.Columns.Add("samples", "Samples");
        foreach (SampleComparisonCqRun run in result.Runs)
        {
            SampleComparisonResult projection = run.Result;
            string range = projection.ProjectedFinalMb > 0
                ? $"{projection.ProjectedFinalMb:N1} MB " +
                  $"({projection.ProjectedLowerMb:N1}–{projection.ProjectedUpperMb:N1} MB)"
                : "Unavailable";
            table.Rows.Add($"CQ{run.QualityValue}",
                projection.AverageBitrateKbps > 0
                    ? $"{projection.AverageBitrateKbps:N0} kbps"
                    : "Unavailable",
                range, projection.ProjectionConfidence.ToString(),
                $"{projection.SampleCount} × 25 s");
        }
        return table;
    }

    private Control BuildSamples(SampleComparisonCqSetResult result)
    {
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            BackColor = Color.White,
            Padding = new Padding(12),
            Margin = new Padding(0, 12, 0, 0)
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.Controls.Add(new Label
        {
            Text = "Beginning, middle and end samples",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(2, 0, 2, 6)
        });

        foreach (SampleComparisonCqRun run in result.Runs)
        foreach (SampleComparisonClip clip in run.Result.Clips)
        {
            var row = new TableLayoutPanel
            {
                Height = 48,
                Dock = DockStyle.Top,
                ColumnCount = 3,
                BackColor = Color.FromArgb(249, 250, 251),
                Margin = new Padding(0, 4, 0, 0),
                Padding = new Padding(8, 5, 8, 5)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(new Label
            {
                Text = $"CQ{run.QualityValue} · {clip.Label}",
                Font = new Font(Font, FontStyle.Bold),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            row.Controls.Add(new Label
            {
                Text = $"Starts at {clip.Start:hh\\:mm\\:ss}",
                ForeColor = Color.FromArgb(107, 114, 128),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 1, 0);
            var play = new Button
            {
                Text = "▶ Play synchronized comparison",
                AutoSize = true,
                MinimumSize = new Size(220, 32),
                Anchor = AnchorStyles.Right
            };
            string comparisonPath = clip.ComparisonPath;
            play.Click += (_, _) => OpenVideo(comparisonPath);
            row.Controls.Add(play, 2, 0);
            host.Controls.Add(row);
        }
        return host;
    }

    private void OpenVideo(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = !string.IsNullOrWhiteSpace(_externalPlayerPath) && File.Exists(_externalPlayerPath)
                    ? _externalPlayerPath
                    : path,
                Arguments = !string.IsNullOrWhiteSpace(_externalPlayerPath) && File.Exists(_externalPlayerPath)
                    ? $"\"{path}\""
                    : "",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the sample comparison.\r\n\r\n{ex.Message}",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
