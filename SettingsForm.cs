using Encode.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Encode
{
    public partial class SettingsForm : Form
    {
        public Config Config { get; }

        private readonly string _supportedVideoExtsPath;
        private readonly IReadOnlyList<string> _defaultVideoExts;

        public SettingsForm(Config cfg, string supportedVideoExtsPath, IEnumerable<string> defaultVideoExts)
        {
            InitializeComponent();
            Config = cfg;

            _supportedVideoExtsPath = supportedVideoExtsPath;
            _defaultVideoExts = (defaultVideoExts ?? Array.Empty<string>()).ToList();

            txtUpdateFolder.Text = cfg.UpdateFolderPath;
            txtPattern.Text = cfg.AutoNamingPattern;
            txtSuffix.Text = cfg.OutputSuffix;
            chkEnableSuffix.Checked = cfg.EnableOutputSuffix;
            chkRememberCheckboxes.Checked = cfg.RememberCheckboxStates;
            ToggleSuffixInputs();

            LoadSupportedExtensionsIntoUi();
        }


        private void btnBrowseUpdate_Click(object sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = Config.UpdateFolderPath };
            if (dlg.ShowDialog() == DialogResult.OK)
                txtUpdateFolder.Text = dlg.SelectedPath;
        }

        private void btnOK_Click(object sender, EventArgs e)
        {
            Config.UpdateFolderPath = txtUpdateFolder.Text.Trim();
            Config.AutoNamingPattern = txtPattern.Text.Trim();
            Config.OutputSuffix = txtSuffix.Text.Trim();  // <-- NEW
            Config.EnableOutputSuffix = chkEnableSuffix.Checked;
            Config.RememberCheckboxStates = chkRememberCheckboxes.Checked;

            // Persist supported video extensions list
            var exts = ReadSupportedExtensionsFromUi();
            if (exts.Count == 0)
            {
                MessageBox.Show(this,
                    "You must keep at least one supported video file extension.",
                    "Invalid extensions list",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            SupportedExtensionsStore.Save(_supportedVideoExtsPath, exts);

            DialogResult = DialogResult.OK;
            Close();
        }

        private void SettingsForm_Load(object sender, EventArgs e)
        {

        }

        private void chkEnableSuffix_CheckedChanged(object sender, EventArgs e)
        {
            ToggleSuffixInputs();
        }

        private void ToggleSuffixInputs()
        {
            bool enabled = chkEnableSuffix.Checked;
            txtSuffix.Enabled = enabled;
            lblSuffix.Enabled = enabled;
        }

        private void LoadSupportedExtensionsIntoUi()
        {
            var list = SupportedExtensionsStore.Load(_supportedVideoExtsPath, _defaultVideoExts);
            lstSupportedExts.BeginUpdate();
            try
            {
                lstSupportedExts.Items.Clear();
                foreach (var ext in list)
                    lstSupportedExts.Items.Add(ext);
            }
            finally
            {
                lstSupportedExts.EndUpdate();
            }
        }

        private List<string> ReadSupportedExtensionsFromUi()
        {
            var exts = new List<string>();
            foreach (var item in lstSupportedExts.Items)
            {
                var s = item?.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    exts.Add(s);
            }
            return SupportedExtensionsStore.Normalize(exts).ToList();
        }

        private void btnAddExt_Click(object sender, EventArgs e)
        {
            var raw = txtNewExt.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return;

            var normalized = SupportedExtensionsStore.Normalize(new[] { raw }).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                MessageBox.Show(this,
                    "Please enter a valid extension like .mp4 or mkv (letters/numbers only).",
                    "Invalid extension",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var exists = lstSupportedExts.Items.Cast<object>()
                .Select(o => o?.ToString() ?? string.Empty)
                .Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));

            if (!exists)
                lstSupportedExts.Items.Add(normalized);

            txtNewExt.Clear();
            txtNewExt.Focus();
        }

        private void btnRemoveExt_Click(object sender, EventArgs e)
        {
            if (lstSupportedExts.SelectedItems.Count == 0) return;

            // Copy because we'll mutate the collection
            var toRemove = lstSupportedExts.SelectedItems.Cast<object>().ToList();
            foreach (var item in toRemove)
                lstSupportedExts.Items.Remove(item);
        }

        private void btnResetExts_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(this,
                "Reset supported video extensions back to the built-in defaults?",
                "Reset extensions",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            lstSupportedExts.BeginUpdate();
            try
            {
                lstSupportedExts.Items.Clear();
                foreach (var ext in SupportedExtensionsStore.Normalize(_defaultVideoExts))
                    lstSupportedExts.Items.Add(ext);
            }
            finally
            {
                lstSupportedExts.EndUpdate();
            }
        }

        private void txtNewExt_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                btnAddExt_Click(sender, EventArgs.Empty);
            }
        }
    }
}
