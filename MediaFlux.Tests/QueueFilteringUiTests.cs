using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class QueueFilteringUiTests
{
    [Fact]
    public void SearchAndOperationalViewsComposeWithoutChangingLogicalOrder()
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
                    AddRow(queue, @"C:\Media\EncodeOne.mkv", "Queued"),
                    AddRow(queue, @"C:\Media\SkipTwo.mkv", "Queued"),
                    AddRow(queue, @"C:\Media\ReviewThree.mkv", "Queued"),
                    AddRow(queue, @"C:\Media\RunningFour.mkv", "Encoding")
                ];
                SetField(main, "_suppressRowEvents", false);
                SetRecommendation(main, rows[0], SmartEncodeRecommendationKind.StrongCandidate);
                SetRecommendation(main, rows[1], SmartEncodeRecommendationKind.Skip);
                SetRecommendation(main, rows[2], SmartEncodeRecommendationKind.Review);
                SetRecommendation(main, rows[3], SmartEncodeRecommendationKind.ModerateCandidate);
                foreach (DataGridViewRow row in rows)
                    Invoke(main, "EnsureRowMeta", row);
                long[] sequences = rows.Select(row => QueueSequence(main, row)).ToArray();

                TextBox search = Field<TextBox>(main, "_queueWorkspaceSearchBox");
                ComboBox view = Field<ComboBox>(main, "_queueWorkspaceViewSelector");
                Label showing = Field<Label>(main, "_queueWorkspaceShowingValue");

                rows[0].Selected = true;
                queue.CurrentCell = rows[0].Cells["colName"];
                search.Text = "skiptwo.mkv";
                Assert.Equal("Showing 1 of 4", showing.Text);
                Assert.True(rows[1].Visible);
                Assert.True(rows[1].Selected);
                Assert.False(rows[0].Visible);
                Assert.False(rows[2].Visible);
                Assert.False(rows[3].Visible);
                Assert.Equal(new[] { "EncodeOne.mkv", "SkipTwo.mkv", "ReviewThree.mkv", "RunningFour.mkv" },
                    Names(InvokeRows(main, "GetEncodeRowsInExecutionOrder")));

                search.Clear();
                Assert.Equal("Showing 4 of 4", showing.Text);
                view.SelectedItem = "Encode";
                Assert.Equal("Showing 2 of 4", showing.Text);
                Assert.True(rows[0].Visible);
                Assert.True(rows[3].Visible);
                Assert.False(rows[1].Visible);
                Assert.False(rows[2].Visible);

                search.Text = "ReviewThree";
                Assert.Equal("Showing 0 of 4", showing.Text);
                Assert.DoesNotContain(queue.SelectedRows.Cast<DataGridViewRow>(), row => !row.IsNewRow);

                search.Clear();
                view.SelectedItem = "Review";
                Assert.Equal("Showing 1 of 4", showing.Text);
                Assert.True(rows[2].Visible);
                Assert.All(rows.Where((_, index) => index != 2), row => Assert.False(row.Visible));
                Assert.Equal(sequences, rows.Select(row => QueueSequence(main, row)).ToArray());
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Queue filtering UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void DuplicateCandidateFilteringUsesTheUnifiedPredicateAndCanLeaveNoVisibleRows()
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
                DataGridViewRow candidate = AddRow(queue, @"C:\Media\Candidate.mkv", "Queued");
                DataGridViewRow other = AddRow(queue, @"C:\Media\Other.mkv", "Queued");
                SetField(main, "_suppressRowEvents", false);
                Invoke(main, "EnsureRowMeta", candidate);
                Invoke(main, "EnsureRowMeta", other);
                SetMetaField(main, candidate, "DuplicateGroupId", 7);
                SetField(main, "_lastDuplicateScanResult", new DuplicateScanResult(
                    Array.Empty<DuplicateGroup>(), 0, 0));

                CheckBox duplicateOnly = Field<CheckBox>(main, "chkOnlyDuplicateCandidates");
                duplicateOnly.Checked = true;
                Invoke(main, "ApplyDuplicateCandidateViewFilter");
                Assert.True(candidate.Visible);
                Assert.False(other.Visible);

                TextBox search = Field<TextBox>(main, "_queueWorkspaceSearchBox");
                search.Text = "other";
                Assert.False(candidate.Visible);
                Assert.False(other.Visible);
                Assert.Equal("Showing 0 of 2", Field<Label>(main, "_queueWorkspaceShowingValue").Text);

                Invoke(main, "ClearDuplicateAnnotations", true);
                search.Clear();
                Assert.True(candidate.Visible);
                Assert.True(other.Visible);
                Assert.Equal("Showing 2 of 2", Field<Label>(main, "_queueWorkspaceShowingValue").Text);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Duplicate queue filtering UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void QueueViewPersistsAndInvalidValuesNormalizeToAll()
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

                Assert.Equal("All", NormalizeView(main, "not-a-view"));
                Assert.Equal("Review", NormalizeView(main, "review"));

                ComboBox view = Field<ComboBox>(main, "_queueWorkspaceViewSelector");
                view.SelectedItem = "Review";
                Assert.Equal("Review", Config.Load(isolatedConfigPath).QueueWorkspaceView);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Queue view persistence UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void PresentationFilteringRetainsTheExistingPlanObject()
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
                DataGridViewRow row = AddRow(queue, @"C:\Media\Planned.mkv", "Queued");
                SetField(main, "_suppressRowEvents", false);
                Invoke(main, "EnsureRowMeta", row);
                var plan = new EncodingPlan { IsAvailable = false, UnavailableReason = "queue filtering test" };
                SetMetaField(main, row, "IntelligencePlan", plan);

                TextBox search = Field<TextBox>(main, "_queueWorkspaceSearchBox");
                search.Text = "not-present";
                Assert.Same(plan, MetaField(main, row, "IntelligencePlan"));
                search.Clear();
                Assert.Same(plan, MetaField(main, row, "IntelligencePlan"));
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Queue plan preservation UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static DataGridViewRow AddRow(DataGridView queue, string path, string status)
    {
        DataGridViewRow row = queue.Rows[queue.Rows.Add()];
        row.Tag = path;
        row.Cells["colName"].Value = Path.GetFileName(path);
        row.Cells["colStatus"].Value = status;
        return row;
    }

    private static void SetRecommendation(MainForm main, DataGridViewRow row, SmartEncodeRecommendationKind kind)
    {
        object meta = Invoke(main, "EnsureRowMeta", row)!;
        SetMetaField(main, row, "EncodeRecommendation", new SmartEncodeRecommendation
        {
            Kind = kind,
            Confidence = SmartEncodeConfidence.High,
            PrimaryReason = "Queue filtering test recommendation."
        });
        row.Cells["colEncodeRecommendation"].Value = kind.ToString();
    }

    private static long QueueSequence(MainForm main, DataGridViewRow row) =>
        Convert.ToInt64(MetaField(main, row, "QueueSequence"));

    private static void SetMetaField(MainForm main, DataGridViewRow row, string name, object? value) =>
        (Meta(main, row).GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingFieldException(name)).SetValue(Meta(main, row), value);

    private static object? MetaField(MainForm main, DataGridViewRow row, string name) =>
        (Meta(main, row).GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingFieldException(name)).GetValue(Meta(main, row));

    private static object Meta(MainForm main, DataGridViewRow row) => Invoke(main, "EnsureRowMeta", row)!;

    private static string NormalizeView(MainForm main, string value)
    {
        MethodInfo method = typeof(MainForm).GetMethod(
            "NormalizeQueueWorkspaceView", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("NormalizeQueueWorkspaceView");
        return (string)method.Invoke(null, [value])!;
    }

    private static List<DataGridViewRow> InvokeRows(MainForm main, string name)
    {
        object result = Invoke(main, name)!;
        return ((System.Collections.IEnumerable)result).Cast<DataGridViewRow>().ToList();
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
