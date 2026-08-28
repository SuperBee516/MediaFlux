using MediaFlux.Models;
using MediaFlux.Services;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Xunit;

namespace MediaFlux.Tests;

public sealed class CommercialDetectionTests
{
    [Fact]
    public async Task NoAudioSourceStillUsesVideoDetectorsAndReturnsSegments()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-CommercialDetection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.mp4"); string ffmpeg = Path.Combine(root, "ffmpeg.exe");
            File.WriteAllText(source, "source"); File.WriteAllText(ffmpeg, "tool"); int calls = 0;
            var service = new CommercialDetectionService(ffmpeg, new ScriptedRunner(request =>
            {
                calls++;
                return new MediaToolProcessResult { ExitCode = 0, StandardError = calls == 1 ? "black_start:20 black_end:20.2 black_duration:0.2" : "pts_time:20.1" };
            }), new FixedProbe(hasAudio: false));

            CommercialDetectionResult result = await service.AnalyzeAsync(source);

            Assert.True(result.Success); Assert.Equal(2, calls);
            Assert.Contains(result.Warnings, warning => warning.Contains("no audio", StringComparison.OrdinalIgnoreCase));
            Assert.Single(result.Boundaries); Assert.Equal(2, result.Segments.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ParsersUseInvariantIntervalsAndIgnoreMalformedOutput()
    {
        DetectionSignal black = Assert.Single(BlackDetectionAnalyzer.Parse("[blackdetect] black_start:60.12 black_end:60.28 black_duration:0.16\nblack_start:oops"));
        Assert.Equal(60.20, black.TimestampSeconds, 2);
        DetectionSignal silence = Assert.Single(SilenceDetectionAnalyzer.Parse("silence_start: 60.10\nsilence_end: 60.30 | silence_duration: 0.20"));
        Assert.Equal(60.20, silence.TimestampSeconds, 2);
        DetectionSignal scene = Assert.Single(SceneDetectionAnalyzer.Parse("[Parsed_metadata] frame:1 pts:1200 pts_time:60.09\nlavfi.scene_score=0.72"));
        Assert.Equal(60.09, scene.TimestampSeconds, 2);
        Assert.Empty(SilenceDetectionAnalyzer.Parse("silence_end: invalid | silence_duration: invalid"));
    }

    [Fact]
    public void CorrelationGroupsNearbySignalsAndScoresCorroboration()
    {
        CommercialDetectionSettings settings = CommercialDetectionSettings.Standard;
        CommercialBoundary boundary = Assert.Single(BoundaryCorrelationEngine.Correlate(new[]
        {
            new DetectionSignal(DetectionSignalKind.Black, 60.20, 60.12, 60.28, .16),
            new DetectionSignal(DetectionSignalKind.Silence, 60.28, 60.18, 60.38, .20),
            new DetectionSignal(DetectionSignalKind.Scene, 60.09)
        }, settings));

        Assert.InRange(boundary.TimestampSeconds, 60.14, 60.23);
        Assert.Equal(3, boundary.Evidence.Count);
        Assert.True(boundary.Confidence >= 90);
        Assert.Equal(CommercialDetectionConfidence.High, boundary.ConfidenceCategory);
    }

    [Fact]
    public void SceneOnlyCandidatesAreFilteredUnlessAggressiveRulesAllowThem()
    {
        CommercialBoundary sceneOnly = Assert.Single(BoundaryCorrelationEngine.Correlate(new[]
        {
            new DetectionSignal(DetectionSignalKind.Scene, 30), new DetectionSignal(DetectionSignalKind.Scene, 30.2)
        }, CommercialDetectionSettings.Standard));
        Assert.Empty(BoundaryCorrelationEngine.FilterAndOrder(new[] { sceneOnly }, 100, CommercialDetectionSettings.Standard).Boundaries);
        Assert.Single(BoundaryCorrelationEngine.FilterAndOrder(new[] { sceneOnly }, 100, CommercialDetectionSettings.Aggressive).Boundaries);
    }

    [Fact]
    public void FilteringRejectsTinyAndSourceEdgeSegmentsWithoutDuplicateBoundaries()
    {
        var candidates = new[]
        {
            Candidate(2), Candidate(20), Candidate(27), Candidate(80), Candidate(98)
        };
        (IReadOnlyList<CommercialBoundary> boundaries, int rejected) = BoundaryCorrelationEngine.FilterAndOrder(candidates, 100, CommercialDetectionSettings.Standard);

        Assert.Equal(new[] { 20d, 80d }, boundaries.Select(boundary => boundary.TimestampSeconds));
        Assert.Equal(3, rejected);
    }

    [Fact]
    public void GeneratedSegmentsAreContiguousAndCoverTheFullSource()
    {
        CommercialSegment[] segments = BoundaryCorrelationEngine.GenerateSegments(new[] { Candidate(20), Candidate(70) }, 100).ToArray();
        Assert.Collection(segments,
            first => { Assert.Equal(0, first.StartSeconds); Assert.Equal(20, first.EndSeconds); },
            second => { Assert.Equal(20, second.StartSeconds); Assert.Equal(70, second.EndSeconds); },
            third => { Assert.Equal(70, third.StartSeconds); Assert.Equal(100, third.EndSeconds); });
    }

    [Fact]
    public void PresetMatchingBecomesCustomAfterAnIndividualChange()
    {
        Assert.Equal(CommercialDetectionPreset.Standard, CommercialDetectionSettings.Standard.GetPreset());
        Assert.Equal(CommercialDetectionPreset.Conservative, CommercialDetectionSettings.Conservative.GetPreset());
        Assert.Equal(CommercialDetectionPreset.Aggressive, CommercialDetectionSettings.Aggressive.GetPreset());
        Assert.Equal(CommercialDetectionPreset.Custom, (CommercialDetectionSettings.Standard with { SceneThreshold = .47 }).GetPreset());
    }

    [Theory]
    [InlineData(0, true, false, false, true)]
    [InlineData(1, false, false, true, false)]
    [InlineData(2, true, true, false, true)]
    [InlineData(3, false, false, true, false)]
    [InlineData(4, true, true, false, true)]
    [InlineData(5, true, true, false, true)]
    [InlineData(6, true, true, false, true)]
    public void UiStateRulesPreventConcurrentAnalysis(int stateValue, bool browse, bool analyze, bool cancel, bool settings)
    {
        CommercialDetectorViewState state = (CommercialDetectorViewState)stateValue;
        CommercialDetectorControlState controls = CommercialDetectorStateRules.For(state);
        Assert.Equal(browse, controls.CanBrowse); Assert.Equal(analyze, controls.CanAnalyze);
        Assert.Equal(cancel, controls.CanCancel); Assert.Equal(settings, controls.CanChangeSettings);
    }

    [Fact]
    public void CommonLengthPreferenceIsOnlyASmallOptionalBoost()
    {
        CommercialBoundary candidate = Candidate(30) with { Confidence = 46, ConfidenceCategory = CommercialDetectionConfidence.Low };
        CommercialBoundary preferred = Assert.Single(BoundaryCorrelationEngine.FilterAndOrder(new[] { candidate }, 100, CommercialDetectionSettings.Standard).Boundaries);
        CommercialBoundary ordinary = Assert.Single(BoundaryCorrelationEngine.FilterAndOrder(new[] { candidate }, 100, CommercialDetectionSettings.Standard with { PreferCommonCommercialLengths = false }).Boundaries);
        Assert.Equal(50, preferred.Confidence);
        Assert.Equal(46, ordinary.Confidence);
    }

    [Fact]
    public void ReviewTimelineMappingRemainsAccurateAfterZoomPanAndResize()
    {
        using var timeline = new CommercialDetectionTimelineControl { Size = new Size(800, 140) };
        var boundary = new CommercialReviewBoundary(Guid.NewGuid(), 50, 50, 80, CommercialDetectionConfidence.High, Array.Empty<DetectionEvidence>(), CommercialBoundaryOrigin.Automatic);
        timeline.SetSource(100, new[] { boundary });
        float fitX = timeline.XForTimestampForTesting(50);
        Assert.Equal(50, timeline.TimestampForXForTesting((int)Math.Round(fitX)), .2);
        timeline.ZoomBy(2, 50);
        Assert.Equal(25, timeline.ViewportStartForTesting, 3);
        Assert.Equal(50, timeline.ViewportDurationForTesting, 3);
        timeline.SetPanFraction(1);
        Assert.Equal(50, timeline.ViewportStartForTesting, 3);
        timeline.Size = new Size(1200, 160);
        float resizedX = timeline.XForTimestampForTesting(75);
        Assert.Equal(75, timeline.TimestampForXForTesting((int)Math.Round(resizedX)), .2);
    }

    [Theory]
    [InlineData(.1, 100, 0, .35)]
    [InlineData(99.9, 100, 99.65, 100)]
    public void BoundaryPreviewFramesClampAtSourceEdges(double boundary, double duration, double expectedBefore, double expectedAfter)
    {
        (double before, double after) = CommercialDetectorForm.ResolveBoundaryPreviewTimes(boundary, duration);
        Assert.Equal(expectedBefore, before, 3); Assert.Equal(expectedAfter, after, 3);
    }

    [Fact]
    public async Task SharedFramePreviewUsesClampedInvariantSeekAndReturnsDetachedImage()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-FramePreview", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string ffmpeg = Path.Combine(root, "ffmpeg.exe"); string source = Path.Combine(root, "source.mkv"); File.WriteAllText(ffmpeg, "tool"); File.WriteAllText(source, "source"); MediaToolProcessRequest? captured = null;
            var runner = new ScriptedRunner(request =>
            {
                captured = request; using var bitmap = new Bitmap(8, 8); bitmap.Save(request.Arguments.Last(), ImageFormat.Jpeg); return new MediaToolProcessResult { ExitCode = 0 };
            });
            var service = new VideoFramePreviewService(root, ffmpeg, runner, Path.Combine(root, "cache"));
            using Image? image = await service.ExtractAsync(source, -.5, 100, 25);
            Assert.NotNull(image); Assert.NotNull(captured); int seek = captured!.Arguments.ToList().IndexOf("-ss"); Assert.Equal("0", captured.Arguments[seek + 1]); Assert.False(File.Exists(captured.Arguments.Last()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CommercialDetectorLayoutKeepsCoreReviewControlsUsable()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-CommercialDetectorUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using var form = new CommercialDetectorForm(new Config(), Path.Combine(root, "config.json"));
                foreach (Size size in new[] { form.MinimumSize, new Size(1280, 860), new Size(1500, 980) })
                {
                    form.Size = size; form.Show(); Application.DoEvents();
                    if (!form.Controls.Find("CommercialAdvancedSettings", true).Single().Visible)
                    {
                        ((Button)form.Controls.Find("CommercialAdvancedToggle", true).Single()).PerformClick();
                        Application.DoEvents();
                    }
                    Assert.True(form.Controls.Find("CommercialAdvancedSettings", true).Single().Visible);
                    ComboBox preset = (ComboBox)form.Controls.Find("CommercialPreset", true).Single();
                    NumericUpDown blackDuration = (NumericUpDown)form.Controls.Find("CommercialBlackDuration", true).Single();
                    preset.SelectedItem = CommercialDetectionPreset.Standard.ToString();
                    blackDuration.Value = .25M; Application.DoEvents();
                    Assert.Equal(CommercialDetectionPreset.Custom.ToString(), preset.SelectedItem);
                    ((Button)form.Controls.Find("CommercialResetPreset", true).Single()).PerformClick(); Application.DoEvents();
                    Assert.Equal(CommercialDetectionPreset.Standard.ToString(), preset.SelectedItem);
                    Assert.Equal(.15M, blackDuration.Value);
                    foreach (string name in new[] { "CommercialSourcePath", "CommercialAnalyzeButton", "CommercialCancelButton", "CommercialMediaPreview", "CommercialDetectionTimeline", "CommercialPreviousBoundary", "CommercialNextBoundary", "CommercialAddBoundary", "CommercialRemoveBoundary", "CommercialPlayAcrossBoundary", "CommercialBeforeFrame", "CommercialAfterFrame", "CommercialTimelineScroll", "CommercialSegmentsGrid", "CommercialOutputDirectory", "CommercialExportSelected", "CommercialExportAll" })
                    {
                        Control control = form.Controls.Find(name, true).Single();
                        Assert.True(control.Visible, $"{name} should be visible at {size}.");
                        Assert.True(control.Width > 0 && control.Height > 0, $"{name} should have usable bounds at {size}.");
                        Assert.True(form.RectangleToScreen(form.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), $"{name} should not be clipped outside the form at {size}.");
                    }
                    Assert.False(form.Controls.Find("CommercialExportSelected", true).Single().Enabled);
                    Assert.False(form.Controls.Find("CommercialExportAll", true).Single().Enabled);
                }
                form.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Commercial Detector layout test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        Directory.Delete(root, recursive: true);
    }

    private static CommercialBoundary Candidate(double timestamp) => new(timestamp, 90, CommercialDetectionConfidence.High,
        new[] { new DetectionEvidence(DetectionSignalKind.Black, timestamp, "test") });

    private sealed class FixedProbe(bool hasAudio) : IMediaProbeService
    {
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new MediaProbeResult
        {
            Success = true, DurationSeconds = 100,
            Streams = hasAudio
                ? new[] { new MediaProbeStreamInfo { CodecType = "video" }, new MediaProbeStreamInfo { CodecType = "audio" } }
                : new[] { new MediaProbeStreamInfo { CodecType = "video" } }
        });
    }

    private sealed class ScriptedRunner(Func<MediaToolProcessRequest, MediaToolProcessResult> run) : IMediaToolProcessRunner
    {
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default) => Task.FromResult(run(request));
    }
}

