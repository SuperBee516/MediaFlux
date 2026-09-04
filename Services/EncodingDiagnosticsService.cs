using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux.Services;

public sealed record EncodingDiagnosticJob(
    string Id, string DisplayName, string Encoder, string EncoderId, string Codec, string Preset,
    string SourceResolution, string OutputResolution, int? BitDepth, double? SourceDurationSeconds,
    string SourcePath);

public sealed record EncodingSystemTelemetry(
    double? SystemCpuPercent, double? ProcessCpuPercent, long? AvailableMemoryBytes,
    long ProcessMemoryBytes, double? GpuPercent, double? GpuEncodePercent,
    double? GpuDecodePercent, long? VramUsedBytes, string GpuStatus);

public sealed record EncodingDiagnosticSample(
    DateTime Utc, double Speed, double Fps, double BitrateKbps, double MediaSeconds,
    double Percent, int ConcurrentJobs, EncodingSystemTelemetry System,
    bool MaintenanceActive, bool SameDeviceMaintenance, bool MaintenanceDeferred,
    string MaintenanceStage, long? OutputSizeBytes = null);

public sealed record EncodingDiagnosticSnapshot(
    EncodingDiagnosticJob Job, DateTime StartedUtc, TimeSpan Elapsed, TimeSpan? EstimatedRemaining,
    EncodingDiagnosticSample? Latest, string Observation, int RetainedSamples);

public sealed record EncodingDiagnosticSummary
{
    public double? AverageSpeed { get; init; }
    public double? MedianSpeed { get; init; }
    public double? AverageFps { get; init; }
    public double? AverageSystemCpuPercent { get; init; }
    public double? AverageProcessCpuPercent { get; init; }
    public double? AverageGpuEncodePercent { get; init; }
    public int PeakConcurrentJobs { get; init; }
    public double MaintenanceOverlapSeconds { get; init; }
    public double SameDeviceMaintenanceSeconds { get; init; }
    public double? StorageWaitSeconds { get; init; }
    public double FinalizationSeconds { get; init; }
    public int Samples { get; init; }
    public string Observation { get; init; } = "Telemetry unavailable.";
}

public interface IEncodingSystemTelemetryProvider { EncodingSystemTelemetry Sample(); }

public sealed class WindowsEncodingSystemTelemetryProvider : IEncodingSystemTelemetryProvider, IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly NvidiaSmiTelemetryReader _gpu = new();
    private ulong _previousIdle, _previousKernel, _previousUser; private TimeSpan _previousProcessCpu; private long _previousTimestamp;
    public EncodingSystemTelemetry Sample()
    {
        double? systemCpu=null,processCpu=null;long? available=null;long now=Stopwatch.GetTimestamp();
        try
        {
            if(OperatingSystem.IsWindows()&&GetSystemTimes(out FileTime idle,out FileTime kernel,out FileTime user))
            {ulong i=idle.Value,k=kernel.Value,u=user.Value;if(_previousKernel!=0){ulong total=(k-_previousKernel)+(u-_previousUser),idleDelta=i-_previousIdle;if(total>0)systemCpu=Math.Clamp((total-idleDelta)*100d/total,0,100);} _previousIdle=i;_previousKernel=k;_previousUser=u;}
            TimeSpan cpu=_process.TotalProcessorTime;if(_previousTimestamp!=0){double seconds=(now-_previousTimestamp)/(double)Stopwatch.Frequency;processCpu=Math.Clamp((cpu-_previousProcessCpu).TotalSeconds/Math.Max(.001,seconds*Environment.ProcessorCount)*100,0,100);} _previousProcessCpu=cpu;_previousTimestamp=now;
            if(OperatingSystem.IsWindows()){var memory=new MemoryStatusEx();if(GlobalMemoryStatusEx(memory))available=(long)Math.Min(memory.AvailablePhysical, long.MaxValue);}
            _process.Refresh();
        }catch{}
        (double? gpu,double? encode,double? decode,long? vram,string status)=_gpu.Sample();
        return new(systemCpu,processCpu,available,_process.WorkingSet64,gpu,encode,decode,vram,status);
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileTime{public uint Low,High;public ulong Value=>((ulong)High<<32)|Low;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Auto)] private sealed class MemoryStatusEx{public uint Length=(uint)Marshal.SizeOf<MemoryStatusEx>();public uint Load;public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;}
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FileTime idle,out FileTime kernel,out FileTime user);
    [DllImport("kernel32.dll",CharSet=CharSet.Auto)] private static extern bool GlobalMemoryStatusEx([In,Out] MemoryStatusEx value);
    public void Dispose()=>_process.Dispose();
}

