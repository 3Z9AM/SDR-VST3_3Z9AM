using System;
using System.Drawing;
using System.Windows.Forms;

namespace Thetis
{
    // Silent start + fade-in of the receiver after TX (Console.RXReturnRamp.cs), on
    // Setup > DSP > Options; created before getOptions() so the values are saved/restored.
    public partial class Setup
    {
        private GroupBoxTS grpRXReturnRamp;
        private LabelTS lblRXReturnDelay;
        private NumericUpDownTS udRXReturnDelay;
        private LabelTS lblRXReturnFade;
        private NumericUpDownTS udRXReturnFade;

        private void initRXReturnRampControls()
        {
            grpRXReturnRamp = new GroupBoxTS();
            grpRXReturnRamp.Text = "RX Return After TX (not CW)";
            grpRXReturnRamp.Location = new Point(419, 306);
            grpRXReturnRamp.Size = new Size(299, 46);
            grpRXReturnRamp.Name = "grpRXReturnRamp";

            lblRXReturnDelay = new LabelTS();
            lblRXReturnDelay.Text = "Silent (ms):";
            lblRXReturnDelay.Location = new Point(6, 20);
            lblRXReturnDelay.Size = new Size(66, 16);
            lblRXReturnDelay.Name = "lblRXReturnDelay";

            udRXReturnDelay = new NumericUpDownTS();
            udRXReturnDelay.Location = new Point(74, 17);
            udRXReturnDelay.Size = new Size(58, 20);
            udRXReturnDelay.Minimum = 0;
            udRXReturnDelay.Maximum = 1000;
            udRXReturnDelay.Increment = 10;
            udRXReturnDelay.Value = 0;
            udRXReturnDelay.Name = "udRXReturnDelay";
            udRXReturnDelay.ValueChanged += udRXReturnRamp_ValueChanged;

            lblRXReturnFade = new LabelTS();
            lblRXReturnFade.Text = "Fade-in (ms):";
            lblRXReturnFade.Location = new Point(150, 20);
            lblRXReturnFade.Size = new Size(74, 16);
            lblRXReturnFade.Name = "lblRXReturnFade";

            udRXReturnFade = new NumericUpDownTS();
            udRXReturnFade.Location = new Point(226, 17);
            udRXReturnFade.Size = new Size(58, 20);
            udRXReturnFade.Minimum = 10;
            udRXReturnFade.Maximum = 1000;
            udRXReturnFade.Increment = 10;
            udRXReturnFade.Value = 10;
            udRXReturnFade.Name = "udRXReturnFade";
            udRXReturnFade.ValueChanged += udRXReturnRamp_ValueChanged;

            string tip = "When the receiver comes back after transmitting it stays silent for 'Silent' ms and then " +
                         "fades in over 'Fade-in' ms. Hides the T/R relay transient and the crackle while the VAC " +
                         "buffers re-settle. Thetis default: 0 / 10. Try 100 / 150. CW (QSK) is not affected.";
            toolTip1.SetToolTip(udRXReturnDelay, tip);
            toolTip1.SetToolTip(udRXReturnFade, tip);

            grpRXReturnRamp.Controls.Add(lblRXReturnDelay);
            grpRXReturnRamp.Controls.Add(udRXReturnDelay);
            grpRXReturnRamp.Controls.Add(lblRXReturnFade);
            grpRXReturnRamp.Controls.Add(udRXReturnFade);
            tpDSPOptions.Controls.Add(grpRXReturnRamp);
        }

        private void udRXReturnRamp_ValueChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            console.SetRXReturnRamp((int)udRXReturnDelay.Value, (int)udRXReturnFade.Value);
        }
    }
}