public sealed class CommercialReviewStateTests
{
    [Fact]
    public void AddMoveResetAndRemoveTrackBoundaryOrigin()
    {
        var state = State(100, 25);
        CommercialReviewBoundary automatic = Assert.Single(state.Boundaries);
        Assert.Equal(CommercialBoundaryOrigin.Automatic, automatic.Origin);
        Assert.True(state.TryMoveBoundary(automatic.Id, 26));
        CommercialReviewBoundary moved = Assert.Single(state.Boundaries);
        Assert.Equal(CommercialBoundaryOrigin.AutomaticMoved, moved.Origin);
        Assert.Equal(25, moved.OriginalDetectedTimestampSeconds);
        Assert.True(state.TryResetBoundary(moved.Id));
        Assert.Equal(25, Assert.Single(state.Boundaries).TimestampSeconds);
        Assert.True(state.TryAddBoundary(50, out CommercialReviewBoundary? manual));
        Assert.Equal(CommercialBoundaryOrigin.Manual, manual!.Origin);
        Assert.True(state.TryRemoveBoundary(manual.Id));
        Assert.Single(state.Boundaries);
    }

    [Fact]
    public void SegmentEditsRemainContiguousAndRejectSourceEdgesAndDuplicates()
    {
        var state = State(100, 25, 75);
        Assert.False(state.TryAddBoundary(0, out _));
        Assert.False(state.TryAddBoundary(100, out _));
        Assert.False(state.TryAddBoundary(25.005, out _));
        Assert.True(state.TrySplitSegment(1, 50));
        Assert.Equal(new[] { 0d, 25d, 50d, 75d }, state.Segments.Select(segment => segment.StartSeconds));
        Assert.All(state.Segments, segment => Assert.True(segment.DurationSeconds > 0));
        Assert.True(state.TryMergePrevious(2));
        Assert.Equal(new[] { 0d, 25d, 75d }, state.Segments.Select(segment => segment.StartSeconds));
        Assert.True(state.TryMergeNext(0));
        Assert.Equal(new[] { 0d, 75d }, state.Segments.Select(segment => segment.StartSeconds));
        Assert.Equal(100, state.Segments[^1].EndSeconds);
    }

