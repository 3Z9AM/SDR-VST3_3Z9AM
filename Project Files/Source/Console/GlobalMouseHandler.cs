/*  GlobalMouseHandler.cs

This file is part of a program that implements a Software-Defined Radio.

This code/file can be found on GitHub : https://github.com/ramdor/Thetis

Copyright (C) 2000-2025 Original authors
Copyright (C) 2020-2026 Richard Samphire MW0LGE
Portions Copyright (C) 2026 Daniel Poznanski, 3Z9AM - changes in this fork (see its git history).

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

The author can be reached by email at

mw0lge@grange-lane.co.uk
*/
//
//============================================================================================//
// Dual-Licensing Statement (Applies Only to Author's Contributions, Richard Samphire MW0LGE) //
// ------------------------------------------------------------------------------------------ //
// For any code originally written by Richard Samphire MW0LGE, or for any modifications       //
// made by him, the copyright holder for those portions (Richard Samphire) reserves the       //
// right to use, license, and distribute such code under different terms, including           //
// closed-source and proprietary licences, in addition to the GNU General Public License      //
// granted above. Nothing in this statement restricts any rights granted to recipients under  //
// the GNU GPL. Code contributed by others (not Richard Samphire) remains licensed under      //
// its original terms and is not affected by this dual-licensing statement in any way.        //
// Richard Samphire can be reached by email at :  mw0lge@grange-lane.co.uk                    //
//============================================================================================//

