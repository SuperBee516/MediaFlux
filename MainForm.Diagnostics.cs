using MediaFlux.Services;

namespace MediaFlux;

internal sealed class EncodingDiagnosticsPanel : UserControl
{
    private readonly EncodingDiagnosticsService _service; private readonly Action<string> _status;
    private readonly DataGridView _grid=new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AllowUserToResizeRows=false,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,AutoGenerateColumns=false};
    private readonly Label _summary=new(){Dock=DockStyle.Bottom,Height=48,AutoEllipsis=true,Padding=new Padding(6)};
    private readonly System.Windows.Forms.Timer _timer=new(){Interval=1000};
    internal bool IsRefreshTimerEnabled=>_timer.Enabled; internal int VisibleSessionCount=>_grid.Rows.Cast<DataGridViewRow>().Count(x=>!x.IsNewRow);
    public EncodingDiagnosticsPanel(EncodingDiagnosticsService service,Action<string> status)
    {
        _service=service;_status=status;Dock=DockStyle.Fill;MinimumSize=new Size(550,180);var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=36};var copy=new Button{Text="Copy selected diagnostic",AutoSize=true};copy.Click+=(_,_)=>CopySelected();bar.Controls.Add(copy);
        AddColumn("Job",155);AddColumn("Speed",65);AddColumn("FPS",55);AddColumn("Elapsed / ETA",140);AddColumn("Encoder / preset",180);AddColumn("Concurrent",75);AddColumn("CPU",100);AddColumn("GPU encode",90);AddColumn("Maintenance",140);AddColumn("Observation",360,true);
        _grid.SelectionChanged+=(_,_)=>UpdateSummary();Controls.Add(_grid);Controls.Add(_summary);Controls.Add(bar);_timer.Tick+=(_,_)=>RefreshNow();_timer.Start();RefreshNow();
    }
    internal void RefreshNow()
    {
        if(IsDisposed)return;IReadOnlyList<EncodingDiagnosticSnapshot> rows=_service.GetActive();string? selected=(_grid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag as EncodingDiagnosticSnapshot)?.Job.Id;_grid.Rows.Clear();
        foreach(var d in rows){EncodingDiagnosticSample? s=d.Latest;int row=_grid.Rows.Add(d.Job.DisplayName,s==null?"—":$"{s.Speed:0.00}x",s==null?"—":$"{s.Fps:0.0}",$"{d.Elapsed:g} / {d.EstimatedRemaining?.ToString("g")??"—"}",$"{d.Job.Encoder} / {d.Job.Preset}",s?.ConcurrentJobs.ToString()??"—",s?.System.SystemCpuPercent is double cpu?$"{cpu:0.#}%":"Unavailable",s?.System.GpuEncodePercent is double gpu?$"{gpu:0.#}%":"Unavailable",s?.MaintenanceActive==true?(s.SameDeviceMaintenance?"Same device":"Active"):s?.MaintenanceDeferred==true?"Deferred":"None",d.Observation);_grid.Rows[row].Tag=d;if(d.Job.Id==selected)_grid.Rows[row].Selected=true;}
        if(rows.Count==0)_summary.Text="No active encode diagnostic sessions. Completed summaries are retained with finalized encoding statistics and history.";else UpdateSummary();
    }
    private void UpdateSummary(){if(_grid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag is EncodingDiagnosticSnapshot d)_summary.Text=$"{d.Job.Codec} · {d.Job.SourceResolution} → {d.Job.OutputResolution} · {d.Observation}";}
    private void CopySelected(){if(_grid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag is not EncodingDiagnosticSnapshot d)return;try{Clipboard.SetText(_service.FormatForClipboard(d));_status("Encoding diagnostic copied without full source paths.");}catch(Exception ex){_status($"Could not copy diagnostic: {ex.Message}");}}
    private void AddColumn(string name,int width,bool fill=false){var c=new DataGridViewTextBoxColumn{Name=name,HeaderText=name,Width=width};if(fill)c.AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;_grid.Columns.Add(c);}
    protected override void Dispose(bool disposing){if(disposing){_timer.Stop();_timer.Dispose();}base.Dispose(disposing);}
}

public partial class MainForm
{
    private EncodingDiagnosticsPanel? _encodingDiagnosticsPanel;
    private Control CreateEncodingDiagnosticsGroup()=>_encodingDiagnosticsPanel=new EncodingDiagnosticsPanel(_encodingDiagnosticsService,message=>toolStripStatusLabel1.Text=message);
    private void RefreshEncodingDiagnostics()=>_encodingDiagnosticsPanel?.RefreshNow();
}
