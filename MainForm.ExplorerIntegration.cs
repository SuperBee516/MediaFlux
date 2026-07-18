using MediaFlux.Services;

namespace MediaFlux
{
    public partial class MainForm
    {
        private readonly Queue<ExplorerQueueRequest> _initialExplorerRequests = new();
        private readonly object _explorerRequestLock = new();
        private readonly SemaphoreSlim _explorerImportGate = new(1, 1);

        private void InitializeExplorerQueueIntegration()
        {
            Shown += MainForm_ShownProcessExplorerRequests;
        }

        private void RepairConfiguredExplorerIntegration()
        {
            bool filesEnabled = _config.ExplorerFileContextMenuEnabled || ExplorerContextMenuService.IsFileMenuInstalled;
            bool foldersEnabled = _config.ExplorerFolderContextMenuEnabled || ExplorerContextMenuService.HasAnyFolderMenuRegistration;
            if (!filesEnabled && !foldersEnabled)
                return;

            try
            {
                ExplorerContextMenuService.Apply(
                    filesEnabled,
                    foldersEnabled,
                    GetAllowedExts());

                if (_config.ExplorerFileContextMenuEnabled != filesEnabled ||
                    _config.ExplorerFolderContextMenuEnabled != foldersEnabled)
                {
                    _config.ExplorerFileContextMenuEnabled = filesEnabled;
                    _config.ExplorerFolderContextMenuEnabled = foldersEnabled;
                    _config.Save(_configPath);
                }
            }
            catch (Exception ex)
            {
                ErrorLogService.Append(
                    Application.StartupPath,
                    "Explorer context-menu registration could not be repaired at startup",
                    exception: ex);
            }
        }

        internal void QueueInitialExplorerRequest(ExplorerQueueRequest request)
        {
            lock (_explorerRequestLock)
                _initialExplorerRequests.Enqueue(request);
        }

        internal void ReceiveExplorerQueueRequest(ExplorerQueueRequest request)
        {
            if (IsDisposed)
                return;

            if (!IsHandleCreated)
            {
                lock (_explorerRequestLock)
                    _initialExplorerRequests.Enqueue(request);
                return;
            }

            try
            {
                BeginInvoke(new Action(() =>
                {
                    RestoreAndActivateFromExplorer();
                    _ = ProcessExplorerQueueRequestAsync(request);
                }));
            }
            catch (InvalidOperationException)
            {
                lock (_explorerRequestLock)
                    _initialExplorerRequests.Enqueue(request);
            }
        }

        private void MainForm_ShownProcessExplorerRequests(object? sender, EventArgs e)
        {
            while (true)
            {
                ExplorerQueueRequest? request;
                lock (_explorerRequestLock)
                    request = _initialExplorerRequests.Count > 0 ? _initialExplorerRequests.Dequeue() : null;
                if (request == null)
                    break;
                _ = ProcessExplorerQueueRequestAsync(request);
            }
        }

