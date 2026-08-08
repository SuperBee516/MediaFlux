using System.Globalization;
using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux
{
    public sealed class LibraryAnalyzerForm : MediaFluxForm
    {
        private const int PageSize = 200;
        private readonly LibraryAnalyzerRuntime _runtime;
        private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
        private readonly DataGridView _locationsGrid = CreateGrid();
        private readonly DataGridView _filesGrid = CreateGrid();
        private readonly Label _overviewFiles = ValueLabel();
        private readonly Label _overviewSize = ValueLabel();
        private readonly Label _overviewActivity = ValueLabel();
        private readonly Label _overviewPending = ValueLabel();
        private readonly Label _overviewUnavailable = ValueLabel();
        private readonly Label _overviewLastScan = ValueLabel();
        private readonly Label _scanStatus = new() { AutoSize = true, Text = "Ready", Padding = new Padding(6) };
        private readonly ProgressBar _scanProgress = new() { Width = 220, Style = ProgressBarStyle.Marquee, Visible = false };
        private readonly TextBox _search = new() { Width = 260, PlaceholderText = "Filename or path" };
        private readonly ComboBox _fileLocation = DropDown();
        private readonly ComboBox _availability = DropDown();
        private readonly ComboBox _probeStatus = DropDown();
        private readonly ComboBox _sort = DropDown();
        private readonly CheckBox _descending = new() { Text = "Descending", AutoSize = true };
        private readonly Label _pageLabel = new() { AutoSize = true, Padding = new Padding(8, 7, 8, 0) };
        private readonly Button _previous = new() { Text = "Previous", AutoSize = true };
        private readonly Button _next = new() { Text = "Next", AutoSize = true };
        private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 2_000 };
        private int _page;
        private long _totalFiles;
        private bool _loadingFiles;
        private bool _scanning;

        public LibraryAnalyzerForm(LibraryAnalyzerRuntime runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            Text = "Library Analyzer";
            MinimumSize = new Size(980, 620);
            Size = new Size(1280, 780);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);

            Controls.Add(_tabs);
            BuildOverviewTab();
            BuildLocationsTab();
            BuildFilesTab();
            _runtime.Enrichment.ProgressChanged += Enrichment_ProgressChanged;
            _refreshTimer.Tick += async (_, _) => await RefreshCurrentStateAsync();
            _refreshTimer.Start();
            Shown += async (_, _) => await RefreshAllAsync();
            FormClosed += (_, _) =>
            {
                _refreshTimer.Stop();
                _runtime.Enrichment.ProgressChanged -= Enrichment_ProgressChanged;
            };
        }

        private void BuildOverviewTab()
        {
            var tab = new TabPage("Overview") { Padding = new Padding(18) };
            var content = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true
            };
            var title = new Label
            {
                Text = "Media library catalog",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 12)
            };
            var table = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 6,
                Dock = DockStyle.Top,
                Padding = new Padding(0, 14, 0, 0)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            AddOverviewRow(table, 0, "Indexed video files", _overviewFiles);
            AddOverviewRow(table, 1, "Indexed logical size", _overviewSize);
            AddOverviewRow(table, 2, "Scanner / enrichment", _overviewActivity);
            AddOverviewRow(table, 3, "Pending enrichment", _overviewPending);
            AddOverviewRow(table, 4, "Unavailable locations", _overviewUnavailable);
            AddOverviewRow(table, 5, "Last completed scan", _overviewLastScan);
            var refresh = new Button { Text = "Refresh", AutoSize = true, Margin = new Padding(0, 22, 0, 0) };
            refresh.Click += async (_, _) => await RefreshAllAsync();
            content.Controls.Add(title);
            content.Controls.Add(table);
            content.Controls.Add(refresh);
            tab.Controls.Add(content);
            _tabs.TabPages.Add(tab);
        }

        private void BuildLocationsTab()
        {
            var tab = new TabPage("Locations") { Padding = new Padding(10) };
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                AutoSize = false,
                WrapContents = false
            };
            Button add = AddButton(buttons, "Add folder / drive…", AddLocation_Click);
            Button remove = AddButton(buttons, "Remove", RemoveLocation_Click);
            Button toggle = AddButton(buttons, "Enable / Disable", ToggleLocation_Click);
            Button scan = AddButton(buttons, "Scan selected", ScanSelected_Click);
            Button pause = AddButton(buttons, "Pause", (_, _) => { _runtime.Scanner.Pause(); _scanStatus.Text = "Paused"; });
            Button resume = AddButton(buttons, "Resume", (_, _) => { _runtime.Scanner.Resume(); _scanStatus.Text = "Resuming…"; });
            Button cancel = AddButton(buttons, "Cancel", (_, _) => _runtime.Scanner.Cancel());
            _ = add; _ = remove; _ = toggle; _ = scan; _ = pause; _ = resume; _ = cancel;

            var statusPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 38,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            statusPanel.Controls.Add(_scanProgress);
            statusPanel.Controls.Add(_scanStatus);

            _locationsGrid.MultiSelect = true;
            _locationsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _locationsGrid.Columns.Add("Id", "Id");
            _locationsGrid.Columns[0].Visible = false;
            _locationsGrid.Columns.Add("Path", "Folder / drive");
            _locationsGrid.Columns.Add("Enabled", "Enabled");
            _locationsGrid.Columns.Add("Availability", "Availability");
            _locationsGrid.Columns.Add("Files", "Indexed files");
            _locationsGrid.Columns.Add("LastScan", "Last completed scan");
            _locationsGrid.Columns.Add("Error", "Status / error");
            _locationsGrid.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _locationsGrid.Columns[6].Width = 260;

            tab.Controls.Add(_locationsGrid);
            tab.Controls.Add(statusPanel);
            tab.Controls.Add(buttons);
            _tabs.TabPages.Add(tab);
        }

        private void BuildFilesTab()
        {
            var tab = new TabPage("Files") { Padding = new Padding(10) };
            var filters = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 72,
                AutoScroll = true,
                WrapContents = true
            };
            filters.Controls.AddRange(new Control[]
            {
                Labeled("Search", _search),
                Labeled("Location", _fileLocation),
                Labeled("Availability", _availability),
                Labeled("Probe", _probeStatus),
                Labeled("Sort", _sort),
                _descending
            });
            var apply = new Button { Text = "Apply", AutoSize = true, Margin = new Padding(8, 19, 3, 3) };
            apply.Click += async (_, _) => { _page = 0; await RefreshFilesAsync(); };
            filters.Controls.Add(apply);
            _search.KeyDown += async (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    _page = 0;
                    await RefreshFilesAsync();
                }
            };

            _availability.Items.AddRange(new object[] { "All", "Present", "Missing", "Unavailable" });
            _availability.SelectedIndex = 0;
            _probeStatus.Items.AddRange(new object[] { "All", "Pending", "In progress", "Succeeded", "Failed" });
            _probeStatus.SelectedIndex = 0;
            _sort.Items.AddRange(new object[] { "Path", "Name", "Size", "Modified", "Codec", "Duration", "Bitrate" });
            _sort.SelectedIndex = 0;

            AddFileColumn("Name", "Filename", 180);
            AddFileColumn("Path", "Path", 360);
            AddFileColumn("Root", "Root", 200);
            AddFileColumn("Size", "Size", 90);
            AddFileColumn("Modified", "Modified", 140);
            AddFileColumn("Availability", "Availability", 90);
            AddFileColumn("Container", "Container", 100);
            AddFileColumn("Codec", "Video codec", 90);
            AddFileColumn("Resolution", "Resolution", 90);
            AddFileColumn("Bitrate", "Bitrate", 90);
            AddFileColumn("Duration", "Duration", 90);
            AddFileColumn("Probe", "Probe status", 110);

            var pager = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            _next.Click += async (_, _) => { _page++; await RefreshFilesAsync(); };
            _previous.Click += async (_, _) => { _page = Math.Max(0, _page - 1); await RefreshFilesAsync(); };
            pager.Controls.Add(_next);
            pager.Controls.Add(_previous);
            pager.Controls.Add(_pageLabel);

            tab.Controls.Add(_filesGrid);
            tab.Controls.Add(pager);
            tab.Controls.Add(filters);
            _tabs.TabPages.Add(tab);
        }

        private async void AddLocation_Click(object? sender, EventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select a media-library folder or drive",
                ShowNewFolderButton = false,
                UseDescriptionForTitle = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            try
            {
                _runtime.Catalog.UpsertLocation(new LibraryLocationUpsert(dialog.SelectedPath));
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                ShowError("The location could not be added.", ex);
            }
        }

        private async void RemoveLocation_Click(object? sender, EventArgs e)
        {
            IReadOnlyList<long> ids = SelectedLocationIds();
            if (ids.Count == 0)
                return;
            DialogResult answer = MessageBox.Show(
                this,
                "Remove the selected location configuration and catalog records that belong only to those locations?\r\n\r\n" +
                "The actual media files will not be changed or deleted.",
                "Remove library location",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.OK)
                return;
            try
            {
                foreach (long id in ids)
                    _runtime.Catalog.RemoveLocation(id, removeOrphanedFiles: true);
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                ShowError("The location could not be removed.", ex);
            }
        }

        private async void ToggleLocation_Click(object? sender, EventArgs e)
        {
            foreach (long id in SelectedLocationIds())
            {
                LibraryLocationRecord? location = _runtime.Catalog.GetLocation(id);
                if (location != null)
                {
                    _runtime.Catalog.UpsertLocation(new LibraryLocationUpsert(
                        location.Path,
                        location.IncludeSubfolders,
                        !location.IsEnabled,
                        location.Availability,
                        location.LastError));
                }
            }
            await RefreshAllAsync();
        }

        private async void ScanSelected_Click(object? sender, EventArgs e)
        {
            if (_scanning)
                return;
            IReadOnlyList<long> ids = SelectedLocationIds();
            if (ids.Count == 0)
                ids = _runtime.Catalog.GetLocations(includeDisabled: false).Select(item => item.Id).ToArray();
            if (ids.Count == 0)
                return;

            _scanning = true;
            _scanProgress.Visible = true;
            try
            {
                var progress = new Progress<LibraryScanProgress>(value =>
                {
                    _scanStatus.Text = $"{value.Stage}: {value.DiscoveredFiles:N0} found, " +
                                       $"{value.NewFiles:N0} new, {value.ChangedFiles:N0} changed, " +
                                       $"{value.ErrorCount:N0} errors";
                });
                foreach (long id in ids)
                {
                    LibraryScanResult result = await _runtime.Scanner.ScanLocationAsync(
                        id,
                        LibraryEnrichmentCoordinator.CurrentMetadataVersion,
                        progress);
                    _scanStatus.Text = result.Outcome == LibraryScanOutcome.Completed
                        ? $"Completed: {result.DiscoveredFiles:N0} files, {result.MissingFiles:N0} missing"
                        : $"{result.Outcome}: {result.ErrorMessage}";
                    await RefreshAllAsync();
                    if (result.Outcome == LibraryScanOutcome.Canceled)
                        break;
                }
            }
            finally
            {
                _scanning = false;
                _scanProgress.Visible = false;
            }
        }

        private async Task RefreshAllAsync()
        {
            await RefreshOverviewAsync();
            await RefreshLocationsAsync();
            await RefreshFilesAsync();
        }

        private async Task RefreshCurrentStateAsync()
        {
            await RefreshOverviewAsync();
            if (_tabs.SelectedIndex == 1)
                await RefreshLocationsAsync();
        }

        private async Task RefreshOverviewAsync()
        {
            LibraryOverview overview = await Task.Run(() =>
                _runtime.Catalog.GetOverview(LibraryEnrichmentCoordinator.CurrentMetadataVersion));
            if (IsDisposed)
                return;
            _overviewFiles.Text = overview.IndexedFiles.ToString("N0");
            _overviewSize.Text = FormatBytes(overview.LogicalSizeBytes);
            _overviewActivity.Text = _scanning || overview.ActiveScans > 0
                ? "Scanning"
                : _runtime.Enrichment.IsRunning
                    ? $"Enriching ({_runtime.Enrichment.QueuedCount:N0} queued)"
                    : "Idle";
            _overviewPending.Text = overview.PendingEnrichment.ToString("N0");
            _overviewUnavailable.Text = overview.UnavailableLocations.ToString("N0");
            _overviewLastScan.Text = overview.LastCompletedScanUtc?.ToLocalTime().ToString("g") ?? "Never";
        }

        private async Task RefreshLocationsAsync()
        {
            IReadOnlyList<LibraryLocationRecord> locations = await Task.Run(() => _runtime.Catalog.GetLocations());
            var rows = new List<object[]>();
            foreach (LibraryLocationRecord location in locations)
            {
                long count = await Task.Run(() => _runtime.Catalog.QueryFiles(new LibraryFileQuery(
                    LocationId: location.Id,
                    Limit: 1)).TotalCount);
                rows.Add(new object[]
                {
                    location.Id,
                    location.Path,
                    location.IsEnabled ? "Yes" : "No",
                    location.Availability,
                    count.ToString("N0"),
                    location.LastCompletedScanUtc?.ToLocalTime().ToString("g") ?? "Never",
                    location.LastError
                });
            }
            if (IsDisposed)
                return;
            _locationsGrid.Rows.Clear();
            foreach (object[] row in rows)
                _locationsGrid.Rows.Add(row);
            RefreshLocationFilter(locations);
        }

        private async Task RefreshFilesAsync()
        {
            if (_loadingFiles || IsDisposed)
                return;
            _loadingFiles = true;
            try
            {
                LibraryFileQuery query = BuildFileQuery();
                LibraryFilePage result = await Task.Run(() => _runtime.Catalog.QueryFiles(query));
                if (IsDisposed)
                    return;
                _totalFiles = result.TotalCount;
                _filesGrid.Rows.Clear();
                foreach (LibraryFileViewRecord file in result.Files)
                {
                    _filesGrid.Rows.Add(
                        file.FileName,
                        file.FullPath,
                        file.LocationPath,
                        FormatBytes(file.SizeBytes),
                        file.LastWriteUtc.ToLocalTime().ToString("g"),
                        file.Availability,
                        file.FormatName,
                        file.VideoCodec,
                        file.Width.HasValue && file.Height.HasValue ? $"{file.Width}×{file.Height}" : "",
                        file.TotalBitRate.HasValue ? $"{file.TotalBitRate.Value / 1_000_000d:0.##} Mbps" : "",
                        file.DurationSeconds.HasValue ? FormatDuration(file.DurationSeconds.Value) : "",
                        file.ProbeStatus == LibraryProbeStatus.Failed && !string.IsNullOrWhiteSpace(file.ProbeError)
                            ? $"Failed: {file.ProbeError}"
                            : file.ProbeStatus.ToString());
                }
                long first = _totalFiles == 0 ? 0 : (long)_page * PageSize + 1;
                long last = Math.Min(_totalFiles, ((long)_page + 1) * PageSize);
                _pageLabel.Text = $"{first:N0}–{last:N0} of {_totalFiles:N0}";
                _previous.Enabled = _page > 0;
                _next.Enabled = last < _totalFiles;
            }
            finally
            {
                _loadingFiles = false;
            }
        }

        private LibraryFileQuery BuildFileQuery()
        {
            long? locationId = _fileLocation.SelectedItem is LocationChoice choice && choice.Id > 0 ? choice.Id : null;
            IndexedFileAvailability? availability = _availability.SelectedIndex switch
            {
                1 => IndexedFileAvailability.Present,
                2 => IndexedFileAvailability.Missing,
                3 => IndexedFileAvailability.Unavailable,
                _ => null
            };
            LibraryProbeStatus? probeStatus = _probeStatus.SelectedIndex switch
            {
                1 => LibraryProbeStatus.Pending,
                2 => LibraryProbeStatus.InProgress,
                3 => LibraryProbeStatus.Succeeded,
                4 => LibraryProbeStatus.Failed,
                _ => null
            };
            return new LibraryFileQuery(
                _search.Text,
                locationId,
                availability,
                probeStatus,
                _sort.Text.ToLowerInvariant(),
                _descending.Checked,
                _page * PageSize,
                PageSize);
        }

        private void RefreshLocationFilter(IReadOnlyList<LibraryLocationRecord> locations)
        {
            long selected = _fileLocation.SelectedItem is LocationChoice choice ? choice.Id : 0;
            _fileLocation.Items.Clear();
            _fileLocation.Items.Add(new LocationChoice(0, "All locations"));
            foreach (LibraryLocationRecord location in locations)
                _fileLocation.Items.Add(new LocationChoice(location.Id, location.Path));
            _fileLocation.SelectedItem = _fileLocation.Items.Cast<LocationChoice>().FirstOrDefault(item => item.Id == selected)
                                         ?? _fileLocation.Items[0];
        }

        private IReadOnlyList<long> SelectedLocationIds() => _locationsGrid.SelectedRows
            .Cast<DataGridViewRow>()
            .Where(row => row.Cells[0].Value != null)
            .Select(row => Convert.ToInt64(row.Cells[0].Value, CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();

        private void Enrichment_ProgressChanged(object? sender, LibraryEnrichmentProgress e)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            BeginInvoke(async () => await RefreshOverviewAsync());
        }

        private static DataGridView CreateGrid() => new()
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoGenerateColumns = false
        };

        private static ComboBox DropDown() => new()
        {
            Width = 160,
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        private static Label ValueLabel() => new()
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10F),
            Padding = new Padding(4)
        };

        private static void AddOverviewRow(TableLayoutPanel table, int row, string name, Label value)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = name, AutoSize = true, Padding = new Padding(4, 6, 4, 4) }, 0, row);
            table.Controls.Add(value, 1, row);
        }

        private static Button AddButton(Control parent, string text, EventHandler handler)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += handler;
            parent.Controls.Add(button);
            return button;
        }

        private static Control Labeled(string text, Control control)
        {
            var panel = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(3, 0, 8, 0)
            };
            panel.Controls.Add(new Label { Text = text, AutoSize = true });
            panel.Controls.Add(control);
            return panel;
        }

        private void AddFileColumn(string name, string header, int width) =>
            _filesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width });

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double value = Math.Max(0, bytes);
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return $"{value:0.##} {units[unit]}";
        }

        private static string FormatDuration(double seconds) =>
            TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 86_400 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss");

        private void ShowError(string message, Exception exception) => MessageBox.Show(
            this,
            message + "\r\n\r\n" + exception.Message,
            "Library Analyzer",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);

        private sealed record LocationChoice(long Id, string Name)
        {
            public override string ToString() => Name;
        }
    }
}
