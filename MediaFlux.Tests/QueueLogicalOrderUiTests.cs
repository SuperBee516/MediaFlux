using System.Collections;
using System.ComponentModel;
using System.Reflection;
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
