//  Setup.AGCAttack.cs
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
        private GroupBoxTS grpDSPRXLimiter;
        private CheckBoxTS chkDSPRXLimiter;
        private NumericUpDownTS udDSPRXLimiterThresh;
        private NumericUpDownTS udDSPRXLimiterRelease;
        private GroupBoxTS grpDSPAGCPauseHold;
        private CheckBoxTS chkDSPAGCPauseHold;
        private NumericUpDownTS udDSPAGCPauseRise;
        private NumericUpDownTS udDSPAGCPauseRelease;
        private GroupBoxTS grpDSPMultibandAGC;
        private CheckBoxTS chkDSPMultibandAGC;
        private TrackBarTS tbDSPMultibandAGCStrength;
        private LabelTS lblDSPMultibandAGCStrength;
        private NumericUpDownTS udDSPMultibandAGCMax;

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

            initRXLimiterControls();
        }

        // RX peak limiter (wdsp rxlim, end of RXA): one setting for RX1, its sub-RX and RX2
        private void initRXLimiterControls()
        {
            grpDSPRXLimiter = new GroupBoxTS();
            grpDSPRXLimiter.Text = "RX Peak Limiter";
            grpDSPRXLimiter.Location = new Point(568, 8);
            grpDSPRXLimiter.Size = new Size(150, 104);
            grpDSPRXLimiter.Name = "grpDSPRXLimiter";

            string tip = "Peak limiter at the very end of the RX DSP chain (after APF, NR, Crystal filter, Warmth, " +
                         "BIN2 / Spatial): audio never goes above the threshold, so nothing clips in the radio " +
                         "codec or VAC. Looks ahead 1 ms (adds 1 ms delay); below the threshold the audio is untouched.";

            chkDSPRXLimiter = new CheckBoxTS();
            chkDSPRXLimiter.Name = "chkDSPRXLimiter";
            chkDSPRXLimiter.Text = "Enabled";
            chkDSPRXLimiter.Checked = true;
            chkDSPRXLimiter.Location = new Point(8, 18);
            chkDSPRXLimiter.Size = new Size(130, 20);
            toolTip1.SetToolTip(chkDSPRXLimiter, tip);
            grpDSPRXLimiter.Controls.Add(chkDSPRXLimiter);

            addRXLimiterLabel("Threshold (dBFS)", new Point(6, 46));
            udDSPRXLimiterThresh = addRXLimiterUpDown("udDSPRXLimiterThresh", new Point(92, 44), -12m, 0m, -1m, 1, 0.5m,
                "Highest audio peak, dBFS. Default -1.");
            addRXLimiterLabel("Release (ms)", new Point(6, 74));
            udDSPRXLimiterRelease = addRXLimiterUpDown("udDSPRXLimiterRelease", new Point(92, 72), 5m, 500m, 50m, 0, 5m,
                "How fast the gain comes back after a peak. Shorter = louder but can pump; default 50 ms.");

            chkDSPRXLimiter.CheckedChanged += rxLimiter_Changed;
            udDSPRXLimiterThresh.ValueChanged += rxLimiter_Changed;
            udDSPRXLimiterRelease.ValueChanged += rxLimiter_Changed;

            tpDSPAGCALC.Controls.Add(grpDSPRXLimiter);

            initAGCPauseHoldControls();
            initMultibandAGCControls();
        }

        // dual-loop AGC "pause hold" (wcpAGC + wdsprs agcfloor), same for RX1, sub-RX and RX2
        private void initAGCPauseHoldControls()
        {
            grpDSPAGCPauseHold = new GroupBoxTS();
            grpDSPAGCPauseHold.Text = "AGC Pause Hold";
            grpDSPAGCPauseHold.Location = new Point(568, 118);
            grpDSPAGCPauseHold.Size = new Size(150, 104);
            grpDSPAGCPauseHold.Name = "grpDSPAGCPauseHold";

            string tip = "Dual-loop AGC: a second, slow detector of the station level. In speech pauses the AGC " +
                         "gain rises at most 'Max rise' dB over the gain during speech, so the noise does not " +
                         "breathe up between words. After the station stops for good the slow level decays " +
                         "('Release') and the gain comes up to the band noise as before.";
            chkDSPAGCPauseHold = new CheckBoxTS();
            chkDSPAGCPauseHold.Name = "chkDSPAGCPauseHold";
            chkDSPAGCPauseHold.Text = "Enabled";
            chkDSPAGCPauseHold.Location = new Point(8, 18);
            chkDSPAGCPauseHold.Size = new Size(130, 20);
            toolTip1.SetToolTip(chkDSPAGCPauseHold, tip);
            grpDSPAGCPauseHold.Controls.Add(chkDSPAGCPauseHold);

            addGroupLabel(grpDSPAGCPauseHold, "Max rise (dB)", new Point(6, 46));
            udDSPAGCPauseRise = addGroupUpDown(grpDSPAGCPauseHold, "udDSPAGCPauseRise", new Point(92, 44), 3m, 30m, 10m, 0, 1m,
                "Most the gain may rise in a speech pause over the gain during speech. Default 10 dB.");
            addGroupLabel(grpDSPAGCPauseHold, "Release (s)", new Point(6, 74));
            udDSPAGCPauseRelease = addGroupUpDown(grpDSPAGCPauseHold, "udDSPAGCPauseRelease", new Point(92, 72), 1m, 10m, 3m, 1, 0.5m,
                "How fast the slow station level decays after the station stops. Default 3 s.");

            chkDSPAGCPauseHold.CheckedChanged += agcPauseHold_Changed;
            udDSPAGCPauseRise.ValueChanged += agcPauseHold_Changed;
            udDSPAGCPauseRelease.ValueChanged += agcPauseHold_Changed;
            tpDSPAGCALC.Controls.Add(grpDSPAGCPauseHold);
        }

        // multiband AGC (wdsp mbagc), same for RX1, sub-RX and RX2
        private void initMultibandAGCControls()
        {
            grpDSPMultibandAGC = new GroupBoxTS();
            grpDSPMultibandAGC.Text = "Multiband AGC";
            grpDSPMultibandAGC.Location = new Point(568, 228);
            grpDSPMultibandAGC.Size = new Size(150, 130);
            grpDSPMultibandAGC.Name = "grpDSPMultibandAGC";

            string tip = "Multiband AGC after the normal AGC: bands on the ear's scale across the RX filter (more for a wider " +
                         "filter: CW 2, SSB 2.7 kHz 12, 4 kHz 15, 8 kHz 20, max 24), reference = " +
                         "the station's own spectrum of the last seconds. Strength 0%: only when one band dominates " +
                         "(strong station, carrier or CW tone in the filter) the other bands get their level back. " +
                         "100%: every band is pulled to its usual level all the time (dense, 'processed' sound). " +
                         "A band under the noise is never raised. Adds 10.7 ms delay.";
            chkDSPMultibandAGC = new CheckBoxTS();
            chkDSPMultibandAGC.Name = "chkDSPMultibandAGC";
            chkDSPMultibandAGC.Text = "Enabled";
            chkDSPMultibandAGC.Location = new Point(8, 18);
            chkDSPMultibandAGC.Size = new Size(130, 20);
            toolTip1.SetToolTip(chkDSPMultibandAGC, tip);
            grpDSPMultibandAGC.Controls.Add(chkDSPMultibandAGC);

            lblDSPMultibandAGCStrength = addGroupLabel(grpDSPMultibandAGC, "Strength 30%", new Point(6, 42));
            lblDSPMultibandAGCStrength.Size = new Size(130, 16);
            tbDSPMultibandAGCStrength = new TrackBarTS();
            tbDSPMultibandAGCStrength.Name = "tbDSPMultibandAGCStrength";
            tbDSPMultibandAGCStrength.Minimum = 0;
            tbDSPMultibandAGCStrength.Maximum = 100;
            tbDSPMultibandAGCStrength.TickFrequency = 25;
            tbDSPMultibandAGCStrength.SmallChange = 5;
            tbDSPMultibandAGCStrength.LargeChange = 25;
            tbDSPMultibandAGCStrength.Value = 30;
            tbDSPMultibandAGCStrength.Location = new Point(4, 58);
            tbDSPMultibandAGCStrength.Size = new Size(140, 30);
            toolTip1.SetToolTip(tbDSPMultibandAGCStrength, "0% = protect from one dominating band only, 100% = every band to its usual level.");
            grpDSPMultibandAGC.Controls.Add(tbDSPMultibandAGCStrength);

            addGroupLabel(grpDSPMultibandAGC, "Max (dB)", new Point(6, 100));
            udDSPMultibandAGCMax = addGroupUpDown(grpDSPMultibandAGC, "udDSPMultibandAGCMax", new Point(92, 98), 3m, 18m, 12m, 0, 1m,
                "Most a band is raised or lowered. Default 12 dB.");

            chkDSPMultibandAGC.CheckedChanged += multibandAGC_Changed;
            tbDSPMultibandAGCStrength.ValueChanged += multibandAGC_Changed;
            udDSPMultibandAGCMax.ValueChanged += multibandAGC_Changed;
            tpDSPAGCALC.Controls.Add(grpDSPMultibandAGC);
        }

        private LabelTS addGroupLabel(GroupBoxTS grp, string text, Point loc)
        {
            LabelTS lbl = new LabelTS();
            lbl.Text = text;
            lbl.Location = loc;
            lbl.Size = new Size(86, 16);
            grp.Controls.Add(lbl);
            return lbl;
        }

        private NumericUpDownTS addGroupUpDown(GroupBoxTS grp, string name, Point loc, decimal min, decimal max,
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
            ud.Size = new Size(52, 20);
            toolTip1.SetToolTip(ud, tip);
            grp.Controls.Add(ud);
            return ud;
        }

        private RadioDSPRX[] allDSPRX() =>
            new[] { console.radio.GetDSPRX(0, 0), console.radio.GetDSPRX(0, 1), console.radio.GetDSPRX(1, 0) };

        private void agcPauseHold_Changed(object sender, EventArgs e)
        {
            if (initializing) return;
            udDSPAGCPauseRise.Enabled = udDSPAGCPauseRelease.Enabled = chkDSPAGCPauseHold.Checked;
            foreach (var rx in allDSPRX())
            {
                rx.RXAGCFloorRise = (double)udDSPAGCPauseRise.Value;
                rx.RXAGCFloorTau = (double)udDSPAGCPauseRelease.Value;
                rx.RXAGCFloor = chkDSPAGCPauseHold.Checked;
            }
        }

        private void multibandAGC_Changed(object sender, EventArgs e)
        {
            lblDSPMultibandAGCStrength.Text = "Strength " + tbDSPMultibandAGCStrength.Value + "%";
            if (initializing) return;
            tbDSPMultibandAGCStrength.Enabled = udDSPMultibandAGCMax.Enabled = chkDSPMultibandAGC.Checked;
            foreach (var rx in allDSPRX())
            {
                rx.RXMultibandAGCStrength = tbDSPMultibandAGCStrength.Value / 100.0;
                rx.RXMultibandAGCMax = (double)udDSPMultibandAGCMax.Value;
                rx.RXMultibandAGC = chkDSPMultibandAGC.Checked;
            }
        }

        private void addRXLimiterLabel(string text, Point loc)
        {
            LabelTS lbl = new LabelTS();
            lbl.Text = text;
            lbl.Location = loc;
            lbl.Size = new Size(86, 16);
            grpDSPRXLimiter.Controls.Add(lbl);
        }

        private NumericUpDownTS addRXLimiterUpDown(string name, Point loc, decimal min, decimal max,
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
            ud.Size = new Size(52, 20);
            toolTip1.SetToolTip(ud, tip);
            grpDSPRXLimiter.Controls.Add(ud);
            return ud;
        }

        private void rxLimiter_Changed(object sender, EventArgs e)
        {
            if (initializing) return;
            udDSPRXLimiterThresh.Enabled = udDSPRXLimiterRelease.Enabled = chkDSPRXLimiter.Checked;
            foreach (var rx in new[] { console.radio.GetDSPRX(0, 0), console.radio.GetDSPRX(0, 1), console.radio.GetDSPRX(1, 0) })
            {
                rx.RXLimiterThresh = (double)udDSPRXLimiterThresh.Value;
                rx.RXLimiterRelease = (double)udDSPRXLimiterRelease.Value;
                rx.RXLimiter = chkDSPRXLimiter.Checked;
            }
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
            rxLimiter_Changed(this, EventArgs.Empty);
            agcPauseHold_Changed(this, EventArgs.Empty);
            multibandAGC_Changed(this, EventArgs.Empty);
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
