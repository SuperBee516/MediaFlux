using MediaFlux.Services;
using System.Diagnostics;

namespace MediaFlux
{
    internal enum SampleComparisonAction
    {
        Accept,
        IncreaseQuality,
        IncreaseCompression,
        TryAnotherCodec,
        Cancel
    }

    internal sealed class SampleComparisonForm : MediaFluxForm
    {
        private readonly string _externalPlayerPath;

        public SampleComparisonForm(
            string sourcePath,
            string settingsSummary,
            SampleComparisonResult result,
            string externalPlayerPath)
        {
            _externalPlayerPath = externalPlayerPath;
            ResultAction = SampleComparisonAction.Cancel;

            Text = "Pre-encode Sample Comparison";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(860, 580);
            Size = new Size(980, 680);
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(246, 248, 251);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(20),
                BackColor = BackColor
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(BuildHeader(sourcePath, settingsSummary), 0, 0);
            root.Controls.Add(BuildMetrics(result), 0, 1);
            root.Controls.Add(BuildClipList(result), 0, 2);
            root.Controls.Add(BuildActions(), 0, 3);
            Controls.Add(root);
        }

        public SampleComparisonAction ResultAction { get; private set; }

        private Control BuildHeader(string sourcePath, string settingsSummary)
        {
            var panel = new TableLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Margin = new Padding(0, 0, 0, 14)
            };
            panel.Controls.Add(new Label
            {
                Text = "Review before you encode",
                Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
                ForeColor = Color.FromArgb(31, 41, 55),
                AutoSize = true,
                Margin = Padding.Empty
            });
            panel.Controls.Add(new Label
            {
                Text = Path.GetFileName(sourcePath),
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.FromArgb(55, 65, 81),
                AutoEllipsis = true,
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = 25,
                Margin = new Padding(0, 7, 0, 0)
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

        private Control BuildMetrics(SampleComparisonResult result)
        {
            var metrics = new TableLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                BackColor = Color.White,
                Padding = new Padding(12),
                Margin = new Padding(0, 0, 0, 14),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.Single
            };
            for (int i = 0; i < 4; i++)
                metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

            metrics.Controls.Add(CreateMetric(
                "Projected final size",
                result.ProjectedFinalMb > 0 ? $"{result.ProjectedFinalMb:N1} MB" : "Unavailable"), 0, 0);
            metrics.Controls.Add(CreateMetric(
                "Measured bitrate",
                result.AverageBitrateKbps > 0 ? $"{result.AverageBitrateKbps:N0} kbps" : "Unavailable"), 1, 0);
            metrics.Controls.Add(CreateMetric(
                "Encode speed",
                result.EncodeSpeed > 0 ? $"{result.EncodeSpeed:0.00}x" : "Unavailable"), 2, 0);
            metrics.Controls.Add(CreateMetric(
                "Estimated completion",
                result.EstimatedCompletion > TimeSpan.Zero
                    ? FormatDuration(result.EstimatedCompletion)
                    : "Unavailable"), 3, 0);
            return metrics;
        }

        private Control BuildClipList(SampleComparisonResult result)
        {
            var host = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.White,
                Padding = new Padding(14),
                Margin = new Padding(0, 0, 0, 14)
            };
            host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            host.Controls.Add(new Label
            {
                Text = "Comparison clips",
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.FromArgb(31, 41, 55),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 3)
            }, 0, 0);

            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                ColumnCount = 1
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            foreach (var clip in result.Clips)
                content.Controls.Add(CreateClipRow(clip));

            var hint = new Label
            {
                Text = "Each preview plays the original on the left and the encoded sample on the right, synchronized.",
                ForeColor = Color.FromArgb(107, 114, 128),
                AutoSize = true,
                Margin = new Padding(4, 10, 4, 4)
            };
            content.Controls.Add(hint);
            host.Controls.Add(content, 0, 1);
            return host;
        }

        private Control CreateClipRow(SampleComparisonClip clip)
        {
            var row = new TableLayoutPanel
            {
                Height = 70,
                Dock = DockStyle.Top,
                ColumnCount = 3,
                BackColor = Color.FromArgb(249, 250, 251),
                Margin = new Padding(0, 7, 0, 0),
                Padding = new Padding(12, 9, 12, 9)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            row.Controls.Add(new Label
            {
                Text = clip.Label,
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.FromArgb(31, 41, 55),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            row.Controls.Add(new Label
            {
                Text = $"Starts at {FormatTimestamp(clip.Start)}",
                ForeColor = Color.FromArgb(107, 114, 128),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 1, 0);

            var play = new Button
            {
                Text = "▶  Play side by side",
                AutoSize = true,
                MinimumSize = new Size(155, 34),
                Anchor = AnchorStyles.Right
            };
            play.Click += (_, __) => OpenVideo(clip.ComparisonPath);
            row.Controls.Add(play, 2, 0);
            return row;
        }

        private Control BuildActions()
        {
            var actions = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = true,
                Margin = Padding.Empty
            };

            actions.Controls.Add(CreateActionButton("Cancel", SampleComparisonAction.Cancel));
            actions.Controls.Add(CreateActionButton("Try another codec", SampleComparisonAction.TryAnotherCodec));
            actions.Controls.Add(CreateActionButton("Increase compression", SampleComparisonAction.IncreaseCompression));
            actions.Controls.Add(CreateActionButton("Increase quality", SampleComparisonAction.IncreaseQuality));

            var accept = CreateActionButton("Accept settings", SampleComparisonAction.Accept);
            accept.Font = new Font(Font, FontStyle.Bold);
            accept.BackColor = Color.FromArgb(37, 99, 235);
            accept.ForeColor = Color.White;
            accept.FlatStyle = FlatStyle.Flat;
            accept.FlatAppearance.BorderSize = 0;
            actions.Controls.Add(accept);
            AcceptButton = accept;

            return actions;
        }

        private Button CreateActionButton(string text, SampleComparisonAction action)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(112, 36),
                Margin = new Padding(8, 0, 0, 0)
            };
            button.Click += (_, __) =>
            {
                ResultAction = action;
                DialogResult = action == SampleComparisonAction.Cancel
                    ? DialogResult.Cancel
                    : DialogResult.OK;
                Close();
            };
            return button;
        }

        private static Control CreateMetric(string label, string value)
        {
            var panel = new TableLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(8),
                Margin = Padding.Empty
            };
            panel.Controls.Add(new Label
            {
                Text = value,
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                ForeColor = Color.FromArgb(31, 41, 55),
                AutoSize = true,
                Margin = Padding.Empty
            });
            panel.Controls.Add(new Label
            {
                Text = label,
                ForeColor = Color.FromArgb(107, 114, 128),
                AutoSize = true,
                Margin = new Padding(0, 3, 0, 0)
            });
            return panel;
        }

        private void OpenVideo(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_externalPlayerPath) &&
                    File.Exists(_externalPlayerPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _externalPlayerPath,
                        Arguments = $"\"{path}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "The comparison clip could not be opened.\r\n\r\n" + ex.Message,
                    "MediaFlux",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private static string FormatTimestamp(TimeSpan value) =>
            value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

        private static string FormatDuration(TimeSpan value)
        {
            if (value.TotalHours >= 1)
                return value.ToString(@"h\:mm\:ss");
            return value.ToString(@"m\:ss");
        }
    }
}
