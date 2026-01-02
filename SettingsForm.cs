using Encode.Models;
using System;
using System.IO;
using System.Windows.Forms;

namespace Encode
{
    public partial class SettingsForm : Form
    {
        public Config Config { get; }

        public SettingsForm(Config cfg)
        {
            InitializeComponent();
            Config = cfg;

            txtUpdateFolder.Text = cfg.UpdateFolderPath;
            txtPattern.Text = cfg.AutoNamingPattern;
            txtSuffix.Text = cfg.OutputSuffix;
            chkRememberCheckboxes.Checked = cfg.RememberCheckboxStates;
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
            Config.RememberCheckboxStates = chkRememberCheckboxes.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void SettingsForm_Load(object sender, EventArgs e)
        {

        }
    }
}