internal sealed class NvidiaSmiTelemetryReader
{
    public (double? Gpu,double? Encode,double? Decode,long? Vram,string Status) Sample()
    {
        try
        {
            using var process=new Process{StartInfo=new ProcessStartInfo
            {
                FileName="nvidia-smi.exe",
                Arguments="--query-gpu=utilization.gpu,utilization.encoder,utilization.decoder,memory.used --format=csv,noheader,nounits",
                UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden
            }};
            if(!process.Start())return (null,null,null,null,"NVIDIA telemetry unavailable.");
            if(!process.WaitForExit(1000)){try{process.Kill(true);}catch{}return(null,null,null,null,"NVIDIA telemetry timed out.");}
            string output=process.StandardOutput.ReadToEnd().Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()??"";
            if(process.ExitCode!=0)return(null,null,null,null,"NVIDIA telemetry unavailable.");
            return ParseLine(output);
        }
        catch{return(null,null,null,null,"GPU telemetry unavailable; nvidia-smi was not found or could not be queried.");}
    }

    internal static (double? Gpu,double? Encode,double? Decode,long? Vram,string Status) ParseLine(string output)
    {
        string[] values=(output??"").Split(',').Select(value=>value.Trim()).ToArray();
        if(values.Length<4)return(null,null,null,null,"NVIDIA telemetry unavailable.");
        double? Number(string value)=>double.TryParse(value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double number)?number:null;
        double? gpu=Number(values[0]),encode=Number(values[1]),decode=Number(values[2]),memory=Number(values[3]);
        return(gpu,encode,decode,memory.HasValue?(long?)(memory.Value*1048576d):null,"NVIDIA telemetry available through nvidia-smi.");
    }
}

public static class EncodingDiagnosticInterpreter
{
    public static string Interpret(EncodingDiagnosticSample? sample)
    {
        if(sample==null)return "Waiting for FFmpeg telemetry.";
        if(sample.SameDeviceMaintenance&&sample.Speed>0&&sample.Speed<.9)return "Possible storage contention: encode speed is below real time while maintenance is active on the same storage device.";
        if(sample.MaintenanceDeferred)return "Maintenance is deferred while encoding has priority; no direct maintenance contention is expected.";
        if(sample.ConcurrentJobs>1)return $"Multiple concurrent jobs ({sample.ConcurrentJobs}) are sharing encoder and system capacity.";
        if(sample.System.GpuEncodePercent is >=75)return "Encoder substantially utilized; no strong storage bottleneck is evident from available signals.";
        if(sample.System.GpuEncodePercent is <35&&sample.System.GpuDecodePercent is >=70&&sample.Speed<1)return "Decode pressure may be limiting throughput: decode utilization is high while encoder utilization and speed are low.";
        if(sample.Speed>0&&sample.System.GpuEncodePercent==null)return "No obvious bottleneck detected from FFmpeg and CPU signals; GPU telemetry is unavailable.";
        return "No obvious bottleneck detected from the available signals.";
    }
}