    [Fact]
    public void GeneratedNamesReindexWhileCustomNamesFollowBestOverlap()
    {
        var state = State(90, 30, 60);
        Assert.Equal("show_Commercial_02.mkv", state.Segments[1].OutputName);
        Assert.True(state.TrySetOutputName(1, "Act Two.mkv"));
        Assert.True(state.TrySplitSegment(1, 40));
        Assert.Equal("show_Commercial_02.mkv", state.Segments[1].OutputName);
        Assert.Equal("Act Two.mkv", state.Segments[2].OutputName);
        Assert.True(state.Segments[2].IsOutputNameCustom);
        Assert.Equal("show_Commercial_04.mkv", state.Segments[3].OutputName);
    }

    [Fact]
    public void ReanalysisKeepRetainsManualWorkAndAvoidsNearDuplicates()
    {
        var state = State(100, 20, 60);
        Guid movedId = state.Boundaries[0].Id;
        Assert.True(state.TryMoveBoundary(movedId, 21));
        Assert.True(state.TryAddBoundary(40, out _));
        Assert.True(state.TrySetOutputName(1, "Custom Middle.mkv"));

        state.ApplyReanalysis(new[] { Detected(21.1), Detected(70) }, keepManualBoundaries: true, duplicateToleranceSeconds: .5);

        Assert.Equal(new[] { 21d, 40d, 70d }, state.Boundaries.Select(boundary => boundary.TimestampSeconds));
        Assert.Contains(state.Boundaries, boundary => boundary.Origin == CommercialBoundaryOrigin.AutomaticMoved);
        Assert.Contains(state.Boundaries, boundary => boundary.Origin == CommercialBoundaryOrigin.Manual);
        Assert.Contains(state.Segments, segment => segment.OutputName == "Custom Middle.mkv" && segment.IsOutputNameCustom);
    }

