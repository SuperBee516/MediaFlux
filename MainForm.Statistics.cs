using MediaFlux.Models;
using MediaFlux.Services;
using System.Diagnostics;

namespace MediaFlux
{
    public partial class MainForm
    {
        private EncodingStatisticsService _encodingStatisticsService = null!;
        private readonly Dictionary<string, Label> _selectedStatisticsLabels =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Label> _lifetimeStatisticsLabels =
            new(StringComparer.OrdinalIgnoreCase);
        private ComboBox? _statisticsPeriodCombo;
        private DateTimePicker? _statisticsFromDate;
        private DateTimePicker? _statisticsToDate;
        private Label? _statisticsPeriodDescription;
        private DataGridView? _statisticsGroupGrid;

        private Control CreateEncodingStatisticsGroup()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 4,
                Margin = Padding.Empty,
                Padding = new Padding(0),
                BackColor = SystemColors.Control
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(CreateStatisticsToolbar(), 0, 0);

            var summaries = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 6, 0, 6),
                Padding = Padding.Empty
            };
            summaries.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            summaries.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            summaries.Controls.Add(
                CreateStatisticsSummaryGroup(
                    "Selected period",
                    _selectedStatisticsLabels),
                0,
                0);
            summaries.Controls.Add(
                CreateStatisticsSummaryGroup(
                    "Lifetime totals",
                    _lifetimeStatisticsLabels),
                1,
                0);
            root.Controls.Add(summaries, 0, 1);

            root.Controls.Add(
                new Label
                {
                    Text = "Size, savings, and encoding-speed metrics use successful finalized outputs. " +
                           "Failed or cancelled partial files are excluded.",
                    AutoSize = true,
                    ForeColor = SystemColors.GrayText,
                    Margin = new Padding(6, 0, 6, 4)
                },
                0,
                2);
            root.Controls.Add(CreateStatisticsGroupTable(), 0, 3);
            return root;
        }

        private Control CreateStatisticsToolbar()
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = Padding.Empty,
                Padding = new Padding(3, 3, 3, 1),
                BackColor = Color.White
            };

            panel.Controls.Add(new Label
            {
                Text = "Period:",
                AutoSize = true,
                Margin = new Padding(3, 7, 2, 0)
            });

            _statisticsPeriodCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 120,
                Margin = new Padding(0, 3, 8, 3)
            };
            _statisticsPeriodCombo.Items.AddRange(
                new object[]
                {
                    "Today",
                    "This week",
                    "This month",
                    "This year",
                    "All time",
                    "Custom"
                });
            _statisticsPeriodCombo.SelectedIndex = 0;
            panel.Controls.Add(_statisticsPeriodCombo);

            var fromLabel = new Label
            {
                Text = "From:",
                AutoSize = true,
                Margin = new Padding(3, 7, 2, 0),
                Visible = false
            };
            panel.Controls.Add(fromLabel);

            _statisticsFromDate = new DateTimePicker
            {
                Format = DateTimePickerFormat.Short,
                Width = 104,
                Value = DateTime.Today.AddDays(-30),
                Margin = new Padding(0, 3, 8, 3),
                Visible = false
            };
            panel.Controls.Add(_statisticsFromDate);

            var toLabel = new Label
            {
                Text = "To:",
                AutoSize = true,
                Margin = new Padding(3, 7, 2, 0),
                Visible = false
            };
            panel.Controls.Add(toLabel);

            _statisticsToDate = new DateTimePicker
            {
                Format = DateTimePickerFormat.Short,
                Width = 104,
                Value = DateTime.Today,
                Margin = new Padding(0, 3, 8, 3),
                Visible = false
            };
            panel.Controls.Add(_statisticsToDate);

            var apply = new Button
            {
                Text = "Apply",
                AutoSize = true,
                Margin = new Padding(0, 2, 8, 2),
                Visible = false
            };
            panel.Controls.Add(apply);

            _statisticsPeriodDescription = new Label
            {
                Text = "",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(8, 7, 3, 0)
            };
            panel.Controls.Add(_statisticsPeriodDescription);

            void UpdateCustomVisibility()
            {
                bool custom =
                    GetSelectedStatisticsPeriod() == EncodingStatisticsPeriod.Custom;
                fromLabel.Visible = custom;
                _statisticsFromDate.Visible = custom;
                toLabel.Visible = custom;
                _statisticsToDate.Visible = custom;
                apply.Visible = custom;
            }

            _statisticsPeriodCombo.SelectedIndexChanged += (_, __) =>
            {
                UpdateCustomVisibility();
                if (GetSelectedStatisticsPeriod() != EncodingStatisticsPeriod.Custom)
                    RefreshEncodingStatistics();
            };
            apply.Click += (_, __) => RefreshEncodingStatistics();

            return panel;
        }

        private static Control CreateStatisticsSummaryGroup(
            string title,
            IDictionary<string, Label> labels)
        {
            var group = new GroupBox
            {
                Text = title,
                Dock = DockStyle.Fill,
                AutoSize = true,
                Padding = new Padding(10, 8, 10, 8),
                Margin = new Padding(3),
                BackColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 0,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            AddStatisticsSummaryRow(table, labels, "files", "Files processed");
            AddStatisticsSummaryRow(table, labels, "outcomes", "Results");
            AddStatisticsSummaryRow(table, labels, "sizes", "Original → output");
            AddStatisticsSummaryRow(table, labels, "saved", "Storage saved");
            AddStatisticsSummaryRow(table, labels, "averageSaved", "Average saved / file");
            AddStatisticsSummaryRow(table, labels, "performance", "Average speed / time");

            group.Controls.Add(table);
            return group;
        }

        private static void AddStatisticsSummaryRow(
            TableLayoutPanel table,
            IDictionary<string, Label> labels,
            string key,
            string caption)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var captionLabel = new Label
            {
                Text = caption,
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 2, 12, 2)
            };
            var valueLabel = new Label
            {
                Text = "--",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 2, 0, 2)
            };

            labels[key] = valueLabel;
            table.Controls.Add(captionLabel, 0, row);
            table.Controls.Add(valueLabel, 1, row);
        }

        private Control CreateStatisticsGroupTable()
        {
            var group = new GroupBox
            {
                Text = "Selected period by codec and encoder",
                Dock = DockStyle.Top,
                Height = 170,
                Margin = new Padding(3),
                Padding = new Padding(8, 7, 8, 8),
                BackColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };

            _statisticsGroupGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoGenerateColumns = false,
                RowHeadersVisible = false,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };
            _statisticsGroupGrid.Columns.Add(
                new DataGridViewTextBoxColumn
                {
                    Name = "colStatsCodec",
                    HeaderText = "Codec",
                    Width = 125
                });
            _statisticsGroupGrid.Columns.Add(
                new DataGridViewTextBoxColumn
                {
                    Name = "colStatsEncoder",
                    HeaderText = "Encoder",
                    Width = 150
                });
            _statisticsGroupGrid.Columns.Add(
                new DataGridViewTextBoxColumn
                {
                    Name = "colStatsFiles",
                    HeaderText = "Files",
                    Width = 55
                });
            _statisticsGroupGrid.Columns.Add(
                new DataGridViewTextBoxColumn
                {
                    Name = "colStatsResults",
                    HeaderText =
                        "Success / Encode failed / Finalization failed / Skipped / Cancelled",
                    Width = 310
                });
            _statisticsGroupGrid.Columns.Add(
                new DataGridViewTextBoxColumn
                {
                    Name = "colStatsSaved",
                    HeaderText = "Storage saved",
                    Width = 115
                });
            _statisticsGroupGrid.Columns.Add(
                new DataGridViewTextBoxColumn
                {
                    Name = "colStatsReduction",
                    HeaderText = "Reduction",
                    Width = 85
                });
            _statisticsGroupGrid.Columns.Add(
                new DataGridViewTextBoxColumn
                {
                    Name = "colStatsSpeed",
                    HeaderText = "Avg speed",
                    AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                    MinimumWidth = 85
                });

            group.Controls.Add(_statisticsGroupGrid);
            return group;
        }

        private EncodingStatisticsPeriod GetSelectedStatisticsPeriod() =>
            _statisticsPeriodCombo?.SelectedIndex switch
            {
                1 => EncodingStatisticsPeriod.ThisWeek,
                2 => EncodingStatisticsPeriod.ThisMonth,
                3 => EncodingStatisticsPeriod.ThisYear,
                4 => EncodingStatisticsPeriod.AllTime,
                5 => EncodingStatisticsPeriod.Custom,
                _ => EncodingStatisticsPeriod.Today
            };

        private void RefreshEncodingStatistics()
        {
            if (_statisticsPeriodCombo == null ||
                _statisticsPeriodDescription == null ||
                _statisticsGroupGrid == null)
            {
                return;
            }

            IReadOnlyList<EncodingStatisticsRecord> all =
                _encodingStatisticsService.GetAll();
            EncodingStatisticsUtcRange range =
                EncodingStatisticsCalculator.GetUtcRange(
                    GetSelectedStatisticsPeriod(),
                    _statisticsFromDate?.Value.Date ?? DateTime.Today,
                    _statisticsToDate?.Value.Date ?? DateTime.Today,
                    DateTimeOffset.Now,
                    TimeZoneInfo.Local);
            EncodingStatisticsSnapshot selected =
                EncodingStatisticsCalculator.Aggregate(
                    all,
                    range.StartUtc,
                    range.EndUtcExclusive);
            EncodingStatisticsSnapshot lifetime =
                EncodingStatisticsCalculator.Aggregate(all);

            _statisticsPeriodDescription.Text = range.Description;
            UpdateStatisticsSummary(_selectedStatisticsLabels, selected);
            UpdateStatisticsSummary(_lifetimeStatisticsLabels, lifetime);

            _statisticsGroupGrid.Rows.Clear();
            foreach (EncodingStatisticsGroup item in selected.Groups)
            {
                _statisticsGroupGrid.Rows.Add(
                    item.Codec,
                    item.Encoder,
                    item.FilesProcessed.ToString("N0"),
                    $"{item.Successful:N0} / {item.Failed:N0} / {item.FinalizationFailed:N0} finalization / " +
                    $"{item.Skipped:N0} / {item.Cancelled:N0}",
                    FormatStatisticsSavings(item.SpaceSavedBytes),
                    item.OriginalBytes > 0
                        ? $"{item.ReductionPercent:0.#}%"
                        : "--",
                    item.AverageEncodingSpeed > 0
                        ? $"{item.AverageEncodingSpeed:0.##}×"
                        : "--");
            }
        }

        private static void UpdateStatisticsSummary(
            IReadOnlyDictionary<string, Label> labels,
            EncodingStatisticsSnapshot snapshot)
        {
            labels["files"].Text = snapshot.FilesProcessed.ToString("N0");
            labels["outcomes"].Text =
                $"{snapshot.Successful:N0} successful · {snapshot.Failed:N0} failed · " +
                $"{snapshot.FinalizationFailed:N0} finalization failed · " +
                $"{snapshot.Skipped:N0} skipped · {snapshot.Cancelled:N0} cancelled";
            labels["sizes"].Text = snapshot.FilesWithSizeData > 0
                ? $"{EncodingStatisticsCalculator.FormatBytes(snapshot.OriginalBytes)} → " +
                  EncodingStatisticsCalculator.FormatBytes(snapshot.OutputBytes)
                : "--";
            labels["saved"].Text = snapshot.FilesWithSizeData > 0
                ? $"{FormatStatisticsSavings(snapshot.SpaceSavedBytes)} " +
                  $"({snapshot.ReductionPercent:0.#}%)"
                : "--";
            labels["averageSaved"].Text = snapshot.FilesWithSizeData > 0
                ? FormatStatisticsSavings(
                    (long)Math.Round(snapshot.AverageSpaceSavedBytes))
                : "--";
            labels["performance"].Text =
                $"{(snapshot.AverageEncodingSpeed > 0 ? $"{snapshot.AverageEncodingSpeed:0.##}×" : "--")} / " +
                $"{FormatStatisticsDuration(snapshot.AverageProcessingSeconds)}";
        }

        private static string FormatStatisticsSavings(long bytes) =>
            bytes >= 0
                ? EncodingStatisticsCalculator.FormatBytes(bytes)
                : $"{EncodingStatisticsCalculator.FormatBytes(-bytes)} larger";

        private static string FormatStatisticsDuration(double seconds)
        {
            if (seconds <= 0 || !double.IsFinite(seconds))
                return "--";

            TimeSpan duration = TimeSpan.FromSeconds(seconds);
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
                : $"{duration.Minutes}:{duration.Seconds:00}";
        }

        private void RecordEncodingStatistics(
            string operationId,
            DateTime startUtc,
            DateTime endUtc,
            EncodingStatisticsOutcome outcome,
            string sourcePath,
            string outputPath,
            string codec,
            string encoder,
            long? sourceSizeBytes,
            long? outputSizeBytes,
            double? mediaDurationSeconds,
            double processingSeconds,
            string notes = "")
        {
            try
            {
                bool added = _encodingStatisticsService.AppendFinalized(
                    new EncodingStatisticsRecord
                    {
                        Id = operationId,
                        StartUtc = startUtc,
                        EndUtc = endUtc,
                        Outcome = outcome,
                        SourcePath = sourcePath,
                        OutputPath = outputPath,
                        Codec = codec,
                        Encoder = encoder,
                        SourceSizeBytes = sourceSizeBytes,
                        OutputSizeBytes = outputSizeBytes,
                        MediaDurationSeconds = mediaDurationSeconds,
                        ProcessingSeconds = processingSeconds,
                        Notes = notes
                    });

                if (added)
                    Ui(RefreshEncodingStatistics);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Statistics append failed: {ex}");
            }
        }

        private void RecordSkippedEncodingRows(
            IEnumerable<DataGridViewRow> rows)
        {
            string encoder = "Unknown";
            string codec = "Unknown";
            try
            {
                encoder = GetSelectedEncoderCapabilities().DisplayName;
                codec = GetValidatedEncoderSettingsFromUi(
                        includeConcurrentSessions: true)
                    .Resolved
                    .Selection
                    .FfmpegCodec;
            }
            catch
            {
                // A skipped decision remains useful even if current encoder metadata is unavailable.
            }

            bool addedAny = false;
            foreach (DataGridViewRow row in rows.Distinct())
            {
                if (row == null || row.IsNewRow)
                    continue;

                RowMeta meta = EnsureRowMeta(row);
                if (string.IsNullOrWhiteSpace(meta.StatisticsOperationId))
                    meta.StatisticsOperationId = Guid.NewGuid().ToString("N");

                DvdImportOptions? dvd = meta.IsDvdEncode
                    ? meta.DvdEncodeOptions
                    : null;
                string sourcePath = dvd != null
                    ? Path.GetDirectoryName(dvd.Candidate.Segments[0].Path) ?? meta.Path
                    : meta.Path;
                long? sourceSize = dvd != null
                    ? dvd.Candidate.CombinedSizeBytes
                    : TryGetFileSizeBytes(meta.Path);
                double? duration = dvd != null
                    ? dvd.Candidate.CombinedDurationSeconds
                    : meta.DurationSec > 0 ? meta.DurationSec : null;
                string reason = meta.ExcludedFromEncodeAsDuplicate
                    ? "Excluded as an exact duplicate."
                    : meta.EncodeRecommendation != null
                        ? $"Smart Encode recommendation: {meta.EncodeRecommendation.Kind}."
                        : "Excluded from the selected encode scope.";
                DateTime finalizedUtc = DateTime.UtcNow;

                try
                {
                    addedAny |= _encodingStatisticsService.AppendFinalized(
                        new EncodingStatisticsRecord
                        {
                            Id = meta.StatisticsOperationId,
                            StartUtc = finalizedUtc,
                            EndUtc = finalizedUtc,
                            Outcome = EncodingStatisticsOutcome.Skipped,
                            SourcePath = sourcePath,
                            Codec = codec,
                            Encoder = encoder,
                            SourceSizeBytes = sourceSize,
                            MediaDurationSeconds = duration,
                            ProcessingSeconds = 0,
                            Notes = reason
                        });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Statistics append (skipped) failed: {ex}");
                }
            }

            if (addedAny)
                RefreshEncodingStatistics();
        }

        private void RecordCancelledPendingRetries(
            IEnumerable<DataGridViewRow> rows)
        {
            string encoder = "Unknown";
            string codec = "Unknown";
            try
            {
                encoder = GetSelectedEncoderCapabilities().DisplayName;
                codec = GetValidatedEncoderSettingsFromUi(
                        includeConcurrentSessions: true)
                    .Resolved
                    .Selection
                    .FfmpegCodec;
            }
            catch
            {
                // Preserve the cancellation result even if encoder metadata is unavailable.
            }

            foreach (DataGridViewRow row in rows.Distinct())
            {
                if (row?.Tag is not RowMeta
                    {
                        AutoRetryScheduled: true
                    } meta ||
                    string.IsNullOrWhiteSpace(meta.StatisticsOperationId))
                {
                    continue;
                }

                DvdImportOptions? dvd = meta.IsDvdEncode
                    ? meta.DvdEncodeOptions
                    : null;
                string sourcePath = dvd != null
                    ? Path.GetDirectoryName(dvd.Candidate.Segments[0].Path) ?? meta.Path
                    : meta.Path;
                long? sourceSize = dvd != null
                    ? dvd.Candidate.CombinedSizeBytes
                    : TryGetFileSizeBytes(meta.Path);
                double? duration = dvd != null
                    ? dvd.Candidate.CombinedDurationSeconds
                    : meta.DurationSec > 0 ? meta.DurationSec : null;

                RecordEncodingStatistics(
                    meta.StatisticsOperationId,
                    meta.StatisticsStartUtc == default
                        ? DateTime.UtcNow
                        : meta.StatisticsStartUtc,
                    DateTime.UtcNow,
                    EncodingStatisticsOutcome.Cancelled,
                    sourcePath,
                    "",
                    codec,
                    encoder,
                    sourceSize,
                    outputSizeBytes: null,
                    mediaDurationSeconds: duration,
                    processingSeconds: meta.StatisticsProcessingSeconds,
                    notes: "The queue was cancelled before the scheduled retry reached a final result.");
            }
        }
    }
}
