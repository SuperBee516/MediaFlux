using System.Reflection;
using System.Threading;
using System.Drawing;
using System.Windows.Forms;
using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class QualityTargetSegmentUiTests
{
    [Theory]
    [InlineData(QualityTarget.SmallerFile, "Smaller", "Smaller File")]
    [InlineData(QualityTarget.Efficient, "Efficient", "Efficient")]
    [InlineData(QualityTarget.Balanced, "Balanced", "Balanced")]
    [InlineData(QualityTarget.HighQuality, "High", "High Quality")]
    [InlineData(QualityTarget.MaximumQuality, "Maximum", "Maximum Quality")]
    public void ExistingQualityTargetRoundTripsThroughTheSegmentedEditor(
        QualityTarget target,
        string visibleLabel,
        string accessibleName)
    {
        if (!OperatingSystem.IsWindows())
            return;

        string configPath = Path.Combine(Path.GetTempPath(), $"MediaFlux.QualitySegments.{Guid.NewGuid():N}.json");
        new Config
        {
            RememberCheckboxStates = false,
            LastQualityMode = "Automatic",
            LastQualityTarget = target.ToString(),
            LastQualityValue = 26
        }.Save(configPath);

        try
        {
            RunOnUiThread(() =>
            {
            MainForm? form = null;
            try
            {
                form = new MainForm(configPath)
                {
                    StartPosition = FormStartPosition.Manual,
                    ClientSize = new Size(1300, 840)
                };
                form.Show();
                Application.DoEvents();

                QualityTargetSegmentSelector selector = GetSelector(form);
                ComboBox mode = Field<ComboBox>(form, "comboQualityMode");
                Assert.Equal(target, selector.SelectedTarget);
                Assert.True(selector.GetSegment(target).Checked);
                Assert.Equal(1, LogicalHandlerCount(selector));
                Assert.Single(form.Controls.Find("qualityTargetSegmentSelector", true));
                Assert.Empty(form.Controls.Find("trkQualityTarget", true));
                Assert.Single(selector.Segments, segment => segment.TabStop);
                foreach (QualityTargetSegmentSelector.Choice choice in QualityTargetSegmentSelector.Choices)
                {
                    RadioButton segment = selector.GetSegment(choice.Target);
                    Assert.Equal(choice.Label, segment.Text);
                    Assert.Equal(choice.AccessibleName, segment.AccessibleName);
                    Assert.Equal(AccessibleRole.RadioButton, segment.AccessibleRole);
                    Assert.Contains(choice.AccessibleName, segment.AccessibleDescription, StringComparison.Ordinal);
                    Assert.Contains(choice.Description, Field<ToolTip>(form, "_uiToolTip").GetToolTip(segment), StringComparison.Ordinal);
                }
                Assert.Equal(visibleLabel, selector.GetSegment(target).Text);
                Assert.Equal(accessibleName, selector.GetSegment(target).AccessibleName);

                for (int iteration = 0; iteration < 3; iteration++)
                {
                    mode.SelectedItem = "Manual • Legacy Numeric";
                    Application.DoEvents();
                    Assert.False(selector.Visible);
                    Assert.Equal(target, selector.SelectedTarget);

                    mode.SelectedItem = "Automatic • Source Adaptive";
                    Application.DoEvents();
                    Assert.True(selector.Visible);
                    Assert.True(selector.GetSegment(target).Checked);
                    Assert.Equal(target, selector.SelectedTarget);
                    Assert.Equal(1, LogicalHandlerCount(selector));
                    Assert.Single(form.Controls.Find("qualityTargetSegmentSelector", true));
                }

                int changeEvents = 0;
                selector.SelectedTargetChanged += (_, __) => changeEvents++;
                QualityTarget[] orderedTargets = QualityTargetSegmentSelector.Choices.Select(choice => choice.Target).ToArray();
                int nextIndex = (Array.IndexOf(orderedTargets, target) + 1) % orderedTargets.Length;
                for (int selection = 0; selection < orderedTargets.Length; selection++)
                {
                    QualityTarget selected = orderedTargets[(nextIndex + selection) % orderedTargets.Length];
                    selector.GetSegment(selected).PerformClick();
                    Assert.Equal(selection + 1, changeEvents);
                    Assert.Equal(selected, selector.SelectedTarget);
                    Assert.True(selector.GetSegment(selected).Checked);
                    Assert.Equal(selected.ToString(), Config.Load(configPath).LastQualityTarget);
                }
                Assert.Equal(target, selector.SelectedTarget);
                Assert.Equal(target.ToString(), Config.Load(configPath).LastQualityTarget);
                Assert.Equal(2, LogicalHandlerCount(selector));
            }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(form);
            }

            using var reloaded = new MainForm(configPath)
            {
                StartPosition = FormStartPosition.Manual,
                ClientSize = new Size(1300, 840)
            };
            reloaded.Show();
            Application.DoEvents();
            QualityTargetSegmentSelector restored = GetSelector(reloaded);
            Assert.Equal(target, restored.SelectedTarget);
            Assert.True(restored.GetSegment(target).Checked);
            Assert.Equal(target.ToString(), Config.Load(configPath).LastQualityTarget);
            WinFormsTestLifecycle.CloseAndDispose(reloaded);
            });
        }
        finally
        {
            if (File.Exists(configPath))
                File.Delete(configPath);
        }
    }

    [Fact]
    public void SegmentedQualitySelectorSupportsArrowHomeAndEndKeyboardNavigation()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string configPath = Path.Combine(Path.GetTempPath(), $"MediaFlux.QualityKeyboard.{Guid.NewGuid():N}.json");
        new Config { LastQualityTarget = nameof(QualityTarget.Balanced) }.Save(configPath);

        try
        {
            RunOnUiThread(() =>
            {
            MainForm? form = null;
            try
            {
                form = new MainForm(configPath) { StartPosition = FormStartPosition.Manual };
                form.Show();
                form.Activate();
                Application.DoEvents();

                QualityTargetSegmentSelector selector = GetSelector(form);
                RadioButton balanced = selector.GetSegment(QualityTarget.Balanced);
                Assert.True(balanced.Focus());
                Assert.True(balanced.Focused);

                RaiseKeyDown(selector.GetSegment(QualityTarget.Balanced), Keys.Right);
                Application.DoEvents();
                Assert.Equal(QualityTarget.HighQuality, selector.SelectedTarget);
                Assert.True(selector.GetSegment(QualityTarget.HighQuality).Focused);

                RaiseKeyDown(selector.GetSegment(QualityTarget.HighQuality), Keys.Home);
                Application.DoEvents();
                Assert.Equal(QualityTarget.SmallerFile, selector.SelectedTarget);

                RaiseKeyDown(selector.GetSegment(QualityTarget.SmallerFile), Keys.End);
                Application.DoEvents();
                Assert.Equal(QualityTarget.MaximumQuality, selector.SelectedTarget);

                RaiseKeyDown(selector.GetSegment(QualityTarget.MaximumQuality), Keys.Left);
                Application.DoEvents();
                Assert.Equal(QualityTarget.HighQuality, selector.SelectedTarget);
                Assert.Single(selector.Segments, segment => segment.TabStop);
            }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(form);
            }
            });
        }
        finally
        {
            if (File.Exists(configPath))
                File.Delete(configPath);
        }
    }

    private static QualityTargetSegmentSelector GetSelector(MainForm form) =>
        (QualityTargetSegmentSelector)(form.Controls.Find("qualityTargetSegmentSelector", true).Single());

    private static int LogicalHandlerCount(QualityTargetSegmentSelector selector)
    {
        FieldInfo eventField = typeof(QualityTargetSegmentSelector).GetField(
            "SelectedTargetChanged", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(QualityTargetSegmentSelector).FullName, "SelectedTargetChanged");
        return ((Delegate?)eventField.GetValue(selector))?.GetInvocationList().Length ?? 0;
    }

    private static void RaiseKeyDown(Control control, Keys key)
    {
        MethodInfo onKeyDown = typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(Control).FullName, "OnKeyDown");
        onKeyDown.Invoke(control, [new KeyEventArgs(key)]);
    }

    private static T Field<T>(MainForm form, string name) where T : class =>
        (T)(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form)
            ?? throw new MissingFieldException(name));

    private static void RunOnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Quality segment UI test timed out.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
