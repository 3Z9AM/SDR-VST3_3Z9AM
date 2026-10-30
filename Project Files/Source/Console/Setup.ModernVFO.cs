//  Setup.ModernVFO.cs
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
using System.Linq;
using System.Windows.Forms;

namespace Thetis
{
    // "Modern VFO" switch (Console.ModernVFO.cs) on Setup > Appearance > General;
    // created before getOptions() so it is saved/restored like designer controls.
    public partial class Setup
    {
        private GroupBoxTS grpModernVFO;
        private CheckBoxTS chkModernVFO;
        private CheckBoxTS chkModernSMeter;
        private CheckBoxTS chkModernUI;
        private CheckBoxTS chkModernHighDpi;
        internal const string ModernHighDpiFlagFile = "modern_highdpi.flag";

        private void initModernVFOControls()
        {
            grpModernVFO = new GroupBoxTS();
            grpModernVFO.Text = "Modern Look";
            grpModernVFO.Location = new Point(613, 8);
            grpModernVFO.Size = new Size(105, 92);
            grpModernVFO.Name = "grpModernVFO";

            chkModernVFO = new CheckBoxTS();
            chkModernVFO.Text = "VFO";
            chkModernVFO.Location = new Point(8, 16);
            chkModernVFO.Size = new Size(94, 17);
            chkModernVFO.Name = "chkModernVFO";
            chkModernVFO.CheckedChanged += chkModernVFO_CheckedChanged;
            toolTip1.SetToolTip(chkModernVFO,
                "Modern look for the VFO A / VFO B panels: smooth digits with smaller Hz, mode/filter and status pills " +
                "(MUTE, LOCK, SPLIT, RIT, XIT, APF), band position bar with CW/digital/phone segments (IARU R1), " +
                "skin-style panel background, colour by state (amber on receive (VFO B paler), transmitting red). " +
                "Tuning, hover digits, the mouse wheel and typing a frequency work as before. " +
                "Click the band bar to tune to that spot of the band; right click it (VFO A) for a band menu.");

            chkModernSMeter = new CheckBoxTS();
            chkModernSMeter.Text = "S-meter";
            chkModernSMeter.Location = new Point(8, 34);
            chkModernSMeter.Size = new Size(94, 17);
            chkModernSMeter.Name = "chkModernSMeter";
            chkModernSMeter.CheckedChanged += chkModernSMeter_CheckedChanged;
            toolTip1.SetToolTip(chkModernSMeter,
                "RX1 S-meter panel in the Modern VFO style: S units and dBm, bar with slow decay and peak mark; " +
                "forward power while transmitting. Move it with Shift+drag like the other panels.");

            chkModernUI = new CheckBoxTS();
            chkModernUI.Text = "Whole UI";
            chkModernUI.Location = new Point(8, 52);
            chkModernUI.Size = new Size(94, 17);
            chkModernUI.Name = "chkModernUI";
            chkModernUI.CheckedChanged += chkModernUI_CheckedChanged;
            toolTip1.SetToolTip(chkModernUI,
                "Modern look for the whole console: flat buttons with a short fade and one colour scheme " +
                "(amber = on, red = transmit / mute, green = power), thin sliders with a round thumb, " +
                "status bar dots and panadapter labels on a dark backing. The skin comes back when this is off.");

            chkModernHighDpi = new CheckBoxTS();
            chkModernHighDpi.Text = "High-DPI";
            chkModernHighDpi.Location = new Point(8, 70);
            chkModernHighDpi.Size = new Size(94, 17);
            chkModernHighDpi.Name = "chkModernHighDpi";
            chkModernHighDpi.CheckedChanged += chkModernHighDpi_CheckedChanged;
            toolTip1.SetToolTip(chkModernHighDpi,
                "Experimental, takes effect after a restart: tells Windows the program handles display scaling itself, " +
                "so text and lines stay sharp on 150-200 % screens instead of being stretched (blurry). " +
                "Panel positions and skin images are pixel based - if the layout looks wrong, turn this off again.");

            grpModernVFO.Controls.Add(chkModernVFO);
            grpModernVFO.Controls.Add(chkModernUI);
            grpModernVFO.Controls.Add(chkModernHighDpi);
            grpModernVFO.Controls.Add(chkModernSMeter);
            tpAppearanceGeneral.Controls.Add(grpModernVFO);

            // accent colour of the modern look; shown in place of the Skins group with the new layout
            grpModernAccent = new GroupBoxTS { Text = "Accent colour", Name = "grpModernAccent", Location = grpAppSkins.Location, Size = grpAppSkins.Size, Visible = false };
            clrModernAccent = new ColorButton { Name = "clrModernAccent", Location = new Point(10, 24), Size = new Size(80, 24), Color = ModernTheme.Accent, Automatic = "" };
            clrModernAccent.Changed += clrModernAccent_Changed;
            btnModernAccentDefault = new ButtonTS { Name = "btnModernAccentDefault", Text = "Default", Location = new Point(100, 24), Size = new Size(80, 24) };
            btnModernAccentDefault.Click += (s, e) => clrModernAccent.Color = ModernTheme.DefaultAccent;
            LabelTS info = new LabelTS { Text = "Buttons, frames, text and backgrounds take this hue (TX red, power green stay). After a restart.", Location = new Point(10, 54), Size = new Size(grpAppSkins.Width - 20, 44) };
            toolTip1.SetToolTip(clrModernAccent, "Leading colour of the modern look. Takes effect after SDR-VST3 is restarted.");
            grpModernAccent.Controls.Add(clrModernAccent);
            grpModernAccent.Controls.Add(btnModernAccentDefault);
            grpModernAccent.Controls.Add(info);
            tpAppearanceGeneral.Controls.Add(grpModernAccent);
        }

