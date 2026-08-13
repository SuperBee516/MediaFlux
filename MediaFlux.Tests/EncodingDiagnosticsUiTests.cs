using MediaFlux.Services;
using System.Windows.Forms;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class EncodingDiagnosticsUiTests
{
    [Fact]
    public void DiagnosticsPanelRefreshesAndStopsTimerWhenDisposed()
    {
        if(!OperatingSystem.IsWindows())return;Exception? failure=null;var thread=new Thread(()=>{try{SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());using var service=new EncodingDiagnosticsService(new FakeSystem(),TimeSpan.FromDays(1));service.Start(new("job","movie.mkv","NVENC","nvenc","hevc_nvenc","p5","1080p","1080p",10,100,"C:\\Media\\movie.mkv"));service.UpdateProgress("job","fps= 60 time=00:00:10.00 bitrate=1000kbits/s speed=2.0x",100);service.CaptureNow();var panel=new EncodingDiagnosticsPanel(service,_=>{});panel.CreateControl();panel.RefreshNow();Assert.Equal(1,panel.VisibleSessionCount);Assert.True(panel.IsRefreshTimerEnabled);panel.Dispose();Assert.False(panel.IsRefreshTimerEnabled);}catch(Exception ex){failure=ex;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(20)),"Diagnostics UI smoke test timed out.");if(failure!=null)throw new Xunit.Sdk.XunitException(failure.ToString());
    }
    private sealed class FakeSystem:IEncodingSystemTelemetryProvider{public EncodingSystemTelemetry Sample()=>new(20,5,1L<<30,100L<<20,null,null,null,null,"Unavailable");}
}
