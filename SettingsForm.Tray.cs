using MediaFlux.Models;

namespace MediaFlux;

public partial class SettingsForm
{
    private void InitializeSystemTraySettingsControl(Config cfg)
    {
        // Keep the existing options in their established order rather than
        // overlaying the large-queue controls that begin at this position.
        foreach (Control control in Controls.Cast<Control>().Where(control => control.Top >= 490))
            control.Top += 30;

        chkMinimizeToSystemTray = new CheckBox
        {
            Name = "chkMinimizeToSystemTray",
            Text = "Minimize to system tray when minimized",
            AutoSize = true,
            Location = new Point(15, 490),
            Checked = cfg.MinimizeToSystemTrayWhenMinimized,
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };
        Controls.Add(chkMinimizeToSystemTray);
    }
}
