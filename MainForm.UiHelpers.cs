using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Encode
{
    public partial class MainForm : Form
    {
        // ───────── UI thread helpers ─────────
        private void Ui(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { try { BeginInvoke(a); } catch { } }
            else a();
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
