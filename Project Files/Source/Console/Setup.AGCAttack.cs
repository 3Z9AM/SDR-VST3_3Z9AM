using System;
using System.Drawing;
using System.Windows.Forms;

namespace Thetis
{
    // AGC attack and impulse ("pop") recovery for RX1 / RX2.
    // Controls live on Setup > DSP > AGC/ALC; they are created before getOptions() so
    // the generic control walk saves/restores them like designer controls.
    public partial class Setup
    {
        private GroupBoxTS grpDSPAGCAttack;
        private NumericUpDownTS udDSPAGCAttack;
        private NumericUpDownTS udDSPAGCRX2Attack;
        private NumericUpDownTS udDSPAGCFastDecay;
        private NumericUpDownTS udDSPAGCRX2FastDecay;
        private NumericUpDownTS udDSPAGCPopRatio;
        private NumericUpDownTS udDSPAGCRX2PopRatio;

        private void initAGCAttackControls()
        {
            grpDSPAGCAttack = new GroupBoxTS();
            grpDSPAGCAttack.Text = "AGC Attack / Impulse Recovery";
            grpDSPAGCAttack.Location = new Point(264, 270);
            grpDSPAGCAttack.Size = new Size(296, 112);
            grpDSPAGCAttack.Name = "grpDSPAGCAttack";

            addAGCAttackLabel("RX1", new Point(150, 16), 50);
            addAGCAttackLabel("RX2", new Point(220, 16), 50);

            string tipAttack = "AGC attack time constant (1-10 ms). Longer = softer onset of syllables and CW " +
                               "elements, less click/distortion on strong signals. The AGC looks ahead 4 x attack, " +
                               "so audio delay grows by the same amount. Default 1 ms.";
            string tipDecay = "Impulse recovery time (ms): after a short pop/crash the AGC recovers with this " +
                              "time constant instead of hang/decay, so it does not stay deaf after a single impulse. " +
                              "Shorter = faster recovery. Default 5 ms.";
            string tipRatio = "Impulse detection ratio: a peak this many times above the 250 ms average level " +
                              "is treated as an impulse. Lower = more peaks treated as impulses (faster recovery " +
                              "on QRN); higher = closer to plain hang/decay. Default 5.0.";

            addAGCAttackLabel("Attack (ms)", new Point(6, 36), 140);
            udDSPAGCAttack = addAGCAttackUpDown("udDSPAGCAttack", new Point(150, 34), 1, 10, 1, 0, 1, tipAttack);
            udDSPAGCRX2Attack = addAGCAttackUpDown("udDSPAGCRX2Attack", new Point(220, 34), 1, 10, 1, 0, 1, tipAttack);

            addAGCAttackLabel("Impulse recovery (ms)", new Point(6, 62), 140);
            udDSPAGCFastDecay = addAGCAttackUpDown("udDSPAGCFastDecay", new Point(150, 60), 1, 200, 5, 0, 1, tipDecay);
            udDSPAGCRX2FastDecay = addAGCAttackUpDown("udDSPAGCRX2FastDecay", new Point(220, 60), 1, 200, 5, 0, 1, tipDecay);

            addAGCAttackLabel("Impulse ratio (x)", new Point(6, 88), 140);
            udDSPAGCPopRatio = addAGCAttackUpDown("udDSPAGCPopRatio", new Point(150, 86), 1.5m, 50m, 5m, 1, 0.5m, tipRatio);
            udDSPAGCRX2PopRatio = addAGCAttackUpDown("udDSPAGCRX2PopRatio", new Point(220, 86), 1.5m, 50m, 5m, 1, 0.5m, tipRatio);

            udDSPAGCAttack.ValueChanged += udDSPAGCAttack_ValueChanged;
            udDSPAGCRX2Attack.ValueChanged += udDSPAGCRX2Attack_ValueChanged;
            udDSPAGCFastDecay.ValueChanged += udDSPAGCFastDecay_ValueChanged;
            udDSPAGCRX2FastDecay.ValueChanged += udDSPAGCRX2FastDecay_ValueChanged;
            udDSPAGCPopRatio.ValueChanged += udDSPAGCPopRatio_ValueChanged;
            udDSPAGCRX2PopRatio.ValueChanged += udDSPAGCRX2PopRatio_ValueChanged;

            tpDSPAGCALC.Controls.Add(grpDSPAGCAttack);
        }

        private void addAGCAttackLabel(string text, Point loc, int width)
        {
            LabelTS lbl = new LabelTS();
            lbl.Text = text;
            lbl.Location = loc;
            lbl.Size = new Size(width, 16);
            grpDSPAGCAttack.Controls.Add(lbl);
        }

        private NumericUpDownTS addAGCAttackUpDown(string name, Point loc, decimal min, decimal max,
                                                   decimal value, int decimals, decimal inc, string tip)
        {
            NumericUpDownTS ud = new NumericUpDownTS();
            ud.Name = name;
            ud.Minimum = min;
            ud.Maximum = max;
            ud.DecimalPlaces = decimals;
            ud.Increment = inc;
            ud.Value = value;
            ud.Location = loc;
            ud.Size = new Size(56, 20);
            toolTip1.SetToolTip(ud, tip);
            grpDSPAGCAttack.Controls.Add(ud);
            return ud;
        }

        private void forceAGCAttackControls()
        {
            udDSPAGCAttack_ValueChanged(this, EventArgs.Empty);
            udDSPAGCRX2Attack_ValueChanged(this, EventArgs.Empty);
            udDSPAGCFastDecay_ValueChanged(this, EventArgs.Empty);
            udDSPAGCRX2FastDecay_ValueChanged(this, EventArgs.Empty);
            udDSPAGCPopRatio_ValueChanged(this, EventArgs.Empty);
            udDSPAGCRX2PopRatio_ValueChanged(this, EventArgs.Empty);
        }

        private void udDSPAGCAttack_ValueChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            console.radio.GetDSPRX(0, 0).RXAGCAttack = (int)udDSPAGCAttack.Value;
            console.radio.GetDSPRX(0, 1).RXAGCAttack = (int)udDSPAGCAttack.Value;
        }

        private void udDSPAGCRX2Attack_ValueChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            console.radio.GetDSPRX(1, 0).RXAGCAttack = (int)udDSPAGCRX2Attack.Value;
        }

        private void udDSPAGCFastDecay_ValueChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            console.radio.GetDSPRX(0, 0).RXAGCFastDecay = (double)udDSPAGCFastDecay.Value;
            console.radio.GetDSPRX(0, 1).RXAGCFastDecay = (double)udDSPAGCFastDecay.Value;
        }

        private void udDSPAGCRX2FastDecay_ValueChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            console.radio.GetDSPRX(1, 0).RXAGCFastDecay = (double)udDSPAGCRX2FastDecay.Value;
        }

        private void udDSPAGCPopRatio_ValueChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            console.radio.GetDSPRX(0, 0).RXAGCPopRatio = (double)udDSPAGCPopRatio.Value;
            console.radio.GetDSPRX(0, 1).RXAGCPopRatio = (double)udDSPAGCPopRatio.Value;
        }

        private void udDSPAGCRX2PopRatio_ValueChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            console.radio.GetDSPRX(1, 0).RXAGCPopRatio = (double)udDSPAGCRX2PopRatio.Value;
        }
    }
}
