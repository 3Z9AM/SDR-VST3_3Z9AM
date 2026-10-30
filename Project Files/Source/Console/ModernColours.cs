//  ModernColours.cs
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
    // Dark HUD colours for a whole window (Setup, and every other form while Modern UI is on).
    // Colours are only changed where the control shows a system default colour (colour pickers
    // and coloured warnings keep theirs), and every changed property is saved for Restore.
    internal sealed class ModernColours
    {
        public static readonly Color Back = ModernTheme.Tint(Color.FromArgb(10, 20, 30));
        public static readonly Color Panel = ModernTheme.Tint(Color.FromArgb(6, 14, 22));
        public static readonly Color Input = ModernTheme.Tint(Color.FromArgb(14, 32, 46));
        public static readonly Color Button = ModernTheme.Tint(Color.FromArgb(16, 40, 56));
        public static readonly Color Border = ModernTheme.Tint(Color.FromArgb(30, 96, 122));
        public static readonly Color Text = ModernTheme.Tint(Color.FromArgb(190, 236, 248));
        public static readonly Color TextDim = ModernTheme.Tint(Color.FromArgb(110, 160, 178));

        // left alone with its children (Setup's own navigation panel)
        public Control Skip;

        private sealed class SavedLook
        {
            public Color Back, Fore;
            public bool BackSet, ForeSet;   // false = inherited from the parent; restored by Reset*
            public bool UseVisualStyle;
            public FlatStyle Flat;
            public Color FlatBorder;
            public BorderStyle Border;
            public bool HasBorder;
            public Color Link, ActiveLink;
            public Color GridBack, GridLines;
            public bool GridHeaderStyles;
            public DataGridViewCellStyle Cell, AltCell, ColHeader, RowHeader;
            public DrawMode ComboDraw;
        }

        // one combo box outside a themed window (the console's own)
        public void ApplyCombo(ComboBox c)
        {
            if (_saved.ContainsKey(c)) return;
            themeControl(c);
            // the skin gives these its own greys: HUD colours always (saved above for Restore)
            c.BackColor = Input;
            c.ForeColor = Text;
        }

        // list rows: the selected one tinted with the accent and a bar on the left; the closed box plain
        private static void hudComboDrawItem(object sender, DrawItemEventArgs e)
        {
            ComboBox cb = (ComboBox)sender;
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool sel = !edit && (e.State & DrawItemState.Selected) != 0;
            Color back = sel ? ModernTheme.Lerp(cb.BackColor, ModernTheme.Accent, 0.35f) : cb.BackColor;
            using (SolidBrush b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
            if (sel)
                using (SolidBrush bar = new SolidBrush(ModernTheme.Accent)) e.Graphics.FillRectangle(bar, e.Bounds.Left, e.Bounds.Top + 2, 2, e.Bounds.Height - 4);
            if (e.Index < 0 || e.Index >= cb.Items.Count) return;
            Color fore = !cb.Enabled ? ModernTheme.TextDisabled : sel ? Color.White : cb.ForeColor;
            TextRenderer.DrawText(e.Graphics, cb.GetItemText(cb.Items[e.Index]), cb.Font,
                Rectangle.FromLTRB(e.Bounds.Left + 4, e.Bounds.Top, e.Bounds.Right - 2, e.Bounds.Bottom), fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
        private readonly Dictionary<Control, SavedLook> _saved = new Dictionary<Control, SavedLook>();
        private readonly HashSet<Control> _themed = new HashSet<Control>();

        public static bool IsDefaultBack(Color c)
        {
            if (c.A == 0) return true;
            int argb = c.ToArgb();
            return argb == SystemColors.Control.ToArgb() || argb == SystemColors.Window.ToArgb() ||
                   argb == SystemColors.ButtonFace.ToArgb() || argb == SystemColors.ControlLight.ToArgb() ||
                   argb == SystemColors.ControlLightLight.ToArgb() || argb == SystemColors.AppWorkspace.ToArgb() ||
                   argb == Color.White.ToArgb();
        }

        public static bool IsDefaultFore(Color c)
        {
            int argb = c.ToArgb();
            return argb == SystemColors.ControlText.ToArgb() || argb == SystemColors.WindowText.ToArgb() ||
                   argb == Color.Black.ToArgb();
        }

        // controls that show data in their colours or draw themselves completely
        private static bool skip(Control c)
        {
            if (c is ColorButton) return true;
            string n = c.GetType().Name;
            return n.Contains("Color") || n.Contains("Gradient") || n.Contains("Picker") || n.Contains("Grapher") ||
                   n.Contains("Meter") || n.Contains("Progress") || n.Contains("LedStrip");
        }

        // the children of top, then top itself; again later for controls added since
        public void Apply(Control top)
        {
            applyChildren(top);
            if (!_saved.ContainsKey(top))
            {
                _saved[top] = newSavedLook(top);
                if (IsDefaultBack(top.BackColor)) top.BackColor = Back;
                if (IsDefaultFore(top.ForeColor)) top.ForeColor = Text;
            }
        }

        private void applyChildren(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c == Skip || skip(c)) continue;
                if (!_saved.ContainsKey(c)) themeControl(c);
                if (c.HasChildren) applyChildren(c);
            }
        }

        private static readonly System.Reflection.MethodInfo _shouldSerializeBack =
            typeof(Control).GetMethod("ShouldSerializeBackColor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        private static readonly System.Reflection.MethodInfo _shouldSerializeFore =
            typeof(Control).GetMethod("ShouldSerializeForeColor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        // whether the colour is the control's own (true) or inherited from its parent
        private static bool isSet(System.Reflection.MethodInfo m, Control c)
        {
            try { return m == null || (bool)m.Invoke(c, null); }
            catch (Exception) { return true; }
        }

        private static SavedLook newSavedLook(Control c)
        {
            return new SavedLook
            {
                Back = c.BackColor, Fore = c.ForeColor,
                BackSet = isSet(_shouldSerializeBack, c), ForeSet = isSet(_shouldSerializeFore, c),
            };
        }

        // a grid cell style: default colours dark, own colours (highlighted rows) kept
        private static void darkCellStyle(DataGridViewCellStyle st, Color back)
        {
            if (st.BackColor.IsEmpty || IsDefaultBack(st.BackColor)) st.BackColor = back;
            if (st.ForeColor.IsEmpty || IsDefaultFore(st.ForeColor)) st.ForeColor = Text;
            if (st.SelectionBackColor.IsEmpty || st.SelectionBackColor.ToArgb() == SystemColors.Highlight.ToArgb())
                st.SelectionBackColor = Border;
            if (st.SelectionForeColor.IsEmpty || st.SelectionForeColor.ToArgb() == SystemColors.HighlightText.ToArgb())
                st.SelectionForeColor = Color.White;
        }

        private void themeControl(Control c)
        {
            SavedLook s = newSavedLook(c);
            bool changed = false;

            if (c is TabPage tp)
            {
                s.UseVisualStyle = tp.UseVisualStyleBackColor;
                tp.UseVisualStyleBackColor = false;
                tp.BackColor = Back;
                tp.ForeColor = Text;
                _saved[c] = s;
                return;
            }

            // push buttons and button-style check boxes: the console's modern button painter
            // (FlatStyle.System never raises Paint, so those switch to Standard while themed)
            if (c is ButtonBase bb && ModernButtonPainter.IsButtonLike(c))
            {
                s.Flat = bb.FlatStyle;
                s.FlatBorder = bb.FlatAppearance.BorderColor;
                s.UseVisualStyle = bb.UseVisualStyleBackColor;
                if (bb.FlatStyle == FlatStyle.System) bb.FlatStyle = FlatStyle.Standard;
                ModernButtonPainter.Attach(bb);
                _saved[c] = s;
                return;
            }

            if (c is DataGridView dg)
            {
                s.GridBack = dg.BackgroundColor;
                s.GridLines = dg.GridColor;
                s.GridHeaderStyles = dg.EnableHeadersVisualStyles;
                s.Cell = dg.DefaultCellStyle.Clone();
                s.AltCell = dg.AlternatingRowsDefaultCellStyle.Clone();
                s.ColHeader = dg.ColumnHeadersDefaultCellStyle.Clone();
                s.RowHeader = dg.RowHeadersDefaultCellStyle.Clone();
                if (IsDefaultBack(dg.BackgroundColor)) dg.BackgroundColor = Back;
                dg.GridColor = Border;
                dg.EnableHeadersVisualStyles = false;   // headers take the style colours only without visual styles
                darkCellStyle(dg.DefaultCellStyle, Input);
                if (!dg.AlternatingRowsDefaultCellStyle.BackColor.IsEmpty) darkCellStyle(dg.AlternatingRowsDefaultCellStyle, Panel);
                darkCellStyle(dg.ColumnHeadersDefaultCellStyle, Button);
                darkCellStyle(dg.RowHeadersDefaultCellStyle, Button);
                setWindowTheme(c, true);
                _saved[c] = s;
                return;
            }

            if (c is ListView lv)
            {
                if (IsDefaultBack(lv.BackColor)) lv.BackColor = Input;
                if (IsDefaultFore(lv.ForeColor)) lv.ForeColor = Text;
                setWindowTheme(c, true);
                _saved[c] = s;
                return;
            }

            if (c is TreeView tv)
            {
                if (IsDefaultBack(tv.BackColor)) tv.BackColor = Input;
                if (IsDefaultFore(tv.ForeColor)) tv.ForeColor = Text;
                setWindowTheme(c, true);
                _saved[c] = s;
                return;
            }

            if (c is TextBoxBase || c is NumericUpDown || c is ListBox || c is ComboBox)
            {
                if (c is ComboBox combo)
                {
                    s.Flat = combo.FlatStyle;
                    s.ComboDraw = combo.DrawMode;
                    // Flat/Popup paint their own light border and arrow; Standard takes the dark window theme
                    combo.FlatStyle = FlatStyle.Standard;
                    // the drop-down list in HUD colours (lists drawn by their owner, e.g. colours, stay)
                    if (combo.DrawMode == DrawMode.Normal)
                    {
                        combo.DrawMode = DrawMode.OwnerDrawFixed;
                        combo.DrawItem += hudComboDrawItem;
                    }
                }
                if (c is ComboBox || c is ListBox || (c is TextBoxBase tbb && tbb.Multiline)) setWindowTheme(c, true);
                if (c is TextBox tb) { s.Border = tb.BorderStyle; s.HasBorder = true; }
                if (IsDefaultBack(c.BackColor)) { c.BackColor = Input; changed = true; }
                if (IsDefaultFore(c.ForeColor)) { c.ForeColor = Text; changed = true; }
                if (changed || c is ComboBox) _saved[c] = s;
                return;
            }

            if (c is GroupBox gb)
            {
                gb.BackColor = Back;
                gb.ForeColor = IsDefaultFore(s.Fore) || s.Fore.ToArgb() == Text.ToArgb() ? Text : s.Fore;
                _saved[c] = s;
                return;
            }

            if (c is ScrollBar) { setWindowTheme(c, true); return; }
            if (c is ScrollableControl sc && sc.AutoScroll) setWindowTheme(c, true);

            if (c is TrackBar || c is System.Windows.Forms.Panel || c is Label || c is CheckBox || c is RadioButton ||
                c is UserControl || c is PictureBox || c is TabControl || c is SplitContainer)
            {
                if (!(c is PictureBox) && IsDefaultBack(c.BackColor) && c.BackColor.A != 0)
                {
                    c.BackColor = Back;
                    changed = true;
                }
                if (c is LinkLabel ll)
                {
                    s.Link = ll.LinkColor;
                    s.ActiveLink = ll.ActiveLinkColor;
                    ll.LinkColor = Color.FromArgb(120, 175, 255);
                    ll.ActiveLinkColor = ModernTheme.AccentSoft;
                    changed = true;
                }

                bool ownLightBack = s.BackSet && s.Back.A == 255 && !IsDefaultBack(s.Back) && s.Back.GetBrightness() > 0.6f;
                // an own light background (status colours like the spectral warning LEDs) keeps dark text
                if (ownLightBack)
                {
                    c.ForeColor = Color.FromArgb(24, 24, 26);
                    changed = true;
                }
                else if (IsDefaultFore(c.ForeColor))
                {
                    c.ForeColor = Text;
                    changed = true;
                }
                else if (s.ForeSet && s.Fore.GetBrightness() < 0.35f && !(c is PictureBox) &&
                         ModernTheme.EffectiveBack(c).GetBrightness() < 0.4f)
                {
                    // dark grey captions (secondary text on a light form) become the dim light grey
                    c.ForeColor = TextDim;
                    changed = true;
                }
                if (changed) _saved[c] = s;
            }
        }

        public void Restore()
        {
            foreach (KeyValuePair<Control, SavedLook> kv in _saved)
            {
                Control c = kv.Key;
                SavedLook s = kv.Value;
                if (c.IsDisposed) continue;
                if (c is ButtonBase bb)
                {
                    bb.FlatStyle = s.Flat;
                    bb.FlatAppearance.BorderColor = s.FlatBorder;
                }
                if (c is ComboBox combo)
                {
                    combo.FlatStyle = s.Flat;
                    combo.DrawItem -= hudComboDrawItem;
                    combo.DrawMode = s.ComboDraw;
                }
                if (c is LinkLabel ll && !s.Link.IsEmpty)
                {
                    ll.LinkColor = s.Link;
                    ll.ActiveLinkColor = s.ActiveLink;
                }
                if (c is DataGridView dg && s.Cell != null)
                {
                    dg.BackgroundColor = s.GridBack;
                    dg.GridColor = s.GridLines;
                    dg.EnableHeadersVisualStyles = s.GridHeaderStyles;
                    dg.DefaultCellStyle = s.Cell;
                    dg.AlternatingRowsDefaultCellStyle = s.AltCell;
                    dg.ColumnHeadersDefaultCellStyle = s.ColHeader;
                    dg.RowHeadersDefaultCellStyle = s.RowHeader;
                }
                if (s.BackSet) c.BackColor = s.Back; else c.ResetBackColor();
                if (s.ForeSet) c.ForeColor = s.Fore; else c.ResetForeColor();
                if (c is TabPage tp) tp.UseVisualStyleBackColor = s.UseVisualStyle;
                else if (c is ButtonBase b2) b2.UseVisualStyleBackColor = s.UseVisualStyle;
            }
            _saved.Clear();
            foreach (Control c in _themed)
                if (!c.IsDisposed) ModernTheme.SetDarkWindowTheme(c, false);
            _themed.Clear();
        }

        // dark scroll bars / combo boxes (Windows 10 1809+)
        private void setWindowTheme(Control c, bool dark)
        {
            ModernTheme.SetDarkWindowTheme(c, dark);
            if (dark) _themed.Add(c);
        }
    }
}
