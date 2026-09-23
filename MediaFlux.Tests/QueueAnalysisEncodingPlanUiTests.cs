using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class QueueAnalysisEncodingPlanUiTests
{
    [Fact]
    public void QueueAnalysisPersistsBeforePreflightAndClearsForAnotherSelectedRow()
    {
        if (!OperatingSystem.IsWindows()) return;

        Exception? failure = null;
        MainForm? main = null;
        string? isolatedConfigPath = null;
        var thread = new Thread(() =>
        {
            try
            {
                main = new MainForm();
                isolatedConfigPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.CreateControl();
                main.Show();
                Application.DoEvents();

                Type formType = typeof(MainForm);
                DataGridView queue = Field<DataGridView>(formType, main, "dgvEncodeQueue");
                DataGridViewRow analyzed = queue.Rows[queue.Rows.Add()];
                DataGridViewRow notAnalyzed = queue.Rows[queue.Rows.Add()];
                SetMeta(formType, main, analyzed, Recommendation(), 1_000);
                SetMeta(formType, main, notAnalyzed, null, 0);
                analyzed.Selected = true;

                Invoke(formType, main, "ScheduleEncodingPlanRefresh");
                TableLayoutPanel analysis = Field<TableLayoutPanel>(formType, main, "_queueAnalysisTable");
                Label analysisStatus = Field<Label>(formType, main, "_queueAnalysisStatusLabel");
                Label planStatus = Field<Label>(formType, main, "_encodingPlanStatusLabel");
                Assert.Empty(analysisStatus.Text);
                Assert.Contains("RECOMMENDATION", ControlText(analysis));
                Assert.Contains("Strong candidate", ControlText(analysis));
                Assert.Equal("Encoding Intelligence will appear when the existing encode preflight publishes its plan.", planStatus.Text);

                Control analysisControl = analysis.Controls[0];
                Invoke(formType, main, "ScheduleEncodingPlanRefresh");
                Assert.Same(analysisControl, analysis.Controls[0]);

                SetMeta(formType, main, notAnalyzed, Recommendation(), 1_000, "analyzed.mkv");
                object reboundMeta = (formType.GetMethod("EnsureRowMeta", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("EnsureRowMeta")).Invoke(main, [notAnalyzed])!;
                Invoke(formType, main, "RenderQueueAnalysis", notAnalyzed, reboundMeta, null);
                Assert.Same(analysisControl, analysis.Controls[0]);

                SetMeta(formType, main, analyzed, Recommendation(EstimatedSavingsPercent: 20), 1_000);
                Invoke(formType, main, "ScheduleEncodingPlanRefresh");
                Assert.NotSame(analysisControl, analysis.Controls[0]);
                Assert.Contains("20%", ControlText(analysis));

                SetPlan(formType, main, analyzed);
                Invoke(formType, main, "ScheduleEncodingPlanRefresh");
                TableLayoutPanel plan = Field<TableLayoutPanel>(formType, main, "_encodingPlanTable");
                Assert.NotEmpty(plan.Controls.Cast<Control>());
                Control planControl = plan.Controls[0];
                object planMeta = (formType.GetMethod("EnsureRowMeta", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("EnsureRowMeta")).Invoke(main, [analyzed])!;
                EncodingPlan frozenPlan = (EncodingPlan)(planMeta.GetType()
                    .GetField("IntelligencePlan", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(planMeta) ?? throw new MissingFieldException("IntelligencePlan"));
                DateTime frozenDecisionUtc = Assert.IsType<DateTime>(frozenPlan.SizePredictionCalibration?.DecisionUtc);
                Invoke(formType, main, "ScheduleEncodingPlanRefresh");
                Assert.Same(planControl, plan.Controls[0]);

                analyzed.Cells["colProgress"].Value = "27%";
                analyzed.Cells["colETA"].Value = "00:04:30";
                Invoke(formType, main, "RefreshQueueWorkspaceRow", analyzed);
                Invoke(formType, main, "RefreshQueueWorkspacePresentation");
                Assert.Same(planControl, plan.Controls[0]);
                Assert.Equal(frozenDecisionUtc, frozenPlan.SizePredictionCalibration?.DecisionUtc);

                object analyzedMeta = (formType.GetMethod("EnsureRowMeta", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("EnsureRowMeta")).Invoke(main, [analyzed])!;
                FieldInfo calibrationField = analyzedMeta.GetType().GetField("SizePredictionCalibration", BindingFlags.Instance | BindingFlags.Public)
                    ?? throw new MissingFieldException("SizePredictionCalibration");
                var calibration = EncodingSizePredictionCalibration.Unavailable(100, "No eligible historical cohort.",
                    "PredictionCalibrationPolicyV1", DateTime.UtcNow);
                calibrationField.SetValue(analyzedMeta, calibration);
                Invoke(formType, main, "ScheduleEncodingPlanRefresh");
                Control calibrationControl = analysis.Controls.Cast<Control>().First(control => ControlText(control).Contains("SIZE CALIBRATION", StringComparison.Ordinal));
                Assert.Contains("PredictionCalibrationPolicyV1", ControlText(calibrationControl));

                Invoke(formType, main, "UpdateSizeTotals", true);
                Assert.Same(planControl, plan.Controls[0]);
                Assert.Same(calibrationControl, analysis.Controls.Cast<Control>().First(control => ControlText(control).Contains("SIZE CALIBRATION", StringComparison.Ordinal)));
                Assert.Equal(frozenDecisionUtc, frozenPlan.SizePredictionCalibration?.DecisionUtc);

                calibrationField.SetValue(analyzedMeta, calibration with
                {
                    EffectivenessState = EncodingCalibrationEffectivenessState.Harmful,
                    LearningStrength = .75
                });
                Invoke(formType, main, "ScheduleEncodingPlanRefresh");
                Control updatedCalibrationControl = analysis.Controls.Cast<Control>().First(control => ControlText(control).Contains("SIZE CALIBRATION", StringComparison.Ordinal));
                Assert.NotSame(calibrationControl, updatedCalibrationControl);
                Assert.Contains("Harmful", ControlText(updatedCalibrationControl));
                Assert.Contains("learning strength 0.75", ControlText(updatedCalibrationControl));
                Assert.Same(planControl, plan.Controls[0]);

                // Recommendation/estimate publication changes Queue Analysis only;
                // it must not invalidate or rebuild the cached Encoding Plan.
                Invoke(formType, main, "ApplySmartRecommendation", analyzed, Recommendation(EstimatedSavingsPercent: 21), true);
                Assert.Same(planControl, plan.Controls[0]);

                object meta = (formType.GetMethod("EnsureRowMeta", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("EnsureRowMeta")).Invoke(main, [analyzed])!;
                meta.GetType().GetField("IntelligenceOutcome", BindingFlags.Instance | BindingFlags.Public)!.SetValue(
                    meta,
                    new EncodingExecutionOutcome(Guid.Empty, [], [], TerminalResult: EncodingTerminalResult.Completed));
                Invoke(formType, main, "RefreshCurrentEncodingIntelligence", analyzed, meta);
                Assert.Same(planControl, plan.Controls[0]);
                Assert.Contains("Completed", ControlText(plan));

                SetPlan(formType, main, analyzed);
                Invoke(formType, main, "ScheduleEncodingPlanRefresh");
                Assert.NotSame(planControl, plan.Controls[0]);

                SetMeta(formType, main, notAnalyzed, null, 0);
                analyzed.Selected = false;
                notAnalyzed.Selected = true;
                Invoke(formType, main, "ScheduleEncodingPlanRefresh");
                Assert.Equal("Queue analysis has not been performed for this file.", analysisStatus.Text);
                Assert.Empty(analysis.Controls.Cast<Control>());
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Queue Analysis UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void SetMeta(Type formType, MainForm main, DataGridViewRow row, SmartEncodeRecommendation? recommendation, double sourceMb, string? path = null)
    {
        MethodInfo ensure = formType.GetMethod("EnsureRowMeta", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("EnsureRowMeta");
        object meta = ensure.Invoke(main, [row])!;
        Type metaType = meta.GetType();
        metaType.GetField("Path", BindingFlags.Instance | BindingFlags.Public)!.SetValue(meta, path ?? (row.Index == 0 ? "analyzed.mkv" : "not-analyzed.mkv"));
        metaType.GetField("SrcMb", BindingFlags.Instance | BindingFlags.Public)!.SetValue(meta, sourceMb);
        metaType.GetField("EncodeRecommendation", BindingFlags.Instance | BindingFlags.Public)!.SetValue(meta, recommendation);
    }

    private static void SetPlan(Type formType, MainForm main, DataGridViewRow row)
    {
        object meta = (formType.GetMethod("EnsureRowMeta", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("EnsureRowMeta")).Invoke(main, [row])!;
        meta.GetType().GetField("IntelligencePlan", BindingFlags.Instance | BindingFlags.Public)!.SetValue(meta, new EncodingPlan
        {
            IsAvailable = true,
            Source = new("h264", 1920, 1080, 24, 600),
            Video = new("Reencode", "hevc_nvenc", "nvenc", 1920, 1080, 1920, 1080, "yuv420p"),
            Hardware = new(true, "nvenc", true),
            Estimates = new(null, null, null),
            SizePredictionCalibration = EncodingSizePredictionCalibration.Unavailable(
                100, "Frozen test decision.", "PredictionCalibrationPolicyV1", DateTime.UnixEpoch)
        });
    }

    private static SmartEncodeRecommendation Recommendation(double EstimatedSavingsPercent = 35) => new()
    {
        Kind = SmartEncodeRecommendationKind.StrongCandidate,
        Confidence = SmartEncodeConfidence.High,
        EstimatedSavingsPercent = EstimatedSavingsPercent,
        EstimatedSavingsMb = 350,
        PrimaryReason = "Expected savings.",
        Reasons = ["Expected savings."]
    };

    private static T Field<T>(Type formType, MainForm main, string name) where T : class =>
        (T)(formType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
            ?? throw new MissingFieldException(name));

    private static void Invoke(Type formType, MainForm main, string name, params object?[] args)
    {
        MethodInfo method = formType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length)
            ?? throw new MissingMethodException(name);
        method.Invoke(main, args);
    }

    private static string ControlText(Control control) => string.Join("\n", control.Controls.Cast<Control>()
        .Select(child => child.Text + "\n" + ControlText(child)));
}