public sealed class EncodingDiagnosticsService : IDisposable
{
    public const int MaximumSamplesPerSession=300; private readonly object _sync=new();private readonly object _sampleGate=new();private readonly Dictionary<string,Session> _active=new(StringComparer.OrdinalIgnoreCase);
    private readonly IEncodingSystemTelemetryProvider _system;private readonly System.Threading.Timer _timer;private readonly LibraryStorageScheduler _storageKeys=new();private bool _disposed;
    public event Action? Changed;
    public EncodingDiagnosticsService(IEncodingSystemTelemetryProvider? system=null,TimeSpan? interval=null){_system=system??new WindowsEncodingSystemTelemetryProvider();_timer=new System.Threading.Timer(_=>SampleAll(),null,interval??TimeSpan.FromSeconds(1),interval??TimeSpan.FromSeconds(1));}
    public void Start(EncodingDiagnosticJob job,DateTime? utc=null){if(Volatile.Read(ref _disposed))return;lock(_sync){if(_disposed)return;_active[job.Id]=new(job,utc??DateTime.UtcNow,_storageKeys.ResolveStorageKey(job.SourcePath));}Changed?.Invoke();}
    public void UpdateProgress(string id,string line,double? totalDurationSeconds=null)
    {if(Volatile.Read(ref _disposed)||!TryParseProgress(line,totalDurationSeconds,out var p))return;lock(_sync)if(!_disposed&&_active.TryGetValue(id,out Session? s))s.Progress=p;}
    public IReadOnlyList<EncodingDiagnosticSnapshot> GetActive(){lock(_sync)return _disposed?Array.Empty<EncodingDiagnosticSnapshot>():_active.Values.Select(Snapshot).OrderBy(x=>x.StartedUtc).ToArray();}
    public EncodingDiagnosticSummary? Complete(string id,double finalizationSeconds=0)
    {Session? s;lock(_sync){if(_disposed||!_active.Remove(id,out s))return null;}EncodingDiagnosticSummary summary=Summarize(s,finalizationSeconds);Changed?.Invoke();return summary;}
    public EncodingDiagnosticSummary? Cancel(string id,double finalizationSeconds=0)=>Complete(id,finalizationSeconds);
    public void CaptureNow()=>SampleAll();
    public static bool TryParseProgress(string line,double? totalDurationSeconds,out ProgressValue value)
    {value=default;if(string.IsNullOrWhiteSpace(line))return false;double time=ParseTime(Value(line,"time="));if(time<0)return false;double speed=Number(Value(line,"speed=").TrimEnd('x'));double fps=Number(Value(line,"fps="));double bitrate=Number(Value(line,"bitrate=").Replace("kbits/s","",StringComparison.OrdinalIgnoreCase));string sizeText=Value(line,"size=");long? size=ParseSize(sizeText);double percent=totalDurationSeconds is>0?Math.Clamp(time/totalDurationSeconds.Value*100,0,100):0;value=new(speed,fps,bitrate,time,percent,size);return true;}
    public readonly record struct ProgressValue(double Speed,double Fps,double BitrateKbps,double MediaSeconds,double Percent,long? OutputSizeBytes);
    public string FormatForClipboard(EncodingDiagnosticSnapshot snapshot)
    {var s=snapshot.Latest;var b=new StringBuilder("MediaFlux Encoding Diagnostic\r\n");b.AppendLine($"Job: {snapshot.Job.DisplayName}").AppendLine($"Encoder: {snapshot.Job.Encoder}").AppendLine($"Codec / Preset: {snapshot.Job.Codec} / {snapshot.Job.Preset}").AppendLine($"Resolution: {snapshot.Job.SourceResolution} → {snapshot.Job.OutputResolution}").AppendLine($"Speed / FPS: {(s?.Speed.ToString("0.00")??"Unavailable")}x / {s?.Fps.ToString("0.0")??"Unavailable"}").AppendLine($"Output bitrate / size: {(s==null?"Unavailable":$"{s.BitrateKbps:0.0} kbit/s")} / {(s?.OutputSizeBytes is long bytes?$"{bytes/1048576d:0.0} MiB":"Unavailable")}").AppendLine($"Elapsed / ETA: {snapshot.Elapsed:g} / {snapshot.EstimatedRemaining?.ToString("g")??"Unavailable"}").AppendLine($"Concurrent jobs: {s?.ConcurrentJobs.ToString()??"Unavailable"}").AppendLine($"CPU system / MediaFlux: {Percent(s?.System.SystemCpuPercent)} / {Percent(s?.System.ProcessCpuPercent)}").AppendLine($"GPU Encode / Decode / VRAM: {Percent(s?.System.GpuEncodePercent)} / {Percent(s?.System.GpuDecodePercent)} / {(s?.System.VramUsedBytes is long v?$"{v/1048576d:0} MiB":"Unavailable")}").AppendLine($"Storage wait: Unavailable").AppendLine($"Maintenance: {(s?.MaintenanceActive==true?s.MaintenanceStage:s?.MaintenanceDeferred==true?s.MaintenanceStage:"Not active")}").AppendLine($"Observation: {snapshot.Observation}");return b.ToString();}
    private void SampleAll(){lock(_sampleGate){if(_disposed)return;EncodingSystemTelemetry system=_system.Sample();MaintenanceActivitySnapshot maintenance=LibraryMaintenanceActivity.Current;lock(_sync){if(_disposed)return;int concurrent=_active.Count;foreach(Session s in _active.Values){bool same=maintenance.Active&&!string.IsNullOrWhiteSpace(maintenance.StorageKey)&&string.Equals(s.StorageKey,maintenance.StorageKey,StringComparison.OrdinalIgnoreCase);var sample=new EncodingDiagnosticSample(DateTime.UtcNow,s.Progress.Speed,s.Progress.Fps,s.Progress.BitrateKbps,s.Progress.MediaSeconds,s.Progress.Percent,concurrent,system,maintenance.Active,same,maintenance.WaitingForEncoding,maintenance.Stage,s.Progress.OutputSizeBytes);s.Samples.Enqueue(sample);while(s.Samples.Count>MaximumSamplesPerSession)s.Samples.Dequeue();if(maintenance.Active)s.MaintenanceSamples++;if(same)s.SameDeviceSamples++;}}}if(!Volatile.Read(ref _disposed))Changed?.Invoke();}
    private static EncodingDiagnosticSnapshot Snapshot(Session s){EncodingDiagnosticSample? latest=s.Samples.LastOrDefault();TimeSpan elapsed=DateTime.UtcNow-s.Started;double? etaSeconds=s.Job.SourceDurationSeconds is>0?EncodeEtaCalculator.CalculateSeconds(s.Job.SourceDurationSeconds.Value,s.Progress.MediaSeconds,s.Progress.Speed):null;TimeSpan? eta=etaSeconds.HasValue?TimeSpan.FromSeconds(etaSeconds.Value):null;return new(s.Job,s.Started,elapsed,eta,latest,EncodingDiagnosticInterpreter.Interpret(latest),s.Samples.Count);}
    private static EncodingDiagnosticSummary Summarize(Session s,double finalization){EncodingDiagnosticSample[] a=s.Samples.ToArray();double? Avg(Func<EncodingDiagnosticSample,double?> f){double[] v=a.Select(f).Where(x=>x.HasValue).Select(x=>x!.Value).ToArray();return v.Length==0?null:v.Average();}double[] speeds=a.Where(x=>x.Speed>0).Select(x=>x.Speed).Order().ToArray();double? median=speeds.Length==0?null:speeds.Length%2==1?speeds[speeds.Length/2]:(speeds[speeds.Length/2-1]+speeds[speeds.Length/2])/2;return new(){AverageSpeed=speeds.Length==0?null:speeds.Average(),MedianSpeed=median,AverageFps=Avg(x=>x.Fps>0?x.Fps:null),AverageSystemCpuPercent=Avg(x=>x.System.SystemCpuPercent),AverageProcessCpuPercent=Avg(x=>x.System.ProcessCpuPercent),AverageGpuEncodePercent=Avg(x=>x.System.GpuEncodePercent),PeakConcurrentJobs=a.Length==0?0:a.Max(x=>x.ConcurrentJobs),MaintenanceOverlapSeconds=s.MaintenanceSamples,SameDeviceMaintenanceSeconds=s.SameDeviceSamples,StorageWaitSeconds=null,FinalizationSeconds=Math.Max(0,finalization),Samples=a.Length,Observation=EncodingDiagnosticInterpreter.Interpret(a.LastOrDefault())};}
    private static string Value(string line,string key){Match m=Regex.Match(line,$@"(?:^|\s){Regex.Escape(key)}\s*([^\s]+)",RegexOptions.IgnoreCase);return m.Success?m.Groups[1].Value:"";}private static double Number(string v)=>double.TryParse(v,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double n)?n:0;private static double ParseTime(string v)=>TimeSpan.TryParse(v,System.Globalization.CultureInfo.InvariantCulture,out TimeSpan t)?t.TotalSeconds:-1;private static long? ParseSize(string value){Match m=Regex.Match(value,@"^(\d+(?:\.\d+)?)(KiB|kB|MiB|MB|B)$",RegexOptions.IgnoreCase);if(!m.Success||!double.TryParse(m.Groups[1].Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double n))return null;double factor=m.Groups[2].Value.ToLowerInvariant() switch{"kib"=>1024,"kb"=>1000,"mib"=>1048576,"mb"=>1000000,_=>1};return (long)Math.Max(0,n*factor);}private static string Percent(double? v)=>v.HasValue?$"{v:0.#}%":"Unavailable";
    public static string FormatCompletedSummary(EncodingDiagnosticSummary? s)
    {
        if(s==null)return "";return $"MediaFlux Encoding Diagnostic Summary{Environment.NewLine}"+
            $"Average / median speed: {Value(s.AverageSpeed,"0.00x")} / {Value(s.MedianSpeed,"0.00x")}{Environment.NewLine}"+
            $"Average FPS: {Value(s.AverageFps,"0.0")}{Environment.NewLine}"+
            $"System / process CPU: {Value(s.AverageSystemCpuPercent,"0.0'%'" )} / {Value(s.AverageProcessCpuPercent,"0.0'%'")}{Environment.NewLine}"+
            $"GPU encode: {Value(s.AverageGpuEncodePercent,"0.0'%'")}{Environment.NewLine}"+
            $"Peak concurrent jobs: {s.PeakConcurrentJobs}{Environment.NewLine}"+
            $"Maintenance overlap / same device: {s.MaintenanceOverlapSeconds:0}s / {s.SameDeviceMaintenanceSeconds:0}s{Environment.NewLine}"+
            $"Storage wait: {(s.StorageWaitSeconds.HasValue?$"{s.StorageWaitSeconds:0.0}s":"Unavailable")}{Environment.NewLine}"+
            $"Finalization overhead: {s.FinalizationSeconds:0.0}s{Environment.NewLine}"+
            $"Observation: {s.Observation}";
        static string Value(double? value,string format)=>value.HasValue?value.Value.ToString(format,System.Globalization.CultureInfo.InvariantCulture):"Unavailable";
    }
    public void Dispose(){if(Volatile.Read(ref _disposed))return;_timer.Dispose();lock(_sampleGate){if(_disposed)return;_disposed=true;lock(_sync)_active.Clear();if(_system is IDisposable disposable)disposable.Dispose();}}
    private sealed class Session(EncodingDiagnosticJob job,DateTime started,string storageKey){public EncodingDiagnosticJob Job=job;public DateTime Started=started;public string StorageKey=storageKey;public ProgressValue Progress;public Queue<EncodingDiagnosticSample> Samples=new();public int MaintenanceSamples;public int SameDeviceSamples;}
}
