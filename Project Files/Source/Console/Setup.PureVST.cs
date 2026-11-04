//  Setup.PureVST.cs
//
//  Setup > Audio > "TX Advanced": "Pure VST TX" - the TX audio comes shaped from the VST chain, so WDSP skips its own
//  dynamics/EQ (phase rotator, downward expander, TX EQ, Leveler, CFC, COMP, CESSB; SetTXAPureVST in TXA.c). The
//  primary TX filter and the ALC stay: 0 dB TX AF (sine) = full PEP. Off = everything as before.
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

using System;
using System.Drawing;
using System.Windows.Forms;

namespace Thetis
{
    public partial class Setup
    {
        private TabPage tpAudioTXAdvanced;
        private CheckBoxTS chkTXPureVST;

        // created before getOptions() so the switch is saved/restored
        private void initPureVSTControls()
        {
            tpAudioTXAdvanced = new TabPage();
            tpAudioTXAdvanced.Name = "tpAudioTXAdvanced";
            tpAudioTXAdvanced.Text = "TX Advanced";
            tpAudioTXAdvanced.UseVisualStyleBackColor = true;

            GroupBoxTS grp = new GroupBoxTS();
            grp.Name = "grpTXPureVST";
            grp.Text = "Pure VST TX";
            grp.Location = new Point(8, 8);
            grp.Size = new Size(460, 132);

            chkTXPureVST = new CheckBoxTS();
            chkTXPureVST.Name = "chkTXPureVST";
            chkTXPureVST.Text = "Pure VST TX (no WDSP audio processing)";
            chkTXPureVST.Location = new Point(12, 22);
            chkTXPureVST.AutoSize = true;
            chkTXPureVST.CheckedChanged += chkTXPureVST_CheckedChanged;

            LabelTS info = new LabelTS();
            info.Name = "lblTXPureVSTInfo";
            info.Text = "On: Leveler, CESSB, COMP, CFC, TX EQ, phase rotator and downward expander are bypassed - the sound " +
                        "comes only from the TX VST chain. Only the TX filter and ALC remain: TX AF 0 dB = full power " +
                        "(see TX AF / TX PEP above the panorama). Off: everything works as before.";
            info.Location = new Point(12, 46);
            info.Size = new Size(436, 78);

            toolTip1.SetToolTip(chkTXPureVST, info.Text);
            grp.Controls.Add(chkTXPureVST);
            grp.Controls.Add(info);
            tpAudioTXAdvanced.Controls.Add(grp);
            tcAudio.TabPages.Insert(tcAudio.TabPages.IndexOf(tpAdvancedAudio) + 1, tpAudioTXAdvanced);
        }

        private void chkTXPureVST_CheckedChanged(object sender, EventArgs e)
        {
            WDSP.SetTXAPureVST(WDSP.id(1, 0), chkTXPureVST.Checked ? 1 : 0);
        }
    }
}
