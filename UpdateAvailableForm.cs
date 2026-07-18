using System.Drawing;
using System.Windows.Forms;

namespace MediaFlux
{
    internal sealed class UpdateAvailableForm : Form
    {
        public UpdateAvailableForm(string currentVersion, string targetVersion, string? releaseNotes)
        {
            Text = "MediaFlux Update Available";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(620, 440);

            var heading = new Label
            {
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Location = new Point(18, 18),
                Text = $"MediaFlux {targetVersion} is available"
            };

            var versions = new Label
            {
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Location = new Point(18, 46),
                Text = $"Installed version: {currentVersion}"
            };

            var notesLabel = new Label
            {
                AutoSize = true,
                Location = new Point(18, 78),
                Text = "Release notes"
            };

            var notes = new RichTextBox
            {
                Location = new Point(18, 100),
                Size = new Size(584, 275),
                ReadOnly = true,
                BackColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                DetectUrls = true,
                Text = string.IsNullOrWhiteSpace(releaseNotes)
                    ? "No release notes were provided for this version."
                    : releaseNotes.Trim()
            };

            var install = new Button
            {
                Text = "Download and Install",
                DialogResult = DialogResult.OK,
                Location = new Point(422, 395),
                Size = new Size(180, 30)
            };

            var later = new Button
            {
                Text = "Later",
                DialogResult = DialogResult.Cancel,
                Location = new Point(332, 395),
                Size = new Size(80, 30)
            };

            AcceptButton = install;
            CancelButton = later;
            Controls.AddRange(new Control[] { heading, versions, notesLabel, notes, later, install });
        }
    }
}
