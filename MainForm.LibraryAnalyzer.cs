using MediaFlux.Services.LibraryCatalog;

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
                            AddToEncodeQueue: paths => _ = ImportEncodePathsAsync(
                                paths, includeSubfolders: false, applyCodecFilters: true,
                                replaceExisting: false, rememberRoots: false)));
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

        private void DisposeLibraryAnalyzer()
        {
            _libraryAnalyzerForm?.Close();
            _libraryAnalyzerForm = null;
            _libraryAnalyzerRuntime?.Dispose();
            _libraryAnalyzerRuntime = null;
        }
    }
}
