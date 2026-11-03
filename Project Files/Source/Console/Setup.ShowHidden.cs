//  Setup.ShowHidden.cs
//
//  The Setup window appears only once it is painted: with Modern Look its controls showed as white boxes for
//  ~0.5-1 s (system colours first, the dark theme after). Shown at alpha 0 (layered), revealed after the
//  Shown work (GPU waterfall page, theming of new pages) and a full synchronous repaint.
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
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Thetis
{
    public partial class Setup
    {
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr h, IntPtr rc, IntPtr rgn, uint flags);
        private const int GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000;
        private const uint LWA_ALPHA = 2, RDW_INVALIDATE = 1, RDW_ERASE = 4, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100;

        protected override void SetVisibleCore(bool value)
        {
            bool hide = value && !Visible && !DesignMode;
            if (hide)
            {
                IntPtr h = Handle;                      // creates it on the first show
                // stays layered afterwards (taking the style off made the window flash empty, as with the drawers)
                SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_LAYERED);
                SetLayeredWindowAttributes(h, 0, 0, LWA_ALPHA);
            }
            base.SetVisibleCore(value);
            if (!hide) return;
            // two hops: after Shown and after what Shown queues (refreshModernSetup via BeginInvoke)
            BeginInvoke(new MethodInvoker(() => BeginInvoke(new MethodInvoker(() =>
            {
                if (IsDisposed) return;
                RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
                SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);
            }))));
        }
    }
}