    [Fact]
    public void ReanalysisEverythingReplacesEditsAndGeneratedNames()
    {
        var state = State(100, 20);
        Assert.True(state.TryAddBoundary(40, out _));
        Assert.True(state.TrySetOutputName(0, "Custom.mkv"));
        state.ApplyReanalysis(new[] { Detected(70) }, keepManualBoundaries: false, duplicateToleranceSeconds: .5);
        CommercialReviewBoundary boundary = Assert.Single(state.Boundaries);
        Assert.Equal(70, boundary.TimestampSeconds);
        Assert.Equal(CommercialBoundaryOrigin.Automatic, boundary.Origin);
        Assert.DoesNotContain(state.Segments, segment => segment.IsOutputNameCustom);
    }

    [Fact]
    public void RemovedAutomaticBoundaryRemainsSuppressedWhenKeepingManualWork()
    {
        var state = State(100, 20, 60);
        Assert.True(state.TryRemoveBoundary(state.Boundaries[0].Id));
        Assert.True(state.HasManualChanges);
        state.ApplyReanalysis(new[] { Detected(20.2), Detected(80) }, keepManualBoundaries: true, duplicateToleranceSeconds: .5);
        Assert.Equal(new[] { 80d }, state.Boundaries.Select(boundary => boundary.TimestampSeconds));
        state.ApplyReanalysis(new[] { Detected(20.2), Detected(80) }, keepManualBoundaries: false, duplicateToleranceSeconds: .5);
        Assert.Equal(new[] { 20.2, 80d }, state.Boundaries.Select(boundary => boundary.TimestampSeconds));
    }

