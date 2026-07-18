using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MediaFlux.Services;

namespace MediaFlux
{
    public partial class MainForm : Form
    {
        // ───────── UI thread helpers ─────────
        private void Ui(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => RunUiActionSafely(a))); }
                catch { }
            }
            else
            {
                RunUiActionSafely(a);
            }
        }

        // Use for state transitions that must finish before the calling worker can
        // report completion. Exceptions are allowed to reach the caller's handler.
        private void UiInvoke(Action action)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            if (InvokeRequired)
                Invoke(action);
            else
                action();
        }

        private void RunUiActionSafely(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                LogUiException("UI callback failed", ex);
            }
        }

        private void LogUiException(string title, Exception ex)
        {
            try
            {
                var logPath = ErrorLogService.Append(Application.StartupPath, title, exception: ex);
                if (!IsDisposed && IsHandleCreated && statusStrip1 != null)
                    toolStripStatusLabel1.Text = $"UI error logged: {logPath}";
            }
            catch
            {
                // Last-chance logging must never trigger another UI exception.
            }
        }

        // Optional: return a value (use sparingly on UI thread only)
        private T UiGet<T>(Func<T> f, T fallback = default!)
        {
            if (IsDisposed || !IsHandleCreated) return fallback;
            if (InvokeRequired)
            {
                try
                {
                    return (T)Invoke(f);
                }
                catch
                {
                    return fallback;
                }
            }

            return f();
        }
        // Simple scope-based busy indicator for status + cursor
        private sealed class ActionOnDispose : IDisposable
        {
            private readonly Action _onDispose;
            public ActionOnDispose(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose();
        }

        private int _busyDepth = 0;
        private System.Windows.Forms.Timer? _statusResetTimer;

        private void ShowStatusInfo(string message, int resetAfterMs = 6000)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            Ui(() =>
            {
                toolStripStatusLabel1.Text = message;

                _statusResetTimer?.Stop();
                _statusResetTimer?.Dispose();
                _statusResetTimer = new System.Windows.Forms.Timer { Interval = resetAfterMs };
                _statusResetTimer.Tick += (_, __) =>
                {
                    _statusResetTimer?.Stop();
                    _statusResetTimer?.Dispose();
                    _statusResetTimer = null;
                    if (!_encodingActive && _busyDepth == 0)
                        toolStripStatusLabel1.Text = "Ready";
                };
                _statusResetTimer.Start();
            });
        }

        private ActionOnDispose UiBusy(string statusText)
        {
            _busyDepth++;
            try
            {
                toolStripStatusLabel1.Text = statusText;  // show in StatusStrip
                this.Cursor = Cursors.WaitCursor;
            }
            catch { /* ignore */ }

            return new ActionOnDispose(() =>
            {
                _busyDepth = Math.Max(0, _busyDepth - 1);
                if (_busyDepth == 0)
                {
                    try
                    {
                        this.Cursor = Cursors.Default;
                        toolStripStatusLabel1.Text = "Ready";  // reset to Ready
                    }
                    catch { /* ignore */ }
                }
            });
        }
    }
}
