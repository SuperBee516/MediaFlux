namespace Encode
{
    partial class SettingsForm
    {
        private System.ComponentModel.IContainer components = null;

        private System.Windows.Forms.Label lblUpdateFolder;
        private System.Windows.Forms.TextBox txtUpdateFolder;
        private System.Windows.Forms.Button btnBrowseUpdate;

        private System.Windows.Forms.Label lblPattern;
        private System.Windows.Forms.TextBox txtPattern;

        private System.Windows.Forms.Label lblSuffix;
        private System.Windows.Forms.TextBox txtSuffix;

        private System.Windows.Forms.GroupBox grpExtensions;
        private System.Windows.Forms.ListBox lstSupportedExts;
        private System.Windows.Forms.Label lblNewExt;
        private System.Windows.Forms.TextBox txtNewExt;
        private System.Windows.Forms.Button btnAddExt;
        private System.Windows.Forms.Button btnRemoveExt;
        private System.Windows.Forms.Button btnResetExts;
        private System.Windows.Forms.Label lblExtHint;

        private System.Windows.Forms.CheckBox chkRememberCheckboxes;

        private System.Windows.Forms.Button btnOK;
        private System.Windows.Forms.Button btnCancel;

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
            this.grpExtensions = new System.Windows.Forms.GroupBox();
            this.lstSupportedExts = new System.Windows.Forms.ListBox();
            this.lblNewExt = new System.Windows.Forms.Label();
            this.txtNewExt = new System.Windows.Forms.TextBox();
            this.btnAddExt = new System.Windows.Forms.Button();
            this.btnRemoveExt = new System.Windows.Forms.Button();
            this.btnResetExts = new System.Windows.Forms.Button();
            this.lblExtHint = new System.Windows.Forms.Label();
            this.chkRememberCheckboxes = new System.Windows.Forms.CheckBox();
            this.btnOK = new System.Windows.Forms.Button();
            this.btnCancel = new System.Windows.Forms.Button();
            this.grpExtensions.SuspendLayout();
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
            // grpExtensions
            // 
            this.grpExtensions.Controls.Add(this.lstSupportedExts);
            this.grpExtensions.Controls.Add(this.lblNewExt);
            this.grpExtensions.Controls.Add(this.txtNewExt);
            this.grpExtensions.Controls.Add(this.btnAddExt);
            this.grpExtensions.Controls.Add(this.btnRemoveExt);
            this.grpExtensions.Controls.Add(this.btnResetExts);
            this.grpExtensions.Controls.Add(this.lblExtHint);
            this.grpExtensions.Location = new System.Drawing.Point(15, 185);
            this.grpExtensions.Name = "grpExtensions";
            this.grpExtensions.Size = new System.Drawing.Size(380, 175);
            this.grpExtensions.TabIndex = 7;
            this.grpExtensions.TabStop = false;
            this.grpExtensions.Text = "Supported Video File Extensions";
            // 
            // lstSupportedExts
            // 
            this.lstSupportedExts.FormattingEnabled = true;
            this.lstSupportedExts.IntegralHeight = false;
            this.lstSupportedExts.ItemHeight = 15;
            this.lstSupportedExts.Location = new System.Drawing.Point(12, 22);
            this.lstSupportedExts.Name = "lstSupportedExts";
            this.lstSupportedExts.SelectionMode = System.Windows.Forms.SelectionMode.MultiExtended;
            this.lstSupportedExts.Size = new System.Drawing.Size(150, 140);
            this.lstSupportedExts.TabIndex = 0;
            // 
            // lblNewExt
            // 
            this.lblNewExt.AutoSize = true;
            this.lblNewExt.Location = new System.Drawing.Point(175, 25);
            this.lblNewExt.Name = "lblNewExt";
            this.lblNewExt.Size = new System.Drawing.Size(86, 15);
            this.lblNewExt.TabIndex = 1;
            this.lblNewExt.Text = "Add extension:";
            // 
            // txtNewExt
            // 
            this.txtNewExt.Location = new System.Drawing.Point(175, 45);
            this.txtNewExt.Name = "txtNewExt";
            this.txtNewExt.Size = new System.Drawing.Size(90, 23);
            this.txtNewExt.TabIndex = 2;
            this.txtNewExt.KeyDown += new System.Windows.Forms.KeyEventHandler(this.txtNewExt_KeyDown);
            // 
            // btnAddExt
            // 
            this.btnAddExt.Location = new System.Drawing.Point(270, 43);
            this.btnAddExt.Name = "btnAddExt";
            this.btnAddExt.Size = new System.Drawing.Size(94, 26);
            this.btnAddExt.TabIndex = 3;
            this.btnAddExt.Text = "Add";
            this.btnAddExt.UseVisualStyleBackColor = true;
            this.btnAddExt.Click += new System.EventHandler(this.btnAddExt_Click);
            // 
            // btnRemoveExt
            // 
            this.btnRemoveExt.Location = new System.Drawing.Point(175, 80);
            this.btnRemoveExt.Name = "btnRemoveExt";
            this.btnRemoveExt.Size = new System.Drawing.Size(189, 26);
            this.btnRemoveExt.TabIndex = 4;
            this.btnRemoveExt.Text = "Remove selected";
            this.btnRemoveExt.UseVisualStyleBackColor = true;
            this.btnRemoveExt.Click += new System.EventHandler(this.btnRemoveExt_Click);
            // 
            // btnResetExts
            // 
            this.btnResetExts.Location = new System.Drawing.Point(175, 115);
            this.btnResetExts.Name = "btnResetExts";
            this.btnResetExts.Size = new System.Drawing.Size(189, 26);
            this.btnResetExts.TabIndex = 5;
            this.btnResetExts.Text = "Reset to defaults";
            this.btnResetExts.UseVisualStyleBackColor = true;
            this.btnResetExts.Click += new System.EventHandler(this.btnResetExts_Click);
            // 
            // lblExtHint
            // 
            this.lblExtHint.AutoSize = true;
            this.lblExtHint.Location = new System.Drawing.Point(175, 147);
            this.lblExtHint.Name = "lblExtHint";
            this.lblExtHint.Size = new System.Drawing.Size(194, 15);
            this.lblExtHint.TabIndex = 6;
            this.lblExtHint.Text = "Examples: .mp4  mkv  m2ts";
            // 
            // chkRememberCheckboxes
            // 
            this.chkRememberCheckboxes.AutoSize = true;
            this.chkRememberCheckboxes.Location = new System.Drawing.Point(15, 370);
            this.chkRememberCheckboxes.Name = "chkRememberCheckboxes";
            this.chkRememberCheckboxes.Size = new System.Drawing.Size(288, 19);
            this.chkRememberCheckboxes.TabIndex = 8;
            this.chkRememberCheckboxes.Text = "Remember last-used settings for checkboxes";
            this.chkRememberCheckboxes.UseVisualStyleBackColor = true;
            // 
            // btnOK
            // 
            this.btnOK.Location = new System.Drawing.Point(15, 405);
            this.btnOK.Name = "btnOK";
            this.btnOK.Size = new System.Drawing.Size(75, 25);
            this.btnOK.TabIndex = 9;
            this.btnOK.Text = "OK";
            this.btnOK.UseVisualStyleBackColor = true;
            this.btnOK.Click += new System.EventHandler(this.btnOK_Click);
            // 
            // btnCancel
            // 
            this.btnCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.btnCancel.Location = new System.Drawing.Point(100, 405);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(75, 25);
            this.btnCancel.TabIndex = 10;
            this.btnCancel.Text = "Cancel";
            this.btnCancel.UseVisualStyleBackColor = true;
            // 
            // SettingsForm
            // 
            this.AcceptButton = this.btnOK;
            this.CancelButton = this.btnCancel;
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(420, 445);
            this.Controls.Add(this.btnCancel);
            this.Controls.Add(this.btnOK);
            this.Controls.Add(this.chkRememberCheckboxes);
            this.Controls.Add(this.grpExtensions);
            this.Controls.Add(this.txtSuffix);
            this.Controls.Add(this.lblSuffix);
            this.Controls.Add(this.txtPattern);
            this.Controls.Add(this.lblPattern);
            this.Controls.Add(this.btnBrowseUpdate);
            this.Controls.Add(this.txtUpdateFolder);
            this.Controls.Add(this.lblUpdateFolder);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "SettingsForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Settings";
            this.Load += new System.EventHandler(this.SettingsForm_Load);
            this.grpExtensions.ResumeLayout(false);
            this.grpExtensions.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