        private async Task ProcessExplorerQueueRequestAsync(ExplorerQueueRequest request)
        {
            await _explorerImportGate.WaitAsync();
            try
            {
                RestoreAndActivateFromExplorer();
                if (string.Equals(request.Kind, "activate", StringComparison.OrdinalIgnoreCase))
                    return;

                if (string.Equals(request.Kind, "folder", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string folder in request.Paths.Distinct(StringComparer.OrdinalIgnoreCase))
                        await ProcessExplorerFolderAsync(folder);
                    return;
                }

                if (string.Equals(request.Kind, "duplicate-folder", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string folder in request.Paths.Distinct(StringComparer.OrdinalIgnoreCase))
                        await ProcessExplorerDuplicateFolderAsync(folder);
                    return;
                }

                var files = request.Paths
                    .Where(File.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (files.Length == 0)
                    return;

                await ImportEncodePathsAsync(
                    files,
                    includeSubfolders: false,
                    applyCodecFilters: true,
                    replaceExisting: false);
            }
            catch (Exception ex)
            {
                ErrorLogService.Append(Application.StartupPath, "Explorer queue import failed", exception: ex);
                MessageBox.Show(this,
                    "The Explorer selection could not be added to the queue.\r\n\r\n" + ex.Message,
                    "Explorer Import",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                _explorerImportGate.Release();
            }
        }

        private async Task ProcessExplorerFolderAsync(string folder)
        {
            if (!Directory.Exists(folder))
                return;

            bool includeSubfolders = _config.ExplorerFolderIncludeSubfolders;
            bool replaceExisting = false;
            int currentQueueCount = dgvEncodeQueue.Rows
                .Cast<DataGridViewRow>()
                .Count(row => !row.IsNewRow);
            bool canOfferToClearQueue = _config.PromptToClearQueueOnExplorerFolderImport &&
                                        currentQueueCount > 0 &&
                                        !_encodingActive;
            if (_config.ConfirmExplorerFolderImports)
            {
                string filters = BuildExplorerCodecFilterSummary();
                using var confirmation = new FolderImportConfirmationForm(
                    folder,
                    GetAllowedExts(),
                    includeSubfolders,
                    currentQueueCount,
                    filters,
                    canOfferToClearQueue);
                if (confirmation.ShowDialog(this) != DialogResult.OK)
                    return;
                includeSubfolders = confirmation.IncludeSubfolders;
                replaceExisting = confirmation.ReplaceExistingQueue;
            }
            else if (canOfferToClearQueue)
            {
                var choice = PromptToClearExplorerQueue(
                    currentQueueCount,
                    "Add Folder to Encode Queue",
                    "adding this folder");
                if (choice == DialogResult.Cancel)
                    return;
                replaceExisting = choice == DialogResult.Yes;
            }

            await ImportEncodePathsAsync(
                new[] { folder },
                includeSubfolders,
                applyCodecFilters: true,
                replaceExisting: replaceExisting);
        }

        private async Task ProcessExplorerDuplicateFolderAsync(string folder)
        {
            if (!Directory.Exists(folder))
                return;

            bool replaceExisting = false;
            int currentQueueCount = dgvEncodeQueue.Rows
                .Cast<DataGridViewRow>()
                .Count(row => !row.IsNewRow);
            bool canOfferToClearQueue = _config.PromptToClearQueueOnExplorerFolderImport &&
                                        currentQueueCount > 0 &&
                                        !_encodingActive;
            if (canOfferToClearQueue)
            {
                var choice = PromptToClearExplorerQueue(
                    currentQueueCount,
                    "Check Folder for Duplicates",
                    "checking this folder for duplicates");
                if (choice == DialogResult.Cancel)
                    return;
                replaceExisting = choice == DialogResult.Yes;
            }

            await ImportEncodePathsAsync(
                new[] { folder },
                _config.ExplorerFolderIncludeSubfolders,
                applyCodecFilters: true,
                replaceExisting: replaceExisting,
                forceDuplicateScan: true);
        }

        private DialogResult PromptToClearExplorerQueue(
            int currentQueueCount,
            string title,
            string actionDescription)
        {
            return MessageBox.Show(
                this,
                $"The encode queue already contains {currentQueueCount:N0} file(s).\r\n\r\n" +
                $"Clear the existing queue before {actionDescription}?\r\n\r\n" +
                "Choose No to keep the current files and compare them together with the new folder.",
                title,
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);
        }

        private string BuildExplorerCodecFilterSummary()
        {
            var enabled = new List<string>();
            if (chkFilterX264.Checked) enabled.Add("x264 / H.264");
            if (chkFilterX265.Checked) enabled.Add("x265 / H.265");
            if (chkFilterAv1.Checked) enabled.Add("AV1");
            if (chkFilterOtherCodecs.Checked) enabled.Add("other codecs");
            return enabled.Count == 0 ? "none selected" : string.Join(", ", enabled);
        }

        private void RestoreAndActivateFromExplorer()
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Show();
            Activate();
            BringToFront();
        }
    }
}
