using Encode.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Encode
{
    public partial class MainForm : Form
    {
        private void ScanAndPopulateEncodeGrid(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;

            // Use the central helper that normalizes extensions and falls back to defaults
            var extSet = GetAllowedExts();

            int added = 0;

            _activityIndicator?.StartActivity(UiActivity.FolderScan);
            try
            {
                using (UiBusy("Scanning folder…"))
                {
                    dgvEncodeQueue.Rows.Clear();

                    var searchOpt = chkIncludeSubfolders.Checked
                        ? SearchOption.AllDirectories
                        : SearchOption.TopDirectoryOnly;

                    foreach (var f in Directory.GetFiles(folder, "*.*", searchOpt))
                    {
                        var ext = Path.GetExtension(f);
                        if (string.IsNullOrEmpty(ext) || !extSet.Contains(ext))
                            continue;

                        // Respect codec filters
                        var codec = GetVideoCodec(f);
                        if (!PassesCodecFilter(codec))
                            continue;

                        if (AddEncodeItemIfNotPresent(f))
                            added++;
                    }
                }
            }
            finally
            {
                _activityIndicator?.StopActivity(UiActivity.FolderScan);

                // Useful feedback for sanity-checking
                toolStripStatusLabel1.Text = added > 0
                    ? $"Scanned \"{folder}\" — {added} file(s) added."
                    : $"Scanned \"{folder}\" — no files matched current filters.";
            }
        }

        private void RescanInputFolderAndMerge()
        {
            var folder = cmbInputFolder.Text;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

            _activityIndicator?.StartActivity(UiActivity.FolderScan);
            try
            {
                var allowedExts = GetAllowedExtensionsFromUi();
                var searchOpt = chkIncludeSubfolders.Checked
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly;

                var fs = new HashSet<string>(Directory
                    .EnumerateFiles(folder, "*.*", searchOpt)
                    .Where(f =>
                    {
                        var ext = Path.GetExtension(f);
                        if (string.IsNullOrEmpty(ext) || !allowedExts.Contains(ext)) return false;

                        // Respect codec filters
                        var codec = GetVideoCodec(f);
                        if (!PassesCodecFilter(codec))
                            return false;
                        return true;
                    }),
                    StringComparer.OrdinalIgnoreCase);

                _suppressRowEvents = true;
                try
                {
                    // Add new files
                    foreach (var p in fs)
                        AddEncodeItemIfNotPresent(p);

                    // Remove missing/unmatched rows
                    foreach (DataGridViewRow row in dgvEncodeQueue.Rows.Cast<DataGridViewRow>().ToList())
                    {
                        var p = (row.Tag as RowMeta)?.Path ?? row.Tag as string;
                        if (string.IsNullOrWhiteSpace(p) || !fs.Contains(p))
                            dgvEncodeQueue.Rows.Remove(row);   // no estimate call here due to _suppressRowEvents
                    }
                }
                finally
                {
                    _suppressRowEvents = false;
                }
                RunEstimatePass();
            }
            finally
            {
                _activityIndicator?.StopActivity(UiActivity.FolderScan);
            }            
        }
    }
}
