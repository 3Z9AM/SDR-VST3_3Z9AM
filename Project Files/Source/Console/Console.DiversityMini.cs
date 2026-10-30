//  Console.DiversityMini.cs
//
//  Diversity mini radar and the diversity controls in the DSP drawer
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
    // Small diversity panel on the Console: only the gain/phase radar circle of the
    // Diversity window. Drawing and mouse handling are forwarded to DiversityForm, so
    // R/angle locks, memories and the gain/phase maths stay identical. The panel is a
    // normal movable Console panel (Shift+drag, Shift+wheel opacity, separate
    // Expand/Collapse positions) and is shown only while the Diversity window is open.
    public partial class Console
    {
        private DiversityMiniPanel panelDiversityMini;
        private DiversityForm _diversityMiniForm;

        // DSP drawer (new GUI, above the filter shape): per-section diversity mode without bringing up the Diversity window.
        // Mirrors DiversityForm.SectionMode; greyed while the window is closed (no diversity mix then).
        private Control createDiversityModePanel()
        {
            // label + list in the style of the AF/RF labels and sliders above; the width follows the panel
            var p = new Panel { Name = "ngDivMode", Size = new Size(150, 60), BackColor = Color.Transparent };
            var l = new LabelTS { Text = "Diversity:", AutoSize = true, Location = new Point(1, 0), Font = lblRX2AF.Font, ForeColor = lblRX2AF.ForeColor, BackColor = Color.Transparent };
            var cb = new ComboBoxTS { Name = "ngDivModeList", DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(0, 16), Size = new Size(150, 21),
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
            cb.Items.AddRange(DiversityForm.SectionModeItems);
            cb.SelectedIndex = 0;
            _modernConsoleCombos.ApplyCombo(cb);
            cb.SelectionChangeCommitted += (s, e) =>
            {
                DiversityForm f = diversityForm;
                if (f != null && !f.IsDisposed) f.SectionMode = cb.SelectedIndex;
            };
            // max-SINR clean share (DiversityForm.SectionClean): 0 = default weights, 100 = station direction only from its own cells
            var cl = new LabelTS { Text = "Clean: 0%", AutoSize = true, Location = new Point(1, 40), Font = lblRX2AF.Font, ForeColor = lblRX2AF.ForeColor, BackColor = Color.Transparent };
            var tb = new PrettyTrackBar { Name = "ngDivClean", Orientation = Orientation.Horizontal, Minimum = 0, Maximum = 100, Value = 0, SmallChange = 5, LargeChange = 25,
                HeadImage = ptbRX2AF.HeadImage, BackColor = Color.Transparent, Location = new Point(72, 38), Size = new Size(150 - 72 - 52, ptbRX2AF.Height),
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
            toolTip1.SetToolTip(tb, "Sections max-SINR: share of the clean weight (station direction learned only where the station is).\n" +
                "0% = default (more gain, part of it NR-like gating of the noise), 100% = pure diversity, station level kept.\n" +
                "With AUTO: how readily a bin counts as station (middle = default; right = clean sooner, station better kept;\n" +
                "left = clean only on a clear station, more background taken down); the label shows the clean share AUTO gives now");
            // auto: clean per bin where the station has been lately, the default weights where only noise is
            // a button like NR/ANF in the drawer - the modern look does not draw plain check boxes
            var au = new CheckBoxTS { Name = "ngDivCleanAuto", Text = "AUTO", Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter,
                Font = ModernTheme.UiFont(7.5f, FontStyle.Bold), UseVisualStyleBackColor = false, TabStop = false, Location = new Point(150 - 46, 38), Size = new Size(46, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Right };
            au.FlatAppearance.BorderSize = 0;
            ModernButtonPainter.Attach(au);
            toolTip1.SetToolTip(au, "Clean per frequency bin: clean where the station has been lately (its level kept),\n" +
                "the default weights where only noise is (they lower the background)");
            // the slider: manual share, or with AUTO its sensitivity; the label: the share, or with AUTO what it gives now
            Action<DiversityForm> show = f =>
            {
                double sh = f != null ? f.SectionCleanShare : -1.0;
                cl.Text = !au.Checked ? "Clean: " + tb.Value + "%" : sh >= 0.0 ? "Auto: " + (int)Math.Round(100.0 * sh) + "%" : "Auto";
            };
            tb.Scroll += (s, e) =>
            {
                DiversityForm f = diversityForm;
                if (f == null || f.IsDisposed) return;
                if (au.Checked) f.SectionCleanSens = tb.Value; else f.SectionClean = tb.Value;
                show(f);
            };
            au.Click += (s, e) =>
            {
                DiversityForm f = diversityForm;
                if (f == null || f.IsDisposed) return;
                f.SectionClean = au.Checked ? -1 : f.SectionCleanPct;
                tb.Value = au.Checked ? f.SectionCleanSens : f.SectionCleanPct;
                show(f);
            };
            var t = new Timer { Interval = 300 };
            t.Tick += (s, e) =>
            {
                if (!p.Visible || cb.DroppedDown) return;
                DiversityForm f = diversityForm;
                bool alive = f != null && !f.IsDisposed && ActivationCode.Has(ActivationFeature.SectionDiversity);   // extra for chosen testers
                cb.Enabled = alive;
                int m = alive ? f.SectionMode : 0;
                if (cb.SelectedIndex != m) cb.SelectedIndex = m;
                au.Enabled = tb.Enabled = alive && m == 4;
                if (alive && !tb.Capture)
                {
                    au.Checked = f.SectionClean < 0;
                    tb.Value = au.Checked ? f.SectionCleanSens : f.SectionCleanPct;
                    show(f);
                }
            };
            t.Start();
            p.Controls.Add(l);
            p.Controls.Add(cb);
            p.Controls.Add(cl);
            p.Controls.Add(tb);
            p.Controls.Add(au);
            return p;
        }

        private sealed class DiversityMiniPanel : PanelTS
        {
            private const int Margin_ = 3;
            public DiversityForm Form;

            public DiversityMiniPanel()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            private int RadarSize => Math.Max(0, Math.Min(ClientSize.Width, ClientSize.Height) - 2 * Margin_);
            private Point RadarOrigin => new Point((ClientSize.Width - RadarSize) / 2, (ClientSize.Height - RadarSize) / 2);

            private bool FormAlive => Form != null && !Form.IsDisposed;

            // the panel is only the circle: clip the window to it, no rectangular background
            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                updateRegion();
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                updateRegion();
            }

            private void updateRegion()
            {
                int size = RadarSize;
                if (size < 8) return;
                Point o = RadarOrigin;
                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    path.AddEllipse(o.X - 1, o.Y - 1, size + 2, size + 2);
                    Region old = Region;
                    Region = new Region(path);
                    old?.Dispose();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (!FormAlive) return;
                Point o = RadarOrigin;
                var state = e.Graphics.Save();
                e.Graphics.TranslateTransform(o.X, o.Y);
                Form.PaintRadarMini(e.Graphics, RadarSize);
                e.Graphics.Restore(state);
            }

            private void forward(int kind, MouseEventArgs e)
            {
                if (!FormAlive) return;
                Point o = RadarOrigin;
                Form.RadarMiniMouse(kind,
                    new MouseEventArgs(e.Button, e.Clicks, e.X - o.X, e.Y - o.Y, e.Delta), RadarSize);
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); forward(0, e); }
            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); forward(1, e); }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); forward(2, e); }
        }

        private void initDiversityMini()
        {
            panelDiversityMini = new DiversityMiniPanel();
            panelDiversityMini.Name = "panelDiversityMini";
            // same size as the MOX/TUN panel
            panelDiversityMini.Size = panelOptions.Size;
            panelDiversityMini.Location = new Point(panelOptions.Right + 4, panelOptions.Top);
            panelDiversityMini.Visible = false;
            Controls.Add(panelDiversityMini);
            panelDiversityMini.BringToFront();
        }

        private void attachDiversityMini(DiversityForm form)
        {
            if (panelDiversityMini == null || form == null) return;

            _diversityMiniForm = form;
            panelDiversityMini.Form = form;

            form.RadarChanged += (s, e) => invalidateDiversityMini();
            form.VisibleChanged += (s, e) => updateDiversityMiniVisibility();
            form.FormClosed += (s, e) => updateDiversityMiniVisibility();
            form.Disposed += (s, e) => updateDiversityMiniVisibility();

            updateDiversityMiniVisibility();
        }

        private void invalidateDiversityMini()
        {
            if (panelDiversityMini == null || panelDiversityMini.IsDisposed || !panelDiversityMini.Visible) return;
            if (panelDiversityMini.InvokeRequired)
                panelDiversityMini.BeginInvoke(new MethodInvoker(() => panelDiversityMini.Invalidate()));
            else
                panelDiversityMini.Invalidate();
        }

        private void updateDiversityMiniVisibility()
        {
            if (panelDiversityMini == null || panelDiversityMini.IsDisposed) return;
            if (panelDiversityMini.InvokeRequired)
            {
                panelDiversityMini.BeginInvoke(new MethodInvoker(updateDiversityMiniVisibility));
                return;
            }

            DiversityForm f = _diversityMiniForm;
            bool show = f != null && !f.IsDisposed && !f.Disposing && f.Visible;
            if (panelDiversityMini.Visible != show)
            {
                panelDiversityMini.Visible = show;
                if (show) { panelDiversityMini.BringToFront(); MeterManager.BringToFront(); }
            }
            if (show) panelDiversityMini.Invalidate();
        }
    }
}
