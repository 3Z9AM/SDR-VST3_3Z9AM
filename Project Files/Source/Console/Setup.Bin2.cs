using System;
using System.Drawing;
using System.Windows.Forms;

namespace Thetis
{
    // BIN2 spectral binaural (wdsp/bin2.c) on Setup > DSP > Options: while BIN is on, the audio is
    // spread across the stereo stage by its place in the RX filter (lowest RF left, highest right).
    // Spatial mode for speakers on its own page Setup > DSP > Spatial.
    // Created before getOptions() so the switches are saved/restored.
    public partial class Setup
    {
        private GroupBoxTS grpDSPBin2;
        private CheckBoxTS chkDSPBin2;
        private TabPage tpDSPSpatial;
        private GroupBoxTS grpDSPBin2Spatial;
        private CheckBoxTS chkDSPBin2Spatial;
        private NumericUpDownTS udDSPBin2Width, udDSPBin2Speaker, udDSPBin2Room;

        private void initBin2Controls()
        {
            grpDSPBin2 = new GroupBoxTS { Text = "Binaural (BIN)", Name = "grpDSPBin2", Location = new Point(534, 8), Size = new Size(184, 46) };
            chkDSPBin2 = new CheckBoxTS { Text = "BIN2: stereo by frequency", Name = "chkDSPBin2", Location = new Point(8, 19), AutoSize = true };
            chkDSPBin2.CheckedChanged += chkDSPBin2_CheckedChanged;
            toolTip1.SetToolTip(chkDSPBin2, "With BIN on, spread the audio across the stereo stage by its place in the RX filter: " +
                "USB low tones left, high right; LSB low tones right, high left (like the panadapter). " +
                "The spread follows the filter width. Off = classic BIN. Spatial sound: DSP > Spatial.");
            grpDSPBin2.Controls.Add(chkDSPBin2);
            tpDSPOptions.Controls.Add(grpDSPBin2);

            initBin2SpatialControls();
        }

        private void initBin2SpatialControls()
        {
            tpDSPSpatial = new TabPage { Text = "Spatial", Name = "tpDSPSpatial", UseVisualStyleBackColor = true };
            grpDSPBin2Spatial = new GroupBoxTS { Text = "Spatial sound (stereo speakers)", Name = "grpDSPBin2Spatial", Location = new Point(8, 8), Size = new Size(330, 156) };
            chkDSPBin2Spatial = new CheckBoxTS { Text = "Spatial: stations on an arc in front of you", Name = "chkDSPBin2Spatial", Location = new Point(10, 20), AutoSize = true };
            chkDSPBin2Spatial.CheckedChanged += bin2Spatial_Changed;
            toolTip1.SetToolTip(chkDSPBin2Spatial, "Needs BIN on and BIN2 (DSP > Options). Each place in the RX filter becomes a direction " +
                "on an arc in front of you (lowest RF left, like BIN2): a spherical head model plus crosstalk cancellation " +
                "for the two speakers lets sources sit outside the speakers, with a few early reflections for depth. " +
                "Sit centred between the speakers. Adds ~11 ms of delay (BIN2 alone ~21 ms).");
            grpDSPBin2Spatial.Controls.Add(chkDSPBin2Spatial);

            udDSPBin2Width = addSpatialRow("Arc width (+-deg)", "udDSPBin2Width", 50, 10, 90, 60,
                "Half width of the arc: the RX filter edges sit this far left and right of the centre. Beyond the speaker angle the sound goes outside the speakers.");
            udDSPBin2Speaker = addSpatialRow("Speakers at (+-deg)", "udDSPBin2Speaker", 78, 10, 45, 30,
                "Angle of each speaker from straight ahead, as seen from your listening position (a usual stereo triangle: 30).");
            udDSPBin2Room = addSpatialRow("Room (%)", "udDSPBin2Room", 106, 0, 100, 30,
                "Early reflections (floor, ceiling, walls) for depth, no reverb tail. 0 = dry.");

            tpDSPSpatial.Controls.Add(grpDSPBin2Spatial);
            tcDSP.TabPages.Add(tpDSPSpatial);
        }

        private NumericUpDownTS addSpatialRow(string label, string name, int y, int min, int max, int value, string tip)
        {
            LabelTS lbl = new LabelTS { Text = label, Name = "lbl" + name.Substring(2), Location = new Point(10, y + 2), Size = new Size(150, 16) };
            NumericUpDownTS ud = new NumericUpDownTS
            {
                Name = name, Location = new Point(166, y), Size = new Size(64, 20),
                Minimum = min, Maximum = max, Value = value, Increment = 5,
            };
            ud.ValueChanged += bin2Spatial_Changed;
            toolTip1.SetToolTip(ud, tip);
            toolTip1.SetToolTip(lbl, tip);
            grpDSPBin2Spatial.Controls.Add(lbl);
            grpDSPBin2Spatial.Controls.Add(ud);
            return ud;
        }

        private void chkDSPBin2_CheckedChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            console.radio.GetDSPRX(0, 0).RXBin2 = chkDSPBin2.Checked;
            console.radio.GetDSPRX(0, 1).RXBin2 = chkDSPBin2.Checked;
            console.radio.GetDSPRX(1, 0).RXBin2 = chkDSPBin2.Checked;
        }

        private void bin2Spatial_Changed(object sender, EventArgs e)
        {
            if (initializing) return;
            foreach (RadioDSPRX rx in new[] { console.radio.GetDSPRX(0, 0), console.radio.GetDSPRX(0, 1), console.radio.GetDSPRX(1, 0) })
            {
                rx.RXBin2Width = (double)udDSPBin2Width.Value;
                rx.RXBin2Speaker = (double)udDSPBin2Speaker.Value;
                rx.RXBin2Room = (double)udDSPBin2Room.Value / 100.0;
                rx.RXBin2Spatial = chkDSPBin2Spatial.Checked;
            }
        }
    }
}
