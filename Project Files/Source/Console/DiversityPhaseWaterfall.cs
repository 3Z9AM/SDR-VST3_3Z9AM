using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Thetis
{
    // Phase waterfall +-25 kHz around VFO A: hue = phase of RX2 against RX1, brightness = coherence
    // weighted by level. Skywave drifts through colours, local noise keeps one, plain noise stays dark.
    // Fed from the DiversityForm phase thread (Feed), drawn on the UI thread.
    internal sealed class DiversityPhaseWaterfall : Form
    {
        private const double SpanHz = 25000, AvgSeconds = 1, RowSeconds = 0.25;
        private const int W = 1000, H = 600, ScaleH = 18;

        private readonly int[] _px = new int[W * H];            // newest row on top; guarded by itself
        private readonly Bitmap _bmp = new Bitmap(W, H, PixelFormat.Format32bppRgb);
        private readonly double[] _colP = new double[W], _sorted = new double[W];
        private double[] _cr, _ci, _p1, _p2;
        private int _bc = int.MinValue;                          // centre bin (VFO A) of the averages
        private long _count;
        private double _rowTime;

        public DiversityPhaseWaterfall()
        {
            Text = "Phase waterfall ±25 kHz  (colour = phase RX2-RX1, brightness = coherence)";
            BackColor = Color.Black;
            ForeColor = Color.Silver;
            ClientSize = new Size(W, H / 2 + ScaleH);
            DoubleBuffered = true;
            ResizeRedraw = true;
            FormClosed += delegate { _bmp.Dispose(); };
        }

        // phase thread, after each DiversityCross.Feed
        internal void Feed(DiversityCross dc)
        {
            double binHz = dc.BinHz;
            int half = (int)Math.Ceiling(SpanHz / binHz);
            int bc = (int)Math.Round(Display.RX1VfoIqOffset() / binHz);
            if (_cr == null || _cr.Length != 2 * half + 1 || bc != _bc)
            {
                _cr = new double[2 * half + 1]; _ci = new double[_cr.Length]; _p1 = new double[_cr.Length]; _p2 = new double[_cr.Length];
                _bc = bc; _count = 0;
            }
            _count++;
            dc.AverageBins(bc - half, Math.Max(1 / binHz / AvgSeconds, 1.0 / _count), _cr, _ci, _p1, _p2);

            _rowTime += 1 / binHz;                               // one FFT frame
            if (_rowTime < RowSeconds) return;
            _rowTime -= RowSeconds;
            addRow();
            if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(Invalidate));
        }

        private void addRow()
        {
            int nb = _cr.Length;
            int[] row = new int[W];
            double[] cr = new double[W], ci = new double[W], p1 = new double[W], p2 = new double[W];
            for (int j = 0; j < W; j++)
            {
                int i0 = j * nb / W, i1 = Math.Max(i0 + 1, (j + 1) * nb / W);
                for (int i = i0; i < i1; i++) { cr[j] += _cr[i]; ci[j] += _ci[i]; p1[j] += _p1[i]; p2[j] += _p2[i]; }
                _colP[j] = 10 * Math.Log10(p1[j] + p2[j] + 1e-30);
            }
            Array.Copy(_colP, _sorted, W);
            Array.Sort(_sorted);
            double floor = _sorted[W / 2];
            for (int j = 0; j < W; j++)
            {
                double den = Math.Sqrt(p1[j] * p2[j]);
                double coh = den > 0 ? Math.Min(1, Math.Sqrt(cr[j] * cr[j] + ci[j] * ci[j]) / den) : 0;
                // coherent noise at the floor still shows (local QRM), louder signals brighter
                double level = Math.Max(0, Math.Min(1, (_colP[j] - floor) / 25));
                double v = coh * coh * (0.4 + 0.6 * level);
                double phase = Math.Atan2(-ci[j], cr[j]);         // RX2 lead, real IQ frame (DiversityCross.Phase)
                if (Display.PhaseViewRx1Lead) phase = -phase;
                row[j] = Hsv(phase, v);
            }
            lock (_px)
            {
                Array.Copy(_px, 0, _px, W, W * (H - 1));
                Array.Copy(row, _px, W);
            }
        }

        // RGB (0xRRGGBB) of a phase as hue (0 = red), value v
        internal static int Hsv(double phase, double v)
        {
            double h = (phase / (2 * Math.PI) + 1) % 1 * 6;
            int sector = (int)h;
            double f = h - sector;
            double p = 0, q = v * (1 - f), t = v * f;
            double r, g, b;
            switch (sector)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return (int)(r * 255) << 16 | (int)(g * 255) << 8 | (int)(b * 255);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int w = ClientSize.Width, h = ClientSize.Height - ScaleH;
            if (w < 2 || h < 2) return;

            BitmapData bd = _bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            lock (_px) Marshal.Copy(_px, 0, bd.Scan0, _px.Length);
            _bmp.UnlockBits(bd);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            // newest rows at the top; the window shows as many rows as it is tall (1 px each, up to H)
            int rows = Math.Min(H, h);
            g.DrawImage(_bmp, new Rectangle(0, ScaleH, w, rows), new Rectangle(0, 0, W, rows), GraphicsUnit.Pixel);

            // kHz scale and the RX1 filter
            Func<double, float> x = off => (float)((off + SpanHz) / (2 * SpanHz) * w);
            using (Pen tick = new Pen(Color.Gray))
                for (int k = -25; k <= 25; k += 5)
                {
                    float px = x(k * 1000);
                    g.DrawLine(tick, px, ScaleH - 5, px, ScaleH);
                    string s = k == 0 ? "VFO A" : k.ToString("+0;-0");
                    SizeF sz = g.MeasureString(s, Font);
                    g.DrawString(s, Font, Brushes.Silver, Math.Max(0, Math.Min(w - sz.Width, px - sz.Width / 2)), 0);
                }
            Display.RX1FilterIqOffsets(out int lo, out int hi);
            int c = Display.RX1VfoIqOffset();
            using (Pen fp = new Pen(Color.FromArgb(160, Color.Yellow)) { DashStyle = DashStyle.Dash })
            {
                g.DrawLine(fp, x(lo - c), ScaleH, x(lo - c), ScaleH + rows);
                g.DrawLine(fp, x(hi - c), ScaleH, x(hi - c), ScaleH + rows);
            }
        }
    }
}
