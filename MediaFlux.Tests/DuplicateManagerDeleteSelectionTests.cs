using System.Reflection;
using System.Collections;
using System.Threading;
using System.Windows.Forms;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class DuplicateManagerDeleteSelectionTests
{
    [Fact]
    public void UnresolvedStrongVisualPairsAreEditableAndPersistThroughTheActualManagerGrid()
    {
        RunSta(() =>
        {
            DuplicateScanResult result = Result(Enumerable.Range(1, 78).Select(UnresolvedPair).ToArray());
            using var form = new MainForm();
            SetField(form, "_lastDuplicateScanResult", result);
            using DataGridView grid = Invoke<DataGridView>(form, "CreateDuplicateManagerGrid");
            Invoke(form, "RefreshDuplicateManagerGrid", grid);

            Assert.Equal(156, grid.Rows.Count);
            DataGridViewRow first = grid.Rows[0];
            DataGridViewRow second = grid.Rows[1];
            Assert.False(first.Cells["Delete"].ReadOnly);
            Assert.False(second.Cells["Delete"].ReadOnly);

            SetChecked(grid, second, true);
            Assert.True(IsChecked(second));
            Assert.False(IsChecked(first));
            Assert.True(SelectionState(form).Resolve(UnresolvedPair(1), ItemFor(second)));

            Invoke(form, "RefreshDuplicateManagerGrid", grid);
            first = grid.Rows.Cast<DataGridViewRow>().Single(row => string.Equals(row.Tag as string, "group-1-a.mkv", StringComparison.OrdinalIgnoreCase));
            second = grid.Rows.Cast<DataGridViewRow>().Single(row => string.Equals(row.Tag as string, "group-1-b.mkv", StringComparison.OrdinalIgnoreCase));
            Assert.False(IsChecked(first));
            Assert.True(IsChecked(second));

            SetChecked(grid, second, false);
            SetChecked(grid, first, true);
            Assert.True(IsChecked(first));
            Assert.False(IsChecked(second));

            Invoke(form, "MarkDuplicateManagerGroupsReviewed", grid);
            DuplicateManagerDeleteSelectionState state = SelectionState(form);
            Assert.All(result.Groups, group => Assert.True(state.IsReviewed(group)));
            Assert.All(result.Groups, group => Assert.DoesNotContain(group.Items, item =>
                string.Equals(item.Recommendation, "Selected keeper", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains("Pending review: 0", Invoke<string>(form, "BuildDuplicateManagerSummary", "All groups"));
            first = grid.Rows.Cast<DataGridViewRow>().Single(row => string.Equals(row.Tag as string, "group-1-a.mkv", StringComparison.OrdinalIgnoreCase));
            Assert.False(first.Cells["Delete"].ReadOnly);
        });
    }

    [Fact]
    public void ThreeFileManualSelectionAlwaysLeavesASurvivor()
    {
        DuplicateGroup group = Group(1,
            Item("a.mkv", "Review required"),
            Item("b.mkv", "Review required"),
            Item("c.mkv", "Review required"));
        var state = new DuplicateManagerDeleteSelectionState();

        Assert.True(state.Record(group, group.Items[0], true));
        Assert.True(state.Record(group, group.Items[1], true));
        Assert.False(state.CanSelectForDeletion(group, group.Items[2]));
        Assert.False(state.Record(group, group.Items[2], true));
        Assert.False(state.Resolve(group, group.Items[2]));

        Assert.False(state.Record(group, group.Items[0], false));
        Assert.True(state.CanSelectForDeletion(group, group.Items[2]));
        Assert.True(state.Record(group, group.Items[2], true));
    }

    [Fact]
    public void ExplicitKeeperIsReadOnlyButOtherEligibleRowsRemainSelectable()
    {
        DuplicateGroup group = Group(1,
            Item("keeper.mkv", "Selected keeper"),
            Item("candidate.mkv", "Review required"));
        var state = new DuplicateManagerDeleteSelectionState();

        Assert.False(state.CanSelectForDeletion(group, group.Items[0]));
        Assert.True(state.CanSelectForDeletion(group, group.Items[1]));
        Assert.True(state.Record(group, group.Items[1], true));
        Assert.False(state.Resolve(group, group.Items[0]));
        Assert.True(state.Resolve(group, group.Items[1]));
    }

    [Fact]
    public void ProtectedRowsRemainUnavailableAndRecommendationDefaultsRemainAdvisory()
    {
        DuplicateGroup group = Group(1,
            Item("suggested.mkv", "Suggested keeper"),
            Item("default.mkv", "Trash candidate"),
            Item("protected.mkv", "Review required", isProtected: true));
        var state = new DuplicateManagerDeleteSelectionState();

        Assert.True(state.Resolve(group, group.Items[1]));
        Assert.True(state.CanSelectForDeletion(group, group.Items[0]));
        Assert.False(state.CanSelectForDeletion(group, group.Items[2]));
        Assert.True(state.Record(group, group.Items[0], true));
        Assert.True(state.Resolve(group, group.Items[0]));
    }

    [Fact]
    public void CleanupCandidatesContainOnlyTheExplicitlySelectedSurvivorSafeRow()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-DuplicateManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string survivor = Path.Combine(root, "survivor.mkv");
            string selected = Path.Combine(root, "selected.mkv");
            File.WriteAllBytes(survivor, [1]);
            File.WriteAllBytes(selected, [1]);
            RunSta(() =>
            {
                DuplicateGroup group = Group(1, Item(survivor, "Review required"), Item(selected, "Review required"));
                using var form = new MainForm();
                SetField(form, "_lastDuplicateScanResult", Result([group]));
                using DataGridView grid = Invoke<DataGridView>(form, "CreateDuplicateManagerGrid");
                Invoke(form, "RefreshDuplicateManagerGrid", grid);
                DataGridViewRow selectedRow = grid.Rows.Cast<DataGridViewRow>().Single(row => string.Equals(row.Tag as string, selected, StringComparison.OrdinalIgnoreCase));
                SetChecked(grid, selectedRow, true);

                IEnumerable candidates = (IEnumerable)Invoke<object>(form, "GetDuplicateActionCandidates", grid, "Suggested duplicate files", false);
                object candidate = Assert.Single(candidates.Cast<object>());
                string candidatePath = (string)(candidate.GetType().GetProperty("Path")?.GetValue(candidate) ?? throw new MissingMemberException("Path"));
                Assert.Equal(selected, candidatePath);
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static DuplicateScanResult Result(IReadOnlyList<DuplicateGroup> groups) => new(groups, groups.Count, groups.Sum(group => group.Items.Skip(1).Sum(item => item.LengthBytes)));

    private static DuplicateGroup UnresolvedPair(int id) => Group(id,
        Item($"group-{id}-a.mkv", "Review required"),
        Item($"group-{id}-b.mkv", "Review required"));

    private static DuplicateGroup Group(int id, params DuplicateItem[] items) => new(
        id, "Strong visual match", 98, "Strong visual evidence", "Frame hashes", 9, 9, 2, 0.1, items);

    private static DuplicateItem Item(string path, string recommendation, bool isProtected = false) => new(
        path, 100, "h264", 1920, 1080, 60, 2_000, DateTime.UtcNow, DateTime.UtcNow,
        isProtected, recommendation == "Review required" ? "Manual review required" : "test", recommendation);

    private static DuplicateItem ItemFor(DataGridViewRow row) => new(
        (string)row.Tag!, 100, "h264", 1920, 1080, 60, 2_000, DateTime.UtcNow, DateTime.UtcNow,
        false, "Manual review required", "Review required");

    private static void SetChecked(DataGridView grid, DataGridViewRow row, bool selected)
    {
        DataGridViewCell cell = row.Cells["Delete"];
        Assert.False(cell.ReadOnly);
        grid.CurrentCell = cell;
        cell.Value = selected;
        grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
    }

    private static bool IsChecked(DataGridViewRow row) => row.Cells["Delete"].Value is bool selected && selected;

    private static DuplicateManagerDeleteSelectionState SelectionState(MainForm form) =>
        (DuplicateManagerDeleteSelectionState)(typeof(MainForm).GetField("_duplicateManagerDeleteSelections", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form)
            ?? throw new MissingFieldException("_duplicateManagerDeleteSelections"));

    private static void SetField(object instance, string name, object value)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name);
        field.SetValue(instance, value);
    }

    private static void Invoke(object instance, string name, params object[] arguments)
    {
        MethodInfo method = FindMethod(instance, name, arguments.Length)
            ?? throw new MissingMethodException(name);
        method.Invoke(instance, arguments);
    }

    private static T Invoke<T>(object instance, string name, params object[] arguments) =>
        (T)(FindMethod(instance, name, arguments.Length)?.Invoke(instance, arguments)
            ?? throw new MissingMethodException(name));

    private static MethodInfo? FindMethod(object instance, string name, int parameterCount) => instance.GetType()
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
        .SingleOrDefault(method => method.Name == name && method.GetParameters().Length == parameterCount);

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
