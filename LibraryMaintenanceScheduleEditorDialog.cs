using MediaFlux.Services.LibraryCatalog;
using System.Globalization;

namespace MediaFlux;

internal sealed class LibraryMaintenanceScheduleEditorDialog : MediaFluxForm
{
    private const LibraryMaintenanceActions QuickScrubActions = LibraryMaintenanceActions.QuickScrubNew |
        LibraryMaintenanceActions.QuickScrubNeverChecked | LibraryMaintenanceActions.QuickScrubStale |
        LibraryMaintenanceActions.QuickScrubFailed;

    private static readonly (LibraryMaintenanceCadence Value, string Label)[] CadenceChoices =
    {
        (LibraryMaintenanceCadence.ManualOnly, "Manual only"),
        (LibraryMaintenanceCadence.Daily, "Daily"),
        (LibraryMaintenanceCadence.Weekly, "Weekly"),
        (LibraryMaintenanceCadence.OnStartup, "When MediaFlux starts")
    };

    private static readonly (LibraryMaintenanceMissedRun Value, string Label)[] MissedRunChoices =
    {
        (LibraryMaintenanceMissedRun.RunAtNextWindow, "Run later during the available maintenance window"),
        (LibraryMaintenanceMissedRun.RunOnNextStartup, "Run when MediaFlux next starts"),
        (LibraryMaintenanceMissedRun.Skip, "Skip the missed run")
    };

    private readonly LibraryMaintenanceProfile _original;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeZoneInfo _timeZone;
    private LibraryMaintenanceActions _quickScrubActions;
    private int _periodicDays;
    private bool _runAutomaticallyBeforeManual;
    private bool _wasManualCadence;

