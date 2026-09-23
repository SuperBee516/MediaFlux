using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class QueueInspectorUiTests
{
    [Fact]
    public void FourTabInspectorKeepsSelectionSummaryAndFrozenPlanStableAtSupportedSizes()
    {
        if (!OperatingSystem.IsWindows()) return;

        Exception? failure = null;
        MainForm? main = null;
        string? isolatedConfigPath = null;
        string? tempDirectory = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                isolatedConfigPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.StartPosition = FormStartPosition.Manual;
                main.Show();
                Application.DoEvents();

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow first = queue.Rows[queue.Rows.Add()];
                DataGridViewRow second = queue.Rows[queue.Rows.Add()];
                SetField(main, "_suppressRowEvents", false);

                tempDirectory = Path.Combine(Path.GetTempPath(), $"MediaFlux.QueueInspector.{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDirectory);
                string firstPath = MakeFile(tempDirectory, "first.mkv", 2);
                string secondPath = MakeFile(tempDirectory, "second.mp4", 3);
                object firstMeta = PrepareRow(main, first, firstPath, "Encoding", 20, 8);
                object secondMeta = PrepareRow(main, second, secondPath, "Queued", 30, 22);
                EncodingPlan plan = MakePlan(firstPath);
                firstMeta.GetType().GetField("IntelligencePlan")!.SetValue(firstMeta, plan);
                firstMeta.GetType().GetField("IntelligenceOutcome")!.SetValue(
                    firstMeta,
                    new EncodingExecutionOutcome(plan.PlanId, [], [], TerminalResult: EncodingTerminalResult.CompletedAfterRecovery));
                firstMeta.GetType().GetField("CuratedFailureDiagnosticReport")!.SetValue(firstMeta, "Curated report line: recovered source validated.");
                Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[firstPath] = 8;
                Field<ConcurrentDictionary<DataGridViewRow, string>>(main, "_runningEncodeJobs")[first] = firstPath;
                Invoke(main, "RefreshQueueWorkspaceRow", first);
                Invoke(main, "UpdateContextualDetails");

                TabControl tabs = Field<TabControl>(main, "_encodeInfoTabs");
                Assert.Equal(new[] { "Summary", "Plan & Analysis", "Media", "Diagnostics & Logs" },
                    tabs.TabPages.Cast<TabPage>().Select(page => page.Text));
                Assert.True(tabs.TabStop);
                Assert.Equal(tabs.TabPages.Cast<TabPage>().Select(page => page.Text),
                    tabs.TabPages.Cast<TabPage>().Select(page => page.AccessibleName));

                first.Selected = true;
                queue.CurrentCell = first.Cells["colName"];
                Invoke(main, "RefreshQueueInspectorFromSelection");
                Label summaryTitle = Field<Label>(main, "_queueInspectorSummaryTitle");
                Assert.Equal("first.mkv", summaryTitle.Text);
                Assert.Equal("Encoding", Field<Label>(main, "_queueInspectorStatus").Text);
                Assert.Equal("Strong candidate", Field<Label>(main, "_queueInspectorRecommendation").Text);
                Assert.Equal("High", Field<Label>(main, "_queueInspectorConfidence").Text);
                Assert.Equal(FormatSize(20), Field<Label>(main, "_queueInspectorSourceSize").Text);
                Assert.Equal(FormatSize(8), Field<Label>(main, "_queueInspectorEstimate").Text);
                Assert.Contains("Expected savings", Field<Label>(main, "_queueInspectorRationale").Text);
                Assert.Contains("hevc_nvenc", Field<Label>(main, "_queueInspectorOutput").Text);

                Invoke(main, "ScheduleEncodingPlanRefresh");
                TableLayoutPanel planTable = Field<TableLayoutPanel>(main, "_encodingPlanTable");
                Assert.NotEmpty(planTable.Controls.Cast<Control>());
                Control retainedPlanControl = planTable.Controls[0];
                DateTime frozenDecisionUtc = Assert.IsType<DateTime>(plan.SizePredictionCalibration?.DecisionUtc);

                first.Cells["colProgress"].Value = "42%";
                first.Cells["colETA"].Value = "00:03:10";
                Invoke(main, "RefreshQueueInspectorForRow", first, true);
                Assert.Equal("42%", Field<Label>(main, "_queueInspectorProgress").Text);
                Assert.Equal("00:03:10", Field<Label>(main, "_queueInspectorEta").Text);
                Assert.Same(retainedPlanControl, planTable.Controls[0]);
                Assert.Equal(frozenDecisionUtc, plan.SizePredictionCalibration?.DecisionUtc);

                foreach (string tabName in new[] { "Media", "Diagnostics & Logs", "Summary", "Plan & Analysis" })
                {
                    tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == tabName);
                    Application.DoEvents();
                    Assert.Same(retainedPlanControl, planTable.Controls[0]);
                    Assert.Equal(frozenDecisionUtc, plan.SizePredictionCalibration?.DecisionUtc);
                }

                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Diagnostics & Logs");
                Assert.Contains("Curated report line", Field<TextBox>(main, "_queueInspectorDiagnostics").Text);
                Assert.Contains("CompletedAfterRecovery", Field<TextBox>(main, "_queueInspectorDiagnostics").Text);
                firstMeta.GetType().GetMethod("AppendInspectorLogLine")!.Invoke(firstMeta, ["ffmpeg frame=128 time=00:00:05.12 speed=1.2x"]);
                first.Cells["colProgress"].Value = "43%";
                Application.DoEvents();
                Assert.Contains("ffmpeg frame=128", Field<TextBox>(main, "_queueInspectorDiagnostics").Text);

                // Same queue identity survives a row rebind; row-object identity is not the key.
                string originalIdentity = (string)Invoke(main, "BuildQueueInspectorSelectionKey", new object?[] { new[] { first } })!;
                DataGridViewRow rebound = new() { Tag = firstMeta };
                string reboundIdentity = (string)Invoke(main, "BuildQueueInspectorSelectionKey", new object?[] { new[] { rebound } })!;
                Assert.Equal(originalIdentity, reboundIdentity);

                first.Selected = false;
                second.Selected = true;
                queue.CurrentCell = second.Cells["colName"];
                Invoke(main, "RefreshQueueInspectorFromSelection");
                Assert.Equal("second.mp4", summaryTitle.Text);

                second.Selected = false;
                queue.ClearSelection();
                Invoke(main, "RefreshQueueInspectorFromSelection");
                Assert.Equal("No item selected", Field<Label>(main, "_queueInspectorStatus").Text);
                Assert.Contains("Select one queue item", Field<TextBox>(main, "_queueInspectorMedia").Text);
                Assert.Contains("Select a queue item", Field<TextBox>(main, "_queueInspectorDiagnostics").Text);

                first.Selected = true;
                second.Selected = true;
                Invoke(main, "RefreshQueueInspectorFromSelection");
                Assert.Contains("2 queue items selected", summaryTitle.Text);
                Assert.Contains("Multiple sources", Field<Label>(main, "_queueInspectorPath").Text);
                Assert.Contains("one selected item at a time", Field<TextBox>(main, "_queueInspectorMedia").Text);
                Assert.DoesNotContain(firstPath, Field<TextBox>(main, "_queueInspectorMedia").Text);
                queue.Rows.RemoveAt(second.Index);
                Application.DoEvents();
                Assert.DoesNotContain("second.mp4", summaryTitle.Text);

                Config config = Field<Config>(main, "_config");
                config.EncodeDetailsTab = "Streams";
                Invoke(main, "ApplyRememberedEncodeDetailsState");
                Assert.Equal("Media", tabs.SelectedTab?.Text);
                config.EncodeInfoHeight = int.MaxValue;
                foreach (Size size in new[] { new Size(1100, 700), new Size(1360, 840), new Size(1800, 1100) })
                {
                    main.ClientSize = size;
                    main.PerformLayout();
                    Application.DoEvents();
                    SplitContainer splitter = Field<SplitContainer>(main, "_encodeQueueSplit");
                    Invoke(main, "ApplyEncodeInfoSplitterState");
                    Assert.Equal(size, main.ClientSize);
                    Assert.True(queue.ClientSize.Height >= 24, $"Queue grid should remain usable at {size}.");
                    Assert.True(tabs.ClientSize.Width > 0 && tabs.ClientSize.Height > 0, $"Inspector tabs should remain available at {size}.");
                    int maximumDistance = splitter.Height - splitter.SplitterWidth - splitter.Panel2MinSize;
                    Assert.InRange(splitter.SplitterDistance, splitter.Panel1MinSize, Math.Max(splitter.Panel1MinSize, maximumDistance));
                }

                config.EncodeInfoHeight = 260;
                SplitContainer encodeSplit = Field<SplitContainer>(main, "_encodeQueueSplit");
                int available = Math.Max(0, encodeSplit.ClientSize.Height - encodeSplit.SplitterWidth);
                int adjustedDistance = Math.Clamp(encodeSplit.SplitterDistance - 20, 0, Math.Max(0, available));
                encodeSplit.SplitterDistance = adjustedDistance;
                Invoke(main, "EncodeQueueSplit_SplitterMoved", encodeSplit,
                    new SplitterEventArgs(0, encodeSplit.SplitterDistance, 0, encodeSplit.SplitterDistance));
                Assert.True(Config.Load(isolatedConfigPath!).EncodeInfoHeight > 0);

                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Diagnostics & Logs");
                Assert.Equal("Diagnostics & Logs", Config.Load(isolatedConfigPath!).EncodeDetailsTab);
                Assert.Contains("diagnostic",
                    Field<Button>(main, "_queueInspectorCopyDiagnostic").AccessibleName ?? "",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(isolatedConfigPath);
                if (!string.IsNullOrWhiteSpace(tempDirectory) && Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Queue inspector UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void MediaTabUsesCachedMetadataAndFrozenStreamProjectionWithoutProbing()
    {
        if (!OperatingSystem.IsWindows()) return;

        Exception? failure = null;
        MainForm? main = null;
        string? isolatedConfigPath = null;
        string? tempDirectory = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                isolatedConfigPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                tempDirectory = Path.Combine(Path.GetTempPath(), $"MediaFlux.QueueInspectorMedia.{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDirectory);
                string path = MakeFile(tempDirectory, "cached.mkv", 1);
                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow row = queue.Rows[queue.Rows.Add()];
                SetField(main, "_suppressRowEvents", false);
                object meta = PrepareRow(main, row, path, "Encoding", 10, 5);
                var plan = new EncodingPlan
                {
                    IsAvailable = true,
                    Source = new("h264", 1920, 1080, 23.976, 1200)
                    {
                        SizeBytes = new FileInfo(path).Length,
                        BitrateKbps = 2400
                    },
                    Audio = [new EncodingPlanStream(1, "audio", "aac", StreamCompatibilityAction.Copy, null, "Compatible; copied", 2)],
                    Subtitles = [new EncodingPlanStream(2, "subtitle", "subrip", StreamCompatibilityAction.Copy, null, "Compatible; copied")],
                    Estimates = new(null, null, null),
                    SizePredictionCalibration = EncodingSizePredictionCalibration.Unavailable(
                        5, "Frozen decision", "PredictionCalibrationPolicyV1", DateTime.UnixEpoch)
                };
                meta.GetType().GetField("IntelligencePlan")!.SetValue(meta, plan);

                MediaInfoService service = Field<MediaInfoService>(main, "_mediaInfoService");
                var cached = new MediaInfoService.MediaInfo
                {
                    FormatName = "matroska,webm",
                    VideoCodec = "h264",
                    Width = 1920,
                    Height = 1080,
                    Fps = 23.976,
                    DurationSeconds = 1200,
                    BitrateKbps = 2400,
                    TotalBitrateKbps = 2800,
                    AudioStreamCount = 2,
                    AudioBitrateKbps = 320,
                    SubtitleStreamCount = 1,
                    SubtitleBitrateKbps = 0
                };
                AddCachedInfo(service, path, cached);
                row.Selected = true;
                queue.CurrentCell = row.Cells["colName"];

                TabControl tabs = Field<TabControl>(main, "_encodeInfoTabs");
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Media");
                Application.DoEvents();
                TextBox media = Field<TextBox>(main, "_queueInspectorMedia");
                Assert.Contains("Container: matroska,webm", media.Text);
                Assert.Contains("1920×1080", media.Text);
                Assert.Contains("#1: aac", media.Text);
                Assert.Contains("#2: subrip", media.Text);
                Assert.DoesNotContain("not cached yet", media.Text, StringComparison.OrdinalIgnoreCase);

                // Ownership is intentionally active and no cache exists for this input.
                // The cache-only inspector still renders a safe unavailable state without
                // asking MediaInfoService to fill the miss.
                string uncachedPath = MakeFile(tempDirectory, "active-no-cache.mkv", 1);
                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow uncachedRow = queue.Rows[queue.Rows.Add()];
                SetField(main, "_suppressRowEvents", false);
                object uncachedMeta = PrepareRow(main, uncachedRow, uncachedPath, "Attempting source recovery…", 10, 5);
                Field<ConcurrentDictionary<DataGridViewRow, string>>(main, "_runningEncodeJobs")[uncachedRow] = uncachedPath;
                uncachedRow.Selected = true;
                row.Selected = false;
                queue.CurrentCell = uncachedRow.Cells["colName"];
                Invoke(main, "RefreshQueueInspectorFromSelection");
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Media");
                Assert.Contains("Not cached", media.Text);
                Assert.Equal("Attempting source recovery…", Field<Label>(main, "_queueInspectorStatus").Text);
                Assert.Contains("Historical profile estimate", Field<Label>(main, "_queueInspectorProvenance").Text);
                Assert.NotNull(uncachedMeta);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(isolatedConfigPath);
                if (!string.IsNullOrWhiteSpace(tempDirectory) && Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Queue inspector media test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static object PrepareRow(MainForm main, DataGridViewRow row, string path, string status, double sourceMb, double estimateMb)
    {
        row.Cells["colName"].Value = Path.GetFileName(path);
        row.Cells["colSize"].Value = FormatSize(sourceMb);
        row.Cells["colEstimatedSize"].Value = FormatSize(estimateMb);
        row.Cells["colProgress"].Value = "0%";
        row.Cells["colETA"].Value = "--:--:--";
        row.Cells["colStatus"].Value = status;
        object meta = Invoke(main, "EnsureRowMeta", row)!;
        meta.GetType().GetField("Path")!.SetValue(meta, path);
        meta.GetType().GetField("SrcMb")!.SetValue(meta, sourceMb);
        meta.GetType().GetField("Resolution")!.SetValue(meta, "1920x1080");
        meta.GetType().GetField("VideoCodec")!.SetValue(meta, "h264");
        meta.GetType().GetField("Fps")!.SetValue(meta, 24);
        meta.GetType().GetField("DurationSec")!.SetValue(meta, 1200d);
        meta.GetType().GetField("EstimateDiagnostic")!.SetValue(meta, "Historical profile estimate; no source transcode was performed.");
        meta.GetType().GetField("EncodeRecommendation")!.SetValue(meta, Recommendation());
        return meta;
    }

    private static EncodingPlan MakePlan(string path) => new()
    {
        IsAvailable = true,
        Source = new("h264", 1920, 1080, 24, 1200)
        {
            SizeBytes = new FileInfo(path).Length,
            BitrateKbps = 2400
        },
        Video = new("Reencode", "hevc_nvenc", "nvenc", 1920, 1080, 1920, 1080, "yuv420p"),
        Hardware = new(true, "nvenc", true),
        Estimates = new(null, null, null),
        SizePredictionCalibration = EncodingSizePredictionCalibration.Unavailable(
            8, "Frozen inspector test decision.", "PredictionCalibrationPolicyV1", DateTime.UnixEpoch)
    };

    private static SmartEncodeRecommendation Recommendation() => new()
    {
        Kind = SmartEncodeRecommendationKind.StrongCandidate,
        Confidence = SmartEncodeConfidence.High,
        EstimatedSavingsPercent = 60,
        EstimatedSavingsMb = 12,
        PrimaryReason = "Expected savings with acceptable quality risk.",
        Reasons = ["Expected savings with acceptable quality risk."]
    };

    private static string MakeFile(string directory, string name, int sizeMb)
    {
        string path = Path.Combine(directory, name);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.SetLength(sizeMb * 1024L * 1024L);
        return path;
    }

    private static void AddCachedInfo(MediaInfoService service, string path, MediaInfoService.MediaInfo info)
    {
        FileInfo file = new(path);
        Type entryType = typeof(MediaInfoService).GetNestedType("CacheEntry", BindingFlags.NonPublic)
            ?? throw new MissingMemberException("MediaInfoService.CacheEntry");
        object entry = Activator.CreateInstance(
            entryType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [info, file.Length, file.LastWriteTimeUtc],
            culture: null) ?? throw new InvalidOperationException("Could not create cached media entry.");
        object cache = typeof(MediaInfoService).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        MethodInfo tryAdd = cache.GetType().GetMethod("TryAdd", [typeof(string), entryType])
            ?? throw new MissingMethodException("Cached media TryAdd");
        Assert.True((bool)tryAdd.Invoke(cache, [path, entry])!);
    }

    private static string FormatSize(double megabytes) => megabytes >= 1024
        ? $"{megabytes / 1024d:0.##} GB"
        : $"{megabytes:0.#} MB";

    private static T Field<T>(MainForm main, string name) =>
        (T)(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
            ?? throw new MissingFieldException(name));

    private static void SetField(MainForm main, string name, object value) =>
        (typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name)).SetValue(main, value);

    private static object? Invoke(MainForm main, string name, params object?[] args)
    {
        MethodInfo method = typeof(MainForm).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length)
            ?? throw new MissingMethodException(name);
        return method.Invoke(main, args);
    }
}
