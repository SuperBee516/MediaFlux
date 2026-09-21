using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiRuntimeDashboardUiTests
{
    [Fact]
    public void DashboardHasDistinctCurrentAndLastSessionAreasAtMinimumSize()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            AiRuntimeDashboardForm? form = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                string database = Path.Combine(Path.GetTempPath(), "MediaFluxAiDashboardTests", Guid.NewGuid() + ".db");
                var telemetry = new AiRuntimeTelemetryService(new AiBenchmarkDatabase(database));
                form = new AiRuntimeDashboardForm(telemetry, new AiHealthService(telemetry, () => null, () => null, () => true), new Config());
                form.Show(); Application.DoEvents();
                Assert.True(form.ClientSize.Width > 0 && form.ClientSize.Height > 0);
                Assert.NotEmpty(form.Controls.Find("dashboardSummaryCards", true));
                Assert.NotEmpty(form.Controls.Find("dashboardDetailCardLastSession", true));
                Assert.NotEmpty(form.Controls.Find("dashboardRefreshButton", true));
                Assert.True(form.Controls.Find("dashboardDetailGrid", true).Single().Bounds.Width > 0);
                foreach (Size size in new[] { new Size(1100, 700), new Size(1360, 840), new Size(1800, 1100), form.MinimumSize })
                {
                    form.Size = size; Application.DoEvents();
                    TableLayoutPanel summary = form.Controls.Find("dashboardSummaryCards", true).OfType<TableLayoutPanel>().Single();
                    Assert.Equal(4, summary.Controls.Count);
                    foreach (TableLayoutPanel card in summary.Controls.OfType<TableLayoutPanel>())
                    {
                        Assert.True(card.Width > 0 && card.Height > 0);
                        Assert.True(new Rectangle(Point.Empty, card.ClientSize).Contains(card.Controls[0].Bounds));
                        Assert.True(new Rectangle(Point.Empty, card.ClientSize).Contains(card.Controls[1].Bounds));
                        Assert.True(new Rectangle(Point.Empty, card.ClientSize).Contains(card.Controls[2].Bounds));
                    }
                    TableLayoutPanel details = form.Controls.Find("dashboardDetailGrid", true).OfType<TableLayoutPanel>().Single();
                    foreach (Control card in details.Controls) Assert.True(card.Width > 0 && card.Height > 0 && new Rectangle(Point.Empty, details.ClientSize).Contains(card.Bounds), $"{card.Name} {card.Bounds} / {details.ClientSize}");
                }
                var model = new AiRestorationModel("general", "General", AiRestorationMode.General, new[] { AiRestorationScale.X2 }, "models", "model.param", "model.bin", "ncnn-vulkan", "general-x2");
                var session = new AiRestorationSession(new(true, "ncnn-vulkan", "backend.exe", "identity", true, new[] { "GPU" }, new[] { model }, null), model);
                telemetry.Begin(session, new VideoRestorationSettings { AiMode = AiRestorationMode.General, AiScale = AiRestorationScale.X2 }, 120, 1920, 1080, new HardwareSnapshot("CPU", 8, "GPU", "Driver", 8L * 1024 * 1024 * 1024, null, "C:", "C:", "C:", "Windows", "FFmpeg"));
                Application.DoEvents();
                TableLayoutPanel activeDetails = form.Controls.Find("dashboardDetailGrid", true).OfType<TableLayoutPanel>().Single();
                Control liveCard = form.Controls.Find("dashboardDetailCardLivePerformance", true).Single();
                Assert.True(liveCard.Height > 0 && new Rectangle(Point.Empty, activeDetails.ClientSize).Contains(liveCard.Bounds));
                telemetry.Complete(); Application.DoEvents();
                Control lastCard = form.Controls.Find("dashboardDetailCardLastSession", true).Single();
                Assert.True(lastCard.Controls.Count >= 8);
            }
            catch (Exception ex) { failure = ex; }
            finally { WinFormsTestLifecycle.CloseAndDispose(form); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The dashboard UI test timed out.");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
