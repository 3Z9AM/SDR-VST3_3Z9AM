using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace Thetis
{
    // "Modern VFO" look for the VFO A / VFO B group boxes (Setup > Appearance > General).
    // Only a visual layer: a custom drawn frequency display sits over the original
    // MSD/LSD text boxes and the group gets a rounded skin-style background. Tuning, hover
    // digits, the mouse wheel, typing a frequency (click) and CAT still go through the
    // original controls and fields (txtVFOxFreq, vfox_hover_digit, Console_MouseWheel).
    // Turning the option off restores every changed property.
    public partial class Console
    {
        private bool _modernVFO;
        private ModernVfoDisplay _modernVfoA, _modernVfoB;
        private readonly Dictionary<Control, Color> _modernVfoSavedBackColors = new Dictionary<Control, Color>();
        private readonly Dictionary<Control, bool> _modernVfoSavedVisible = new Dictionary<Control, bool>();

        // sci-fi HUD palette (ModernTheme): cyan VFO A, pale cyan VFO B, red/magenta TX
        private static Color ModernVfoColorA => ModernTheme.Accent;
        private static Color ModernVfoColorB => ModernTheme.AccentSoft;
        private static Color ModernVfoColorTX => ModernTheme.Danger;
        private static Color ModernVfoColorDim => ModernTheme.TextDim;
        private static Color ModernPanelBack => ModernTheme.PanelBack;
        private static Color ModernPanelBorder => ModernTheme.Border;
        private const int ModernGlassRadius = 7;

        public bool ModernVFO
        {
            get { return _modernVFO; }
            set
            {
                if (value == _modernVFO) return;
                _modernVFO = value;
                if (_modernVFO) enableModernVFO();
                else disableModernVFO();
            }
        }

        private void enableModernVFO()
        {
            if (_modernVfoA == null)
            {
                _modernVfoA = createModernVfo(false);
                _modernVfoB = createModernVfo(true);
                grpVFOA.Paint += grpVFOModern_Paint;
                grpVFOB.Paint += grpVFOModern_Paint;
                grpVFOA.SizeChanged += (s, e) => updateModernVfoRegion(grpVFOA);
                grpVFOB.SizeChanged += (s, e) => updateModernVfoRegion(grpVFOB);
                MoxChangeHandlers += (rx, oldMox, newMox) => invalidateModernVfo();
                chkVFOATX.CheckedChanged += (s, e) => invalidateModernVfo();
                chkVFOBTX.CheckedChanged += (s, e) => invalidateModernVfo();
                // the designer leaves the CheckBox default (MiddleLeft); the modern button paints the caption centred only when asked
                chkVFOATX.TextAlign = chkVFOBTX.TextAlign = ContentAlignment.MiddleCenter;
                foreach (CheckBox cb in new CheckBox[] { chkRIT, chkXIT, chkVFOSplit, chkVFOLock, chkVFOBLock })
                    cb.CheckedChanged += (s, e) => invalidateModernVfo();
                foreach (GroupBoxTS g in new[] { grpVFOA, grpVFOB })
                {
                    g.MouseDown += modernBandBar_MouseDown;
                    g.MouseMove += modernBandBar_MouseMove;
                    g.MouseLeave += (s, e) => { if (_modernVFO) ((Control)s).Cursor = Cursors.Default; };
                }
            }

            applyModernVfoStyle(grpVFOA, panelVFOAHover, txtVFOABand, panelVFOASubHover);
            applyModernVfoStyle(grpVFOB, panelVFOBHover, txtVFOBBand, null);
            updateModernVfoVisibility(_modernVfoA);
            updateModernVfoVisibility(_modernVfoB);
            updateModernVfoRegion(grpVFOA);
            updateModernVfoRegion(grpVFOB);
            grpVFOA.Invalidate(true);
            grpVFOB.Invalidate(true);
        }

        private void disableModernVFO()
        {
            if (_modernVfoA == null) return;

            _modernVfoA.Visible = false;
            _modernVfoB.Visible = false;
            foreach (KeyValuePair<Control, Color> kv in _modernVfoSavedBackColors)
                if (!kv.Key.IsDisposed) kv.Key.BackColor = kv.Value;
            foreach (KeyValuePair<Control, bool> kv in _modernVfoSavedVisible)
                if (!kv.Key.IsDisposed) kv.Key.Visible = kv.Value;
            _modernVfoSavedBackColors.Clear();
            _modernVfoSavedVisible.Clear();

            setModernVfoRegion(grpVFOA, null);
            setModernVfoRegion(grpVFOB, null);
            grpVFOA.Invalidate(true);
            grpVFOB.Invalidate(true);
        }

        private ModernVfoDisplay createModernVfo(bool isB)
        {
            GroupBoxTS grp = isB ? grpVFOB : grpVFOA;
            TextBoxTS freq = isB ? txtVFOBFreq : txtVFOAFreq;
            TextBoxTS msd = isB ? txtVFOBMSD : txtVFOAMSD;
            TextBoxTS lsd = isB ? txtVFOBLSD : txtVFOALSD;

            ModernVfoDisplay d = new ModernVfoDisplay(this, isB);
            d.Name = isB ? "modernVfoB" : "modernVfoA";
            d.Bounds = freq.Bounds;
            d.Visible = false;
            grp.Controls.Add(d);

            // on top of the MSD/LSD boxes and the small mode/filter/MUTE/APF labels: the
            // display draws those as pills itself (their text/visibility is read from them)
            d.BringToFront();
            foreach (Label l in modernVfoStatusLabels(isB))
            {
                l.TextChanged += (s, e) => modernVfoStatusChanged(d);
                l.VisibleChanged += (s, e) => modernVfoStatusChanged(d);
            }

            freq.TextChanged += (s, e) =>
            {
                d.Invalidate();
                if (_modernVFO) grp.Invalidate(modernBandBarRect(grp));
            };
            freq.LocationChanged += (s, e) => d.Bounds = freq.Bounds;
            freq.SizeChanged += (s, e) => d.Bounds = freq.Bounds;
            freq.GotFocus += (s, e) => updateModernVfoVisibility(d);
            freq.LostFocus += (s, e) => updateModernVfoVisibility(d);
            msd.ForeColorChanged += (s, e) => d.Invalidate();
            return d;
        }

        private void applyModernVfoStyle(GroupBoxTS grp, Control hover, Control band, Control subHover)
        {
            // the hover underline is drawn by the modern display itself
            _modernVfoSavedVisible[hover] = hover.Visible;
            hover.Visible = false;

            // text boxes can not be transparent - give them the panel colour
            _modernVfoSavedBackColors[band] = band.BackColor;
            band.BackColor = ModernPanelBack;
            if (subHover != null)
            {
                _modernVfoSavedBackColors[subHover] = subHover.BackColor;
                subHover.BackColor = ModernPanelBack;
            }
        }

        private void updateModernVfoVisibility(ModernVfoDisplay d)
        {
            if (d == null) return;
            TextBoxTS freq = d.IsB ? txtVFOBFreq : txtVFOAFreq;
            d.Visible = _modernVFO && !freq.Focused;   // typing a frequency shows the original box
        }

        private void invalidateModernVfo()
        {
            if (!_modernVFO || _modernVfoA == null || IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new MethodInvoker(invalidateModernVfo));
                return;
            }
            // MOX state settles after the handlers run
            BeginInvoke(new MethodInvoker(() =>
            {
                if (!_modernVFO) return;
                grpVFOA.Invalidate(true);
                grpVFOB.Invalidate(true);
            }));
        }

        private bool modernVfoTx(bool isB)
        {
            return _mox && (isB ? VFOBTX : VFOATX);
        }

        private bool modernVfoDim(bool isB)
        {
            Color fc = (isB ? txtVFOBMSD : txtVFOAMSD).ForeColor;
            return vfo_text_dark_color.ToArgb() != vfo_text_light_color.ToArgb() &&
                   fc.ToArgb() == vfo_text_dark_color.ToArgb();
        }

        private Color modernVfoAccent(bool isB)
        {
            if (modernVfoTx(isB)) return ModernVfoColorTX;
            if (modernVfoDim(isB)) return ModernVfoColorDim;
            return isB ? ModernVfoColorB : ModernVfoColorA;
        }

        private static Color lerpColor(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // HUD plates have cut corners instead of round ones
        private static GraphicsPath roundedRect(RectangleF r, float radius) => ModernTheme.Chamfer(r, radius * 1.4f);

        private void updateModernVfoRegion(GroupBoxTS grp)
        {
            if (!_modernVFO || grp.Width < 2 * ModernGlassRadius || grp.Height < 2 * ModernGlassRadius) return;
            using (GraphicsPath p = roundedRect(new RectangleF(0, 0, grp.Width, grp.Height), ModernGlassRadius))
                setModernVfoRegion(grp, new Region(p));
        }

        private static void setModernVfoRegion(Control c, Region r)
        {
            Region old = c.Region;
            c.Region = r;
            old?.Dispose();
        }

        private static Font _modernCaptionFont;
        private static Font ModernCaptionFont => _modernCaptionFont ?? (_modernCaptionFont = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Point));

        // skin-style panel drawn over the GroupBox frame; transparent children (the modern
        // display) get it through their parent-background painting
        private void grpVFOModern_Paint(object sender, PaintEventArgs e)
        {
            if (!_modernVFO) return;
            GroupBoxTS grp = (GroupBoxTS)sender;
            bool isB = grp == grpVFOB;
            int w = grp.Width, h = grp.Height;
            if (w < 2 * ModernGlassRadius || h < 2 * ModernGlassRadius) return;

            Graphics g = e.Graphics;
            Color accent = modernVfoAccent(isB);
            g.Clear(ModernPanelBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // glass pane, glowing in the VFO colour (red) while this VFO transmits
            ModernTheme.GlassFrame(g,new RectangleF(0.5f, 0.5f, w - 1f, h - 1f), modernVfoTx(isB) ? accent : ModernTheme.BorderStrong,
                ModernGlassRadius * 1.4f, modernVfoTx(isB));
            using (SolidBrush b = new SolidBrush(Color.FromArgb(190, accent)))
                g.DrawString(isB ? "VFO B" : "VFO A", ModernCaptionFont, b, new PointF(ModernGlassRadius, 1f));

            drawModernStatusPills(g, grp, isB, accent);
            drawModernBandBar(g, grp, isB, accent);
        }

        // ---- status pills (caption row) ----

        private Label[] modernVfoStatusLabels(bool isB)
        {
            return isB
                ? new Label[] { lblRX2ModeLabel, lblRX2FilterLabel, lblRX2MuteVFOB, lblRX2APF, lblRX2ModeBigLabel }
                : new Label[] { lblModeLabel, lblFilterLabel, lblRX1MuteVFOA, lblRX1APF, lblModeBigLabel };
        }

        private void modernVfoStatusChanged(ModernVfoDisplay d)
        {
            if (!_modernVFO) return;
            // console code brings MUTE/APF labels to the front at times - stay above them
            if (d.Parent != null && d.Parent.Controls.GetChildIndex(d) != 0) d.BringToFront();
            invalidateModernVfo();
        }

        private static Font _modernPillFont;
        private static Font ModernPillFont => _modernPillFont ?? (_modernPillFont = new Font("Segoe UI", 6.75f, FontStyle.Bold, GraphicsUnit.Point));

        // rounded tag; filled = solid accent with dark text, else outlined. Returns its width.
        private static float drawModernPill(Graphics g, string text, float x, float y, float h, Color accent, bool filled, bool rightAligned)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            Font f = ModernPillFont;
            SizeF ts = g.MeasureString(text, f, 200, StringFormat.GenericTypographic);
            float w = (float)Math.Ceiling(ts.Width) + 8;
            if (rightAligned) x -= w;
            RectangleF r = new RectangleF(x, y, w, h);
            using (GraphicsPath p = roundedRect(r, h / 2f))
            {
                if (filled)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(215, accent))) g.FillPath(b, p);
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(32, accent))) g.FillPath(b, p);
                    using (Pen pen = new Pen(Color.FromArgb(150, accent), 1f)) g.DrawPath(pen, p);
                }
            }
            using (SolidBrush tb = new SolidBrush(filled ? Color.FromArgb(18, 18, 20) : lerpColor(accent, Color.White, 0.25f)))
                g.DrawString(text, f, tb, x + 4, y + (h - ts.Height) / 2f, StringFormat.GenericTypographic);
            return w;
        }

        private void drawModernStatusPills(Graphics g, GroupBoxTS grp, bool isB, Color accent)
        {
            // right to left along the caption row; only states that are on
            List<(string text, Color colour, bool filled)> pills = new List<(string, Color, bool)>();
            Label mute = isB ? lblRX2MuteVFOB : lblRX1MuteVFOA;
            Label apf = isB ? lblRX2APF : lblRX1APF;
            bool txVfo = chkVFOSplit.Checked ? isB : !isB;   // the VFO that transmits

            if (mute.Visible) pills.Add(("MUTE", ModernVfoColorTX, true));
            if (isB ? chkVFOBLock.Checked : chkVFOLock.Checked) pills.Add(("LOCK", accent, true));
            if (chkVFOSplit.Checked) pills.Add(("SPLIT", accent, false));
            if (txVfo && chkXIT.Checked) pills.Add(("XIT", accent, false));
            if (!isB && chkRIT.Checked) pills.Add(("RIT", accent, false));
            if (apf.Visible && !string.IsNullOrEmpty(apf.Text)) pills.Add((apf.Text.Trim(), accent, false));

            float x = grp.Width - ModernGlassRadius;
            foreach (var p in pills)
                x -= drawModernPill(g, p.text, x, 2f, 11f, p.colour, p.filled, true) + 3;
        }

        // ---- band position bar (IARU Region 1 plan) ----

        private enum ModernSeg { CW, Digi, Beacon, Phone }

        // band edges and segment starts in kHz
        private static readonly (double lo, double hi, (double start, ModernSeg kind)[] segs)[] ModernBands =
        {
            (1810, 2000, new[] { (1810.0, ModernSeg.CW), (1838.0, ModernSeg.Digi), (1843.0, ModernSeg.Phone) }),
            (3500, 3800, new[] { (3500.0, ModernSeg.CW), (3570.0, ModernSeg.Digi), (3600.0, ModernSeg.Phone) }),
            (5351.5, 5366.5, new[] { (5351.5, ModernSeg.CW), (5354.0, ModernSeg.Phone), (5366.0, ModernSeg.CW) }),
            (7000, 7200, new[] { (7000.0, ModernSeg.CW), (7040.0, ModernSeg.Digi), (7050.0, ModernSeg.Phone) }),
            (10100, 10150, new[] { (10100.0, ModernSeg.CW), (10130.0, ModernSeg.Digi) }),
            (14000, 14350, new[] { (14000.0, ModernSeg.CW), (14070.0, ModernSeg.Digi), (14099.0, ModernSeg.Beacon), (14101.0, ModernSeg.Phone) }),
            (18068, 18168, new[] { (18068.0, ModernSeg.CW), (18095.0, ModernSeg.Digi), (18109.0, ModernSeg.Beacon), (18111.0, ModernSeg.Phone) }),
            (21000, 21450, new[] { (21000.0, ModernSeg.CW), (21070.0, ModernSeg.Digi), (21149.0, ModernSeg.Beacon), (21151.0, ModernSeg.Phone) }),
            (24890, 24990, new[] { (24890.0, ModernSeg.CW), (24915.0, ModernSeg.Digi), (24929.0, ModernSeg.Beacon), (24931.0, ModernSeg.Phone) }),
            (28000, 29700, new[] { (28000.0, ModernSeg.CW), (28070.0, ModernSeg.Digi), (28190.0, ModernSeg.Beacon), (28225.0, ModernSeg.Phone) }),
            (50000, 52000, new[] { (50000.0, ModernSeg.CW), (50100.0, ModernSeg.Phone), (50300.0, ModernSeg.Digi), (50400.0, ModernSeg.Phone) }),
            (144000, 146000, new[] { (144000.0, ModernSeg.CW), (144150.0, ModernSeg.Phone), (144400.0, ModernSeg.Beacon), (144500.0, ModernSeg.Phone) }),
        };

        private static Color modernSegColour(ModernSeg s)
        {
            switch (s)
            {
                case ModernSeg.CW: return Color.FromArgb(95, 165, 225);
                case ModernSeg.Digi: return Color.FromArgb(175, 125, 235);
                case ModernSeg.Beacon: return Color.FromArgb(200, 200, 200);
                default: return Color.FromArgb(221, 167, 74);
            }
        }

        private static Rectangle modernBandBarRect(Control grp)
        {
            return new Rectangle(ModernGlassRadius, grp.Height - 6, Math.Max(1, grp.Width - 2 * ModernGlassRadius), 5);
        }

        private void drawModernBandBar(Graphics g, GroupBoxTS grp, bool isB, Color accent)
        {
            Rectangle area = modernBandBarRect(grp);
            RectangleF track = new RectangleF(area.X, area.Y + 1.5f, area.Width, 2f);
            double khz = (isB ? VFOBFreq : VFOAFreq) * 1000.0;

            var band = ModernBands.FirstOrDefault(b => khz >= b.lo && khz <= b.hi);
            if (band.segs == null)
            {
                // out of an amateur band: just a faint track
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillRectangle(tb, track);
                return;
            }

            float span = (float)(band.hi - band.lo);
            for (int i = 0; i < band.segs.Length; i++)
            {
                double s0 = band.segs[i].start;
                double s1 = i + 1 < band.segs.Length ? band.segs[i + 1].start : band.hi;
                float x0 = track.X + (float)((s0 - band.lo) / span) * track.Width;
                float x1 = track.X + (float)((s1 - band.lo) / span) * track.Width;
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(120, modernSegColour(band.segs[i].kind))))
                    g.FillRectangle(sb, x0, track.Y, Math.Max(1f, x1 - x0 - 1f), track.Height);
            }

            // position marker
            float mx = track.X + (float)((khz - band.lo) / span) * track.Width;
            using (SolidBrush glow = new SolidBrush(Color.FromArgb(70, accent)))
                g.FillRectangle(glow, mx - 2.5f, area.Y, 5f, area.Height);
            using (SolidBrush mb = new SolidBrush(lerpColor(accent, Color.White, 0.5f)))
                g.FillRectangle(mb, mx - 1f, area.Y, 2f, area.Height);
        }

        // ---- band bar: click = tune there, right click (VFO A) = band menu ----

        private ContextMenuStrip _modernBandMenu;

        private bool modernBandBarHit(Control grp, Point p)
        {
            Rectangle r = modernBandBarRect(grp);
            return p.Y >= r.Top - 3 && p.X >= r.Left && p.X <= r.Right;
        }

        private void modernBandBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_modernVFO) return;
            Control grp = (Control)sender;
            Cursor want = modernBandBarHit(grp, e.Location) ? Cursors.Hand : Cursors.Default;
            if (grp.Cursor != want) grp.Cursor = want;
        }

        private void modernBandBar_MouseDown(object sender, MouseEventArgs e)
        {
            if (!_modernVFO) return;
            GroupBoxTS grp = (GroupBoxTS)sender;
            if (!modernBandBarHit(grp, e.Location)) return;
            bool isB = grp == grpVFOB;

            if (e.Button == MouseButtons.Right && !isB)
            {
                showModernBandMenu(grp, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (isB ? chkVFOBLock.Checked : chkVFOLock.Checked) return;   // locked VFO stays put

            double khz = (isB ? VFOBFreq : VFOAFreq) * 1000.0;
            var band = ModernBands.FirstOrDefault(b => khz >= b.lo && khz <= b.hi);
            if (band.segs == null) return;
            Rectangle r = modernBandBarRect(grp);
            double frac = Math.Max(0, Math.Min(1, (e.X - r.Left) / (double)Math.Max(1, r.Width)));
            double target = Math.Round(band.lo + frac * (band.hi - band.lo));
            target = Math.Max(band.lo, Math.Min(band.hi, target));
            if (isB) VFOBFreq = target / 1000.0;
            else VFOAFreq = target / 1000.0;
        }

        private static readonly (string name, Band band)[] ModernBandMenuItems =
        {
            ("160 m", Band.B160M), ("80 m", Band.B80M), ("60 m", Band.B60M), ("40 m", Band.B40M), ("30 m", Band.B30M),
            ("20 m", Band.B20M), ("17 m", Band.B17M), ("15 m", Band.B15M), ("12 m", Band.B12M), ("10 m", Band.B10M),
            ("6 m", Band.B6M), ("2 m", Band.B2M),
        };

        private void showModernBandMenu(Control grp, Point at)
        {
            if (_modernBandMenu == null)
            {
                _modernBandMenu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
                if (_ngMenuRenderer != null) _modernBandMenu.Renderer = _ngMenuRenderer;
                foreach (var it in ModernBandMenuItems)
                {
                    Band b = it.band;
                    ToolStripMenuItem mi = new ToolStripMenuItem(it.name) { Tag = b };
                    mi.Click += (s, e) => BandPreChangeHandlers?.Invoke(1, b);   // same path as the band buttons
                    _modernBandMenu.Items.Add(mi);
                }
            }
            foreach (ToolStripMenuItem mi in _modernBandMenu.Items)
            {
                Band b = (Band)mi.Tag;
                mi.Checked = b == RX1Band;
                mi.Visible = b != Band.B2M || radBand2.Enabled;   // 2 m only where the band button is usable
            }
            _modernBandMenu.Show(grp, at);
        }

        // ---- the frequency display ----
        private sealed class ModernVfoDisplay : Control
        {
            private readonly Console _c;
            public readonly bool IsB;
            private RectangleF[] _cells = new RectangleF[10];
            private int _hover = -1;

            private static readonly string[] FontNames = { "Bahnschrift SemiBold", "Bahnschrift", "Segoe UI Semibold", "Segoe UI" };
            private static FontFamily _family;

            public ModernVfoDisplay(Console c, bool isB)
            {
                _c = c;
                IsB = isB;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                SetStyle(ControlStyles.Selectable, false);
                BackColor = Color.Transparent;
                Cursor = Cursors.Default;
            }

            internal static FontFamily Family
            {
                get
                {
                    if (_family != null) return _family;
                    foreach (string n in FontNames)
                    {
                        try { _family = new FontFamily(n); return _family; }
                        catch (ArgumentException) { }
                    }
                    _family = FontFamily.GenericSansSerif;
                    return _family;
                }
            }

            private double Frequency => IsB ? _c.VFOBFreq : _c.VFOAFreq;

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                int W = ClientSize.Width, H = ClientSize.Height;
                if (W < 20 || H < 10) return;

                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                long hz = (long)Math.Round(Math.Abs(Frequency) * 1e6);
                if (hz > 9999999999L) hz = 9999999999L;
                string digits = hz.ToString("D10");     // 4 MHz, 3 kHz, 3 Hz digits

                bool tx = _c.modernVfoTx(IsB);
                bool dim = !tx && _c.modernVfoDim(IsB);
                Color accent = _c.modernVfoAccent(IsB);
                bool smallHz = _c.small_lsd;

                // ---- layout: right aligned, Hz group smaller ----
                FontFamily fam = Family;
                FontStyle style = fam.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
                float ascent = fam.GetCellAscent(style) / (float)fam.GetEmHeight(style);

                // mode / filter pills on the left (they replace the small labels)
                float pillsRight = 0;
                {
                    Label modeL = IsB ? _c.lblRX2ModeLabel : _c.lblModeLabel;
                    Label filtL = IsB ? _c.lblRX2FilterLabel : _c.lblFilterLabel;
                    string modeT = modeL.Text?.Trim(), filtT = filtL.Text?.Trim();
                    const float ph = 13f;
                    float py = (H - (2 * ph + 3)) / 2f;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    float w1 = drawModernPill(g, modeT, 2, py, ph, accent, true, false);
                    float w2 = drawModernPill(g, filtT, 2, py + ph + 3, ph, accent, false, false);
                    float pw = Math.Max(w1, w2);
                    if (pw > 0) pillsRight = 2 + pw + 6;
                }

                // leading zeros are not drawn (that room holds the pills)
                int firstSignificant = 3;
                for (int i = 0; i < 3; i++) if (digits[i] != '0') { firstSignificant = i; break; }
                int bigShown = 7 - firstSignificant;

                float avail = W - 2 - pillsRight;
                float big = H * 0.92f;
                float cellK = 0.56f, sepK = 0.26f, smallK = smallHz ? 0.70f : 1f;
                float total = big * (bigShown * cellK + 2 * sepK + 3 * cellK * smallK);
                if (total > avail) big *= avail / total;
                float small = big * smallK;
                float cellBig = big * cellK, cellSmall = small * cellK, sep = big * sepK;
                float baseline = H / 2f + big * ascent * 0.42f;

                float x = W - 2 - (bigShown * cellBig + 2 * sep + 3 * cellSmall);
                for (int i = 0; i < 10; i++)
                {
                    if (i < firstSignificant) { _cells[i] = new RectangleF(x, 0, 0, H); continue; }
                    float cw = i >= 7 ? cellSmall : cellBig;
                    _cells[i] = new RectangleF(x, 0, cw, H);
                    x += cw;
                    if (i == 3 || i == 6) x += sep;
                }

                // hover highlight
                if (_hover >= 3 && _hover <= 9 && !dim)
                {
                    RectangleF hc = _cells[_hover];
                    using (GraphicsPath p = roundedRect(new RectangleF(hc.X - 1, 2, hc.Width + 2, H - 4), 4))
                    using (SolidBrush hb = new SolidBrush(Color.FromArgb(28, 255, 255, 255)))
                        g.FillPath(hb, p);
                    using (SolidBrush ub = new SolidBrush(Color.FromArgb(230, accent)))
                        g.FillRectangle(ub, hc.X + 1, H - 3, hc.Width - 2, 2);
                }

                using (GraphicsPath main = new GraphicsPath(FillMode.Winding))
                using (GraphicsPath hzPath = new GraphicsPath(FillMode.Winding))
                using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
                {
                    sf.Alignment = StringAlignment.Center;
                    for (int i = 0; i < 10; i++)
                    {
                        if (i < firstSignificant) continue;
                        float size = i >= 7 ? small : big;
                        float top = baseline - size * ascent;
                        GraphicsPath target = i >= 7 ? hzPath : main;
                        target.AddString(digits[i].ToString(), fam, (int)style, size,
                            new RectangleF(_cells[i].X, top, _cells[i].Width, size * 1.3f), sf);
                    }

                    // group separators as dots on the baseline
                    float dot = Math.Max(2f, big * 0.09f);
                    for (int k = 0; k < 2; k++)
                    {
                        float cx = (k == 0 ? _cells[3].Right : _cells[6].Right) + sep / 2f;
                        main.AddEllipse(cx - dot / 2f, baseline - dot, dot, dot);
                    }

                    if (!dim)
                    {
                        float[] widths = tx ? new[] { 9f, 5f, 2.5f } : new[] { 7f, 4f, 2f };
                        int glowA = tx ? 48 : 30;
                        foreach (float pw in widths)
                        {
                            using (Pen glow = new Pen(Color.FromArgb(glowA, accent), pw) { LineJoin = LineJoin.Round })
                            {
                                g.DrawPath(glow, main);
                                g.DrawPath(glow, hzPath);
                            }
                        }
                    }

                    Color topCol = dim ? accent : lerpColor(accent, Color.White, 0.45f);
                    using (LinearGradientBrush fill = new LinearGradientBrush(new PointF(0, baseline - big * ascent),
                        new PointF(0, baseline), topCol, accent))
                        g.FillPath(fill, main);
                    using (SolidBrush hzBrush = new SolidBrush(Color.FromArgb(dim ? 200 : 185, accent)))
                        g.FillPath(hzBrush, hzPath);
                }
            }

            private int digitAt(int x)
            {
                for (int i = 0; i < 10; i++)
                {
                    RectangleF r = _cells[i];
                    float right = i < 9 ? _cells[i + 1].Left : r.Right + 2;
                    if (x >= r.Left - (i == 0 ? 4 : 0) && x < right) return i;
                }
                return -1;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                // also when Thetis is not the active window: Windows still sends it the wheel
                // digits 0..2 (100/10 MHz) give a step the wheel tuning ignores, as before
                int d = digitAt(e.X);
                if (IsB) _c.vfob_hover_digit = d; else _c.vfoa_hover_digit = d;
                if (d != _hover) { _hover = d; Invalidate(); }
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (IsB) _c.vfob_hover_digit = -1; else _c.vfoa_hover_digit = -1;
                if (_hover != -1) { _hover = -1; Invalidate(); }
            }

            // click = type a frequency in the original box (as clicking the MSD/LSD did)
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                TextBoxTS msd = IsB ? _c.txtVFOBMSD : _c.txtVFOAMSD;
                TextBoxTS lsd = IsB ? _c.txtVFOBLSD : _c.txtVFOALSD;
                TextBoxTS freq = IsB ? _c.txtVFOBFreq : _c.txtVFOAFreq;
                msd.Visible = false;
                lsd.Visible = false;
                Visible = false;
                freq.Focus();
                freq.SelectAll();
            }
        }
    }
}
