using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.EncodingV2;
using Xunit;
using Xunit.Abstractions;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class EncodingV2QueueAdmissionUiTests
{
    private readonly ITestOutputHelper _output;

    public EncodingV2QueueAdmissionUiTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ProvisionalQueueRowIsNotEligibleUntilAdmissionChecksFinish()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Exception? failure = null;
        MainForm? main = null;
        string? configPath = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                configPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow row = queue.Rows[queue.Rows.Add()];
                row.Cells["colName"].Value = "provisional.mp4";
                row.Cells["colStatus"].Value = "Preparing";
                object meta = Invoke(main, "EnsureRowMeta", row)!;
                SetMetaField(meta, "QueueAdmissionPending", true);
                SetField(main, "_suppressRowEvents", false);

                IEnumerable<DataGridViewRow> eligible = EligibleRows(main);
                Assert.DoesNotContain(row, eligible);

                SetMetaField(meta, "QueueAdmissionPending", false);
                Assert.Contains(row, EligibleRows(main));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (main != null)
                    WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(configPath);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Queue admission UI test timed out.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void CoordinatorResultProjectsNumericAdvisoryEstimateIntoQueueGrid()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Exception? failure = null;
        MainForm? main = null;
        string? configPath = null;
        string? testRoot = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                configPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                testRoot = Path.Combine(Path.GetTempPath(), "MediaFluxV2QueueUi-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(testRoot);
                string path = Path.Combine(testRoot, "source.mp4");
                File.WriteAllBytes(path, Enumerable.Range(0, 4096).Select(value => (byte)value).ToArray());
                Assert.True((bool)Invoke(main, "AddEncodeItemIfNotPresent", path, false, false)!);
                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                DataGridViewRow row = queue.Rows.Cast<DataGridViewRow>()
                    .Single(candidate => candidate.Tag?.GetType().GetField("Path")?.GetValue(candidate.Tag)?.ToString() == path);
                object meta = row.Tag!;
                Guid queueItemId = (Guid)meta.GetType().GetField("QueueItemId")!.GetValue(meta)!;
                V2SourceFileIdentity identity = V2SourceFileIdentity.Capture(path);
                var facts = new V2FastEstimateSourceFacts
                {
                    SourcePath = path,
                    SourceSizeBytes = identity.SourceSizeBytes,
                    DurationSeconds = 30,
                    VideoCodec = "h264",
                    Width = 1920,
                    Height = 1080,
                    FrameRate = 24,
                    VideoBitrateKbps = 7000
                };
                var coordinator = new V2EstimateRefreshCoordinator(new V2SourceFactsCache((_, _) =>
                    throw new InvalidOperationException("Supplied source facts must prevent a probe.")));
                var previous = Field<V2EstimateRefreshCoordinator?>(main, "_v2EstimateCoordinator");
                previous?.Dispose();
                SetField(main, "_v2EstimateCoordinator", coordinator);
                V2EstimateRefreshTicket ticket = coordinator.Request(queueItemId, identity,
                    new V2FastEstimateCommandRequest
                    {
                        SourcePath = path,
                        SourceFacts = facts,
                        Encoder = "libx264",
                        Codec = "h264",
                        Container = "mp4",
                        QualityMode = "crf",
                        QualityValue = 24,
                        Preset = "medium",
                        AudioMode = "copy"
                    });
                V2EstimateRefreshResult result = ticket.Completion.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                SetMetaField(meta, "V2SourceIdentity", identity);
                SetMetaField(meta, "V2EstimateGeneration", ticket.Generation);
                Invoke(main, "ApplyV2EstimateResultsBatch");

                Assert.True(result.Estimate!.EstimatedTotalBytes > 0);
                Assert.NotEqual("", row.Cells["colEstimatedSize"].Value?.ToString());
                Assert.Contains("V2 fast estimate", row.Cells["colEstimatedSize"].ToolTipText, StringComparison.Ordinal);
                Assert.Contains("advisory", row.Cells["colEstimatedSize"].ToolTipText, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(result.Estimate.EstimatedTotalBytes / (1024d * 1024d),
                    Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path], 5);
                Assert.Equal(0, coordinator.SourceProbeCount);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (main != null)
                    WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(configPath);
                if (testRoot != null && Directory.Exists(testRoot))
                    Directory.Delete(testRoot, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Queue estimate projection UI test timed out.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void ManualEncodingSpeedChangeSchedulesExactlyOneFreshV2Estimate()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Exception? failure = null;
        MainForm? main = null;
        string? configPath = null;
        string? testRoot = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                configPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                Invoke(main, "SelectEncoderById", VideoEncoderIds.Libx265);
                Invoke(main, "RefreshVideoFormatItems", VideoCodecFamily.Hevc);
                Field<ComboBox>(main, "comboQualityMode").SelectedItem = "Manual • Legacy Numeric";
                Field<NumericUpDown>(main, "nudAutoQuality").Value = 24;
                Field<ComboBox>(main, "comboCompressionProfile").SelectedItem = "Medium Quality (Default)";
                Field<CheckBox>(main, "chkAutoTargetSize").Checked = true;
                Field<TextBox>(main, "txtTargetSize").Text = "";
                Field<CheckBox>(main, "chkTenBit").Checked = false;
                ComboBox speed = Field<ComboBox>(main, "comboEncoderPreset");
                object medium = speed.Items.Cast<object>().Single(item =>
                    string.Equals(item.GetType().GetProperty("Value")?.GetValue(item)?.ToString(), "medium", StringComparison.OrdinalIgnoreCase));
                speed.SelectedItem = medium;

                testRoot = Path.Combine(Path.GetTempPath(), "MediaFluxManualSpeedRefresh-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(testRoot);
                string path = Path.Combine(testRoot, "source.mp4");
                File.WriteAllBytes(path, Enumerable.Range(0, 4096).Select(value => (byte)value).ToArray());
                V2SourceFileIdentity identity = V2SourceFileIdentity.Capture(path);
                V2FastEstimateSourceFacts facts = new()
                {
                    SourcePath = path,
                    SourceSizeBytes = identity.SourceSizeBytes,
                    DurationSeconds = 30,
                    VideoCodec = "h264",
                    Width = 1920,
                    Height = 1080,
                    FrameRate = 24,
                    VideoBitrateKbps = 3000
                };

                var coordinator = new V2EstimateRefreshCoordinator(new V2SourceFactsCache((sourcePath, _) =>
                    Task.FromResult(facts with { SourcePath = sourcePath })));
                Field<V2EstimateRefreshCoordinator?>(main, "_v2EstimateCoordinator")?.Dispose();
                SetField(main, "_v2EstimateCoordinator", coordinator);

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow row;
                object meta;
                try
                {
                    row = queue.Rows[queue.Rows.Add()];
                    row.Tag = path;
                    row.Cells["colName"].Value = Path.GetFileName(path);
                    row.Cells["colStatus"].Value = "Queued";
                    meta = Invoke(main, "EnsureRowMeta", row)!;
                    SetMetaField(meta, "Path", path);
                    Field<System.Collections.Concurrent.ConcurrentDictionary<string, DataGridViewRow>>(main, "_rowsByPath")[path] = row;
                }
                finally
                {
                    SetField(main, "_suppressRowEvents", false);
                }

                int productionQuality = (int)Invoke(main, "GetDefaultQualityForSelection")!;
                int speedHandlers = ControlEventHandlerCount(speed, "SelectedIndexChanged");
                Assert.Equal(1, speedHandlers);

                Invoke(main, "SafeRefreshEstimates");
                PumpMainFormUntil(main, () =>
                {
                    V2EstimateResultForTest? current = CurrentV2Estimate(meta);
                    return current is { Preset: "medium" } && coordinator.PendingWorkCount == 0;
                }, "Initial Manual estimate did not settle at the medium speed preset.");

                V2EstimateResultForTest initial = CurrentV2Estimate(meta)
                    ?? throw new Xunit.Sdk.XunitException("Initial V2 estimate was not applied.");
                Assert.Equal(24d, initial.QualityValue);
                Assert.Equal(productionQuality, (int)Invoke(main, "GetDefaultQualityForSelection")!);
                Assert.Contains("V2 fast estimate", row.Cells["colEstimatedSize"].ToolTipText, StringComparison.Ordinal);
                Assert.Contains("advisory", row.Cells["colEstimatedSize"].ToolTipText, StringComparison.OrdinalIgnoreCase);
                long initialGeneration = (long)meta.GetType().GetField("V2EstimateGeneration")!.GetValue(meta)!;
                double initialEstimateMb = Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path];

                object slow = speed.Items.Cast<object>().Single(item =>
                    string.Equals(item.GetType().GetProperty("Value")?.GetValue(item)?.ToString(), "slow", StringComparison.OrdinalIgnoreCase));
                speed.SelectedItem = slow;
                PumpMainFormUntil(main, () =>
                {
                    V2EstimateResultForTest? current = CurrentV2Estimate(meta);
                    return current is { Preset: "slow" } &&
                           (long)meta.GetType().GetField("V2EstimateGeneration")!.GetValue(meta)! > initialGeneration &&
                           coordinator.PendingWorkCount == 0;
                }, "Changing Manual Encoding Speed did not schedule a fresh V2 estimate.");

                V2EstimateResultForTest refreshed = CurrentV2Estimate(meta)
                    ?? throw new Xunit.Sdk.XunitException("Refreshed V2 estimate was not applied.");
                long refreshedGeneration = (long)meta.GetType().GetField("V2EstimateGeneration")!.GetValue(meta)!;
                double refreshedEstimateMb = Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path];
                Assert.Equal(initialGeneration + 1, refreshedGeneration);
                Assert.Equal("slow", refreshed.Preset);
                Assert.Equal(24d, refreshed.QualityValue);
                Assert.NotEqual(initial.EstimatedTotalBytes, refreshed.EstimatedTotalBytes);
                Assert.True(refreshed.EstimatedTotalBytes < initial.EstimatedTotalBytes,
                    $"The slower x264 preset should lower this fixed fixture estimate: medium={initial.EstimatedTotalBytes}, slow={refreshed.EstimatedTotalBytes}.");
                Assert.NotEqual(initialEstimateMb, refreshedEstimateMb);
                Assert.Equal(refreshedEstimateMb, Field<double>(main, "_queueTotalEstimatedMb"), 5);
                Assert.Equal(FormatExpectedEstimate(refreshedEstimateMb), row.Cells["colEstimatedSize"].Value?.ToString());
                Assert.Equal(row.Cells["colEstimatedSize"].Value?.ToString(), Field<Label>(main, "_summaryNewSizeValue").Text);
                Assert.Equal("slow", Config.Load(configPath).LastEncodingSpeedPreset);
                Assert.Equal(productionQuality, (int)Invoke(main, "GetDefaultQualityForSelection")!);
                Assert.Equal(1, ControlEventHandlerCount(speed, "SelectedIndexChanged"));

                long unchangedGeneration = refreshedGeneration;
                speed.SelectedItem = slow;
                Application.DoEvents();
                Assert.Equal(unchangedGeneration, (long)meta.GetType().GetField("V2EstimateGeneration")!.GetValue(meta)!);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (main != null)
                    WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(configPath);
                if (testRoot != null && Directory.Exists(testRoot))
                    Directory.Delete(testRoot, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Manual speed estimate refresh UI test timed out.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void LiveSelectedIndexChangedRefreshesManualV2EstimateForEveryPreset()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Exception? failure = null;
        MainForm? main = null;
        string? configPath = null;
        string? testRoot = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                configPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                Invoke(main, "SelectEncoderById", VideoEncoderIds.Libx264);
                Invoke(main, "RefreshVideoFormatItems", VideoCodecFamily.H264);
                Field<ComboBox>(main, "comboQualityMode").SelectedItem = "Manual • Legacy Numeric";
                Field<NumericUpDown>(main, "nudAutoQuality").Value = 24;
                Field<ComboBox>(main, "comboCompressionProfile").SelectedItem = "Medium Quality (Default)";
                Field<CheckBox>(main, "chkAutoTargetSize").Checked = true;
                Field<TextBox>(main, "txtTargetSize").Text = "";

                testRoot = Path.Combine(Path.GetTempPath(), "MediaFluxV2PresetRefresh-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(testRoot);
                string path = Path.Combine(testRoot, "source.mp4");
                File.WriteAllBytes(path, Enumerable.Range(0, 4096).Select(value => (byte)value).ToArray());
                V2SourceFileIdentity identity = V2SourceFileIdentity.Capture(path);
                V2FastEstimateSourceFacts facts = new()
                {
                    SourcePath = path,
                    SourceSizeBytes = identity.SourceSizeBytes,
                    DurationSeconds = 30,
                    VideoCodec = "h264",
                    Width = 1920,
                    Height = 1080,
                    FrameRate = 24,
                    VideoBitrateKbps = 3000
                };

                int probeCount = 0;
                var coordinator = new V2EstimateRefreshCoordinator(new V2SourceFactsCache((sourcePath, _) =>
                {
                    Interlocked.Increment(ref probeCount);
                    return Task.FromResult(facts with { SourcePath = sourcePath });
                }));
                Field<V2EstimateRefreshCoordinator?>(main, "_v2EstimateCoordinator")?.Dispose();
                SetField(main, "_v2EstimateCoordinator", coordinator);

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow row;
                object meta;
                try
                {
                    row = queue.Rows[queue.Rows.Add()];
                    row.Tag = path;
                    row.Cells["colName"].Value = Path.GetFileName(path);
                    row.Cells["colStatus"].Value = "Queued";
                    meta = Invoke(main, "EnsureRowMeta", row)!;
                    SetMetaField(meta, "Path", path);
                    Field<System.Collections.Concurrent.ConcurrentDictionary<string, DataGridViewRow>>(main, "_rowsByPath")[path] = row;
                }
                finally
                {
                    SetField(main, "_suppressRowEvents", false);
                }

                int productionQualityBefore = (int)Invoke(main, "GetDefaultQualityForSelection")!;
                Invoke(main, "SafeRefreshEstimates");
                WaitForEstimate(coordinator);
                Invoke(main, "ApplyV2EstimateResultsBatch");
                V2FastEstimateResult? initial = (V2FastEstimateResult?)meta.GetType()
                    .GetField("V2EstimateResult")!.GetValue(meta);
                Assert.NotNull(initial);
                Assert.Equal(24d, initial.QualityValue);
                double initialOutputMb = Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path];
                long previousGeneration = (long)meta.GetType().GetField("V2EstimateGeneration")!.GetValue(meta)!;
                Assert.Equal(1, Volatile.Read(ref probeCount));

                ComboBox profile = Field<ComboBox>(main, "comboCompressionProfile");
                string[] presets =
                [
                    "Very High Quality (Largest File)",
                    "High Quality",
                    "Medium Quality (Default)",
                    "Low Quality (Smaller File)",
                    "Very Low Quality (Smallest File)"
                ];
                double[] outputBytes = new double[presets.Length];
                for (int index = 0; index < presets.Length; index++)
                {
                    string selectedPreset = presets[index];
                    profile.SelectedItem = selectedPreset;
                    double expectedEstimateQuality = ExpectedManualEstimateQuality(24, selectedPreset);
                    PumpMainFormUntil(main, () =>
                    {
                        V2FastEstimateResult? current = (V2FastEstimateResult?)meta.GetType()
                            .GetField("V2EstimateResult")!.GetValue(meta);
                        return current?.QualityValue == expectedEstimateQuality &&
                               (long)meta.GetType().GetField("V2EstimateGeneration")!.GetValue(meta)! > previousGeneration &&
                               coordinator.PendingWorkCount == 0;
                    }, $"Live estimate refresh did not settle for profile '{selectedPreset}'.");

                    V2FastEstimateResult refreshed = (V2FastEstimateResult)meta.GetType()
                        .GetField("V2EstimateResult")!.GetValue(meta)!;
                    long refreshedGeneration = (long)meta.GetType().GetField("V2EstimateGeneration")!.GetValue(meta)!;
                    Assert.True(refreshedGeneration > previousGeneration);
                    previousGeneration = refreshedGeneration;
                    Assert.Equal(expectedEstimateQuality, refreshed.QualityValue, 10);
                    Assert.Equal(24, (int)Invoke(main, "GetDefaultQualityForSelection")!);
                    Assert.Equal(productionQualityBefore, (int)Invoke(main, "GetDefaultQualityForSelection")!);
                    Assert.Equal(1, Volatile.Read(ref probeCount));
                    Assert.Equal(1, coordinator.SourceProbeCount);
                    Assert.Contains("Source facts: reused", row.Cells["colEstimatedSize"].ToolTipText, StringComparison.Ordinal);

                    double estimateMb = refreshed.EstimatedTotalBytes / (1024d * 1024d);
                    outputBytes[index] = refreshed.EstimatedTotalBytes;
                    Assert.Equal(estimateMb, Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path], 5);
                    Assert.Matches(@"^\d+(\.\d+)? MB$", row.Cells["colEstimatedSize"].Value?.ToString() ?? "");
                    Assert.Equal(estimateMb, Field<double>(main, "_queueTotalEstimatedMb"), 5);
                    Assert.Equal(row.Cells["colEstimatedSize"].Value?.ToString(), Field<Label>(main, "_summaryNewSizeValue").Text);
                }

                Assert.Equal(24d, initial.QualityValue);
                Assert.Equal(initialOutputMb, outputBytes[2] / (1024d * 1024d), 5);
                Assert.True(outputBytes[0] > outputBytes[1]);
                Assert.True(outputBytes[1] > outputBytes[2]);
                Assert.True(outputBytes[2] > outputBytes[3]);
                Assert.True(outputBytes[3] > outputBytes[4]);

                // Several real combo changes before the WinForms timer fires must leave
                // the row showing the last selected preset after the normal debounce.
                long rapidChangeGeneration = previousGeneration;
                profile.SelectedItem = presets[0];
                profile.SelectedItem = presets[3];
                profile.SelectedItem = presets[1];
                profile.SelectedItem = presets[4];
                PumpMainFormUntil(main, () =>
                {
                    V2FastEstimateResult? current = (V2FastEstimateResult?)meta.GetType()
                        .GetField("V2EstimateResult")!.GetValue(meta);
                    return current?.QualityValue == ExpectedManualEstimateQuality(24, presets[4]) &&
                           (long)meta.GetType().GetField("V2EstimateGeneration")!.GetValue(meta)! > rapidChangeGeneration &&
                           coordinator.PendingWorkCount == 0;
                }, "Rapid preset changes did not settle on the final selected profile.");
                Assert.Equal(ExpectedManualEstimateQuality(24, presets[4]),
                    ((V2FastEstimateResult)meta.GetType().GetField("V2EstimateResult")!.GetValue(meta)!).QualityValue, 10);
                Assert.Equal(24, (int)Invoke(main, "GetDefaultQualityForSelection")!);
                Assert.Equal(1, Volatile.Read(ref probeCount));
                Assert.Equal(1, coordinator.SourceProbeCount);
                Assert.Equal(0, coordinator.StaleSuppressedCount);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (main != null)
                    WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(configPath);
                if (testRoot != null && Directory.Exists(testRoot))
                    Directory.Delete(testRoot, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "File-size estimate preset UI test timed out.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void LiveManualPresetRefreshUsesProfileWhenTenBitFallsBackToLegacyEstimate()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Exception? failure = null;
        MainForm? main = null;
        string? configPath = null;
        string? testRoot = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                configPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                Config config = Field<Config>(main, "_config");
                config.StorageSavings.Enabled = true;
                config.StorageSavings.TargetMode = StorageSavingsOptions.SourceBitrateTarget;
                config.StorageSavings.SourceVideoBitratePercent = 50;
                config.SmartRecommendationsEnabled = false;
                config.VideoRestoration = new();

                Invoke(main, "SelectEncoderById", VideoEncoderIds.Libx265);
                Invoke(main, "RefreshVideoFormatItems", VideoCodecFamily.Hevc);
                Field<ComboBox>(main, "comboQualityMode").SelectedItem = "Manual • Legacy Numeric";
                Field<NumericUpDown>(main, "nudAutoQuality").Value = 27;
                Field<ComboBox>(main, "comboCompressionProfile").SelectedItem = "Medium Quality (Default)";
                Field<CheckBox>(main, "chkAutoTargetSize").Checked = true;
                Field<TextBox>(main, "txtTargetSize").Text = "";
                Field<CheckBox>(main, "chkTenBit").Checked = true;

                testRoot = Path.Combine(Path.GetTempPath(), "MediaFluxLegacyPresetRefresh-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(testRoot);
                string path = Path.Combine(testRoot, "source.mp4");
                using (var source = new FileStream(path, FileMode.CreateNew))
                    source.SetLength(15_784_043);

                int metadataReadCount = 0;
                var sourceInfo = new MediaInfoService.MediaInfo
                {
                    FormatName = "mov,mp4,m4a,3gp,3g2,mj2",
                    VideoCodec = "h264",
                    Width = 608,
                    Height = 1080,
                    Fps = 30,
                    DurationSeconds = 31.5,
                    BitrateKbps = 4064,
                    TotalBitrateKbps = 4195,
                    AudioBitrateKbps = 129,
                    AudioStreamCount = 1
                };
                var previousService = Field<EstimateBackgroundService>(main, "_estimateService");
                var legacyService = new EstimateBackgroundService(
                    Field<MediaInfoService>(main, "_mediaInfoService"),
                    null!,
                    _ =>
                    {
                        Interlocked.Increment(ref metadataReadCount);
                        return sourceInfo;
                    });
                previousService.Dispose();
                SetField(main, "_estimateService", legacyService);

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow row;
                object meta;
                try
                {
                    row = queue.Rows[queue.Rows.Add()];
                    row.Tag = path;
                    row.Cells["colName"].Value = Path.GetFileName(path);
                    row.Cells["colStatus"].Value = "Queued";
                    meta = Invoke(main, "EnsureRowMeta", row)!;
                    SetMetaField(meta, "Path", path);
                    Field<System.Collections.Concurrent.ConcurrentDictionary<string, DataGridViewRow>>(
                        main, "_rowsByPath")[path] = row;
                }
                finally
                {
                    SetField(main, "_suppressRowEvents", false);
                }

                int productionQualityBefore = (int)Invoke(main, "GetDefaultQualityForSelection")!;
                Invoke(main, "SafeRefreshEstimates");
                PumpMainFormUntil(main, () =>
                    Field<Dictionary<string, double>>(main, "_estimatedSizeMap").ContainsKey(path) &&
                    legacyService.PendingEstimates == 0,
                    "Initial legacy estimate did not reach the queue row.");

                V2EstimateRefreshCoordinator coordinator = Field<V2EstimateRefreshCoordinator>(main, "_v2EstimateCoordinator");
                Assert.Equal(0, coordinator.SourceProbeCount);
                Assert.Null(meta.GetType().GetField("V2EstimateResult")!.GetValue(meta));

                string[] presets =
                [
                    "Very High Quality (Largest File)",
                    "High Quality",
                    "Medium Quality (Default)",
                    "Low Quality (Smaller File)",
                    "Very Low Quality (Smallest File)"
                ];
                double[] estimatesMb = new double[presets.Length];
                ComboBox profile = Field<ComboBox>(main, "comboCompressionProfile");
                for (int index = 0; index < presets.Length; index++)
                {
                    double previousEstimate = Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path];
                    profile.SelectedItem = presets[index];
                    PumpMainFormUntil(main, () =>
                    {
                        Dictionary<string, double> map = Field<Dictionary<string, double>>(main, "_estimatedSizeMap");
                        return map.TryGetValue(path, out double estimate) &&
                               Math.Abs(estimate - previousEstimate) > 0.000001 &&
                               legacyService.PendingEstimates == 0;
                    }, $"Legacy estimate did not refresh for profile '{presets[index]}'.");

                    estimatesMb[index] = Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path];
                    Assert.Equal(estimatesMb[index], Field<double>(main, "_queueTotalEstimatedMb"), 5);
                    Assert.Equal(FormatExpectedEstimate(estimatesMb[index]), row.Cells["colEstimatedSize"].Value?.ToString());
                    Assert.Equal(productionQualityBefore, (int)Invoke(main, "GetDefaultQualityForSelection")!);
                }

                Assert.True(estimatesMb[0] > estimatesMb[1]);
                Assert.True(estimatesMb[1] > estimatesMb[2]);
                Assert.True(estimatesMb[2] > estimatesMb[3]);
                Assert.True(estimatesMb[3] > estimatesMb[4]);
                Assert.Equal(1, Volatile.Read(ref metadataReadCount));
                Assert.Equal(0, coordinator.SourceProbeCount);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (main != null)
                    WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(configPath);
                if (testRoot != null && Directory.Exists(testRoot))
                    Directory.Delete(testRoot, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Legacy file-size estimate preset test timed out.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void LiveManualLegacyNvencHevcTenBitPresetChangesRefreshTheLegacyEstimate()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Exception? failure = null;
        MainForm? main = null;
        string? configPath = null;
        string? testRoot = null;
        var trace = new List<string>();
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                configPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                Config config = Field<Config>(main, "_config");
                config.StorageSavings.Enabled = true;
                config.StorageSavings.TargetMode = StorageSavingsOptions.SourceBitrateTarget;
                config.StorageSavings.SourceVideoBitratePercent = 50;
                config.SmartRecommendationsEnabled = false;
                config.VideoRestoration = new();

                Invoke(main, "SelectEncoderById", VideoEncoderIds.Nvenc);
                Invoke(main, "RefreshVideoFormatItems", VideoCodecFamily.Hevc);
                Field<ComboBox>(main, "comboQualityMode").SelectedItem = "Manual • Legacy Numeric";
                Field<NumericUpDown>(main, "nudAutoQuality").Value = 27;
                Field<ComboBox>(main, "comboCompressionProfile").SelectedItem = "Medium Quality (Default)";
                Field<CheckBox>(main, "chkAutoTargetSize").Checked = true;
                Field<TextBox>(main, "txtTargetSize").Text = "";
                Field<CheckBox>(main, "chkTenBit").Checked = true;
                ComboBox speed = Field<ComboBox>(main, "comboEncoderPreset");

                testRoot = Path.Combine(Path.GetTempPath(), "MediaFluxManualNvencTenBitPreset-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(testRoot);
                string path = Path.Combine(testRoot, "source.mp4");
                using (var source = new FileStream(path, FileMode.CreateNew))
                    source.SetLength(15_784_043);

                var sourceInfo = new MediaInfoService.MediaInfo
                {
                    FormatName = "mov,mp4,m4a,3gp,3g2,mj2",
                    VideoCodec = "h264",
                    Width = 608,
                    Height = 1080,
                    Fps = 30,
                    DurationSeconds = 31.5,
                    BitrateKbps = 4064,
                    TotalBitrateKbps = 4195,
                    AudioBitrateKbps = 129,
                    AudioStreamCount = 1
                };
                var previousService = Field<EstimateBackgroundService>(main, "_estimateService");
                int metadataReadCount = 0;
                var legacyService = new EstimateBackgroundService(
                    Field<MediaInfoService>(main, "_mediaInfoService"),
                    null!,
                    _ =>
                    {
                        Interlocked.Increment(ref metadataReadCount);
                        return sourceInfo;
                    });
                previousService.Dispose();
                SetField(main, "_estimateService", legacyService);

                object FindPreset(string value) => speed.Items.Cast<object>().Single(item =>
                    string.Equals(item.GetType().GetProperty("Value")?.GetValue(item)?.ToString(), value, StringComparison.OrdinalIgnoreCase));
                string PresetValue(object item) =>
                    item.GetType().GetProperty("Value")?.GetValue(item)?.ToString() ?? "";

                object p1 = FindPreset("p1");
                object p4 = FindPreset("p4");
                object p6 = FindPreset("p6");
                speed.SelectedItem = p1;

                VideoEncoderSelection selectedEncoder = (VideoEncoderSelection)Invoke(main, "GetSelectedVideoEncoderSelection")!;
                int selectedQuality = (int)Invoke(main, "GetDefaultQualityForSelection")!;
                Assert.Equal(VideoEncoderIds.Nvenc, selectedEncoder.EncoderId);
                Assert.Equal("hevc_nvenc", selectedEncoder.FfmpegCodec);
                Assert.Equal(27, selectedQuality);
                Assert.Equal("Manual • Legacy Numeric", Field<ComboBox>(main, "comboQualityMode").SelectedItem?.ToString());

                bool v2Accepted = V2QueueEstimateRequestMapper.TryCreate(
                    path,
                    selectedEncoder,
                    selectedQuality,
                    "Medium Quality (Default)",
                    PresetValue(p1),
                    OutputContainerSelection.Auto,
                    null,
                    (int?)Invoke(main, "GetSelectedAudioChannels"),
                    tenBit: true,
                    adaptiveQuality: false,
                    targetSize: false,
                    customSettings: false,
                    restorationEnabled: false,
                    out _,
                    out string v2RejectionReason);
                Assert.False(v2Accepted);
                Assert.Contains("Ten-bit", v2RejectionReason, StringComparison.Ordinal);

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow row;
                object meta;
                try
                {
                    row = queue.Rows[queue.Rows.Add()];
                    row.Tag = path;
                    row.Cells["colName"].Value = Path.GetFileName(path);
                    row.Cells["colStatus"].Value = "Queued";
                    meta = Invoke(main, "EnsureRowMeta", row)!;
                    SetMetaField(meta, "Path", path);
                    Field<System.Collections.Concurrent.ConcurrentDictionary<string, DataGridViewRow>>(
                        main, "_rowsByPath")[path] = row;
                }
                finally
                {
                    SetField(main, "_suppressRowEvents", false);
                }

                Invoke(main, "SafeRefreshEstimates");
                var observations = new List<(string Preset, int Generation, double EstimateMb, double VideoKbps,
                    double AudioKbps, double AncillaryKbps, string Cell, string ActiveProfile, string Diagnostic)>();
                object[] selectedPresets = [p1, p4, p6];
                for (int index = 0; index < selectedPresets.Length; index++)
                {
                    object preset = selectedPresets[index];
                    int previousGeneration = legacyService.CurrentGeneration;
                    if (index > 0 && !ReferenceEquals(speed.SelectedItem, preset))
                        speed.SelectedItem = preset;

                    PumpMainFormUntil(main, () =>
                            (index == 0 || legacyService.CurrentGeneration > previousGeneration) &&
                            Field<Dictionary<string, double>>(main, "_estimatedSizeMap").ContainsKey(path) &&
                            legacyService.PendingEstimates == 0 &&
                            row.Cells["colEstimatedSize"].Value?.ToString() is not ("" or "Estimating…"),
                        $"Legacy NVENC estimate did not finish for preset '{PresetValue(preset)}'.");

                    int generation = legacyService.CurrentGeneration;
                    double estimateMb = Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path];
                    string activeProfile = Field<Label>(main, "_activeConfigurationSummaryLabel").Text;
                    object? calibration = meta.GetType().GetField("SizePredictionCalibration")!.GetValue(meta);
                    object? predictedVideoRate = calibration?.GetType()
                        .GetProperty("EstimatePredictedVideoBitrateKbps")?.GetValue(calibration);
                    double videoKbps = predictedVideoRate is double value ? value : double.NaN;
                    double audioKbps = (double)meta.GetType().GetField("EstimatedPlannedAudioBitrateKbps")!.GetValue(meta)!;
                    double ancillaryKbps = (double)meta.GetType().GetField("EstimatedPlannedMappedAncillaryBitrateKbps")!.GetValue(meta)!;
                    double targetTotalKbps = SizeEstimateService.CalculateTargetTotalBitrateKbps(
                        videoKbps, audioKbps + ancillaryKbps);
                    string cell = row.Cells["colEstimatedSize"].Value?.ToString() ?? "";
                    string diagnostic = (string)meta.GetType().GetField("EstimateDiagnostic")!.GetValue(meta)!;

                    if (index > 0)
                        Assert.Equal(previousGeneration + 1, generation);
                    Assert.Equal(FormatExpectedEstimate(estimateMb), cell);
                    PumpMainFormUntil(main,
                        () => Field<Label>(main, "_summaryNewSizeValue").Text == cell,
                        $"Queue summary did not refresh for preset '{PresetValue(preset)}'.");
                    Assert.Equal(estimateMb, Field<double>(main, "_queueTotalEstimatedMb"), 5);
                    Assert.Equal(cell, Field<Label>(main, "_summaryNewSizeValue").Text);
                    Assert.Contains(speed.Text, activeProfile, StringComparison.Ordinal);
                    Assert.Contains("Manual quality 27", activeProfile, StringComparison.Ordinal);
                    Assert.Equal(PresetValue(preset), Config.Load(configPath).LastEncoderPreset);
                    Assert.Equal(27, (int)Invoke(main, "GetDefaultQualityForSelection")!);
                    Assert.Null(meta.GetType().GetField("V2EstimateResult")!.GetValue(meta));
                    Assert.Equal(0, Field<V2EstimateRefreshCoordinator>(main, "_v2EstimateCoordinator").SourceProbeCount);
                    Assert.DoesNotContain("storage bitrate target", diagnostic, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("source floor applied", diagnostic, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("source ceiling applied", diagnostic, StringComparison.OrdinalIgnoreCase);

                    observations.Add((PresetValue(preset), generation, estimateMb, videoKbps, audioKbps,
                        ancillaryKbps, cell, activeProfile, diagnostic));
                    trace.Add($"preset={PresetValue(preset)} gen={generation} encoder={selectedEncoder.EncoderId}/{selectedEncoder.FfmpegCodec} " +
                        $"bitDepth=10 qualityMode=ManualLegacyNumeric CQ=27 estimateProfile=MediumQualityDefault " +
                        $"targetControl={(Field<CheckBox>(main, "chkAutoTargetSize").Checked ? "AutoOn" : "AutoOff")} blankTarget=true " +
                        $"intent=<none> rowProfileEstimate=true configuredStorageSavings=on/estimateClone=off " +
                        $"persistedPreset={Config.Load(configPath).LastEncoderPreset} v2=reject[{v2RejectionReason}] legacy=EstimateBackgroundService " +
                        $"mediaInfoVideoKbps=4064 normalizedSourceVideoKbps=4009 durationSec=31.5 " +
                        $"profileMultiplier={SizeEstimateService.GetCompressionMultiplier("Medium Quality (Default)"):0.###} " +
                        $"presetRateMultiplier={V2FastEstimateService.GetPresetRateMultiplier(selectedEncoder.FfmpegCodec, PresetValue(preset)):0.###} " +
                        $"targetVideoBeforeGuardrailsKbps={videoKbps:0.###} targetVideoAfterGuardrailsKbps={videoKbps:0.###} " +
                        $"guardrails=none targetTotalKbps={targetTotalKbps:0.###} audioKbps={audioKbps:0.###} ancillaryKbps={ancillaryKbps:0.###} " +
                        $"estimateMiB={estimateMb:0.#####} outputBytes={estimateMb * 1024d * 1024d:0.##} cell='{cell}' " +
                        $"queueTotalMiB={Field<double>(main, "_queueTotalEstimatedMb"):0.#####} active='{activeProfile}' " +
                        $"metadataReads={Volatile.Read(ref metadataReadCount)} diagnostic='{diagnostic}'");
                }

                Assert.True(observations[0].EstimateMb > observations[1].EstimateMb &&
                            observations[1].EstimateMb > observations[2].EstimateMb,
                    "Manual Legacy Numeric NVENC estimates must reflect the existing NVENC preset-rate model (p1 > p4 > p6). " +
                    string.Join(Environment.NewLine, observations.Select(item =>
                        $"{item.Preset}: {item.EstimateMb:0.#####} MiB, video={item.VideoKbps:0.###} kbps, {item.Diagnostic}")));

                int beforeBlankFallbackGeneration = legacyService.CurrentGeneration;
                speed.SelectedItem = p4;
                PumpMainFormUntil(main, () => legacyService.CurrentGeneration > beforeBlankFallbackGeneration &&
                    Field<Dictionary<string, double>>(main, "_estimatedSizeMap").ContainsKey(path) && legacyService.PendingEstimates == 0,
                    "Legacy p4 estimate did not refresh before testing blank-target fallback.");
                double p4AutoOnEstimate = Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path];
                int beforeBlankFallbackToggle = legacyService.CurrentGeneration;
                Field<CheckBox>(main, "chkAutoTargetSize").Checked = false;
                PumpMainFormUntil(main, () => legacyService.CurrentGeneration > beforeBlankFallbackToggle &&
                    Field<Dictionary<string, double>>(main, "_estimatedSizeMap").ContainsKey(path) && legacyService.PendingEstimates == 0,
                    "Auto Target OFF with a blank target did not complete the profile-estimate fallback.");
                double blankFallbackEstimate = Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path];
                Assert.Equal(p4AutoOnEstimate, blankFallbackEstimate, 5);
                Assert.Equal("", Field<TextBox>(main, "txtTargetSize").Text);
                trace.Add($"autoTargetOffBlank=p4 estimateMiB={blankFallbackEstimate:0.#####} " +
                    $"matchesAutoOn={p4AutoOnEstimate:0.#####} gen={legacyService.CurrentGeneration}");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (main != null)
                    WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(configPath);
                if (testRoot != null && Directory.Exists(testRoot))
                    Directory.Delete(testRoot, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Manual NVENC ten-bit preset estimate test timed out.");
        foreach (string line in trace)
            _output.WriteLine(line);
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static string FormatExpectedEstimate(double estimateMb) =>
        estimateMb >= 1024
            ? $"{estimateMb / 1024d:0.##} GB"
            : $"{estimateMb:0.#} MB";

    private static double ExpectedManualEstimateQuality(int productionQuality, string profile)
    {
        double profileScale = SizeEstimateService.GetCompressionMultiplier(profile);
        double mediumScale = SizeEstimateService.GetCompressionMultiplier("Medium Quality (Default)");
        return Math.Clamp(productionQuality - 6d * Math.Log2(profileScale / mediumScale), 0d, 51d);
    }

    private readonly record struct V2EstimateResultForTest(string Preset, double QualityValue, long EstimatedTotalBytes);

    private static V2EstimateResultForTest? CurrentV2Estimate(object meta)
    {
        object? estimate = meta.GetType().GetField("V2EstimateResult")!.GetValue(meta);
        if (estimate == null)
            return null;

        Type type = estimate.GetType();
        return new V2EstimateResultForTest(
            (string)type.GetProperty("Preset")!.GetValue(estimate)!,
            (double)type.GetProperty("QualityValue")!.GetValue(estimate)!,
            (long)type.GetProperty("EstimatedTotalBytes")!.GetValue(estimate)!);
    }

    private static int ControlEventHandlerCount(Control control, string eventName)
    {
        PropertyInfo eventsProperty = typeof(Control).GetProperty("Events", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(Control).FullName, "Events");
        var events = (System.ComponentModel.EventHandlerList?)eventsProperty.GetValue(control)
            ?? throw new InvalidOperationException("Control event list is unavailable.");
        for (Type? type = control.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo[] eventKeys = type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(field => field.FieldType == typeof(object) && field.Name.Contains(eventName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (FieldInfo key in eventKeys)
            {
                if (events[key.GetValue(null)!] is Delegate handlers)
                    return handlers.GetInvocationList().Length;
            }
        }
        throw new MissingFieldException(typeof(Control).FullName, $"{eventName} event key");
    }

    private static void PumpMainFormUntil(MainForm main, Func<bool> condition, string failureMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Application.DoEvents();
            Thread.Yield();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), failureMessage);
        }
        Application.DoEvents();
    }

    private static void WaitForEstimate(V2EstimateRefreshCoordinator coordinator)
    {
        Assert.True(SpinWait.SpinUntil(() => coordinator.PendingWorkCount == 0, TimeSpan.FromSeconds(10)),
            "V2 estimate did not complete within the bounded test window.");
    }

    private static IEnumerable<DataGridViewRow> EligibleRows(MainForm main) =>
        (IEnumerable<DataGridViewRow>)Invoke(main, "GetEligibleEncodeRowsInExecutionOrder")!;

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, args);

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetMetaField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)!.SetValue(target, value);

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

}
