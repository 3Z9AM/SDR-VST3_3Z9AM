//  Setup.AnalogTone.cs
//
//  Copyright (C) 2026 Daniel Poznanski, 3Z9AM
//
//  This program is free software; you can redistribute it and/or
//  modify it under the terms of the GNU General Public License
//  as published by the Free Software Foundation; either version 2
//  of the License, or (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program; if not, write to the Free Software
//  Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace Thetis
{
    // Analog tone (wdsp/atone.c, filter in wdsprs/src/atone.rs) on Setup > DSP > Spatial / Tone:
    // the gentle treble roll-off of an analog receiver's audio chain for the RX audio.
    // Created before getOptions() so the controls are saved/restored; the values go through
    // radio.cs, which applies them once the WDSP channels exist.
    public partial class Setup
    {
        private GroupBoxTS grpDSPAnalogTone;
        private CheckBoxTS chkDSPAnalogTone, chkDSPAnalogToneFollow;
        private NumericUpDownTS udDSPAnalogToneCorner, udDSPAnalogToneFollowPct, udDSPAnalogToneDepth, udDSPAnalogToneRipple;

        private void initAnalogToneControls()
        {
            tpDSPSpatial.Text = "Spatial / Tone";
            grpDSPAnalogTone = new GroupBoxTS { Text = "Analog tone (RX audio)", Name = "grpDSPAnalogTone", Location = new Point(8, 172), Size = new Size(330, 180) };
            chkDSPAnalogTone = new CheckBoxTS { Text = "Analog tone: darker, softer noise", Name = "chkDSPAnalogTone", Location = new Point(10, 20), AutoSize = true };
            chkDSPAnalogTone.CheckedChanged += analogTone_Changed;
            toolTip1.SetToolTip(chkDSPAnalogTone, "The gentle treble roll-off of an analog receiver's audio chain (AF amplifier, transformer, speaker): " +
                "flat up to the corner, then -6 dB per octave, at most the depth below. The background noise loses its hiss, " +
                "speech below the corner is untouched. No added delay, no ringing. Off by itself in digital modes (DIGU/DIGL).");
            chkDSPAnalogToneFollow = new CheckBoxTS { Text = "Follow RX filter", Name = "chkDSPAnalogToneFollow", Location = new Point(10, 44), AutoSize = true, Checked = true };
            chkDSPAnalogToneFollow.CheckedChanged += analogTone_Changed;
            toolTip1.SetToolTip(chkDSPAnalogToneFollow, "The corner moves with the RX filter: a % of its upper audio edge, so a wide filter " +
                "(e.g. 5 kHz) keeps its top and a narrow one gets the same character. Off = a fixed corner in Hz.");
            grpDSPAnalogTone.Controls.AddRange(new Control[] { chkDSPAnalogTone, chkDSPAnalogToneFollow });

            udDSPAnalogToneFollowPct = addAnalogToneRow("Corner (% of filter)", "udDSPAnalogToneFollowPct", 68, 40, 100, 70, 5, 0,
                "With Follow RX filter: where the roll-off starts, as a % of the filter's upper edge (70% of 2.7 kHz = 1.9 kHz, of 5 kHz = 3.5 kHz).");
            udDSPAnalogToneCorner = addAnalogToneRow("Corner (Hz)", "udDSPAnalogToneCorner", 94, 1000, 5000, 2000, 100, 0,
                "Without Follow RX filter: where the roll-off starts (about -3 dB). Lower = darker.");
            udDSPAnalogToneDepth = addAnalogToneRow("Depth (dB)", "udDSPAnalogToneDepth", 120, 0, 12, 8, 1, 0,
                "The most it takes off at the top of the band. 0 = no change.");
            udDSPAnalogToneRipple = addAnalogToneRow("Crystal ripple (dB)", "udDSPAnalogToneRipple", 146, 0, 2, 0, 0.1m, 1,
                "The uneven passband of a 6-pole crystal filter: gentle peaks and dips across the RX filter, denser toward its edges, " +
                "with the group delay wandering like in the real thing. Peak to peak; 0 = flat. Follows the filter width.");

            tpDSPSpatial.Controls.Add(grpDSPAnalogTone);
        }

        private NumericUpDownTS addAnalogToneRow(string label, string name, int y, decimal min, decimal max, decimal value, decimal step, int decimals, string tip)
        {
            LabelTS lbl = new LabelTS { Text = label, Name = "lbl" + name.Substring(2), Location = new Point(10, y + 2), Size = new Size(150, 16) };
            NumericUpDownTS ud = new NumericUpDownTS
            {
                Name = name, Location = new Point(166, y), Size = new Size(64, 20),
                Minimum = min, Maximum = max, Value = value, Increment = step, DecimalPlaces = decimals,
            };
            ud.ValueChanged += analogTone_Changed;
            toolTip1.SetToolTip(ud, tip);
            toolTip1.SetToolTip(lbl, tip);
            grpDSPAnalogTone.Controls.Add(lbl);
            grpDSPAnalogTone.Controls.Add(ud);
            return ud;
        }

        private void analogTone_Changed(object sender, EventArgs e)
        {
            udDSPAnalogToneCorner.Enabled = !chkDSPAnalogToneFollow.Checked;
            udDSPAnalogToneFollowPct.Enabled = chkDSPAnalogToneFollow.Checked;
            if (initializing) return;
            foreach (RadioDSPRX rx in new[] { console.radio.GetDSPRX(0, 0), console.radio.GetDSPRX(0, 1), console.radio.GetDSPRX(1, 0) })
            {
                rx.RXAnalogToneCorner = (double)udDSPAnalogToneCorner.Value;
                rx.RXAnalogToneFollowPct = (double)udDSPAnalogToneFollowPct.Value;
                rx.RXAnalogToneFollow = chkDSPAnalogToneFollow.Checked;
                rx.RXAnalogToneDepth = (double)udDSPAnalogToneDepth.Value;
                rx.RXAnalogToneRipple = (double)udDSPAnalogToneRipple.Value;
                rx.RXAnalogTone = chkDSPAnalogTone.Checked;
            }
        }
    }
}
