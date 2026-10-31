//  ModernTheme.cs
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
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Thetis
{
    // "Modern UI" (Setup > Appearance > General): one palette, one font and one way of
    // drawing buttons for the whole console. Only a paint layer over the skin - the skin
    // still sets its images, the modern painter draws over them while Enabled is true,
    // so switching it off gives the exact skin look back.
    internal static class ModernTheme
    {
        public static bool Enabled { get; private set; }
        public static event Action Changed;

        public static void SetEnabled(bool on)
        {
            if (on == Enabled) return;
            Enabled = on;
            Changed?.Invoke();
        }

        // ---- palette: sci-fi HUD - cyan light on blue-black glass, red/magenta = transmit and
        // warnings only, green = OK only ----
        public static readonly Color Danger = Color.FromArgb(255, 64, 96);
        public static readonly Color Ok = Color.FromArgb(40, 240, 170);
        public static readonly Color Violet = Color.FromArgb(190, 110, 255);
        public static readonly Color Warn = Color.FromArgb(255, 196, 60);

        // the cyan design colours; SetAccent turns them to the hue of another accent colour
        public static readonly Color DefaultAccent = Color.FromArgb(0, 214, 255);
        public static Color Accent = DefaultAccent;
        public static Color AccentSoft = Color.FromArgb(150, 236, 255);
        public static Color Back = Color.FromArgb(3, 7, 12);
        public static Color PanelBack = Color.FromArgb(5, 12, 19);
        public static Color Surface = Color.FromArgb(8, 22, 32);
        public static Color SurfaceHover = Color.FromArgb(12, 38, 52);
        public static Color SurfacePressed = Color.FromArgb(4, 12, 18);
        public static Color Border = Color.FromArgb(22, 74, 96);
        public static Color BorderStrong = Color.FromArgb(34, 124, 156);
        public static Color Text = Color.FromArgb(178, 232, 246);
        public static Color TextDim = Color.FromArgb(84, 142, 162);
        public static Color TextDisabled = Color.FromArgb(36, 70, 84);
        public static Color TextOnAccent = Color.FromArgb(0, 18, 26);

        // Setup > Appearance > General "Accent colour": read in Main before any window exists (colours
        // are copied into many controls, so a change takes effect after a restart)
        public const string AccentFile = "modern_accent.txt";
        private static float _hueShift;

        public static void SetAccent(Color accent)
        {
            if (accent.IsEmpty || accent.ToArgb() == DefaultAccent.ToArgb()) return;
            _hueShift = accent.GetHue() - DefaultAccent.GetHue();
            Accent = accent;
            AccentSoft = Lerp(accent, Color.White, 0.6f);
            Back = Tint(Back); PanelBack = Tint(PanelBack);
            Surface = Tint(Surface); SurfaceHover = Tint(SurfaceHover); SurfacePressed = Tint(SurfacePressed);
            Border = Tint(Border); BorderStrong = Tint(BorderStrong);
            Text = Tint(Text); TextDim = Tint(TextDim); TextDisabled = Tint(TextDisabled); TextOnAccent = Tint(TextOnAccent);
        }

        public static Color AccentFromFile(string path)
        {
            try
            {
                if (System.IO.File.Exists(path)) return ColorTranslator.FromHtml(System.IO.File.ReadAllText(path).Trim());
            }
            catch (Exception) { }
            return Color.Empty;
        }

        // a design colour (cyan) turned to the accent's hue, saturation and lightness kept
        public static Color Tint(Color c)
        {
            if (_hueShift == 0f) return c;
            float h = (c.GetHue() + _hueShift + 360f) % 360f, s = c.GetSaturation(), l = c.GetBrightness();
            float q = l < 0.5f ? l * (1f + s) : l + s - l * s, p = 2f * l - q;
            int ch(float t)
            {
                t = (t + 1f) % 1f;
                float v = t < 1f / 6f ? p + (q - p) * 6f * t : t < 0.5f ? q : t < 2f / 3f ? p + (q - p) * (2f / 3f - t) * 6f : p;
                return (int)Math.Round(Math.Max(0f, Math.Min(1f, v)) * 255f);
            }
            return Color.FromArgb(c.A, ch(h / 360f + 1f / 3f), ch(h / 360f), ch(h / 360f - 1f / 3f));
        }

        public const int ButtonRadius = 4;

        // ---- fonts ----
        private static readonly string[] UiFontNames = { "Segoe UI Variable Text", "Segoe UI" };
        private static readonly string[] NumberFontNames = { "Bahnschrift SemiBold", "Bahnschrift", "Segoe UI Semibold" };
        private static FontFamily _ui, _numbers;
        public static FontFamily UiFamily => _ui ?? (_ui = firstFamily(UiFontNames));
        public static FontFamily NumberFamily => _numbers ?? (_numbers = firstFamily(NumberFontNames));

        private static FontFamily firstFamily(string[] names)
        {
            foreach (string n in names)
            {
                try { return new FontFamily(n); }
                catch (ArgumentException) { }
            }
            return FontFamily.GenericSansSerif;
        }

        private static readonly Dictionary<(float, FontStyle), Font> _uiFonts = new Dictionary<(float, FontStyle), Font>();
        public static Font UiFont(float points, FontStyle style = FontStyle.Regular)
        {
            points = (float)Math.Round(points * 4) / 4f;   // few distinct sizes -> few cached fonts
            if (_uiFonts.TryGetValue((points, style), out Font f)) return f;
            FontStyle use = UiFamily.IsStyleAvailable(style) ? style : FontStyle.Regular;
            f = new Font(UiFamily, Math.Max(1f, points), use, GraphicsUnit.Point);
            _uiFonts[(points, style)] = f;
            return f;
        }

        // captions: Bahnschrift reads like instrument lettering
        private static readonly Dictionary<float, Font> _hudFonts = new Dictionary<float, Font>();
        public static Font HudFont(float points)
        {
            points = (float)Math.Round(points * 4) / 4f;
            if (_hudFonts.TryGetValue(points, out Font f)) return f;
            f = new Font(NumberFamily, Math.Max(1f, points), FontStyle.Regular, GraphicsUnit.Point);
            _hudFonts[points] = f;
            return f;
        }

        // icon glyphs: Segoe Fluent Icons (Windows 11), Segoe MDL2 Assets (Windows 10) has the same code points
        private static FontFamily _icons;
        private static readonly Dictionary<int, Font> _iconFonts = new Dictionary<int, Font>();
        public static Font IconFont(float pixels)
        {
            int px = Math.Max(6, (int)Math.Round(pixels));
            if (_iconFonts.TryGetValue(px, out Font f)) return f;
            if (_icons == null) _icons = firstFamily(new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" });
            f = new Font(_icons, px, FontStyle.Regular, GraphicsUnit.Pixel);
            _iconFonts[px] = f;
            return f;
        }

        // ---- helpers ----
        public static Color Lerp(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (d < 1f)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // sci-fi plate: top-left and bottom-right corners cut at 45 degrees, the other two only nicked
        public static GraphicsPath Chamfer(RectangleF r, float cut)
        {
            GraphicsPath p = new GraphicsPath();
            cut = Math.Max(0f, Math.Min(cut, Math.Min(r.Width, r.Height) / 2f));
            float nick = cut * 0.3f;
            p.AddPolygon(new[]
            {
                new PointF(r.Left + cut, r.Top), new PointF(r.Right - nick, r.Top), new PointF(r.Right, r.Top + nick),
                new PointF(r.Right, r.Bottom - cut), new PointF(r.Right - cut, r.Bottom), new PointF(r.Left + nick, r.Bottom),
                new PointF(r.Left, r.Bottom - nick), new PointF(r.Left, r.Top + cut),
            });
            return p;
        }

        // HUD frame: glowing chamfered outline plus bright corner brackets
        public static void HudFrame(Graphics g, RectangleF r, Color c, float cut, bool glow = true)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Chamfer(r, cut))
            {
                if (glow)
                    foreach ((float w, int a) in new[] { (6f, 18), (3f, 40) })
                        using (Pen gp = new Pen(Color.FromArgb(a, c), w) { LineJoin = LineJoin.Bevel }) g.DrawPath(gp, p);
                using (Pen pen = new Pen(Color.FromArgb(150, c), 1f)) g.DrawPath(pen, p);
            }
            float len = Math.Min(14f, Math.Min(r.Width, r.Height) / 3f);
            using (Pen br = new Pen(Lerp(c, Color.White, 0.35f), 2f))
            {
                // brackets on the two nicked corners, a bright tick beside each cut
                g.DrawLines(br, new[] { new PointF(r.Right - len, r.Top + 1), new PointF(r.Right - 1, r.Top + 1), new PointF(r.Right - 1, r.Top + len) });
                g.DrawLines(br, new[] { new PointF(r.Left + 1, r.Bottom - len), new PointF(r.Left + 1, r.Bottom - 1), new PointF(r.Left + len, r.Bottom - 1) });
                g.DrawLine(br, r.Left + cut + 3, r.Top + 1, r.Left + cut + 3 + len, r.Top + 1);
                g.DrawLine(br, r.Right - cut - 3 - len, r.Bottom - 1, r.Right - cut - 3, r.Bottom - 1);
            }
        }

        // glass panel: a faintly tinted pane lighter at the top, a sheen over the upper part,
        // a bright top edge and a border fading downwards; lit = glow in the colour (TX)
        public static void GlassFrame(Graphics g, RectangleF r, Color c, float cut, bool lit = false)
        {
            GlassFrame(g, r, c, cut, lit ? 0.3f : 0f, lit);
        }

        // tint 0..1 = how strongly the colour fills the pane (a lit button: 1), sheen 0..1 (pressed: less)
        public static void GlassFrame(Graphics g, RectangleF r, Color c, float cut, float tint, bool glow, float sheenK = 1f)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Chamfer(r, cut))
            {
                using (LinearGradientBrush b = new LinearGradientBrush(RectangleF.Inflate(r, 0f, 1f),
                    Color.FromArgb((int)(44 + 90 * tint), Lerp(c, Color.White, 0.25f)), Color.FromArgb((int)(8 + 55 * tint), c), LinearGradientMode.Vertical))
                    g.FillPath(b, p);

                GraphicsState st = g.Save();
                g.SetClip(p, CombineMode.Intersect);
                RectangleF sheen = new RectangleF(r.X, r.Y, r.Width, Math.Max(2f, r.Height * 0.42f));
                using (LinearGradientBrush b = new LinearGradientBrush(RectangleF.Inflate(sheen, 0f, 1f),
                    Color.FromArgb((int)(34 * sheenK), Color.White), Color.FromArgb(0, Color.White), LinearGradientMode.Vertical))
                    g.FillRectangle(b, sheen);
                g.Restore(st);

                if (glow)
                    foreach ((float w, int a) in new[] { (6f, 18), (3f, 40) })
                        using (Pen gp = new Pen(Color.FromArgb(a, c), w) { LineJoin = LineJoin.Bevel }) g.DrawPath(gp, p);
                using (LinearGradientBrush eb = new LinearGradientBrush(RectangleF.Inflate(r, 0f, 1f),
                    Color.FromArgb(210, Lerp(c, Color.White, 0.45f)), Color.FromArgb(70, c), LinearGradientMode.Vertical))
                using (Pen pen = new Pen(eb, 1f))
                    g.DrawPath(pen, p);
            }
            // light from above: the bottom edge in shade
            using (Pen shade = new Pen(Color.FromArgb(90, Color.Black)))
                g.DrawLine(shade, r.Left + cut * 0.3f + 1f, r.Bottom - 1f, r.Right - cut - 1f, r.Bottom - 1f);
            // reflection along the top edge, brightest in the middle
            RectangleF top = new RectangleF(r.Left + cut + 2f, r.Top + 1f, Math.Max(1f, r.Width - cut - 4f), 1f);
            using (LinearGradientBrush hb = new LinearGradientBrush(RectangleF.Inflate(top, 1f, 0f), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
            {
                hb.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(0, Color.White), Color.FromArgb((int)(140 * sheenK), Color.White), Color.FromArgb(0, Color.White) },
                    Positions = new[] { 0f, 0.5f, 1f },
                };
                g.FillRectangle(hb, top);
            }
        }

        // faint horizontal scan lines over a dark area
        public static void ScanLines(Graphics g, Rectangle r)
        {
            using (Pen p = new Pen(Color.FromArgb(10, Accent)))
                for (int y = r.Top + 1; y < r.Bottom; y += 3) g.DrawLine(p, r.Left, y, r.Right, y);
        }

        // soft drop shadow cast down onto a surface far behind a floating panel; layers grow and sink,
        // so the shadow is wide, blurred and heavier below than at the sides
        public static void DepthShadow(Graphics g, RectangleF r, float cut, int depth)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = depth; i >= 1; i--)
            {
                float k = 1f - (i - 1f) / depth;
                RectangleF s = RectangleF.Inflate(r, i * 0.8f, i * 0.6f);
                s.Offset(0f, i * 0.6f);
                using (GraphicsPath p = Chamfer(s, cut + i * 0.5f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(14 + 46 * k * k), Color.Black)))
                    g.FillPath(b, p);
            }
        }

        // first opaque background up the parent chain (what shows around a rounded face)
        public static Color EffectiveBack(Control c)
        {
            for (Control p = c?.Parent; p != null; p = p.Parent)
            {
                // skin panels show a near-black bitmap; their BackColor (often grey) is not what is seen
                if (p.BackgroundImage != null) return PanelBack;
                if (p.BackColor.A == 255) return p.BackColor;
            }
            return Back;
        }

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        // Windows 10 1809+ dark classes: combo boxes "DarkMode_CFD", scroll bars "DarkMode_Explorer";
        // dark = false puts the normal theme back. Older Windows: no effect.
        public static void SetDarkWindowTheme(Control c, bool dark)
        {
            try
            {
                if (!c.IsHandleCreated)
                {
                    // never force a window handle early (that would create the whole parent chain);
                    // apply when the control creates its own
                    if (dark && _pendingTheme.Add(c)) c.HandleCreated += pendingTheme_HandleCreated;
                    return;
                }
                if (dark) SetWindowTheme(c.Handle, c is ComboBox ? "DarkMode_CFD" : "DarkMode_Explorer", null);
                else SetWindowTheme(c.Handle, null, null);
            }
            catch (Exception) { }
        }

        private static readonly HashSet<Control> _pendingTheme = new HashSet<Control>();

        private static void pendingTheme_HandleCreated(object sender, EventArgs e)
        {
            Control c = (Control)sender;
            c.HandleCreated -= pendingTheme_HandleCreated;
            _pendingTheme.Remove(c);
            if (Enabled) SetDarkWindowTheme(c, true);
        }

        public static void SetRegion(Control c, Region r)
        {
            Region old = c.Region;
            c.Region = r;
            old?.Dispose();
        }

        // one modern button face. on = 0..1 (animated checked state), hover/pressed as the mouse is
        public static void DrawButton(Graphics g, Rectangle bounds, string text, Font font, Image image,
            float on, float hover, bool pressed, bool enabled, Color onColour, ContentAlignment align, Color? corner = null,
            string glyph = null)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // skins pack buttons edge to edge; a 1 px gap on each side keeps them apart (tiny ones stay full size)
            float inset = Math.Min(bounds.Width, bounds.Height) >= 16 ? 1.5f : 0.5f;
            RectangleF r = new RectangleF(bounds.X + inset, bounds.Y + inset, bounds.Width - 2 * inset, bounds.Height - 2 * inset);
            // the face rises toward the mouse and sinks when pressed
            float lift = !enabled || inset <= 1f ? 0f : pressed ? 1f : -hover;
            r.Offset(0f, lift);
            float cut = Math.Max(2f, Math.Min(7f, r.Height / 2.8f));
            // glass button (as the top panels): lit = the colour shining through the pane, the edge warms up on hover
            Color glass = enabled ? Lerp(Lerp(BorderStrong, onColour, 0.6f * hover), onColour, on) : Lerp(Border, Back, 0.5f);
            float tint = enabled ? on + 0.15f * hover : 0.3f * on;
            Color textC = enabled ? Lerp(Lerp(Text, Color.White, hover * 0.5f), Color.White, on)
                                  : Lerp(TextDisabled, Lerp(onColour, Back, 0.4f), on * 0.5f);

            // cover the skin bitmap edge to edge first; the cut corners take the panel colour
            using (SolidBrush under = new SolidBrush(corner ?? Back))
                g.FillRectangle(under, bounds);

            if (inset > 1f && enabled && on > 0f)
            {
                // glow around a lit button
                using (GraphicsPath sp = Chamfer(RectangleF.Inflate(r, 1f, 1f), cut + 0.5f))
                using (Pen pen = new Pen(Color.FromArgb((int)(110 * on), onColour), 1f))
                    g.DrawPath(pen, sp);
            }

            GlassFrame(g, r, glass, cut, tint, false, pressed ? 0.35f : 1f);

            // lit: a bright "LED" strip along the bottom edge
            if (enabled && on > 0f && r.Width > 3 * cut)
            {
                float lw = r.Width * 0.45f, lx = r.Left + (r.Width - lw) / 2f, ly = r.Bottom - 2.5f;
                using (Pen led = new Pen(Color.FromArgb((int)(60 * on), onColour), 4f)) g.DrawLine(led, lx, ly, lx + lw, ly);
                using (Pen led = new Pen(Color.FromArgb((int)(255 * on), Lerp(onColour, Color.White, 0.4f)), 1.5f)) g.DrawLine(led, lx, ly, lx + lw, ly);
            }

            if (image != null)
            {
                Rectangle ir = new Rectangle(bounds.X + (bounds.Width - image.Width) / 2, bounds.Y + (bounds.Height - image.Height) / 2,
                    image.Width, image.Height);
                if (enabled) g.DrawImage(image, ir);
                else ControlPaint.DrawImageDisabled(g, image, ir.X, ir.Y, Color.Transparent);
                if (string.IsNullOrEmpty(text)) return;
            }
            if (string.IsNullOrEmpty(text)) return;

            int dy = (int)Math.Round(lift);
            if (glyph != null)
            {
                // icon on the left, caption in the rest of the face
                Font gf = IconFont(bounds.Height * 0.5f);
                int gw = (int)(bounds.Height * 0.9f);
                Rectangle gr = new Rectangle(bounds.X + 6, bounds.Y + dy, gw, bounds.Height);
                TextRenderer.DrawText(g, glyph, gf, gr, enabled ? Lerp(Lerp(Accent, Color.White, 0.2f), Color.White, on) : textC,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                bounds = new Rectangle(gr.Right, bounds.Y, Math.Max(1, bounds.Right - gr.Right - 4), bounds.Height);
            }

            // shrink the font until the caption fits - skins sized these buttons for bitmap text
            Font f = font;
            Size avail = new Size(Math.Max(1, bounds.Width - 4), Math.Max(1, bounds.Height - 2));
            TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | alignFlags(align);
            for (int i = 0; i < 6; i++)
            {
                Size ts = TextRenderer.MeasureText(g, text, f, avail, flags);
                if (ts.Width <= avail.Width || f.SizeInPoints <= 6f) break;
                f = f.FontFamily.Name == NumberFamily.Name ? HudFont(f.SizeInPoints - 0.75f) : UiFont(f.SizeInPoints - 0.75f, f.Style);
            }
            Rectangle tr = new Rectangle(bounds.X + 2, bounds.Y + dy, bounds.Width - 4, bounds.Height);
            TextRenderer.DrawText(g, text, f, tr, textC, flags);
        }

        private static TextFormatFlags alignFlags(ContentAlignment a)
        {
            TextFormatFlags f;
            switch (a)
            {
                case ContentAlignment.TopLeft: case ContentAlignment.MiddleLeft: case ContentAlignment.BottomLeft:
                    f = TextFormatFlags.Left; break;
                case ContentAlignment.TopRight: case ContentAlignment.MiddleRight: case ContentAlignment.BottomRight:
                    f = TextFormatFlags.Right; break;
                default:
                    f = TextFormatFlags.HorizontalCenter; break;
            }
            return f | TextFormatFlags.VerticalCenter;
        }
    }

    // Paints button-style CheckBox / RadioButton / Button controls in the modern look while
    // ModernTheme.Enabled. Hooked once per control; when the theme is off the handlers do
    // nothing and the skin bitmap shows as before.
    internal static class ModernButtonPainter
    {
        private sealed class State
        {
            public float On;          // animated 0..1
            public float Hover;       // animated 0..1
            public bool MouseOver, Pressed;
        }

        private static readonly Dictionary<ButtonBase, State> _states = new Dictionary<ButtonBase, State>();
        private static readonly HashSet<ButtonBase> _animating = new HashSet<ButtonBase>();
        private static Timer _timer;
        private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private static double _lastTick;
        private const float AnimMs = 130f;

        // per control colour for the "on" state; red for transmit related controls
        public static Func<ButtonBase, Color> OnColourFor = b => ModernTheme.Accent;

        // paints what lies behind a button floating over its parent (new GUI dock); true = cut corners stay see-through
        public static Func<ButtonBase, Graphics, bool> PaintBehind;
        // caption; null = no known caption (skins bake some into the bitmap) -> leave the skin face
        public static Func<ButtonBase, string> CaptionFor = b => string.IsNullOrEmpty(b.Text) ? null : b.Text;
        // controls whose red BackColor means an error state (e.g. POWER without a radio)
        public static Func<ButtonBase, bool> RedMeansError = b => false;
        // optional icon glyph drawn left of the caption
        public static Func<ButtonBase, string> GlyphFor = b => null;

        public static bool IsButtonLike(Control c)
        {
            if (c is CheckBox cb) return cb.Appearance == Appearance.Button;
            if (c is RadioButton rb) return rb.Appearance == Appearance.Button;
            return c is Button;
        }

        public static void AttachTree(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is ButtonBase b && IsButtonLike(c)) Attach(b);
                if (c.HasChildren) AttachTree(c);
            }
        }

        public static void Attach(ButtonBase b)
        {
            if (_states.ContainsKey(b)) { refreshShape(b); return; }
            State s = new State { On = isOn(b) ? 1f : 0f };
            _states[b] = s;

            b.Paint += onPaint;
            b.MouseEnter += (o, e) => { s.MouseOver = true; animate(b); };
            b.MouseLeave += (o, e) => { s.MouseOver = false; s.Pressed = false; animate(b); };
            b.MouseDown += (o, e) => { if (e.Button == MouseButtons.Left) { s.Pressed = true; b.Invalidate(); } };
            b.MouseUp += (o, e) => { s.Pressed = false; b.Invalidate(); };
            b.SizeChanged += (o, e) => refreshShape(b);
            b.Disposed += (o, e) => { _states.Remove(b); _animating.Remove(b); };
            if (b is CheckBox cb) cb.CheckedChanged += (o, e) => animate(b);
            else if (b is RadioButton rb) rb.CheckedChanged += (o, e) => animate(b);
            b.BackColorChanged += (o, e) => { if (ModernTheme.Enabled) b.Invalidate(); };
            refreshShape(b);
        }

        // no window Region for the rounded corners: over the DirectX panadapter a regioned
        // child let the swap chain show through its edge rows (white specks). The corners are
        // painted in the panel colour instead.
        public static void refreshShape(ButtonBase b)
        {
            if (b.Region != null) ModernTheme.SetRegion(b, null);
        }

        public static void RefreshAll()
        {
            foreach (ButtonBase b in new List<ButtonBase>(_states.Keys))
            {
                if (b.IsDisposed) continue;
                _states[b].On = isOn(b) ? 1f : 0f;
                refreshShape(b);
                b.Invalidate();
            }
        }

        private static bool isOn(ButtonBase b)
        {
            if (b is CheckBox cb) return cb.CheckState != CheckState.Unchecked;
            if (b is RadioButton rb) return rb.Checked;
            return false;
        }

        private static void animate(ButtonBase b)
        {
            if (!ModernTheme.Enabled) return;
            _animating.Add(b);
            if (_timer == null)
            {
                _timer = new Timer { Interval = 15 };
                _timer.Tick += (o, e) => tick();
            }
            if (!_timer.Enabled)
            {
                _lastTick = _clock.Elapsed.TotalMilliseconds;
                _timer.Start();
            }
        }

        private static void tick()
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            float step = (float)((now - _lastTick) / AnimMs);
            _lastTick = now;
            List<ButtonBase> done = null;
            foreach (ButtonBase b in _animating)
            {
                if (b.IsDisposed || !_states.TryGetValue(b, out State s)) { (done ?? (done = new List<ButtonBase>())).Add(b); continue; }
                float onT = isOn(b) ? 1f : 0f, hovT = s.MouseOver ? 1f : 0f;
                s.On = approach(s.On, onT, step);
                s.Hover = approach(s.Hover, hovT, step);
                b.Invalidate();
                if (s.On == onT && s.Hover == hovT) (done ?? (done = new List<ButtonBase>())).Add(b);
            }
            if (done != null) foreach (ButtonBase b in done) _animating.Remove(b);
            if (_animating.Count == 0) _timer.Stop();
        }

        private static float approach(float v, float target, float step)
        {
            if (v < target) return Math.Min(target, v + step);
            if (v > target) return Math.Max(target, v - step);
            return v;
        }

        private static void onPaint(object sender, PaintEventArgs e)
        {
            if (!ModernTheme.Enabled) return;
            ButtonBase b = (ButtonBase)sender;
            if (!_states.TryGetValue(b, out State s)) return;

            // a state change made while the theme was off, or without an event (CheckState set directly)
            float onT = isOn(b) ? 1f : 0f;
            if (!_animating.Contains(b)) s.On = onT;

            string caption = CaptionFor(b);
            if (caption == null && b.Image == null) return;

            Color onColour = OnColourFor(b);
            if (b.BackColor.ToArgb() == Color.Red.ToArgb() && RedMeansError(b)) { onColour = ModernTheme.Danger; s.On = 1f; }

            float pts = b.Font.SizeInPoints;
            Font f = ModernTheme.HudFont(pts + 0.5f);
            ModernTheme.DrawButton(e.Graphics, b.ClientRectangle, caption?.ToUpperInvariant(), f, b.Image,
                s.On, s.Hover, s.Pressed, b.Enabled, onColour, b.TextAlign,
                PaintBehind?.Invoke(b, e.Graphics) == true ? Color.Transparent : ModernTheme.EffectiveBack(b), GlyphFor(b));
        }
    }
}
