//  Console.PhasePick.cs - station pick by the RX2-RX1 phase: PICK + phase slider next to RX1 Pan
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
    // The phase of RX2 against RX1 is a station's signature (its direction / polarization). PICK keeps the cells
    // of the RX filter whose phase agrees with the slider and turns strong cells pointing elsewhere down
    // (wdsp secdiv.c + wdsprs pick.rs; offline tools/divtest pick). Needs the diversity mix of RX1 + RX2 and
    // the Diversity window open (it pushes the filter to WDSP); best with "fine" (12 Hz bins, as tested offline).
    public partial class Console
    {
        private const double PhasePickDepthDb = 15;          // divtest picktune: 15 best (20: +0.06 dB mean but worse worst case)
        private CheckBoxTS _pickBtn;
        private PrettyTrackBar _pickBar;
        private LabelTS _pickLbl;

        private void initPhasePick()
        {
            Control host = btnDisplayPanCenter.Parent;
            if (host == null || _pickBtn != null) return;
            _pickBtn = new CheckBoxTS { Name = "chkPhasePick", Text = "PICK", Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter,
                Font = ModernTheme.UiFont(7.5f, FontStyle.Bold), UseVisualStyleBackColor = false, TabStop = false, Size = new Size(46, btnDisplayPanCenter.Height) };
            _pickBtn.FlatAppearance.BorderSize = 0;
            ModernButtonPainter.Attach(_pickBtn);
            _pickBar = new PrettyTrackBar { Name = "ptbPhasePick", Orientation = Orientation.Horizontal, Minimum = -180, Maximum = 180, Value = 0, SmallChange = 5, LargeChange = 30,
                HeadImage = ptbDisplayPan.HeadImage, BackColor = ptbDisplayPan.BackColor, TabStop = false, Size = new Size(180, ptbDisplayPan.Height) };
            _pickLbl = new LabelTS { AutoSize = true, Font = lblDisplayPan.Font, ForeColor = lblDisplayPan.ForeColor, BackColor = Color.Transparent };
            string tip = "Station pick by phase: hear the station whose RX2-RX1 phase matches the slider,\n" +
                "strong signals with another phase go down " + PhasePickDepthDb + " dB (each one followed as its phase wanders).\n" +
                "Turn the slider by ear until the wanted station stays and the other one fades.\n" +
                "Needs Diversity enabled with RX1 + RX2 mixed and the Diversity window open; best with 'fine'.\n" +
                "'steady' / 'random': how steady the phase in the filter is; a station with a random phase\n" +
                "(a rainbow in the phase views) cannot be picked, PICK then steps aside until it steadies.";
            toolTip1.SetToolTip(_pickBtn, tip);
            toolTip1.SetToolTip(_pickBar, tip);
            host.Controls.Add(_pickBtn); host.Controls.Add(_pickBar); host.Controls.Add(_pickLbl);
            _pickBtn.BringToFront(); _pickBar.BringToFront(); _pickLbl.BringToFront();

            // the colours of the phase views (Phase WF, 3D Phase view, WF phase): a station shown in a colour is picked at that colour
            _pickBar.ModernTrackColour = v => phasePickColour(v);
            _pickBtn.CheckedChanged += (s, e) => pushPhasePick();
            _pickBar.Scroll += (s, e) => pushPhasePick();
            EventHandler place = (s, e) => placePhasePick();
            foreach (Control c in new Control[] { btnDisplayPanCenter, lblDisplayZoom, lblRX2DisplayPan })
            {
                c.LocationChanged += place;
                c.VisibleChanged += place;
            }
            var t = new Timer { Interval = 200 };
            t.Tick += (s, e) => showPhasePick();
            t.Start();
            placePhasePick();
            pushPhasePick();
        }

        // right of RX1 Pan's CENTER; hidden when the next control on the row leaves no room
        private void placePhasePick()
        {
            if (_pickBtn == null) return;
            int x = btnDisplayPanCenter.Right + 12, y = btnDisplayPanCenter.Top;
            int end = lblRX2DisplayPan.Visible ? lblRX2DisplayPan.Left : lblDisplayZoom.Left;
            bool room = btnDisplayPanCenter.Visible && end - x >= _pickBtn.Width + 4 + _pickBar.Width + 4 + 40;
            _pickBtn.Visible = _pickBar.Visible = _pickLbl.Visible = room;
            if (!room) return;
            _pickBtn.Location = new Point(x, y);
            _pickBar.Location = new Point(_pickBtn.Right + 4, ptbDisplayPan.Top);
            _pickLbl.Location = new Point(_pickBar.Right + 4, _pickBtn.Top + (_pickBtn.Height - _pickLbl.Height) / 2);
            showPhasePick();
        }

        // slider = arg z1 conj(z2) in the panadapter frame; the phase views colour arg z2 conj(z1) negated (FillPhaseColours),
        // i.e. the same angle, with their RX1-RX2 switch
        private static Color phasePickColour(double deg)
        {
            int rgb = DiversityPhaseWaterfall.Hsv((Display.PhaseViewRx1Lead ? -1 : 1) * deg * Math.PI / 180, 1);
            return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }

        private void pushPhasePick()
        {
            WDSP.SetEXTDIVPick(_pickBtn.Checked ? 1 : 0, _pickBar.Value, PhasePickDepthDb);
            showPhasePick();
        }

        private void showPhasePick()
        {
            if (_pickLbl == null || !_pickLbl.Visible) return;
            DiversityForm f = diversityForm;
            bool alive = f != null && !f.IsDisposed && Diversity2 && ActivationCode.Has(ActivationFeature.SectionDiversity);
            // the slider follows the station's phase as it wanders (not while held: dragging sets a new one)
            if (_pickBtn.Checked && alive && !_pickBar.Capture)
            {
                int v = (int)Math.Round(WDSP.GetEXTDIVPickPhase());
                if (v > 180) v -= 360; else if (v < -180) v += 360;
                if (v != _pickBar.Value) _pickBar.Value = v;
            }
            // is there a phase to pick? steady: the mask acts; random ("rainbow" in the phase views): it stepped aside
            string state = "";
            bool off = false;
            if (_pickBtn.Checked && !alive) state = " (Diversity off)";
            else if (_pickBtn.Checked)
            {
                double st = WDSP.GetEXTDIVPickSteady(out int act);
                off = act == 0;
                state = (off ? "  random " : "  steady ") + st.ToString("0.00") + (off ? " - off" : "");
            }
            string text = _pickBar.Value + "°" + state;
            if (_pickLbl.Text != text) _pickLbl.Text = text;
            Color c = off ? ModernTheme.TextDim : phasePickColour(_pickBar.Value);
            if (_pickLbl.ForeColor != c) { _pickLbl.ForeColor = c; _pickBar.Invalidate(); }   // RX1-RX2 switch flips the strip too
            _pickBtn.Enabled = _pickBar.Enabled = ActivationCode.Has(ActivationFeature.SectionDiversity);
        }
    }
}
