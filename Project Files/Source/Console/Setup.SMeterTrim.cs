//  Setup.SMeterTrim.cs
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
    // Manual S-meter correction (+-15 dB) on top of the level calibration, Setup > General > Calibration.
    // Created before getOptions() so the generic control walk saves/restores the values.
    public partial class Setup
    {
        private GroupBoxTS grpSMeterTrim;
        private NumericUpDownTS udRX1MeterTrim, udRX2MeterTrim;

        private void initSMeterTrimControls()
        {
            grpSMeterTrim = new GroupBoxTS { Text = "S-Meter Correction", Name = "grpSMeterTrim", Location = new Point(360, 8), Size = new Size(168, 80) };
            udRX1MeterTrim = trimUpDown("udRX1MeterTrim", 22);
            udRX2MeterTrim = trimUpDown("udRX2MeterTrim", 48);
            udRX1MeterTrim.ValueChanged += (s, e) => console.RX1MeterTrim = (float)udRX1MeterTrim.Value;
            udRX2MeterTrim.ValueChanged += (s, e) => console.RX2MeterTrim = (float)udRX2MeterTrim.Value;
            string tip = "Manual S-meter correction in dB, added to the level calibration. Meters only (S-meter, Meters/Gadgets, squelch level) - the panadapter is not moved.";
            toolTip1.SetToolTip(udRX1MeterTrim, tip);
            toolTip1.SetToolTip(udRX2MeterTrim, tip);
            grpSMeterTrim.Controls.AddRange(new Control[] {
                endLabel("RX1 (dB):", "lblRX1MeterTrim", 8, 24, 70, ContentAlignment.TopLeft), udRX1MeterTrim,
                endLabel("RX2 (dB):", "lblRX2MeterTrim", 8, 50, 70, ContentAlignment.TopLeft), udRX2MeterTrim });
            tpGeneralCalibration.Controls.Add(grpSMeterTrim);
        }

        private static NumericUpDownTS trimUpDown(string name, int y)
        {
            return new NumericUpDownTS
            {
                Name = name, Location = new Point(90, y), Size = new Size(60, 20),
                Minimum = -15, Maximum = 15, Increment = 0.5m, DecimalPlaces = 1, Value = 0, TextAlign = HorizontalAlignment.Right
            };
        }
    }
}
