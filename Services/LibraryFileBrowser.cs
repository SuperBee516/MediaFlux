using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux.Services;

/// <summary>
/// Reusable, catalog-backed file results view. Queries one bounded page at a time and
/// preserves the selected file identities when the current page is refreshed.
/// </summary>
public sealed class LibraryFileBrowser : UserControl
{
    public const int DefaultPageSize = 200;

    private readonly Func<LibraryFileQuery, LibraryFilePage> _query;
    private readonly Label _heading = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        Font = new Font("Segoe UI Semibold", 9F),
        Padding = new Padding(4, 7, 4, 0)
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(4, 0, 4, 0)
    };
    private readonly Button _refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly Button _previous = new() { Text = "Previous", AutoSize = true };
    private readonly Button _next = new() { Text = "Next", AutoSize = true };
    private LibraryStatisticDrillDown? _drillDown;
    private int _page;
    private string _sortColumn = "path";
    private bool _descending;
    private int _refreshGeneration;
    private bool _loading;

    public LibraryFileBrowser(Func<LibraryFileQuery, LibraryFilePage> query)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        Dock = DockStyle.Fill;
        _heading.Text = "Statistics file drill-down";
        _status.Text = "Double-click a statistics category or choose View Files.";

        Grid = new DataGridView
        {
            Name = "StatisticsDrillDownFilesGrid",
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeColumns = true,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            AutoGenerateColumns = false
        };
        AddColumn("Name", "File Name", 190);
        AddColumn("Path", "Path / Location", 390);
        AddColumn("Size", "Size", 90);
        AddColumn("Codec", "Codec", 90);
        AddColumn("Resolution", "Resolution", 90);
        AddColumn("Container", "Container", 95);
        AddColumn("Bitrate", "Bitrate", 90, visible: false);
        AddColumn("DynamicRange", "HDR / SDR", 85);
        AddColumn("Duration", "Duration", 90);
        AddColumn("Created", "Created", 135, visible: false);
        AddColumn("Modified", "Modified", 135);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            ColumnCount = 2,
            Padding = new Padding(0, 0, 0, 4)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(_heading, 0, 0);
        header.Controls.Add(_refresh, 1, 0);

        var pager = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            ColumnCount = 3,
            Padding = new Padding(0, 4, 0, 0)
        };
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pager.Controls.Add(_status, 0, 0);
        pager.Controls.Add(_previous, 1, 0);
        pager.Controls.Add(_next, 2, 0);

        Controls.Add(Grid);
        Controls.Add(pager);
        Controls.Add(header);

        _refresh.Click += async (_, _) => await RefreshAsync();
        _previous.Click += async (_, _) =>
        {
            if (_page == 0) return;
            _page--;
            await RefreshAsync(preserveSelection: false);
        };
        _next.Click += async (_, _) =>
        {
            _page++;
            await RefreshAsync(preserveSelection: false);
        };
        Grid.ColumnHeaderMouseClick += async (_, e) =>
        {
            string? sort = SortName(Grid.Columns[e.ColumnIndex].Name);
            if (sort == null) return;
            if (_sortColumn.Equals(sort, StringComparison.OrdinalIgnoreCase))
                _descending = !_descending;
            else
            {
                _sortColumn = sort;
                _descending = sort is "size" or "modified" or "created";
            }
            _page = 0;
            await RefreshAsync(preserveSelection: false);
        };
    }

    public DataGridView Grid { get; }

    public LibraryStatisticDrillDown? DrillDown => _drillDown;

    public LibraryFileViewRecord[] SelectedFiles() =>
        LibraryAnalyzerGridInteraction.SelectedItems<LibraryFileViewRecord>(Grid);

    public async Task OpenAsync(LibraryStatisticDrillDown drillDown, string heading)
    {
        ArgumentNullException.ThrowIfNull(drillDown);
        bool changed = _drillDown != drillDown;
        _drillDown = drillDown;
        _heading.Text = heading;
        if (changed)
        {
            _page = 0;
            Grid.ClearSelection();
        }
        await RefreshAsync(preserveSelection: !changed);
    }

    public async Task RefreshAsync(bool preserveSelection = true)
    {
        if (_drillDown == null || IsDisposed) return;
        int generation = ++_refreshGeneration;
        long[] selectedIds = preserveSelection
            ? SelectedFiles().Select(file => file.FileId).ToArray()
            : Array.Empty<long>();
        _loading = true;
        UpdateNavigation(0, 0, "Loading files…");
        try
        {
            var query = new LibraryFileQuery(
                SortColumn: _sortColumn,
                Descending: _descending,
                Offset: checked(_page * DefaultPageSize),
                Limit: DefaultPageSize,
                Statistic: _drillDown);
            LibraryFilePage result = await Task.Run(() => _query(query));
            if (generation != _refreshGeneration || IsDisposed) return;
            if (_page > 0 && result.TotalCount <= (long)_page * DefaultPageSize)
            {
                _page = Math.Max(0, (int)((result.TotalCount - 1) / DefaultPageSize));
                await RefreshAsync(preserveSelection);
                return;
            }

            Grid.SuspendLayout();
            try
            {
                Grid.Rows.Clear();
                foreach (LibraryFileViewRecord file in result.Files)
                {
                    int row = Grid.Rows.Add(
                        file.FileName,
                        file.FullPath,
                        FormatBytes(file.SizeBytes),
                        file.VideoCodec,
                        file.Width.HasValue && file.Height.HasValue ? $"{file.Width}×{file.Height}" : "",
                        file.FormatName,
                        file.TotalBitRate.HasValue ? $"{file.TotalBitRate.Value / 1_000_000d:0.##} Mbps" : "",
                        file.DynamicRange,
                        file.DurationSeconds.HasValue ? FormatDuration(file.DurationSeconds.Value) : "",
                        file.CreationUtc?.ToLocalTime().ToString("g") ?? "",
                        file.LastWriteUtc.ToLocalTime().ToString("g"));
                    Grid.Rows[row].Tag = file;
                }
                Grid.ClearSelection();
                if (selectedIds.Length > 0)
                {
                    var selected = selectedIds.ToHashSet();
                    foreach (DataGridViewRow row in Grid.Rows)
                        row.Selected = row.Tag is LibraryFileViewRecord file && selected.Contains(file.FileId);
                }
            }
            finally { Grid.ResumeLayout(); }

            long first = result.TotalCount == 0 ? 0 : (long)_page * DefaultPageSize + 1;
            long last = Math.Min(result.TotalCount, ((long)_page + 1) * DefaultPageSize);
            string status = result.TotalCount == 0
                ? "No files match this category."
                : $"{first:N0}–{last:N0} of {result.TotalCount:N0}";
            _loading = false;
            UpdateNavigation(result.TotalCount, last, status);
        }
        catch (Exception ex)
        {
            if (generation == _refreshGeneration && !IsDisposed)
            {
                Grid.Rows.Clear();
                _loading = false;
                UpdateNavigation(0, 0, "Files could not be loaded: " + ex.Message);
            }
        }
        finally
        {
            if (generation == _refreshGeneration)
            {
                _loading = false;
                _refresh.Enabled = true;
            }
        }
    }

    private void UpdateNavigation(long total, long last, string status)
    {
        _status.Text = status;
        _previous.Enabled = !_loading && _page > 0;
        _next.Enabled = !_loading && last < total;
        _refresh.Enabled = !_loading;
    }

    private void AddColumn(string name, string header, int width, bool visible = true) =>
        Grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            Visible = visible,
            SortMode = DataGridViewColumnSortMode.Programmatic
        });

    private static string? SortName(string column) => column switch
    {
        "Name" => "name",
        "Path" => "path",
        "Size" => "size",
        "Codec" => "codec",
        "Resolution" => "resolution",
        "Container" => "container",
        "Bitrate" => "bitrate",
        "DynamicRange" => "dynamicrange",
        "Duration" => "duration",
        "Created" => "created",
        "Modified" => "modified",
        _ => null
    };

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
}
