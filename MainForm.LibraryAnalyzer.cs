using MediaFlux.Services.LibraryCatalog;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;

namespace MediaFlux
{
    public partial class MainForm
    {
        private LibraryAnalyzerRuntime? _libraryAnalyzerRuntime;
        private LibraryAnalyzerForm? _libraryAnalyzerForm;

        private void InitializeLibraryAnalyzerMenu()
        {
            var item = new ToolStripMenuItem("Library Analyzer");
            item.Click += ShowLibraryAnalyzer_Click;
            toolsToolStripMenuItem.DropDownItems.Insert(0, item);
            toolsToolStripMenuItem.DropDownItems.Insert(1, new ToolStripSeparator());
        }

        private void ShowLibraryAnalyzer_Click(object? sender, EventArgs e)
        {
            try
            {
                _libraryAnalyzerRuntime ??= new LibraryAnalyzerRuntime(
                    GetAllowedExts(),
                    Application.StartupPath,
                    _config.FfmpegPath,
                    _config.FfprobePath,
                    () => _encodingActive,
                    string.IsNullOrWhiteSpace(_config.DuplicateReferenceFolder)
                        ? Array.Empty<string>()
                        : new[] { _config.DuplicateReferenceFolder },
                    _config.DuplicateKeeperPreferences);
                if (_libraryAnalyzerForm == null || _libraryAnalyzerForm.IsDisposed)
                {
                    LibraryPolicyStore policyStore = new(AppPaths.LibraryPolicyFile);
                    string ffmpegPath = FfmpegToolResolver.Resolve(Application.StartupPath, _config.FfmpegPath).FfmpegPath;
                    LibraryPolicyCapabilitySnapshot policyCapabilities = LibraryPolicyCapabilityFactory.Create(ffmpegPath, _presetService.LoadAll());
                    _libraryAnalyzerForm = new LibraryAnalyzerForm(
                        _libraryAnalyzerRuntime,
                        new LibraryAnalyzerForm.LibraryAnalyzerCleanupOptions(
                            _config.AllowDuplicateRecycleBin,
                            _config.AllowDuplicateQuarantine,
                            _config.DuplicateQuarantineFolder,
                            _config.LibraryAnalyzerCleanupMode switch
                            {
                                "RecycleBin" => DuplicateCleanupAction.RecycleBin,
                                "Quarantine" => DuplicateCleanupAction.Quarantine,
                                _ => DuplicateCleanupAction.PermanentDelete
                            },
                            _config.AllowUnreviewedVisualBulkCleanup,
                            _config.VisualBulkCleanupMinimumConfidence),
                        new LibraryAnalyzerForm.LibraryAnalyzerReviewOptions(
                            _config.FfmpegPath,
                            _config.ExternalPlayerPath,
                            KeeperPreferences: _config.DuplicateKeeperPreferences,
                            KeeperPreferencesChanged: preferences =>
                            {
                                _config.DuplicateKeeperPreferences = preferences.Clone();
                                _config.Save(_configPath);
                                RescoreDuplicateKeeperRecommendations();
                            },
                            AutomationOptions: new LibraryVisualReviewAutomationOptions(
                                _config.SemiAutomaticVisualKeeperApproval,
                                _config.VisualMassReviewMaximumMatches,
                                _config.VisualMassReviewMinimumAutomationMargin,
                                _config.VisualMassReviewMinimumConfidence),
                            AutomationOptionsProvider: () => new LibraryVisualReviewAutomationOptions(
                                _config.SemiAutomaticVisualKeeperApproval,
                                _config.VisualMassReviewMaximumMatches,
                                _config.VisualMassReviewMinimumAutomationMargin,
                                _config.VisualMassReviewMinimumConfidence),
                            AddToEncodeQueue: paths => _ = ImportEncodePathsAsync(
                                paths, includeSubfolders: false, applyCodecFilters: true,
                                replaceExisting: false, rememberRoots: false),
                            PolicyStore: policyStore,
                            PolicyCapabilities: policyCapabilities,
                            AddPolicyCandidatesToEncodeQueue: AddLibraryPolicyCandidatesToQueueAsync,
                            RuntimeEstimator: new EncodingRuntimeEstimatorService(_encodingStatisticsService)));
                    _libraryAnalyzerForm.FormClosed += (_, _) => _libraryAnalyzerForm = null;
                    _libraryAnalyzerForm.Show(this);
                }
                else
                {
                    _libraryAnalyzerForm.WindowState = FormWindowState.Normal;
                    _libraryAnalyzerForm.Activate();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "MediaFlux could not open the Library Analyzer. No media files were changed.\r\n\r\n" + ex.Message,
                    "Library Analyzer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private async Task AddLibraryPolicyCandidatesToQueueAsync(IReadOnlyList<LibraryPolicyQueueItem> items)
        {
            LibraryPolicyQueueItem[] available = items
                .Where(item => !string.IsNullOrWhiteSpace(item.FullPath) && File.Exists(item.FullPath))
                .GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            if (available.Length == 0) return;

            await ImportEncodePathsAsync(available.Select(item => item.FullPath), includeSubfolders: false,
                applyCodecFilters: false, replaceExisting: false, rememberRoots: false);

            foreach (LibraryPolicyQueueItem item in available)
            {
                if (!_rowsByPath.TryGetValue(item.FullPath, out DataGridViewRow? row) || row.DataGridView != dgvEncodeQueue)
                    continue;
                RowMeta meta = EnsureRowMeta(row);
                meta.LibraryPolicyIntent = item;
                meta.CustomCompressionProfile = "Medium Quality (Default)";
                UpdateRowCustomFlag(row);
            }
            SafeRefreshEstimates();
        }

        private static EncodingService.ScaleMode PolicyScaleMode(LibraryPolicyQueueItem item)
        {
            if (item.PreserveSourceResolution || !item.MaximumOutputHeight.HasValue) return EncodingService.ScaleMode.None;
            return item.MaximumOutputHeight.Value switch
            {
                <= 720 => EncodingService.ScaleMode.To720p,
                <= 1080 => EncodingService.ScaleMode.To1080p,
                <= 1440 => EncodingService.ScaleMode.To1440p,
                _ => EncodingService.ScaleMode.To4K
            };
        }

        private OutputContainerSelection PolicyOutputContainer(LibraryPolicyQueueItem? item)
        {
            if (item == null) return _activeOutputContainer;
            if (!string.IsNullOrWhiteSpace(item.EncodingPresetName))
            {
                EncodingPreset? preset = _presetService.LoadAll().FirstOrDefault(value => value.Name.Equals(item.EncodingPresetName, StringComparison.OrdinalIgnoreCase));
                if (preset != null && Enum.TryParse(preset.OutputContainer, true, out OutputContainerSelection container)) return container;
            }
            return item.TargetContainer;
        }

        private bool EnsureRequestedVideoEncodersAvailable(IReadOnlyList<DataGridViewRow> rows)
        {
            if (rows.Any(row => row.Tag is not RowMeta { LibraryPolicyIntent: not null }) && !EnsureSelectedVideoEncoderAvailable())
                return false;
            FfmpegEncoderCapabilities capabilities = GetFfmpegEncoderCapabilities();
            if (!capabilities.InspectionSucceeded) return true;
            foreach (LibraryPolicyQueueItem intent in rows.Select(row => (row.Tag as RowMeta)?.LibraryPolicyIntent).Where(value => value != null).Cast<LibraryPolicyQueueItem>())
            {
                try
                {
                    EncodingPreset? preset = string.IsNullOrWhiteSpace(intent.EncodingPresetName)
                        ? null
                        : _presetService.LoadAll().FirstOrDefault(value => value.Name.Equals(intent.EncodingPresetName, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(intent.EncodingPresetName) && preset == null)
                        throw new InvalidOperationException($"Referenced encoding preset '{intent.EncodingPresetName}' is no longer available.");
                    VideoCodecFamily codec = preset == null ? intent.ProposedCodec : VideoEncoderCompatibility.ParseCodecFamily(string.IsNullOrWhiteSpace(preset.VideoCodec) ? preset.VideoFormat : preset.VideoCodec);
                    string encoderId = preset == null ? intent.EncoderId : VideoEncoderCompatibility.ResolveEncoderId(string.IsNullOrWhiteSpace(preset.EncoderId) ? preset.EncoderMode : preset.EncoderId, codec);
                    ResolvedVideoEncoder resolved = EncoderRegistry.Default.Resolve(encoderId, codec);
                    if (!capabilities.Contains(resolved.Selection.FfmpegCodec))
                        throw new InvalidOperationException($"The configured FFmpeg build does not provide '{resolved.Selection.FfmpegCodec}'.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Library policy '{intent.PolicyName}' requires attention before encoding.\r\n\r\n{ex.Message}",
                        "Policy encoder unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            return true;
        }

        private void DisposeLibraryAnalyzer()
        {
            _libraryAnalyzerForm?.Close();
            _libraryAnalyzerForm = null;
            _libraryAnalyzerRuntime?.Dispose();
            _libraryAnalyzerRuntime = null;
        }
    }
}
