using MediaFlux.Models;

namespace MediaFlux
{
    public sealed class DvdConversionProgressForm : MediaFluxForm
    {
        private readonly Func<IProgress<DvdOperationProgress>, CancellationToken, Task<object?>> _operation;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Label _statusLabel;
        private readonly Label _timeLabel;
        private readonly ProgressBar _progressBar;
        private readonly Button _cancelButton;
        private bool _operationFinished;

        public DvdConversionProgressForm(
            string title,
            Func<IProgress<DvdOperationProgress>, CancellationToken, Task<object?>> operation)
        {
            _operation = operation ?? throw new ArgumentNullException(nameof(operation));
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            Width = 560;
            Height = 220;
            MinimizeBox = false;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                ColumnCount = 1,
                RowCount = 4
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _statusLabel = new Label
            {
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Text = "Preparing DVD operation…",
                Margin = new Padding(0, 0, 0, 10)
            };
            _progressBar = new ProgressBar
            {
                Dock = DockStyle.Top,
                Height = 24,
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 25,
                Margin = new Padding(0, 0, 0, 8)
            };
            _timeLabel = new Label
            {
                AutoSize = true,
                Text = "",
                ForeColor = SystemColors.GrayText
            };
            _cancelButton = new Button
            {
                Text = "Cancel",
                Width = 100,
                Height = 32,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            _cancelButton.Click += (_, _) => RequestCancellation();

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            buttonPanel.Controls.Add(_cancelButton);

            layout.Controls.Add(_statusLabel, 0, 0);
            layout.Controls.Add(_progressBar, 0, 1);
            layout.Controls.Add(_timeLabel, 0, 2);
            layout.Controls.Add(buttonPanel, 0, 3);
            Controls.Add(layout);

            Shown += RunOperationAsync;
            FormClosing += OnProgressFormClosing;
        }

        public object? OperationResult { get; private set; }
        public Exception? OperationException { get; private set; }
        public bool WasCanceled { get; private set; }

        private async void RunOperationAsync(object? sender, EventArgs e)
        {
            var progress = new Progress<DvdOperationProgress>(UpdateProgress);
            try
            {
                OperationResult = await _operation(progress, _cancellation.Token);
                DialogResult = DialogResult.OK;
            }
            catch (OperationCanceledException)
            {
                WasCanceled = true;
                DialogResult = DialogResult.Cancel;
            }
            catch (Exception ex)
            {
                OperationException = ex;
                DialogResult = DialogResult.Abort;
            }
            finally
            {
                _operationFinished = true;
                Close();
            }
        }

        private void UpdateProgress(DvdOperationProgress progress)
        {
            if (IsDisposed)
                return;

            _statusLabel.Text = string.IsNullOrWhiteSpace(progress.Status)
                ? "Working…"
                : progress.Status;
            if (progress.Percent.HasValue)
            {
                _progressBar.Style = ProgressBarStyle.Continuous;
                _progressBar.MarqueeAnimationSpeed = 0;
                _progressBar.Value = Math.Clamp((int)Math.Round(progress.Percent.Value), 0, 100);
            }
            else
            {
                _progressBar.Style = ProgressBarStyle.Marquee;
                _progressBar.MarqueeAnimationSpeed = 25;
            }

            _timeLabel.Text = progress.CurrentTime.HasValue && progress.TotalDuration.HasValue
                ? $"{progress.CurrentTime.Value:hh\\:mm\\:ss} / {progress.TotalDuration.Value:hh\\:mm\\:ss}"
                : "";
        }

        private void RequestCancellation()
        {
            if (_operationFinished || _cancellation.IsCancellationRequested)
                return;

            _cancellation.Cancel();
            _cancelButton.Enabled = false;
            _statusLabel.Text = "Canceling and cleaning temporary files…";
            _progressBar.Style = ProgressBarStyle.Marquee;
        }

        private void OnProgressFormClosing(object? sender, FormClosingEventArgs e)
        {
            if (_operationFinished)
                return;

            RequestCancellation();
            e.Cancel = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _cancellation.Dispose();
            base.Dispose(disposing);
        }
    }
}