    [Fact]
    public void ExportPlanUsesPatternForGeneratedNamesAndPreservesCustomNames()
    {
        var state = State(100, 40);
        Assert.True(state.TrySetOutputName(1, "Sponsor Break.mkv"));

        CommercialSegmentExportPlan plan = CommercialSegmentExportPlanner.CreatePlan("C:\\media\\show.mkv", state.Segments, null, "{source}_Part_{index:000}");

        Assert.Empty(plan.Errors);
        Assert.Equal("show_Part_001.mkv", plan.Segments[0].OutputFileName);
        Assert.Equal("Sponsor Break.mkv", plan.Segments[1].OutputFileName);
    }

    [Fact]
    public void ExportPlanSelectedRowsKeepTheirReviewedSegmentNumbers()
    {
        var state = State(100, 20, 60);
        CommercialSegmentExportPlan plan = CommercialSegmentExportPlanner.CreatePlan("C:\\media\\show.mp4", state.Segments, new[] { 2 }, CommercialSegmentExportPlanner.DefaultNamingPattern);

        VideoSplitterSegment segment = Assert.Single(plan.Segments);
        Assert.Empty(plan.Errors);
        Assert.Equal(2, segment.Number);
        Assert.Equal("show_Commercial_02.mp4", segment.OutputFileName);
    }

