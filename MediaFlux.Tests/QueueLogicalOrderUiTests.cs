using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class QueueLogicalOrderUiTests
{
    [Fact]
    public void LogicalOrderIgnoresPresentationSortVisibilityAndMetadataRefresh()
    {
        if (!OperatingSystem.IsWindows()) return;

        Exception? failure = null;
        MainForm? main = null;
        string? isolatedConfigPath = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                isolatedConfigPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow[] rows = [
                    AddRow(queue, "z-logical.mkv", "Queued"),
                    AddRow(queue, "a-visual.mkv", "Failed"),
                    AddRow(queue, "m-duplicate.mkv", "Queued"),
                    AddRow(queue, "b-selected.mkv", "Encoding")
                ];
                SetField(main, "_suppressRowEvents", false);

                foreach (DataGridViewRow row in rows)
                    Invoke(main, "EnsureRowMeta", row);

                long[] originalSequences = rows.Select(row => QueueSequence(main, row)).ToArray();
                SetMetaField(main, rows[2], "ExcludedFromEncodeAsDuplicate", true);
                rows[1].Visible = false;

                queue.Sort(queue.Columns["colName"], ListSortDirection.Ascending);
                Assert.Equal(
                    new[] { "z-logical.mkv", "a-visual.mkv", "m-duplicate.mkv", "b-selected.mkv" },
                    Names(InvokeRows(main, "GetEncodeRowsInExecutionOrder")));
                Assert.Equal(
                    new[] { "z-logical.mkv", "a-visual.mkv", "b-selected.mkv" },
                    Names(InvokeRows(main, "GetEligibleEncodeRowsInExecutionOrder")));
                Assert.Equal(4, InvokeRows(main, "GetEncodeRowsInExecutionOrder").Count);

                queue.Sort(queue.Columns["colStatus"], ListSortDirection.Descending);
                Assert.Equal(
                    new[] { "z-logical.mkv", "a-visual.mkv", "m-duplicate.mkv", "b-selected.mkv" },
                    Names(InvokeRows(main, "GetEncodeRowsInExecutionOrder")));

                queue.ClearSelection();
                rows[1].Selected = true;
                rows[3].Selected = true;
                Assert.Equal(
                    new[] { "a-visual.mkv", "b-selected.mkv" },
                    Names(InvokeRows(main, "GetSelectedEncodeRowsInExecutionOrder")));
                var selectedScope = EncodingScopeResolver.Analyze(
                    InvokeRows(main, "GetEligibleEncodeRowsInExecutionOrder"),
                    InvokeRows(main, "GetSelectedEncodeRowsInExecutionOrder"));
                Assert.Equal(
                    new[] { "a-visual.mkv", "b-selected.mkv" },
                    Names(selectedScope.Resolve(EncodingScopeChoice.Selected)!));

                SetMetaField(main, rows[0], "EstimateDiagnostic", "refreshed");
                SetMetaField(main, rows[0], "EncodeRecommendation", new SmartEncodeRecommendation
                {
                    Kind = SmartEncodeRecommendationKind.StrongCandidate,
                    Confidence = SmartEncodeConfidence.High,
                    PrimaryReason = "Queue order test refresh."
                });
                SetMetaField(main, rows[0], "QueueSequence", QueueSequence(main, rows[0]));
                Invoke(main, "EnsureRowMeta", rows[0]);
                Assert.Equal(originalSequences[0], QueueSequence(main, rows[0]));

                Assert.Equal(
                    new[] { "z-logical.mkv", "a-visual.mkv", "m-duplicate.mkv", "b-selected.mkv" },
                    ExportedPaths(main));
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(isolatedConfigPath);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Queue logical order UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void ImportedItemsAppendAfterExistingLogicalItemsInSerializedOrder()
    {
        if (!OperatingSystem.IsWindows()) return;

        Exception? failure = null;
        MainForm? main = null;
        string? isolatedConfigPath = null;
        string? tempRoot = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                isolatedConfigPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                tempRoot = Path.Combine(Path.GetTempPath(), $"MediaFlux.QueueOrder.{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempRoot);
                string[] existing = CreateFiles(tempRoot, "existing-1.mkv", "existing-2.mkv");
                string[] imported = CreateFiles(tempRoot, "imported-2.mkv", "imported-1.mkv");

                foreach (string path in existing)
                    Assert.True((bool)Invoke(main, "AddEncodeItemIfNotPresent", path, false, false)!);

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                queue.Sort(queue.Columns["colName"], ListSortDirection.Ascending);
                foreach (string path in imported)
                    Assert.True((bool)Invoke(main, "AddEncodeItemIfNotPresent", path, false, false)!);

                Assert.Equal(
                    existing.Concat(imported).ToArray(),
                    InvokeRows(main, "GetEncodeRowsInExecutionOrder")
                        .Select(row => (string)Invoke(main, "GetPathFromRow", row)!)
                        .ToArray());
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(isolatedConfigPath);
                if (!string.IsNullOrWhiteSpace(tempRoot) && Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Queue import order UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void ExplicitReorderUsesLogicalOrderAcrossSortSearchAndDuplicateExclusion()
    {
        if (!OperatingSystem.IsWindows()) return;

        Exception? failure = null;
        MainForm? main = null;
        string? isolatedConfigPath = null;
        string? tempRoot = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                isolatedConfigPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                tempRoot = Path.Combine(Path.GetTempPath(), $"MediaFlux.QueueReorder.{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempRoot);
                string[] paths = CreateFiles(tempRoot, "d.mkv", "b.mkv", "c.mkv", "a.mkv");
                foreach (string path in paths)
                    Assert.True((bool)Invoke(main, "AddEncodeItemIfNotPresent", path, false, false)!);

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                DataGridViewRow[] rows = InvokeRows(main, "GetEncodeRowsInExecutionOrder").ToArray();
                SetMetaField(main, rows[1], "ExcludedFromEncodeAsDuplicate", true);

                queue.Sort(queue.Columns["colName"], ListSortDirection.Descending);
                TextBox search = Field<TextBox>(main, "_queueWorkspaceSearchBox");
                search.Text = "c.mkv";
                Assert.Equal(1, rows.Count(row => row.Visible));

                object result = Invoke(
                    main,
                    "ReorderQueueRows",
                    new[] { rows[3] },
                    QueueExecutionOrderOperation.MoveToTop)!;
                Assert.True((bool)result.GetType().GetProperty("Changed")!.GetValue(result)!);

                string[] logicalPaths = InvokeRows(main, "GetEncodeRowsInExecutionOrder")
                    .Select(row => (string)Invoke(main, "GetPathFromRow", row)!)
                    .ToArray();
                Assert.Equal(new[] { paths[3], paths[0], paths[1], paths[2] }, logicalPaths);
                Assert.Equal(4, rows.Select(row => QueueSequence(main, row)).Distinct().Count());
                Assert.Equal(new long[] { 1, 2, 3, 4 },
                    InvokeRows(main, "GetEncodeRowsInExecutionOrder")
                        .Select(row => QueueSequence(main, row)).ToArray());
                Assert.Equal(
                    new[] { paths[3], paths[0], paths[2] },
                    InvokeRows(main, "GetEligibleEncodeRowsInExecutionOrder")
                        .Select(row => (string)Invoke(main, "GetPathFromRow", row)!)
                        .ToArray());

                string[] exported = ExportedPaths(main);
                Assert.Equal(logicalPaths, exported);
                string queueJson = JsonSerializer.Serialize(
                    Invoke(main, "CaptureQueueItemsInExecutionOrder"));
                string[] serializedPaths;
                using (JsonDocument document = JsonDocument.Parse(queueJson))
                {
                    serializedPaths = document.RootElement.EnumerateArray()
                        .Select(item => item.GetProperty("Path").GetString()!)
                        .ToArray();
                }
                Assert.Equal(logicalPaths, serializedPaths);

                // The queue snapshot and saved-job file lists are ordered arrays;
                // verify their existing serializers retain the intentional order.
                string persistencePath = Path.Combine(tempRoot, "queue-order.json");
                var jobService = new EncodeJobService(persistencePath);
                jobService.Save(new[]
                {
                    new EncodeJob
                    {
                        Name = "Queue order",
                        Files = exported.Select(path => new EncodeJobFile { SourcePath = path }).ToList()
                    }
                });
                Assert.Equal(
                    exported,
                    jobService.Load().Single().Files.Select(file => file.SourcePath).ToArray());

                // Queue import consumes the serialized item list in order. Rebuild
                // from that list to verify a reordered export restores its priority.
                SetField(main, "_suppressRowEvents", true);
                try
                {
                    queue.Rows.Clear();
                    Field<System.Collections.Concurrent.ConcurrentDictionary<string, DataGridViewRow>>(
                        main, "_rowsByPath").Clear();
                }
                finally
                {
                    SetField(main, "_suppressRowEvents", false);
                }
                foreach (string path in serializedPaths)
                    Assert.True((bool)Invoke(main, "AddEncodeItemIfNotPresent", path, false, false)!);
                Assert.Equal(serializedPaths,
                    InvokeRows(main, "GetEncodeRowsInExecutionOrder")
                        .Select(row => (string)Invoke(main, "GetPathFromRow", row)!).ToArray());

                List<DataGridViewRow> restoredRows = InvokeRows(main, "GetEncodeRowsInExecutionOrder");
                long[] restoredSequences = restoredRows.Select(row => QueueSequence(main, row)).ToArray();
                foreach (DataGridViewRow row in restoredRows)
                {
                    SetMetaField(main, row, "EstimateDiagnostic", "refresh after reorder");
                    SetMetaField(main, row, "EncodeRecommendation", new SmartEncodeRecommendation
                    {
                        Kind = SmartEncodeRecommendationKind.Review,
                        Confidence = SmartEncodeConfidence.Medium,
                        PrimaryReason = "Order remains independent of recommendations."
                    });
                    Invoke(main, "EnsureRowMeta", row);
                }
                search.Clear();
                Assert.Equal(logicalPaths,
                    InvokeRows(main, "GetEncodeRowsInExecutionOrder")
                        .Select(row => (string)Invoke(main, "GetPathFromRow", row)!).ToArray());
                Assert.Equal(restoredSequences,
                    restoredRows.Select(row => QueueSequence(main, row)).ToArray());
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(isolatedConfigPath);
                if (!string.IsNullOrWhiteSpace(tempRoot) && Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Queue explicit reorder UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void ActiveReorderPreservesDispatchedPrefixAndCanPromoteAnAppendedRow()
    {
        if (!OperatingSystem.IsWindows()) return;

        Exception? failure = null;
        MainForm? main = null;
        string? isolatedConfigPath = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                isolatedConfigPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow[] rows =
                [
                    AddRow(queue, "active-1.mkv", "Encoding"),
                    AddRow(queue, "inactive-2.mkv", "Queued"),
                    AddRow(queue, "excluded-3.mkv", "Excluded - exact duplicate"),
                    AddRow(queue, "pending-4.mkv", "Queued"),
                    AddRow(queue, "appended-5.mkv", "Queued")
                ];
                SetField(main, "_suppressRowEvents", false);
                foreach (DataGridViewRow row in rows)
                    Invoke(main, "EnsureRowMeta", row);

                SetMetaField(main, rows[2], "ExcludedFromEncodeAsDuplicate", true);
                SetField(main, "_encodingActive", true);
                SetField(main, "_activeEncodeQueue", new List<DataGridViewRow> { rows[0], rows[2], rows[3] });
                SetField(main, "_activeEncodeQueueDispatchedCount", 1);
                SetField(main, "_activeEncodeQueueAccepting", true);

                Assert.False((bool)Invoke(
                    main,
                    "TrySoftExcludePendingDuplicateRow",
                    rows[0],
                    rows[0].Tag!,
                    "Queued")!);
                Assert.False((bool)MetaField(main, rows[0], "ExcludedFromEncodeAsDuplicate"));
                Invoke(main, "RemoveSoftExcludedRowsFromActiveEncodeQueue");
                List<DataGridViewRow> active = (List<DataGridViewRow>)Field<object>(main, "_activeEncodeQueue");
                Assert.Equal(new[] { rows[0], rows[3] }, active);
                Assert.Equal(1, (int)Field<object>(main, "_activeEncodeQueueDispatchedCount"));

                Assert.True((bool)Invoke(main, "TryAppendActiveEncodeQueueRow", rows[4])!);
                object rejected = Invoke(
                    main,
                    "ReorderQueueRows",
                    new[] { rows[0] },
                    QueueExecutionOrderOperation.EncodeNext)!;
                Assert.False((bool)rejected.GetType().GetProperty("Changed")!.GetValue(rejected)!);
                Assert.Equal(1, (int)rejected.GetType().GetProperty("DispatchedCount")!.GetValue(rejected)!);

                object promoted = Invoke(
                    main,
                    "ReorderQueueRows",
                    new[] { rows[4] },
                    QueueExecutionOrderOperation.EncodeNext)!;
                Assert.True((bool)promoted.GetType().GetProperty("Changed")!.GetValue(promoted)!);

                Assert.Equal(new[] { rows[0], rows[4], rows[3] }, active);
                Assert.Equal(rows[0], active[0]);
                Assert.True((bool)MetaField(main, rows[2], "ExcludedFromEncodeAsDuplicate"));
                Assert.DoesNotContain(rows[2], active);

                List<DataGridViewRow> logicalRows = InvokeRows(main, "GetEncodeRowsInExecutionOrder");
                Assert.Equal(new[] { rows[0], rows[1], rows[2], rows[4], rows[3] }, logicalRows);
                Assert.Equal(
                    active.Distinct().ToArray(),
                    logicalRows.Where(active.ToHashSet().Contains).ToArray());
                Assert.Equal(5, logicalRows.Distinct().Count());
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (main != null)
                {
                    SetField(main, "_encodingActive", false);
                    SetField(main, "_activeEncodeQueue", null!);
                    SetField(main, "_activeEncodeQueueAccepting", false);
                }
                WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(isolatedConfigPath);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Active queue reorder UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void RetryAppendAndActiveReorderKeepPendingLogicalOrderAligned()
    {
        if (!OperatingSystem.IsWindows()) return;

        Exception? failure = null;
        MainForm? main = null;
        string? isolatedConfigPath = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                isolatedConfigPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow[] rows =
                [
                    AddRow(queue, "retry-a.mkv", "Failed"),
                    AddRow(queue, "pending-b.mkv", "Queued"),
                    AddRow(queue, "appended-c.mkv", "Queued")
                ];
                SetField(main, "_suppressRowEvents", false);
                foreach (DataGridViewRow row in rows)
                    Invoke(main, "EnsureRowMeta", row);
                long highestInitialSequence = rows.Max(row => QueueSequence(main, row));

                Field<CheckBox>(main, "chkRetryFailedJobs").Checked = true;
                SetField(main, "_encodingActive", true);
                SetField(main, "_activeEncodeQueue", new List<DataGridViewRow> { rows[0], rows[1] });
                SetField(main, "_activeEncodeQueueDispatchedCount", 1);
                SetField(main, "_activeEncodeQueueAccepting", true);

                Assert.True((bool)Invoke(main, "TryQueueFailedRowForAutoRetry", rows[0])!);
                Assert.True(QueueSequence(main, rows[0]) > highestInitialSequence);
                Assert.True((bool)Invoke(main, "TryAppendActiveEncodeQueueRow", rows[2])!);
                Assert.True(QueueSequence(main, rows[2]) > QueueSequence(main, rows[0]));

                object result = Invoke(
                    main,
                    "ReorderQueueRows",
                    new[] { rows[2] },
                    QueueExecutionOrderOperation.EncodeNext)!;
                Assert.True((bool)result.GetType().GetProperty("Changed")!.GetValue(result)!);

                List<DataGridViewRow> active = (List<DataGridViewRow>)Field<object>(main, "_activeEncodeQueue");
                DataGridViewRow[] pendingDistinct = active.Skip(1).Distinct().ToArray();
                List<DataGridViewRow> logicalRows = InvokeRows(main, "GetEncodeRowsInExecutionOrder");
                Assert.Equal(new[] { rows[2], rows[1], rows[0] }, pendingDistinct);
                Assert.Equal(
                    pendingDistinct,
                    logicalRows.Where(active.ToHashSet().Contains).ToArray());
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (main != null)
                {
                    SetField(main, "_encodingActive", false);
                    SetField(main, "_activeEncodeQueue", null!);
                    SetField(main, "_activeEncodeQueueAccepting", false);
                }
                WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(isolatedConfigPath);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Retry queue-order UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static DataGridViewRow AddRow(DataGridView queue, string name, string status)
    {
        DataGridViewRow row = queue.Rows[queue.Rows.Add()];
        row.Tag = name;
        row.Cells["colName"].Value = name;
        row.Cells["colStatus"].Value = status;
        return row;
    }

    private static string[] CreateFiles(string root, params string[] names)
    {
        string[] paths = names.Select(name => Path.Combine(root, name)).ToArray();
        foreach (string path in paths)
            File.WriteAllText(path, "queue-order-test");
        return paths;
    }

    private static long QueueSequence(MainForm main, DataGridViewRow row) =>
        (long)MetaField(main, row, "QueueSequence");

    private static void SetMetaField(MainForm main, DataGridViewRow row, string name, object value) =>
        (Meta(main, row).GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingFieldException(name)).SetValue(Meta(main, row), value);

    private static object MetaField(MainForm main, DataGridViewRow row, string name) =>
        (Meta(main, row).GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingFieldException(name)).GetValue(Meta(main, row))!;

    private static object Meta(MainForm main, DataGridViewRow row) =>
        Invoke(main, "EnsureRowMeta", row)!;

    private static string[] ExportedPaths(MainForm main)
    {
        object items = Invoke(main, "CaptureQueueItemsInExecutionOrder")!;
        return ((IEnumerable)items).Cast<object>()
            .Select(item => (string)(item.GetType().GetProperty("Path")?.GetValue(item)
                ?? throw new MissingMemberException("Path")))
            .ToArray();
    }

    private static List<DataGridViewRow> InvokeRows(MainForm main, string method)
    {
        object result = Invoke(main, method)!;
        return ((IEnumerable)result).Cast<DataGridViewRow>().ToList();
    }

    private static string[] Names(IEnumerable<DataGridViewRow> rows) =>
        rows.Select(row => row.Cells["colName"].Value?.ToString() ?? "").ToArray();

    private static T Field<T>(MainForm main, string name) where T : class =>
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
