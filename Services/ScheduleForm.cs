using System;
using System.Windows.Forms;

namespace MediaFlux.Services
{
    public partial class ScheduleForm : Form
    {
        public DateTime ScheduledUtc { get; private set; }

        // Marked with null-forgiving; they are assigned in InitializeComponent()
        private DateTimePicker dtp = null!;
        private Label lbl = null!;
        private Button ok = null!;
        private Button cancel = null!;

        public ScheduleForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.dtp = new DateTimePicker();
            this.lbl = new Label();
            this.ok = new Button();
            this.cancel = new Button();
            this.SuspendLayout();

            this.Text = "Schedule Start";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false; this.MinimizeBox = false;
            this.ClientSize = new System.Drawing.Size(360, 120);

            lbl.Text = "Run at (local time):";
            lbl.AutoSize = true;
            lbl.Location = new System.Drawing.Point(12, 15);

            dtp.Format = DateTimePickerFormat.Custom;
            dtp.CustomFormat = "yyyy-MM-dd  HH:mm:ss";
            dtp.ShowUpDown = true;
            dtp.Width = 220;
            dtp.Location = new System.Drawing.Point(130, 10);
            dtp.Value = DateTime.Now.AddMinutes(5);

            ok.Text = "OK";
            ok.Location = new System.Drawing.Point(190, 70);
            ok.Click += (s, e) =>
            {
                ScheduledUtc = dtp.Value.ToUniversalTime();
                this.DialogResult = DialogResult.OK;
            };

            cancel.Text = "Cancel";
            cancel.Location = new System.Drawing.Point(270, 70);
            cancel.Click += (s, e) => this.DialogResult = DialogResult.Cancel;

            this.Controls.Add(lbl);
            this.Controls.Add(dtp);
            this.Controls.Add(ok);
            this.Controls.Add(cancel);
            this.ResumeLayout(false);
        }
    }
}
