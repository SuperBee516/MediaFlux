using System.Diagnostics;
using System.Text.Json;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingDiagnosticsTests
{
    private static readonly EncodingSystemTelemetry SystemTelemetry=new(35,12,8L<<30,256L<<20,null,null,null,null,"GPU unavailable");

    [Fact]
    public void NvidiaTelemetryParsesUtilizationAndVramWithoutHardwareDependency()
    {
        var value=NvidiaSmiTelemetryReader.ParseLine("42, 71, 18, 2048");
        Assert.Equal(42,value.Gpu);Assert.Equal(71,value.Encode);Assert.Equal(18,value.Decode);Assert.Equal(2048L*1048576,value.Vram);
    }

    [Theory]
    [InlineData("frame= 100 fps= 58 q=22.0 size=1234KiB time=00:00:10.00 bitrate=1000.0kbits/s speed=2.50x",2.5,58,10)]
    [InlineData("size= 10kB time=00:00:05.50 bitrate=128.0kbits/s speed=1.25x",1.25,0,5.5)]
    public void ParsesExistingFfmpegProgressTelemetry(string line,double speed,double fps,double seconds)
    {
        Assert.True(EncodingDiagnosticsService.TryParseProgress(line,20,out var p));Assert.Equal(speed,p.Speed,3);Assert.Equal(fps,p.Fps,3);Assert.Equal(seconds,p.MediaSeconds,3);Assert.Equal(seconds/20*100,p.Percent,3);
    }

    [Fact]
    public void SessionLifecycleProducesBoundedCompletionSummary()
    {
        using var service=new EncodingDiagnosticsService(new FakeSystem(),TimeSpan.FromDays(1));service.Start(Job("a"));for(int i=0;i<350;i++){service.UpdateProgress("a",$"frame= 1 fps= 60 time=00:00:{i%60:00}.00 bitrate=1000kbits/s speed=2.0x",120);service.CaptureNow();}EncodingDiagnosticSnapshot active=Assert.Single(service.GetActive());Assert.Equal(EncodingDiagnosticsService.MaximumSamplesPerSession,active.RetainedSamples);EncodingDiagnosticSummary summary=service.Complete("a",2)!;Assert.Equal(300,summary.Samples);Assert.Equal(2,summary.AverageSpeed);Assert.Equal(2,summary.FinalizationSeconds);Assert.Empty(service.GetActive());
    }

    [Fact]
    public void ConcurrentSessionsRemainIndependentAndCancellationDoesNotLeak()
    {
        using var service=new EncodingDiagnosticsService(new FakeSystem(),TimeSpan.FromDays(1));service.Start(Job("a"));service.Start(Job("b"));service.UpdateProgress("a","fps= 30 time=00:00:10.00 bitrate=500kbits/s speed=1.0x",100);service.UpdateProgress("b","fps= 90 time=00:00:20.00 bitrate=900kbits/s speed=3.0x",100);service.CaptureNow();var rows=service.GetActive().ToDictionary(x=>x.Job.Id);EncodingDiagnosticSample first=rows["a"].Latest!;Assert.Equal(2,first.ConcurrentJobs);Assert.Equal(1,first.Speed);Assert.Equal(3,rows["b"].Latest!.Speed);Assert.NotNull(service.Cancel("a"));Assert.Single(service.GetActive());service.Start(Job("a"));service.CaptureNow();Assert.Equal(0,service.GetActive().Single(x=>x.Job.Id=="a").Latest!.Speed);
    }

    [Fact]
    public void MaintenanceOverlapAndSameDeviceAreStructured()
    {
        string path=Path.Combine(Path.GetTempPath(),"diagnostic-source.mkv");using var service=new EncodingDiagnosticsService(new FakeSystem(),TimeSpan.FromDays(1));service.Start(Job("a",path));LibraryMaintenanceActivity.Update(true,false,"Scanning",path);service.CaptureNow();EncodingDiagnosticSnapshot snapshot=Assert.Single(service.GetActive());Assert.True(snapshot.Latest!.MaintenanceActive);Assert.True(snapshot.Latest.SameDeviceMaintenance);LibraryMaintenanceActivity.Clear();EncodingDiagnosticSummary summary=service.Complete("a")!;Assert.Equal(1,summary.MaintenanceOverlapSeconds);Assert.Equal(1,summary.SameDeviceMaintenanceSeconds);Assert.Null(summary.StorageWaitSeconds);
    }

    [Fact]
    public void MaintenanceDeferredForEncodingIsReportedWithoutFalseContention()
    {
        string path="C:\\Media\\movie.mkv";using var service=new EncodingDiagnosticsService(new FakeSystem(),TimeSpan.FromDays(1));service.Start(Job("a",path));service.UpdateProgress("a","fps= 60 time=00:00:10.00 bitrate=1000kbits/s speed=2.0x",100);LibraryMaintenanceActivity.Defer("Quick Scrub deferred for active encoding",path);service.CaptureNow();EncodingDiagnosticSnapshot snapshot=Assert.Single(service.GetActive());Assert.False(snapshot.Latest!.MaintenanceActive);Assert.True(snapshot.Latest.MaintenanceDeferred);Assert.Contains("deferred",snapshot.Observation,StringComparison.OrdinalIgnoreCase);LibraryMaintenanceActivity.Update(false,false,"","");
    }

    [Fact]
    public void InterpretationRulesAreConservativeAndExplainable()
    {
        EncodingDiagnosticSample Sample(double speed,int jobs,double? encode,double? decode,bool maintenance=false,bool same=false,bool deferred=false)=>new(DateTime.UtcNow,speed,30,1000,10,10,jobs,SystemTelemetry with{GpuEncodePercent=encode,GpuDecodePercent=decode},maintenance,same,deferred,maintenance?"Scanning":"");
        Assert.Contains("storage contention",EncodingDiagnosticInterpreter.Interpret(Sample(.5,1,20,20,true,true)),StringComparison.OrdinalIgnoreCase);Assert.Contains("substantially utilized",EncodingDiagnosticInterpreter.Interpret(Sample(2,1,85,20)),StringComparison.OrdinalIgnoreCase);Assert.Contains("Decode pressure",EncodingDiagnosticInterpreter.Interpret(Sample(.7,1,20,80)),StringComparison.OrdinalIgnoreCase);Assert.Contains("concurrent",EncodingDiagnosticInterpreter.Interpret(Sample(2,2,50,50)),StringComparison.OrdinalIgnoreCase);Assert.Contains("unavailable",EncodingDiagnosticInterpreter.Interpret(Sample(2,1,null,null)),StringComparison.OrdinalIgnoreCase);Assert.Contains("deferred",EncodingDiagnosticInterpreter.Interpret(Sample(2,1,20,20,false,false,true)),StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClipboardSummaryOmitsFullSourcePathAndReportsUnsupportedGpu()
    {
        string secret=Path.Combine("C:\\Private","movie.mkv");using var service=new EncodingDiagnosticsService(new FakeSystem(),TimeSpan.FromDays(1));service.Start(Job("a",secret));service.CaptureNow();string text=service.FormatForClipboard(Assert.Single(service.GetActive()));Assert.DoesNotContain("C:\\Private",text,StringComparison.OrdinalIgnoreCase);Assert.Contains("movie.mkv",text);Assert.Contains("Unavailable",text);
    }

    [Fact]
    public void ExistingStatisticsJsonWithoutDiagnosticsRemainsCompatible()
    {
        string json="{\"schemaVersion\":2,\"id\":\"old\",\"startUtc\":\"2026-01-01T00:00:00Z\",\"endUtc\":\"2026-01-01T00:01:00Z\",\"outcome\":0,\"codec\":\"hevc\",\"encoder\":\"nvenc\",\"processingSeconds\":60}";EncodingStatisticsRecord record=JsonSerializer.Deserialize<EncodingStatisticsRecord>(json,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;Assert.Null(record.DiagnosticSummary);Assert.Equal("old",record.Id);
    }

    [Fact]
    public void CompletedSummaryRoundTripsThroughStatisticsAndHistory()
    {
        string root=Path.Combine(Path.GetTempPath(),"MediaFlux-DiagnosticPersistence",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);try{var summary=new EncodingDiagnosticSummary{AverageSpeed=2.5,MedianSpeed=2.4,Samples=10,Observation="No obvious bottleneck detected."};string statsPath=Path.Combine(root,"statistics.jsonl");var statistics=new EncodingStatisticsService(statsPath);Assert.True(statistics.AppendFinalized(new EncodingStatisticsRecord{Id="job",StartUtc=DateTime.UtcNow.AddMinutes(-1),EndUtc=DateTime.UtcNow,Outcome=EncodingStatisticsOutcome.Success,Codec="hevc",Encoder="nvenc",ProcessingSeconds=60,DiagnosticSummary=summary}));Assert.Equal(2.5,new EncodingStatisticsService(statsPath).GetAll().Single().DiagnosticSummary!.AverageSpeed);var history=new HistoryService(Path.Combine(root,"history.json"));history.Append(new JobHistoryRecord{Id="job",Type=JobType.Encode,Status=JobStatus.Success,StartUtc=DateTime.UtcNow.AddMinutes(-1),EndUtc=DateTime.UtcNow,DiagnosticSummary=summary});Assert.Equal(10,new HistoryService(Path.Combine(root,"history.json")).LoadAll().Single().DiagnosticSummary!.Samples);}finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    [Fact]
    public void InstrumentationCostIsNegligibleForThousandSamples()
    {
        using var service=new EncodingDiagnosticsService(new FakeSystem(),TimeSpan.FromDays(1));service.Start(Job("a"));var timer=Stopwatch.StartNew();for(int i=0;i<1000;i++){service.UpdateProgress("a","fps= 60 time=00:00:10.00 bitrate=1000kbits/s speed=2.0x",100);service.CaptureNow();}timer.Stop();Assert.True(timer.Elapsed<TimeSpan.FromSeconds(2),$"Sampling took {timer.Elapsed}.");
    }

    [Fact]
    public async Task DisposalWaitsForInFlightTelemetryAndLeavesNoActiveSessions()
    {
        var provider = new BlockingSystem();
        var service = new EncodingDiagnosticsService(provider, TimeSpan.FromDays(1));
        service.Start(Job("a"));
        Task sample = Task.Run(service.CaptureNow);
        Assert.True(provider.Entered.Wait(TimeSpan.FromSeconds(5)));

        Task dispose = Task.Run(service.Dispose);
        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);
        provider.Release.Set();

        await Task.WhenAll(sample, dispose);
        service.Start(Job("after-dispose"));
        Assert.Empty(service.GetActive());
        Assert.True(provider.Disposed);
    }

    private static EncodingDiagnosticJob Job(string id,string path="C:\\Media\\movie.mkv")=>new(id,"movie.mkv","NVENC","nvenc","hevc_nvenc","p5","1080p","1080p",10,100,path);
    private sealed class FakeSystem:IEncodingSystemTelemetryProvider{public EncodingSystemTelemetry Sample()=>SystemTelemetry;}
    private sealed class BlockingSystem:IEncodingSystemTelemetryProvider,IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public bool Disposed { get; private set; }
        public EncodingSystemTelemetry Sample(){Entered.Set();Release.Wait(TimeSpan.FromSeconds(5));if(Disposed)throw new ObjectDisposedException(nameof(BlockingSystem));return SystemTelemetry;}
        public void Dispose(){Disposed=true;Entered.Dispose();Release.Dispose();}
    }
}
