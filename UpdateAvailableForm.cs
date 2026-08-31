using System.Drawing;
using System.Windows.Forms;
using MediaFlux.Services;

namespace MediaFlux
{
    internal sealed class UpdateAvailableForm : MediaFluxForm
    {
        public UpdateAvailableForm(string currentVersion, string targetVersion, string? releaseNotes)
        {
            Text = "MediaFlux Update Available";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            MinimumSize = new Size(620, 440);
            ClientSize = new Size(700, 520);

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
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Size = new Size(664, 350),
                ReadOnly = true,
                BackColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                DetectUrls = true,
                Text = ReleaseNotesFormatter.Format(releaseNotes)
            };

            var install = new Button
            {
                Text = "Download and Install",
                DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Location = new Point(502, 460),
                Size = new Size(180, 30)
            };

            var later = new Button
            {
                Text = "Later",
                DialogResult = DialogResult.Cancel,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Location = new Point(412, 460),
                Size = new Size(80, 30)
            };

            AcceptButton = install;
            CancelButton = later;
            Controls.AddRange(new Control[] { heading, versions, notesLabel, notes, later, install });
        }
    }
}
