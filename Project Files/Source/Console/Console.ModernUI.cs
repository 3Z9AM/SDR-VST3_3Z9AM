//  Console.ModernUI.cs
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
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Thetis
{
    // "Modern UI" switch (Setup > Appearance > General): modern buttons and sliders over the
    // whole console (ModernTheme.cs, PrettyTrackBar), see also the status bar and panadapter
    // parts. Only paint layers - the skin keeps loading its images and comes back as it was
    // when the option is turned off.
    public partial class Console
    {
        private bool _modernUiHooked;

        public bool ModernUI
        {
            get { return ModernTheme.Enabled; }
            set
            {
                if (value == ModernTheme.Enabled) return;
                if (value && !_modernUiHooked)
                {
                    _modernUiHooked = true;
                    ModernButtonPainter.OnColourFor = modernOnColour;
                    ModernButtonPainter.CaptionFor = modernCaption;
                    ModernButtonPainter.RedMeansError = b => b == chkPower || b.Tag == chkPower;
                    ModernButtonPainter.AttachTree(this);
                    foreach (PrettyTrackBar t in modernTrackBars(this))
                        t.ModernCenterFill = t.Name.Contains("Pan") || (t.Minimum < 0 && t.Maximum > 0);
                }
                ModernTheme.SetEnabled(value);
                // a paint layer must never stop the radio from starting: log and go on
                modernStep(ModernButtonPainter.RefreshAll);
                modernStep(() => applyModernStatusBar(value));
                modernStep(() => applyModernFonts(value));
                modernStep(() => applyModernPanelShadows(value));
                modernStep(() =>
                {
                    // every console combo box (display mode, AGC, filters...) dark with a HUD drop-down list
                    if (value) foreach (ComboBox cb in modernCombos(this)) _modernConsoleCombos.ApplyCombo(cb);
                    else _modernConsoleCombos.Restore();
                });
                modernStep(() => applyModernForms(value));
                Invalidate(true);
            }
        }

        // the other windows (Memory, XVTR, CWX, PureSignal, DB Manager...): dark colours when shown.
        // Only forms with a light system background - the ones with their own dark design keep it.
        private readonly Dictionary<Form, ModernColours> _modernForms = new Dictionary<Form, ModernColours>();
        private readonly ModernColours _modernConsoleCombos = new ModernColours();

        private void applyModernForms(bool on)
        {
            Application.Idle -= modernFormsIdle;
            if (on)
            {
                Application.Idle += modernFormsIdle;
                return;
            }
            foreach (KeyValuePair<Form, ModernColours> kv in _modernForms)
                if (!kv.Key.IsDisposed) modernStep(kv.Value.Restore);
            _modernForms.Clear();
        }

        private void modernFormsIdle(object sender, EventArgs e)
        {
            foreach (Form f in Application.OpenForms)
            {
                if (f == this || f is Setup || !f.Visible || _modernForms.ContainsKey(f)) continue;
                if (!ModernColours.IsDefaultBack(f.BackColor) || f.TransparencyKey != Color.Empty) continue;
                ModernColours mc = new ModernColours();
                _modernForms[f] = mc;
                f.FormClosed += (s, a) => _modernForms.Remove(f);
                // again when shown later: controls filled in at run time
                f.VisibleChanged += (s, a) => { if (f.Visible && ModernTheme.Enabled) modernStep(() => mc.Apply(f)); };
                modernStep(() => mc.Apply(f));
                modernStep(() => ngDarkTitleBar(f));
                f.Invalidate(true);
            }
        }

        private static void modernStep(Action a)
        {
            try { a(); }
            catch (Exception ex) { Common.LogException(ex); }
        }

        // red for what transmits or silences, green for power, amber for the rest
        private Color modernOnColour(ButtonBase b)
        {
            if (b.Tag is ButtonBase original && original != b) return modernOnColour(original);   // dock copies (Console.NewGUI.cs)
            if (b == chkMOX || b == chkTUN || b == chk2TONE || b == chkMUT || b == chkRX2Mute)
                return ModernTheme.Danger;
            if (b == chkPower) return ModernTheme.Ok;
            return ModernTheme.Accent;
        }

        // the skin bitmaps carry these captions; the designer text is empty
        private string modernCaption(ButtonBase b)
        {
            if (!string.IsNullOrEmpty(b.Text)) return b.Text;
            if (b == chkPower) return "POWER";
            if (b == chkRX2) return "RX2";
            if (b == ckQuickRec) return "● REC";
            if (b == ckQuickPlay) return "▶ PLAY";
            if (b == radBand2) return "2m";
            if (b == radRX2ModeSPEC) return "SPEC";
            if (b == ucQuickRecallPad.PreviousButton) return "◀";
            if (b == ucQuickRecallPad.ListButton) return "▾";
            if (b == ucQuickRecallPad.NextButton) return "▶";
            return null;
        }

        // ---- status bar: dark strip, state dots for radio / VAC, badges on the service icons ----

        private ToolStripRenderer _skinStatusRenderer;
        private ToolStripRenderMode _skinStatusRenderMode;
        private ToolStripStatusLabel _modernRadioLabel, _modernVac1Label, _modernVac2Label, _modernMicLabel;
        private Timer _modernStatusTimer;
        private readonly Dictionary<int, Bitmap> _modernDots = new Dictionary<int, Bitmap>();

        private void applyModernStatusBar(bool on)
        {
            if (on)
            {
                if (_modernRadioLabel == null)
                {
                    _modernRadioLabel = modernStatusLabel("RADIO", "Radio: green = running");
                    _modernVac1Label = modernStatusLabel("VAC1", "VAC1: grey = off, green = running, yellow = recent under/overflow");
                    _modernVac2Label = modernStatusLabel("VAC2", "VAC2: grey = off, green = running, yellow = recent under/overflow");
                    _modernMicLabel = modernStatusLabel("MIC", "TX audio source: MIC = radio mic, VAC1/VAC2, TCI, MODEM (WSJT-X/fldigi),\n" +
                        "SONOBUS = the SonoBus window; /VST = through the TX VST chain too");
                    int at = statusStripMain.Items.IndexOf(toolStripStatusLabel_N1MM);
                    if (at < 0) at = statusStripMain.Items.Count;
                    statusStripMain.Items.Insert(at, _modernMicLabel);
                    statusStripMain.Items.Insert(at, _modernVac2Label);
                    statusStripMain.Items.Insert(at, _modernVac1Label);
                    statusStripMain.Items.Insert(at, _modernRadioLabel);
                    _modernStatusTimer = new Timer { Interval = 500 };
                    _modernStatusTimer.Tick += (s, e) => updateModernStatus();
                }
                _skinStatusRenderMode = statusStripMain.RenderMode;
                _skinStatusRenderer = statusStripMain.Renderer;
                statusStripMain.Renderer = new ModernStatusRenderer(this);
                _modernRadioLabel.Visible = _modernVac1Label.Visible = _modernVac2Label.Visible = _modernMicLabel.Visible = true;
                updateModernStatus();
                _modernStatusTimer.Start();
            }
            else if (_modernRadioLabel != null)
            {
                _modernStatusTimer.Stop();
                _modernRadioLabel.Visible = _modernVac1Label.Visible = _modernVac2Label.Visible = _modernMicLabel.Visible = false;
                if (_skinStatusRenderMode == ToolStripRenderMode.Custom) statusStripMain.Renderer = _skinStatusRenderer;
                else statusStripMain.RenderMode = _skinStatusRenderMode;
            }
            statusStripMain.Invalidate();
        }

        private ToolStripStatusLabel modernStatusLabel(string text, string tip)
        {
            return new ToolStripStatusLabel
            {
                Text = text,
                ToolTipText = tip,
                ForeColor = ModernTheme.TextDim,
                ImageScaling = ToolStripItemImageScaling.None,
                Margin = new Padding(6, 0, 2, 0),
                Visible = false,
            };
        }

        private Bitmap modernDot(Color c)
        {
            if (_modernDots.TryGetValue(c.ToArgb(), out Bitmap b)) return b;
            b = new Bitmap(10, 10);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (SolidBrush glow = new SolidBrush(Color.FromArgb(60, c))) g.FillEllipse(glow, 0, 0, 10, 10);
                using (SolidBrush fill = new SolidBrush(c)) g.FillEllipse(fill, 2, 2, 6, 6);
            }
            _modernDots[c.ToArgb()] = b;
            return b;
        }

        private void setModernStatus(ToolStripStatusLabel l, Color dot, bool active)
        {
            Bitmap img = modernDot(dot);
            if (l.Image != img) l.Image = img;
            Color fc = active ? ModernTheme.Text : ModernTheme.TextDim;
            if (l.ForeColor != fc) l.ForeColor = fc;
        }

        private void updateModernStatus()
        {
            if (!ModernTheme.Enabled || _modernRadioLabel == null || IsDisposed) return;
            if (Display.ModernPanelShadowRects == null) modernStep(updateModernPanelShadows);   // first time after the window exists
            setModernStatus(_modernRadioLabel, PowerOn ? ModernTheme.Ok : ModernTheme.TextDisabled, PowerOn);
            updateModernMicStatus();
            setModernStatus(_modernVac1Label, !VACEnabled ? ModernTheme.TextDisabled : ucVAC1UnderOver.ShowingIssue ? ModernTheme.Warn : ModernTheme.Ok, VACEnabled);
            setModernStatus(_modernVac2Label, !VAC2Enabled ? ModernTheme.TextDisabled : ucVAC2UnderOver.ShowingIssue ? ModernTheme.Warn : ModernTheme.Ok, VAC2Enabled);
        }

        // where the transmitter takes its audio from (cmaster.c GetTXAudioSource); SonoBus replaces the mic in the
        // TX VST callback, so it is the source whenever that callback runs with the SonoBus window on
        private void updateModernMicStatus()
        {
            int s = PowerOn ? cmaster.GetTXAudioSource(0) : 0;
            bool vst = (s & 16) != 0;
            string src = vst && VstHost.RemoteOn ? "SONOBUS" : new[] { "MIC", "VAC1", "VAC2", "TCI", "MODEM" }[Math.Min(s & 15, 4)];
            // shortcut: TXVST on = the chain processes (an empty chain also shows /VST)
            string text = "MIC: " + src + (vst && chkTXVST.Visible && chkTXVST.Checked ? "/VST" : "");
            if (_modernMicLabel.Text != text) _modernMicLabel.Text = text;
            setModernStatus(_modernMicLabel, !PowerOn ? ModernTheme.TextDisabled : src == "SONOBUS" ? ModernTheme.Ok : ModernTheme.Accent, PowerOn);
        }

        // badge colour for the service icons (they are only visible while the service runs)
        private Color? modernStatusBadge(ToolStripItem item)
        {
            if (item == toolStripStatusLabel_CMstatus) return cmaster.GetCMAstate() == 1 ? ModernTheme.Ok : ModernTheme.Danger;
            if (item == toolStripStatusLabel_N1MM || item == toolStripStatusLabel_TCI || item == toolStripStatusLabel_CatTCPip)
                return ModernTheme.Ok;
            return null;
        }

        // Segoe Fluent Icons / MDL2 glyphs for the left status group (screen, CPU, volts, antennas)
        private string modernStatusGlyph(ToolStripItem item)
        {
            if (item == toolStripDropDownButton_ScreenSize) return "";   // TV monitor
            if (item == toolStripDropDownButton_CPU || item == toolStripStatusLabel_GPU) return "";          // component (chip)
            if (item == toolStripStatusLabel_Volts) return "";           // lightning bolt
            if (item == toolStripStatusLabelRXAnt || item == toolStripStatusLabelTXAnt) return "";   // network tower
            return null;
        }

        private sealed class ModernStatusRenderer : ToolStripProfessionalRenderer
        {
            private readonly Console _c;
            public ModernStatusRenderer(Console c) { _c = c; RoundedEdges = false; }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                Rectangle r = new Rectangle(Point.Empty, e.ToolStrip.Size);
                using (System.Drawing.Drawing2D.LinearGradientBrush b = new System.Drawing.Drawing2D.LinearGradientBrush(
                    r, Color.FromArgb(18, 18, 20), Color.FromArgb(8, 8, 9), 90f))
                    e.Graphics.FillRectangle(b, r);
                using (Pen top = new Pen(ModernTheme.Border))
                    e.Graphics.DrawLine(top, 0, 0, r.Width, 0);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

            protected override void OnRenderStatusStripSizingGrip(ToolStripRenderEventArgs e) { }

            protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e) => hover(e);
            protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e) => hover(e);

            private static void hover(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected && !e.Item.Pressed) return;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (System.Drawing.Drawing2D.GraphicsPath p = ModernTheme.RoundedRect(new RectangleF(1, 2, e.Item.Width - 2, e.Item.Height - 4), 4))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(e.Item.Pressed ? 60 : 36, 255, 255, 255)))
                    e.Graphics.FillPath(b, p);
            }

            // text in the status bar colour (Setup > Appearance "Status Bar Text") in the HUD text colour;
            // the darker blink of the timer dim, own warning colours kept
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                Color status = _c.toolStripDropDownButton_ScreenSize.ForeColor;
                if (e.Item == _c.toolStripStatusLabel_GPU) e.TextColor = ModernTheme.Text;     // own fixed colour, not the status one
                else if (e.TextColor.ToArgb() == status.ToArgb()) e.TextColor = ModernTheme.Text;
                else if (e.TextColor.ToArgb() == ControlPaint.Dark(status).ToArgb()) e.TextColor = ModernTheme.TextDim;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
            {
                string glyph = _c.modernStatusGlyph(e.Item);
                if (glyph != null && e.ImageRectangle.Width >= 8)
                {
                    // icon font glyph in the accent colour instead of the skin's bitmap
                    TextRenderer.DrawText(e.Graphics, glyph, ModernTheme.IconFont(e.ImageRectangle.Height * 0.95f), Rectangle.Inflate(e.ImageRectangle, 2, 2),
                        e.Item.Enabled ? ModernTheme.Accent : ModernTheme.TextDisabled,
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                else base.OnRenderItemImage(e);
                Color? badge = _c.modernStatusBadge(e.Item);
                if (badge == null || e.ImageRectangle.Width < 8) return;
                Rectangle ir = e.ImageRectangle;
                float d = 7f, x = ir.Right - d + 1f, y = ir.Top - 1f;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (SolidBrush ring = new SolidBrush(Color.FromArgb(12, 12, 13))) e.Graphics.FillEllipse(ring, x - 1.5f, y - 1.5f, d + 3f, d + 3f);
                using (SolidBrush b = new SolidBrush(badge.Value)) e.Graphics.FillEllipse(b, x, y, d, d);
            }
        }

        // ---- shadows under the panels that float over the display (drawn by Display) ----

        private bool _modernShadowHooks;

        private void applyModernPanelShadows(bool on)
        {
            if (on && !_modernShadowHooks)
            {
                _modernShadowHooks = true;
                foreach (Control c in Controls)
                {
                    if (!IsNativeMovableControl(c) && c != panelModernSMeter) continue;
                    c.LocationChanged += (s, e) => updateModernPanelShadows();
                    c.SizeChanged += (s, e) => updateModernPanelShadows();
                    c.VisibleChanged += (s, e) => updateModernPanelShadows();
                }
                pnlDisplay.SizeChanged += (s, e) => updateModernPanelShadows();
            }
            updateModernPanelShadows();
        }

        private void updateModernPanelShadows()
        {
            Display.ModernPanelShadowRects = null;
            if (!ModernTheme.Enabled || pnlDisplay == null || !IsHandleCreated) return;

            // pnlDisplay sits inside panelDisplay; compare z-order at the Console level
            Control host = pnlDisplay;
            while (host.Parent != null && host.Parent != this) host = host.Parent;
            if (host.Parent != this) return;

            Rectangle area = new Rectangle(Point.Empty, pnlDisplay.Size);
            int displayZ = Controls.GetChildIndex(host);
            List<Rectangle> rects = new List<Rectangle>();
            foreach (Control c in Controls)
            {
                if (c == host || !c.Visible || (!IsNativeMovableControl(c) && c != panelModernSMeter)) continue;
                if (Controls.GetChildIndex(c) > displayZ) continue;   // under the display, not over it
                Rectangle r = pnlDisplay.RectangleToClient(RectangleToScreen(c.Bounds));
                if (r.IntersectsWith(area)) rects.Add(r);
            }
            Display.ModernDisplayHwnd = pnlDisplay.Handle;   // hover lift: the render thread maps the cursor into the display
            Display.ModernPanelShadowRects = rects.ToArray();
        }

        // ---- one UI font: the designer's Microsoft Sans Serif / Tahoma labels become Segoe UI ----

        private readonly Dictionary<Control, Font> _modernSavedFonts = new Dictionary<Control, Font>();

        private void applyModernFonts(bool on)
        {
            if (on)
            {
                foreach (Control c in modernTextControls(this))
                {
                    Font f = c.Font;
                    string fam = f.FontFamily.Name;
                    if (fam != "Microsoft Sans Serif" && fam != "Tahoma") continue;
                    _modernSavedFonts[c] = f;
                    c.Font = ModernTheme.UiFont(f.SizeInPoints, f.Style);
                }
            }
            else
            {
                foreach (KeyValuePair<Control, Font> kv in _modernSavedFonts)
                    if (!kv.Key.IsDisposed) kv.Key.Font = kv.Value;
                _modernSavedFonts.Clear();
            }
        }

        // labels, captions and plain check boxes; not text boxes, combos, up-downs (their height follows the font)
        private static IEnumerable<Control> modernTextControls(Control root)
        {
            foreach (Control c in root.Controls)
            {
                bool text = c is Label ||
                            (c is CheckBox cb && cb.Appearance != Appearance.Button) ||
                            (c is RadioButton rb && rb.Appearance != Appearance.Button);
                if (text) yield return c;
                if (c.HasChildren && !(c is UserControl))
                    foreach (Control sub in modernTextControls(c)) yield return sub;
            }
        }

        // the console's combo boxes already have dark colours from the skin; the dark window theme
        // also darkens their drop-down button
        private static IEnumerable<ComboBox> modernCombos(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is ComboBox cb) yield return cb;
                if (c.HasChildren)
                    foreach (ComboBox sub in modernCombos(c)) yield return sub;
            }
        }

        private static IEnumerable<PrettyTrackBar> modernTrackBars
(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is PrettyTrackBar t) yield return t;
                if (c.HasChildren)
                    foreach (PrettyTrackBar sub in modernTrackBars(c)) yield return sub;
            }
        }
    }
}