    internal readonly Label LocationLabel = new() { Name = "maintenanceLocation", AutoSize = true, MaximumSize = new Size(720, 0) };
    internal readonly CheckBox RunAutomatically = new() { Name = "runAutomatically", Text = "Run automatically", AutoSize = true };
    internal readonly ComboBox Frequency = FriendlyCombo("frequency");
    internal readonly CheckedListBox Weekdays = new() { Name = "weekdays", CheckOnClick = true, MultiColumn = true, ColumnWidth = 92, Height = 48, Width = 680 };
    internal readonly Panel WeekdayPanel = new() { Name = "weekdayPanel", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    internal readonly Panel WindowPanel = new() { Name = "windowPanel", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    internal readonly Label ManualOnlyNote = new() { Name = "manualOnlyNote", AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = SystemColors.GrayText, Text = "Manual only does not schedule automatic runs. You can still start maintenance with Run Now." };
    internal readonly DateTimePicker WindowStart = TimePicker("windowStart");
    internal readonly DateTimePicker WindowEnd = TimePicker("windowEnd");
    internal readonly ComboBox EncodingConflict = FriendlyCombo("encodingConflict");
    internal readonly Label EncodingConflictHelper = new() { Name = "encodingConflictHelper", AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = SystemColors.GrayText };
    internal readonly ComboBox MissedRun = FriendlyCombo("missedRun");
    internal readonly Label MissedRunHelper = new() { Name = "missedRunHelper", AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = SystemColors.GrayText };
    internal readonly Label StartupWindowNote = new() { Name = "startupWindowNote", AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = SystemColors.GrayText, Text = "Startup maintenance is not restricted by the normal maintenance window." };
    internal readonly RadioButton IncrementalScope = new() { Name = "incrementalScope", Text = "Incremental — Recommended", AutoSize = true };
    internal readonly RadioButton FullScope = new() { Name = "fullScope", Text = "Full reanalysis", AutoSize = true };
    internal readonly CheckBox RefreshCatalog = TaskCheck("refreshCatalog", "Refresh library catalog", LibraryMaintenanceActions.IncrementalScan);
    internal readonly CheckBox RefreshMetadata = TaskCheck("refreshMetadata", "Refresh metadata", LibraryMaintenanceActions.Metadata);
    internal readonly CheckBox ExactDuplicates = TaskCheck("exactDuplicates", "Find exact duplicates", LibraryMaintenanceActions.ExactDuplicates);
    internal readonly CheckBox VisualDuplicates = TaskCheck("visualDuplicates", "Find visually similar files", LibraryMaintenanceActions.VisualDuplicates);
    internal readonly CheckBox DuplicateFamilies = new() { Name = "duplicateFamilies", Text = "Build duplicate families", AutoSize = true };
    internal readonly CheckBox QuickScrubEnabled = new() { Name = "quickScrubEnabled", Text = "Quick Scrub file integrity", AutoSize = true };
    internal readonly Label QuickScrubSummary = new() { Name = "quickScrubSummary", AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = SystemColors.GrayText };
    internal readonly Button QuickScrubAdvancedButton = new() { Name = "quickScrubAdvanced", Text = "Advanced settings…", AutoSize = true };
    internal readonly Panel QuickScrubAdvanced = new() { Name = "quickScrubAdvancedPanel", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false, Padding = new Padding(8), BorderStyle = BorderStyle.FixedSingle };
    internal readonly CheckBox QuickScrubNew = new() { Name = "quickScrubNew", Text = "Check new files", AutoSize = true };
    internal readonly CheckBox QuickScrubNeverChecked = new() { Name = "quickScrubNeverChecked", Text = "Check files never verified", AutoSize = true };
    internal readonly CheckBox QuickScrubStale = new() { Name = "quickScrubStale", Text = "Check files with stale results", AutoSize = true };
    internal readonly CheckBox QuickScrubFailed = new() { Name = "quickScrubFailed", Text = "Retry failed or interrupted checks", AutoSize = true };
    internal readonly ComboBox PeriodicHealthyRecheck = FriendlyCombo("periodicHealthyRecheck");
    internal readonly Label PeriodicHealthyLabel = new() { Text = "Recheck healthy files after", AutoSize = true };
    internal readonly Label ValidationMessage = new() { Name = "validationMessage", AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(720, 0) };
    internal readonly Label ScheduleSummary = new() { Name = "scheduleSummary", Dock = DockStyle.Fill, AutoEllipsis = true, Padding = new Padding(8), UseMnemonic = false };

    internal LibraryMaintenanceScheduleEditorDialog(
        LibraryMaintenanceProfile profile,
        string locationPath,
        Func<DateTime>? utcNow = null,
        TimeZoneInfo? timeZone = null)
    {
        _original = profile;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _quickScrubActions = profile.Actions & QuickScrubActions;
        _periodicDays = profile.PeriodicQuickScrubDays;
        _runAutomaticallyBeforeManual = profile.Enabled;
        _wasManualCadence = profile.Cadence == LibraryMaintenanceCadence.ManualOnly;

        Text = "Scheduled Library Maintenance";
        Name = "maintenanceScheduleEditor";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(840, 860);
        MinimumSize = new Size(800, 680);
        AutoScaleMode = AutoScaleMode.Dpi;

        InitializeValues(locationPath, profile);
        BuildLayout();
        HookChanges();
        UpdateConditionalSections();
        UpdateQuickScrubSummary();
        UpdateSummary();
    }

    internal LibraryMaintenanceProfile? SavedProfile { get; private set; }

    internal bool TryCreateProfile(out LibraryMaintenanceProfile profile, out string error)
    {
        profile = _original;
        error = string.Empty;
        LibraryMaintenanceCadence cadence = SelectedCadence();
        bool enabled = cadence != LibraryMaintenanceCadence.ManualOnly && RunAutomatically.Checked;
        if (enabled && cadence == LibraryMaintenanceCadence.Weekly && SelectedDays() == LibraryMaintenanceDays.None)
        {
            error = "Choose at least one weekday for a weekly automatic schedule.";
            return false;
        }

        if (Frequency.SelectedIndex < 0 || MissedRun.SelectedIndex < 0 || EncodingConflict.SelectedIndex < 0)
        {
            error = "Choose a valid schedule and conflict policy.";
            return false;
        }

        LibraryMaintenanceActions actions = LibraryMaintenanceActions.None;
        foreach (CheckBox task in new[] { RefreshCatalog, RefreshMetadata, ExactDuplicates, VisualDuplicates })
            if (task.Checked) actions |= (LibraryMaintenanceActions)task.Tag!;
        bool quickEnabled = QuickScrubEnabled.Checked;
        int periodicDays = quickEnabled ? ReadPeriodicDays() : 0;
        if (quickEnabled)
        {
            _quickScrubActions = (QuickScrubNew.Checked ? LibraryMaintenanceActions.QuickScrubNew : 0) |
                (QuickScrubNeverChecked.Checked ? LibraryMaintenanceActions.QuickScrubNeverChecked : 0) |
                (QuickScrubStale.Checked ? LibraryMaintenanceActions.QuickScrubStale : 0) |
                (QuickScrubFailed.Checked ? LibraryMaintenanceActions.QuickScrubFailed : 0);
            actions |= _quickScrubActions;
        }
        // AnalyzeFamilies is a separate persisted option, not an action bit.
        profile = _original with
        {
            Enabled = enabled,
            Cadence = cadence,
            Days = SelectedDays(),
            StartTime = WindowStart.Value.TimeOfDay,
            EndTime = WindowEnd.Value.TimeOfDay,
            MissedRun = SelectedMissedRun(),
            Actions = actions,
            PeriodicQuickScrubDays = periodicDays,
            AnalysisMode = FullScope.Checked ? LibraryMaintenanceAnalysisMode.FullReanalysis : LibraryMaintenanceAnalysisMode.Incremental,
            ConflictBehavior = EncodingConflict.SelectedIndex == 1 ? LibraryMaintenanceConflictBehavior.Skip : LibraryMaintenanceConflictBehavior.Wait,
            AnalyzeFamilies = DuplicateFamilies.Checked,
            UpdatedUtc = DateTime.UtcNow
        };
        return true;
    }

    internal void SaveButtonClick(object? sender, EventArgs e)
    {
        if (!TryCreateProfile(out LibraryMaintenanceProfile profile, out string error))
        {
            ValidationMessage.Text = error;
            UpdateSummary();
            return;
        }
        SavedProfile = profile;
        DialogResult = DialogResult.OK;
    }

    private void InitializeValues(string locationPath, LibraryMaintenanceProfile profile)
    {
        LocationLabel.Text = locationPath;
        RunAutomatically.Checked = profile.Enabled;
        Frequency.Items.AddRange(CadenceChoices.Select(item => item.Label).Cast<object>().ToArray());
        Frequency.SelectedIndex = Array.FindIndex(CadenceChoices, item => item.Value == profile.Cadence);
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
            Weekdays.Items.Add(day.ToString(), (profile.Days & (LibraryMaintenanceDays)(1 << (int)day)) != 0);
        WindowStart.Value = DateTime.Today + profile.StartTime;
        WindowEnd.Value = DateTime.Today + profile.EndTime;
        EncodingConflict.Items.AddRange(new object[] { "Wait for encoding to finish", "Skip this scheduled run" });
        EncodingConflict.SelectedIndex = profile.ConflictBehavior == LibraryMaintenanceConflictBehavior.Skip ? 1 : 0;
        MissedRun.Items.AddRange(MissedRunChoices.Select(item => item.Label).Cast<object>().ToArray());
        MissedRun.SelectedIndex = Array.FindIndex(MissedRunChoices, item => item.Value == profile.MissedRun);
        MissedRunHelper.Text = "If the scheduled start is missed, maintenance can run later, wait for MediaFlux to start, or be skipped.";

        IncrementalScope.Checked = profile.AnalysisMode != LibraryMaintenanceAnalysisMode.FullReanalysis;
        FullScope.Checked = profile.AnalysisMode == LibraryMaintenanceAnalysisMode.FullReanalysis;
        RefreshCatalog.Checked = profile.Actions.HasFlag(LibraryMaintenanceActions.IncrementalScan);
        RefreshMetadata.Checked = profile.Actions.HasFlag(LibraryMaintenanceActions.Metadata);
        ExactDuplicates.Checked = profile.Actions.HasFlag(LibraryMaintenanceActions.ExactDuplicates);
        VisualDuplicates.Checked = profile.Actions.HasFlag(LibraryMaintenanceActions.VisualDuplicates);
        DuplicateFamilies.Checked = profile.AnalyzeFamilies;

        QuickScrubEnabled.Checked = _quickScrubActions != LibraryMaintenanceActions.None || _periodicDays > 0;
        QuickScrubNew.Checked = _quickScrubActions.HasFlag(LibraryMaintenanceActions.QuickScrubNew);
        QuickScrubNeverChecked.Checked = _quickScrubActions.HasFlag(LibraryMaintenanceActions.QuickScrubNeverChecked);
        QuickScrubStale.Checked = _quickScrubActions.HasFlag(LibraryMaintenanceActions.QuickScrubStale);
        QuickScrubFailed.Checked = _quickScrubActions.HasFlag(LibraryMaintenanceActions.QuickScrubFailed);
        int[] periods = { 0, 7, 14, 30, 60, 90, 180, 365 };
        foreach (int period in periods.Append(_periodicDays).Distinct().Order())
            PeriodicHealthyRecheck.Items.Add(FormatPeriod(period));
        PeriodicHealthyRecheck.SelectedItem = FormatPeriod(_periodicDays);
    }

    private void BuildLayout()
    {
        var content = new FlowLayoutPanel
        {
            Name = "maintenanceEditorContent",
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10),
            Width = 760
        };
        var scroll = new Panel { Name = "maintenanceEditorScroll", Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(content);
        content.Controls.Add(CreateSection("A. Location", BuildLocationSection()));
        content.Controls.Add(CreateSection("B. Schedule", BuildScheduleSection()));
        content.Controls.Add(CreateSection("C. If maintenance can't start", BuildConflictSection()));
        content.Controls.Add(CreateSection("D. Analysis scope", BuildScopeSection()));
        content.Controls.Add(CreateSection("E. Maintenance tasks", BuildTasksSection()));
        content.Controls.Add(new AnalyzerSectionPanel("F. Schedule summary", ScheduleSummary) { Width = 728, Height = 126, Margin = new Padding(3, 7, 3, 6) });

        var buttons = new FlowLayoutPanel
        {
            Name = "maintenanceEditorButtons",
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(10, 7, 12, 5)
        };
        var save = new Button { Name = "saveSchedule", Text = "Save Schedule", AutoSize = true, MinimumSize = new Size(120, 30) };
        var cancel = new Button { Name = "cancelSchedule", Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 30), DialogResult = DialogResult.Cancel };
        AnalyzerUi.StylePrimary(save);
        AnalyzerUi.StyleSecondary(cancel);
        save.Click += SaveButtonClick;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        content.Controls.Add(ValidationMessage);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.Controls.Add(scroll, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private Control BuildLocationSection()
    {
        var body = Vertical();
        body.Controls.Add(new Label { Text = "Library location", AutoSize = true, ForeColor = SystemColors.GrayText });
        body.Controls.Add(LocationLabel);
        return body;
    }

    private Control BuildScheduleSection()
    {
        var body = Vertical();
        body.Controls.Add(RunAutomatically);
        body.Controls.Add(Row(new Label { Text = "Frequency", AutoSize = true, Margin = new Padding(3, 7, 12, 0) }, Frequency));
        body.Controls.Add(ManualOnlyNote);
        var weekdayBody = Vertical();
        weekdayBody.Controls.Add(new Label { Text = "Run on", AutoSize = true, ForeColor = SystemColors.GrayText });
        weekdayBody.Controls.Add(Weekdays);
        WeekdayPanel.Controls.Add(weekdayBody);
        body.Controls.Add(WeekdayPanel);
        WindowPanel.Controls.Add(Row(new Label { Text = "Maintenance window", AutoSize = true, Margin = new Padding(3, 7, 12, 0) },
            new Label { Text = "From", AutoSize = true, Margin = new Padding(3, 7, 3, 0) }, WindowStart,
            new Label { Text = "to", AutoSize = true, Margin = new Padding(8, 7, 3, 0) }, WindowEnd));
        body.Controls.Add(WindowPanel);
        body.Controls.Add(StartupWindowNote);
        return body;
    }

    private Control BuildConflictSection()
    {
        var body = Vertical();
        body.Controls.Add(Row(new Label { Text = "If encoding is running", AutoSize = true, Width = 190 }, EncodingConflict));
        body.Controls.Add(EncodingConflictHelper);
        body.Controls.Add(Row(new Label { Text = "If the scheduled start is missed", AutoSize = true, Width = 190 }, MissedRun));
        body.Controls.Add(MissedRunHelper);
        return body;
    }

    private Control BuildScopeSection()
    {
        var body = Vertical();
        body.Controls.Add(IncrementalScope);
        body.Controls.Add(new Label { Text = "Process only new, changed, missing, or stale information.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(22, 0, 3, 5) });
        body.Controls.Add(FullScope);
        body.Controls.Add(new Label { Text = "Reprocess the entire library location. This takes considerably longer.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(22, 0, 3, 2) });
        body.Controls.Add(new Label { Text = "Analysis scope selects which files are eligible; maintenance tasks select what operations run.", AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 5, 3, 0) });
        return body;
    }

    private Control BuildTasksSection()
    {
        var body = Vertical();
        body.Controls.Add(Row(RefreshCatalog, RefreshMetadata));
        body.Controls.Add(Row(ExactDuplicates, VisualDuplicates));
        body.Controls.Add(DuplicateFamilies);
        body.Controls.Add(new Label { Text = "Missing visual fingerprints are generated when duplicate families are built.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(22, 0, 3, 2) });
        var quickRow = Row(QuickScrubEnabled, QuickScrubSummary, QuickScrubAdvancedButton);
        body.Controls.Add(quickRow);
        var advanced = Vertical();
        advanced.Controls.Add(Row(QuickScrubNew, QuickScrubNeverChecked));
        advanced.Controls.Add(Row(QuickScrubStale, QuickScrubFailed));
        advanced.Controls.Add(Row(PeriodicHealthyLabel, PeriodicHealthyRecheck));
        QuickScrubAdvanced.Controls.Add(advanced);
        body.Controls.Add(QuickScrubAdvanced);
        return body;
    }

    private void HookChanges()
    {
        RunAutomatically.CheckedChanged += (_, _) => { ValidationMessage.Text = string.Empty; UpdateSummary(); };
        Frequency.SelectedIndexChanged += (_, _) => { UpdateConditionalSections(); UpdateSummary(); };
        Weekdays.ItemCheck += (_, _) =>
        {
            if (IsHandleCreated) BeginInvoke(UpdateSummary);
            else UpdateSummary();
        };
        WindowStart.ValueChanged += (_, _) => UpdateSummary();
        WindowEnd.ValueChanged += (_, _) => UpdateSummary();
        EncodingConflict.SelectedIndexChanged += (_, _) => { UpdateConflictHelper(); UpdateSummary(); };
        MissedRun.SelectedIndexChanged += (_, _) => { UpdateMissedRunHelper(); UpdateSummary(); };
        IncrementalScope.CheckedChanged += (_, _) => UpdateSummary();
        FullScope.CheckedChanged += (_, _) => UpdateSummary();
        foreach (CheckBox task in new[] { RefreshCatalog, RefreshMetadata, ExactDuplicates, VisualDuplicates, DuplicateFamilies })
            task.CheckedChanged += (_, _) => UpdateSummary();
        QuickScrubEnabled.CheckedChanged += (_, _) =>
        {
            if (QuickScrubEnabled.Checked && _quickScrubActions == LibraryMaintenanceActions.None && _periodicDays == 0)
            {
                _quickScrubActions = LibraryMaintenanceActions.QuickScrubNew | LibraryMaintenanceActions.QuickScrubStale;
                QuickScrubNew.Checked = true;
                QuickScrubStale.Checked = true;
            }
            else if (!QuickScrubEnabled.Checked)
            {
                _quickScrubActions = LibraryMaintenanceActions.None;
                _periodicDays = 0;
                QuickScrubNew.Checked = QuickScrubNeverChecked.Checked = QuickScrubStale.Checked = QuickScrubFailed.Checked = false;
                PeriodicHealthyRecheck.SelectedItem = "Never";
            }
            UpdateQuickScrubSummary();
            UpdateSummary();
        };
        QuickScrubAdvancedButton.Click += (_, _) =>
        {
            QuickScrubAdvanced.Visible = !QuickScrubAdvanced.Visible;
            QuickScrubAdvancedButton.Text = QuickScrubAdvanced.Visible ? "Hide advanced settings" : "Advanced settings…";
        };
        foreach (CheckBox option in new[] { QuickScrubNew, QuickScrubNeverChecked, QuickScrubStale, QuickScrubFailed })
            option.CheckedChanged += (_, _) => { UpdateQuickScrubSummary(); UpdateSummary(); };
        PeriodicHealthyRecheck.SelectedIndexChanged += (_, _) => { UpdateQuickScrubSummary(); UpdateSummary(); };
    }

    private void UpdateConditionalSections()
    {
        LibraryMaintenanceCadence cadence = SelectedCadence();
        bool manualOnly = cadence == LibraryMaintenanceCadence.ManualOnly;
        if (manualOnly != _wasManualCadence)
        {
            if (manualOnly)
            {
                _runAutomaticallyBeforeManual = RunAutomatically.Checked;
                RunAutomatically.Checked = false;
            }
            else
            {
                RunAutomatically.Checked = _runAutomaticallyBeforeManual;
            }
            _wasManualCadence = manualOnly;
        }
        if (manualOnly) RunAutomatically.Checked = false;
        RunAutomatically.Enabled = !manualOnly;
        WeekdayPanel.Visible = cadence == LibraryMaintenanceCadence.Weekly;
        WindowPanel.Visible = cadence is LibraryMaintenanceCadence.Daily or LibraryMaintenanceCadence.Weekly;
        ManualOnlyNote.Visible = cadence == LibraryMaintenanceCadence.ManualOnly;
        StartupWindowNote.Visible = cadence == LibraryMaintenanceCadence.OnStartup;
        bool hasMissedRunPolicy = cadence is not (LibraryMaintenanceCadence.ManualOnly or LibraryMaintenanceCadence.OnStartup);
        MissedRun.Visible = hasMissedRunPolicy;
        MissedRunHelper.Visible = hasMissedRunPolicy;
        UpdateMissedRunHelper();
        UpdateConflictHelper();
        ValidationMessage.Text = string.Empty;
    }

    private void UpdateConflictHelper()
    {
        LibraryMaintenanceCadence cadence=SelectedCadence();
        if(cadence==LibraryMaintenanceCadence.OnStartup)
            EncodingConflictHelper.Text=EncodingConflict.SelectedIndex==1
                ? "If encoding is active when startup maintenance is ready, that run is skipped."
                : "Maintenance waits for encoding to finish. Startup runs are not restricted by the normal maintenance window.";
        else if(cadence==LibraryMaintenanceCadence.ManualOnly)
            EncodingConflictHelper.Text=EncodingConflict.SelectedIndex==1
                ? "If encoding is active when you run maintenance manually, that run is skipped."
                : "Manual maintenance waits for encoding to finish and is not restricted by the scheduled maintenance window.";
        else
            EncodingConflictHelper.Text=EncodingConflict.SelectedIndex==1
                ? "If encoding is active when a scheduled run is ready, that run is skipped."
                : "Maintenance waits for encoding during the allowed maintenance window. If the window closes first, the run is deferred.";
    }

    private void UpdateMissedRunHelper()
    {
        if (SelectedMissedRun() == LibraryMaintenanceMissedRun.RunOnNextStartup)
            MissedRunHelper.Text = "This may run outside the normal maintenance window when MediaFlux next starts.";
        else if (SelectedMissedRun() == LibraryMaintenanceMissedRun.RunAtNextWindow)
            MissedRunHelper.Text = "If the start is missed, maintenance can still run while the configured window is open.";
        else
            MissedRunHelper.Text = "A missed occurrence is recorded as skipped.";
    }

    private void UpdateQuickScrubSummary()
    {
        QuickScrubAdvancedButton.Enabled = QuickScrubEnabled.Checked;
        QuickScrubAdvanced.Enabled = QuickScrubEnabled.Checked;
        if (!QuickScrubEnabled.Checked)
        {
            QuickScrubSummary.Text = "Off";
            return;
        }
        var checks = new List<string>();
        if (QuickScrubNew.Checked) checks.Add("new files");
        if (QuickScrubNeverChecked.Checked) checks.Add("never checked");
        if (QuickScrubStale.Checked) checks.Add("stale results");
        string retry = QuickScrubFailed.Checked ? "On" : "Off";
        string healthy = PeriodicHealthyRecheck.SelectedItem?.ToString() ?? FormatPeriod(_periodicDays);
        QuickScrubSummary.Text = $"Check: {(checks.Count == 0 ? "none" : string.Join(", ", checks))} · Retry failed/interrupted: {retry} · Recheck healthy files: {healthy}";
    }

    private void UpdateSummary()
    {
        if (ScheduleSummary.IsDisposed) return;
        LibraryMaintenanceProfile current = CreateSummaryProfile();
        string schedule = current.Cadence switch
        {
            LibraryMaintenanceCadence.ManualOnly => "This maintenance profile runs manually only.",
            LibraryMaintenanceCadence.OnStartup => "When MediaFlux starts. Startup execution is not restricted by the normal maintenance window.",
            LibraryMaintenanceCadence.Weekly when SelectedDays() == LibraryMaintenanceDays.None => "Weekly. Choose at least one weekday.",
            LibraryMaintenanceCadence.Weekly => $"Every {FormatDays(SelectedDays())} between {FormatTime(current.StartTime)} and {FormatTime(current.EndTime)}.",
            _ => $"Daily between {FormatTime(current.StartTime)} and {FormatTime(current.EndTime)}."
        };
        if (!RunAutomatically.Checked && current.Cadence != LibraryMaintenanceCadence.ManualOnly) schedule = $"Schedule disabled. Configured frequency: {schedule}";

        string scope=FullScope.Checked?"full reanalysis":"incremental maintenance";
        string runDescription;
        if(current.Cadence==LibraryMaintenanceCadence.ManualOnly)
            runDescription=$"Manual runs use {scope}";
        else
            runDescription=$"Runs {scope}";
        string conflict=EncodingConflict.SelectedIndex==1
            ? current.Cadence==LibraryMaintenanceCadence.ManualOnly?"; if encoding is active, the manual run is skipped.":"; if encoding is active, the scheduled run is skipped."
            : current.Cadence==LibraryMaintenanceCadence.OnStartup?"and waits for active encoding to finish; startup runs are not restricted by the normal maintenance window."
            : current.Cadence==LibraryMaintenanceCadence.ManualOnly?"and waits for active encoding to finish."
            : "and waits for active encoding to finish during the allowed maintenance window; if the window closes first, the run is deferred.";
        string missed = current.Cadence is LibraryMaintenanceCadence.ManualOnly or LibraryMaintenanceCadence.OnStartup
            ? string.Empty
            : SelectedMissedRun() switch
            {
                LibraryMaintenanceMissedRun.RunAtNextWindow => "If the scheduled start is missed, maintenance runs later during the available maintenance window.",
                LibraryMaintenanceMissedRun.RunOnNextStartup => "If the scheduled start is missed, maintenance runs when MediaFlux next starts. May run outside the normal maintenance window when MediaFlux next starts.",
                _ => "If the scheduled start is missed, that occurrence is skipped."
            };
        string next = DescribeNextRun(current);
        var lines = new List<string> { schedule, $"{runDescription} {conflict}" };
        if (missed.Length > 0) lines.Add(missed);
        lines.Add(next);
        ScheduleSummary.Text = string.Join(Environment.NewLine, lines);
        ValidationMessage.Text = RunAutomatically.Checked && current.Cadence == LibraryMaintenanceCadence.Weekly && SelectedDays() == LibraryMaintenanceDays.None
            ? "Choose at least one weekday for a weekly automatic schedule."
            : string.Empty;
    }

    private string DescribeNextRun(LibraryMaintenanceProfile profile)
    {
        if (!profile.Enabled) return "Next run: Not scheduled while disabled.";
        if (profile.Cadence == LibraryMaintenanceCadence.ManualOnly) return "Next run: Manual only; use Run Now when needed.";
        if (profile.Cadence == LibraryMaintenanceCadence.OnStartup) return "Next run: When MediaFlux next starts.";
        DateTime now = DateTime.SpecifyKind(_utcNow(), DateTimeKind.Utc);
        if (LibraryMaintenanceScheduleCalculator.IsDue(profile, now, false, _timeZone))
            return $"Next run: Due now ({TimeZoneInfo.ConvertTimeFromUtc(now, _timeZone):dddd, MMMM d 'at' h:mm tt}).";
        if (profile.MissedRun == LibraryMaintenanceMissedRun.RunOnNextStartup && LibraryMaintenanceScheduleCalculator.IsDue(profile, now, true, _timeZone))
            return "Next run: When MediaFlux next starts.";
        DateTime? next = LibraryMaintenanceScheduleCalculator.GetNextRunUtc(profile, now, _timeZone);
        return next.HasValue
            ? $"Next run: {TimeZoneInfo.ConvertTimeFromUtc(next.Value, _timeZone):dddd, MMMM d 'at' h:mm tt}."
            : "Next run: No upcoming automatic run.";
    }

    private LibraryMaintenanceProfile CreateSummaryProfile() => _original with
    {
        Enabled = RunAutomatically.Checked,
        Cadence = SelectedCadence(),
        Days = SelectedDays(),
        StartTime = WindowStart.Value.TimeOfDay,
        EndTime = WindowEnd.Value.TimeOfDay,
        MissedRun = SelectedMissedRun(),
        AnalysisMode = FullScope.Checked ? LibraryMaintenanceAnalysisMode.FullReanalysis : LibraryMaintenanceAnalysisMode.Incremental,
        ConflictBehavior = EncodingConflict.SelectedIndex == 1 ? LibraryMaintenanceConflictBehavior.Skip : LibraryMaintenanceConflictBehavior.Wait
    };

    private static string FormatDays(LibraryMaintenanceDays days)
    {
        string[] selected = Enum.GetValues<DayOfWeek>()
            .Where(day => (days & (LibraryMaintenanceDays)(1 << (int)day)) != 0)
            .Select(day => day.ToString()).ToArray();
        if (selected.Length == 0) return "no weekdays";
        if (selected.Length == 1) return selected[0];
        if (selected.Length == 2) return $"{selected[0]} and {selected[1]}";
        return $"{string.Join(", ", selected.Take(selected.Length - 1))}, and {selected[^1]}";
    }

    private static string FormatTime(TimeSpan time) => DateTime.Today.Add(time).ToString("h:mm tt", CultureInfo.CurrentCulture);
    private static string FormatPeriod(int days) => days == 0 ? "Never" : $"{days} days";
    private int ReadPeriodicDays()
    {
        string value = PeriodicHealthyRecheck.SelectedItem?.ToString() ?? FormatPeriod(_periodicDays);
        if (value == "Never") return 0;
        return int.Parse(value.Split(' ')[0], CultureInfo.InvariantCulture);
    }

    private LibraryMaintenanceCadence SelectedCadence() => Frequency.SelectedIndex >= 0 && Frequency.SelectedIndex < CadenceChoices.Length
        ? CadenceChoices[Frequency.SelectedIndex].Value : _original.Cadence;
    private LibraryMaintenanceMissedRun SelectedMissedRun() => MissedRun.SelectedIndex >= 0 && MissedRun.SelectedIndex < MissedRunChoices.Length
        ? MissedRunChoices[MissedRun.SelectedIndex].Value : _original.MissedRun;
    private LibraryMaintenanceDays SelectedDays()
    {
        LibraryMaintenanceDays days = LibraryMaintenanceDays.None;
        for (int i = 0; i < Weekdays.Items.Count; i++)
            if (Weekdays.GetItemChecked(i)) days |= (LibraryMaintenanceDays)(1 << i);
        return days;
    }

    private static GroupBox CreateSection(string title, Control body)
    {
        var section = new GroupBox
        {
            Text = title,
            Name = "section" + string.Concat(title.Where(char.IsLetter)),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = 728,
            MinimumSize = new Size(728, 0),
            Padding = new Padding(10),
            Margin = new Padding(3, 5, 3, 4)
        };
        body.Margin = Padding.Empty;
        body.Dock = DockStyle.Top;
        section.Controls.Add(body);
        return section;
    }

    private static FlowLayoutPanel Vertical() => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Margin = Padding.Empty,
        Padding = Padding.Empty
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        row.Controls.AddRange(controls);
        return row;
    }

    private static ComboBox FriendlyCombo(string name) => new()
    {
        Name = name,
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 320,
        IntegralHeight = false
    };

    private static DateTimePicker TimePicker(string name) => new()
    {
        Name = name,
        Format = DateTimePickerFormat.Time,
        ShowUpDown = true,
        Width = 104,
        Value = DateTime.Today
    };

    private static CheckBox TaskCheck(string name, string label, LibraryMaintenanceActions action) => new()
    {
        Name = name,
        Text = label,
        AutoSize = true,
        Tag = action
    };
}
