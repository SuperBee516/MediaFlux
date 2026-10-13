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
    public void EstimatedOutputColumnSortsByOutputSizeAndKeepsUnknownsDeterministic()
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
                object estimateService = Field<object>(main, "_estimateService");
                estimateService.GetType().GetMethod("ResetAndCancel")!.Invoke(estimateService, null);

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                AddRow(main, queue, "medium.mkv", "1.2 GB", 1_288_490_189, 1_500);
                AddRow(main, queue, "unknown-b.mkv", "—", null, 450);
                AddRow(main, queue, "large.mkv", "2.0 GB", 2_147_483_648, 1_900);
                AddRow(main, queue, "small.mkv", "950 MB", 996_147_200, 700);
                AddRow(main, queue, "unknown-a.mkv", "—", null, null);

                DataGridViewColumn estimatedOutput = queue.Columns["colEstimatedSize"];
                Assert.Equal(DataGridViewColumnSortMode.Automatic, estimatedOutput.SortMode);

                string[] originalLogicalOrder = LogicalNames(main);
                // Exercise numeric fallback from the retained cell's MB/GB text.
                Field<Dictionary<string, double>>(main, "_estimatedSizeMap").Clear();
                foreach (DataGridViewRow row in queue.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow))
                {
                    string name = Convert.ToString(row.Cells["colName"].Value) ?? string.Empty;
                    row.Cells["colEstimatedSize"].Value = name switch
                    {
                        "medium.mkv" => "1.46 GB",
                        "unknown-b.mkv" => "450 MB",
                        "large.mkv" => "1.86 GB",
                        "small.mkv" => "700 MB",
                        _ => "—"
                    };
                }
                DataGridViewRow mediumRow = FindRow(queue, "medium.mkv");
                DataGridViewRow smallRow = FindRow(queue, "small.mkv");
                Assert.Equal("1.46 GB", mediumRow.Cells["colEstimatedSize"].Value);
                Assert.InRange((double)Invoke(main, "ParseSizeToMb", "1.46 GB")!, 1495, 1495.1);
                var mediumKey = ((double? OutputMb, string Path, long Sequence))Invoke(
                    main, "GetQueueEstimatedOutputSortKey", mediumRow.Index)!;
                var smallKey = ((double? OutputMb, string Path, long Sequence))Invoke(
                    main, "GetQueueEstimatedOutputSortKey", smallRow.Index)!;
                Assert.InRange(mediumKey.OutputMb!.Value, 1490, 1500);
                Assert.Equal(700d, smallKey.OutputMb);
                queue.Sort(estimatedOutput, ListSortDirection.Ascending);
                Application.DoEvents();
                Assert.Equal(
                    new[] { "unknown-a.mkv", "unknown-b.mkv", "small.mkv", "medium.mkv", "large.mkv" },
                    DisplayNames(queue));

                queue.Sort(queue.Columns["colSize"], ListSortDirection.Ascending);
                Application.DoEvents();
                Assert.Equal(
                    new[] { "unknown-a.mkv", "unknown-b.mkv", "small.mkv", "medium.mkv", "large.mkv" },
                    DisplayNames(queue));

                queue.Sort(estimatedOutput, ListSortDirection.Descending);
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

    private static void AddRow(
        MainForm main,
        DataGridView queue,
        string name,
        string sizeText,
        long? sizeBytes,
        double? estimatedMb)
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
        row.Cells["colEstimatedSize"].Value = estimatedMb is > 0
            ? estimatedMb.Value >= 1024
                ? $"{estimatedMb.Value / 1024:0.##} GB"
                : $"{estimatedMb.Value:0.#} MB"
            : "—";
        row.Cells["colStatus"].Value = "Queued";
        if (estimatedMb is > 0)
            Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path] = estimatedMb.Value;
    }

    private static string[] DisplayNames(DataGridView queue) => queue.Rows
        .Cast<DataGridViewRow>()
        .Where(row => !row.IsNewRow)
        .Select(row => Convert.ToString(row.Cells["colName"].Value) ?? string.Empty)
        .ToArray();

    private static DataGridViewRow FindRow(DataGridView queue, string name) => queue.Rows
        .Cast<DataGridViewRow>()
        .Single(row => !row.IsNewRow && string.Equals(Convert.ToString(row.Cells["colName"].Value), name, StringComparison.Ordinal));

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