using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Thetis
{
    public delegate void MouseMovedEvent();

    public class GlobalMouseHandler : IMessageFilter
    {
        private const int WM_MOUSEMOVE = 0x0200;
        //private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;

        public event MouseMovedEvent MouseUp; 
        public event MouseEventHandler MouseMove;   //MW0LGE_21e

        //public event MouseMovedEvent MouseDown;

        #region IMessageFilter Members

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == WM_MOUSEMOVE)
            {
                MouseMove?.Invoke(null, new MouseEventArgs(MouseButtons.None,0, Control.MousePosition.X, Control.MousePosition.Y, 0));
            }
            //if (m.Msg == WM_LBUTTONDOWN)
            //{
            //    if (MouseUp != null)
            //    {
            //        MouseDown();
            //    }
            //}

            if (m.Msg == WM_LBUTTONUP)
            {
                if (MouseUp != null)
                {
                    MouseUp();
                }
            }

            // Always allow message to continue to the next filter control
            return false;
        }

        #endregion
    }


    // SQ4KOU native Console panel move support.
    // These are the actual built-in WinForms PanelTS blocks from console.Designer.cs
    // (BAND, MODE, FILTER, VFO, DSP, RX2 blocks, etc.), not Meters/Gadgets.
    public partial class Console
    {
        private NativePanelShiftDragFilter _nativePanelShiftDragFilter;
        private Control _nativePanelDragPrimary;
        private Dictionary<Control, Point> _nativePanelDragOrigins;
        private Point _nativePanelDragMouseStart;
        private Point? _nativeModeSpecificSharedLocation;
        private bool _nativeApplyingModeSpecificLocation;

        private static readonly string[] _nativeModeSpecificPanelNames =
        {
            "panelModeSpecificCW",
            "panelModeSpecificPhone",
            "panelModeSpecificDigital",
            "panelModeSpecificFM"
        };

        private static readonly HashSet<string> _nativeMovablePanelNames =
            new HashSet<string>(StringComparer.Ordinal)
            {
                // Top-level PanelTS blocks
                "panelBandHF",
                "panelBandVHF",
                "panelBandGEN",
                "panelMode",
                "panelFilter",
                "panelDisplay",
                "panelDisplay2",
                "panelMeterLabels",
                "panelOptions",
                "panelSoundControls",
                "panelVFO",
                "panelVFOALabels",
                "panelVFOBLabels",
                "panelVFOLabels",
                "panelDSP",
                "panelMultiRX",
                "panelPower",
                "panelModeSpecificCW",
                "panelModeSpecificPhone",
                "panelModeSpecificDigital",
                "panelModeSpecificFM",
                "panelRX2Mixer",
                "panelRX2DSP",
                "panelRX2Display",
                "panelRX2Mode",
                "panelRX2Filter",
                "panelRX2Power",
                "panelRX2RF",
                "panelButtonBar",
                "panelAndromedaMisc",
                "panelDiversityMini",
                "panelModernSMeter",
                "ngTop",    // info panel of the new layout (Console.NewGUI.cs)

                // RX1 squelch is not contained in a PanelTS in the native
                // designer. It is three separate top-level controls that
                // visually form one movable block.
                "chkSquelch",
                "ptbSquelch",
                "picSquelch",
                "chkRX2Squelch",
                "ptbRX2Squelch",
                "picRX2Squelch",

                // Top-level GroupBoxTS blocks. These were previously excluded
                // because the drag resolver accepted PanelTS only.
                "grpMultimeter",
                "grpRX2Meter",
                "grpDisplaySplit",
                "grpVFOBetween",
                "grpVFOA",
                "grpVFOB",
                "grpMultimeterMenus"
            };

        private void InitializeNativePanelShiftDrag()
        {
            if (_nativePanelShiftDragFilter != null) return;

            _nativePanelShiftDragFilter = new NativePanelShiftDragFilter(this);
            Application.AddMessageFilter(_nativePanelShiftDragFilter);
            InitializeNativeModeSpecificLocationGuard();
            panelDisplay.SizeChanged += NativeDisplaySize_Changed;
            Shown += (s, e) => RaiseNativeMovablePanelsAboveDisplay();
        }

        // Movable panels must always stay on a layer above the spectrum/waterfall
        // (pnlDisplay), so a panel dragged over the display is never hidden by it.
        internal void RaiseNativeMovablePanelsAboveDisplay()
        {
            if (IsDisposed || Disposing) return;

            // back-to-front so the panels keep their existing order relative to each other
            foreach (Control c in Controls.Cast<Control>().Reverse().ToList())
            {
                // panelDisplay is the spectrum/waterfall container itself - it stays below
                if (c.Parent == this && IsNativeMovableControl(c) && c.Name != "panelDisplay")
                    c.BringToFront();
            }
        }

        private void InitializeNativeModeSpecificLocationGuard()
        {
            Control seed = null;

            foreach (string name in _nativeModeSpecificPanelNames)
            {
                Control panel = Controls.Cast<Control>()
                    .FirstOrDefault(c => c.Parent == this && c.Name == name);
                if (panel == null) continue;

                if (seed == null) seed = panel;
                panel.VisibleChanged -= NativeModeSpecificPanel_VisibleChanged;
                panel.VisibleChanged += NativeModeSpecificPanel_VisibleChanged;
            }

            if (seed != null && !_nativeModeSpecificSharedLocation.HasValue)
                _nativeModeSpecificSharedLocation = seed.Location;
        }

        private static bool IsNativeModeSpecificPanelName(string name)
        {
            return Array.IndexOf(_nativeModeSpecificPanelNames, name) >= 0;
        }

        private void NativeModeSpecificPanel_VisibleChanged(object sender, EventArgs e)
        {
            if (_nativeApplyingModeSpecificLocation || !_nativeModeSpecificSharedLocation.HasValue)
                return;

            Control panel = sender as Control;
            if (panel == null || !panel.Visible || IsDisposed || Disposing)
                return;

            // Native mode switching can re-apply the designer Location while changing
            // which mode-specific panel is visible. Re-assert the operator-selected
            // shared position after that layout pass has completed.
            BeginInvoke((MethodInvoker)delegate
            {
                if (!IsDisposed && !Disposing)
                    ApplyNativeModeSpecificSharedLocation();
            });
        }

        private void ApplyNativeModeSpecificSharedLocation()
        {
            if (!_nativeModeSpecificSharedLocation.HasValue) return;

            List<Control> panels = new List<Control>();
            foreach (string name in _nativeModeSpecificPanelNames)
            {
                Control panel = Controls.Cast<Control>()
                    .FirstOrDefault(c => c.Parent == this && c.Name == name);
                if (panel != null) panels.Add(panel);
            }
            if (panels.Count == 0) return;

            Point requested = _nativeModeSpecificSharedLocation.Value;
            int maxX = panels.Min(p => Math.Max(0, ClientSize.Width - p.Width));
            int maxY = panels.Min(p => Math.Max(0, ClientSize.Height - p.Height));
            Point target = new Point(
                Math.Max(0, Math.Min(maxX, requested.X)),
                Math.Max(0, Math.Min(maxY, requested.Y)));

            _nativeModeSpecificSharedLocation = target;
            _nativeApplyingModeSpecificLocation = true;
            try
            {
                foreach (Control panel in panels)
                {
                    if (panel.Location != target)
                        panel.Location = target;
                }
            }
            finally
            {
                _nativeApplyingModeSpecificLocation = false;
            }
        }

        private const string NativePanelLocationKeyPrefix = "NativePanelLocation.";

        private static bool PersistNativePanelLocation(string name)
        {
            return _nativeMovablePanelNames.Contains(name);
        }

        // RX2 panels are parked by the native RX2 layout while RX2 is OFF: their saved positions
        // are only read and applied while RX2 is ON, otherwise they would pull the panels back
        // onto the Console.
        private static bool IsRx2NativePanelName(string name)
        {
            return name.Contains("RX2");
        }

        private void ApplyCurrentViewRx2PanelLocations()
        {
            Dictionary<string, Point> saved = collapsedDisplay
                ? _nativePanelLocationsCollapsed
                : _nativePanelLocationsExpanded;
            if (saved == null) return;
            RestoreCapturedNativePanelLocations(saved
                .Where(kvp => IsRx2NativePanelName(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal));
        }

        // Expanded and collapsed views keep separate panel positions. The expanded set uses
        // the original key prefix so positions saved by earlier builds still load.
        private const string NativePanelLocationCollapsedKeyPrefix = "NativePanelLocationCollapsed.";
        private Dictionary<string, Point> _nativePanelLocationsExpanded;
        private Dictionary<string, Point> _nativePanelLocationsCollapsed;

        private void AppendNativePanelLocations(List<string> state)
        {
            if (state == null) return;

            StoreCurrentViewNativePanelLocations();
            AppendNativePanelLocationSet(state, NativePanelLocationKeyPrefix, _nativePanelLocationsExpanded);
            AppendNativePanelLocationSet(state, NativePanelLocationCollapsedKeyPrefix, _nativePanelLocationsCollapsed);
            AppendNativePanelOpacities(state);
            AppendNativeDisplaySizes(state);
            if (_ngActive) ngAppendTopPos(state);
        }

        private static void AppendNativePanelLocationSet(List<string> state, string prefix, Dictionary<string, Point> set)
        {
            if (set == null) return;

            foreach (KeyValuePair<string, Point> kvp in set)
            {
                if (!PersistNativePanelLocation(kvp.Key)) continue;
                state.Add(prefix + kvp.Key + "/" + kvp.Value.X.ToString() + "|" + kvp.Value.Y.ToString());
            }
        }

        // Snapshot of the panels as they are on screen now, filed under the active view.
        private void StoreCurrentViewNativePanelLocations()
        {
            Dictionary<string, Point> captured = CapturePersistedNativePanelLocations();

            // RX2 OFF: keep the RX2 positions saved while it was ON
            Dictionary<string, Point> previous = collapsedDisplay
                ? _nativePanelLocationsCollapsed
                : _nativePanelLocationsExpanded;
            if (previous != null)
                foreach (KeyValuePair<string, Point> kvp in previous)
                    if (IsRx2NativePanelName(kvp.Key) && !captured.ContainsKey(kvp.Key))
                        captured[kvp.Key] = kvp.Value;

            if (_nativeModeSpecificSharedLocation.HasValue)
            {
                foreach (string name in _nativeModeSpecificPanelNames)
                    if (captured.ContainsKey(name))
                        captured[name] = _nativeModeSpecificSharedLocation.Value;
            }

            if (collapsedDisplay)
                _nativePanelLocationsCollapsed = captured;
            else
                _nativePanelLocationsExpanded = captured;
        }

        private void ApplyCurrentViewNativePanelLocations()
        {
            Dictionary<string, Point> saved = collapsedDisplay
                ? _nativePanelLocationsCollapsed
                : _nativePanelLocationsExpanded;
            if (saved == null || saved.Count == 0) return;

            RestoreCapturedNativePanelLocations(saved);

            Point shared;
            if (saved.TryGetValue("panelModeSpecificPhone", out shared))
            {
                _nativeModeSpecificSharedLocation = shared;
                ApplyNativeModeSpecificSharedLocation();
                return;
            }
            foreach (string name in _nativeModeSpecificPanelNames)
            {
                if (saved.TryGetValue(name, out shared))
                {
                    _nativeModeSpecificSharedLocation = shared;
                    ApplyNativeModeSpecificSharedLocation();
                    return;
                }
            }
        }

        // Called at the start of Expand/CollapseDisplay, while the old view is still laid out.
        private void BeginNativePanelViewSwitch()
        {
            if (initializing) return;
            StoreCurrentViewNativePanelLocations();
        }

        // Called at the end of Expand/CollapseDisplay, after the native layout pass.
        private void EndNativePanelViewSwitch()
        {
            if (initializing) return;
            ApplyNativeDisplaySize();
            ApplyCurrentViewNativePanelLocations();
            RaiseNativeMovablePanelsAboveDisplay();
        }

        private Dictionary<string, Point> CapturePersistedNativePanelLocations()
        {
            Dictionary<string, Point> captured = new Dictionary<string, Point>(StringComparer.Ordinal);

            foreach (string name in _nativeMovablePanelNames)
            {
                if (!PersistNativePanelLocation(name)) continue;
                if (!RX2Enabled && IsRx2NativePanelName(name)) continue;

                Control panel = Controls.Cast<Control>()
                    .FirstOrDefault(c => c.Parent == this && c.Name == name);
                if (panel != null)
                    captured[name] = panel.Location;
            }

            return captured;
        }

        private void RestoreCapturedNativePanelLocations(Dictionary<string, Point> captured)
        {
            if (captured == null || captured.Count == 0) return;

            foreach (KeyValuePair<string, Point> kvp in captured)
            {
                if (!RX2Enabled && IsRx2NativePanelName(kvp.Key)) continue;

                Control panel = Controls.Cast<Control>()
                    .FirstOrDefault(c => c.Parent == this && c.Name == kvp.Key);
                if (panel == null) continue;

                int maxX = Math.Max(0, ClientSize.Width - panel.Width);
                int maxY = Math.Max(0, ClientSize.Height - panel.Height);
                Point restored = new Point(
                    Math.Max(0, Math.Min(maxX, kvp.Value.X)),
                    Math.Max(0, Math.Min(maxY, kvp.Value.Y)));

                if (panel.Location != restored)
                    panel.Location = restored;
            }
        }

        private void RestoreNativePanelLocationsFromState()
        {
            Dictionary<string, Point> expanded = new Dictionary<string, Point>(StringComparer.Ordinal);
            Dictionary<string, Point> collapsed = new Dictionary<string, Point>(StringComparer.Ordinal);
            Dictionary<string, int> opacities = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (string entry in DB.GetVars("State"))
            {
                int slash = entry.IndexOf('/');
                if (slash <= 0 || slash >= entry.Length - 1) continue;

                string key = entry.Substring(0, slash);
                if (key == NativeDisplaySizeKey || key == NativeDisplaySizeCollapsedKey)
                {
                    string[] wh = entry.Substring(slash + 1).Split('|');
                    if (wh.Length == 2 &&
                        Int32.TryParse(wh[0], out int w) &&
                        Int32.TryParse(wh[1], out int h))
                    {
                        if (key == NativeDisplaySizeKey)
                            _nativeDisplaySizeExpanded = new Size(w, h);
                        else
                            _nativeDisplaySizeCollapsed = new Size(w, h);
                    }
                    continue;
                }
                if (key.StartsWith(NativePanelOpacityKeyPrefix, StringComparison.Ordinal))
                {
                    string opacityName = key.Substring(NativePanelOpacityKeyPrefix.Length);
                    if (_nativeMovablePanelNames.Contains(opacityName) &&
                        Int32.TryParse(entry.Substring(slash + 1), out int pct))
                        opacities[opacityName] = ClampNativePanelOpacity(pct);
                    continue;
                }

                Dictionary<string, Point> target;
                string name;
                if (key.StartsWith(NativePanelLocationCollapsedKeyPrefix, StringComparison.Ordinal))
                {
                    target = collapsed;
                    name = key.Substring(NativePanelLocationCollapsedKeyPrefix.Length);
                }
                else if (key.StartsWith(NativePanelLocationKeyPrefix, StringComparison.Ordinal))
                {
                    target = expanded;
                    name = key.Substring(NativePanelLocationKeyPrefix.Length);
                }
                else continue;

                if (!PersistNativePanelLocation(name))
                    continue;

                string[] xy = entry.Substring(slash + 1).Split('|');
                if (xy.Length != 2) continue;

                if (Int32.TryParse(xy[0], out int x) &&
                    Int32.TryParse(xy[1], out int y))
                {
                    target[name] = new Point(x, y);
                }
            }

            _nativePanelLocationsExpanded = expanded.Count > 0 ? expanded : null;
            _nativePanelLocationsCollapsed = collapsed.Count > 0 ? collapsed : null;

            ApplyNativeDisplaySize();
            ApplyCurrentViewNativePanelLocations();
            RaiseNativeMovablePanelsAboveDisplay();

            foreach (KeyValuePair<string, int> kvp in opacities)
            {
                Control panel = Controls.Cast<Control>()
                    .FirstOrDefault(c => c.Parent == this && c.Name == kvp.Key);
                if (panel != null)
                    SetNativePanelOpacity(panel, kvp.Value);
            }
        }

        // ---- Display (spectrum/waterfall) size: Shift + drag an edge or corner ----
        // Lets the display grow under the (transparent) panels and act as their background.
        // Expanded and collapsed views keep separate sizes. While a custom size is set it is
        // re-asserted whenever the native layout code (ResizeConsole, Collapse/Expand,
        // Extend...) resizes panelDisplay. Ctrl + Shift + click on the display resets it.
        private const string NativeDisplaySizeKey = "NativePanelSize.panelDisplay";
        private const string NativeDisplaySizeCollapsedKey = "NativePanelSizeCollapsed.panelDisplay";
        private const int NativeDisplayResizeGrip = 12;
        private const int NativeDisplayMinWidth = 240;
        private const int NativeDisplayMinHeight = 120;

        [Flags]
        private enum NativeResizeEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

        private Size? _nativeDisplaySizeExpanded;
        private Size? _nativeDisplaySizeCollapsed;
        private bool _nativeApplyingDisplaySize;
        private bool _nativeDisplaySizePending;
        private NativeResizeEdges _nativeDisplayResizeEdges;
        private Rectangle _nativeDisplayResizeOrigin;

        private Size? CurrentViewNativeDisplaySize
        {
            get { return collapsedDisplay ? _nativeDisplaySizeCollapsed : _nativeDisplaySizeExpanded; }
            set
            {
                if (collapsedDisplay) _nativeDisplaySizeCollapsed = value;
                else _nativeDisplaySizeExpanded = value;
            }
        }

        private void AppendNativeDisplaySizes(List<string> state)
        {
            if (_nativeDisplaySizeExpanded.HasValue)
                state.Add(NativeDisplaySizeKey + "/" + _nativeDisplaySizeExpanded.Value.Width.ToString() + "|" + _nativeDisplaySizeExpanded.Value.Height.ToString());
            if (_nativeDisplaySizeCollapsed.HasValue)
                state.Add(NativeDisplaySizeCollapsedKey + "/" + _nativeDisplaySizeCollapsed.Value.Width.ToString() + "|" + _nativeDisplaySizeCollapsed.Value.Height.ToString());
        }

        private void ApplyNativeDisplaySize()
        {
            _nativeDisplaySizePending = false;
            if (IsDisposed || Disposing) return;

            Size? custom = CurrentViewNativeDisplaySize;
            if (!custom.HasValue) return;

            int w = Math.Max(NativeDisplayMinWidth, Math.Min(ClientSize.Width, custom.Value.Width));
            int h = Math.Max(NativeDisplayMinHeight, Math.Min(ClientSize.Height, custom.Value.Height));
            int x = Math.Max(0, Math.Min(panelDisplay.Left, ClientSize.Width - w));
            int y = Math.Max(0, Math.Min(panelDisplay.Top, ClientSize.Height - h));
            Rectangle target = new Rectangle(x, y, w, h);
            if (panelDisplay.Bounds == target) return;

            _nativeApplyingDisplaySize = true;
            try
            {
                panelDisplay.Bounds = target;
            }
            finally
            {
                _nativeApplyingDisplaySize = false;
            }
        }

        private void NativeDisplaySize_Changed(object sender, EventArgs e)
        {
            if (_nativeApplyingDisplaySize || _nativeDisplayResizeEdges != NativeResizeEdges.None) return;
            if (initializing || _nativeDisplaySizePending || !IsHandleCreated) return;

            Size? custom = CurrentViewNativeDisplaySize;
            if (!custom.HasValue || panelDisplay.Size == custom.Value) return;

            // let the native layout pass finish first (it positions the zoom/pan controls
            // for its own size; their Bottom/Right anchors then follow our resize)
            _nativeDisplaySizePending = true;
            BeginInvoke(new MethodInvoker(() =>
            {
                ApplyNativeDisplaySize();
                RaiseNativeMovablePanelsAboveDisplay();
            }));
        }

        private NativeResizeEdges HitTestNativeDisplayEdges(Control display)
        {
            Point p = display.PointToClient(Control.MousePosition);
            NativeResizeEdges edges = NativeResizeEdges.None;

            if (p.X < NativeDisplayResizeGrip) edges |= NativeResizeEdges.Left;
            else if (p.X >= display.Width - NativeDisplayResizeGrip) edges |= NativeResizeEdges.Right;
            if (p.Y < NativeDisplayResizeGrip) edges |= NativeResizeEdges.Top;
            else if (p.Y >= display.Height - NativeDisplayResizeGrip) edges |= NativeResizeEdges.Bottom;

            return edges;
        }

        private static Cursor NativeResizeCursor(NativeResizeEdges edges)
        {
            bool horz = (edges & (NativeResizeEdges.Left | NativeResizeEdges.Right)) != 0;
            bool vert = (edges & (NativeResizeEdges.Top | NativeResizeEdges.Bottom)) != 0;
            if (horz && vert)
            {
                bool nwse = edges == (NativeResizeEdges.Left | NativeResizeEdges.Top) ||
                            edges == (NativeResizeEdges.Right | NativeResizeEdges.Bottom);
                return nwse ? Cursors.SizeNWSE : Cursors.SizeNESW;
            }
            return horz ? Cursors.SizeWE : Cursors.SizeNS;
        }

        private void ResetNativeDisplaySize()
        {
            CurrentViewNativeDisplaySize = null;

            // re-run the native layout for the current window size, keeping the panel positions
            StoreCurrentViewNativePanelLocations();
            Console_Resize(this, EventArgs.Empty);
            ApplyCurrentViewNativePanelLocations();
            RaiseNativeMovablePanelsAboveDisplay();
        }

        private void ContinueNativeDisplayResize()
        {
            Point mouse = Control.MousePosition;
            int dx = mouse.X - _nativePanelDragMouseStart.X;
            int dy = mouse.Y - _nativePanelDragMouseStart.Y;
            Rectangle r = _nativeDisplayResizeOrigin;

            int left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
            if ((_nativeDisplayResizeEdges & NativeResizeEdges.Left) != 0)
                left = Math.Max(0, Math.Min(right - NativeDisplayMinWidth, r.Left + dx));
            if ((_nativeDisplayResizeEdges & NativeResizeEdges.Right) != 0)
                right = Math.Min(ClientSize.Width, Math.Max(left + NativeDisplayMinWidth, r.Right + dx));
            if ((_nativeDisplayResizeEdges & NativeResizeEdges.Top) != 0)
                top = Math.Max(0, Math.Min(bottom - NativeDisplayMinHeight, r.Top + dy));
            if ((_nativeDisplayResizeEdges & NativeResizeEdges.Bottom) != 0)
                bottom = Math.Min(ClientSize.Height, Math.Max(top + NativeDisplayMinHeight, r.Bottom + dy));

            Rectangle next = Rectangle.FromLTRB(left, top, right, bottom);
            if (panelDisplay.Bounds != next)
                panelDisplay.Bounds = next;

            Cursor.Current = NativeResizeCursor(_nativeDisplayResizeEdges);
        }

        // ---- Panel opacity (Shift + mouse wheel over a movable panel) ----
        // A panel is made a layered child window (WS_EX_LAYERED, Windows 8+ with the
        // supportedOS manifest entries) so DWM blends it, children included, over the
        // spectrum/waterfall underneath. 100% removes the layered style entirely.
        private const string NativePanelOpacityKeyPrefix = "NativePanelOpacity.";
        private const int NativePanelOpacityMin = 20;
        private const int NativePanelOpacityStep = 10;
        private readonly Dictionary<string, int> _nativePanelOpacities = new Dictionary<string, int>(StringComparer.Ordinal);

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_LAYERED = 0x00080000;
        private const uint LWA_ALPHA = 0x00000002;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point pt);

        private static int ClampNativePanelOpacity(int pct)
        {
            return Math.Max(NativePanelOpacityMin, Math.Min(100, pct));
        }

        private void AppendNativePanelOpacities(List<string> state)
        {
            // 100% is written too: DB.SaveVars never deletes rows, a skipped entry would bring back the old value
            foreach (KeyValuePair<string, int> kvp in _nativePanelOpacities)
                state.Add(NativePanelOpacityKeyPrefix + kvp.Key + "/" + kvp.Value.ToString());
        }

        private void SetNativePanelOpacity(Control panel, int pct)
        {
            pct = ClampNativePanelOpacity(pct);

            _nativePanelOpacities[panel.Name] = pct;

            // the layered style is lost when WinForms recreates the handle
            panel.HandleCreated -= NativePanelOpacity_HandleCreated;
            if (pct < 100)
                panel.HandleCreated += NativePanelOpacity_HandleCreated;

            if (panel.IsHandleCreated)
                ApplyNativePanelOpacity(panel, pct);
        }

        private void NativePanelOpacity_HandleCreated(object sender, EventArgs e)
        {
            Control panel = sender as Control;
            if (panel != null && _nativePanelOpacities.TryGetValue(panel.Name, out int pct))
                ApplyNativePanelOpacity(panel, pct);
        }

        private static void ApplyNativePanelOpacity(Control panel, int pct)
        {
            IntPtr hwnd = panel.Handle;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();

            if (pct >= 100)
            {
                if ((ex & WS_EX_LAYERED) != 0)
                    SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex & ~WS_EX_LAYERED));
            }
            else
            {
                if ((ex & WS_EX_LAYERED) == 0)
                    SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
                SetLayeredWindowAttributes(hwnd, 0, (byte)(pct * 255 / 100), LWA_ALPHA);
            }

            panel.Invalidate(true);
        }

        private bool AdjustNativePanelOpacityFromWheel(IntPtr wParam)
        {
            bool shiftDown = (Control.ModifierKeys & Keys.Shift) == Keys.Shift || Common.ShiftKeyDown;
            if (!shiftDown) return false;

            // the drawer of the new layout (Console.NewGUI.cs) fades as one window
            bool drawer = _ngDrawer != null && _ngDrawer.Visible &&
                          _ngDrawer.RectangleToScreen(_ngDrawer.ClientRectangle).Contains(Control.MousePosition);
            Control primary = drawer ? _ngDrawer : ResolveNativeMovablePanel(WindowFromPoint(Control.MousePosition));
            if (primary == null) return false;

            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            if (delta == 0) return true;

            int current = _nativePanelOpacities.TryGetValue(primary.Name, out int pct) ? pct : 100;
            int next = ClampNativePanelOpacity(current + (delta > 0 ? NativePanelOpacityStep : -NativePanelOpacityStep));

            foreach (Control c in drawer ? new[] { primary } : GetNativePanelDragGroup(primary))
                SetNativePanelOpacity(c, next);

            return true;
        }


        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // the wheel goes to the focused control: after a value was changed in the drawer of the new layout
        // (filter width...) it kept changing that value with the mouse over the VFO / panorama. Outside the
        // drawer the wheel goes to the window under the mouse instead, as if the drawer had never had focus.
        private bool ngDrawerUnderMouse() =>
            _ngDrawer != null && _ngDrawer.Visible && _ngDrawer.RectangleToScreen(_ngDrawer.ClientRectangle).Contains(Control.MousePosition);

        private bool RedirectWheelFromDrawer(Message m)
        {
            if (_ngDrawer == null || !_ngDrawer.Visible || !_ngDrawer.ContainsFocus) return false;
            IntPtr under = WindowFromPoint(Control.MousePosition);
            Control target = Control.FromChildHandle(under);
            if (target == null || target.FindForm() != this || _ngDrawer.Contains(target) || target == _ngDrawer) return false;
            btnHidden.Focus();
            SendMessage(under, m.Msg, m.WParam, m.LParam);
            return true;
        }

        private static bool IsNativeMovableControl(Control control)
        {
            if (control == null) return false;
            if (!_nativeMovablePanelNames.Contains(control.Name)) return false;

            return control is PanelTS || control is GroupBoxTS ||
                   control.Name == "chkSquelch" ||
                   control.Name == "ptbSquelch" ||
                   control.Name == "picSquelch" ||
                   control.Name == "chkRX2Squelch" ||
                   control.Name == "ptbRX2Squelch" ||
                   control.Name == "picRX2Squelch";
        }

        private Control ResolveNativeMovablePanel(IntPtr hwnd)
        {
            Control c = hwnd != IntPtr.Zero ? Control.FromHandle(hwnd) : null;

            while (c != null && c != this)
            {
                if (c.Parent == this)
                {
                    if (IsNativeMovableControl(c))
                        return c;

                    break;
                }

                c = c.Parent;
            }

            // Fallback for native child HWNDs that are not represented directly
            // by Control.FromHandle(): resolve the top-level Console child under
            // the current pointer.
            Point client = PointToClient(Control.MousePosition);
            Control direct = GetChildAtPoint(client, GetChildAtPointSkip.Invisible);
            if (IsNativeMovableControl(direct))
                return direct;

            return null;
        }

        private IEnumerable<Control> GetNativePanelDragGroup(Control primary)
        {
            string[] names;

            switch (primary.Name)
            {
                case "panelBandHF":
                case "panelBandVHF":
                case "panelBandGEN":
                    names = new[] { "panelBandHF", "panelBandVHF", "panelBandGEN" };
                    break;

                case "panelModeSpecificCW":
                case "panelModeSpecificPhone":
                case "panelModeSpecificDigital":
                case "panelModeSpecificFM":
                    names = new[]
                    {
                        "panelModeSpecificCW",
                        "panelModeSpecificPhone",
                        "panelModeSpecificDigital",
                        "panelModeSpecificFM"
                    };
                    break;

                case "chkSquelch":
                case "ptbSquelch":
                case "picSquelch":
                    names = new[]
                    {
                        "chkSquelch",
                        "ptbSquelch",
                        "picSquelch"
                    };
                    break;

                case "chkRX2Squelch":
                case "ptbRX2Squelch":
                case "picRX2Squelch":
                    names = new[] { "chkRX2Squelch", "ptbRX2Squelch", "picRX2Squelch" };
                    break;

                default:
                    names = new[] { primary.Name };
                    break;
            }

            foreach (string name in names)
            {
                Control found = Controls.Cast<Control>().FirstOrDefault(x => x.Name == name);
                if (found != null && found.Parent == this)
                    yield return found;
            }
        }

        private bool BeginNativePanelShiftDrag(IntPtr hwnd)
        {
            bool shiftDown = (Control.ModifierKeys & Keys.Shift) == Keys.Shift || Common.ShiftKeyDown;
            if (!shiftDown) return false;

            Control primary = ResolveNativeMovablePanel(hwnd);
            if (primary == null) return false;

            if ((Control.ModifierKeys & Keys.Control) == Keys.Control && ngTopPanelDragged(primary, true))
                return true;

            if (primary == panelDisplay)
            {
                if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
                {
                    ResetNativeDisplaySize();
                    return true;
                }

                NativeResizeEdges edges = HitTestNativeDisplayEdges(primary);
                if (edges != NativeResizeEdges.None)
                {
                    _nativePanelDragPrimary = primary;
                    _nativePanelDragMouseStart = Control.MousePosition;
                    _nativePanelDragOrigins = null;
                    _nativeDisplayResizeEdges = edges;
                    _nativeDisplayResizeOrigin = primary.Bounds;
                    RaiseNativeMovablePanelsAboveDisplay();
                    Cursor.Current = NativeResizeCursor(edges);
                    return true;
                }
            }

            _nativePanelDragPrimary = primary;
            _nativePanelDragMouseStart = Control.MousePosition;
            _nativePanelDragOrigins = new Dictionary<Control, Point>();

            foreach (Control c in GetNativePanelDragGroup(primary))
                _nativePanelDragOrigins[c] = c.Location;

            primary.BringToFront();
            if (primary.Name == "panelDisplay")
                RaiseNativeMovablePanelsAboveDisplay(); // dragging the display must not cover panels
            Cursor.Current = Cursors.SizeAll;
            return true;
        }

        private bool ContinueNativePanelShiftDrag()
        {
            if (_nativePanelDragPrimary == null ||
                (_nativePanelDragOrigins == null && _nativeDisplayResizeEdges == NativeResizeEdges.None))
                return false;

            bool shiftDown = (Control.ModifierKeys & Keys.Shift) == Keys.Shift || Common.ShiftKeyDown;
            bool leftDown = (Control.MouseButtons & MouseButtons.Left) == MouseButtons.Left;

            if (!shiftDown || !leftDown)
            {
                EndNativePanelShiftDrag();
                return true;
            }

            if (_nativeDisplayResizeEdges != NativeResizeEdges.None)
            {
                ContinueNativeDisplayResize();
                return true;
            }

            Point mouse = Control.MousePosition;
            int dx = mouse.X - _nativePanelDragMouseStart.X;
            int dy = mouse.Y - _nativePanelDragMouseStart.Y;

            Point primaryOrigin = _nativePanelDragOrigins[_nativePanelDragPrimary];
            int newX = primaryOrigin.X + dx;
            int newY = primaryOrigin.Y + dy;
            bool modeSpecificGroup = IsNativeModeSpecificPanelName(_nativePanelDragPrimary.Name);

            int maxX = modeSpecificGroup
                ? _nativePanelDragOrigins.Keys.Min(p => Math.Max(0, ClientSize.Width - p.Width))
                : Math.Max(0, ClientSize.Width - _nativePanelDragPrimary.Width);
            int maxY = modeSpecificGroup
                ? _nativePanelDragOrigins.Keys.Min(p => Math.Max(0, ClientSize.Height - p.Height))
                : Math.Max(0, ClientSize.Height - _nativePanelDragPrimary.Height);

            newX = Math.Max(0, Math.Min(maxX, newX));
            newY = Math.Max(0, Math.Min(maxY, newY));

            int appliedDx = newX - primaryOrigin.X;
            int appliedDy = newY - primaryOrigin.Y;

            foreach (KeyValuePair<Control, Point> kvp in _nativePanelDragOrigins)
            {
                Point next = modeSpecificGroup
                    ? new Point(newX, newY)
                    : new Point(kvp.Value.X + appliedDx, kvp.Value.Y + appliedDy);

                if (kvp.Key.Location != next)
                    kvp.Key.Location = next;
            }

            Cursor.Current = Cursors.SizeAll;
            return true;
        }

        private bool EndNativePanelShiftDrag()
        {
            if (_nativePanelDragPrimary == null)
                return false;

            if (_nativeDisplayResizeEdges != NativeResizeEdges.None)
            {
                _nativeDisplayResizeEdges = NativeResizeEdges.None;
                CurrentViewNativeDisplaySize = _nativePanelDragPrimary.Size;
                RaiseNativeMovablePanelsAboveDisplay();
            }

            if (IsNativeModeSpecificPanelName(_nativePanelDragPrimary.Name))
            {
                _nativeModeSpecificSharedLocation = _nativePanelDragPrimary.Location;
                ApplyNativeModeSpecificSharedLocation();
            }

            Control dragged = _nativePanelDragPrimary;
            _nativePanelDragPrimary = null;
            _nativePanelDragOrigins = null;
            ngTopPanelDragged(dragged, false);
            Cursor.Current = Cursors.Default;
            return true;
        }

        private sealed class NativePanelShiftDragFilter : IMessageFilter
        {
            private const int WM_MOUSEMOVE = 0x0200;
            private const int WM_LBUTTONDOWN = 0x0201;
            private const int WM_LBUTTONUP = 0x0202;
            private const int WM_MOUSEWHEEL = 0x020A;

            private readonly Console _owner;

            public NativePanelShiftDragFilter(Console owner)
            {
                _owner = owner;
            }

            public bool PreFilterMessage(ref Message m)
            {
                if (_owner == null || _owner.IsDisposed || _owner.Disposing)
                    return false;

                switch (m.Msg)
                {
                    case WM_LBUTTONDOWN:
                        // Consume Shift+LMB only when it starts a drag on one of
                        // the native movable Console panels. Without Shift the
                        // original control receives the click unchanged (LOCKED).
                        return _owner.BeginNativePanelShiftDrag(m.HWnd);

                    case WM_MOUSEMOVE:
                        return _owner.ContinueNativePanelShiftDrag();

                    case WM_LBUTTONUP:
                        return _owner.EndNativePanelShiftDrag();

                    case WM_MOUSEWHEEL:
                        // Shift + wheel over a movable panel changes its opacity.
                        return _owner.AdjustNativePanelOpacityFromWheel(m.WParam) || _owner.RedirectWheelFromDrawer(m);
                }

                return false;
            }
        }
    }

}
