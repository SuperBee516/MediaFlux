using System.ComponentModel;
using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class QueueSizeSortingUiTests
{
    [Fact]
    public void SourceSizeColumnSortsByBytesAndKeepsUnknownsDeterministic()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string configPath = Path.Combine(Path.GetTempPath(), $"MediaFlux.QueueSizeSort.{Guid.NewGuid():N}.json");
        Exception? failure = null;
        MainForm? main = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm(configPath) { StartPosition = FormStartPosition.Manual };
                main.Show();
                Application.DoEvents();

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                AddRow(main, queue, "medium.mkv", "1.2 GB", 1_288_490_189);
                AddRow(main, queue, "unknown-b.mkv", "—", null);
                AddRow(main, queue, "large.mkv", "2.0 GB", 2_147_483_648);
                AddRow(main, queue, "small.mkv", "950 MB", 996_147_200);
                AddRow(main, queue, "unknown-a.mkv", "—", null);

                DataGridViewColumn sourceSize = queue.Columns["colSourceEstimate"];
                Assert.Equal(DataGridViewColumnSortMode.Automatic, sourceSize.SortMode);

                string[] originalLogicalOrder = LogicalNames(main);
                queue.Sort(sourceSize, ListSortDirection.Ascending);
                Application.DoEvents();
                Assert.Equal(
                    new[] { "unknown-a.mkv", "unknown-b.mkv", "small.mkv", "medium.mkv", "large.mkv" },
                    DisplayNames(queue));

                queue.Sort(queue.Columns["colSize"], ListSortDirection.Ascending);
                Application.DoEvents();
                Assert.Equal(
                    new[] { "unknown-a.mkv", "unknown-b.mkv", "small.mkv", "medium.mkv", "large.mkv" },
                    DisplayNames(queue));

                queue.Sort(sourceSize, ListSortDirection.Descending);
                Application.DoEvents();
                Assert.Equal(
                    new[] { "large.mkv", "medium.mkv", "small.mkv", "unknown-b.mkv", "unknown-a.mkv" },
                    DisplayNames(queue));
                Assert.Equal(originalLogicalOrder, LogicalNames(main));
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(main);
                if (File.Exists(configPath))
                    File.Delete(configPath);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Queue size sorting UI test timed out.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void AddRow(MainForm main, DataGridView queue, string name, string sizeText, long? sizeBytes)
    {
        int index = queue.Rows.Add();
        DataGridViewRow row = queue.Rows[index];
        string path = Path.Combine(Path.GetTempPath(), name);
        row.Tag = path;
        object meta = Invoke(main, "EnsureRowMeta", row)!;
        meta.GetType().GetField("Path", BindingFlags.Instance | BindingFlags.Public)!.SetValue(meta, path);
        meta.GetType().GetField("SourceSizeBytes", BindingFlags.Instance | BindingFlags.Public)!.SetValue(meta, sizeBytes);
        row.Cells["colName"].Value = name;
        row.Cells["colSize"].Value = sizeText;
        row.Cells["colSourceEstimate"].Value = $"{sizeText} → estimate";
        row.Cells["colStatus"].Value = "Queued";
    }

    private static string[] DisplayNames(DataGridView queue) => queue.Rows
        .Cast<DataGridViewRow>()
        .Where(row => !row.IsNewRow)
        .Select(row => Convert.ToString(row.Cells["colName"].Value) ?? string.Empty)
        .ToArray();

    private static string[] LogicalNames(MainForm main) => ((IEnumerable<DataGridViewRow>)Invoke(
            main,
            "GetEncodeRowsInExecutionOrder")!)
        .Select(row => Convert.ToString(row.Cells["colName"].Value) ?? string.Empty)
        .ToArray();

    private static T Field<T>(MainForm main, string name) where T : class =>
        (T)(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
            ?? throw new MissingFieldException(name));

    private static object? Invoke(MainForm main, string name, params object?[] args)
    {
        MethodInfo method = typeof(MainForm).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length);
        return method.Invoke(main, args);
    }
}