    [Fact]
    public void ExportPlanRejectsDuplicateNamesInvalidRangesAndUnsupportedTokens()
    {
        var duplicate = new[]
        {
            new CommercialReviewSegment(1, 0, 10, "same.mp4", true),
            new CommercialReviewSegment(2, 10, 20, "same.mp4", true),
            new CommercialReviewSegment(3, 20, 20, "bad.mp4", true)
        };
        CommercialSegmentExportPlan plan = CommercialSegmentExportPlanner.CreatePlan("C:\\media\\show.mp4", duplicate, null, "{unknown}");

        Assert.Contains(plan.Errors, error => error.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Errors, error => error.Contains("start", StringComparison.OrdinalIgnoreCase));

        CommercialSegmentExportPlan invalidPattern = CommercialSegmentExportPlanner.CreatePlan("C:\\media\\show.mp4", new[] { new CommercialReviewSegment(1, 0, 10, "", false) }, null, "{unknown}");
        Assert.Contains(invalidPattern.Errors, error => error.Contains("pattern", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExistingOutputAutoRenameReservesUniquePaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-CommercialExport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "show_Commercial_01.mp4"), "existing");
            VideoSplitterSegment[] renamed = VideoSplitterForm.AutoRenameConflictingOutputs(new[]
            {
                new VideoSplitterSegment(1, 0, 10, "show_Commercial_01.mp4"),
                new VideoSplitterSegment(2, 10, 20, "show_Commercial_01.mp4")
            }, root);

            Assert.Equal(new[] { "show_Commercial_01 (2).mp4", "show_Commercial_01 (3).mp4" }, renamed.Select(segment => segment.OutputFileName));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static CommercialReviewState State(double duration, params double[] boundaries)
    {
        var state = new CommercialReviewState();
        state.Initialize("C:\\media\\show.mkv", duration, boundaries.Select(Detected));
        return state;
    }

    private static CommercialBoundary Detected(double timestamp) => new(timestamp, 80, CommercialDetectionConfidence.High,
        new[] { new DetectionEvidence(DetectionSignalKind.Black, timestamp, "Black") });
}

public sealed class CommercialDetectorPersistenceTests
{
    [Fact]
    public void ConfigRoundTripsCommercialDetectorPreferences()
    {
        string path = Path.Combine(Path.GetTempPath(), $"MediaFlux-CommercialConfig-{Guid.NewGuid():N}.json");
        try
        {
            var config = new Config
            {
                CommercialDetectorAdvancedExpanded = true,
                CommercialDetectorPreferences = new CommercialDetectorPreferences
                {
                    DetectionPreset = nameof(CommercialDetectionPreset.Aggressive),
                    Settings = CommercialDetectionSettings.Aggressive,
                    ExportModeIndex = 1,
                    FilenameTemplate = "{source}_Spot_{index:000}"
                }
            };
            config.Save(path);
            Config restored = Config.Load(path);

            Assert.True(restored.CommercialDetectorAdvancedExpanded);
            Assert.Equal(nameof(CommercialDetectionPreset.Aggressive), restored.CommercialDetectorPreferences.DetectionPreset);
            Assert.Equal(1, restored.CommercialDetectorPreferences.ExportModeIndex);
            Assert.Equal("{source}_Spot_{index:000}", restored.CommercialDetectorPreferences.FilenameTemplate);
            Assert.Equal(.12, restored.CommercialDetectorPreferences.Settings.MinimumSilenceDurationSeconds, 2);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void AnalysisStoreRoundTripsReviewStateAndRejectsChangedSource()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-CommercialAnalysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "show.mp4"); string storePath = Path.Combine(root, "analysis.json");
            File.WriteAllText(source, "source-v1");
            var review = new CommercialReviewState();
            review.Initialize(source, 100, new[] { Detected(25) });
            Assert.True(review.TryMoveBoundary(review.Boundaries[0].Id, 26));
            Assert.True(review.TryAddBoundary(60, out _));
            Assert.True(review.TrySetOutputName(1, "Custom break.mp4"));
            var store = new CommercialAnalysisStore(storePath);
            store.Save(source, 100, CommercialDetectionPreset.Standard, CommercialDetectionSettings.Standard, review);

            CommercialAnalysisLookup lookup = store.Find(source, 100);
            CommercialAnalysisSnapshot snapshot = Assert.IsType<CommercialAnalysisSnapshot>(lookup.Snapshot);
            Assert.Equal(CommercialAnalysisMatch.Exact, lookup.Match);
            Assert.Contains(snapshot.Boundaries, item => item.Origin == nameof(CommercialBoundaryOrigin.AutomaticMoved) && item.OriginalDetectedTimestampSeconds == 25);
            Assert.Contains(snapshot.Boundaries, item => item.Origin == nameof(CommercialBoundaryOrigin.Manual));
            Assert.Contains(snapshot.Segments, item => item.OutputName == "Custom break.mp4" && item.IsOutputNameCustom);

            File.AppendAllText(source, "changed");
            Assert.Equal(CommercialAnalysisMatch.Stale, store.Find(source, 100).Match);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RestoredReviewKeepsManualAndCustomState()
    {
        string source = "C:\\media\\show.mkv";
        var original = new CommercialReviewState();
        original.Initialize(source, 90, new[] { Detected(30) });
        Assert.True(original.TryMoveBoundary(original.Boundaries[0].Id, 31));
        Assert.True(original.TryAddBoundary(60, out _));
        Assert.True(original.TrySetOutputName(1, "Manual name.mkv"));

        var restored = new CommercialReviewState();
        restored.Restore(source, 90, original.Boundaries, original.Segments);

        Assert.Contains(restored.Boundaries, item => item.Origin == CommercialBoundaryOrigin.AutomaticMoved && item.OriginalDetectedTimestampSeconds == 30);
        Assert.Contains(restored.Boundaries, item => item.Origin == CommercialBoundaryOrigin.Manual);
        Assert.Contains(restored.Segments, item => item.OutputName == "Manual name.mkv" && item.IsOutputNameCustom);
    }

    private static CommercialBoundary Detected(double timestamp) => new(timestamp, 80, CommercialDetectionConfidence.High,
        new[] { new DetectionEvidence(DetectionSignalKind.Black, timestamp, "Black") });
}
