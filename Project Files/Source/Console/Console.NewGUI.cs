//  Console.NewGUI.cs
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
using System.Linq;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Thetis
{
    // New console layout (always on in this branch): the spectrum/waterfall fills the window,
    // a slim bar on top carries VFO A, the S-meter and VFO B, and a dock at the bottom has
    // tiles (Band, Mode, Filter, ...) that open the original panels in a drawer above it, plus
    // POWER/RX2/MON/MUT/TUN/MOX. The original panels and all their logic stay as they are:
    // the ones not on screen wait in an off-screen parking panel (kept Visible, so native code
    // that reads, shows, hides or moves them behaves as before).
    public partial class Console
    {
        private Panel _ngParking, _ngTop, _ngInfo, _ngDock, _ngDrawer;
        private NgTile _ngOpen;
        private readonly List<NgTile> _ngTiles = new List<NgTile>();
        private List<NgTile> _ngTilesDefault;           // order as built, for Ctrl + Shift + click
        private List<CheckBoxTS> _ngActionsDefault;
        private object _ngDragItem;                     // tile or radio button moved by Shift + drag
        private bool _ngDragged;                        // Shift was down on the press: the mouse up is no click
        private readonly List<CheckBoxTS> _ngActions = new List<CheckBoxTS>();
        private Control[] _ngParked;
        private Control _ngFilterShape;     // RX1 filter shape in the DSP drawer (Console.FilterShape.cs)
        private Control _ngDivMode;         // diversity section mode in the DSP drawer (Console.DiversityMini.cs)
        private CheckBoxTS _ngXpa;          // dock xPA: Red Pitaya only, shown while an Ext PA Control pin is set (as on the old console)
        private bool _ngActive, _ngLayoutQueued, _ngLayingOut;

        private const int NgPad = 6, NgDockHeight = 52, NgButtonHeight = 30, NgGap = 8;

        // a dock tile: slots side by side; a slot holds alternatives drawn at the same place
        // (HF/VHF/GEN bands, the four mode specific panels) of which native code shows one;
        // an alternative is a cluster of controls kept at their relative offsets
        private sealed class NgTile
        {
            public CheckBoxTS Button;
            public Control[][][] Slots;
            public Func<bool> Available = () => true;
            public IEnumerable<Control> All => Slots.SelectMany(s => s).SelectMany(g => g);
        }

        private static Control[][] ngSlot(params Control[][] alternatives) => alternatives;
        private static Control[] ngGroup(params Control[] cluster) => cluster;

        private void initNewGui()
        {
            if (_ngActive) return;
            if (collapsedDisplay) ExpandDisplay(true);

            SuspendDrawing(this);
            try
            {
                // custom display size from the old layout would fight the new one
                _nativeDisplaySizeExpanded = _nativeDisplaySizeCollapsed = null;
                collapseToolStripMenuItem.Visible = false;

                _ngParking = new Panel { Name = "ngParking", Location = new Point(-20000, 0), Size = new Size(4000, 2000) };
                _ngTop = new Panel { Name = "ngBar", BackColor = ModernTheme.Back };
                _ngTop.Paint += (s, e) =>
                {
                    ngPaintBar(e.Graphics, _ngTop, false);
                    ngPaintShadows(e.Graphics, NgTopPanels.Where(c => c.Visible).Select(c => _ngTop.RectangleToClient(RectangleToScreen(c.Bounds))));
                };
                // info panel between VFO A and B: its own PanelTS on the bar, so Shift + drag / wheel move and fade it
                // like the other top panels (name "ngTop": keeps the position / opacity saved before)
                _ngInfo = new PanelTS { Name = "ngTop", BackColor = ModernTheme.PanelBack };
                _ngInfo.Paint += (s, e) => ngPaintTopInfo(e.Graphics, _ngInfo.ClientRectangle);
                _ngDock = new Panel { Name = "ngDock", BackColor = ModernTheme.Back, Height = NgDockHeight };
                _ngDock.Paint += (s, e) => ngPaintDock(e.Graphics);
                ModernButtonPainter.PaintBehind = (b, g) =>
                {
                    if (b.Parent != _ngDock) return false;
                    var st = g.Save();
                    g.TranslateTransform(-b.Left, -b.Top);
                    ngPaintDock(g);
                    g.Restore(st);
                    return true;
                };
                _ngDrawer = new Panel { Name = "ngDrawer", BackColor = ModernTheme.PanelBack, Visible = false, Padding = new Padding(NgGap) };
                _ngDrawer.Paint += (s, e) =>
                {
                    ModernTheme.ScanLines(e.Graphics, _ngDrawer.ClientRectangle);
                    ModernTheme.HudFrame(e.Graphics, new RectangleF(1.5f, 1.5f, _ngDrawer.Width - 3f, _ngDrawer.Height - 3f), ModernTheme.Accent, 10f);
                };
                foreach (Panel p in new[] { _ngTop, _ngInfo, _ngDock, _ngDrawer })
                    typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(p, true);
                Dictionary<string, string> st = DB.GetVarsDictionary("State");

                Controls.Add(_ngParking);
                Controls.Add(_ngTop);
                Controls.Add(_ngInfo);
                Controls.Add(_ngDock);
                Controls.Add(_ngDrawer);
                // bar shadows follow the panels: repaint the bar when one moves, resizes or hides
                foreach (Control c in NgTopPanels)
                {
                    c.Move += (s, e) => _ngTop.Invalidate();
                    c.Resize += (s, e) => _ngTop.Invalidate();
                    c.VisibleChanged += (s, e) => _ngTop.Invalidate();
                }

                _ngFilterShape = new FilterShapeView(this);
                _ngDivMode = createDiversityModePanel();
                _ngParked = new Control[]
                {
                    _ngFilterShape, _ngDivMode, lblAGC, comboAGC,
                    lblPAProfile, panelModeSpecificCW, panelModeSpecificPhone, panelModeSpecificFM, panelModeSpecificDigital,
                    chkRX2Preamp, chkRX1Preamp, chkDX, nudPwrTemp2, nudPwrTemp, chkFullDuplex, btnHidden,
                    grpMultimeter, grpMultimeterMenus, grpRX2Meter, grpDisplaySplit, grpVFOBetween,
                    panelFilter, panelMode, panelBandHF, panelBandVHF, panelBandGEN,
                    panelOptions, panelPower, panelVFO, panelDSP, panelDisplay2, panelMultiRX, panelSoundControls,
                    chkSquelch, ptbSquelch, picSquelch, lblAF2, lblPWR2, lblRF2,
                    panelRX2RF, panelRX2DSP, panelRX2Filter, panelRX2Mode, panelRX2Display, panelRX2Mixer, panelRX2Power,
                    chkRX2Squelch, ptbRX2Squelch, picRX2Squelch,
                    panelMeterLabels, panelButtonBar, panelVFOBLabels, panelVFOALabels, panelVFOLabels, panelAndromedaMisc,
                    chkVAC1, chkVAC2, ucVAC1UnderOver, ucVAC2UnderOver, chkMUT,
                };

                ngTile("Band", ngSlot(ngGroup(panelBandHF), ngGroup(panelBandVHF), ngGroup(panelBandGEN)));
                ngTile("Mode", ngSlot(ngGroup(panelMode)));
                ngTile("Filter", ngSlot(ngGroup(panelFilter)));
                ngTile("VFO", ngSlot(ngGroup(grpVFOBetween)), ngSlot(ngGroup(panelVFO)));
                ngTile("Audio", ngSlot(ngGroup(panelSoundControls)), ngSlot(ngGroup(chkVAC1, chkMUT, chkVAC2, ucVAC1UnderOver, ucVAC2UnderOver)), ngSlot(ngGroup(chkSquelch, ptbSquelch, picSquelch)), ngSlot(ngGroup(panelMultiRX)));
                ngTile("DSP", ngSlot(ngGroup(panelDSP)), ngSlot(ngGroup(lblAGC, comboAGC)), ngSlot(ngGroup(_ngDivMode, _ngFilterShape)));
                ngTile("Display", ngSlot(ngGroup(panelDisplay2)), ngSlot(ngGroup(grpDisplaySplit)));
                ngTile("Meter", ngSlot(ngGroup(grpMultimeter)), ngSlot(ngGroup(grpMultimeterMenus)));
                ngTile("TX", ngSlot(ngGroup(panelModeSpecificPhone), ngGroup(panelModeSpecificCW), ngGroup(panelModeSpecificDigital), ngGroup(panelModeSpecificFM)),
                    ngSlot(ngGroup(panelOptions)));
                NgTile rx2 = ngTile("RX2 Panel", ngSlot(ngGroup(panelRX2Mode)), ngSlot(ngGroup(panelRX2Filter)), ngSlot(ngGroup(panelRX2DSP)),
                    ngSlot(ngGroup(panelRX2Display)), ngSlot(ngGroup(panelRX2Mixer)), ngSlot(ngGroup(panelRX2RF)),
                    ngSlot(ngGroup(chkRX2Squelch, ptbRX2Squelch, picRX2Squelch)), ngSlot(ngGroup(grpRX2Meter)));
                rx2.Available = () => RX2Enabled;

                ngAction("POWER", chkPower);
                ngAction("RX2", chkRX2);
                ngAction("MON", chkMON);
                ngAction("2TONE", chk2TONE);
                ngAction("xPA", chkExternalPA);
                _ngXpa = _ngActions.Last();
                XPAChangedHandlers += (inUse, oldState, newState) => ngLayout();
                ngAction("TUN", chkTUN);
                ngAction("MOX", chkMOX);

                // VAC1/VAC2 went to the Audio drawer: the VFO panel ends above where they were
                panelVFO.Height = chkVAC1.Top;
                // MUT joins the VAC row (left of VAC2) so the Audio drawer does not grow; Expand restores from chk_mut_basis
                chkMUT.Location = chk_mut_basis = chkVAC2.Location;
                chkMUT.AutoCheck = false;   // click: off -> radio AF (green) -> VAC1 (violet) -> whole RX1 (red) -> off
                chkMUT.Click += chkMUT_ClickCycle;
                toolTip1.SetToolTip(chkMUT, "Mute RX1. Green: only the radio's audio output (VAC, TCI, VST keep playing). " +
                    "Violet (second click): only VAC1, the radio keeps playing. Red (third click): the whole receiver. Fourth click: off.");
                chkVAC2.Left += chkMUT.Width + 1;
                ucVAC2UnderOver.Left += chkMUT.Width + 1;
                // DSP drawer: AGC under the DSP buttons as wide as them, diversity mode above the filter shape
                lblAGC.Left = 1;
                lblAGC.BackColor = Color.Transparent;
                comboAGC.Bounds = new Rectangle(0, lblAGC.Bottom + 1, panelDSP.Width, comboAGC.Height);
                comboAGC.DropDownWidth = comboAGC.Width;
                _ngDivMode.Location = Point.Empty;
                _ngDivMode.Width = _ngFilterShape.Width;
                _ngFilterShape.Location = new Point(0, _ngDivMode.Bottom + 4);

                // Audio drawer: AGC gone and RF/drive in the dock - tune and preamp/ATT move up into the gap,
                // preamp/ATT take the AGC place and are wide enough to read; Expand restores from the basis fields
                const int wide = 50;
                panelSoundControls.Width += wide;
                foreach (PrettyTrackBar tb in panelSoundControls.Controls.OfType<PrettyTrackBar>()) tb.Width += wide;
                int upDy = lblTune.Top - lblRF.Top;
                foreach (Control c in new Control[] { lblTune, ptbTune, lblPreamp, comboPreamp, udRX1StepAttData, udTXStepAttData, pbAutoAttWarningRX1 })
                    c.Top -= upDy;
                pbAutoAttWarningRX1.Left += wide;
                comboPreamp.Bounds = new Rectangle(ptbTune.Left, comboPreamp.Top, comboPreamp.Width + wide, comboPreamp.Height);
                lblPreamp.Left = comboPreamp.Left + 1;
                udRX1StepAttData.Bounds = new Rectangle(comboPreamp.Left, udRX1StepAttData.Top, comboPreamp.Width, udRX1StepAttData.Height);
                udTXStepAttData.Width = comboPreamp.Width;
                comboPreamp.DropDownWidth = comboPreamp.Width;
                panelSoundControls.Height -= upDy;
                combo_agc_basis = comboAGC.Location;
                combo_preamp_basis = comboPreamp.Location;
                ud_rx1_step_att_present_data_basis = udRX1StepAttData.Location;

                foreach (Control c in _ngParked) _ngParking.Controls.Add(c);
                // drawer panels drop the skin plate for the HUD glass (also when a skin is loaded later)
                foreach (Control c in _ngTiles.SelectMany(t => t.All).Where(c => c is Panel || c is GroupBox))
                {
                    ngHudPlate(c);
                    c.BackgroundImageChanged += (s, e) => ngHudPlate((Control)s);
                }
                // dock tile order (Shift + drag); names missing from the saved order keep their place at the end
                _ngTilesDefault = _ngTiles.ToList();
                _ngActionsDefault = _ngActions.ToList();
                if (st.TryGetValue(NgTileOrderKey, out string order)) ngApplyOrder(_ngTiles, order, t => t.Button.Text);
                if (st.TryGetValue(NgActionOrderKey, out order)) ngApplyOrder(_ngActions, order, b => b.Text);
                // a button newer than the saved order lands after MOX; MOX has to stay last
                CheckBoxTS moxKey = _ngActions.First(b => b.Text == "MOX");
                _ngActions.Remove(moxKey);
                _ngActions.Add(moxKey);
                // the top panels stay Console children over the bar, so Shift + drag / wheel move and fade them as before
                foreach (Control c in NgTopPanels) Controls.Add(c);
                foreach (KeyValuePair<string, string> kv in st)
                    if (kv.Key.StartsWith(NgPosKeyPrefix, StringComparison.Ordinal))
                    {
                        string[] xy = kv.Value.Split('|');
                        if (xy.Length == 2 && int.TryParse(xy[0], out int px) && int.TryParse(xy[1], out int py))
                            _ngTopPos[kv.Key.Substring(NgPosKeyPrefix.Length)] = new Point(px, py);
                    }
                // drawer see-through level (Shift + wheel); the classic loader only knows the native panels
                if (st.TryGetValue(NativePanelOpacityKeyPrefix + _ngDrawer.Name, out string dop) && int.TryParse(dop, out int dpct))
                    _nativePanelOpacities[_ngDrawer.Name] = ClampNativePanelOpacity(dpct);
                if (st.TryGetValue(NativePanelOpacityKeyPrefix + _ngInfo.Name, out string iop) && int.TryParse(iop, out int ipct))
                    SetNativePanelOpacity(_ngInfo, ipct);

                // the main frequency 1.5x larger: the whole VFO A box with its children and their fonts
                grpVFOA.Scale(new SizeF(NgVfoAScale, NgVfoAScale));
                foreach (Control c in grpVFOA.Controls)
                    c.Font = new Font(c.Font.FontFamily, c.Font.Size * NgVfoAScale, c.Font.Style, c.Font.Unit);

                // no white 3D frame around the band name in the VFOs
                txtVFOABand.BorderStyle = txtVFOBBand.BorderStyle = BorderStyle.None;

                // the new layout is drawn with the modern look only
                if (!IsSetupFormNull) SetupForm.ForceModernLook();
                ModernUI = true;
                ModernVFO = true;
                ModernSMeter = true;
                ModernButtonPainter.GlyphFor = b => _ngGlyphs.TryGetValue(b, out string glyph) ? glyph : null;
                foreach (CheckBoxTS b in _ngTiles.Select(t => t.Button).Concat(_ngActions)) ModernButtonPainter.Attach(b);
                _ngActions.Last().Paint += ngMoxOutline;   // after the painter, so it lands on top of the face

                foreach (Control c in _ngTiles.SelectMany(t => t.All).Concat(new Control[] { grpVFOA, grpVFOB, panelModernSMeter, panelDisplay }))
                {
                    c.LocationChanged += ngLayoutEvent;
                    c.SizeChanged += ngLayoutEvent;
                    c.VisibleChanged += ngLayoutEvent;
                    // see-through levels from the classic layout (Shift + wheel) would dim the drawer;
                    // the top panels keep theirs
                    if (Array.IndexOf(NgTopPanels, c) >= 0)
                    {
                        if (st.TryGetValue(NativePanelOpacityKeyPrefix + c.Name, out string op) && int.TryParse(op, out int pct))
                            SetNativePanelOpacity(c, pct);
                    }
                    else SetNativePanelOpacity(c, 100);
                }
                chkRX2.CheckedChanged += ngLayoutEvent;
                Resize += ngLayoutEvent;

                _ngActive = true;
                ngLayout();

                // window chrome in the HUD colours, info panel refreshed in the top bar
                ngDarkTitleBar(this);
                HandleCreated += (s, e) => ngDarkTitleBar(this);
                NgMenuRenderer menu = new NgMenuRenderer();
                _ngMenuRenderer = menu;
                menuStrip1.Renderer = menu;
                ngHudMenu(menuStrip1.Items, menu);
                // room for the tile faces: upper-case HUD captions are wider than the plain menu text
                menuStrip1.Padding = new Padding(NgGap, 3, NgGap, 3);
                foreach (ToolStripItem i in menuStrip1.Items)
                {
                    i.Font = ModernTheme.HudFont(8f);
                    i.Margin = new Padding(2, 0, 2, 0);
                    i.Padding = new Padding(10, 4, 10, 4);
                }
                // the taller menu would cover the edge line under it
                menuStrip1.SizeChanged += ngLayoutEvent;
                ngLayout();
                foreach (ContextMenuStrip cm in new[] { contextMenuStripFilterRX1, contextMenuStripFilterRX2, contextMenuStripNotch })
                {
                    cm.Renderer = menu;
                    ngHudMenu(cm.Items, menu);
                }
                _ngClock = new Timer { Interval = 500 };
                _ngClock.Tick += (s, e) => _ngInfo.Invalidate();
                _ngClock.Start();

                // shadows / hover lift on the panorama follow the display when the layout moves it
                panelDisplay.LocationChanged += (s, e) => updateModernPanelShadows();
                panelDisplay.SizeChanged += (s, e) => updateModernPanelShadows();

                infoBar.ApplyModernLook();

                // otherwise the start-up focus lands in the VFO A frequency box (shows the edit box instead of the digits)
                BeginInvoke(new System.Windows.Forms.MethodInvoker(() => btnHidden.Focus()));
            }
            catch (Exception ex)
            {
                Common.LogException(ex);
            }
            finally
            {
                ResumeDrawing(this);
            }
        }

        // Segoe Fluent Icons / MDL2 code points for the dock tiles
        private static readonly Dictionary<string, string> NgTileGlyphs = new Dictionary<string, string>
        {
            { "Band", "\uEC05" }, { "Mode", "\uE8AB" }, { "Filter", "\uE71C" }, { "VFO", "\uE895" }, { "Audio", "\uE767" },
            { "DSP", "\uE9E9" }, { "Display", "\uE7F4" }, { "Meter", "\uE9D9" }, { "TX", "\uE720" }, { "RX2 Panel", "\uE8A1" },
        };
        private readonly Dictionary<ButtonBase, string> _ngGlyphs = new Dictionary<ButtonBase, string>();

        private NgTile ngTile(string text, params Control[][][] slots)
        {
            NgTile t = new NgTile { Slots = slots, Button = ngButton(text, ngTileWidth(text)) };
            if (NgTileGlyphs.TryGetValue(text, out string glyph)) _ngGlyphs[t.Button] = glyph;
            t.Button.Click += (s, e) =>
            {
                if (_ngDragged) { _ngDragged = false; return; }
                ngOpen(_ngOpen == t ? null : t);
            };
            t.Button.MouseDown += (s, e) => ngDragDown(_ngTiles, _ngTilesDefault, t, e);
            t.Button.MouseMove += (s, e) => ngDragMove(_ngTiles, t, u => u.Button, u => true);
            t.Button.MouseUp += (s, e) => _ngDragItem = null;
            _ngTiles.Add(t);
            return t;
        }

        // Shift + drag moves a dock button among its group (tiles / radio buttons), Ctrl + Shift + click
        // restores the built order; names missing from a saved order keep their place at the end
        private static void ngApplyOrder<T>(List<T> list, string order, Func<T, string> name)
        {
            List<string> names = order.Split('|').ToList();
            List<T> sorted = list.OrderBy(x => { int i = names.IndexOf(name(x)); return i < 0 ? int.MaxValue : i; }).ToList();
            list.Clear();
            list.AddRange(sorted);
        }

        private void ngDragDown<T>(List<T> list, List<T> deflt, T item, MouseEventArgs e)
        {
            _ngDragged = false;
            if (e.Button != MouseButtons.Left || (ModifierKeys & Keys.Shift) != Keys.Shift) return;
            _ngDragged = true;
            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                list.Clear();
                list.AddRange(deflt);
                ngLayout();
                return;
            }
            _ngDragItem = item;
        }

        private void ngDragMove<T>(List<T> list, T item, Func<T, Control> button, Func<T, bool> movable)
        {
            if (!Equals(_ngDragItem, item) || (MouseButtons & MouseButtons.Left) == 0) return;
            int mx = _ngDock.PointToClient(Cursor.Position).X;
            T over = list.FirstOrDefault(u => !Equals(u, item) && movable(u) && button(u).Visible && mx >= button(u).Left && mx < button(u).Right);
            if (over == null) return;
            int to = list.IndexOf(over);
            list.Remove(item);
            list.Insert(to, item);
            ngLayout();
        }

        // dock copy of a console button: shows its state, a click is a click on the original
        private void ngAction(string text, CheckBoxTS target)
        {
            CheckBoxTS b = ngButton(text, 62);
            if (text == "MOX")
            {
                // the big transmit key
                b.Size = new Size(NgMoxWidth, NgDockHeight - 8);
                b.Font = ModernTheme.UiFont(14f, FontStyle.Bold);
            }
            b.Tag = target;
            b.Checked = target.Checked;
            b.Enabled = target.Enabled;
            b.BackColor = target.BackColor;
            target.CheckedChanged += (s, e) => b.Checked = target.Checked;
            target.EnabledChanged += (s, e) => b.Enabled = target.Enabled;
            target.BackColorChanged += (s, e) => b.BackColor = target.BackColor;
            b.Click += (s, e) =>
            {
                if (_ngDragged) { _ngDragged = false; return; }
                // OnClick toggles the CheckBox and raises Click, exactly as a mouse click does
                if (target.Enabled)
                    typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, new object[] { EventArgs.Empty });
            };
            b.MouseDown += (s, e) =>
            {
                // right click opens the original's Setup page (TUN, MON...)
                if (e.Button == MouseButtons.Right)
                    typeof(Control).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, new object[] { e });
            };
            // MOX stays in the middle; the radio buttons right of it move with Shift + drag
            if (text != "MOX")
            {
                b.MouseDown += (s, e) => ngDragDown(_ngActions, _ngActionsDefault, b, e);
                b.MouseMove += (s, e) => ngDragMove(_ngActions, b, u => u, u => u.Text != "MOX");
                b.MouseUp += (s, e) => _ngDragItem = null;
            }
            _ngActions.Add(b);
        }

        // red outline on the idle MOX key so it stands out as the transmit key
        private void ngMoxOutline(object sender, PaintEventArgs e)
        {
            CheckBoxTS b = (CheckBoxTS)sender;
            if (!ModernTheme.Enabled || !b.Enabled || b.Checked) return;
            RectangleF r = new RectangleF(1.5f, 1.5f, b.Width - 3f, b.Height - 3f);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = ModernTheme.Chamfer(r, Math.Max(2f, Math.Min(7f, r.Height / 2.8f))))
            using (Pen pen = new Pen(Color.FromArgb(190, ModernTheme.Danger), 1.5f))
                e.Graphics.DrawPath(pen, path);
        }

        private CheckBoxTS ngButton(string text, int width)
        {
            CheckBoxTS b = new CheckBoxTS
            {
                Text = text,
                Appearance = Appearance.Button,
                AutoCheck = false,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(width, NgButtonHeight),
                Font = ModernTheme.UiFont(9f, FontStyle.Bold),
                UseVisualStyleBackColor = false,
                TabStop = false,
            };
            b.FlatAppearance.BorderSize = 0;
            // the face is drawn over a piece of the dock: unbuffered, the half-drawn state flickers
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(b, true);
            _ngDock.Controls.Add(b);
            return b;
        }

        private void ngOpen(NgTile t)
        {
            if (t != null && !t.Available()) t = null;
            _ngOpen = t;
            foreach (NgTile x in _ngTiles) x.Button.Checked = x == t;
            if (t == null)
            {
                ngLayout();
                return;
            }

            // the drawer rises a little and fades in; hidden before the panels move in,
            // otherwise the half-painted new content shows in the old drawer for a frame
            ApplyNativePanelOpacity(_ngDrawer, 1);
            _ngSlide = NgSlidePx;
            ngLayout();
            _ngFadeStart = Environment.TickCount;
            if (_ngFadeTimer == null)
            {
                _ngFadeTimer = new Timer { Interval = 15 };
                _ngFadeTimer.Tick += (s, e) => ngFadeTick();
            }
            _ngFadeTimer.Start();
        }

        private Timer _ngFadeTimer, _ngClock;
        private int _ngFadeStart, _ngSlide;
        private Point _ngDrawerHome;
        private const int NgFadeMs = 170, NgSlidePx = 16;

        private void ngFadeTick()
        {
            float k = Math.Min(1f, (Environment.TickCount - _ngFadeStart) / (float)NgFadeMs);
            float ease = 1f - (1f - k) * (1f - k) * (1f - k);
            // fades in to the level set with Shift + wheel
            int pct = _nativePanelOpacities.TryGetValue(_ngDrawer.Name, out int o) ? o : 100;
            _ngSlide = k >= 1f ? 0 : (int)Math.Round((1f - ease) * NgSlidePx);
            _ngDrawer.Location = new Point(_ngDrawerHome.X, _ngDrawerHome.Y + _ngSlide);
            ngDrawerShadow(ease);
            if (k >= 1f)
            {
                _ngFadeTimer.Stop();
                // stays layered even at 100%: dropping WS_EX_LAYERED throws away the drawer's image
                // and it flashes empty while every button repaints
                SetLayeredWindowAttributes(_ngDrawer.Handle, 0, (byte)(pct * 255 / 100), LWA_ALPHA);
            }
            else ApplyNativePanelOpacity(_ngDrawer, Math.Max(1, (int)(pct * ease)));
        }

        private void ngLayoutEvent(object sender, EventArgs e)
        {
            if (!_ngActive || _ngLayingOut || _ngLayoutQueued || IsDisposed || !IsHandleCreated) return;
            // after the native layout pass that caused the event has finished
            _ngLayoutQueued = true;
            BeginInvoke(new System.Windows.Forms.MethodInvoker(() => { _ngLayoutQueued = false; ngLayout(); }));
        }

        private void ngLayout()
        {
            if (!_ngActive || IsDisposed || WindowState == FormWindowState.Minimized) return;
            _ngLayingOut = true;
            try
            {
                if (_ngOpen != null && !_ngOpen.Available()) { _ngOpen.Button.Checked = false; _ngOpen = null; }
                // before moving panels in: Visible of a child reads false while its parent is hidden
                _ngDrawer.Visible = _ngOpen != null;

                // everything not in the open drawer goes (back) to parking; native code
                // (Expand, mode changes) may have pulled some onto the Console
                HashSet<Control> inDrawer = new HashSet<Control>(_ngOpen?.All ?? Enumerable.Empty<Control>());
                foreach (Control c in _ngParked)
                {
                    Control want = inDrawer.Contains(c) ? _ngDrawer : _ngParking;
                    if (c.Parent != want) want.Controls.Add(c);
                }
                foreach (Control c in NgTopPanels)
                    if (c.Parent != this) Controls.Add(c);

                int w = ClientSize.Width;
                int top = menuStrip1.Visible ? menuStrip1.Bottom : 0;
                int bottom = statusStripMain.Visible ? statusStripMain.Top : ClientSize.Height;

                // top bar: S-meter left, big VFO A in the middle, info panel between it and VFO B on the right
                Size meter = new Size(grpVFOB.Width + 80, grpVFOB.Height);
                if (panelModernSMeter.Size != meter) panelModernSMeter.Size = meter;
                int barH = Math.Max(grpVFOA.Height, panelModernSMeter.Height) + 2 * NgPad;
                int meterX = NgGap, vfoBX = w - NgGap - grpVFOB.Width;
                int vfoAX = Math.Max(meterX + panelModernSMeter.Width + NgGap, (w - grpVFOA.Width) / 2);
                // info panel the size of VFO B, centred in the gap between VFO A and VFO B
                int gapL = vfoAX + grpVFOA.Width, gapR = vfoBX;
                _ngTop.Bounds = new Rectangle(0, top, w, barH);
                _ngClockRect = new Rectangle(gapL + (gapR - gapL - grpVFOB.Width) / 2, top + (barH - grpVFOB.Height) / 2, grpVFOB.Width, grpVFOB.Height);
                Rectangle info = ngInfoRect();
                _ngInfo.Visible = !info.IsEmpty;
                if (!info.IsEmpty && _ngInfo.Size != info.Size) _ngInfo.Size = info.Size;
                if (!info.IsEmpty) ngPlaceTop(_ngInfo, info.X, info.Y);
                ngPlaceTop(panelModernSMeter, meterX, top + (barH - panelModernSMeter.Height) / 2);
                ngPlaceTop(grpVFOB, vfoBX, top + (barH - grpVFOB.Height) / 2);
                ngPlaceTop(grpVFOA, vfoAX, top + (barH - grpVFOA.Height) / 2);

                // dock: MOX in the middle, tiles left of it (narrowed to fit), radio buttons from the right
                _ngDock.Bounds = new Rectangle(0, bottom - NgDockHeight, w, NgDockHeight);
                CheckBoxTS mox = _ngActions[_ngActions.Count - 1];
                // the slider plate gives a fifth of its natural width to MOX, which grows to the right
                _ngXpa.Visible = HardwareSpecific.Model == HPSDRModel.REDPITAYA && _xpa_in_use;
                int actionsLeft = w - NgGap - _ngActions.Take(_ngActions.Count - 1).Where(a => a != _ngXpa || _ngXpa.Visible).Sum(a => a.Width + 4);
                int moxL = (w - NgMoxWidth) / 2;
                int room0 = actionsLeft - (moxL + NgMoxWidth + 18 - NgGap) - 4 * NgGap;   // extra NgGap each side: air around the slider plate
                int give = room0 > 160 ? (NgSliderLabelWidth + 28 + Math.Min(400, room0 - NgSliderLabelWidth - 8)) / 5 : 0;
                if (mox.Width != NgMoxWidth + give) mox.Width = NgMoxWidth + give;
                ngMove(mox, moxL, (NgDockHeight - mox.Height) / 2);
                foreach (NgTile t in _ngTiles)
                {
                    // a tile whose panels native code keeps hidden (e.g. the legacy meter) is left out
                    t.Button.Visible = t.All.Any(c => c.Visible);
                    t.Button.Enabled = t.Available();
                }
                List<NgTile> shown = _ngTiles.Where(t => t.Button.Visible).ToList();
                int natural = shown.Sum(t => ngTileWidth(t.Button.Text) + 4);
                float k = Math.Min(1f, (mox.Left - 18 - NgGap) / (float)Math.Max(1, natural));
                int x = NgGap;
                foreach (NgTile t in shown)
                {
                    int tw = Math.Max(36, (int)((ngTileWidth(t.Button.Text) + 4) * k) - 4);
                    if (t.Button.Width != tw) t.Button.Width = tw;
                    ngMove(t.Button, x, (NgDockHeight - t.Button.Height) / 2);
                    x += tw + 4;
                }
                int tilesRight = mox.Right + 18 - NgGap;    // the AGC slider goes right of MOX
                x = w - NgGap;
                for (int i = _ngActions.Count - 2; i >= 0; i--)
                {
                    if (_ngActions[i] == _ngXpa && !_ngXpa.Visible) continue;
                    x -= _ngActions[i].Width;
                    ngMove(_ngActions[i], x, (NgDockHeight - _ngActions[i].Height) / 2);
                    x -= 4;
                }

                // RX1 AGC gain above TX drive, between MOX and the radio buttons
                (Label lbl, PrettyTrackBar tb)[] sliders = { (lblRF, ptbRF), (lblPWR, ptbPWR) };
                int room = x - tilesRight - 4 * NgGap;
                int lx = tilesRight + 2 * NgGap;
                int rowH = (NgDockHeight - 2) / sliders.Length;
                for (int i = 0; i < sliders.Length; i++)
                {
                    var (lbl, tb) = sliders[i];
                    int rowY = 1 + i * rowH;
                    if (lbl.Parent != _ngDock)
                    {
                        _ngDock.Controls.Add(lbl);
                        _ngDock.Controls.Add(tb);
                        lbl.BackColor = Color.Transparent;
                        tb.BackColor = Color.Transparent;   // shows the glass plate drawn by the dock
                        lbl.ForeColor = ModernTheme.Text;
                    }
                    // fixed width: the text changes with the value ("Drive:  100W") and must not run into the slider
                    lbl.AutoSize = false;
                    lbl.Width = NgSliderLabelWidth;
                    lbl.TextAlign = ContentAlignment.MiddleLeft;
                    lbl.Visible = tb.Visible = room0 > 160;
                    int sliderW = Math.Min(400, room0 - lbl.Width - 8) - give;
                    int sx = lx + Math.Max(0, (room - lbl.Width - 8 - sliderW) / 2);
                    ngMove(lbl, sx, rowY + (rowH - lbl.Height) / 2);
                    if (sliderW > 40 && tb.Width != sliderW) tb.Width = sliderW;
                    ngMove(tb, lbl.Right + 8, rowY + (rowH - tb.Height) / 2);
                }

                Rectangle display = new Rectangle(0, _ngTop.Bottom, w, Math.Max(40, _ngDock.Top - _ngTop.Bottom));
                if (panelDisplay.Bounds != display) panelDisplay.Bounds = display;
                // its labels ran past the right edge, cutting the PureSignal indicator in half
                infoBar.ModernRelayout();

                ngLayoutDrawer();
                _ngTop.BringToFront();
                _ngDock.BringToFront();
                foreach (Control c in NgTopPanels) c.BringToFront();
                // docked Meters/Gadgets containers are Console children too: they went under the bars and panels
                MeterManager.BringToFront();
                _ngDrawer.BringToFront();
            }
            catch (Exception ex)
            {
                Common.LogException(ex);
            }
            finally
            {
                _ngLayingOut = false;
            }
        }

        private void ngLayoutDrawer()
        {
            if (_ngOpen == null)
            {
                _ngDrawer.Visible = false;
                ngDrawerShadow(0f);
                return;
            }
            _ngDrawer.Visible = true;

            // children of a visible drawer: Visible is the native show/hide state; per slot the
            // visible alternatives with their bounding boxes
            var slots = _ngOpen.Slots
                .Select(slot => slot.Where(g => g.Any(c => c.Visible))
                    .Select(g => (group: g, bb: g.Select(ngBounds).Aggregate(Rectangle.Union))).ToList())
                .Where(s => s.Count > 0).ToList();
            int h = slots.Count == 0 ? 0 : slots.Max(s => s.Max(a => a.bb.Height));

            // columns: a slot goes below the previous one while the tallest slot's height has room
            int x = NgGap, y = 0, colW = 0;
            foreach (var slot in slots)
            {
                int sw = slot.Max(a => a.bb.Width), sh = slot.Max(a => a.bb.Height);
                if (y > 0 && y + sh > h)
                {
                    x += colW + NgGap;
                    y = colW = 0;
                }
                foreach (var (group, bb) in slot)
                    foreach (Control c in group)
                        ngMove(c, x + c.Left - bb.Left, NgGap + y + c.Top - bb.Top);
                colW = Math.Max(colW, sw);
                y += sh + NgGap;
            }
            x += colW + NgGap;

            Size size = new Size(Math.Max(120, x), Math.Max(40, h + 2 * NgGap));
            int left = Math.Max(NgGap, Math.Min(_ngOpen.Button.Left, ClientSize.Width - size.Width - NgGap));
            _ngDrawerHome = new Point(left, _ngDock.Top - size.Height - NgGap);
            _ngDrawer.Bounds = new Rectangle(new Point(_ngDrawerHome.X, _ngDrawerHome.Y + _ngSlide), size);
            _ngDrawer.Invalidate();
            ngDrawerShadow(1f - _ngSlide / (float)NgSlidePx);
        }

        // the open drawer floats highest: a deep shadow on the panorama (drawn by Display)
        private void ngDrawerShadow(float k)
        {
            if (pnlDisplay == null || !pnlDisplay.IsHandleCreated) return;
            Display.ModernDrawerRect = k > 0f && _ngDrawer.Visible ? pnlDisplay.RectangleToClient(RectangleToScreen(_ngDrawer.Bounds)) : Rectangle.Empty;
            Display.ModernDim = _ngDrawer.Visible ? k : 0f;
        }

        // ---- top bar readout: info panel ----

        private const int NgClockWidth = 270,NgSliderLabelWidth = 110, NgMoxWidth = 150;
        private const float NgVfoAScale = 1.5f;

        // ---- top panels: a default place in the bar, or where Shift + drag left them (State NgPos.*) ----

        private const string NgPosKeyPrefix = "NgPos.";
        private readonly Dictionary<string, Point> _ngTopPos = new Dictionary<string, Point>(StringComparer.Ordinal);
        private Rectangle _ngClockRect;
        private Control[] NgTopPanels => new Control[] { grpVFOA, grpVFOB, panelModernSMeter, _ngInfo };

        private void ngPlaceTop(Control c, int x, int y)
        {
            if (c == _nativePanelDragPrimary) return;   // being dragged
            if (_ngTopPos.TryGetValue(c.Name, out Point p))
            {
                // anywhere above the dock
                int bottom = (statusStripMain.Visible ? statusStripMain.Top : ClientSize.Height) - NgDockHeight;
                x = Math.Max(0, Math.Min(ClientSize.Width - c.Width, p.X));
                y = Math.Max(0, Math.Min(bottom - c.Height, p.Y));
            }
            ngMove(c, x, y);
        }

        // called at the end of a Shift + drag; reset = Ctrl + Shift + click puts the panel back in the bar
        internal bool ngTopPanelDragged(Control c, bool reset)
        {
            if (!_ngActive || Array.IndexOf(NgTopPanels, c) < 0) return false;
            if (reset) _ngTopPos.Remove(c.Name);
            else _ngTopPos[c.Name] = c.Location;
            ngLayout();
            _ngTop.Invalidate();   // the panel's shadow on the bar moves with it
            return true;
        }

        private const string NgTileOrderKey = "NgTileOrder", NgActionOrderKey = "NgActionOrder";

        private void ngAppendTopPos(List<string> state)
        {
            state.Add(NgTileOrderKey + "/" + string.Join("|", _ngTiles.Select(t => t.Button.Text)));
            state.Add(NgActionOrderKey + "/" + string.Join("|", _ngActions.Select(b => b.Text)));
            // a reset panel is written as empty: DB.SaveVars never deletes rows
            foreach (Control c in NgTopPanels)
                state.Add(NgPosKeyPrefix + c.Name + "/" + (_ngTopPos.TryGetValue(c.Name, out Point p) ? p.X + "|" + p.Y : ""));
        }

        private static int ngTileWidth(string text) => text.Length > 7 ? 108 : 90;

        // info panel between VFO A and VFO B (the clock is already in the status bar):
        // diversity per-section mode / clean AUTO bin share, TX attenuator + PureSignal state, network traffic from / to the radio

        private Rectangle ngInfoRect()
        {
            Rectangle r = _ngClockRect;
            if (r.Width < 150 || r.Height < 40) return Rectangle.Empty;
            int w = Math.Min(r.Width, NgClockWidth);
            return new Rectangle(r.Left + (r.Width - w) / 2, r.Top, w, r.Height);
        }

        // panels and buttons float high above the bars: their shadows fall on the bar far below
        private static void ngPaintShadows(Graphics g, IEnumerable<Rectangle> rects)
        {
            foreach (Rectangle r in rects)
                if (!r.IsEmpty) ModernTheme.DepthShadow(g, r, Math.Max(2f, Math.Min(8f, r.Height / 2.8f)), 7);
        }

        // dock surface, the glass plate under the AGC / drive sliders, shadows of everything floating on it
        private void ngPaintDock(Graphics g)
        {
            ngPaintBar(g, _ngDock, true);
            // a button repaints only its own piece: skip shadows that cannot reach it
            RectangleF clip = g.ClipBounds;
            bool near(Rectangle r) => clip.IntersectsWith(Rectangle.Inflate(r, 12, 12));
            Rectangle u = Rectangle.Empty;
            foreach (Control c in new Control[] { lblRF, ptbRF, lblPWR, ptbPWR })
                if (c.Parent == _ngDock && c.Visible) u = u.IsEmpty ? c.Bounds : Rectangle.Union(u, c.Bounds);
            if (!u.IsEmpty && near(new Rectangle(u.X - 10, 4, u.Width + 20, NgDockHeight - 10)))
            {
                Rectangle plate = new Rectangle(u.X - 10, 4, u.Width + 20, NgDockHeight - 10);
                ModernTheme.DepthShadow(g, plate, 8f, 7);
                ModernTheme.GlassFrame(g, new RectangleF(plate.X + 0.5f, plate.Y + 0.5f, plate.Width - 1f, plate.Height - 1f), ModernTheme.BorderStrong, 8f);
            }
            ngPaintShadows(g, _ngDock.Controls.OfType<CheckBoxTS>().Where(b => b.Visible && near(b.Bounds)).Select(b => b.Bounds));
        }

        private void ngPaintTopInfo(Graphics g, Rectangle b)
        {
            if (b.Width < 2 || b.Height < 2) return;
            ModernTheme.GlassFrame(g, new RectangleF(b.X + 0.5f, b.Y + 0.5f, b.Width - 1f, b.Height - 1f), ModernTheme.BorderStrong, 8f);
            TextFormatFlags c = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, "DIVERSITY / FILTERS", ModernTheme.HudFont(7f), new Point(b.X + 12, b.Y + 4), ModernTheme.TextDim, TextFormatFlags.NoPadding);

            // three rows under the title: diversity, TX attenuator + PureSignal, RX/TX filter
            int rowTop = b.Y + 16, rowH = (b.Bottom - 3 - rowTop) / 3;
            Rectangle row(int i) => new Rectangle(b.X + 8, rowTop + i * rowH, b.Width - 16, rowH);

            // per-section mode; with clean AUTO the share of bins it treats as station now (as "Auto: xx%" in the DSP drawer)
            DiversityForm df = diversityForm;
            string cap = df != null && !df.IsDisposed ? df.SectionCaption : null;
            double share = cap != null && df.SectionClean < 0 ? df.SectionCleanShare : -1.0;
            if (share >= 0.0)
            {
                Rectangle r0 = row(0);
                int tw = r0.Width * 11 / 20;
                TextRenderer.DrawText(g, "AUTO BIN " + (int)Math.Round(100.0 * share) + "%", ModernTheme.HudFont(9f),
                    new Rectangle(r0.X, r0.Y, tw, r0.Height), ModernTheme.AccentSoft, c);
                RectangleF bar = new RectangleF(r0.X + tw + 4, r0.Y + r0.Height / 2f - 3f, r0.Width - tw - 8, 6f);
                using (SolidBrush track = new SolidBrush(Color.FromArgb(28, 255, 255, 255))) g.FillRectangle(track, bar);
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(210, ModernTheme.Accent)))
                    g.FillRectangle(fill, bar.X, bar.Y, bar.Width * (float)Math.Min(1.0, share), bar.Height);
            }
            else
                TextRenderer.DrawText(g, cap != null ? "DIV " + cap : "DIV OFF", ModernTheme.HudFont(9f), row(0),
                    cap != null ? ModernTheme.AccentSoft : ModernTheme.TextDim, c);

            // attenuation the radio gets on TX; PS: feedback level (target colours) and whether the correction is applied
            string att = ATTOnTX ? TxAttenData + " DB" : "OFF";
            string ps = !PureSignalEnabled ? "PS OFF"
                : "PS FB " + puresignal.FeedbackLevel + (puresignal.CorrectionsBeingApplied ? "  CORR" : "  --");
            Color psColour = PureSignalEnabled && puresignal.CorrectionsBeingApplied ? puresignal.FeedbackColourLevel : ModernTheme.TextDim;
            TextRenderer.DrawText(g, "ATT " + att, ModernTheme.HudFont(8f), new Rectangle(row(1).X, row(1).Y, row(1).Width / 2, rowH), ModernTheme.Text, c);
            TextRenderer.DrawText(g, ps, ModernTheme.HudFont(8f), new Rectangle(row(1).X + row(1).Width / 2, row(1).Y, row(1).Width / 2, rowH), psColour, c);

            // LSB-side modes keep negative RX edges; shown as audio low..high like TX
            int lo = RX1FilterLow, hi = RX1FilterHigh;
            if (hi <= 0) { int t = lo; lo = -hi; hi = -t; }
            string filt = string.Format(CultureInfo.InvariantCulture, "RX {0}-{1} HZ     TX {2}-{3} HZ", lo, hi, TXFilterLow, TXFilterHigh);
            TextRenderer.DrawText(g, filt, ModernTheme.HudFont(8f), row(2), ModernTheme.TextDim, c);
        }

        // ---- window chrome ----

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // dark title bar (Windows 10 2004+), HUD caption / border / text colours (Windows 11; ignored before)
        internal static void ngDarkTitleBar(Form f)
        {
            try
            {
                int on = 1;
                int caption = ColorTranslator.ToWin32(ModernTheme.Back);
                int border = ColorTranslator.ToWin32(ModernTheme.BorderStrong);
                int text = ColorTranslator.ToWin32(ModernTheme.Text);
                DwmSetWindowAttribute(f.Handle, 20, ref on, 4);
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);
                DwmSetWindowAttribute(f.Handle, 34, ref border, 4);
                DwmSetWindowAttribute(f.Handle, 36, ref text, 4);
            }
            catch (Exception ex) { Common.LogException(ex); }
        }

        // drop-downs keep their own renderer, so every level gets the HUD one;
        // again on opening, for sub-menus filled at run time
        private static void ngHudMenu(ToolStripItemCollection items, ToolStripRenderer r)
        {
            foreach (ToolStripItem i in items)
                if (i is ToolStripDropDownItem d)
                {
                    d.DropDownOpening -= ngHudMenuOpening;
                    d.DropDownOpening += ngHudMenuOpening;
                    if (!d.HasDropDownItems) continue;
                    d.DropDown.Renderer = r;
                    ngHudMenu(d.DropDownItems, r);
                }
        }

        private ToolStripRenderer _ngMenuRenderer;   // also for menus created later (Modern VFO band menu)

        private static void ngHudMenuOpening(object sender, EventArgs e)
        {
            ToolStripDropDownItem d = (ToolStripDropDownItem)sender;
            if (!(d.Owner?.Renderer is NgMenuRenderer r)) return;
            d.DropDown.Renderer = r;
            ngHudMenu(d.DropDownItems, r);
        }

        private sealed class NgMenuRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                bool drop = e.ToolStrip is ToolStripDropDown;
                using (SolidBrush b = new SolidBrush(drop ? ModernTheme.PanelBack : ModernTheme.Back)) e.Graphics.FillRectangle(b, e.AffectedBounds);
                if (!drop)
                {
                    ModernTheme.ScanLines(e.Graphics, e.AffectedBounds);
                    if (e.ToolStrip is MenuStrip) ngPaintEdge(e.Graphics, e.ToolStrip.Width, 0);   // the same line above the menu tiles
                }
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (e.ToolStrip is ToolStripDropDown)
                    using (Pen p = new Pen(ModernTheme.BorderStrong)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(ModernTheme.PanelBack)) e.Graphics.FillRectangle(b, e.AffectedBounds);
            }

            // top-level items of the main menu: small tiles like the dock buttons (caption drawn here too)
            private static bool isTile(ToolStripItem i) => !i.IsOnDropDown && i.Owner is MenuStrip;

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (isTile(e.Item))
                {
                    // a coloured caption (Diversity / SonoBus state) lights the tile in its colour
                    Color c = e.Item.ForeColor;
                    bool coloured = c.GetSaturation() >= 0.25f && c.GetBrightness() >= 0.4f;
                    bool on = e.Item.Pressed || (e.Item is ToolStripMenuItem mi && mi.Checked);
                    ModernTheme.DrawButton(e.Graphics, new Rectangle(Point.Empty, e.Item.Size), e.Item.Text.Replace("&", "").ToUpperInvariant(),
                        e.Item.Font, null, on ? 1f : coloured ? 0.5f : 0f, e.Item.Selected ? 1f : 0f, e.Item.Pressed, e.Item.Enabled,
                        coloured ? c : ModernTheme.Accent, ContentAlignment.MiddleCenter);
                    return;
                }
                if (!e.Item.Enabled || (!e.Item.Selected && !e.Item.Pressed)) return;
                Rectangle r = new Rectangle(2, 1, e.Item.Width - 5, e.Item.Height - 3);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(46, ModernTheme.Accent))) e.Graphics.FillRectangle(b, r);
                using (Pen p = new Pen(Color.FromArgb(170, ModernTheme.Accent))) e.Graphics.DrawRectangle(p, r);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (isTile(e.Item)) return;
                // grey/white/dark captions take the HUD text colour; bright coloured ones (Diversity on) keep theirs
                Color c = e.TextColor;
                if (!e.Item.Enabled) e.TextColor = ModernTheme.TextDisabled;
                else if (e.Item.Selected || e.Item.Pressed) e.TextColor = Color.White;
                else if (c.GetSaturation() < 0.25f || c.GetBrightness() < 0.4f) e.TextColor = ModernTheme.Text;
                base.OnRenderItemText(e);
            }

            // check marks: HUD box instead of the light system square with a black tick
            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                Rectangle r = new Rectangle(e.ImageRectangle.X - 1, e.ImageRectangle.Y - 1, e.ImageRectangle.Width + 1, e.ImageRectangle.Height + 1);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(60, ModernTheme.Accent))) e.Graphics.FillRectangle(b, r);
                using (Pen p = new Pen(ModernTheme.Accent)) e.Graphics.DrawRectangle(p, r);
                TextRenderer.DrawText(e.Graphics, "\uE73E", ModernTheme.IconFont(Math.Max(8f, r.Height * 0.62f)), r,
                    e.Item.Enabled ? Color.White : ModernTheme.TextDisabled,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using (Pen p = new Pen(ModernTheme.Border)) e.Graphics.DrawLine(p, 28, y, e.Item.Width - 4, y);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = ModernTheme.Accent;
                base.OnRenderArrow(e);
            }
        }

        // a panel's bounds grown to children that stick out of it (FreeDV under panelMode)
        private static Rectangle ngBounds(Control c)
        {
            Rectangle r = c.Bounds;
            foreach (Control k in c.Controls)
                if (k.Visible) r = Rectangle.Union(r, new Rectangle(c.Left + k.Left, c.Top + k.Top, k.Width, k.Height));
            return r;
        }

        private static void ngHudPlate(Control c)
        {
            if (c.BackgroundImage != null) c.BackgroundImage = null;
            if (c.BackColor != ModernTheme.PanelBack) c.BackColor = ModernTheme.PanelBack;
        }

        // top bar / dock: glass with scan lines and a glowing edge line toward the display
        private static void ngPaintBar(Graphics g, Control bar, bool edgeOnTop)
        {
            Rectangle r = bar.ClientRectangle;
            // a lighter far surface, so the shadows of the floating panels show on it
            using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(r, ModernTheme.Lerp(ModernTheme.PanelBack, ModernTheme.Border, 0.3f), ModernTheme.Back,
                edgeOnTop ? 90f : 270f))
                g.FillRectangle(b, r);
            ModernTheme.ScanLines(g, r);
            ngPaintEdge(g, r.Width, edgeOnTop ? 0 : r.Bottom - 1);
        }

        // the glowing edge line: brighter in the middle, fading out to the sides
        private static void ngPaintEdge(Graphics g, int width, int y)
        {
            Rectangle r = new Rectangle(0, 0, width, 1);
            using (Pen p = new Pen(ModernTheme.Border)) g.DrawLine(p, 0, y, r.Width, y);
            // brighter centre section of the edge, fading out to the sides
            using (var lb = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, r.Width), 1), Color.Transparent, Color.Transparent, 0f))
            {
                var blend = new System.Drawing.Drawing2D.ColorBlend
                {
                    Colors = new[] { Color.FromArgb(0, ModernTheme.Accent), Color.FromArgb(200, ModernTheme.Accent), Color.FromArgb(0, ModernTheme.Accent) },
                    Positions = new[] { 0f, 0.5f, 1f },
                };
                lb.InterpolationColors = blend;
                using (Pen p = new Pen(lb, 1f)) g.DrawLine(p, 0, y, r.Width, y);
            }
        }

        private static void ngMove(Control c, int x, int y)
        {
            Point p = new Point(x, y);
            if (c.Location != p) c.Location = p;
        }
    }
}
