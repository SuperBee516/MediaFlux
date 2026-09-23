using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class QueueSelectionResponsivenessUiTests
{
    [Fact]
    public void SelectingRowsWithMissingMetadataUsesRetainedValuesAndRefreshesWhenMetadataArrives()
    {
        if (!OperatingSystem.IsWindows()) return;

        Exception? failure = null;
        MainForm? main = null;
        string? isolatedConfigPath = null;
        string? tempDirectory = null;
        MediaInfoService? mediaInfoService = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                isolatedConfigPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                mediaInfoService = Field<MediaInfoService>(main, "_mediaInfoService");
                SetField(main, "_mediaInfoService", null);
                Field<ComboBox>(main, "comboOutputContainer").SelectedIndex = 1;

                tempDirectory = Path.Combine(Path.GetTempPath(), $"MediaFlux.QueueSelection.{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDirectory);
                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow[] rows = Enumerable.Range(0, 3)
                    .Select(_ => queue.Rows[queue.Rows.Add()])
                    .ToArray();
                SetField(main, "_suppressRowEvents", false);

                (string Name, double Duration, string Resolution, int Fps, string LengthText, string DimensionsText, string FpsText)[] cases =
                [
                    ("missing-duration.mkv", 0, "1920x1080", 24, "--", "1920 × 1080", "24 frames/second"),
                    ("missing-resolution.mkv", 1200, "", 24, "00:20:00", "--", "24 frames/second"),
                    ("missing-fps.mkv", 1200, "1920x1080", 0, "00:20:00", "1920 × 1080", "--")
                ];

                var estimates = Field<Dictionary<string, double>>(main, "_estimatedSizeMap");
                object[] metas = new object[rows.Length];
                for (int index = 0; index < rows.Length; index++)
                {
                    string path = MakeFile(tempDirectory, cases[index].Name);
                    DataGridViewRow row = rows[index];
                    row.Cells["colName"].Value = cases[index].Name;
                    row.Cells["colSize"].Value = "1 MB";
                    row.Cells["colEstimatedSize"].Value = "0.5 MB";
                    row.Cells["colProgress"].Value = "0%";
                    row.Cells["colETA"].Value = "--:--:--";
                    row.Cells["colStatus"].Value = "Queued";
                    object meta = Invoke(main, "EnsureRowMeta", row)!;
                    SetField(meta, "Path", path);
                    SetField(meta, "SrcMb", 1d);
                    SetField(meta, "DurationSec", cases[index].Duration);
                    SetField(meta, "Resolution", cases[index].Resolution);
                    SetField(meta, "Fps", cases[index].Fps);
                    SetField(meta, "CustomTargetMb", 1d); // Prevents the separate advisory-quality request.
                    estimates[path] = 0.5;
                    metas[index] = meta;
                }

                Dictionary<string, Label> preview = Field<Dictionary<string, Label>>(main, "_previewValueLabels");
                Button startSelected = Field<Button>(main, "_btnStartSelectedQueue");
                Button removeSelected = Field<Button>(main, "_btnRemoveSelectedQueue");
                for (int index = 0; index < rows.Length; index++)
                {
                    queue.ClearSelection();
                    rows[index].Selected = true;
                    queue.CurrentCell = rows[index].Cells["colName"];
                    Application.DoEvents();

                    Assert.Equal(cases[index].LengthText, preview["Length"].Text);
                    Assert.Equal(cases[index].DimensionsText, preview["Dimensions"].Text);
                    Assert.Equal(cases[index].FpsText, preview["Frame rate"].Text);
                    Assert.Null(metas[index].GetType().GetField("IntelligencePlan")?.GetValue(metas[index]));
                    Assert.Equal(0.5, estimates[Field<string>(metas[index], "Path")]);
                    Assert.True(startSelected.Enabled);
                    Assert.True(removeSelected.Enabled);
                }

                // Re-selecting a row and receiving later estimate metadata must remain
                // cache-only while refreshing the visible preview values.
                queue.ClearSelection();
                rows[0].Selected = true;
                queue.CurrentCell = rows[0].Cells["colName"];
                SetField(metas[0], "DurationSec", 1800d);
                Invoke(main, "UpdateQueueSelectionPreview");
                Assert.Equal("00:30:00", preview["Length"].Text);

                DateTime frozenDecisionUtc = DateTime.UnixEpoch;
                var plan = new EncodingPlan
                {
                    IsAvailable = true,
                    Source = new("h264", 1920, 1080, 24, 1800)
                    {
                        SizeBytes = 1024,
                        BitrateKbps = 2400
                    },
                    Estimates = new(null, null, null),
                    SizePredictionCalibration = EncodingSizePredictionCalibration.Unavailable(
                        4, "Frozen selection plan", "SelectionResponsivenessTest", frozenDecisionUtc)
                };
                SetField(metas[0], "IntelligencePlan", plan);
                Invoke(main, "UpdateQueueSelectionPreview");
                Assert.Same(plan, Field<EncodingPlan?>(metas[0], "IntelligencePlan"));
                Assert.Equal(frozenDecisionUtc, plan.SizePredictionCalibration?.DecisionUtc);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (main != null && mediaInfoService != null)
                    SetField(main, "_mediaInfoService", mediaInfoService);
                WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(isolatedConfigPath);
                if (!string.IsNullOrWhiteSpace(tempDirectory) && Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Queue selection responsiveness UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void CachedMediaSnapshotDoesNotValidateTheFileOnTheCallingThread()
    {
        var service = new MediaInfoService(persistentCacheEnabled: false);
        string missingPath = Path.Combine(Path.GetTempPath(), $"MediaFlux.Missing.{Guid.NewGuid():N}.mkv");
        var source = new MediaInfoService.MediaInfo
        {
            FormatName = "matroska,webm",
            Width = 1280,
            Height = 720,
            Fps = 23.976,
            DurationSeconds = 90
        };

        Type entryType = typeof(MediaInfoService).GetNestedType("CacheEntry", BindingFlags.NonPublic)
            ?? throw new MissingMemberException("MediaInfoService.CacheEntry");
        object entry = Activator.CreateInstance(
            entryType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [source, 123L, DateTime.UnixEpoch],
            culture: null) ?? throw new InvalidOperationException("Could not create cached media entry.");
        object cache = typeof(MediaInfoService).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        MethodInfo tryAdd = cache.GetType().GetMethod("TryAdd", [typeof(string), entryType])
            ?? throw new MissingMethodException("Cached media TryAdd");
        Assert.True((bool)tryAdd.Invoke(cache, [missingPath, entry])!);

        Assert.True(service.TryGetCachedInfoSnapshot(missingPath, out MediaInfoService.MediaInfo snapshot));
        Assert.Equal(1280, snapshot.Width);
        Assert.Equal(720, snapshot.Height);
        Assert.Equal(90, snapshot.DurationSeconds);
        Assert.False(service.TryGetCachedInfo(missingPath, out _));
    }

    [Fact]
    public void RepeatedQualityPreviewRequestForTheSameRowAndGenerationIsDeduplicated()
    {
        Type rowMetaType = typeof(MainForm).GetNestedType("RowMeta", BindingFlags.NonPublic)
            ?? throw new MissingMemberException("MainForm.RowMeta");
        object meta = Activator.CreateInstance(rowMetaType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create queue row metadata.");
        MethodInfo begin = typeof(MainForm).GetMethod(
            "TryBeginQualityPreviewRequest",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("TryBeginQualityPreviewRequest");
        MethodInfo end = typeof(MainForm).GetMethod(
            "EndQualityPreviewRequest",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("EndQualityPreviewRequest");

        Assert.True((bool)begin.Invoke(null, [meta, 4])!);
        Assert.False((bool)begin.Invoke(null, [meta, 4])!);

        // A new configuration generation may replace a stale request; completion
        // of the old request must not clear the newer request's ownership marker.
        Assert.True((bool)begin.Invoke(null, [meta, 5])!);
        end.Invoke(null, [meta, 4]);
        Assert.False((bool)begin.Invoke(null, [meta, 5])!);
        end.Invoke(null, [meta, 5]);
        Assert.True((bool)begin.Invoke(null, [meta, 5])!);
    }

    private static string MakeFile(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [0x00, 0x01, 0x02]);
        return path;
    }

    private static T Field<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target)
            ?? throw new MissingFieldException(target.GetType().Name, name));

    private static void SetField(object target, string name, object? value) =>
        (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().Name, name)).SetValue(target, value);

    private static object? Invoke(object target, string method, params object?[] arguments)
    {
        MethodInfo methodInfo = target.GetType().GetMethod(
            method,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().Name, method);
        return methodInfo.Invoke(target, arguments);
    }
}
