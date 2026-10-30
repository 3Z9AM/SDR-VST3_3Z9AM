using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Thetis
{
    // RX1 filter shape in the Audio drawer of the new layout: the passband as WDSP builds it
    // (soft raised-cosine edges inside the band, wdsp fir.c fir_soft_edges; sharp edges as wide as
    // the window main lobe of the filter size), drag an edge to move it like on the panadapter.
    public partial class Console
    {
        // same steps as dragging a filter edge on the panadapter (VAR1, mirrored for two sidebands)
        private void setRX1FilterEdgeFromShape(bool lowEdge, int hz)
        {
            int low = radio.GetDSPRX(0, 0).RXFilterLow, high = radio.GetDSPRX(0, 0).RXFilterHigh;
            bool mirror = CurrentDSPhasTwoSidebands(1) && !Common.ShiftKeyDown;
            if (lowEdge)
            {
                low = Math.Min(hz, mirror ? -10 : high - 10);
                if (mirror) high = -low;
            }
            else
            {
                high = Math.Max(hz, mirror ? 10 : low + 10);
                if (mirror) low = -high;
            }
            SelectRX1VarFilter(false, true);
            UpdateRX1Filters(low, high);
            rx1_filters[(int)_rx1_dsp_mode].SetLow(Filter.VAR1, m_nLowOutRX1);
            rx1_filters[(int)_rx1_dsp_mode].SetHigh(Filter.VAR1, m_nHighOutRX1);
        }

        private sealed class FilterShapeView : Control
        {
            private const int PadL = 30, PadR = 8, PadT = 18, PadB = 16, Grab = 6;
            private const double FloorDb = -60.0;
            private readonly Console _c;
            private readonly Timer _poll = new Timer { Interval = 200 };
            private string _shown = "";
            private double _viewLo, _viewHi;    // Hz shown, frozen while dragging so the edge stays under the mouse
            private int _hover;                 // -1 low edge, +1 high edge, 0 none
            private int _drag;

            public FilterShapeView(Console c)
            {
                _c = c;
                Name = "ngFilterShape";
                Size = new Size(320, 120);
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                // filter, mode or soft edges may change from anywhere (panadapter, CAT, Setup): repaint on change
                _poll.Tick += (s, e) => { if (Visible && signature() != _shown) Invalidate(); };
                _poll.Start();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _poll.Dispose();
                base.Dispose(disposing);
            }

            private struct Shape { public double Low, High, SoftLow, SoftHigh, Trans; public bool Soft; }

            private Shape shape()
            {
                RadioDSPRX rx = _c.radio.GetDSPRX(0, 0);
                Shape s = new Shape { Low = rx.RXFilterLow, High = rx.RXFilterHigh };
                bool ssb = RadioDSPRX.SoftEdgeMode(_c.RX1DSPMode);
                double soft = ssb ? rx.RXSoftEdge : 0, softC = ssb ? rx.RXSoftEdgeCarrier : 0;
                // as fir_soft_edges: soft on the high-tone edge, softC on the carrier edge, each at most bw / n
                double m = Math.Max(Math.Abs(s.Low), Math.Abs(s.High)), bw = s.High - s.Low;
                s.SoftLow = soft > 0 && s.Low < 0 && -s.Low >= 0.5 * m ? soft : 0;
                s.SoftHigh = soft > 0 && s.High > 0 && s.High >= 0.5 * m ? soft : 0;
                if (softC > 0 && s.Low >= 0 && s.High > 0) s.SoftLow = softC;
                if (softC > 0 && s.High <= 0 && s.Low < 0) s.SoftHigh = softC;
                int n = (s.SoftLow > 0 ? 1 : 0) + (s.SoftHigh > 0 ? 1 : 0);
                if (n > 0 && bw > 0)
                {
                    s.SoftLow = Math.Min(s.SoftLow, bw / n);
                    s.SoftHigh = Math.Min(s.SoftHigh, bw / n);
                }
                s.Soft = s.SoftLow > 0 || s.SoftHigh > 0;
                // shortcut: main lobe of a 4-term Blackman-Harris (8 bins of the filter size); other windows look the same here
                int rate = _c.RX1DSPMode == DSPMode.FM ? 192000 : 48000;
                s.Trans = 8.0 * rate / Math.Max(64, rx.FilterSize);
                return s;
            }

            private string signature()
            {
                Shape s = shape();
                return s.Low + "|" + s.High + "|" + s.SoftLow + "|" + s.SoftHigh + "|" + s.Trans + "|" + _hover + "|" + Width;
            }

            private static double ideal(Shape s, double f)
            {
                if (f <= s.Low || f >= s.High) return 0;
                double g = 1;
                if (s.SoftLow > 0 && f < s.Low + s.SoftLow) g *= 0.5 - 0.5 * Math.Cos(Math.PI * (f - s.Low) / s.SoftLow);
                if (s.SoftHigh > 0 && f > s.High - s.SoftHigh) g *= 0.5 - 0.5 * Math.Cos(Math.PI * (s.High - f) / s.SoftHigh);
                return g;
            }

            // ideal shape smoothed by the window main lobe (Hann kernel), as magnitude in dB
            private static double responseDb(Shape s, double f)
            {
                const int K = 24;
                double sum = 0, wsum = 0;
                for (int i = 0; i <= K; i++)
                {
                    double t = (double)i / K - 0.5, w = 0.5 + 0.5 * Math.Cos(2 * Math.PI * t);
                    sum += w * ideal(s, f + t * s.Trans);
                    wsum += w;
                }
                double a = sum / wsum;
                return a <= 1e-3 ? FloorDb : Math.Max(FloorDb, 20 * Math.Log10(a));
            }

            private void updateView(Shape s)
            {
                if (_drag != 0) return;
                double width = Math.Max(10, s.High - s.Low);
                double a = s.Low, b = s.High;
                // the carrier is shown when it sits close to the filter (SSB/AM), not for CW far below it
                if (Math.Min(Math.Abs(a), Math.Abs(b)) < width || (a < 0 && b > 0)) { a = Math.Min(a, 0); b = Math.Max(b, 0); }
                double pad = Math.Max(100, 0.15 * (b - a));
                _viewLo = a - pad;
                _viewHi = b + pad;
            }

            private RectangleF plot => new RectangleF(PadL, PadT, Math.Max(10, Width - PadL - PadR), Math.Max(10, Height - PadT - PadB));
            private float xOf(double hz) { RectangleF p = plot; return (float)(p.Left + (hz - _viewLo) / (_viewHi - _viewLo) * p.Width); }
            private double hzOf(float x) { RectangleF p = plot; return _viewLo + (x - p.Left) / p.Width * (_viewHi - _viewLo); }
            private float yOf(double db) { RectangleF p = plot; return (float)(p.Top + db / FloorDb * p.Height); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Shape s = shape();
                updateView(s);
                _shown = signature();
                RectangleF p = plot;

                using (SolidBrush back = new SolidBrush(ModernTheme.PanelBack)) g.FillRectangle(back, ClientRectangle);
                // shared cached fonts: never dispose them
                Font small = ModernTheme.UiFont(7f), title = ModernTheme.UiFont(8f, FontStyle.Bold);
                using (Pen grid = new Pen(Color.FromArgb(90, ModernTheme.Border)))
                using (SolidBrush dim = new SolidBrush(ModernTheme.TextDim))
                using (SolidBrush text = new SolidBrush(ModernTheme.Text))
                {
                    // dB grid
                    for (int db = 0; db >= (int)FloorDb; db -= 20)
                    {
                        float y = yOf(db);
                        g.DrawLine(grid, p.Left, y, p.Right, y);
                        g.DrawString(db.ToString(), small, dim, 2, y - 6);
                    }
                    // frequency ticks
                    double span = _viewHi - _viewLo, step = 100;
                    foreach (double st in new double[] { 100, 200, 500, 1000, 2000, 5000 }) { step = st; if (span / st <= 6) break; }
                    for (double f = Math.Ceiling(_viewLo / step) * step; f <= _viewHi; f += step)
                    {
                        float x = xOf(f);
                        g.DrawLine(grid, x, p.Top, x, p.Bottom);
                        string lbl = Math.Abs(f) >= 1000 ? (f / 1000).ToString("0.#") + "k" : f.ToString("0");
                        SizeF sz = g.MeasureString(lbl, small);
                        g.DrawString(lbl, small, dim, x - sz.Width / 2, p.Bottom + 1);
                    }
                    // carrier
                    if (_viewLo < 0 && _viewHi > 0)
                        using (Pen car = new Pen(Color.FromArgb(160, ModernTheme.TextDim)) { DashStyle = DashStyle.Dot })
                            g.DrawLine(car, xOf(0), p.Top, xOf(0), p.Bottom);

                    // response
                    int cols = (int)p.Width;
                    PointF[] curve = new PointF[cols + 1];
                    for (int i = 0; i <= cols; i++)
                    {
                        float x = p.Left + i;
                        curve[i] = new PointF(x, yOf(responseDb(s, hzOf(x))));
                    }
                    PointF[] area = new PointF[cols + 3];
                    curve.CopyTo(area, 0);
                    area[cols + 1] = new PointF(p.Right, p.Bottom);
                    area[cols + 2] = new PointF(p.Left, p.Bottom);
                    using (LinearGradientBrush fill = new LinearGradientBrush(new RectangleF(p.Left, p.Top - 1, p.Width, p.Height + 2),
                        Color.FromArgb(110, ModernTheme.Accent), Color.FromArgb(10, ModernTheme.Accent), 90f))
                        g.FillPolygon(fill, area);
                    using (Pen line = new Pen(ModernTheme.Accent, 1.6f))
                        g.DrawLines(line, curve);

                    // edges: drag handles
                    drawEdge(g, s.Low, _hover == -1 || _drag == -1);
                    drawEdge(g, s.High, _hover == 1 || _drag == 1);

                    g.DrawString("RX1 FILTER", title, text, PadL, 2);
                    string info = s.Low.ToString("0") + " .. " + s.High.ToString("0") + " Hz   BW " + ((s.High - s.Low) / 1000.0).ToString("0.00") + " kHz";
                    if (s.Soft) info += "   soft " + s.SoftLow.ToString("0") + " / " + s.SoftHigh.ToString("0");
                    SizeF isz = g.MeasureString(info, small);
                    g.DrawString(info, small, dim, Width - PadR - isz.Width, 4);
                }
                using (Pen frame = new Pen(ModernTheme.Border)) g.DrawRectangle(frame, 0, 0, Width - 1, Height - 1);
            }

            private void drawEdge(Graphics g, double hz, bool hot)
            {
                RectangleF p = plot;
                float x = xOf(hz);
                Color c = hot ? ModernTheme.AccentSoft : Color.FromArgb(170, ModernTheme.Accent);
                using (Pen pen = new Pen(c, hot ? 2f : 1f) { DashStyle = hot ? DashStyle.Solid : DashStyle.Dash })
                    g.DrawLine(pen, x, p.Top, x, p.Bottom);
                using (SolidBrush b = new SolidBrush(c))
                    g.FillRectangle(b, x - 3, p.Top + p.Height / 2 - 7, 6, 14);
            }

            private int edgeAt(int x)
            {
                Shape s = shape();
                float lx = xOf(s.Low), hx = xOf(s.High);
                float dl = Math.Abs(x - lx), dh = Math.Abs(x - hx);
                if (Math.Min(dl, dh) > Grab) return 0;
                return dl <= dh ? -1 : 1;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_drag != 0)
                {
                    _c.setRX1FilterEdgeFromShape(_drag < 0, (int)Math.Round(hzOf(e.X)));
                    Invalidate();
                    return;
                }
                int h = edgeAt(e.X);
                Cursor = h != 0 ? Cursors.SizeWE : Cursors.Default;
                if (h != _hover) { _hover = h; Invalidate(); }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                _drag = edgeAt(e.X);
                if (_drag != 0) Capture = true;
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (_drag == 0) return;
                _drag = 0;
                Capture = false;
                Invalidate();       // the view re-fits the new filter
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (_drag == 0 && _hover != 0) { _hover = 0; Invalidate(); }
            }
        }
    }
}
