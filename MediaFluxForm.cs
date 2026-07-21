using System.Drawing;
using System.Windows.Forms;

namespace MediaFlux
{
    public class MediaFluxForm : Form
    {
        public MediaFluxForm()
        {
            using Icon? executableIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (executableIcon != null)
                Icon = (Icon)executableIcon.Clone();
        }
    }
}
