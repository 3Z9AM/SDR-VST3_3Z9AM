//  Setup.ModernFit.cs
//
//  Modern Setup: the pages were laid out for 8.25 pt Microsoft Sans Serif, .NET draws them in 9 pt Segoe UI, and the
//  new layout hides options it forces. When a page is first shown: text that does not fit is widened into free space
//  beside it (labels, check boxes, combos, buttons), otherwise drawn smaller; group boxes with hidden options close the
//  gap and what stands below them moves up.
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
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Thetis
{
    public partial class Setup
    {
        private readonly HashSet<TabPage> _modernFitDone = new HashSet<TabPage>();
        private static readonly Dictionary<string, Font> _modernFitFonts = new Dictionary<string, Font>();
        private const TextFormatFlags FitLine = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        // after the page is on screen: Visible is only true then
        private void queueModernFit()
        {
            if (_modernNav == null || IsDisposed) return;
            BeginInvoke(new MethodInvoker(() =>
            {
                try
                {
                    TabPage p = currentModernPage();
                    if (p == null || !p.Visible || !_modernFitDone.Add(p)) return;
                    if (p.AutoScroll) ModernTheme.SetDarkWindowTheme(p, true);   // light scroll bars otherwise
                    compactModernGroups(p);
                    if (p == tpAppearanceGeneral) raiseModernColumns(p);   // holes of the groups the new layout hides
                    fitModernText(p);
                    // again a moment later: some pages build their controls on the first show (Display > Waterfall);
                    // a second pass leaves text that already fits alone
                    Timer t = new Timer { Interval = 500 };
                    t.Tick += (s, e) =>
                    {
                        t.Stop(); t.Dispose();
                        try { if (!IsDisposed && !p.IsDisposed) fitModernText(p); }
                        catch (Exception ex) { Common.LogException(ex); }
                    };
                    t.Start();
                }
                catch (Exception ex) { Common.LogException(ex); }   // cosmetics must never break Setup
            }));
        }

        // ---- group boxes whose options are hidden: close the gap, move what is below up ----

        private static void compactModernGroups(Control parent)
        {
            foreach (Control c in parent.Controls.Cast<Control>().Where(x => x.Visible).OrderBy(x => x.Top).ToList())
            {
                if (c is GroupBox g)
                {
                    compactModernGroups(g);
                    int shrink = compactModernGroup(g);
                    if (shrink <= 0) continue;
                    int oldBottom = g.Bottom + shrink;
                    List<Control> below = parent.Controls.Cast<Control>()
                        .Where(s => s != g && s.Visible && s.Top >= oldBottom - 2 && s.Left < g.Right && s.Right > g.Left).ToList();
                    // one shift for all of them (a check box sitting on a group's title stays with it), never onto
                    // something above that is wider than this group's column
                    int shift = shrink;
                    foreach (Control s in below)
                        foreach (Control a in parent.Controls)
                            if (a != s && a.Visible && !below.Contains(a) && a.Top < s.Top && a.Left < s.Right && a.Right > s.Left)
                                shift = Math.Min(shift, s.Top - (a.Bottom + 4));
                    if (shift > 0) foreach (Control s in below) s.Top -= shift;
                }
                else if (c.HasChildren && !(c is UserControl) && !(c is TabControl))
                    compactModernGroups(c);
            }
        }

        // every top-level control slides up to just under whatever stands above it in its column
        private static void raiseModernColumns(Control page)
        {
            List<Control> shown = page.Controls.Cast<Control>().Where(x => x.Visible).OrderBy(x => x.Top).ToList();
            foreach (Control c in shown)
            {
                int top = 6;
                foreach (Control a in shown)
                    if (a != c && a.Top < c.Top && a.Left < c.Right && a.Right > c.Left)
                        top = Math.Max(top, a.Bottom + 6);
                if (top < c.Top - 6) c.Top = top;
            }
        }

        // returns how much shorter the group got
        private static int compactModernGroup(GroupBox g)
        {
            List<Control> kids = g.Controls.Cast<Control>().ToList();
            List<Control> shown = kids.Where(k => k.Visible).ToList();
            if (shown.Count == 0 || shown.Count == kids.Count) return 0;   // only groups the new layout trimmed
            int top = shown.Min(k => k.Top);
            int dy = top - 18;
            if (dy > 12) foreach (Control k in shown) k.Top -= dy;
            else dy = 0;
            int bottom = shown.Max(k => k.Bottom);
            int h = Math.Min(g.Height, bottom + 8);
            int shrink = g.Height - h;
            if (shrink < 12) return 0;
            g.Height = h;
            return shrink;
        }

        // ---- text that does not fit ----

        private void fitModernText(Control parent)
        {
            // hidden ones too: some (e.g. the DSP > Options warnings) are shown later by a timer
            foreach (Control c in parent.Controls.Cast<Control>().ToList())
            {
                if (c is ComboBox cb) fitModernCombo(cb);
                else if (c is Button || (c is CheckBox kb && kb.Appearance == Appearance.Button) ||
                         (c is RadioButton rb && rb.Appearance == Appearance.Button)) fitModernButton((ButtonBase)c);
                else if (c is Label || c is CheckBox || c is RadioButton) fitModernLabel(c);
                if (c.Visible && c.HasChildren && !(c is UserControl) && !(c is ComboBox) && !(c is NumericUpDown) && !(c is TabControl))
                    fitModernText(c);
            }
        }

        // free room to the right (or left) of c in its parent, up to the nearest control on the same row
        private static int modernFreeRight(Control c)
        {
            Control p = c.Parent;
            int limit = p.ClientSize.Width - (p is GroupBox ? 4 : 2);
            int band = modernTextBottom(c);
            foreach (Control s in p.Controls)
                if (s != c && s.Visible && s.Top < band - 1 && s.Bottom > c.Top + 1 && s.Left > c.Left + 4)
                    limit = Math.Min(limit, s.Left - 2);
            return limit;
        }

        private static int modernFreeLeft(Control c)
        {
            Control p = c.Parent;
            int limit = p is GroupBox ? 4 : 2;
            int band = modernTextBottom(c);
            foreach (Control s in p.Controls)
                if (s != c && s.Visible && s.Top < band - 1 && s.Bottom > c.Top + 1 && s.Right < c.Right - 4)
                    limit = Math.Max(limit, s.Right + 2);
            return limit;
        }

        // single-line text sits in the first line of a taller control (a check box 30 px high next to the row below)
        private static int modernTextBottom(Control c) =>
            c is ComboBox || c is ButtonBase && !(c is CheckBox) && !(c is RadioButton) ? c.Bottom : Math.Min(c.Bottom, c.Top + c.Font.Height + 6);

        private static void fitModernCombo(ComboBox cb)
        {
            int need = 0;
            IEnumerable<object> items = cb.Items.Cast<object>().Take(64);
            if (!string.IsNullOrEmpty(cb.Text)) items = items.Concat(new object[] { cb.Text });
            foreach (object o in items)
                need = Math.Max(need, TextRenderer.MeasureText(cb.GetItemText(o), cb.Font, Size.Empty, FitLine).Width);
            need += SystemInformation.VerticalScrollBarWidth + 10;
            if (need <= cb.Width) return;
            int w = Math.Min(need, modernFreeRight(cb) - cb.Left);
            if (w > cb.Width) cb.Width = w;
        }

        // the modern painter shrinks the caption itself; give it room where there is some (at most +60 %)
        private static void fitModernButton(ButtonBase b)
        {
            if (string.IsNullOrEmpty(b.Text) || b.Image != null) return;
            int need = TextRenderer.MeasureText(b.Text.ToUpperInvariant(), ModernTheme.HudFont(b.Font.SizeInPoints), Size.Empty, FitLine).Width + 14;
            if (need <= b.Width) return;
            int w = Math.Min(Math.Min(need, b.Width * 8 / 5), modernFreeRight(b) - b.Left);
            if (w > b.Width) b.Width = w;
        }

        private static void fitModernLabel(Control c)
        {
            string text = c.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            int glyph = c is Label ? 0 : 16;     // check box / radio circle + gap
            bool autoSize = c.AutoSize;
            bool multi = !autoSize && c.Height >= c.Font.Height * 1.8;
            bool right = c is Label l && (l.TextAlign == ContentAlignment.TopRight || l.TextAlign == ContentAlignment.MiddleRight ||
                                          l.TextAlign == ContentAlignment.BottomRight);

            if (multi && !(c is Label)) return;      // wrapped check boxes: their measure is not reliable
            if (multi)
            {
                if (!modernFitsWrapped(c, c.Font, glyph))
                {
                    // taller first, into free room below
                    Control p = c.Parent;
                    int limit = p.ClientSize.Height - 2;
                    foreach (Control s in p.Controls)
                        if (s != c && s.Visible && s.Left < c.Right - 1 && s.Right > c.Left + 1 && s.Top >= c.Bottom - 1)
                            limit = Math.Min(limit, s.Top - 2);
                    int h = Math.Min(c.GetPreferredSize(new Size(c.Width, 0)).Height + 2, limit - c.Top);
                    if (h > c.Height) c.Height = h;
                }
                Font f = c.Font;
                for (int i = 0; i < 6 && !modernFitsWrapped(c, f, glyph); i++)
                    f = modernFitFont(f, f.SizeInPoints - 0.5f);
                if (!ReferenceEquals(f, c.Font)) c.Font = f;
                return;
            }

            // labels: their own measure (what AutoSize uses, padding included); check boxes: text + box - their
            // preferred size is wider than what they draw
            int textW = TextRenderer.MeasureText(text, c.Font, Size.Empty, FitLine).Width;
            int extra = c is Label ? Math.Max(0, c.GetPreferredSize(Size.Empty).Width - textW) : glyph + 1;
            int need = textW + extra;
            if (!autoSize && need > c.Width)
            {
                if (right)
                {
                    int left = Math.Max(modernFreeLeft(c), c.Right - need);
                    if (left < c.Left) { int r = c.Right; c.Left = left; c.Width = r - left; }
                }
                else
                {
                    int w = Math.Min(need, modernFreeRight(c) - c.Left);
                    if (w > c.Width) c.Width = w;
                }
            }
            // still too long (or an auto-sized one running under its neighbour): a smaller font - labels only,
            // check boxes measured wider than they draw and shrank text that fitted
            if (!(c is Label)) return;
            if (c.BackColor != c.Parent.BackColor) return;   // boxed readouts (VAC monitor): text changes all the time
            // an auto-sized one may run a few px of its own empty padding under its neighbour (VAC monitor boxes)
            int room = autoSize ? (right ? c.Right - modernFreeLeft(c) + 6 : modernFreeRight(c) + 6 - c.Left) : c.Width + 4;
            Font fnt = c.Font;
            for (int i = 0; i < 4 && need > room && fnt.SizeInPoints > 7f; i++)
            {
                fnt = modernFitFont(fnt, fnt.SizeInPoints - 0.5f);
                need = TextRenderer.MeasureText(text, fnt, Size.Empty, FitLine).Width + extra;
            }
            if (!ReferenceEquals(fnt, c.Font))
            {
                int r = c.Right;
                c.Font = fnt;
                if (autoSize && right) c.Left = r - c.Width;
            }
        }

        // the label's own wrapping (padding included); only labels come here
        private static bool modernFitsWrapped(Control c, Font f, int glyph)
        {
            Font was = c.Font;
            if (!ReferenceEquals(f, was)) c.Font = f;
            int h = c.GetPreferredSize(new Size(c.Width, 0)).Height;
            if (!ReferenceEquals(f, was)) c.Font = was;
            return h <= c.Height;
        }

        // shared: fonts handed to controls must stay alive
        private static Font modernFitFont(Font f, float size)
        {
            string key = f.FontFamily.Name + "|" + size + "|" + (int)f.Style;
            if (!_modernFitFonts.TryGetValue(key, out Font nf))
            {
                nf = new Font(f.FontFamily, size, f.Style);
                _modernFitFonts[key] = nf;
            }
            return nf;
        }
    }
}
