//  Setup.AoA.cs
//
//  Setup > Antennas: the two diversity antennas (type, height, length, distance) and the angle of
//  arrival switches; read by DiversityForm (DiversityForm.Phase.cs).
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
    // Created before getOptions() so everything is saved/restored.
    public partial class Setup
    {
        private CheckBoxTS chkAoAEnable, chkAoAInvert, chkAoALog, chkIonoHud, chkAoAAuto;
        private TextBoxTS txtAoALocator;
        private NumericUpDownTS udAoALayerKm;
        private LabelTS lblAoAAutoStatus;
        private ComboBoxTS comboAoAMethod, comboAoARx1Type, comboAoARx2Type;
        private NumericUpDownTS udAoACalElev;
        private TextBoxTS txtAoABandCal;                    // every band's corrections, saved with the options
        private LabelTS lblAoABand;
        private bool _aoaBandLoaded, _aoaBandBusy;
        private Band _aoaBand;
        private NumericUpDownTS udAoARx1Height, udAoARx1Length, udAoARx2Height, udAoARx2Length, udAoADistance, udAoAPhaseCal, udAoARatioCal;

        private void initAoAControls()
        {
            var tp = new TabPage { Text = "Antennas", Name = "tpAntennas", UseVisualStyleBackColor = true };
            var grp = new GroupBoxTS { Text = "Angle of arrival (Diversity RX1 + RX2)", Name = "grpAoA", Location = new Point(8, 8), Size = new Size(560, 298) };
            tp.Controls.Add(grp);

            chkAoAEnable = new CheckBoxTS { Text = "Show the elevation of the arriving wave (Diversity radar + Modern S-meter)", Name = "chkAoAEnable", Location = new Point(12, 20), AutoSize = true };
            toolTip1.SetToolTip(chkAoAEnable, "Needs the Diversity window open (minimised is fine) and RX1 + RX2 running.\n" +
                "S-meter: average of the signals in the RX1 filter over 5 s; radar: instant value.");
            grp.Controls.Add(chkAoAEnable);

            grp.Controls.Add(new LabelTS { Text = "Method", Location = new Point(12, 48), AutoSize = true });
            comboAoAMethod = new ComboBoxTS { Name = "comboAoAMethod", DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 45), Size = new Size(250, 21) };
            comboAoAMethod.Items.AddRange(new object[] { "Auto (from the antenna types)", "Phase - antennas side by side", "Phase - one above the other", "Level ratio - wire + vertical" });
            comboAoAMethod.SelectedIndex = 0;
            toolTip1.SetToolTip(comboAoAMethod, "Auto: wire + vertical -> level ratio; two alike antennas -> phase (side by side when a distance is set, else one above the other).\n" +
                "Phase needs two alike antennas: the time the wave takes from one to the other.\n" +
                "Side by side: the line RX1 -> RX2 should point at the station.\n" +
                "Level ratio: a vertical hears low angles, a horizontal wire high ones - the dB difference through both patterns over average ground.");
            grp.Controls.Add(comboAoAMethod);

            comboAoARx1Type = antennaRow(grp, 1, 76, false, 22, 0, out udAoARx1Height, out udAoARx1Length);
            comboAoARx2Type = antennaRow(grp, 2, 104, true, 5, 20, out udAoARx2Height, out udAoARx2Length);

            udAoADistance = aoaNumber(grp, "Distance between the antennas (m, ground)", "udAoADistance", 136, 0, 1000, 0, 1,
                "Horizontal distance between the antennas, feed point to feed point. Used by phase, side by side.\n0 with Auto and alike antennas = one above the other (height difference).");
            udAoAPhaseCal = aoaNumber(grp, "Phase correction (deg)", "udAoAPhaseCal", 164, -180, 180, 0, 1,
                "Degrees taken off the phase RX2-RX1 (cables, preamps): set it so a station of known elevation reads right.");
            chkAoAInvert = new CheckBoxTS { Text = "Invert phase", Name = "chkAoAInvert", Location = new Point(390, 165), AutoSize = true };
            toolTip1.SetToolTip(chkAoAInvert, "Flip the sign of the phase RX2-RX1 (side comes out reversed / one-above-the-other gives no angles)");
            grp.Controls.Add(chkAoAInvert);
            // level: entered as the elevation of the station being heard; the dB it gives is only shown (and can be fine-tuned)
            var lblLevel = new LabelTS { Text = "Level: this station's elevation (deg)", Location = new Point(12, 195), AutoSize = true };
            grp.Controls.Add(lblLevel);
            udAoACalElev = new NumericUpDownTS { Name = "udAoACalElev", Location = new Point(300, 192), Size = new Size(60, 20), Minimum = 1, Maximum = 89, Value = 8 };
            toolTip1.SetToolTip(udAoACalElev, "Elevation of the station you are listening to (far DX ~5-10, NVIS ~70-80), then Calibrate");
            toolTip1.SetToolTip(lblLevel, "Level ratio (wire + vertical): tune a station of known elevation, type its angle, Calibrate.");
            grp.Controls.Add(udAoACalElev);
            var btnCal = new ButtonTS { Text = "Calibrate", Name = "btnAoACalibrate", Location = new Point(370, 190), Size = new Size(90, 23) };
            udAoARatioCal = new NumericUpDownTS { Name = "udAoARatioCal", Location = new Point(470, 192), Size = new Size(56, 20), Minimum = -60, Maximum = 60, DecimalPlaces = 1, Increment = 0.5m };
            toolTip1.SetToolTip(udAoARatioCal, "Level correction in dB found by Calibrate for the current band (antenna gains, cables) - normally leave it alone");
            grp.Controls.Add(udAoARatioCal);
            grp.Controls.Add(new LabelTS { Text = "dB", Location = new Point(528, 195), AutoSize = true });
            toolTip1.SetToolTip(btnCal, "Level ratio: tune a station whose elevation you know (far DX ~5-10 deg, near NVIS ~70-80 deg),\n" +
                "wait 5 s with it in the RX1 filter, click - the level correction is set from the current average.\n" +
                "Per band: the losses and patterns change with frequency.");
            btnCal.Click += delegate
            {
                aoaLoadBand();
                if (!DiversityForm.AoARatioCalFor((double)udAoACalElev.Value, console.VFOAFreq * 1e6, out double cal))
                {
                    MessageBox.Show(this, "No level reading yet: AoA on, Diversity window open, a wire + a vertical set, a station in the RX1 filter for 5 s.",
                        "Calibrate AoA", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                udAoARatioCal.Value = (decimal)Math.Round(Math.Max(-60, Math.Min(60, cal)), 1);
            };
            grp.Controls.Add(btnCal);
            lblAoABand = new LabelTS { Text = "Corrections are kept per band", Location = new Point(12, 222), AutoSize = true };
            toolTip1.SetToolTip(lblAoABand, "Phase and level corrections belong to the RX1 band shown here; each band keeps its own.");
            grp.Controls.Add(lblAoABand);

            chkAoALog = new CheckBoxTS { Text = "Log every 5 s to AoA_log_<UTC date>.csv (ionosphere)", Name = "chkAoALog", Location = new Point(12, 244), AutoSize = true };
            toolTip1.SetToolTip(chkAoALog, "UTC, frequency, method, phase RX2-RX1, coherence, level RX2-RX1, RX1 dBm, the angles -\n" +
                "for plotting over the day (layer height, sunrise/sunset, disturbances).");
            grp.Controls.Add(chkAoALog);

            toolTip1.SetToolTip(grp, "Antenna height: a wire = its height; a vertical = the height of its base. Length counts for a vertical only.\n" +
                       "Two different antennas (wire + vertical): only the level ratio works - their phase follows polarisation, not geometry.\n" +
                       "Several angles = the measurement fits each of them (antennas more than half a wavelength apart).");
            grp.Controls.Add(new LabelTS { Text = "All bands", Location = new Point(12, 271), AutoSize = true });
            txtAoABandCal = new TextBoxTS { Name = "txtAoABandCal", Location = new Point(80, 268), Size = new Size(460, 20), ReadOnly = true };
            toolTip1.SetToolTip(txtAoABandCal, "Band: level correction dB / phase correction deg");
            txtAoABandCal.TextChanged += delegate { if (!_aoaBandBusy) aoaParseBands(); };
            grp.Controls.Add(txtAoABandCal);
            tp.Enter += delegate { aoaLoadBand(); };
            tp.VisibleChanged += delegate { if (tp.Visible) aoaLoadBand(); };
            // full licence only: checked again each time the page shows (activation can change at any time)
            tp.VisibleChanged += delegate
            {
                if (!tp.Visible) return;
                grp.Enabled = DiversityForm.AoALicensed;
                grp.Text = "Angle of arrival (Diversity RX1 + RX2)" + (grp.Enabled ? "" : " - locked (full licence)");
            };

            // automatic level calibration from the CW skimmer's spots (AoAAutoCal.cs)
            var grpAuto = new GroupBoxTS { Text = "Auto calibration (level ratio, CW skimmer)", Name = "grpAoAAuto", Location = new Point(8, 310), Size = new Size(560, 70) };
            chkAoAAuto = new CheckBoxTS { Text = "Calibrate the level from the stations the CW skimmer decodes", Name = "chkAoAAuto", Location = new Point(12, 18), AutoSize = true };
            toolTip1.SetToolTip(chkAoAAuto, "Leave RX1 on a CW band with the CW skimmer and the Diversity window open.\n" +
                "Each decoded call is placed by its prefix (cty.dat, downloaded to the data folder), stations " + AoAAutoCal.MinKm + "-" + AoAAutoCal.MaxKm + " km away\n" +
                "give the elevation of one hop off the layer; the level correction of the band = median over at least " + AoAAutoCal.MinStations + " stations.");
            grpAuto.Controls.Add(chkAoAAuto);
            grpAuto.Controls.Add(new LabelTS { Text = "My locator", Location = new Point(400, 20), AutoSize = true });
            txtAoALocator = new TextBoxTS { Name = "txtAoALocator", Location = new Point(470, 17), Size = new Size(70, 20), MaxLength = 6, CharacterCasing = CharacterCasing.Upper };
            toolTip1.SetToolTip(txtAoALocator, "Your Maidenhead locator, 4 or 6 characters (e.g. JO82LK)");
            grpAuto.Controls.Add(txtAoALocator);
            grpAuto.Controls.Add(new LabelTS { Text = "Layer height km", Location = new Point(12, 46), AutoSize = true });
            udAoALayerKm = new NumericUpDownTS { Name = "udAoALayerKm", Location = new Point(110, 43), Size = new Size(60, 20), Minimum = 100, Maximum = 600, Increment = 10, Value = 300 };
            toolTip1.SetToolTip(udAoALayerKm, "Assumed reflection height (F2 ~250-350 km by day, higher at night). At 1000 km +-50 km is about +-4 deg.");
            grpAuto.Controls.Add(udAoALayerKm);
            lblAoAAutoStatus = new LabelTS { Text = "", Location = new Point(190, 46), AutoSize = true };
            grpAuto.Controls.Add(lblAoAAutoStatus);
            tp.Controls.Add(grpAuto);
            tp.VisibleChanged += delegate { if (tp.Visible) grpAuto.Enabled = DiversityForm.AoALicensed; };
            var autoTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            autoTimer.Tick += delegate
            {
                AoAAutoCal.Tick();
                if (tp.Visible) lblAoAAutoStatus.Text = chkAoAAuto.Checked && console != null ? AoAAutoCal.StatusFor(console.RX1Band) : "";
            };
            autoTimer.Start();
            // the corrections are kept through the text box (saved with the options): written on the UI thread
            AoAAutoCal.Changed += delegate
            {
                if (console == null || !console.IsHandleCreated) return;
                console.BeginInvoke(new Action(() => { aoaWriteBands(); if (_aoaBandLoaded) aoaLoadBand(); }));
            };
            foreach (Control c in grpAuto.Controls)
            {
                if (c is CheckBoxTS cb) cb.CheckedChanged += aoa_Changed;
                else if (c is TextBoxTS tb) tb.TextChanged += aoa_Changed;
                else if (c is NumericUpDownTS ud) ud.ValueChanged += aoa_Changed;
            }

            foreach (Control c in grp.Controls)
            {
                if (c is CheckBoxTS cb) cb.CheckedChanged += aoa_Changed;
                else if (c is ComboBoxTS co) co.SelectedIndexChanged += aoa_Changed;
                else if (c is NumericUpDownTS ud) ud.ValueChanged += aoa_Changed;
            }
            // not part of the licensed AoA group: the plate works from RX1 alone
            var grpHud = new GroupBoxTS { Text = "Ionosphere height", Name = "grpIonoHud", Location = new Point(8, 386), Size = new Size(560, 44) };
            chkIonoHud = new CheckBoxTS { Text = "HUD plate on the panadapter (IONO / FULL drift, Δh)", Name = "chkIonoHud", Location = new Point(12, 18), AutoSize = true, Checked = true };
            toolTip1.SetToolTip(chkIonoHud, "Layer speed from the Doppler of steady carriers (visible span / whole IQ band) and the metres moved since start.\n" +
                "With AoA on, IONO is corrected by the elevation of the RX1 filter signal (v / sin el, shown as ∠deg).");
            chkIonoHud.CheckedChanged += delegate { Display.IonoHudOn = chkIonoHud.Checked; };
            grpHud.Controls.Add(chkIonoHud);
            tp.Controls.Add(grpHud);
            tcSetup.TabPages.Add(tp);   // own top-level page "Antennas"
            aoa_Changed(null, EventArgs.Empty);
        }

        private ComboBoxTS antennaRow(GroupBoxTS grp, int rx, int y, bool vertical, decimal height, decimal length,
            out NumericUpDownTS udHeight, out NumericUpDownTS udLength)
        {
            grp.Controls.Add(new LabelTS { Text = "RX" + rx + " antenna", Location = new Point(12, y + 3), AutoSize = true });
            var type = new ComboBoxTS { Name = "comboAoARx" + rx + "Type", DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, y), Size = new Size(120, 21) };
            type.Items.AddRange(new object[] { "Horizontal wire", "Vertical" });
            type.SelectedIndex = vertical ? 1 : 0;
            toolTip1.SetToolTip(type, "RX" + rx + " antenna type (dipole / G5RV / loop in a horizontal plane = horizontal wire)");
            grp.Controls.Add(type);
            grp.Controls.Add(new LabelTS { Text = "height m", Location = new Point(240, y + 3), AutoSize = true });
            udHeight = new NumericUpDownTS { Name = "udAoARx" + rx + "Height", Location = new Point(300, y), Size = new Size(60, 20), Minimum = 0, Maximum = 300, DecimalPlaces = 1, Increment = 0.5m, Value = height };
            toolTip1.SetToolTip(udHeight, "Wire: its height above ground. Vertical: the height of its base.");
            grp.Controls.Add(udHeight);
            grp.Controls.Add(new LabelTS { Text = "length m", Location = new Point(372, y + 3), AutoSize = true });
            udLength = new NumericUpDownTS { Name = "udAoARx" + rx + "Length", Location = new Point(432, y), Size = new Size(60, 20), Minimum = 0, Maximum = 300, DecimalPlaces = 1, Increment = 0.5m, Value = length };
            toolTip1.SetToolTip(udLength, "Vertical: radiator length. Not used for a wire.");
            grp.Controls.Add(udLength);
            return type;
        }

        private NumericUpDownTS aoaNumber(GroupBoxTS grp, string label, string name, int y, int min, int max, decimal value, int decimals, string tip)
        {
            var lbl = new LabelTS { Text = label, Location = new Point(12, y + 3), AutoSize = true };
            var ud = new NumericUpDownTS { Name = name, Location = new Point(300, y), Size = new Size(60, 20), Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = 0.5m, Value = value };
            toolTip1.SetToolTip(ud, tip);
            toolTip1.SetToolTip(lbl, tip);
            grp.Controls.Add(lbl);
            grp.Controls.Add(ud);
            return ud;
        }

        // the current RX1 band's corrections into the boxes
        private void aoaLoadBand()
        {
            if (console == null) return;
            _aoaBand = console.RX1Band;
            double[] c = DiversityForm.AoABandCal.TryGetValue(_aoaBand, out double[] v) ? v : new double[2];
            _aoaBandBusy = true;
            udAoARatioCal.Value = (decimal)Math.Max(-60, Math.Min(60, c[0]));
            udAoAPhaseCal.Value = (decimal)Math.Max(-180, Math.Min(180, c[1]));
            _aoaBandBusy = false;
            _aoaBandLoaded = true;
            lblAoABand.Text = "Corrections above are for " + aoaBandName(_aoaBand) + " (each band kept separately)";
        }

        private static string aoaBandName(Band b) => b.ToString().TrimStart('B');

        // "80M -18.0/0.0; 40M -15.5/0.0"
        private void aoaWriteBands()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var parts = new System.Collections.Generic.List<string>();
            foreach (var kv in DiversityForm.AoABandCal)
                if (kv.Value[0] != 0 || kv.Value[1] != 0)
                    parts.Add(aoaBandName(kv.Key) + " " + kv.Value[0].ToString("0.0", ci) + "/" + kv.Value[1].ToString("0.0", ci));
            parts.Sort();
            _aoaBandBusy = true;
            txtAoABandCal.Text = string.Join("; ", parts);
            _aoaBandBusy = false;
        }

        private void aoaParseBands()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            DiversityForm.AoABandCal.Clear();
            foreach (string part in txtAoABandCal.Text.Split(';'))
            {
                string[] f = part.Trim().Split(' ', '/');
                if (f.Length == 3 && Enum.TryParse("B" + f[0], out Band b)
                    && double.TryParse(f[1], System.Globalization.NumberStyles.Float, ci, out double db)
                    && double.TryParse(f[2], System.Globalization.NumberStyles.Float, ci, out double deg))
                    DiversityForm.AoABandCal[b] = new[] { db, deg };
            }
            if (_aoaBandLoaded) aoaLoadBand();
        }

        // plain statics, safe while getOptions restores the page
        private void aoa_Changed(object sender, EventArgs e)
        {
            if (udAoARatioCal == null || chkAoALog == null || udAoALayerKm == null) return;     // still building the page
            DiversityForm.AoAMethod = Math.Max(0, comboAoAMethod.SelectedIndex);
            DiversityForm.AoARx1Vertical = comboAoARx1Type.SelectedIndex == 1;
            DiversityForm.AoARx2Vertical = comboAoARx2Type.SelectedIndex == 1;
            DiversityForm.AoARx1Height = (double)udAoARx1Height.Value;
            DiversityForm.AoARx1Length = (double)udAoARx1Length.Value;
            DiversityForm.AoARx2Height = (double)udAoARx2Height.Value;
            DiversityForm.AoARx2Length = (double)udAoARx2Length.Value;
            DiversityForm.AoADistance = (double)udAoADistance.Value;
            // the correction boxes edit the band loaded into them (none until the page was shown)
            if (_aoaBandLoaded && !_aoaBandBusy && (sender == udAoARatioCal || sender == udAoAPhaseCal))
            {
                DiversityForm.AoABandCal[_aoaBand] = new[] { (double)udAoARatioCal.Value, (double)udAoAPhaseCal.Value };
                aoaWriteBands();
            }
            DiversityForm.AoAInvert = chkAoAInvert.Checked;
            DiversityForm.AoALog = chkAoALog.Checked;
            DiversityForm.AoAOn = chkAoAEnable.Checked;
            AoAAutoCal.LayerKm = (double)udAoALayerKm.Value;
            AoAAutoCal.Locator = txtAoALocator.Text.Trim();
            if (console != null) AoAAutoCal.DataDir = console.AppDataPath;
            AoAAutoCal.On = chkAoAAuto.Checked;
        }
    }
}
