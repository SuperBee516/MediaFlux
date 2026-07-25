using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux
{
    public sealed class DvdTitleSelectionForm : MediaFluxForm
    {
        private readonly DvdFolderAnalysisResult _analysis;
        private readonly string _initialOutputFolder;
        private readonly string _outputNamingPattern;
        private readonly DataGridView _candidateGrid;
        private readonly Label _recommendationLabel;
        private readonly TextBox _warningsText;
        private readonly Label _videoStreamLabel;
        private readonly CheckedListBox _audioStreams;
        private readonly CheckedListBox _subtitleStreams;
        private readonly RadioButton _losslessMode;
        private readonly RadioButton _encodeMode;
        private readonly Label _modeDescription;
        private readonly GroupBox _outputGroup;
        private readonly TextBox _outputPathText;
        private readonly Button _browseButton;
        private readonly Button _startButton;
        private readonly Label _validationLabel;
        private bool _updatingOutputPath;
        private bool _outputPathEdited;
        private bool _ambiguityConfirmed;

        public DvdTitleSelectionForm(
            DvdFolderAnalysisResult analysis,
            string initialOutputFolder,
            DvdOutputMode initialOutputMode = DvdOutputMode.LosslessRemuxToMkv,
            string? outputNamingPattern = null)
        {
            _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
            _initialOutputFolder = initialOutputFolder ?? "";
            _outputNamingPattern = outputNamingPattern ?? "";
            Text = "DVD Title Selection";
            StartPosition = FormStartPosition.CenterParent;
            Width = 1180;
            Height = 780;
            MinimumSize = new Size(980, 680);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 1,
                RowCount = 7
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 27));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var heading = new Label
            {
                AutoSize = true,
                Font = new Font(Font.FontFamily, 14, FontStyle.Bold),
                Text = "Choose the DVD title to process",
                Margin = new Padding(0, 0, 0, 8)
            };

            _candidateGrid = CreateCandidateGrid();
            _candidateGrid.SelectionChanged += (_, _) => UpdateSelectedCandidate();

            var detailTabs = new TabControl { Dock = DockStyle.Fill };
            var detailsTab = new TabPage("Details and warnings");
            var streamsTab = new TabPage("Streams");

            var detailsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                ColumnCount = 1,
                RowCount = 2
            };
            detailsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            detailsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _recommendationLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(1060, 0),
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.DarkGreen,
                Margin = new Padding(0, 0, 0, 6)
            };
            _warningsText = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = SystemColors.Window
            };
            detailsLayout.Controls.Add(_recommendationLabel, 0, 0);
            detailsLayout.Controls.Add(_warningsText, 0, 1);
            detailsTab.Controls.Add(detailsLayout);

            var streamsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                ColumnCount = 2,
                RowCount = 2
            };
            streamsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            streamsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            streamsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            streamsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _videoStreamLabel = new Label
            {
                AutoSize = true,
                Text = "Video: —",
                Margin = new Padding(0, 0, 0, 6)
            };
            var videoHint = new Label
            {
                AutoSize = true,
                Text = "The primary video stream is required.",
                ForeColor = SystemColors.GrayText,
                Anchor = AnchorStyles.Right
            };
            _audioStreams = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true
            };
            _subtitleStreams = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true
            };
            var audioGroup = WrapInGroup("Audio streams", _audioStreams);
            var subtitleGroup = WrapInGroup("Subtitle streams", _subtitleStreams);
            streamsLayout.Controls.Add(_videoStreamLabel, 0, 0);
            streamsLayout.Controls.Add(videoHint, 1, 0);
            streamsLayout.Controls.Add(audioGroup, 0, 1);
            streamsLayout.Controls.Add(subtitleGroup, 1, 1);
            streamsTab.Controls.Add(streamsLayout);

            detailTabs.TabPages.Add(detailsTab);
            detailTabs.TabPages.Add(streamsTab);

            var modeGroup = new GroupBox
            {
                Text = "Processing mode",
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(10)
            };
            var modeLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 3
            };
            _losslessMode = new RadioButton
            {
                AutoSize = true,
                Checked = initialOutputMode != DvdOutputMode.EncodeUsingCurrentSettings,
                Font = new Font(Font, FontStyle.Bold),
                Text = "Lossless Remux to MKV — Recommended"
            };
            _encodeMode = new RadioButton
            {
                AutoSize = true,
                Checked = initialOutputMode == DvdOutputMode.EncodeUsingCurrentSettings,
                Text = "Encode Using Current MediaFlux Settings"
            };
            _modeDescription = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(1050, 0),
                ForeColor = SystemColors.GrayText,
                Text = "Combines the DVD segments into one MKV without reducing quality."
            };
            _losslessMode.CheckedChanged += (_, _) => UpdateModeState();
            _encodeMode.CheckedChanged += (_, _) => UpdateModeState();
            modeLayout.Controls.Add(_losslessMode, 0, 0);
            modeLayout.Controls.Add(_encodeMode, 0, 1);
            modeLayout.Controls.Add(_modeDescription, 0, 2);
            modeGroup.Controls.Add(modeLayout);

            _outputGroup = new GroupBox
            {
                Text = "Output file",
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(10)
            };
            var outputLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1
            };
            outputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _outputPathText = new TextBox { Dock = DockStyle.Fill };
            _outputPathText.TextChanged += (_, _) =>
            {
                if (!_updatingOutputPath)
                    _outputPathEdited = true;
                UpdateStartButtonState();
            };
            _browseButton = new Button
            {
                Text = "Browse…",
                AutoSize = true,
                Margin = new Padding(8, 0, 0, 0)
            };
            _browseButton.Click += BrowseOutput_Click;
            outputLayout.Controls.Add(_outputPathText, 0, 0);
            outputLayout.Controls.Add(_browseButton, 1, 0);
            _outputGroup.Controls.Add(outputLayout);

            var sourceSafetyLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(1100, 0),
                ForeColor = Color.DarkBlue,
                Text = "DVD source files will never be renamed, moved, deleted, or modified. " +
                       "MediaFlux will not apply Delete Source After Compression to this DVD folder.",
                Margin = new Padding(0, 6, 0, 6)
            };
            _validationLabel = new Label
            {
                AutoSize = true,
                ForeColor = Color.Firebrick,
                Margin = new Padding(0, 0, 0, 4)
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            var cancelButton = new Button
            {
                Text = "Cancel",
                Width = 100,
                DialogResult = DialogResult.Cancel
            };
            _startButton = new Button
            {
                Text = "Start Remux",
                Width = 140
            };
            _startButton.Click += StartButton_Click;
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(_startButton);

            root.Controls.Add(heading, 0, 0);
            root.Controls.Add(_candidateGrid, 0, 1);
            root.Controls.Add(detailTabs, 0, 2);
            root.Controls.Add(modeGroup, 0, 3);
            root.Controls.Add(_outputGroup, 0, 4);
            root.Controls.Add(sourceSafetyLabel, 0, 5);
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 2
            };
            footer.Controls.Add(_validationLabel, 0, 0);
            footer.Controls.Add(buttons, 0, 1);
            root.Controls.Add(footer, 0, 6);
            Controls.Add(root);

            AcceptButton = _startButton;
            CancelButton = cancelButton;
            PopulateCandidates();
            UpdateModeState();
        }

        public DvdImportOptions? Options { get; private set; }

        private DataGridView CreateCandidateGrid()
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                MultiSelect = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false
            };
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Recommendation",
                HeaderText = "Recommendation",
                Width = 130
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "TitleSet",
                HeaderText = "Title set",
                Width = 80
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Segments",
                HeaderText = "Segments",
                Width = 70
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Size",
                HeaderText = "Source size",
                Width = 100
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Duration",
                HeaderText = "Duration",
                Width = 90
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Resolution",
                HeaderText = "Resolution",
                Width = 90
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Codec",
                HeaderText = "Video codec",
                Width = 100
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Aspect",
                HeaderText = "Aspect",
                Width = 70
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Audio",
                HeaderText = "Audio",
                Width = 60
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Subtitles",
                HeaderText = "Subs",
                Width = 60
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Warnings",
                HeaderText = "Analysis",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 140
            });
            return grid;
        }

        private void PopulateCandidates()
        {
            int selectedRow = 0;
            for (int index = 0; index < _analysis.Candidates.Count; index++)
            {
                DvdTitleCandidate candidate = _analysis.Candidates[index];
                int rowIndex = _candidateGrid.Rows.Add(
                    candidate.IsLikelyMainFeature ? "Likely Main Feature" : "",
                    candidate.TitleSetId,
                    candidate.Segments.Count,
                    FormatSize(candidate.CombinedSizeBytes),
                    FormatDuration(candidate.CombinedDurationSeconds),
                    candidate.VideoWidth.HasValue && candidate.VideoHeight.HasValue
                        ? $"{candidate.VideoWidth}×{candidate.VideoHeight}"
                        : "Unknown",
                    string.IsNullOrWhiteSpace(candidate.VideoCodec) ? "Unknown" : candidate.VideoCodec,
                    string.IsNullOrWhiteSpace(candidate.DisplayAspectRatio)
                        ? "Unknown"
                        : candidate.DisplayAspectRatio,
                    candidate.AudioStreamCount,
                    candidate.SubtitleStreamCount,
                    candidate.Warnings.Count == 0 ? "Ready" : $"{candidate.Warnings.Count} warning(s)");
                DataGridViewRow row = _candidateGrid.Rows[rowIndex];
                row.Tag = candidate;
                if (candidate.IsLikelyMainFeature)
                {
                    selectedRow = rowIndex;
                    row.DefaultCellStyle.BackColor = Color.Honeydew;
                }
                if (!candidate.IsValidForConversion)
                    row.DefaultCellStyle.ForeColor = Color.Firebrick;
            }

            if (_candidateGrid.Rows.Count > 0)
            {
                _candidateGrid.ClearSelection();
                _candidateGrid.Rows[selectedRow].Selected = true;
                _candidateGrid.CurrentCell = _candidateGrid.Rows[selectedRow].Cells["TitleSet"];
            }
        }

        private void UpdateSelectedCandidate()
        {
            DvdTitleCandidate? candidate = SelectedCandidate;
            if (candidate == null)
                return;

            _recommendationLabel.Text = candidate.IsLikelyMainFeature
                ? candidate.RecommendationReason
                : $"{candidate.TitleSetId} is available for manual selection; the longest title is only a recommendation.";
            if (_analysis.HasAmbiguousMainFeature)
            {
                _recommendationLabel.Text += Environment.NewLine + _analysis.AmbiguityWarning;
                _recommendationLabel.ForeColor = Color.DarkOrange;
            }
            else
            {
                _recommendationLabel.ForeColor = candidate.IsLikelyMainFeature
                    ? Color.DarkGreen
                    : SystemColors.ControlText;
            }
            _ambiguityConfirmed = false;

            _warningsText.Text = candidate.Warnings.Count == 0
                ? "No analysis warnings."
                : string.Join(Environment.NewLine + Environment.NewLine, candidate.Warnings);
            PopulateStreams(candidate);

            if (!_outputPathEdited)
            {
                _updatingOutputPath = true;
                try
                {
                    _outputPathText.Text = OutputPathService.BuildDefaultDvdOutputPath(
                        _analysis,
                        candidate,
                        _initialOutputFolder,
                        _outputNamingPattern);
                }
                finally
                {
                    _updatingOutputPath = false;
                }
                UpdateOutputExtensionForMode();
            }

            UpdateStartButtonState();
        }

        private void PopulateStreams(DvdTitleCandidate candidate)
        {
            _audioStreams.Items.Clear();
            _subtitleStreams.Items.Clear();
            MediaProbeResult? representative = candidate.Segments
                .Select(segment => segment.ProbeResult)
                .FirstOrDefault(probe => probe?.Success == true);
            MediaProbeStreamInfo? video = representative?.Streams.FirstOrDefault(stream =>
                stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
            _videoStreamLabel.Text = video == null
                ? "Video: unavailable"
                : $"Video: stream {video.Index} — {video.CodecName}, " +
                  $"{video.Width}×{video.Height}, {video.DisplayAspectRatio}";

            foreach (MediaProbeStreamInfo stream in representative?.Streams
                         .Where(stream => stream.CodecType.Equals(
                             "audio",
                             StringComparison.OrdinalIgnoreCase)) ??
                     Enumerable.Empty<MediaProbeStreamInfo>())
            {
                _audioStreams.Items.Add(new StreamChoice(stream), isChecked: true);
            }

            foreach (MediaProbeStreamInfo stream in representative?.Streams
                         .Where(stream => stream.CodecType.Equals(
                             "subtitle",
                             StringComparison.OrdinalIgnoreCase)) ??
                     Enumerable.Empty<MediaProbeStreamInfo>())
            {
                _subtitleStreams.Items.Add(new StreamChoice(stream), isChecked: true);
            }
        }

        private void UpdateModeState()
        {
            if (_losslessMode.Checked)
            {
                _modeDescription.Text =
                    "Combines the DVD segments into one MKV without reducing quality. " +
                    "Video, selected audio, and selected subtitle streams are copied.";
                _startButton.Text = "Start Remux";
            }
            else
            {
                _modeDescription.Text =
                    "Re-encodes the selected DVD title using the current MediaFlux compression settings. " +
                    "This is normally a lossy conversion and will be added to the existing Encode queue.";
                _startButton.Text = "Add to Encode Queue";
            }

            _outputGroup.Text = _losslessMode.Checked
                ? "Output file"
                : "Encode output file";
            UpdateOutputExtensionForMode();
            UpdateStartButtonState();
        }

        private void UpdateStartButtonState()
        {
            DvdTitleCandidate? candidate = SelectedCandidate;
            bool validOutput = !string.IsNullOrWhiteSpace(_outputPathText.Text);
            _startButton.Enabled =
                candidate?.IsValidForConversion == true &&
                validOutput;

            _validationLabel.Text = candidate?.IsValidForConversion != true
                ? "This title contains analysis errors and cannot be processed safely."
                : _encodeMode.Checked
                    ? "The logical DVD title will be queued as one job. Source files will never be deleted."
                    : "";
        }

        private void BrowseOutput_Click(object? sender, EventArgs e)
        {
            DvdTitleCandidate? candidate = SelectedCandidate;
            if (candidate == null)
                return;

            string currentPath = _outputPathText.Text;
            using var dialog = new SaveFileDialog
            {
                Title = _losslessMode.Checked
                    ? "Choose DVD MKV Output"
                    : "Choose DVD Encode Output",
                Filter = _losslessMode.Checked
                    ? "Matroska video (*.mkv)|*.mkv"
                    : "MP4 video (*.mp4)|*.mp4",
                AddExtension = true,
                DefaultExt = _losslessMode.Checked ? "mkv" : "mp4",
                OverwritePrompt = _losslessMode.Checked,
                FileName = string.IsNullOrWhiteSpace(currentPath)
                    ? OutputPathService.BuildDefaultDvdBaseName(
                          _analysis,
                          candidate,
                          _outputNamingPattern) +
                      (_losslessMode.Checked ? ".mkv" : ".mp4")
                    : Path.GetFileName(currentPath),
                InitialDirectory = GetExistingDirectory(currentPath)
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            _outputPathEdited = true;
            _outputPathText.Text = dialog.FileName;
        }

        private void StartButton_Click(object? sender, EventArgs e)
        {
            DvdTitleCandidate? candidate = SelectedCandidate;
            if (candidate == null)
                return;

            if (_analysis.HasAmbiguousMainFeature && !_ambiguityConfirmed)
            {
                DialogResult review = MessageBox.Show(
                    this,
                    $"{_analysis.AmbiguityWarning}\r\n\r\n" +
                    $"Continue with {candidate.TitleSetId}?",
                    "Confirm DVD Title",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (review != DialogResult.Yes)
                    return;
                _ambiguityConfirmed = true;
            }

            string outputPath;
            try
            {
                outputPath = _losslessMode.Checked
                    ? OutputPathService.EnsureMkvExtension(_outputPathText.Text)
                    : OutputPathService.EnsureMp4Extension(_outputPathText.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"The output path is invalid:\r\n\r\n{ex.Message}",
                    "DVD Output",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            string sourceFolder = Path.GetDirectoryName(candidate.Segments[0].Path) ?? "";
            if (OutputPathService.IsPathWithinDirectory(outputPath, sourceFolder))
            {
                MessageBox.Show(
                    this,
                    "The output cannot be written inside the source VIDEO_TS folder. " +
                    "Choose its parent folder or another destination.",
                    "DVD Output",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            bool overwrite = _losslessMode.Checked && File.Exists(outputPath);
            if (overwrite)
            {
                DialogResult confirmation = MessageBox.Show(
                    this,
                    "The selected output file already exists. MediaFlux will preserve it until " +
                    "the new MKV has passed validation, then replace it.\r\n\r\nContinue?",
                    "Replace Existing Output?",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirmation != DialogResult.Yes)
                    return;
            }

            Options = new DvdImportOptions
            {
                Candidate = candidate,
                OutputMode = _losslessMode.Checked
                    ? DvdOutputMode.LosslessRemuxToMkv
                    : DvdOutputMode.EncodeUsingCurrentSettings,
                OutputPath = outputPath,
                SelectedAudioStreamIndexes = GetCheckedStreamIndexes(_audioStreams),
                SelectedSubtitleStreamIndexes = GetCheckedStreamIndexes(_subtitleStreams),
                OverwriteExistingOutput = overwrite
            };
            DialogResult = DialogResult.OK;
            Close();
        }

        private void UpdateOutputExtensionForMode()
        {
            if (string.IsNullOrWhiteSpace(_outputPathText.Text))
                return;

            string desiredExtension = _losslessMode.Checked ? ".mkv" : ".mp4";
            try
            {
                _updatingOutputPath = true;
                _outputPathText.Text = Path.ChangeExtension(
                    _outputPathText.Text,
                    desiredExtension);
            }
            finally
            {
                _updatingOutputPath = false;
            }
        }

        private DvdTitleCandidate? SelectedCandidate =>
            _candidateGrid.SelectedRows.Count == 0
                ? null
                : _candidateGrid.SelectedRows[0].Tag as DvdTitleCandidate;

        private static IReadOnlyList<int> GetCheckedStreamIndexes(CheckedListBox list)
        {
            return list.CheckedItems
                .OfType<StreamChoice>()
                .Select(choice => choice.Stream.Index)
                .ToArray();
        }

        private static GroupBox WrapInGroup(string title, Control content)
        {
            var group = new GroupBox
            {
                Text = title,
                Dock = DockStyle.Fill,
                Padding = new Padding(8)
            };
            group.Controls.Add(content);
            return group;
        }

        private static string GetExistingDirectory(string path)
        {
            try
            {
                string? folder = Path.GetDirectoryName(path);
                return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
                    ? folder
                    : "";
            }
            catch
            {
                return "";
            }
        }

        private static string FormatSize(long bytes)
        {
            double value = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.##} {units[unit]}";
        }

        private static string FormatDuration(double seconds)
        {
            return seconds > 0
                ? TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss")
                : "Unknown";
        }

        private sealed class StreamChoice
        {
            public StreamChoice(MediaProbeStreamInfo stream)
            {
                Stream = stream;
            }

            public MediaProbeStreamInfo Stream { get; }

            public override string ToString()
            {
                string language = string.IsNullOrWhiteSpace(Stream.Language)
                    ? "language unknown"
                    : Stream.Language;
                string channels = Stream.Channels.HasValue
                    ? $", {Stream.Channels} channels"
                    : "";
                return $"Stream {Stream.Index}: {Stream.CodecName}, {language}{channels}";
            }
        }
    }
}
