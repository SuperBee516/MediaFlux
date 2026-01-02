namespace Encode
{
    partial class SettingsForm
    {
        private System.ComponentModel.IContainer components = null;
        private Label lblUpdateFolder;
        private TextBox txtUpdateFolder;
        private Button btnBrowseUpdate;
        private Label lblPattern;
        private TextBox txtPattern;
        private Label lblSuffix;
        private TextBox txtSuffix;
        private Button btnOK;
        private Button btnCancel;
        private CheckBox chkRememberCheckboxes;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.lblUpdateFolder = new System.Windows.Forms.Label();
            this.txtUpdateFolder = new System.Windows.Forms.TextBox();
            this.btnBrowseUpdate = new System.Windows.Forms.Button();
            this.lblPattern = new System.Windows.Forms.Label();
            this.txtPattern = new System.Windows.Forms.TextBox();
            this.lblSuffix = new System.Windows.Forms.Label();
            this.txtSuffix = new System.Windows.Forms.TextBox();
            this.btnOK = new System.Windows.Forms.Button();
            this.btnCancel = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // lblUpdateFolder
            // 
            this.lblUpdateFolder.AutoSize = true;
            this.lblUpdateFolder.Location = new System.Drawing.Point(12, 15);
            this.lblUpdateFolder.Name = "lblUpdateFolder";
            this.lblUpdateFolder.Size = new System.Drawing.Size(116, 15);
            this.lblUpdateFolder.TabIndex = 0;
            this.lblUpdateFolder.Text = "Update Folder Path:";
            // 
            // chkRememberCheckboxes
            // 
            this.chkRememberCheckboxes = new System.Windows.Forms.CheckBox();
            this.chkRememberCheckboxes.AutoSize = true;
            this.chkRememberCheckboxes.Location = new System.Drawing.Point(15, 180);
            this.chkRememberCheckboxes.Name = "chkRememberCheckboxes";
            this.chkRememberCheckboxes.Size = new System.Drawing.Size(288, 19);
            this.chkRememberCheckboxes.TabIndex = 7;
            this.chkRememberCheckboxes.Text = "Remember last-used settings for checkboxes";
            this.chkRememberCheckboxes.UseVisualStyleBackColor = true;
            this.Controls.Add(this.chkRememberCheckboxes);

            // Bump buttons down a bit if needed:
            this.btnOK.Location = new System.Drawing.Point(15, 210);
            this.btnCancel.Location = new System.Drawing.Point(100, 210);
            this.ClientSize = new System.Drawing.Size(420, 255);
            // 
            // txtUpdateFolder
            // 
            this.txtUpdateFolder.Location = new System.Drawing.Point(15, 35);
            this.txtUpdateFolder.Name = "txtUpdateFolder";
            this.txtUpdateFolder.Size = new System.Drawing.Size(300, 23);
            this.txtUpdateFolder.TabIndex = 1;
            // 
            // btnBrowseUpdate
            // 
            this.btnBrowseUpdate.Location = new System.Drawing.Point(320, 34);
            this.btnBrowseUpdate.Name = "btnBrowseUpdate";
            this.btnBrowseUpdate.Size = new System.Drawing.Size(75, 23);
            this.btnBrowseUpdate.TabIndex = 2;
            this.btnBrowseUpdate.Text = "Browse…";
            this.btnBrowseUpdate.UseVisualStyleBackColor = true;
            this.btnBrowseUpdate.Click += new System.EventHandler(this.btnBrowseUpdate_Click);
            // 
            // lblPattern
            // 
            this.lblPattern.AutoSize = true;
            this.lblPattern.Location = new System.Drawing.Point(12, 70);
            this.lblPattern.Name = "lblPattern";
            this.lblPattern.Size = new System.Drawing.Size(127, 15);
            this.lblPattern.TabIndex = 3;
            this.lblPattern.Text = "Auto-Naming Pattern:";
            // 
            // txtPattern
            // 
            this.txtPattern.Location = new System.Drawing.Point(15, 90);
            this.txtPattern.Name = "txtPattern";
            this.txtPattern.Size = new System.Drawing.Size(300, 23);
            this.txtPattern.TabIndex = 4;
            // 
            // lblSuffix
            // 
            this.lblSuffix.AutoSize = true;
            this.lblSuffix.Location = new System.Drawing.Point(12, 130);
            this.lblSuffix.Name = "lblSuffix";
            this.lblSuffix.Size = new System.Drawing.Size(137, 15);
            this.lblSuffix.TabIndex = 5;
            this.lblSuffix.Text = "Output Filename Suffix:";
            // 
            // txtSuffix
            // 
            this.txtSuffix.Location = new System.Drawing.Point(15, 150);
            this.txtSuffix.Name = "txtSuffix";
            this.txtSuffix.Size = new System.Drawing.Size(100, 23);
            this.txtSuffix.TabIndex = 6;
            // 
            // btnOK
            // 
            this.btnOK.Location = new System.Drawing.Point(15, 200);
            this.btnOK.Name = "btnOK";
            this.btnOK.Size = new System.Drawing.Size(75, 25);
            this.btnOK.TabIndex = 7;
            this.btnOK.Text = "OK";
            this.btnOK.UseVisualStyleBackColor = true;
            this.btnOK.Click += new System.EventHandler(this.btnOK_Click);
            // 
            // btnCancel
            // 
            this.btnCancel.Location = new System.Drawing.Point(100, 200);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(75, 25);
            this.btnCancel.TabIndex = 8;
            this.btnCancel.Text = "Cancel";
            this.btnCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.btnCancel.UseVisualStyleBackColor = true;
            // 
            // SettingsForm
            // 
            this.AcceptButton = this.btnOK;
            this.CancelButton = this.btnCancel;
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(420, 250);
            this.Controls.Add(this.lblUpdateFolder);
            this.Controls.Add(this.txtUpdateFolder);
            this.Controls.Add(this.btnBrowseUpdate);
            this.Controls.Add(this.lblPattern);
            this.Controls.Add(this.txtPattern);
            this.Controls.Add(this.lblSuffix);
            this.Controls.Add(this.txtSuffix);
            this.Controls.Add(this.btnOK);
            this.Controls.Add(this.btnCancel);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.Name = "SettingsForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Settings";
            this.Load += new System.EventHandler(this.SettingsForm_Load);
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
