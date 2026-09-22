using System.Drawing;
using MediaFlux.Services;

namespace MediaFlux;

/// <summary>Read-only accuracy view over the finalized statistics journal.</summary>
public sealed class EncodingResultsForm : MediaFluxForm
{
    private readonly EncodingStatisticsService _statistics;
    private readonly HistoryService _history;
    private readonly EncodingPredictionAccuracyService _accuracy = new();
    private readonly DataGridView _results = Grid("encodingResultsGrid");
    private readonly DataGridView _cohorts = Grid("encodingResultsCohorts");
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
    private readonly ComboBox _codec = Filter("All codecs");
    private readonly ComboBox _encoder = Filter("All encoders");
    private readonly ComboBox _recovery = Filter("Clean results", "Include recovered");
    private readonly Label _summary = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8), BackColor = Color.White };
    private List<EncodingPredictionAccuracyRow> _rows = new();

    public EncodingResultsForm(EncodingStatisticsService statistics, HistoryService history)
    {
        _statistics = statistics; _history = history;
        Text = "Encoding Results"; StartPosition = FormStartPosition.CenterParent; MinimumSize = new Size(1050, 700); Size = new Size(1300, 820); BackColor = Color.FromArgb(243, 246, 249);
        BuildUi(); LoadRows();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 4, BackColor = BackColor };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        root.Controls.Add(new Label { Text = "Encoding Results\nPrediction accuracy from finalized encoding attempts", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(4), ForeColor = Color.FromArgb(30, 41, 59) }, 0, 0);
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        filters.Controls.AddRange(new Control[] { new Label { Text = "Target", AutoSize = true, Padding = new Padding(2, 8, 2, 0) }, _codec, new Label { Text = "Encoder", AutoSize = true, Padding = new Padding(10, 8, 2, 0) }, _encoder, new Label { Text = "Recovery", AutoSize = true, Padding = new Padding(10, 8, 2, 0) }, _recovery });
        var refresh = new Button { Text = "Refresh", Width = 84, Margin = new Padding(14, 3, 0, 3) }; refresh.Click += (_, _) => LoadRows(); filters.Controls.Add(refresh); root.Controls.Add(filters, 0, 1);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 350 };
        ConfigureResults(); ConfigureCohorts();
        var resultsPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 }; resultsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); resultsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); resultsPanel.Controls.Add(_summary, 0, 0); resultsPanel.Controls.Add(_results, 0, 1); split.Panel1.Controls.Add(resultsPanel);
        var cohortPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 }; cohortPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); cohortPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); cohortPanel.Controls.Add(new Label { Text = "Clean completed cohorts (failed, canceled, sample, and recovered attempts are excluded by default)", Dock = DockStyle.Fill }, 0, 0); cohortPanel.Controls.Add(_cohorts, 0, 1); split.Panel2.Controls.Add(cohortPanel); root.Controls.Add(split, 0, 2); root.Controls.Add(_details, 0, 3); Controls.Add(root);
        foreach (ComboBox filter in new[] { _codec, _encoder, _recovery }) filter.SelectedIndexChanged += (_, _) => Bind();
        _results.SelectionChanged += (_, _) => ShowDetails();
        _cohorts.SelectionChanged += (_, _) => ShowCohortDetails();
    }

    private void ConfigureResults()
    {
        Add(_results, "finished", "Finished", 140); Add(_results, "outcome", "Outcome", 100); Add(_results, "source", "Source", 180); Add(_results, "target", "Target", 80); Add(_results, "encoder", "Encoder", 100); Add(_results, "predicted", "Predicted size", 105); Add(_results, "actual", "Actual size", 100); Add(_results, "sizeApe", "Size APE", 82); Add(_results, "etaApe", "ETA APE", 82); Add(_results, "recovery", "Recovery", 80);
    }
    private void ConfigureCohorts()
    {
        Add(_cohorts, "cohort", "Cohort", 270); Add(_cohorts, "samples", "N size/ETA", 82); Add(_cohorts, "bias", "Median bias", 95); Add(_cohorts, "absolute", "Median abs. error", 120); Add(_cohorts, "iqr", "Bias IQR", 85); Add(_cohorts, "confidence", "Confidence", 90); Add(_cohorts, "state", "Bias state", 105); Add(_cohorts, "savings", "Expected → realized savings", 180);
    }
    private void LoadRows()
    {
        _rows = _accuracy.CreateRows(_statistics.GetAll()).ToList();
        SetItems(_codec, "All codecs", _rows.Select(row => Value(row.Record.PredictionTargetCodec, row.Record.Codec)));
        SetItems(_encoder, "All encoders", _rows.Select(row => Value(row.Record.EncoderId, row.Record.Encoder)));
        Bind();
    }
    private void Bind()
    {
        if (_codec.SelectedItem == null || _encoder.SelectedItem == null || _recovery.SelectedItem == null) return;
        bool includeRecovered = _recovery.Text == "Include recovered";
        EncodingPredictionAccuracyRow[] filtered = _rows.Where(row =>
            (_codec.Text == "All codecs" || Value(row.Record.PredictionTargetCodec, row.Record.Codec).Equals(_codec.Text, StringComparison.OrdinalIgnoreCase)) &&
            (_encoder.Text == "All encoders" || Value(row.Record.EncoderId, row.Record.Encoder).Equals(_encoder.Text, StringComparison.OrdinalIgnoreCase)) &&
            (includeRecovered || !row.Record.RecoveredSuccessful)).ToArray();
        EncodingPredictionAccuracyMetrics metrics = _accuracy.Summarize(filtered, includeRecovered);
        EncodingCalibrationEvaluation calibration = EncodingPredictionAccuracyService.EvaluateCalibrations(filtered.Select(row => row.Record));
        _summary.Text = $"Clean completed: {metrics.CompletedCount}   •   Size N: {metrics.SizePredictionCount} (median bias {Percent(metrics.MedianSizeSignedPercentageError)}, median abs. error {Percent(metrics.MedianSizeAbsolutePercentageError)}, IQR {Percent(metrics.SizeSignedPercentageIqr)})   •   ETA N: {metrics.EtaPredictionCount} (median abs. error {Percent(metrics.MedianEtaAbsolutePercentageError)})   •   Calibrated N: {calibration.CalibratedCount} (base bias {Percent(calibration.MedianBaseSignedErrorPercent)} → calibrated {Percent(calibration.MedianCalibratedSignedErrorPercent)}, median abs-error change {Percent(calibration.MedianAbsoluteErrorImprovement)}; {calibration.Improved} improved / {calibration.Neutral} neutral / {calibration.Worsened} worsened)   •   {metrics.Confidence} / {BiasLabel(metrics.BiasState)}";
        _results.Rows.Clear(); foreach (var row in filtered) { int i = _results.Rows.Add(row.Record.EndUtc.ToLocalTime(), row.Record.Outcome, Path.GetFileName(row.Record.SourcePath), Value(row.Record.PredictionTargetCodec, row.Record.Codec), Value(row.Record.EncoderId, row.Record.Encoder), Bytes(row.Record.PredictedOutputSizeBytes), Bytes(row.Record.OutputSizeBytes), Percent(row.SizeAbsolutePercentageError), Percent(row.EtaAbsolutePercentageError), row.Record.RecoveredSuccessful ? "Recovered" : "Normal"); _results.Rows[i].Tag = row; }
        _cohorts.Rows.Clear(); foreach (var cohort in _accuracy.BuildCohorts(filtered, includeRecovered)) { int index = _cohorts.Rows.Add(CohortLabel(cohort), $"{cohort.SizePredictionCount}/{cohort.EtaPredictionCount}", Percent(cohort.MedianSizeSignedPercentageError), Percent(cohort.MedianSizeAbsolutePercentageError), Percent(cohort.SizeSignedPercentageIqr), cohort.Confidence, BiasLabel(cohort.BiasState), $"{Bytes(cohort.PredictedSavingsBytes)} → {Bytes(cohort.ActualSavingsBytes)}"); _cohorts.Rows[index].Tag = cohort; }
        ShowDetails();
    }
    private void ShowDetails()
    {
        if (_results.SelectedRows.Count == 0 || _results.SelectedRows[0].Tag is not EncodingPredictionAccuracyRow row) { _details.Text = "Select an attempt to review its frozen plan prediction and final outcome."; return; }
        var r = row.Record;
        JobHistoryRecord? history = _history.LoadAll().FirstOrDefault(item => item.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase));
        string historyDetail = history == null ? "No linked Job History entry is available (older observations did not retain the shared operation ID)." : $"Job History: {JobHistoryPresentation.OutcomeSummary(history)}\r\nFinalization: {history.FinalizationOutcome ?? "Unavailable"}";
        EncodingCalibrationEvaluationRow? calibration = EncodingPredictionAccuracyService.EvaluateCalibrations(new[] { r }).Rows.FirstOrDefault();
        string calibrationDetail = calibration == null ? "Size calibration: not applied / unavailable" : $"Size calibration: base {Bytes(r.BasePredictedOutputSizeBytes)}; calibrated {Bytes(r.PredictedOutputSizeBytes)}; base signed error {Percent(calibration.BaseSignedErrorPercent)}; calibrated signed error {Percent(calibration.CalibratedSignedErrorPercent)}; absolute error improvement {Percent(calibration.AbsoluteErrorImprovement)}; {calibration.Outcome}; {Value(r.CalibrationConfidence)} confidence, N={r.CalibrationSampleCount?.ToString() ?? "Unavailable"}; {Value(r.CalibrationReason)}";
        _details.Text = $"Operation: {r.Id}\r\nFrozen plan: {Value(r.PredictionPlanId)}\r\nTerminal: {Value(r.TerminalResult, r.Outcome.ToString())}\r\nSource: {r.SourcePath}\r\nOutput: {r.OutputPath}\r\n\r\nSize: predicted {Bytes(r.PredictedOutputSizeBytes)}; actual {Bytes(r.OutputSizeBytes)}; signed error {Bytes(row.SizeSignedErrorBytes)}; APE {Percent(row.SizeAbsolutePercentageError)}\r\n{calibrationDetail}\r\nETA: predicted {Duration(r.PredictedProcessingSeconds)}; actual {Duration(r.ProcessingSeconds)}; signed error {Duration(row.EtaSignedErrorSeconds)}; APE {Percent(row.EtaAbsolutePercentageError)}\r\n\r\nPlan context: source={Value(r.PredictionSourceCodec)}; target={Value(r.PredictionTargetCodec)}; same codec={r.PredictionSameCodec?.ToString() ?? "Unavailable"}; quality={Value(r.PredictionQuality)}; assessment={Value(r.PredictionAssessment)}; recommendation={Value(r.PredictionRecommendation)}; confidence={Value(r.PredictionConfidence)}\r\n\r\n{historyDetail}";
    }
    private void ShowCohortDetails()
    {
        if (_cohorts.SelectedRows.Count == 0 || _cohorts.SelectedRows[0].Tag is not EncodingPredictionAccuracyCohort cohort) return;
        _details.Text = $"{CohortLabel(cohort)}\r\n\r\nSize predictions: N={cohort.SizePredictionCount}; median bias={Percent(cohort.MedianSizeSignedPercentageError)}; median absolute error={Percent(cohort.MedianSizeAbsolutePercentageError)}; signed-error IQR={Percent(cohort.SizeSignedPercentageIqr)}\r\nETA predictions: N={cohort.EtaPredictionCount}; median bias={Percent(cohort.MedianEtaSignedPercentageError)}; median absolute error={Percent(cohort.MedianEtaAbsolutePercentageError)}; signed-error IQR={Percent(cohort.EtaSignedPercentageIqr)}\r\n\r\nConfidence: {cohort.Confidence}. Bias state: {BiasLabel(cohort.BiasState)}.\r\nExpected savings: {Bytes(cohort.PredictedSavingsBytes)}; realized savings: {Bytes(cohort.ActualSavingsBytes)}.\r\n\r\nPercentages are actual minus predicted, divided by predicted. IQR uses linear-interpolated Q3 minus Q1. Recovered attempts are only included when the recovery filter is enabled.";
    }
    private static DataGridView Grid(string name) => new() { Name = name, Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoGenerateColumns = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false };
    private static ComboBox Filter(params string[] values) { var box = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList }; box.Items.AddRange(values); box.SelectedIndex = 0; return box; }
    private static void Add(DataGridView grid, string name, string text, int width) => grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = text, Width = width, SortMode = DataGridViewColumnSortMode.Automatic });
    private static void SetItems(ComboBox box, string first, IEnumerable<string> values) { string selected = box.Text; box.Items.Clear(); box.Items.Add(first); foreach (string value in values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value)) box.Items.Add(value); box.SelectedItem = box.Items.Contains(selected) ? selected : first; }
    private static string Value(string? primary, string? fallback = null) => string.IsNullOrWhiteSpace(primary) ? (string.IsNullOrWhiteSpace(fallback) ? "Unavailable" : fallback.Trim()) : primary.Trim();
    private static string Percent(double? value) => value.HasValue && double.IsFinite(value.Value) ? $"{value.Value:0.#}%" : "—";
    private static string Bytes(long? value) => value.HasValue ? EncodingStatisticsCalculator.FormatBytes(value.Value) : "—";
    private static string Duration(double? value) => value is > 0 && double.IsFinite(value.Value) ? TimeSpan.FromSeconds(value.Value).ToString() : "—";
    private static string CohortLabel(EncodingPredictionAccuracyCohort cohort) => $"{cohort.SourceCodec} → {cohort.TargetCodec} · {cohort.ResolutionTier} · {cohort.Encoder} · Q{cohort.Quality} · {cohort.Recommendation}";
    private static string BiasLabel(EncodingPredictionBiasState state) => state switch { EncodingPredictionBiasState.Underestimating => "Underestimating", EncodingPredictionBiasState.Overestimating => "Overestimating", EncodingPredictionBiasState.NearTarget => "Near target", _ => "Insufficient data" };
}