        private GroupBoxTS grpModernAccent;
        private ColorButton clrModernAccent;
        private ButtonTS btnModernAccentDefault;

        // the file is read in Main (ModernTheme.SetAccent) before the first window exists
        private void clrModernAccent_Changed(object sender, EventArgs e)
        {
            if (initializing) return;
            string file = console.AppDataPath + ModernTheme.AccentFile;
            Color was = ModernTheme.AccentFromFile(file);
            if (was.IsEmpty) was = ModernTheme.DefaultAccent;
            Color now = clrModernAccent.Color;
            if (now.ToArgb() == was.ToArgb()) return;
            try
            {
                if (now.ToArgb() == ModernTheme.DefaultAccent.ToArgb()) System.IO.File.Delete(file);
                else System.IO.File.WriteAllText(file, ColorTranslator.ToHtml(Color.FromArgb(255, now)));
            }
            catch (Exception ex) { Common.LogException(ex); }
            if (Visible)
                MessageBox.Show(this, "The accent colour takes effect after SDR-VST3 is restarted.", "Modern Look",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // the flag file is read in Main before the first window exists (the database is not loaded yet)
        private void chkModernHighDpi_CheckedChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            string flag = console.AppDataPath + ModernHighDpiFlagFile;
            bool had = System.IO.File.Exists(flag);
            try
            {
                if (chkModernHighDpi.Checked && !had) System.IO.File.WriteAllText(flag, "system-aware DPI, see Setup > Appearance > General");
                else if (!chkModernHighDpi.Checked && had) System.IO.File.Delete(flag);
            }
            catch (Exception ex) { Common.LogException(ex); }
            if (had != chkModernHighDpi.Checked && Visible)
                MessageBox.Show(this, "The high-DPI setting takes effect after SDR-VST3 is restarted.", "Modern Look",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // the new console layout (Console.NewGUI.cs) is drawn with the modern look only
        internal void ForceModernLook()
        {
            foreach (CheckBoxTS c in new[] { chkModernVFO, chkModernSMeter, chkModernUI })
            {
                c.Checked = true;
                c.Visible = false;
            }
            // Legacy Items "Expanded Console UI": the new layout places the panels itself, hiding VFO A/B,
            // POWER/RX2 or MON/TUN broke the top bar and the dock; only "hide legacy meters" stays
            foreach (CheckBoxTS c in groupBoxTS44.Controls.OfType<CheckBoxTS>().Where(c => c != chkHideLegacyMeters))
            {
                c.Checked = false;
                c.Visible = false;
            }
            LegacyItemController.HideBands = LegacyItemController.HideModes = LegacyItemController.HideFilters =
                LegacyItemController.HideVFOA = LegacyItemController.HideVFOB = LegacyItemController.HideVFOSync =
                LegacyItemController.HidePowerRx = LegacyItemController.HideMonTune = LegacyItemController.HideSplitRitVac =
                LegacyItemController.HideNoiseMnf = LegacyItemController.HideMicCompVox =
                LegacyItemController.ExpandSpectrumToRight = LegacyItemController.ExpandSpectrumToTop = false;
            // the modern painter draws the buttons; the general background colour has no effect at all
            lblAppearanceGenBtnSel.Visible = clrbtnBtnSel.Visible = lblGenBackground.Visible = clrbtnGenBackground.Visible = false;
            // skins are not used by the new layout: the accent colour instead
            grpAppSkins.Visible = false;
            grpModernAccent.Visible = true;
            grpModernAccent.BringToFront();
            // drawn by the modern look (VFO digits, status bar) or not reachable (skins, collapsed view)
            btnSkinExport.Visible = grpAppearanceVFO.Visible = groupBoxTS12.Visible = chkEnableLEDFont.Visible = chkConsoleDarkModeTitleBar.Visible = false;
            _modernHidden.Add(tpSkinServers);       // hidden, not removed: their settings keep saving
            _modernHidden.Add(tpAppearanceCollapsible);
            hideUnusedFeatures();
            rebuildModernTree();
            bool init = initializing;
            initializing = true;    // the saved option may differ from the colour in use (another database)
            clrModernAccent.Color = ModernTheme.Accent;
            initializing = init;
        }

        // features switched off in this database drop out of the tree and the search;
        // whoever has one enabled (e.g. an Aries ATU) keeps seeing its page
        private void hideUnusedFeatures()
        {
            bool apache = chkApolloFilter.Checked || chkApolloTuner.Checked || chkEnableAries.Checked ||
                          chkEnableGanymede.Checked || chkEnableAndromeda.Checked;
            if (!apache)
            {
                _modernHidden.Add(tpOtherHW);       // Apollo / Aries / Ganymede
                _modernHidden.Add(tpAndromeda);
            }
            if (!chkDSPEERon.Checked) _modernHidden.Add(tpDSPEER);
            if (!chkDiscordEnabled.Checked) _modernHidden.Add(tpDiscord);
            if (chkHideLegacyMeters.Checked)        // these only style/feed the legacy meters
            {
                _modernHidden.Add(tpAppearanceMeter);
                _modernHidden.Add(grpDisplayMultimeter);
                grpDisplayMultimeter.Visible = false;
            }
        }

        private void chkModernUI_CheckedChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            console.ModernUI = chkModernUI.Checked;
            try { ApplyModernUI(chkModernUI.Checked); }
            catch (Exception ex) { Common.LogException(ex); }   // the theme must never stop Setup from loading
        }

        private void chkModernSMeter_CheckedChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            console.ModernSMeter = chkModernSMeter.Checked;
        }

        private void chkModernVFO_CheckedChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            console.ModernVFO = chkModernVFO.Checked;
        }
    }
}
