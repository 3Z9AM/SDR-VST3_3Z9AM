using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Thetis
{
    // Modern UI for the Setup window (ModernTheme.Enabled): dark colours, a category tree on the
    // left instead of the rows of tab headers, and a "Search settings" box that finds a setting
    // by its caption or tooltip and jumps to it. Colours are only changed where the control
    // shows a system default colour (colour pickers and coloured warnings keep theirs), and
    // every changed property is saved and put back when the option is turned off.
    public partial class Setup
    {
        private const int ModernNavWidth = 196;

        private static readonly Color MSBack = ModernColours.Back;
        private static readonly Color MSPanel = ModernColours.Panel;
        private static readonly Color MSInput = ModernColours.Input;
        private static readonly Color MSButton = ModernColours.Button;
        private static readonly Color MSBorder = ModernColours.Border;
        private static readonly Color MSText = ModernColours.Text;
        private static readonly Color MSTextDim = ModernColours.TextDim;

        private bool _modernSetupOn;
        private Panel _modernNav;
        private TextBox _modernSearch;
        private TreeView _modernTree;
        private ListBox _modernResults;
        private Timer _modernSearchTimer, _modernFlashTimer;
        private bool _modernTreeUpdating;
        private readonly ModernColours _modernColours = new ModernColours();
        // pages/groups left out of the tree and the search (unused features, Setup.ModernVFO.cs).
        // They stay in the form: SaveOptions walks the control tree, a removed TabPage would lose its settings.
        private readonly HashSet<Control> _modernHidden = new HashSet<Control>();

        private readonly Dictionary<TabControl, (System.Windows.Forms.TabAppearance app, TabSizeMode mode, Size item, bool itemSet)> _modernSavedTabs =
            new Dictionary<TabControl, (System.Windows.Forms.TabAppearance, TabSizeMode, Size, bool)>();
        private Size _modernSavedMin, _modernSavedMax;
        private int _modernSavedTcLeft;

        internal void ApplyModernUI(bool on)
        {
            if (on == _modernSetupOn)
            {
                if (on) refreshModernSetup();   // new controls since the last time (skin servers, MM lists)
                return;
            }
            _modernSetupOn = on;
            SuspendLayout();
            try
            {
                if (on)
                {
                    createModernNav();
                    foreach (TabControl tc in modernNavTabControls()) hideModernTabHeaders(tc);
                    _modernColours.Skip = _modernNav;
                    _modernColours.Apply(this);
                    layoutModernSetup(true);
                    rebuildModernTree();
                }
                else
                {
                    layoutModernSetup(false);
                    foreach (KeyValuePair<TabControl, (System.Windows.Forms.TabAppearance app, TabSizeMode mode, Size item, bool itemSet)> kv in _modernSavedTabs)
                    {
                        TabControl tc = kv.Key;
                        ModernTheme.SetRegion(tc, null);
                        tc.SizeMode = kv.Value.mode;
                        if (kv.Value.itemSet) tc.ItemSize = kv.Value.item;
                        else System.ComponentModel.TypeDescriptor.GetProperties(tc)["ItemSize"]?.ResetValue(tc);
                        tc.Appearance = kv.Value.app;
                        tc.Invalidate(true);
                    }
                    _modernSavedTabs.Clear();
                    _modernColours.Restore();
                }
            }
            finally
            {
                ResumeLayout(true);
            }
            Invalidate(true);
        }

        private void refreshModernSetup()
        {
            foreach (TabControl tc in modernNavTabControls().ToList()) hideModernTabHeaders(tc);
            _modernColours.Skip = _modernNav;
            _modernColours.Apply(this);
            layoutModernSetup(true);
            rebuildModernTree();
        }

        // ---- tab headers -> category tree ----

        // tcSetup and every tab control that fills its tab page (the 2nd/3rd navigation level)
        private IEnumerable<TabControl> modernNavTabControls()
        {
            yield return tcSetup;
            Stack<TabControl> todo = new Stack<TabControl>();
            todo.Push(tcSetup);
            while (todo.Count > 0)
            {
                TabControl tc = todo.Pop();
                foreach (TabPage p in tc.TabPages)
                {
                    foreach (Control c in p.Controls)
                    {
                        if (c is TabControl sub && c.Left <= 6 && c.Top <= 6 && c.Width >= p.ClientSize.Width * 0.75)
                        {
                            yield return sub;
                            todo.Push(sub);
                        }
                    }
                }
            }
        }

        private void hideModernTabHeaders(TabControl tc)
        {
            if (_modernSavedTabs.ContainsKey(tc)) return;
            System.ComponentModel.PropertyDescriptor itemPd = System.ComponentModel.TypeDescriptor.GetProperties(tc)["ItemSize"];
            _modernSavedTabs[tc] = (tc.Appearance, tc.SizeMode, tc.ItemSize, itemPd == null || itemPd.ShouldSerializeValue(tc));
            tc.Appearance = System.Windows.Forms.TabAppearance.Buttons;
            tc.SizeMode = TabSizeMode.Fixed;
            tc.ItemSize = new Size(0, 1);
            clipModernTabControl(tc);
            tc.SizeChanged -= modernTabControl_SizeChanged;
            tc.SizeChanged += modernTabControl_SizeChanged;
            tc.ControlAdded -= modernTabControl_PagesChanged;
            tc.ControlAdded += modernTabControl_PagesChanged;
            tc.ControlRemoved -= modernTabControl_PagesChanged;
            tc.ControlRemoved += modernTabControl_PagesChanged;
            tc.SelectedIndexChanged -= modernTabControl_SelectedIndexChanged;
            tc.SelectedIndexChanged += modernTabControl_SelectedIndexChanged;
        }

        // hide the 3D border the tab control still draws around its pages
        private void clipModernTabControl(TabControl tc)
        {
            if (!_modernSetupOn || !_modernSavedTabs.ContainsKey(tc)) return;
            Rectangle r = tc.DisplayRectangle;
            ModernTheme.SetRegion(tc, new Region(r));
        }

        private void modernTabControl_SizeChanged(object sender, EventArgs e) => clipModernTabControl((TabControl)sender);

        private void modernTabControl_PagesChanged(object sender, ControlEventArgs e)
        {
            if (!_modernSetupOn || !IsHandleCreated) return;
            // pages built later (the Waterfall page is filled in OnShown): theme them once that code is done
            BeginInvoke(new MethodInvoker(() =>
            {
                if (!_modernSetupOn) return;
                try { refreshModernSetup(); }
                catch (Exception ex) { Common.LogException(ex); }
            }));
        }

        private void modernTabControl_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_modernSetupOn && !_modernTreeUpdating) selectModernTreeNodeForCurrentPage();
        }

        private void createModernNav()
        {
            if (_modernNav != null)
            {
                _modernNav.Visible = true;
                return;
            }
            _modernNav = new Panel { Name = "panelModernNav", BackColor = MSPanel, Visible = true };

            _modernSearch = new TextBox
            {
                Name = "txtModernSearch",
                AccessibleName = "Search settings",
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = MSInput,
                ForeColor = MSText,
                Font = ModernTheme.UiFont(9.5f),
                PlaceholderText = "Search settings",
            };
            _modernSearch.TextChanged += (s, e) => { _modernSearchTimer.Stop(); _modernSearchTimer.Start(); };
            _modernSearch.KeyDown += modernSearch_KeyDown;

            _modernTree = new TreeView
            {
                Name = "tvModernNav",
                BackColor = MSPanel,
                ForeColor = MSText,
                BorderStyle = BorderStyle.None,
                FullRowSelect = true,
                HideSelection = false,
                ShowLines = false,
                ShowPlusMinus = false,
                ShowRootLines = false,
                ItemHeight = 22,
                Indent = 14,
                Font = ModernTheme.UiFont(9f),
                // the whole row: with OwnerDrawText the system paints the indent of a full-row selection grey
                DrawMode = TreeViewDrawMode.OwnerDrawAll,
            };
            _modernTree.DrawNode += modernTree_DrawNode;
            _modernTree.AfterSelect += (s, e) => { if (!_modernTreeUpdating && e.Node?.Tag is TabPage p) showModernPage(p); };
            _modernTree.HandleCreated += (s, e) => ModernTheme.SetDarkWindowTheme(_modernTree, true);

            _modernResults = new ListBox
            {
                Name = "lbModernSearchResults",
                BackColor = MSPanel,
                ForeColor = MSText,
                BorderStyle = BorderStyle.None,
                IntegralHeight = false,
                Font = ModernTheme.UiFont(8.75f),
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 34,
                Visible = false,
            };
            _modernResults.DrawItem += modernResults_DrawItem;
            _modernResults.HandleCreated += (s, e) => ModernTheme.SetDarkWindowTheme(_modernResults, true);
            _modernResults.Click += (s, e) => openModernResult(_modernResults.SelectedItem as ModernSearchHit);
            _modernResults.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) openModernResult(_modernResults.SelectedItem as ModernSearchHit); };

            _modernNav.Controls.Add(_modernResults);
            _modernNav.Controls.Add(_modernTree);
            _modernNav.Controls.Add(_modernSearch);
            Controls.Add(_modernNav);
            _modernNav.BringToFront();

            _modernSearchTimer = new Timer { Interval = 220 };
            _modernSearchTimer.Tick += (s, e) => { _modernSearchTimer.Stop(); runModernSearch(); };
            _modernFlashTimer = new Timer { Interval = 180 };
            _modernFlashTimer.Tick += modernFlash_Tick;
        }

        private void layoutModernSetup(bool on)
        {
            if (on)
            {
                if (_modernNav == null) return;
                if (tcSetup.Left < ModernNavWidth)
                {
                    _modernSavedTcLeft = tcSetup.Left;
                    _modernSavedMin = MinimumSize;
                    _modernSavedMax = MaximumSize;
                    MinimumSize = Size.Empty;
                    MaximumSize = Size.Empty;
                    ClientSize = new Size(ClientSize.Width + ModernNavWidth, ClientSize.Height);
                    MinimumSize = Size;
                    MaximumSize = Size;
                    tcSetup.Left = _modernSavedTcLeft + ModernNavWidth;
                }
                int h = btnOK.Top - 8 - tcSetup.Top;
                _modernNav.Bounds = new Rectangle(8, tcSetup.Top, ModernNavWidth - 10, Math.Max(100, h + (btnOK.Bottom - btnOK.Top) + 8));
                _modernSearch.Bounds = new Rectangle(6, 6, _modernNav.Width - 12, 24);
                Rectangle list = new Rectangle(4, 36, _modernNav.Width - 8, _modernNav.Height - 40);
                _modernTree.Bounds = list;
                _modernResults.Bounds = list;
                lblTXProfileWarning.Location = new Point(tcSetup.Left, btnOK.Top + 4);
            }
            else if (_modernNav != null)
            {
                _modernNav.Visible = false;
                if (tcSetup.Left >= ModernNavWidth)
                {
                    MinimumSize = Size.Empty;
                    MaximumSize = Size.Empty;
                    ClientSize = new Size(ClientSize.Width - ModernNavWidth, ClientSize.Height);
                    tcSetup.Left = _modernSavedTcLeft;
                    MinimumSize = _modernSavedMin;
                    MaximumSize = _modernSavedMax;
                }
            }
            btnApply.Left = ClientSize.Width - btnApply.Width - 10;
            btnCancel.Left = btnApply.Left - btnCancel.Width - 6;
            btnOK.Left = btnCancel.Left - btnOK.Width - 6;
            labelSavingLoading.Left = ClientSize.Width - labelSavingLoading.Width - 10;
        }

        private void rebuildModernTree()
        {
            if (_modernTree == null) return;
            _modernTreeUpdating = true;
            _modernTree.BeginUpdate();
            try
            {
                _modernTree.Nodes.Clear();
                addModernTreeNodes(_modernTree.Nodes, tcSetup);
            }
            finally
            {
                _modernTree.EndUpdate();
                _modernTreeUpdating = false;
            }
            selectModernTreeNodeForCurrentPage();
        }

        private void addModernTreeNodes(TreeNodeCollection nodes, TabControl tc)
        {
            foreach (TabPage p in tc.TabPages)
            {
                if (_modernHidden.Contains(p)) continue;
                TreeNode n = new TreeNode(p.Text.Trim()) { Tag = p };
                nodes.Add(n);
                TabControl sub = p.Controls.OfType<TabControl>().FirstOrDefault(t => _modernSavedTabs.ContainsKey(t));
                if (sub != null) addModernTreeNodes(n.Nodes, sub);
            }
        }

        // the deepest page currently shown
        private TabPage currentModernPage()
        {
            TabPage p = tcSetup.SelectedTab;
            while (p != null)
            {
                TabControl sub = p.Controls.OfType<TabControl>().FirstOrDefault(t => _modernSavedTabs.ContainsKey(t));
                if (sub == null || sub.SelectedTab == null) break;
                p = sub.SelectedTab;
            }
            return p;
        }

        private void selectModernTreeNodeForCurrentPage()
        {
            if (_modernTree == null) return;
            TabPage cur = currentModernPage();
            TreeNode found = findModernNode(_modernTree.Nodes, cur);
            if (found == null || _modernTree.SelectedNode == found) return;
            _modernTreeUpdating = true;
            try
            {
                HashSet<TreeNode> keep = new HashSet<TreeNode>();
                for (TreeNode k = found; k != null; k = k.Parent) keep.Add(k);
                collapseModernExcept(_modernTree.Nodes, keep);
                for (TreeNode k = found.Parent; k != null; k = k.Parent) k.Expand();
                found.Expand();
                _modernTree.SelectedNode = found;
                found.EnsureVisible();
            }
            finally { _modernTreeUpdating = false; }
        }

        private static void collapseModernExcept(TreeNodeCollection nodes, HashSet<TreeNode> keep)
        {
            foreach (TreeNode n in nodes)
            {
                if (!keep.Contains(n) && n.IsExpanded) n.Collapse();
                collapseModernExcept(n.Nodes, keep);
            }
        }

        private static TreeNode findModernNode(TreeNodeCollection nodes, TabPage p)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Tag == p) return n;
                TreeNode sub = findModernNode(n.Nodes, p);
                if (sub != null) return sub;
            }
            return null;
        }

        // select the page and every page above it; a category node opens its first sub page
        private void showModernPage(TabPage p)
        {
            _modernTreeUpdating = true;
            try
            {
                for (Control c = p; c != null && c != this; c = c.Parent)
                    if (c is TabPage tp && tp.Parent is TabControl tc && tc.SelectedTab != tp) tc.SelectedTab = tp;
                // accordion: only the branch of the chosen page stays open
                TreeNode n = findModernNode(_modernTree.Nodes, p);
                if (n != null)
                {
                    HashSet<TreeNode> keep = new HashSet<TreeNode>();
                    for (TreeNode k = n; k != null; k = k.Parent) keep.Add(k);
                    collapseModernExcept(_modernTree.Nodes, keep);
                    for (TreeNode k = n; k != null; k = k.Parent) k.Expand();
                }
            }
            finally { _modernTreeUpdating = false; }
        }

        private void modernTree_DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            if (e.Node == null || e.Bounds.Height == 0) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // e.Bounds is the whole row (OwnerDrawAll); the node's own bounds give the indent
            int x = e.Node.Bounds.X;
            Rectangle row = new Rectangle(x - 2, e.Bounds.Y + 1, _modernTree.ClientSize.Width - x - 2, e.Bounds.Height - 2);
            bool sel = (e.State & TreeNodeStates.Selected) != 0;
            bool hot = (e.State & TreeNodeStates.Hot) != 0;
            using (SolidBrush bg = new SolidBrush(MSPanel)) g.FillRectangle(bg, new Rectangle(0, e.Bounds.Y, _modernTree.ClientSize.Width, e.Bounds.Height));
            if (sel || hot)
            {
                using (GraphicsPath p = ModernTheme.RoundedRect(row, 4))
                using (SolidBrush b = new SolidBrush(sel ? Color.FromArgb(54, ModernTheme.Accent) : Color.FromArgb(18, 255, 255, 255)))
                    g.FillPath(b, p);
                if (sel)
                    using (SolidBrush bar = new SolidBrush(ModernTheme.Accent))
                        g.FillRectangle(bar, row.X, row.Y + 3, 3, row.Height - 6);
            }
            Font f = e.Node.Level == 0 ? ModernTheme.UiFont(9f, FontStyle.Bold) : ModernTheme.UiFont(9f);
            Color fc = sel ? ModernTheme.AccentSoft : (e.Node.Level == 0 ? MSText : MSTextDim);
            TextRenderer.DrawText(g, e.Node.Text, f, new Rectangle(row.X + 8, row.Y, row.Width - 10, row.Height), fc,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        // ---- search ----

        private sealed class ModernSearchHit
        {
            public Control Target;
            public string Caption, Path;
            public int Score;
        }

        private void runModernSearch()
        {
            string q = _modernSearch.Text.Trim();
            if (q.Length < 2)
            {
                _modernResults.Visible = false;
                _modernTree.Visible = true;
                return;
            }
            List<ModernSearchHit> hits = new List<ModernSearchHit>();
            HashSet<string> seen = new HashSet<string>();
            foreach (TabPage page in tcSetup.TabPages)
                collectModernHits(page, q, hits, seen);
            _modernResults.BeginUpdate();
            _modernResults.Items.Clear();
            foreach (ModernSearchHit h in hits.OrderBy(x => x.Score).ThenBy(x => x.Path).Take(200))
                _modernResults.Items.Add(h);
            if (_modernResults.Items.Count == 0)
                _modernResults.Items.Add(new ModernSearchHit { Caption = "No setting found", Path = "try another word" });
            _modernResults.EndUpdate();
            _modernTree.Visible = false;
            _modernResults.Visible = true;
        }

        private void collectModernHits(Control root, string q, List<ModernSearchHit> hits, HashSet<string> seen)
        {
            foreach (Control c in root.Controls)
            {
                if (_modernHidden.Contains(c)) continue;
                string text = (c is Label || c is ButtonBase || c is GroupBox) ? c.Text?.Replace("&", "").Trim() : null;
                string tip = toolTip1.GetToolTip(c);
                int score = -1;
                if (!string.IsNullOrEmpty(text) && text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    score = text.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                else if (!string.IsNullOrEmpty(tip) && tip.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    score = 2;
                if (score >= 0 && !(c is ColorButton))
                {
                    string caption = !string.IsNullOrEmpty(text) ? text : tip.Split('\n')[0];
                    if (caption.Length > 60) caption = caption.Substring(0, 57) + "...";
                    string path = modernPathOf(c);
                    if (seen.Add(path + "|" + caption))
                        hits.Add(new ModernSearchHit { Target = c, Caption = caption, Path = path, Score = score });
                }
                if (c.HasChildren && !(c is ColorButton)) collectModernHits(c, q, hits, seen);
            }
        }

        private string modernPathOf(Control c)
        {
            List<string> parts = new List<string>();
            for (Control p = c.Parent; p != null && p != this; p = p.Parent)
            {
                if (p is TabPage tp) parts.Insert(0, tp.Text.Trim());
                else if (p is GroupBox gb && !string.IsNullOrEmpty(gb.Text)) parts.Insert(0, gb.Text.Trim());
            }
            return string.Join(" › ", parts);
        }

        private void modernResults_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            ModernSearchHit h = (ModernSearchHit)_modernResults.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(MSPanel)) g.FillRectangle(bg, e.Bounds);
            Rectangle row = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 1, e.Bounds.Width - 4, e.Bounds.Height - 2);
            if (sel && h.Target != null)
                using (GraphicsPath p = ModernTheme.RoundedRect(row, 4))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(54, ModernTheme.Accent)))
                    g.FillPath(b, p);
            TextFormatFlags fl = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, h.Caption, ModernTheme.UiFont(9f, FontStyle.Bold), new Rectangle(row.X + 6, row.Y + 2, row.Width - 8, 16),
                h.Target == null ? MSTextDim : (sel ? ModernTheme.AccentSoft : MSText), fl);
            TextRenderer.DrawText(g, h.Path, ModernTheme.UiFont(8f), new Rectangle(row.X + 6, row.Y + 17, row.Width - 8, 15), MSTextDim, fl);
        }

        private void modernSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (!_modernResults.Visible || _modernResults.Items.Count == 0) return;
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
            {
                int i = _modernResults.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1);
                _modernResults.SelectedIndex = Math.Max(0, Math.Min(_modernResults.Items.Count - 1, i));
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                openModernResult((_modernResults.SelectedItem ?? _modernResults.Items[0]) as ModernSearchHit);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                _modernSearch.Clear();
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        private void openModernResult(ModernSearchHit h)
        {
            if (h?.Target == null || h.Target.IsDisposed) return;
            TabPage page = null;
            for (Control p = h.Target.Parent; p != null; p = p.Parent)
                if (p is TabPage tp) { page = tp; break; }
            if (page != null) showModernPage(page);
            selectModernTreeNodeForCurrentPage();
            flashModernControl(h.Target);
        }

        // ---- flash the found control a few times ----

        private Control _modernFlashTarget;
        private Color _modernFlashBack;
        private int _modernFlashStep;

        private void flashModernControl(Control c)
        {
            stopModernFlash();
            _modernFlashTarget = c;
            _modernFlashBack = c.BackColor;
            _modernFlashStep = 0;
            _modernFlashTimer.Start();
            modernFlash_Tick(null, EventArgs.Empty);
        }

        private void modernFlash_Tick(object sender, EventArgs e)
        {
            Control c = _modernFlashTarget;
            if (c == null || c.IsDisposed) { stopModernFlash(); return; }
            _modernFlashStep++;
            if (_modernFlashStep > 6) { stopModernFlash(); return; }
            c.BackColor = _modernFlashStep % 2 == 1 ? Color.FromArgb(120, 90, 30) : _modernFlashBack;
        }

        private void stopModernFlash()
        {
            _modernFlashTimer?.Stop();
            if (_modernFlashTarget != null && !_modernFlashTarget.IsDisposed) _modernFlashTarget.BackColor = _modernFlashBack;
            _modernFlashTarget = null;
        }
    }
}
