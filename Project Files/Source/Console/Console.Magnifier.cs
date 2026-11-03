//  Console.Magnifier.cs
//
//  Magnifier over the panadapter: a finer FFT of the RX1 IQ around the mouse
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
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace Thetis
{
    public partial class Console
    {
        private MagnifierForm _magnifier;
        private ToolStripMenuItem _magnifierItem;

        // "Magnifier" tile in the main menu: while checked, a window next to the mouse over the
        // panadapter shows the spectrum under it 8x wider with an FFT fine enough for that
        private void initMagnifier()
        {
            _magnifierItem = new ToolStripMenuItem("Magnifier") { CheckOnClick = true, ForeColor = SystemColors.ControlLightLight };
            _magnifierItem.CheckedChanged += delegate
            {
                if (_magnifierItem.Checked && _magnifier == null) _magnifier = new MagnifierForm();
                else if (!_magnifierItem.Checked && _magnifier != null) { _magnifier.Close(); _magnifier = null; }
            };
            menuStrip1.Items.Insert(menuStrip1.Items.IndexOf(vstToolStripMenuItem) + 2, _magnifierItem);   // after CW Skimmer

            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 20 };   // under RowMs, or rows get skipped
            t.Tick += delegate { if (_magnifier != null) magnifierTick(); };
            t.Start();
        }

        private void magnifierTick()
        {
            Point p = pnlDisplay.PointToClient(Control.MousePosition);
            bool over = pnlDisplay.Visible && pnlDisplay.Width > 0 && pnlDisplay.ClientRectangle.Contains(p) && !MOX &&
                        (Form.ActiveForm == this || Form.ActiveForm == _magnifier);
            if (!over) { _magnifier.Request = null; if (_magnifier.Visible) _magnifier.Hide(); return; }

            getLowHighForRXn(1, out int low, out int high);
            double hzPerPx = (double)(high - low) / pnlDisplay.Width;
            _magnifier.Request = new MagnifierForm.Req(PixelToHz(p.X), hzPerPx * MagnifierForm.W / MagnifierForm.Zoom);

            // above right of the cursor, kept inside the panadapter
            Rectangle area = pnlDisplay.RectangleToScreen(pnlDisplay.ClientRectangle);
            Point m = Control.MousePosition;
            int x = m.X + 24, y = m.Y - MagnifierForm.H - 24;
            if (x + MagnifierForm.W > area.Right) x = m.X - 24 - MagnifierForm.W;
            if (y < area.Top) y = m.Y + 24;
            _magnifier.Location = new Point(x, y);
            if (!_magnifier.Visible) _magnifier.Show(this);
            _magnifier.Invalidate();
        }
    }

    internal sealed class MagnifierForm : Form
    {
        public const int W = 320, H = 100, Zoom = 8;
        private const int SpecH = 0;                       // waterfall only (user)
        private const int RowMs = 40;                      // one waterfall row per 40 ms
        private const int IqChannel = 6;                   // waterfall_iq ring fed with RX1 IQ by cmaster.c
        private const int MaxN = 1 << 17;                  // 1.46 Hz bins at 192 kHz; 2^18 at 25 rows/s ate a core

        internal sealed class Req
        {
            public readonly double Centre, Span;            // RX1 IQ offsets, Hz
            public Req(double centre, double span) { Centre = centre; Span = span; }
        }
        private sealed class Res
        {
            public float[] Db; public double Centre, Span, BinHz;
        }

        public volatile Req Request;
        private volatile Res _res;
        private volatile bool _run = true;

        public MagnifierForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(W, H);
            DoubleBuffered = true;
            BackColor = ModernTheme.PanelBack;
            Display.ExactGpuNative.CM_WaterfallIQ_Init(IqChannel, 1 << 20);
            Display.ExactGpuNative.CM_WaterfallIQ_SetEnabled(IqChannel, 1);
            new Thread(work) { IsBackground = true, Name = "Magnifier", Priority = ThreadPriority.BelowNormal }.Start();
            FormClosed += delegate { _run = false; Display.ExactGpuNative.CM_WaterfallIQ_SetEnabled(IqChannel, 0); };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020 | 0x00080000;   // NOACTIVATE, TOOLWINDOW, TRANSPARENT, LAYERED: clicks go to the panadapter
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Opacity = 0.95;                                 // a layered window needs its attributes set to be drawn
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = (IntPtr)(-1); return; }   // WM_NCHITTEST -> HTTRANSPARENT
            base.WndProc(ref m);
        }

        // newest RX1 IQ in a circle of MaxN samples; FFT of the newest N every ~120 ms
        private void work()
        {
            float[] ci = new float[MaxN], cq = new float[MaxN];   // circle
            float[] ti = new float[65536], tq = new float[65536];
            int head = 0, filled = 0, rate = 0;
            long lastRef = long.MinValue, lastFft = 0;
            Fft fft = null;
            float[] re = null, im = null, win = null;
            double[] avg = null;
            Req avgReq = null;

            while (_run)
            {
                Thread.Sleep(8);
                try
                {
                    int r = cmaster.GetInputRate(0, 0);
                    int avail = Display.ExactGpuNative.CM_WaterfallIQ_Available(IqChannel);
                    long refHz = Display.RX1IqOffsetToHz(0);
                    // a new rate, a retune or TX: the samples held belong to something else
                    if (r <= 0 || r != rate || refHz != lastRef || Audio.MOX)
                    {
                        Display.ExactGpuNative.CM_WaterfallIQ_Skip(IqChannel, avail);
                        rate = r; lastRef = refHz; filled = 0; avg = null; _res = null;
                        continue;
                    }
                    while (avail > 0)
                    {
                        int n = Display.ExactGpuNative.CM_WaterfallIQ_Get(IqChannel, Math.Min(avail, ti.Length), ti, tq);
                        if (n <= 0) break;
                        for (int k = 0; k < n; k++) { ci[head] = ti[k]; cq[head] = tq[k]; head = (head + 1) & (MaxN - 1); }
                        filled = Math.Min(MaxN, filled + n);
                        avail -= n;
                    }

                    Req q = Request;
                    if (q == null || Environment.TickCount64 - lastFft < RowMs) continue;
                    // at least one bin per magnifier pixel, as many as the samples held allow
                    int N = 1024;
                    while (N < MaxN && (double)rate / N > q.Span / W) N <<= 1;
                    while (N > 1024 && N > filled) N >>= 1;
                    if (filled < N) continue;
                    lastFft = Environment.TickCount64;

                    if (fft == null || fft.N != N)
                    {
                        fft = new Fft(N); re = new float[N]; im = new float[N]; win = new float[N];
                        for (int k = 0; k < N; k++) win[k] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * k / N));
                        avg = null;
                    }
                    int start = (head - N) & (MaxN - 1);
                    for (int k = 0; k < N; k++)
                    {
                        int s = (start + k) & (MaxN - 1), d = fft.BitRev[k];
                        re[d] = cq[s] * win[k]; im[d] = ci[s] * win[k];   // same I/Q swap as the GPU waterfall
                    }
                    fft.Transform(re, im);

                    double binHz = (double)rate / N, f0 = q.Centre - q.Span / 2;
                    double[] pw = new double[W];
                    for (int x = 0; x < W; x++)
                    {
                        int b0 = (int)Math.Round((f0 + q.Span * x / W) / binHz);
                        int b1 = Math.Max(b0 + 1, (int)Math.Round((f0 + q.Span * (x + 1) / W) / binHz));
                        double m = 0;
                        for (int b = b0; b < b1; b++) { int k = b & (N - 1); m = Math.Max(m, re[k] * re[k] + im[k] * im[k]); }
                        pw[x] = m;
                    }
                    // short average while the mouse stays put; a move starts afresh
                    bool same = avg != null && avgReq != null && Math.Abs(avgReq.Centre - q.Centre) < q.Span / W && Math.Abs(avgReq.Span - q.Span) < 1e-6 * q.Span;
                    if (!same) avg = pw;
                    else for (int x = 0; x < W; x++) avg[x] += 0.5 * (pw[x] - avg[x]);
                    avgReq = q;
                    float[] db = new float[W];
                    for (int x = 0; x < W; x++) db[x] = (float)(10 * Math.Log10(avg[x] + 1e-30));
                    _res = new Res { Db = db, Centre = q.Centre, Span = q.Span, BinHz = binHz };
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Magnifier: " + ex.Message);
                }
            }
        }

        // waterfall: one row per FFT result, newest on top; rows slide sideways with the mouse
        private int[] _wf;                                  // ARGB, (H - SpecH) rows x W
        private Bitmap _wfBmp;
        private Res _wfLast;
        private float _wfLo = float.NaN;

        private void addWaterfallRow(Res res)
        {
            int rows = H - SpecH;
            if (_wf == null) { _wf = new int[rows * W]; _wfBmp = new Bitmap(W, rows, System.Drawing.Imaging.PixelFormat.Format32bppArgb); }
            int dx = 0;
            if (_wfLast != null)
            {
                if (Math.Abs(_wfLast.Span - res.Span) > 1e-6 * res.Span) Array.Clear(_wf, 0, _wf.Length);   // zoom changed
                else dx = (int)Math.Round((res.Centre - _wfLast.Centre) / res.Span * W);
            }
            _wfLast = res;
            if (Math.Abs(dx) >= W) { Array.Clear(_wf, 0, _wf.Length); dx = 0; }
            // scroll down one row, shift left by dx (the view moved right by dx pixels)
            for (int y = rows - 1; y > 0; y--)
            {
                int d = y * W, s = (y - 1) * W;
                for (int x = 0; x < W; x++)
                {
                    int sx = x + dx;
                    _wf[d + x] = sx >= 0 && sx < W ? _wf[s + sx] : 0;
                }
            }
            // levels: noise floor (smoothed) at the bottom of the palette, the main waterfall's range above it
            float[] sorted = (float[])res.Db.Clone();
            Array.Sort(sorted);
            float noise = sorted[sorted.Length / 5];
            _wfLo = float.IsNaN(_wfLo) ? noise - 3 : _wfLo + 0.2f * (noise - 3 - _wfLo);
            float range = Math.Max(20, Math.Min(80, Display.RX1WaterfallRange()));
            for (int x = 0; x < W; x++)
                _wf[x] = Display.RX1WaterfallColour((res.Db[x] - _wfLo) / range);
            var bd = _wfBmp.LockBits(new Rectangle(0, 0, W, rows), System.Drawing.Imaging.ImageLockMode.WriteOnly, _wfBmp.PixelFormat);
            System.Runtime.InteropServices.Marshal.Copy(_wf, 0, bd.Scan0, _wf.Length);
            _wfBmp.UnlockBits(bd);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle rc = new Rectangle(0, 0, W - 1, H - 1);
            Res res = _res;
            Req q = Request;
            Font font = ModernTheme.UiFont(8f);            // shared cache: not disposed
            int top = 16;

            if (res != null && res != _wfLast) addWaterfallRow(res);
            if (_wfBmp != null) g.DrawImageUnscaled(_wfBmp, 0, SpecH);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(150, ModernTheme.PanelBack))) g.FillRectangle(b, 0, 0, W, top);   // readable captions

            if (res != null)
                TextRenderer.DrawText(g, res.BinHz.ToString(res.BinHz < 10 ? "0.00" : "0") + " Hz  x" + Zoom, font,
                    new Rectangle(0, 1, W - 4, 14), ModernTheme.Text, TextFormatFlags.Right);
            if (q != null)
            {
                float cx = res != null ? (float)((q.Centre - (res.Centre - res.Span / 2)) / res.Span * W) : W / 2f;
                using (Pen p = new Pen(Color.FromArgb(120, ModernTheme.Text)) { DashStyle = DashStyle.Dot }) g.DrawLine(p, cx, top, cx, top + 8);   // a tick only: a line would hide the trace under the mouse
                TextRenderer.DrawText(g, (Display.RX1IqOffsetToHz(q.Centre) / 1e6).ToString("0.000000") + " MHz", font,
                    new Point(4, 1), ModernTheme.Text);
            }
            using (Pen p = new Pen(ModernTheme.BorderStrong)) g.DrawRectangle(p, rc);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _wfBmp?.Dispose();
            base.Dispose(disposing);
        }
    }
}
