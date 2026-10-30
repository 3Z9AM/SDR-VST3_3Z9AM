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
            var p = new Panel { Name = "ngDivMode", Size = new Size(150, 38), BackColor = Color.Transparent };
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
            var t = new Timer { Interval = 300 };
            t.Tick += (s, e) =>
            {
                if (!p.Visible || cb.DroppedDown) return;
                DiversityForm f = diversityForm;
                bool alive = f != null && !f.IsDisposed;
                cb.Enabled = alive;
                int m = alive ? f.SectionMode : 0;
                if (cb.SelectedIndex != m) cb.SelectedIndex = m;
            };
            t.Start();
            p.Controls.Add(l);
            p.Controls.Add(cb);
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
