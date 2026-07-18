namespace MediaFlux
{
    internal sealed class FolderImportConfirmationForm : Form
    {
        private readonly string _folder;
        private readonly HashSet<string> _allowedExtensions;
        private readonly Label _countLabel;
        private readonly Button _addButton;
        private readonly Button? _keepAndAddButton;
        private CancellationTokenSource? _scanCancellation;
        private const int CountLimit = 1000;

        public bool IncludeSubfolders => _includeSubfolders.Checked;
        public bool ReplaceExistingQueue { get; private set; }
        private readonly CheckBox _includeSubfolders;

        public FolderImportConfirmationForm(
            string folder,
            HashSet<string> allowedExtensions,
            bool includeSubfolders,
            int currentQueueCount,
            string codecFilterSummary,
            bool offerToClearQueue)
        {
            _folder = folder;
            _allowedExtensions = allowedExtensions;

            Text = "Add Folder to Encode Queue";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(620, 275);
            Font = SystemFonts.MessageBoxFont;

            var title = new Label
            {
                Text = "Review folder import",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(18, 16)
            };
            var folderLabel = new Label
            {
                Text = folder,
                AutoEllipsis = true,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(18, 45),
                Size = new Size(584, 40),
                Padding = new Padding(6),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _includeSubfolders = new CheckBox
            {
                Text = "Include files in subfolders",
                Checked = includeSubfolders,
                AutoSize = true,
                Location = new Point(20, 101)
            };
            _includeSubfolders.CheckedChanged += async (_, __) => await RefreshCountAsync();

            _countLabel = new Label
            {
                Text = "Counting supported video files...",
                AutoSize = true,
                Location = new Point(20, 133)
            };
            var details = new Label
            {
                Text = $"Current queue: {currentQueueCount:N0} files\r\nCodec filters: {codecFilterSummary}",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Location = new Point(20, 160)
            };
            var warning = new Label
            {
                Text = "Only supported video files matching the current codec filters will be added.",
                AutoSize = true,
                Location = new Point(20, 210)
            };
            bool showQueueChoice = offerToClearQueue && currentQueueCount > 0;
            _addButton = new Button
            {
                Text = showQueueChoice ? "Clear Queue && Add" : "Add to Queue",
                DialogResult = DialogResult.OK,
                Enabled = false,
                Size = new Size(showQueueChoice ? 145 : 108, 28),
                Location = new Point(showQueueChoice ? 205 : 378, 237)
            };
            _addButton.Click += (_, __) => ReplaceExistingQueue = showQueueChoice;
            if (showQueueChoice)
            {
                _keepAndAddButton = new Button
                {
                    Text = "Keep Queue && Add",
                    DialogResult = DialogResult.OK,
                    Enabled = false,
                    Size = new Size(145, 28),
                    Location = new Point(358, 237)
                };
                _keepAndAddButton.Click += (_, __) => ReplaceExistingQueue = false;
            }
            var cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Size = new Size(108, 28),
                Location = new Point(510, 237)
            };

            Controls.AddRange(new Control[] { title, folderLabel, _includeSubfolders, _countLabel, details, warning, _addButton, cancelButton });
            if (_keepAndAddButton != null)
                Controls.Add(_keepAndAddButton);
            AcceptButton = _addButton;
            CancelButton = cancelButton;
            Shown += async (_, __) => await RefreshCountAsync();
            FormClosed += (_, __) => _scanCancellation?.Cancel();
        }

        private async Task RefreshCountAsync()
        {
            _scanCancellation?.Cancel();
            _scanCancellation?.Dispose();
            _scanCancellation = new CancellationTokenSource();
            var token = _scanCancellation.Token;
            _addButton.Enabled = false;
            if (_keepAndAddButton != null)
                _keepAndAddButton.Enabled = false;
            _countLabel.Text = "Counting supported video files...";

            try
            {
                bool recursive = IncludeSubfolders;
                int count = await Task.Run(() => CountFiles(recursive, token), token);
                if (token.IsCancellationRequested)
                    return;

                _countLabel.Text = count > CountLimit
                    ? $"Supported video files found: {CountLimit:N0}+"
                    : $"Supported video files found: {count:N0}";
                _addButton.Enabled = count > 0;
                if (_keepAndAddButton != null)
                    _keepAndAddButton.Enabled = count > 0;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _countLabel.Text = "The folder could not be fully scanned: " + ex.Message;
                _addButton.Enabled = true;
                if (_keepAndAddButton != null)
                    _keepAndAddButton.Enabled = true;
            }
        }

        private int CountFiles(bool recursive, CancellationToken token)
        {
            int count = 0;
            var pending = new Stack<string>();
            pending.Push(_folder);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string current = pending.Pop();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(current))
                    {
                        token.ThrowIfCancellationRequested();
                        if (_allowedExtensions.Contains(Path.GetExtension(file)) && ++count > CountLimit)
                            return count;
                    }
                    if (recursive)
                    {
                        foreach (string subfolder in Directory.EnumerateDirectories(current))
                            pending.Push(subfolder);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Continue through accessible folders; the importer logs inaccessible paths.
                }
            }
            return count;
        }
    }
}
