using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

// Alias TagLib.File so we don't clash with System.IO.File
using TagFile = TagLib.File;

namespace MediaFlux
{
    public sealed class AudioMetadataForm : MediaFluxForm
    {
        private readonly string _path;

        private TextBox txtTitle = null!;
        private TextBox txtArtist = null!;
        private TextBox txtAlbum = null!;
        private TextBox txtYear = null!;
        private TextBox txtTrack = null!;
        private TextBox txtGenre = null!;
        private Button btnOk = null!;
        private Button btnCancel = null!;

        public AudioMetadataForm(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path must not be null or empty.", nameof(path));

            if (!File.Exists(path))
                throw new FileNotFoundException("Audio file not found.", path);

            _path = path;

            InitializeComponents();
            LoadTags();
        }

        private void InitializeComponents()
        {
            Text = "Edit Audio Metadata";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(440, 230);

            // ── Title ───────────────────────────────────────────────
            var lblTitle = new Label
            {
                Text = "Title:",
                AutoSize = true,
                Location = new Point(12, 15)
            };
            txtTitle = new TextBox
            {
                Location = new Point(80, 12),
                Width = 340,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            // ── Artist ──────────────────────────────────────────────
            var lblArtist = new Label
            {
                Text = "Artist:",
                AutoSize = true,
                Location = new Point(12, 45)
            };
            txtArtist = new TextBox
            {
                Location = new Point(80, 42),
                Width = 340,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            // ── Album ───────────────────────────────────────────────
            var lblAlbum = new Label
            {
                Text = "Album:",
                AutoSize = true,
                Location = new Point(12, 75)
            };
            txtAlbum = new TextBox
            {
                Location = new Point(80, 72),
                Width = 340,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            // ── Year / Track ────────────────────────────────────────
            var lblYear = new Label
            {
                Text = "Year:",
                AutoSize = true,
                Location = new Point(12, 105)
            };
            txtYear = new TextBox
            {
                Location = new Point(80, 102),
                Width = 80
            };

            var lblTrack = new Label
            {
                Text = "Track:",
                AutoSize = true,
                Location = new Point(190, 105)
            };
            txtTrack = new TextBox
            {
                Location = new Point(240, 102),
                Width = 60
            };

            // ── Genre ───────────────────────────────────────────────
            var lblGenre = new Label
            {
                Text = "Genre:",
                AutoSize = true,
                Location = new Point(12, 135)
            };
            txtGenre = new TextBox
            {
                Location = new Point(80, 132),
                Width = 340,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            // ── Buttons ─────────────────────────────────────────────
            btnOk = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.None,
                Size = new Size(80, 27),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Size = new Size(80, 27),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };

            btnOk.Location = new Point(ClientSize.Width - 180, ClientSize.Height - 40);
            btnCancel.Location = new Point(ClientSize.Width - 90, ClientSize.Height - 40);

            btnOk.Click += BtnOk_Click;
            btnCancel.Click += (_, _) => Close();

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.AddRange(new Control[]
            {
                lblTitle, txtTitle,
                lblArtist, txtArtist,
                lblAlbum, txtAlbum,
                lblYear, txtYear,
                lblTrack, txtTrack,
                lblGenre, txtGenre,
                btnOk, btnCancel
            });
        }

        private void LoadTags()
        {
            var file = TagFile.Create(_path);

            txtTitle.Text = file.Tag.Title ?? string.Empty;
            txtArtist.Text = file.Tag.JoinedPerformers ?? string.Empty;
            txtAlbum.Text = file.Tag.Album ?? string.Empty;
            txtYear.Text = file.Tag.Year > 0 ? file.Tag.Year.ToString() : string.Empty;
            txtTrack.Text = file.Tag.Track > 0 ? file.Tag.Track.ToString() : string.Empty;
            txtGenre.Text = file.Tag.JoinedGenres ?? string.Empty;
        }

        private void BtnOk_Click(object? sender, EventArgs e)
        {
            var file = TagFile.Create(_path);

            file.Tag.Title = txtTitle.Text;

            file.Tag.Performers = string.IsNullOrWhiteSpace(txtArtist.Text)
                ? Array.Empty<string>()
                : new[] { txtArtist.Text };

            file.Tag.Album = txtAlbum.Text;

            if (uint.TryParse(txtYear.Text, out var year))
                file.Tag.Year = year;
            else
                file.Tag.Year = 0;

            if (uint.TryParse(txtTrack.Text, out var track))
                file.Tag.Track = track;
            else
                file.Tag.Track = 0;

            file.Tag.Genres = string.IsNullOrWhiteSpace(txtGenre.Text)
                ? Array.Empty<string>()
                : new[] { txtGenre.Text };

            file.Save();

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
